using BigJsonViewer.Storage;

namespace BigJsonViewer.Tests;

public sealed class FileSourceTests
{
    [Fact]
    public async Task ReadsFromAnAbsoluteOffset()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "0123456789"u8.ToArray());
            await using var source = new FileSource(path);
            var buffer = new byte[4];

            var bytesRead = await source.ReadAsync(3, buffer);

            Assert.Equal(4, bytesRead);
            Assert.Equal("3456"u8.ToArray(), buffer);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
