namespace BigJsonViewer.Search;

public readonly record struct SearchProgress(
    long BytesProcessed,
    long TotalBytes,
    long MatchesFound,
    IReadOnlyList<SearchMatch> LatestBatch)
{
    public double Fraction => TotalBytes == 0 ? 1 : (double)BytesProcessed / TotalBytes;
}
