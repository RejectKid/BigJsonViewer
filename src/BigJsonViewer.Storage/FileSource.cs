using Microsoft.Win32.SafeHandles;

namespace BigJsonViewer.Storage;

public sealed class FileSource : IRandomAccessSource
{
    private readonly SafeFileHandle _handle;

    public FileSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return RandomAccess.ReadAsync(_handle, destination, offset, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
