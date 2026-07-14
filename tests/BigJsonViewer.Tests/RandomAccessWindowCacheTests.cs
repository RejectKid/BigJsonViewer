using System.Buffers;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Tests;

public sealed class RandomAccessWindowCacheTests
{
    private const int WindowSize = RandomAccessWindowCacheOptions.MinimumWindowSize;

    [Fact]
    public void UsesBoundedMeasuredDefaults()
    {
        var options = new RandomAccessWindowCacheOptions();

        Assert.Equal(1024 * 1024, options.WindowSize);
        Assert.Equal(64L * 1024 * 1024, options.CapacityBytes);
    }

    [Theory]
    [InlineData(WindowSize - 1, WindowSize)]
    [InlineData(WindowSize + 1, WindowSize * 2L)]
    [InlineData(WindowSize, WindowSize - 1L)]
    [InlineData(WindowSize, WindowSize + 1L)]
    public void RejectsInvalidWindowBudgets(int windowSize, long capacityBytes)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RandomAccessWindowCacheOptions(windowSize, capacityBytes));
    }

    [Fact]
    public async Task ReadsAcrossWindowsWithoutExceedingCapacity()
    {
        var data = CreatePattern(3 * WindowSize + 17);
        var source = new MemorySource(data);
        var pool = new TrackingArrayPool();
        var options = new RandomAccessWindowCacheOptions(WindowSize, 2L * WindowSize);
        var cache = new RandomAccessWindowCache(source, options, leaveOpen: false, pool);
        var destination = new byte[data.Length];

        var bytesRead = await cache.ReadAsync(0, destination);
        var statistics = cache.Statistics;

        Assert.Equal(data.Length, bytesRead);
        Assert.Equal(data, destination);
        Assert.Equal(4, statistics.Loads);
        Assert.Equal(2, statistics.Evictions);
        Assert.Equal(2, statistics.ResidentWindows);
        Assert.Equal(options.CapacityBytes, statistics.ResidentBytes);
        Assert.True(pool.MaximumActiveBuffers <= 2);

        await cache.DisposeAsync();
        Assert.Equal(0, pool.ActiveBuffers);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task ServesWarmReadsWithoutReturningToTheSource()
    {
        var source = new MemorySource(CreatePattern(WindowSize));
        await using var cache = CreateCache(source, capacityWindows: 2);
        var first = new byte[128];
        var second = new byte[128];

        await cache.ReadAsync(37, first);
        await cache.ReadAsync(37, second);

        Assert.Equal(first, second);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, cache.Statistics.Misses);
        Assert.Equal(1, cache.Statistics.Hits);
    }

    [Fact]
    public async Task EvictsTheLeastRecentlyUsedUnleasedWindow()
    {
        var source = new MemorySource(CreatePattern(3 * WindowSize));
        await using var cache = CreateCache(source, capacityWindows: 2);

        await ReadByteAsync(cache, 0);
        await ReadByteAsync(cache, WindowSize);
        await ReadByteAsync(cache, 0);
        await ReadByteAsync(cache, 2L * WindowSize);
        await ReadByteAsync(cache, WindowSize);

        var statistics = cache.Statistics;
        Assert.Equal(4, statistics.Misses);
        Assert.Equal(1, statistics.Hits);
        Assert.Equal(4, statistics.Loads);
        Assert.Equal(2, statistics.Evictions);
        Assert.Equal(2L * WindowSize, statistics.ResidentBytes);
    }

    [Fact]
    public async Task CoalescesConcurrentMissesForTheSameWindow()
    {
        var source = new BlockingSource(CreatePattern(WindowSize));
        await using var cache = CreateCache(source, capacityWindows: 1);
        var first = new byte[256];
        var second = new byte[256];

        var firstRead = cache.ReadAsync(100, first).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRead = cache.ReadAsync(100, second).AsTask();
        source.Release.TrySetResult();

        await Task.WhenAll(firstRead, secondRead);

        Assert.Equal(first, second);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, cache.Statistics.Misses);
        Assert.Equal(1, cache.Statistics.CoalescedRequests);
        Assert.Equal(1, cache.Statistics.Loads);
    }

    [Fact]
    public async Task CallerCancellationDoesNotPublishAPartialWindow()
    {
        var source = new BlockingSource(CreatePattern(WindowSize));
        await using var cache = CreateCache(source, capacityWindows: 1);
        using var cancellation = new CancellationTokenSource();
        var cancelledDestination = new byte[256];

        var cancelledRead = cache.ReadAsync(0, cancelledDestination, cancellation.Token).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);

        var successfulDestination = new byte[256];
        var successfulRead = cache.ReadAsync(0, successfulDestination).AsTask();
        source.Release.TrySetResult();
        await successfulRead;

        Assert.Equal(CreatePattern(256), successfulDestination);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, cache.Statistics.Loads);
        Assert.Equal(1, cache.Statistics.CoalescedRequests);
    }

    [Fact]
    public async Task ConcurrentDistinctMissesStayWithinOneWindowBudget()
    {
        var source = new SequencedBlockingSource(CreatePattern(2 * WindowSize));
        var pool = new TrackingArrayPool();
        await using var cache = new RandomAccessWindowCache(
            source,
            new RandomAccessWindowCacheOptions(WindowSize, WindowSize),
            leaveOpen: false,
            pool);
        var firstDestination = new byte[128];
        var secondDestination = new byte[128];

        var firstRead = cache.ReadAsync(0, firstDestination).AsTask();
        await source.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRead = cache.ReadAsync(WindowSize, secondDestination).AsTask();

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(WindowSize, cache.Statistics.ResidentBytes);
        Assert.Equal(1, pool.ActiveBuffers);

        source.ReleaseFirst.TrySetResult();
        await source.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ReleaseSecond.TrySetResult();
        await Task.WhenAll(firstRead, secondRead);

        Assert.Equal(1, source.MaximumConcurrentReads);
        Assert.Equal(1, pool.MaximumActiveBuffers);
        Assert.True(cache.Statistics.ResidentBytes <= WindowSize);
    }

    [Fact]
    public async Task DoesNotCacheTruncatedWindows()
    {
        var source = new TruncatedSource(WindowSize);
        var pool = new TrackingArrayPool();
        var cache = new RandomAccessWindowCache(
            source,
            new RandomAccessWindowCacheOptions(WindowSize, WindowSize),
            leaveOpen: false,
            pool);
        var destination = new byte[WindowSize];

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await cache.ReadAsync(0, destination));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await cache.ReadAsync(0, destination));

        Assert.Equal(4, source.ReadCount);
        Assert.Equal(0, cache.Statistics.Loads);
        Assert.Equal(2, cache.Statistics.Misses);
        Assert.Equal(0, pool.ActiveBuffers);
        Assert.Equal(2, pool.ReturnCount);
        await cache.DisposeAsync();
    }

    [Fact]
    public async Task StopsAtTheSnapshottedEndOfFile()
    {
        var data = CreatePattern(WindowSize + 3);
        await using var cache = CreateCache(new MemorySource(data), capacityWindows: 2);
        var destination = new byte[10];

        var bytesRead = await cache.ReadAsync(data.Length - 3, destination);

        Assert.Equal(3, bytesRead);
        Assert.Equal(data[^3..], destination[..3]);
        Assert.Equal(new byte[7], destination[3..]);
    }

    [Fact]
    public async Task ReadsRangesBeyondTenGiB()
    {
        var path = Path.GetTempFileName();
        const long fileLength = 12L * 1024 * 1024 * 1024 + WindowSize;
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(fileLength);
                stream.Position = fileLength - 4;
                stream.Write("tail"u8);
            }

            await using var cache = new RandomAccessWindowCache(
                path,
                new RandomAccessWindowCacheOptions(WindowSize, 2L * WindowSize));
            var destination = new byte[8];

            var bytesRead = await cache.ReadAsync(fileLength - destination.Length, destination);

            Assert.Equal(fileLength, cache.Length);
            Assert.Equal(destination.Length, bytesRead);
            Assert.Equal([0, 0, 0, 0, .. "tail"u8.ToArray()], destination);
            Assert.True(cache.Statistics.ResidentBytes <= 2L * WindowSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsOffsetsOutsideTheSourceSnapshot()
    {
        await using var cache = CreateCache(new MemorySource([1, 2, 3]), capacityWindows: 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await cache.ReadAsync(-1, new byte[1]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await cache.ReadAsync(4, new byte[1]));
    }

    private static RandomAccessWindowCache CreateCache(IRandomAccessSource source, int capacityWindows) =>
        new(
            source,
            new RandomAccessWindowCacheOptions(WindowSize, (long)capacityWindows * WindowSize));

    private static async Task<byte> ReadByteAsync(RandomAccessWindowCache cache, long offset)
    {
        var destination = new byte[1];
        await cache.ReadAsync(offset, destination);
        return destination[0];
    }

    private static byte[] CreatePattern(int length)
    {
        var data = GC.AllocateUninitializedArray<byte>(length);
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(index * 31 + 7);
        }

        return data;
    }

    private sealed class MemorySource(byte[] data) : IRandomAccessSource
    {
        private int _readCount;

        public long Length => data.LongLength;

        public int ReadCount => Volatile.Read(ref _readCount);

        public int DisposeCount { get; private set; }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            if (offset >= data.LongLength)
            {
                return ValueTask.FromResult(0);
            }

            var count = (int)Math.Min(destination.Length, data.LongLength - offset);
            data.AsMemory((int)offset, count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSource(byte[] data) : IRandomAccessSource
    {
        private int _readCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Length => data.LongLength;

        public int ReadCount => Volatile.Read(ref _readCount);

        public async ValueTask<int> ReadAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var count = (int)Math.Min(destination.Length, data.LongLength - offset);
            data.AsMemory((int)offset, count).CopyTo(destination);
            return count;
        }

        public ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TruncatedSource(long length) : IRandomAccessSource
    {
        private int _readCount;

        public long Length => length;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            if (offset == 0)
            {
                var count = destination.Length / 2;
                destination.Span[..count].Fill(42);
                return ValueTask.FromResult(count);
            }

            return ValueTask.FromResult(0);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequencedBlockingSource(byte[] data) : IRandomAccessSource
    {
        private int _activeReads;
        private int _readCount;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Length => data.LongLength;

        public int ReadCount => Volatile.Read(ref _readCount);

        public int MaximumConcurrentReads { get; private set; }

        public async ValueTask<int> ReadAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            var readNumber = Interlocked.Increment(ref _readCount);
            var active = Interlocked.Increment(ref _activeReads);
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, active);
            try
            {
                var started = readNumber == 1 ? FirstStarted : SecondStarted;
                var release = readNumber == 1 ? ReleaseFirst : ReleaseSecond;
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                var count = (int)Math.Min(destination.Length, data.LongLength - offset);
                data.AsMemory((int)offset, count).CopyTo(destination);
                return count;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public ValueTask DisposeAsync()
        {
            ReleaseFirst.TrySetResult();
            ReleaseSecond.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private readonly object _gate = new();
        private readonly Stack<byte[]> _available = [];
        private readonly HashSet<byte[]> _active = [];

        public int ReturnCount { get; private set; }

        public int ActiveBuffers
        {
            get
            {
                lock (_gate)
                {
                    return _active.Count;
                }
            }
        }

        public int MaximumActiveBuffers { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            lock (_gate)
            {
                var buffer = _available.Count == 0 ? new byte[minimumLength] : _available.Pop();
                Assert.True(buffer.Length >= minimumLength);
                Assert.True(_active.Add(buffer));
                MaximumActiveBuffers = Math.Max(MaximumActiveBuffers, _active.Count);
                return buffer;
            }
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                Assert.True(_active.Remove(array));
                _available.Push(array);
                ReturnCount++;
            }
        }
    }
}
