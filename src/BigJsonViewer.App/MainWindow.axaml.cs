using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BigJsonViewer.Core;
using BigJsonViewer.Indexing;
using BigJsonViewer.Search;

namespace BigJsonViewer.App;

public partial class MainWindow : Window
{
    private const int ChildPageSize = 250;
    private const int SearchPageSize = 500;
    private readonly ObservableCollection<NodeRowViewModel> _treeRows = [];
    private readonly ObservableCollection<SearchRowViewModel> _searchRows = [];
    private readonly ObservableCollection<BreadcrumbItem> _breadcrumbs = [];
    private readonly ObservableCollection<CommandItem> _visibleCommands = [];
    private readonly CommandItem[] _commands =
    [
        new("open", "Open JSON file", "Ctrl+O"),
        new("search", "Focus search", "Ctrl+F"),
        new("go", "Go to JSON Pointer or byte offset", "Ctrl+G"),
        new("expand", "Expand visible nodes one level", "Ctrl+Shift+Right"),
        new("collapse", "Collapse all nodes", "Ctrl+Shift+Left"),
        new("profile", "Profile selected subtree", "Ctrl+I"),
        new("compare", "Compare with another JSON file", "Ctrl+D"),
        new("csv", "Export selected array as CSV", "Ctrl+Shift+E")
    ];
    private readonly DispatcherTimer _staleTimer;
    private readonly RecentFileStore _recentFiles = new();
    private readonly WorkspaceStateStore _workspaceStore = new();
    private readonly List<string> _searchHistory = [];
    private DocumentSession? _session;
    private SearchResults? _searchResults;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _previewOperation;
    private bool _checkingStale;
    private long _searchPageStart;
    private static readonly SolidColorBrush AccentStatusBrush = new(Color.Parse("#738CFF"));
    private static readonly SolidColorBrush ReadyStatusBrush = new(Color.Parse("#5CD6B3"));
    private static readonly SolidColorBrush ErrorStatusBrush = new(Color.Parse("#FF7185"));

