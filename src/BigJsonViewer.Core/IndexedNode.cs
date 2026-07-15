namespace BigJsonViewer.Core;

public readonly record struct IndexedNode(
    long Id,
    long ParentId,
    SourceRange Range,
    SourceRange NameRange,
    long ChildCount,
    JsonNodeKind Kind,
    long FirstChildId = -1,
    long SubtreeEndId = -1);
