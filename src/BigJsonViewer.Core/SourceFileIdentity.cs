namespace BigJsonViewer.Core;

public readonly record struct SourceFileIdentity(
    long Length,
    long LastWriteTimeUtcTicks,
    ulong SampleHash)
{
    public DateTime LastWriteTimeUtc => new(LastWriteTimeUtcTicks, DateTimeKind.Utc);
}