    public MainWindow()
    {
        InitializeComponent();
        TreeRows.ItemsSource = _treeRows;
        SearchRows.ItemsSource = _searchRows;
        BreadcrumbItems.ItemsSource = _breadcrumbs;
        CommandList.ItemsSource = _visibleCommands;
        foreach (var command in _commands)
        {
            _visibleCommands.Add(command);
        }
        RefreshRecentFiles();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, DragOver);
        DragDrop.AddDropHandler(this, DropFile);
        _staleTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, CheckStale);
        _staleTimer.Start();
        ShowWelcome();
    }

    private async void OpenFile(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a JSON or JSON Lines file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON files") { Patterns = ["*.json", "*.jsonl", "*.ndjson"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await OpenPathAsync(path);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        CancelActiveOperation();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        SetDocumentCommands(false);
        SetBusy("Inspecting source…", 0);
        try
        {
            SaveWorkspaceState();
            await DisposeSessionAsync();
            var progress = new Progress<IndexBuildProgress>(value =>
            {
                SetBusy(
                    $"Indexing {value.BytesProcessed:N0} / {value.TotalBytes:N0} bytes · {value.NodesDiscovered:N0} nodes",
                    value.Fraction);
            });
            _session = await DocumentSession.OpenAsync(path, progress, token);
            _recentFiles.Add(path);
            RefreshRecentFiles();
            FileNameText.Text = Path.GetFileName(path);
            FileDetailsText.Text =
                $"{FormatSize(_session.Metadata.Identity.Length)} · {_session.Metadata.Format} · {_session.Metadata.Encoding} · " +
                $"{_session.Index.Header.NodeCount:N0} nodes · {FormatSize(_session.IndexLength)} index";
            Title = $"{Path.GetFileName(path)} — BigJsonViewer";
            _treeRows.Clear();
            await InsertChildrenAsync(0, 0, 0, 0, token);
            ShowWorkspace();
            SetDocumentCommands(true);
            await RestoreWorkspaceStateAsync(token);
            SetReady($"Opened {_session.Metadata.Path}");
            if (TreeRows.SelectedItem is null && _treeRows.Count > 0)
            {
                TreeRows.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            ShowWelcome();
            SetDocumentCommands(false);
            SetReady("Open/index operation cancelled.");
        }
        catch (Exception exception)
        {
            await DisposeSessionAsync();
            ShowWelcome();
            SetDocumentCommands(false);
            SetError(exception.Message);
        }
    }

    private async void ToggleNode(object? sender, RoutedEventArgs args)
    {
        if (_session is null || sender is not Button { DataContext: NodeRowViewModel row })
        {
            return;
        }

        try
        {
            await ToggleRowAsync(row);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async Task ToggleRowAsync(NodeRowViewModel row)
    {
        CancelPreviewOperation();
        _previewOperation = new CancellationTokenSource();
        var token = _previewOperation.Token;
        var index = _treeRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        if (row.IsLoadMore)
        {
            _treeRows.RemoveAt(index);
            await InsertChildrenAsync(row.Node.ParentId, row.Skip, index, row.Depth, token);
            return;
        }

        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            while (index + 1 < _treeRows.Count && _treeRows[index + 1].Depth > row.Depth)
            {
                _treeRows.RemoveAt(index + 1);
            }

            return;
        }

        row.IsExpanded = true;
        await InsertChildrenAsync(row.Node.Id, 0, index + 1, row.Depth + 1, token);
        ApplyTreeFilter();
    }

    private async Task InsertChildrenAsync(
        long parentId,
        long skip,
        int insertionIndex,
        int depth,
        CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var parent = await _session.Index.GetNodeAsync(parentId, cancellationToken);
        var children = await _session.Index.GetChildrenAsync(
            parentId,
            skip,
            ChildPageSize,
            cancellationToken);
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            var child = children[childIndex];
            long? arrayOrdinal = parent.Kind == JsonNodeKind.Array ? skip + childIndex : null;
            var label = await _session.GetNodeLabelAsync(child, cancellationToken, arrayOrdinal);
            _treeRows.Insert(insertionIndex++, new NodeRowViewModel(child, depth, label));
        }

        var next = skip + children.Count;
        if (next < parent.ChildCount)
        {
            _treeRows.Insert(insertionIndex, NodeRowViewModel.LoadMore(parentId, next, depth));
        }
    }

    private async void TreeSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_session is null || TreeRows.SelectedItem is not NodeRowViewModel { IsLoadMore: false } row)
        {
            return;
        }

        CancelPreviewOperation();
        _previewOperation = new CancellationTokenSource();
        try
        {
            RawPreview.Text = "Loading bounded preview…";
            var pretty = await _session.GetPrettyPreviewAsync(row.Node, _previewOperation.Token);
            RawPreview.Text = pretty;
            SourcePreview.Text = await _session.GetRawPreviewAsync(row.Node.Range, _previewOperation.Token);
            TablePreview.Text = await _session.BuildTableSampleAsync(row.Node, _previewOperation.Token);
            NodeTitleText.Text = row.DisplayText;
            NodeMetaText.Text =
                $"{row.Node.Kind}  ·  node {row.Node.Id:N0}  ·  offset {row.Node.Range.Offset:N0}  ·  {FormatSize(row.Node.Range.Length)}";
            StatusText.Text =
                $"Node {row.Node.Id:N0} · {row.Node.Kind} · bytes {row.Node.Range.Offset:N0}–{row.Node.Range.End:N0} · " +
                $"source cache {_session.SourceCacheStatistics.Hits:N0} hits/{_session.SourceCacheStatistics.Misses:N0} misses · " +
                $"index cache {_session.Index.CacheStatistics.Hits:N0} hits/{_session.Index.CacheStatistics.Misses:N0} misses";
            await UpdateBreadcrumbsAsync(row.Node, _previewOperation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async Task UpdateBreadcrumbsAsync(IndexedNode node, CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var path = await _session.GetNodePathAsync(node, cancellationToken);
        _breadcrumbs.Clear();
        foreach (var item in path)
        {
            var label = await _session.GetNodePathSegmentAsync(item, cancellationToken);
            _breadcrumbs.Add(new BreadcrumbItem(item, label));
        }
    }

    private async void NavigateBreadcrumb(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: BreadcrumbItem item })
        {
            await RevealNodeAsync(item.Node);
        }
    }

    private async Task RevealNodeAsync(IndexedNode node)
    {
        if (_session is null)
        {
            return;
        }

        var existing = _treeRows.FirstOrDefault(item => !item.IsLoadMore && item.Node.Id == node.Id);
        if (existing is null)
        {
            var path = await _session.GetNodePathAsync(node, CancellationToken.None);
            _treeRows.Clear();
            var depth = 0;
            foreach (var item in path)
            {
                var row = new NodeRowViewModel(item, depth++, await _session.GetNodeLabelAsync(item));
                row.IsExpanded = item.Id != node.Id;
                _treeRows.Add(row);
            }

            existing = _treeRows.LastOrDefault();
        }

        ApplyTreeFilter();
        if (existing is not null)
        {
            TreeRows.SelectedItem = existing;
            TreeRows.ScrollIntoView(existing);
        }
    }

    private async void NavigateToValue(object? sender, RoutedEventArgs args) => await NavigateToValueAsync();

    private async void GoValueKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            await NavigateToValueAsync();
        }
    }

    private async Task NavigateToValueAsync()
    {
        if (_session is null || string.IsNullOrWhiteSpace(GoValueBox.Text))
        {
            return;
        }

        try
        {
            SetBusy("Locating node…", 0);
            IndexedNode node;
            if (GoModeBox.SelectedIndex == 1)
            {
                if (!long.TryParse(GoValueBox.Text.Replace(",", string.Empty, StringComparison.Ordinal), out var offset))
                {
                    throw new FormatException("Enter a numeric byte offset.");
                }

                node = await _session.FindNodeByOffsetAsync(offset);
            }
            else
            {
                node = await _session.FindNodeByPointerAsync(GoValueBox.Text.Trim());
            }

            await RevealNodeAsync(node);
            SetReady($"Located node {node.Id:N0} at byte {node.Range.Offset:N0}.");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void TreeFilterChanged(object? sender, TextChangedEventArgs args) => ApplyTreeFilter();

    private void ApplyTreeFilter()
    {
        var filter = TreeFilterBox.Text?.Trim();
        foreach (var row in _treeRows)
        {
            row.IsFilterMatch = string.IsNullOrEmpty(filter) ||
                row.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async void ExpandOneLevel(object? sender, RoutedEventArgs args)
    {
        var rows = _treeRows.Where(row => row.CanExpand && !row.IsExpanded && !row.IsLoadMore).ToArray();
        foreach (var row in rows)
        {
            await ToggleRowAsync(row);
        }
    }

    private async void CollapseAll(object? sender, RoutedEventArgs args)
    {
        if (_session is null)
        {
            return;
        }

        _treeRows.Clear();
        await InsertChildrenAsync(0, 0, 0, 0, CancellationToken.None);
        ApplyTreeFilter();
    }

    private async void ProfileSelection(object? sender, RoutedEventArgs args)
    {
        if (_session is null)
        {
            return;
        }

        var node = TreeRows.SelectedItem is NodeRowViewModel { IsLoadMore: false } row
            ? row.Node
            : await _session.Index.GetNodeAsync(0);
        CancelActiveOperation();
        _operation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(value => SetBusy("Profiling selected structure…", value));
            InsightsPreview.Text = await _session.BuildStatisticsAsync(node, progress, _operation.Token);
            DetailTabs.SelectedIndex = 4;
            SetReady("Structure profile complete.");
        }
        catch (OperationCanceledException)
        {
            SetReady("Structure profile cancelled.");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async void CompareFile(object? sender, RoutedEventArgs args)
    {
        if (_session is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Compare with another JSON file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON files") { Patterns = ["*.json", "*.jsonl", "*.ndjson"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        CancelActiveOperation();
        _operation = new CancellationTokenSource();
        try
        {
            var indexProgress = new Progress<IndexBuildProgress>(value => SetBusy("Indexing comparison file…", value.Fraction));
            await using var other = await DocumentSession.OpenAsync(path, indexProgress, _operation.Token);
            var compareProgress = new Progress<double>(value => SetBusy("Comparing indexes and scalar values…", value));
            InsightsPreview.Text = await _session.CompareAsync(other, compareProgress, _operation.Token);
            DetailTabs.SelectedIndex = 4;
            SetReady($"Comparison with {Path.GetFileName(path)} complete.");
        }
        catch (OperationCanceledException)
        {
            SetReady("Comparison cancelled.");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async void ExportCsv(object? sender, RoutedEventArgs args)
    {
        if (_session is null || TreeRows.SelectedItem is not NodeRowViewModel { IsLoadMore: false, Node.Kind: JsonNodeKind.Array } row)
        {
            SetError("Select an array before exporting CSV.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export array as CSV",
            SuggestedFileName = $"array-{row.Node.Id}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }]
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        CancelActiveOperation();
        _operation = new CancellationTokenSource();
        try
        {
            SetBusy("Streaming CSV export…", 0);
            await _session.ExportCsvAsync(row.Node, path, _operation.Token);
            SetReady($"Exported CSV to {path}");
        }
        catch (OperationCanceledException)
        {
            SetReady("CSV export cancelled.");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void WindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.KeyModifiers.HasFlag(KeyModifiers.Shift) && args.Key == Key.P)
        {
            ShowCommandPalette();
            args.Handled = true;
        }
        else if (args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var command = args.Key switch
            {
                Key.O => "open",
                Key.F => "search",
                Key.G => "go",
                Key.I => "profile",
                Key.D => "compare",
                Key.Right when args.KeyModifiers.HasFlag(KeyModifiers.Shift) => "expand",
                Key.Left when args.KeyModifiers.HasFlag(KeyModifiers.Shift) => "collapse",
                Key.E when args.KeyModifiers.HasFlag(KeyModifiers.Shift) => "csv",
                _ => null
            };
            if (command is not null)
            {
                ExecuteCommand(command);
                args.Handled = true;
            }
        }
    }

    private void ShowCommandPalette()
    {
        CommandPaletteOverlay.IsVisible = true;
        CommandFilterBox.Text = string.Empty;
        RefreshVisibleCommands(string.Empty);
        CommandList.SelectedIndex = 0;
        CommandFilterBox.Focus();
    }

    private void PaletteKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            CommandPaletteOverlay.IsVisible = false;
            args.Handled = true;
        }
        else if (args.Key == Key.Enter && CommandList.SelectedItem is CommandItem command)
        {
            CommandPaletteOverlay.IsVisible = false;
            ExecuteCommand(command.Id);
            args.Handled = true;
        }
        else if (args.Key == Key.Down && CommandList.ItemCount > 0)
        {
            CommandList.SelectedIndex = Math.Min(CommandList.ItemCount - 1, CommandList.SelectedIndex + 1);
            args.Handled = true;
        }
        else if (args.Key == Key.Up && CommandList.ItemCount > 0)
        {
            CommandList.SelectedIndex = Math.Max(0, CommandList.SelectedIndex - 1);
            args.Handled = true;
        }
    }

    private void CommandFilterChanged(object? sender, TextChangedEventArgs args)
    {
        var filter = CommandFilterBox.Text?.Trim();
        RefreshVisibleCommands(filter);
    }

    private void RefreshVisibleCommands(string? filter)
    {
        _visibleCommands.Clear();
        var recentSearches = _searchHistory.Take(10).Select(query => new CommandItem($"search:{query}", $"Search again: {query}", string.Empty));
        foreach (var command in _commands.Concat(recentSearches).Where(command =>
                     string.IsNullOrEmpty(filter) || command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            _visibleCommands.Add(command);
        }

        CommandList.SelectedIndex = _visibleCommands.Count == 0 ? -1 : 0;
    }

    private void CommandDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (CommandList.SelectedItem is CommandItem command)
        {
            CommandPaletteOverlay.IsVisible = false;
            ExecuteCommand(command.Id);
        }
    }

    private void ExecuteCommand(string command)
    {
        if (command.StartsWith("search:", StringComparison.Ordinal))
        {
            SearchBox.Text = command[7..];
            StartSearch(null, new RoutedEventArgs());
            return;
        }

        switch (command)
        {
            case "open": OpenFile(null, new RoutedEventArgs()); break;
            case "search": SearchBox.Focus(); break;
            case "go": GoValueBox.Focus(); break;
            case "expand": ExpandOneLevel(null, new RoutedEventArgs()); break;
            case "collapse": CollapseAll(null, new RoutedEventArgs()); break;
            case "profile": ProfileSelection(null, new RoutedEventArgs()); break;
            case "compare": CompareFile(null, new RoutedEventArgs()); break;
            case "csv": ExportCsv(null, new RoutedEventArgs()); break;
        }
    }

    private async void StartSearch(object? sender, RoutedEventArgs args) => await RunSearchAsync();

    private async void SearchBoxKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            args.Handled = true;
            await RunSearchAsync();
        }
    }

    private async Task RunSearchAsync()
    {
        if (_session is null || string.IsNullOrEmpty(SearchBox.Text))
        {
            return;
        }

        RememberSearch(SearchBox.Text);

        CancelActiveOperation();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        _searchRows.Clear();
        if (_searchResults is not null)
        {
            await _searchResults.DisposeAsync();
            _searchResults = null;
        }

        var mode = SearchModeBox.SelectedIndex switch
        {
            1 => SearchMode.StringValues,
            2 => SearchMode.PropertyNames,
            3 => SearchMode.OutsideStrings,
            _ => SearchMode.Anywhere
        };
        SourceRange? searchRange = SelectedSubtreeOnly.IsChecked == true &&
            TreeRows.SelectedItem is NodeRowViewModel { IsLoadMore: false } selected
                ? selected.Node.Range
                : null;
        try
        {
            DetailTabs.SelectedIndex = 3;
            var progress = new Progress<SearchProgress>(value =>
            {
                SetBusy(
                    $"Searching {value.BytesProcessed:N0} / {value.TotalBytes:N0} bytes · {value.MatchesFound:N0} matches",
                    value.Fraction);
                SearchSummary.Text = $"{value.MatchesFound:N0} matches found so far";
                foreach (var match in value.LatestBatch)
                {
                    if (_searchRows.Count >= SearchPageSize)
                    {
                        break;
                    }

                    _searchRows.Add(new SearchRowViewModel(match, "match"));
                }
            });
            _searchResults = await new Utf8FileSearch().SearchAsync(
                _session.Metadata.Path,
                new SearchQuery(SearchBox.Text, mode, range: searchRange),
                progress,
                token);
            if (await _session.Guard.CheckForChangeAsync(token))
            {
                await _searchResults.DisposeAsync();
                _searchResults = null;
                throw new IOException("The source changed during search. Reopen it before searching again.");
            }

            _searchPageStart = 0;
            await LoadSearchPageAsync(_searchPageStart, token);
            SetReady($"Search complete: {_searchResults.Count:N0} matches.");
        }
        catch (OperationCanceledException)
        {
            SetReady("Search cancelled.");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async Task LoadSearchPageAsync(long skip, CancellationToken cancellationToken)
    {
        if (_session is null || _searchResults is null)
        {
            return;
        }

        _searchRows.Clear();
        var page = await _searchResults.GetPageAsync(skip, SearchPageSize, cancellationToken);
        foreach (var match in page)
        {
            var preview = await _session.GetRawPreviewAsync(match.Range, cancellationToken);
            _searchRows.Add(new SearchRowViewModel(match, preview));
        }

        var end = skip + page.Count;
        SearchSummary.Text = _searchResults.Count == 0
            ? "No matches"
            : $"{_searchResults.Count:N0} matches · showing {skip + 1:N0}–{end:N0}";
    }

    private async void PreviousSearchPage(object? sender, RoutedEventArgs args)
    {
        if (_searchResults is null || _searchPageStart == 0)
        {
            return;
        }

        _searchPageStart = Math.Max(0, _searchPageStart - SearchPageSize);
        await LoadSearchPageAsync(_searchPageStart, CancellationToken.None);
    }

    private async void NextSearchPage(object? sender, RoutedEventArgs args)
    {
        if (_searchResults is null || _searchPageStart + SearchPageSize >= _searchResults.Count)
        {
            return;
        }

        _searchPageStart += SearchPageSize;
        await LoadSearchPageAsync(_searchPageStart, CancellationToken.None);
    }

    private async void SearchSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_session is null || SearchRows.SelectedItem is not SearchRowViewModel row)
        {
            return;
        }

        CancelPreviewOperation();
        _previewOperation = new CancellationTokenSource();
        try
        {
            SourcePreview.Text = await _session.ReadAroundAsync(row.Match.Range, _previewOperation.Token);
            DetailTabs.SelectedIndex = 1;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelOperation(object? sender, RoutedEventArgs args)
    {
        CancelActiveOperation();
        CancelPreviewOperation();
    }

    private async void RecentFileSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (RecentFilesBox.SelectedItem is string path)
        {
            RecentFilesBox.SelectedItem = null;
            await OpenPathAsync(path);
        }
    }

    private void DragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = args.DataTransfer.TryGetFiles()?.Any() == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        args.Handled = true;
    }

    private async void DropFile(object? sender, DragEventArgs args)
    {
        var file = args.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path)
        {
            args.Handled = true;
            await OpenPathAsync(path);
        }
    }

    private async void CopyPointer(object? sender, RoutedEventArgs args)
    {
        if (_session is null || TreeRows.SelectedItem is not NodeRowViewModel { IsLoadMore: false } row ||
            Clipboard is null)
        {
            return;
        }

        try
        {
            var pointer = await _session.GetJsonPointerAsync(row.Node);
            await Clipboard.SetTextAsync(pointer);
            SetReady($"Copied JSON Pointer: {pointer}");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async void ExportSelection(object? sender, RoutedEventArgs args)
    {
        if (_session is null || TreeRows.SelectedItem is not NodeRowViewModel { IsLoadMore: false } row)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export selected JSON subtree",
            SuggestedFileName = $"node-{row.Node.Id}.json",
            FileTypeChoices = [new FilePickerFileType("JSON file") { Patterns = ["*.json"] }]
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        CancelActiveOperation();
        _operation = new CancellationTokenSource();
        try
        {
            SetBusy($"Exporting {row.Node.Range.Length:N0} bytes…", 0);
            await _session.ExportAsync(row.Node.Range, path, _operation.Token);
            SetReady($"Exported selection to {path}");
        }
        catch (OperationCanceledException)
        {
            SetReady("Export cancelled.");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async void CheckStale(object? sender, EventArgs args)
    {
        if (_checkingStale || _session is null)
        {
            return;
        }

        _checkingStale = true;
        try
        {
            if (await _session.Guard.CheckForChangeAsync())
            {
                CancelActiveOperation();
                SetError("The source file changed or was replaced. Reopen it before continuing.");
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            _checkingStale = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _staleTimer.Stop();
        SaveWorkspaceState();
        CancelActiveOperation();
        CancelPreviewOperation();
        DisposeSessionAsync().AsTask().GetAwaiter().GetResult();
        base.OnClosed(e);
    }

    private async ValueTask DisposeSessionAsync()
    {
        if (_searchResults is not null)
        {
            await _searchResults.DisposeAsync();
            _searchResults = null;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _treeRows.Clear();
        _searchRows.Clear();
    }

    private void SetBusy(string message, double fraction)
    {
        OperationProgress.Value = Math.Clamp(fraction, 0, 1);
        OperationText.Text = fraction > 0 ? $"WORKING · {fraction:P0}" : "WORKING";
        StatusText.Text = message;
        StatusIndicator.Background = AccentStatusBrush;
        CancelButton.IsEnabled = true;
    }

    private void SetReady(string message)
    {
        OperationProgress.Value = 0;
        OperationText.Text = "Ready";
        StatusText.Text = message;
        StatusIndicator.Background = ReadyStatusBrush;
        CancelButton.IsEnabled = false;
    }

    private void SetError(string message)
    {
        OperationProgress.Value = 0;
        OperationText.Text = "Error";
        StatusText.Text = message;
        RawPreview.Text = message;
        SourcePreview.Text = message;
        StatusIndicator.Background = ErrorStatusBrush;
        CancelButton.IsEnabled = false;
    }

    private void CancelActiveOperation()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private void CancelPreviewOperation()
    {
        _previewOperation?.Cancel();
        _previewOperation?.Dispose();
        _previewOperation = null;
    }

    private void RefreshRecentFiles()
    {
        RecentFilesBox.ItemsSource = _recentFiles.Paths.ToArray();
    }

    private void ShowWelcome()
    {
        WelcomePanel.IsVisible = true;
        WorkspaceGrid.IsVisible = false;
        FileNameText.Text = "No document open";
        FileDetailsText.Text = "Open a JSON or JSON Lines file to begin";
        Title = "BigJsonViewer";
    }

    private void ShowWorkspace()
    {
        WelcomePanel.IsVisible = false;
        WorkspaceGrid.IsVisible = true;
    }

    private void SetDocumentCommands(bool enabled)
    {
        SearchBox.IsEnabled = enabled;
        SearchModeBox.IsEnabled = enabled;
        SearchHistoryBox.IsEnabled = enabled;
        SelectedSubtreeOnly.IsEnabled = enabled;
        SearchButton.IsEnabled = enabled;
    }

    private void RememberSearch(string query)
    {
        _searchHistory.RemoveAll(item => string.Equals(item, query, StringComparison.Ordinal));
        _searchHistory.Insert(0, query);
        if (_searchHistory.Count > 20)
        {
            _searchHistory.RemoveRange(20, _searchHistory.Count - 20);
        }

        RefreshSearchHistory();
    }

    private void RefreshSearchHistory()
    {
        SearchHistoryBox.ItemsSource = _searchHistory.ToArray();
    }

    private void SearchHistorySelected(object? sender, SelectionChangedEventArgs args)
    {
        if (SearchHistoryBox.SelectedItem is string query)
        {
            SearchBox.Text = query;
            SearchHistoryBox.SelectedItem = null;
            SearchBox.Focus();
        }
    }

    private void SaveWorkspaceState()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _workspaceStore.Save(_session.Metadata.Path, new WorkspaceStateData
            {
                SelectedNodeId = TreeRows.SelectedItem is NodeRowViewModel { IsLoadMore: false } selected ? selected.Node.Id : -1,
                SelectedDetailTab = DetailTabs.SelectedIndex,
                ExpandedNodeIds = _treeRows.Where(row => row.IsExpanded && !row.IsLoadMore).Select(row => row.Node.Id).Take(100).ToList(),
                SearchHistory = _searchHistory.Take(20).ToList()
            });
        }
        catch (IOException)
        {
        }
    }

    private async Task RestoreWorkspaceStateAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var state = _workspaceStore.Load(_session.Metadata.Path);
        _searchHistory.Clear();
        _searchHistory.AddRange(state.SearchHistory.Where(query => !string.IsNullOrWhiteSpace(query)).Take(20));
        RefreshSearchHistory();
        foreach (var id in state.ExpandedNodeIds.Distinct().Order().Take(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = _treeRows.FirstOrDefault(item => !item.IsLoadMore && item.Node.Id == id);
            if (row is { CanExpand: true, IsExpanded: false })
            {
                await ToggleRowAsync(row);
            }
        }

        if ((ulong)state.SelectedNodeId < (ulong)_session.Index.Header.NodeCount)
        {
            var node = await _session.Index.GetNodeAsync(state.SelectedNodeId, cancellationToken);
            await RevealNodeAsync(node);
        }

        DetailTabs.SelectedIndex = Math.Clamp(state.SelectedDetailTab, 0, 4);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }
}
