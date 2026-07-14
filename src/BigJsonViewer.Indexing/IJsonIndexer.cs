namespace BigJsonViewer.Indexing;

public interface IJsonIndexer
{
    Task BuildAsync(string sourcePath, string indexPath, IProgress<IndexBuildProgress>? progress = null, CancellationToken cancellationToken = default);
}
