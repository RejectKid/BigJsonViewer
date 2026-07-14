using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace BigJsonViewer.Storage;

public sealed class PooledSequentialFileReader : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ArrayPool<byte> _bufferPool;
    private int _activeRead;
    private int _disposed;

    public PooledSequentialFileReader(string path, SequentialReadOptions? options = null)
        : this(path, options, ArrayPool<byte>.Shared)
    {
    }

    internal PooledSequentialFileReader(
        string path,
        SequentialReadOptions? options,
        ArrayPool<byte> bufferPool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bufferPool);

        Options = options ?? new SequentialReadOptions();
        _bufferPool = bufferPool;
        _handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public SequentialReadOptions Options { get; }

    public long Read(SequentialChunkHandler handler, CancellationToken cancellationToken = default) =>
        Read(0, Length, handler, cancellationToken);

    public long Read(
        long offset,
        long length,
        SequentialChunkHandler handler,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateRange(offset, length);

        if (Interlocked.CompareExchange(ref _activeRead, 1, 0) != 0)
        {
            throw new InvalidOperationException("Only one sequential read can be active on a reader at a time.");
        }

        byte[]? buffer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length == 0)
            {
                return 0;
            }

            buffer = _bufferPool.Rent(Options.BufferSize);
            if (buffer.Length < Options.BufferSize)
            {
                throw new InvalidOperationException("The configured buffer pool returned a buffer that was too small.");
            }

            var currentOffset = offset;
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(remaining, Options.BufferSize);
                var bytesRead = RandomAccess.Read(_handle, buffer.AsSpan(0, requested), currentOffset);
                if (bytesRead == 0)
                {
                    break;
                }

                handler(
                    new SequentialReadChunk(currentOffset, buffer.AsMemory(0, bytesRead)),
                    cancellationToken);
                currentOffset += bytesRead;
                remaining -= bytesRead;
            }

            return currentOffset - offset;
        }
        finally
        {
            if (buffer is not null)
            {
                _bufferPool.Return(buffer, clearArray: false);
            }

            Volatile.Write(ref _activeRead, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }

    private void ValidateRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "The start offset is outside the source snapshot.");
        }

        if (length > Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested range is outside the source snapshot.");
        }
    }
}
