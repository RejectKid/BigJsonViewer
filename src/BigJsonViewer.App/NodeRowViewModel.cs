using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using BigJsonViewer.Core;

namespace BigJsonViewer.App;

public sealed class NodeRowViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isFilterMatch = true;

    public NodeRowViewModel(IndexedNode node, int depth, string displayText)
    {
        Node = node;
        Depth = depth;
        DisplayText = displayText;
        Indent = new Thickness(depth * 18, 2, 4, 2);
    }

    private NodeRowViewModel(long parentId, long skip, int depth)
    {
        Node = new IndexedNode(-1, parentId, default, default, 0, JsonNodeKind.Document);
        Skip = skip;
        Depth = depth;
        DisplayText = $"Load more children after {skip:N0}…";
        Indent = new Thickness(depth * 18, 2, 4, 2);
        IsLoadMore = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IndexedNode Node { get; }

    public int Depth { get; }

    public long Skip { get; }

    public string DisplayText { get; }

    public Thickness Indent { get; }

    public bool IsLoadMore { get; }

    public bool CanExpand => IsLoadMore || Node.ChildCount > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowVerticalStroke));
        }
    }

    public bool ShowVerticalStroke => IsLoadMore || !IsExpanded;

    public bool IsFilterMatch
    {
        get => _isFilterMatch;
        set
        {
            if (_isFilterMatch == value)
            {
                return;
            }

            _isFilterMatch = value;
            OnPropertyChanged();
        }
    }

    public static NodeRowViewModel LoadMore(long parentId, long skip, int depth) => new(parentId, skip, depth);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
