using BigJsonViewer.App;
using BigJsonViewer.Core;

namespace BigJsonViewer.Tests;

public sealed class DocumentSessionFeatureTests
{
    [Fact]
    public async Task NavigationTableStatisticsCsvAndComparisonWorkTogether()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BigJsonViewer-features-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var leftPath = Path.Combine(directory, "left.json");
        var rightPath = Path.Combine(directory, "right.json");
        var csvPath = Path.Combine(directory, "items.csv");
        const string leftJson = """{"items":[{"name":"alpha","value":1},{"name":"beta","value":2}],"a/b~c":true,"when":"2026-07-13T20:45:00\u002B00:00","payloadJson":"{\u0022x\u0022:true}"}""";
        const string rightJson = """{"items":[{"name":"alpha","value":1},{"name":"beta","value":3}],"a/b~c":true,"when":"2026-07-13T20:45:00\u002B00:00","payloadJson":"{\u0022x\u0022:true}"}""";
        await File.WriteAllTextAsync(leftPath, leftJson);
        await File.WriteAllTextAsync(rightPath, rightJson);

        try
        {
            await using var left = await DocumentSession.OpenAsync(leftPath);
            await using var right = await DocumentSession.OpenAsync(rightPath);

            var beta = await left.FindNodeByPointerAsync("/items/1/name");
            Assert.Equal(JsonNodeKind.String, beta.Kind);
            Assert.Equal("\"beta\"", await left.GetRawPreviewAsync(beta.Range));

            var secondItem = await left.FindNodeByPointerAsync("/items/1");
            Assert.StartsWith("[1]:", await left.GetNodeLabelAsync(secondItem), StringComparison.Ordinal);

            var escapedName = await left.FindNodeByPointerAsync("/a~1b~0c");
            Assert.Equal("/a~1b~0c", await left.GetJsonPointerAsync(escapedName));

            var root = await left.FindNodeByPointerAsync(string.Empty);
            var formatted = await left.GetPrettyPreviewAsync(root);
            Assert.DoesNotContain("\\u0022", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\u002B", formatted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("+00:00", formatted, StringComparison.Ordinal);
            Assert.Contains("\"x\": true", formatted, StringComparison.Ordinal);
            Assert.Contains("decoded embedded JSON string", formatted, StringComparison.Ordinal);

            var byOffset = await left.FindNodeByOffsetAsync(leftJson.IndexOf("beta", StringComparison.Ordinal));
            Assert.Equal(beta.Id, byOffset.Id);

            var items = await left.FindNodeByPointerAsync("/items");
            var table = await left.BuildTableSampleAsync(items);
            Assert.Contains("name\tvalue", table, StringComparison.Ordinal);

            var profile = await left.BuildStatisticsAsync(items);
            Assert.Contains("TYPE DISTRIBUTION", profile, StringComparison.Ordinal);
            Assert.Contains("name", profile, StringComparison.Ordinal);

            await left.ExportCsvAsync(items, csvPath);
            var csv = await File.ReadAllTextAsync(csvPath);
            Assert.Contains("name,value", csv, StringComparison.Ordinal);
            Assert.Contains("beta,2", csv, StringComparison.Ordinal);

            var comparison = await left.CompareAsync(right);
            Assert.Contains("Scalar-value changes: 1", comparison, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceStateRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BigJsonViewer-state-{Guid.NewGuid():N}");
        var store = new WorkspaceStateStore(directory);
        var sourcePath = Path.Combine(directory, "source.json");
        var expected = new WorkspaceStateData
        {
            SelectedNodeId = 42,
            SelectedDetailTab = 3,
            ExpandedNodeIds = [1, 4, 8],
            SearchHistory = ["needle", "second"]
        };

        store.Save(sourcePath, expected);
        var actual = store.Load(sourcePath);

        Assert.Equal(expected.SelectedNodeId, actual.SelectedNodeId);
        Assert.Equal(expected.SelectedDetailTab, actual.SelectedDetailTab);
        Assert.Equal(expected.ExpandedNodeIds, actual.ExpandedNodeIds);
        Assert.Equal(expected.SearchHistory, actual.SearchHistory);
        Directory.Delete(directory, recursive: true);
    }
}
