namespace BigJsonViewer.Storage;

public interface IRandomAccessSource : IAsyncDisposable
{
    long Length { get; }

    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default);
}
