using System.Text;
using BigJsonViewer.Core;
using BigJsonViewer.Indexing;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Tests;

public sealed class StreamingJsonIndexerTests
{
    [Fact]
    public async Task BuildsPersistentHierarchyAndReadsDirectChildren()
    {
        var (source, index) = CreatePaths();
        try
        {
            await File.WriteAllTextAsync(source, "{\"a\":[1,true,null,\"x\"],\"b\":{}}", new UTF8Encoding(false));
            var metadata = await SourceInspector.InspectAsync(source);
            await new StreamingJsonIndexer().BuildAsync(source, index);

            await using var reader = await BjxIndexReader.OpenAsync(index, metadata.Identity);
            Assert.True(reader.Header.IsComplete);
            Assert.Equal(8, reader.Header.NodeCount);

            var document = await reader.GetNodeAsync(0);
            var root = Assert.Single(await reader.GetChildrenAsync(document.Id));
            Assert.Equal(JsonNodeKind.Object, root.Kind);
            Assert.Equal(2, root.ChildCount);

            var properties = await reader.GetChildrenAsync(root.Id);
            Assert.Collection(
                properties,
                node =>
                {
                    Assert.Equal(JsonNodeKind.Array, node.Kind);
                    Assert.Equal(4, node.ChildCount);
                },
                node => Assert.Equal(JsonNodeKind.Object, node.Kind));

            var arrayItems = await reader.GetChildrenAsync(properties[0].Id);
            Assert.Equal(
                [JsonNodeKind.Number, JsonNodeKind.Boolean, JsonNodeKind.Null, JsonNodeKind.String],
                arrayItems.Select(node => node.Kind));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task IndexesTokensAcrossFourMiBChunkBoundaryWithoutCopyingThem()
    {
        var (source, index) = CreatePaths();
        try
        {
            await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync("{\"huge\":\""u8.ToArray());
                var block = new byte[4 * 1024 * 1024 + 31];
                Array.Fill(block, (byte)'a');
                await stream.WriteAsync(block);
                await stream.WriteAsync("\"}"u8.ToArray());
            }

            await new StreamingJsonIndexer().BuildAsync(source, index);
            await using var reader = await BjxIndexReader.OpenAsync(index);
            var root = Assert.Single(await reader.GetChildrenAsync(0));
            var value = Assert.Single(await reader.GetChildrenAsync(root.Id));
            Assert.Equal(JsonNodeKind.String, value.Kind);
            Assert.Equal(4 * 1024 * 1024 + 33, value.Range.Length);
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Theory]
    [InlineData("{\"a\": tru}")]
    [InlineData("[01]")]
    [InlineData("[1,]")]
    [InlineData("{\"a\" 1}")]
    [InlineData("{\"a\": [1}")]
    [InlineData("{\"a\":\"\\q\"}")]
    [InlineData("{\"a\":\"\\uD800\"}")]
    [InlineData("{\"a\":\"\\uDC00\"}")]
    public async Task RejectsMalformedStructure(string json)
    {
        var (source, index) = CreatePaths();
        try
        {
            await File.WriteAllTextAsync(source, json, new UTF8Encoding(false));
            await Assert.ThrowsAsync<JsonIndexException>(
                () => new StreamingJsonIndexer().BuildAsync(source, index));
            Assert.False(File.Exists(index));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task SupportsMultipleJsonLinesRoots()
    {
        var (source, index) = CreatePaths("jsonl");
        try
        {
            await File.WriteAllTextAsync(source, "{\"id\":1}\n{\"id\":2}\n", new UTF8Encoding(false));
            await new StreamingJsonIndexer().BuildAsync(source, index);
            await using var reader = await BjxIndexReader.OpenAsync(index);
            var roots = await reader.GetChildrenAsync(0);
            Assert.Equal(2, roots.Count);
            Assert.All(roots, node => Assert.Equal(JsonNodeKind.Object, node.Kind));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task SupportsScalarJsonLinesAndValidSurrogatePairs()
    {
        var (source, index) = CreatePaths("jsonl");
        try
        {
            await File.WriteAllTextAsync(source, "1\n\"\\uD83D\\uDE03\"\nnull\n", new UTF8Encoding(false));
            await new StreamingJsonIndexer().BuildAsync(source, index);
            await using var reader = await BjxIndexReader.OpenAsync(index);
            var roots = await reader.GetChildrenAsync(0);
            Assert.Equal([JsonNodeKind.Number, JsonNodeKind.String, JsonNodeKind.Null], roots.Select(node => node.Kind));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task RejectsInvalidUtf8WithoutPublishingAnIndex()
    {
        var (source, index) = CreatePaths();
        try
        {
            await File.WriteAllBytesAsync(source, [.. "{\"value\":\""u8.ToArray(), 0xC0, .. "\"}"u8.ToArray()]);
            await Assert.ThrowsAsync<JsonIndexException>(
                () => new StreamingJsonIndexer().BuildAsync(source, index));
            Assert.False(File.Exists(index));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task RejectsAnIndexForChangedSource()
    {
        var (source, index) = CreatePaths();
        try
        {
            await File.WriteAllTextAsync(source, "[1]", new UTF8Encoding(false));
            var original = await SourceInspector.InspectAsync(source);
            await new StreamingJsonIndexer().BuildAsync(source, index);
            await File.WriteAllTextAsync(source, "[2]", new UTF8Encoding(false));
            var changed = await SourceInspector.InspectAsync(source);
            Assert.NotEqual(original.Identity, changed.Identity);
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await BjxIndexReader.OpenAsync(index, changed.Identity));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task CompactRecordsAreMateriallySmallerThanFixedRecords()
    {
        var (source, index) = CreatePaths();
        try
        {
            await using (var stream = new StreamWriter(source, false, new UTF8Encoding(false)))
            {
                await stream.WriteAsync('[');
                for (var item = 0; item < 10_000; item++)
                {
                    if (item != 0)
                    {
                        await stream.WriteAsync(',');
                    }

                    await stream.WriteAsync($"{{\"id\":{item},\"name\":\"item-{item}\",\"active\":true}}");
                }

                await stream.WriteAsync(']');
            }

            await new StreamingJsonIndexer().BuildAsync(source, index);
            await using var reader = await BjxIndexReader.OpenAsync(index);
            var oldFixedRecordSize = checked(reader.Header.NodeCount * 80L);
            Assert.True(new FileInfo(index).Length < oldFixedRecordSize / 2);
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task RecordCorruptionIsDetectedWhenTheNodeIsRead()
    {
        var (source, index) = CreatePaths();
        try
        {
            await File.WriteAllTextAsync(source, "{\"value\":123}", new UTF8Encoding(false));
            await new StreamingJsonIndexer().BuildAsync(source, index);
            await using (var stream = new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = 136;
                var value = stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(value ^ 0x5A));
            }

            await using var reader = await BjxIndexReader.OpenAsync(index);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.GetNodeAsync(0));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    [Fact]
    public async Task CancellationNeverPublishesAnIncompleteIndex()
    {
        var (source, index) = CreatePaths();
        try
        {
            await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync("{\"large\":\""u8.ToArray());
                var block = new byte[12 * 1024 * 1024];
                Array.Fill(block, (byte)'x');
                await stream.WriteAsync(block);
                await stream.WriteAsync("\"}"u8.ToArray());
            }

            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<IndexBuildProgress>(_ => cancellation.Cancel());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new StreamingJsonIndexer().BuildAsync(source, index, progress, cancellation.Token));
            Assert.False(File.Exists(index));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(index)!, Path.GetFileName(index) + ".*.building*"));
        }
        finally
        {
            File.Delete(source);
            File.Delete(index);
        }
    }

    private static (string Source, string Index) CreatePaths(string extension = "json")
    {
        var stem = Path.Combine(Path.GetTempPath(), $"bigjsonviewer-{Guid.NewGuid():N}");
        return ($"{stem}.{extension}", $"{stem}.bjx");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
