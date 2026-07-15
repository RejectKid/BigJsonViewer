using BigJsonViewer.Search;

namespace BigJsonViewer.App;

public sealed class SearchRowViewModel(SearchMatch match, string preview)
{
    public SearchMatch Match { get; } = match;

    public string DisplayText { get; } = $"{match.Range.Offset:N0}  {preview.ReplaceLineEndings(" ")}";
}
