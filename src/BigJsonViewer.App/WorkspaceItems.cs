using BigJsonViewer.Core;

namespace BigJsonViewer.App;

public sealed record BreadcrumbItem(IndexedNode Node, string Label);

public sealed record CommandItem(string Id, string Name, string Shortcut);
