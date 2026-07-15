using BigJsonViewer.Core;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Indexing;

public sealed class BjxIndexReader : IAsyncDisposable
{
    private readonly RandomAccessWindowCache _cache;

    private BjxIndexReader(RandomAccessWindowCache cache, BjxHeader header)
    {
        _cache = cache;
        Header = header;
    }

    public BjxHeader Header { get; }

    public RandomAccessWindowCacheStatistics CacheStatistics => _cache.Statistics;

    public static async Task<BjxIndexReader> OpenAsync(
        string indexPath,
        SourceFileIdentity? expectedSource = null,
        CancellationToken cancellationToken = default)
    {
        var cache = new RandomAccessWindowCache(
            indexPath,
            new RandomAccessWindowCacheOptions(64 * 1024, 4L * 1024 * 1024));
        try
        {
            var bytes = new byte[BjxFormat.HeaderSize];
            await ReadExactlyAsync(cache, 0, bytes, cancellationToken).ConfigureAwait(false);
            var header = BjxFormat.ReadHeader(bytes);
            if (!header.IsComplete)
            {
                throw new InvalidDataException("The .bjx index build was interrupted and is incomplete.");
            }

            if (expectedSource is { } identity && header.SourceIdentity != identity)
            {
                throw new InvalidDataException("The .bjx index belongs to a different version of the source file.");
            }

            var expectedCheckpoints = (header.NodeCount + header.CheckpointInterval - 1) / header.CheckpointInterval;
            if (header.CheckpointOffset < BjxFormat.HeaderSize ||
                header.CheckpointCount != expectedCheckpoints)
            {
                throw new InvalidDataException("The .bjx checkpoint metadata is invalid.");
            }

            var expectedLength = checked(header.CheckpointOffset + (header.CheckpointCount * sizeof(long)));
            if (cache.Length < expectedLength)
            {
                throw new InvalidDataException("The .bjx index is truncated.");
            }

            return new BjxIndexReader(cache, header);
        }
        catch
        {
            await cache.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IndexedNode> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if ((ulong)id >= (ulong)Header.NodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        var checkpointIndex = id / Header.CheckpointInterval;
        var checkpoint = new byte[sizeof(long)];
        await ReadExactlyAsync(
            _cache,
            checked(Header.CheckpointOffset + (checkpointIndex * sizeof(long))),
            checkpoint,
            cancellationToken).ConfigureAwait(false);
        var recordOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(checkpoint);
        if (recordOffset < BjxFormat.HeaderSize || recordOffset >= Header.CheckpointOffset)
        {
            throw new InvalidDataException("A .bjx checkpoint points outside the record region.");
        }

        var currentId = checkpointIndex * Header.CheckpointInterval;
        var bytes = new byte[BjxFormat.MaximumRecordSize];
        while (currentId <= id)
        {
            var available = (int)Math.Min(bytes.Length, Header.CheckpointOffset - recordOffset);
            if (available <= 0)
            {
                throw new InvalidDataException("The .bjx record stream ended before the requested node.");
            }

            await ReadExactlyAsync(
                _cache,
                recordOffset,
                bytes.AsMemory(0, available),
                cancellationToken).ConfigureAwait(false);
            var node = BjxFormat.DecodeNode(currentId, bytes.AsSpan(0, available), out var recordLength);
            if (currentId == id)
            {
                return node;
            }

            recordOffset = checked(recordOffset + recordLength);
            currentId++;
        }

        throw new InvalidDataException("The requested .bjx node could not be decoded.");
    }

    public async Task<IReadOnlyList<IndexedNode>> GetChildrenAsync(
        long parentId,
        long skip = 0,
        int take = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        var parent = await GetNodeAsync(parentId, cancellationToken).ConfigureAwait(false);
        if (parent.ChildCount == 0 || skip >= parent.ChildCount)
        {
            return Array.Empty<IndexedNode>();
        }

        var result = new List<IndexedNode>((int)Math.Min(take, parent.ChildCount - skip));
        var id = parent.FirstChildId;
        long childIndex = 0;
        while (id >= 0 && childIndex < parent.ChildCount && result.Count < take)
        {
            var child = await GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
            if (child.ParentId != parentId)
            {
                throw new InvalidDataException("The .bjx child chain is corrupt.");
            }

            if (childIndex >= skip)
            {
                result.Add(child);
            }

            id = checked(child.SubtreeEndId + 1);
            childIndex++;
        }

        return result;
    }

    public ValueTask DisposeAsync() => _cache.DisposeAsync();

    private static async Task ReadExactlyAsync(
        IRandomAccessSource source,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await source.ReadAsync(offset + read, destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The .bjx index ended unexpectedly.");
            }

            read += count;
        }
    }
}
