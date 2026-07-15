using System.Text;
using BigJsonViewer.Search;

namespace BigJsonViewer.Tests;

public sealed class Utf8FileSearchTests
{
    [Fact]
    public async Task FindsMatchesAcrossReadBoundariesAndPagesFromDisk()
    {
        var path = CreatePath();
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var prefix = new byte[4 * 1024 * 1024 - 2];
                Array.Fill(prefix, (byte)' ');
                await stream.WriteAsync(prefix);
                await stream.WriteAsync("needle needle"u8.ToArray());
            }

            await using var results = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery("needle"));
            Assert.Equal(2, results.Count);
            var page = await results.GetPageAsync(0, 1);
            var match = Assert.Single(page);
            Assert.Equal(4 * 1024 * 1024 - 2, match.Range.Offset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FiltersStringAndScalarMatches()
    {
        var path = CreatePath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"value\":\"123\",\"number\":123}", new UTF8Encoding(false));
            await using var strings = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery("123", SearchMode.InsideStrings));
            await using var scalars = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery("123", SearchMode.OutsideStrings));

            Assert.Equal(1, strings.Count);
            Assert.Equal(1, scalars.Count);
            Assert.True(Assert.Single(await strings.GetPageAsync(0)).IsInsideString);
            Assert.False(Assert.Single(await scalars.GetPageAsync(0)).IsInsideString);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DistinguishesPropertyNamesFromStringValuesAndRestrictsRanges()
    {
        var path = CreatePath();
        try
        {
            const string json = "{\"target\":\"target\",\"nested\":{\"target\":\"target\"}}";
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
            await using var names = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery("target", SearchMode.PropertyNames));
            await using var values = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery("target", SearchMode.StringValues));
            var nestedOffset = json.IndexOf("{\"target\"", 2, StringComparison.Ordinal);
            await using var nested = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery(
                    "target",
                    SearchMode.Anywhere,
                    range: new BigJsonViewer.Core.SourceRange(nestedOffset, json.Length - nestedOffset - 1)));

            Assert.Equal(2, names.Count);
            Assert.Equal(2, values.Count);
            Assert.Equal(2, nested.Count);
            Assert.All(await names.GetPageAsync(0), match => Assert.True(match.IsPropertyName));
            Assert.All(await values.GetPageAsync(0), match => Assert.False(match.IsPropertyName));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReportsBoundedBatchesAndSupportsCancellation()
    {
        var path = CreatePath();
        try
        {
            await File.WriteAllTextAsync(path, string.Join(',', Enumerable.Repeat("\"x\"", 10_000)));
            var maximumBatch = 0;
            var progress = new InlineProgress<SearchProgress>(value =>
                maximumBatch = Math.Max(maximumBatch, value.LatestBatch.Count));
            await using var results = await new Utf8FileSearch().SearchAsync(
                path,
                new SearchQuery("x"),
                progress);
            Assert.Equal(10_000, results.Count);
            Assert.InRange(maximumBatch, 1, 256);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new Utf8FileSearch().SearchAsync(
                    path,
                    new SearchQuery("x"),
                    cancellationToken: cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreatePath() =>
        Path.Combine(Path.GetTempPath(), $"bigjsonviewer-{Guid.NewGuid():N}.json");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
