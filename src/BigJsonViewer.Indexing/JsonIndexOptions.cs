namespace BigJsonViewer.Indexing;

public sealed class JsonIndexOptions
{
    public const int DefaultMaximumDepth = 4096;
    public const int DefaultWriteBatchSize = 4096;

    public JsonIndexOptions(
        int maximumDepth = DefaultMaximumDepth,
        int writeBatchSize = DefaultWriteBatchSize)
    {
        if (maximumDepth is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        if (writeBatchSize is < 64 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(writeBatchSize));
        }

        MaximumDepth = maximumDepth;
        WriteBatchSize = writeBatchSize;
    }

    public int MaximumDepth { get; }

    public int WriteBatchSize { get; }
}
