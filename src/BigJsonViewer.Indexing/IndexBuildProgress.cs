namespace BigJsonViewer.Indexing;

public readonly record struct IndexBuildProgress(long BytesProcessed, long TotalBytes, long NodesDiscovered)
{
    public double Fraction => TotalBytes == 0 ? 1 : (double)BytesProcessed / TotalBytes;
}
