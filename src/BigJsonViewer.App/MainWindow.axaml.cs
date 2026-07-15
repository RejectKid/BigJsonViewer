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
    private readonly DispatcherTimer _staleTimer;
    private readonly RecentFileStore _recentFiles = new();
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
            SetReady($"Opened {_session.Metadata.Path}");
            if (_treeRows.Count > 0)
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

        CancelPreviewOperation();
        _previewOperation = new CancellationTokenSource();
        var token = _previewOperation.Token;
        try
        {
            var index = _treeRows.IndexOf(row);
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
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
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
        foreach (var child in children)
        {
            var label = await _session.GetNodeLabelAsync(child, cancellationToken);
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
            TablePreview.Text = await _session.BuildTableSampleAsync(row.Node, _previewOperation.Token);
            NodeTitleText.Text = row.DisplayText;
            NodeMetaText.Text =
                $"{row.Node.Kind}  ·  node {row.Node.Id:N0}  ·  offset {row.Node.Range.Offset:N0}  ·  {FormatSize(row.Node.Range.Length)}";
            StatusText.Text =
                $"Node {row.Node.Id:N0} · {row.Node.Kind} · bytes {row.Node.Range.Offset:N0}–{row.Node.Range.End:N0} · " +
                $"source cache {_session.SourceCacheStatistics.Hits:N0} hits/{_session.SourceCacheStatistics.Misses:N0} misses · " +
                $"index cache {_session.Index.CacheStatistics.Hits:N0} hits/{_session.Index.CacheStatistics.Misses:N0} misses";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
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
            DetailTabs.SelectedIndex = 2;
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
            RawPreview.Text = await _session.ReadAroundAsync(row.Match.Range, _previewOperation.Token);
            DetailTabs.SelectedIndex = 0;
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
        OperationText.Text = message;
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
        SelectedSubtreeOnly.IsEnabled = enabled;
        SearchButton.IsEnabled = enabled;
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
