using BigJsonViewer.Core;

namespace BigJsonViewer.Tests;

public sealed class SourceRangeTests
{
    [Theory]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(9, false)]
    public void ContainsUsesHalfOpenRange(long position, bool expected)
    {
        var range = new SourceRange(10, 5);

        Assert.Equal(expected, range.Contains(position));
    }
}
