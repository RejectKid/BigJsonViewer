using System.Buffers.Binary;
using BigJsonViewer.Core;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Search;

public sealed class SearchResults : IAsyncDisposable
{
    internal const int RecordSize = 24;
    private readonly string _path;
    private readonly RandomAccessWindowCache _cache;
    private int _disposed;

    internal SearchResults(string path, long count)
    {
        _path = path;
        Count = count;
        _cache = new RandomAccessWindowCache(
            path,
            new RandomAccessWindowCacheOptions(64 * 1024, 4L * 1024 * 1024));
    }

    public long Count { get; }

    public async Task<IReadOnlyList<SearchMatch>> GetPageAsync(
        long skip,
        int take = 256,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        var actual = (int)Math.Min(take, Math.Max(0, Count - skip));
        if (actual == 0)
        {
            return Array.Empty<SearchMatch>();
        }

        var bytes = GC.AllocateUninitializedArray<byte>(actual * RecordSize);
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await _cache.ReadAsync(
                checked((skip * RecordSize) + read),
                bytes.AsMemory(read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The search result store is truncated.");
            }

            read += count;
        }

        var result = new SearchMatch[actual];
        for (var index = 0; index < actual; index++)
        {
            var record = bytes.AsSpan(index * RecordSize, RecordSize);
            result[index] = new SearchMatch(
                new SourceRange(
                    BinaryPrimitives.ReadInt64LittleEndian(record),
                    BinaryPrimitives.ReadInt64LittleEndian(record[8..])),
                record[16] != 0,
                record[17] != 0);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cache.DisposeAsync().ConfigureAwait(false);
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
