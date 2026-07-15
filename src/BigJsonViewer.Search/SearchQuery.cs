using System.Text;
using BigJsonViewer.Core;

namespace BigJsonViewer.Search;

public sealed class SearchQuery
{
    public const int MaximumUtf8Bytes = 1024 * 1024;

    public SearchQuery(
        string text,
        SearchMode mode = SearchMode.Anywhere,
        bool caseSensitive = true,
        SourceRange? range = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "Search text exceeds the 1 MiB UTF-8 limit.");
        }

        if (!caseSensitive && text.Any(character => character > 0x7F))
        {
            throw new NotSupportedException("Case-insensitive search currently supports ASCII text only.");
        }

        Text = text;
        Mode = mode;
        CaseSensitive = caseSensitive;
        Range = range;
    }

    public string Text { get; }

    public SearchMode Mode { get; }

    public bool CaseSensitive { get; }

    public SourceRange? Range { get; }
}
