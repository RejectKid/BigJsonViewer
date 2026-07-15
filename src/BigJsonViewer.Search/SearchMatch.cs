using BigJsonViewer.Core;

namespace BigJsonViewer.Search;

public readonly record struct SearchMatch(
    SourceRange Range,
    bool IsInsideString,
    bool IsPropertyName = false);
