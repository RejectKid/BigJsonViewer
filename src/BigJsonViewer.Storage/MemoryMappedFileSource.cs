using System.IO.MemoryMappedFiles;

namespace BigJsonViewer.Storage;

/// <summary>
/// Bounded-view memory-mapped prototype used for platform benchmark comparisons.
/// Each read creates a view over only the requested range; callers should put a
/// <see cref="RandomAccessWindowCache"/> above this source for repeated access.
/// </summary>
public sealed class MemoryMappedFileSource : IRandomAccessSource
{
    private readonly MemoryMappedFile _mapping;
    private int _disposed;

    public MemoryMappedFileSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        Length = info.Length;
        _mapping = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            null,
            0,
            MemoryMappedFileAccess.Read);
    }

    public long Length { get; }

    public async ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var length = (int)Math.Min(destination.Length, Length - offset);
        if (length == 0)
        {
            return 0;
        }

        await using var view = _mapping.CreateViewStream(offset, length, MemoryMappedFileAccess.Read);
        var read = 0;
        while (read < length)
        {
            var count = await view.ReadAsync(destination[read..length], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _mapping.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
