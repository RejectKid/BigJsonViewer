using System.Text;
using BigJsonViewer.Core;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Tests;

public sealed class SourceInspectorTests
{
    [Fact]
    public async Task DetectsUtf8JsonLinesAndStableIdentity()
    {
        var path = CreateFile("{\"a\":1}\n{\"a\":2}\n");
        try
        {
            var first = await SourceInspector.InspectAsync(path);
            var second = await SourceInspector.InspectAsync(path);

            Assert.Equal(SourceEncoding.Utf8, first.Encoding);
            Assert.Equal(JsonDocumentFormat.JsonLines, first.Format);
            Assert.Equal(first.Identity, second.Identity);
            Assert.Null(first.CompressionKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1F8B", "gzip")]
    [InlineData("504B0304", "zip")]
    [InlineData("28B52FFD", "zstd")]
    public async Task DetectsCompressedSignatures(string hex, string expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Convert.FromHexString(hex));
            var metadata = await SourceInspector.InspectAsync(path);
            Assert.Equal(JsonDocumentFormat.Compressed, metadata.Format);
            Assert.Equal(expected, metadata.CompressionKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SessionGuardBecomesStaleWhenContentChanges()
    {
        var path = CreateFile("{\"value\":1}");
        try
        {
            var metadata = await SourceInspector.InspectAsync(path);
            var guard = new SourceSessionGuard(metadata);
            Assert.False(await guard.CheckForChangeAsync());

            await File.WriteAllTextAsync(path, "{\"value\":2}");
            Assert.True(await guard.CheckForChangeAsync());
            Assert.True(guard.IsStale);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bigjsonviewer-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
