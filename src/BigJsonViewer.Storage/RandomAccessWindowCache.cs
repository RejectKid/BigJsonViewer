using System.Buffers;

namespace BigJsonViewer.Storage;

public sealed class RandomAccessWindowCache : IRandomAccessSource
{
    private readonly object _gate = new();
    private readonly IRandomAccessSource _source;
    private readonly bool _leaveOpen;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly Dictionary<long, WindowEntry> _entries = [];
    private readonly LinkedList<WindowEntry> _lru = [];
    private readonly CancellationTokenSource _disposeCancellation = new();
    private TaskCompletionSource _capacityChanged = CreateSignal();
    private TaskCompletionSource _operationsDrained = CreateSignal();
    private Task? _disposeTask;
    private bool _disposed;
    private int _activeOperations;
    private int _inFlightLoads;
    private int _residentWindows;
    private long _residentBytes;
    private long _hits;
    private long _misses;
    private long _coalescedRequests;
    private long _loads;
    private long _evictions;

    public RandomAccessWindowCache(
        string path,
        RandomAccessWindowCacheOptions? options = null)
        : this(new FileSource(path), options, leaveOpen: false, ArrayPool<byte>.Shared)
    {
    }

    public RandomAccessWindowCache(
        IRandomAccessSource source,
        RandomAccessWindowCacheOptions? options = null,
        bool leaveOpen = false)
        : this(source, options, leaveOpen, ArrayPool<byte>.Shared)
    {
    }

