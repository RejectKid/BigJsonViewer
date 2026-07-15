using BigJsonViewer.Storage;

namespace BigJsonViewer.Tests;

public sealed class MemoryMappedFileSourceTests
{
    [Fact]
    public async Task ReadsBoundedViewsAtAbsoluteOffsets()
    {
        var path = Path.GetTempFileName();
        try
        {
            var expected = Enumerable.Range(0, 4096).Select(index => (byte)(index * 31)).ToArray();
            await File.WriteAllBytesAsync(path, expected);
            await using var source = new MemoryMappedFileSource(path);
            var actual = new byte[777];
            var read = await source.ReadAsync(1234, actual);
            Assert.Equal(actual.Length, read);
            Assert.Equal(expected.AsSpan(1234, actual.Length).ToArray(), actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TrimsReadsAtEndOfFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await using var source = new MemoryMappedFileSource(path);
            var destination = new byte[8];
            Assert.Equal(1, await source.ReadAsync(2, destination));
            Assert.Equal(0, await source.ReadAsync(3, destination));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
