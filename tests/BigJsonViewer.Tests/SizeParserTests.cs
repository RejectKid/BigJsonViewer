using BigJsonViewer.CorpusGenerator;

namespace BigJsonViewer.Tests;

public sealed class SizeParserTests
{
    [Theory]
    [InlineData("128", 128)]
    [InlineData("1KB", 1_000)]
    [InlineData("1.5MB", 1_500_000)]
    [InlineData("2KiB", 2_048)]
    [InlineData("3MiB", 3_145_728)]
    [InlineData("1GiB", 1_073_741_824)]
    public void ParsesSupportedSizes(string value, long expected)
    {
        Assert.Equal(expected, SizeParser.Parse(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1.2B")]
    [InlineData("12XB")]
    [InlineData("lots")]
    public void RejectsInvalidSizes(string value)
    {
        Assert.ThrowsAny<Exception>(() => SizeParser.Parse(value));
    }
}