    internal RandomAccessWindowCache(
        IRandomAccessSource source,
        RandomAccessWindowCacheOptions? options,
        bool leaveOpen,
        ArrayPool<byte> bufferPool)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bufferPool);
        if (source.Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Source length must not be negative.");
        }

        _source = source;
        _leaveOpen = leaveOpen;
        _bufferPool = bufferPool;
        Options = options ?? new RandomAccessWindowCacheOptions();
        Length = source.Length;
    }

    public long Length { get; }

    public RandomAccessWindowCacheOptions Options { get; }

    public RandomAccessWindowCacheStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new RandomAccessWindowCacheStatistics(
                    _hits,
                    _misses,
                    _coalescedRequests,
                    _loads,
                    _evictions,
                    _residentBytes,
                    _residentWindows,
                    _inFlightLoads);
            }
        }
    }

    public async ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The offset is outside the source snapshot.");
        }

        BeginOperation();
        try
        {
            var totalToRead = (int)Math.Min(destination.Length, Length - offset);
            var totalRead = 0;
            while (totalRead < totalToRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentOffset = offset + totalRead;
                var windowStart = currentOffset - (currentOffset % Options.WindowSize);
                var entry = await AcquireWindowAsync(windowStart, cancellationToken).ConfigureAwait(false);
                try
                {
                    var indexInWindow = (int)(currentOffset - windowStart);
                    var available = entry.Length - indexInWindow;
                    if (available <= 0)
                    {
                        throw new EndOfStreamException("The source ended before the snapshotted length.");
                    }

                    var count = Math.Min(available, totalToRead - totalRead);
                    entry.Buffer!.AsMemory(indexInWindow, count).CopyTo(destination.Slice(totalRead, count));
                    totalRead += count;
                }
                finally
                {
                    ReleaseWindow(entry);
                }
            }

            return totalRead;
        }
        finally
        {
            EndOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private ValueTask<WindowEntry> AcquireWindowAsync(long windowStart, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowEntry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(windowStart, out entry!))
            {
                if (entry.IsLoaded)
                {
                    _hits++;
                    entry.LeaseCount++;
                    TouchNoLock(entry);
                    return ValueTask.FromResult(entry);
                }

                _coalescedRequests++;
            }
            else
            {
                _misses++;
                _inFlightLoads++;
                entry = new WindowEntry(windowStart);
                _entries.Add(windowStart, entry);
                entry.LoadTask = LoadWindowAsync(entry);
            }

            entry.PendingWaiters++;
            return new ValueTask<WindowEntry>(WaitForWindowAsync(entry, cancellationToken));
        }
    }

    private async Task<WindowEntry> WaitForWindowAsync(WindowEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await entry.LoadTask!.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                entry.PendingWaiters--;
                entry.LeaseCount++;
                TouchNoLock(entry);
                return loaded;
            }
        }
        catch
        {
            lock (_gate)
            {
                entry.PendingWaiters--;
                SignalCapacityChangedNoLock();
            }

            throw;
        }
    }

    private async Task<WindowEntry> LoadWindowAsync(WindowEntry entry)
    {
        var reserved = false;
        byte[]? buffer = null;
        try
        {
            await ReserveWindowAsync().ConfigureAwait(false);
            reserved = true;
            _disposeCancellation.Token.ThrowIfCancellationRequested();

            buffer = _bufferPool.Rent(Options.WindowSize);
            if (buffer.Length < Options.WindowSize)
            {
                throw new InvalidOperationException("The configured buffer pool returned a window that was too small.");
            }

            var expected = (int)Math.Min(Options.WindowSize, Length - entry.Start);
            var read = 0;
            while (read < expected)
            {
                _disposeCancellation.Token.ThrowIfCancellationRequested();
                var count = await _source.ReadAsync(
                    entry.Start + read,
                    buffer.AsMemory(read, expected - read),
                    _disposeCancellation.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The source ended before the snapshotted length.");
                }

                if (count < 0 || count > expected - read)
                {
                    throw new InvalidDataException("The source returned an invalid byte count.");
                }

                read += count;
            }

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                entry.Buffer = buffer;
                entry.Length = read;
                entry.IsLoaded = true;
                entry.LruNode = _lru.AddFirst(entry);
                _residentWindows++;
                _loads++;
                buffer = null;
                return entry;
            }
        }
        catch
        {
            if (buffer is not null)
            {
                _bufferPool.Return(buffer, clearArray: false);
            }

            lock (_gate)
            {
                if (_entries.TryGetValue(entry.Start, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Start);
                }

                if (reserved)
                {
                    _residentBytes -= Options.WindowSize;
                }

                SignalCapacityChangedNoLock();
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _inFlightLoads--;
                SignalCapacityChangedNoLock();
            }
        }
    }

    private async Task ReserveWindowAsync()
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                _disposeCancellation.Token.ThrowIfCancellationRequested();
                if (_residentBytes + Options.WindowSize <= Options.CapacityBytes)
                {
                    _residentBytes += Options.WindowSize;
                    return;
                }

                if (TryEvictOneNoLock())
                {
                    continue;
                }

                waitTask = _capacityChanged.Task;
            }

            await waitTask.WaitAsync(_disposeCancellation.Token).ConfigureAwait(false);
        }
    }

    private bool TryEvictOneNoLock()
    {
        var node = _lru.Last;
        while (node is not null)
        {
            var previous = node.Previous;
            var entry = node.Value;
            if (entry.LeaseCount == 0 && entry.PendingWaiters == 0)
            {
                _lru.Remove(node);
                _entries.Remove(entry.Start);
                _bufferPool.Return(entry.Buffer!, clearArray: false);
                entry.Buffer = null;
                entry.LruNode = null;
                entry.IsLoaded = false;
                _residentBytes -= Options.WindowSize;
                _residentWindows--;
                _evictions++;
                SignalCapacityChangedNoLock();
                return true;
            }

            node = previous;
        }

        return false;
    }

    private void TouchNoLock(WindowEntry entry)
    {
        if (entry.LruNode is null || ReferenceEquals(_lru.First, entry.LruNode))
        {
            return;
        }

        _lru.Remove(entry.LruNode);
        _lru.AddFirst(entry.LruNode);
    }

    private void ReleaseWindow(WindowEntry entry)
    {
        lock (_gate)
        {
            entry.LeaseCount--;
            SignalCapacityChangedNoLock();
        }
    }

    private void BeginOperation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations++;
        }
    }

    private void EndOperation()
    {
        lock (_gate)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] loads;
        Task operationsDrained;
        lock (_gate)
        {
            _disposed = true;
            loads = _entries.Values
                .Select(entry => entry.LoadTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            operationsDrained = _activeOperations == 0 ? Task.CompletedTask : _operationsDrained.Task;
            SignalCapacityChangedNoLock();
        }

        _disposeCancellation.Cancel();
        try
        {
            await Task.WhenAll(loads).ConfigureAwait(false);
        }
        catch (Exception) when (loads.Any(task => task.IsCanceled || task.IsFaulted))
        {
        }

        await operationsDrained.ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Buffer is not null)
                {
                    _bufferPool.Return(entry.Buffer, clearArray: false);
                    entry.Buffer = null;
                }
            }

            _entries.Clear();
            _lru.Clear();
            _residentBytes = 0;
            _residentWindows = 0;
        }

        if (!_leaveOpen)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }

        _disposeCancellation.Dispose();
    }

    private void SignalCapacityChangedNoLock()
    {
        var signal = _capacityChanged;
        _capacityChanged = CreateSignal();
        signal.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WindowEntry(long start)
    {
        public long Start { get; } = start;

        public Task<WindowEntry>? LoadTask { get; set; }

        public byte[]? Buffer { get; set; }

        public int Length { get; set; }

        public int PendingWaiters { get; set; }

        public int LeaseCount { get; set; }

        public bool IsLoaded { get; set; }

        public LinkedListNode<WindowEntry>? LruNode { get; set; }
    }
}
