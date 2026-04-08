using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class SearchResultGroup
{
    public string PageHeader { get; init; } = "";
    public int PageIndex { get; init; }
    public List<SearchResultItem> Items { get; init; } = [];
}

public class SearchResultItem
{
    public int MatchIndex { get; init; }
    public string PreText { get; init; } = "";
    public string MatchText { get; init; } = "";
    public string PostText { get; init; } = "";
}

public class PeekEntryViewModel
{
    public required string Label { get; init; }
    public required string PageDisplay { get; init; }
    public Bitmap? Thumbnail { get; set; }
    public required PeekEntry Entry { get; init; }
}

public partial class OutlinePanel : UserControl
{
    private MainWindowViewModel? _vm;
    private DispatcherTimer? _debounceTimer;
    private CancellationTokenSource? _searchCts;
    private bool _suppressOutlineSelection;

    public OutlinePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        SearchInput.TextChanged += OnSearchTextChanged;
        SearchInput.KeyDown += OnSearchKeyDown;
        CaseSensitiveToggle.IsCheckedChanged += OnSearchOptionChanged;
        RegexToggle.IsCheckedChanged += OnSearchOptionChanged;
        PaneTabs.SelectionChanged += OnPaneTabChanged;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // Clean up DataContext subscriptions
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SearchRequested -= OnSearchRequested;
        }
        if (_watchedTab is not null)
        {
            _watchedTab.PropertyChanged -= OnTabPropertyChanged;
            _watchedTab = null;
        }
        _vm = null;

        // Dispose the debounce timer
        _debounceTimer?.Stop();
        _debounceTimer = null;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        UnsubscribePeekUpdates();

        // Unsubscribe constructor-level subscriptions
        DataContextChanged -= OnDataContextChanged;
        SearchInput.TextChanged -= OnSearchTextChanged;
        SearchInput.KeyDown -= OnSearchKeyDown;
        CaseSensitiveToggle.IsCheckedChanged -= OnSearchOptionChanged;
        RegexToggle.IsCheckedChanged -= OnSearchOptionChanged;
        PaneTabs.SelectionChanged -= OnPaneTabChanged;

        base.OnUnloaded(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SearchRequested -= OnSearchRequested;
            if (_watchedTab is not null)
            {
                _watchedTab.PropertyChanged -= OnTabPropertyChanged;
                _watchedTab = null;
            }
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.SearchRequested += OnSearchRequested;
            WatchActiveTabPage();
            UpdateOutlineSource();
            SyncOutlineToPage();
            UpdateBookmarkSource();
            SubscribePeekUpdates();
        }
    }

    private void OnSearchRequested(string? prefill)
    {
        SwitchToSearchTab();
        if (prefill is not null)
            SearchInput.Text = prefill;
        // Delay focus so the tab switch completes first
        Dispatcher.UIThread.Post(FocusSearch, DispatcherPriority.Input);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
        {
            WatchActiveTabPage();
            UpdateOutlineSource();
            SyncOutlineToPage();
            UpdateBookmarkSource();

            // Only reset peek state when the actual document changes (tab switch),
            // not on every page navigation within the same document.
            var newDoc = _vm?.Controller.ActiveDocument;
            if (newDoc != _peekWatchedDoc)
            {
                SubscribePeekUpdates();
                ClearThumbnailCache();
                if (IsFiguresTabActive) RefreshPeekIndex();
            }
        }
    }

    private TabViewModel? _watchedTab;

    private void WatchActiveTabPage()
    {
        if (_watchedTab is not null)
            _watchedTab.PropertyChanged -= OnTabPropertyChanged;

        _watchedTab = _vm?.ActiveTab;

        if (_watchedTab is not null)
            _watchedTab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TabViewModel.CurrentPage))
            SyncOutlineToPage();
    }

    private void SyncOutlineToPage()
    {
        if (_vm?.ActiveTab is not { } tab) return;
        var outline = tab.Outline;
        if (outline is null || outline.Count == 0) return;

        int currentPage = tab.CurrentPage;
        var best = FindEntryForPage(outline, currentPage);

        if (OutlineTree.SelectedItem == best) return;

        _suppressOutlineSelection = true;
        try
        {
            OutlineTree.SelectedItem = best;
        }
        finally
        {
            _suppressOutlineSelection = false;
        }
    }

    /// <summary>
    /// Find the outline entry whose page is closest to but not past the current page.
    /// Walks the tree depth-first, returning the last entry with Page &lt;= currentPage.
    /// </summary>
    private static OutlineEntry? FindEntryForPage(List<OutlineEntry> entries, int currentPage)
    {
        OutlineEntry? best = null;
        FindEntryForPageRecursive(entries, currentPage, ref best);
        return best;
    }

    private static void FindEntryForPageRecursive(List<OutlineEntry> entries, int currentPage, ref OutlineEntry? best)
    {
        foreach (var entry in entries)
        {
            if (entry.Page is { } p && p <= currentPage)
            {
                if (best is null || p >= best.Page!.Value)
                    best = entry;
            }
            if (entry.Children.Count > 0)
                FindEntryForPageRecursive(entry.Children, currentPage, ref best);
        }
    }

    private void UpdateOutlineSource()
    {
        OutlineTree.ItemsSource = _vm?.ActiveTab?.Outline;
    }

    public void UpdateBookmarkSource()
    {
        var bookmarks = _vm?.ActiveTab?.Annotations.Bookmarks;
        BookmarkList.ItemsSource = bookmarks is not null ? new List<BookmarkEntry>(bookmarks) : null;
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        BackButton.IsVisible = _vm?.Controller.CanGoBack == true;
    }

    public bool IsBookmarksTabActive => PaneTabs.SelectedIndex == 1;
    public bool IsFiguresTabActive => PaneTabs.SelectedIndex == 2;
    public bool IsSearchTabActive => PaneTabs.SelectedIndex == 3;
    public bool IsSearchInputFocused => IsSearchTabActive && SearchInput.IsFocused;

    public void SwitchToOutlineTab() => PaneTabs.SelectedIndex = 0;
    public void SwitchToBookmarksTab() => PaneTabs.SelectedIndex = 1;
    public void SwitchToFiguresTab() => PaneTabs.SelectedIndex = 2;

    public void SwitchToSearchTab()
    {
        PaneTabs.SelectedIndex = 3;
    }

    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    /// <summary>
    /// Called externally when search state changes (e.g. F3 next/prev match).
    /// </summary>
    public void OnSearchInvalidated() => UpdateMatchDisplay();

    private void OnClosePanelClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.ShowOutline = false;
    }

    // --- Outline events ---

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressOutlineSelection) return;
        if (_vm is { } vm && OutlineTree.SelectedItem is OutlineEntry { Page: { } page })
            vm.GoToPage(page);
    }

    // --- Bookmark events ---

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        vm.NavigateBack();
        UpdateBackButton();
    }

    private void OnBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (sender is not Button { DataContext: BookmarkEntry bm }) return;

        var bookmarks = vm.ActiveTab?.Annotations.Bookmarks;
        if (bookmarks is null) return;

        int index = bookmarks.IndexOf(bm);
        if (index < 0) return;

        vm.NavigateToBookmark(index);
        UpdateBackButton();
    }

    private async void OnAddBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || vm.ActiveTab is not { } tab) return;

        if (TopLevel.GetTopLevel(this) is not Window window) return;

        var dialog = new BookmarkNameDialog(tab.CurrentPage + 1) { FontSize = vm.CurrentFontSize };
        var name = await dialog.ShowDialog<string?>(window);
        if (name is not null)
        {
            bool added = vm.Controller.AddBookmark(name);
            UpdateBookmarkSource();
            vm.ShowStatusToast(added ? $"Bookmark: {name}" : $"Updated bookmark: {name}");
        }
    }

    private void OnDeleteBookmarkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is not { } vm) return;
        if (ResolveBookmarkIndex(sender) is not (var bm, var index)) return;

        vm.Controller.RemoveBookmark(index);
        UpdateBookmarkSource();
    }

    private async void OnRenameBookmarkClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm is not { } vm) return;
        if (ResolveBookmarkIndex(sender) is not (var bm, var index)) return;
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        var dialog = new BookmarkNameDialog(bm.Page + 1) { FontSize = vm.CurrentFontSize };
        dialog.SetName(bm.Name);
        var newName = await dialog.ShowDialog<string?>(window);
        if (newName is not null)
        {
            vm.Controller.RenameBookmark(index, newName);
            UpdateBookmarkSource();
        }
    }

    private (BookmarkEntry Entry, int Index)? ResolveBookmarkIndex(object? sender)
    {
        if (sender is not Button btn) return null;
        var bookmarks = _vm?.ActiveTab?.Annotations.Bookmarks;
        if (bookmarks is null) return null;

        BookmarkEntry? bm = null;
        for (var v = btn.GetVisualParent(); v is not null; v = v.GetVisualParent())
        {
            if (v is Button { DataContext: BookmarkEntry entry }) { bm = entry; break; }
            if (v is ItemsControl) break;
        }
        if (bm is null) return null;

        int index = bookmarks.IndexOf(bm);
        return index >= 0 ? (bm, index) : null;
    }

    private void OnPaneTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsFiguresTabActive)
        {
            SubscribePeekUpdates();
            RefreshPeekIndex();
        }
    }

    // --- Figures/Peek events ---

    private DispatcherTimer? _peekDebounceTimer;
    private DocumentState? _peekWatchedDoc;
    private readonly Dictionary<int, Bitmap?> _thumbnailCache = [];
    private readonly Dictionary<int, SKBitmap?> _pageThumbCache = [];
    private bool _peekDirty;

    private void SubscribePeekUpdates()
    {
        var doc = _vm?.Controller.ActiveDocument;
        if (doc == _peekWatchedDoc) return;

        if (_peekWatchedDoc is not null)
            _peekWatchedDoc.AnalysisCacheUpdated -= OnAnalysisCacheUpdated;

        _peekWatchedDoc = doc;

        if (_peekWatchedDoc is not null)
            _peekWatchedDoc.AnalysisCacheUpdated += OnAnalysisCacheUpdated;
    }

    private void UnsubscribePeekUpdates()
    {
        if (_peekWatchedDoc is not null)
        {
            _peekWatchedDoc.AnalysisCacheUpdated -= OnAnalysisCacheUpdated;
            _peekWatchedDoc = null;
        }
        _peekDebounceTimer?.Stop();
        ClearThumbnailCache();
    }

    private void OnAnalysisCacheUpdated()
    {
        if (!IsFiguresTabActive) return;

        _peekDirty = true;

        if (_peekDebounceTimer is null)
        {
            _peekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _peekDebounceTimer.Tick += (_, _) =>
            {
                if (_peekDirty)
                {
                    _peekDirty = false;
                    RefreshPeekIndex();
                    // Keep timer running — if more results arrive, we'll refresh again
                }
                else
                {
                    // No new results since last refresh — stop polling
                    _peekDebounceTimer.Stop();
                }
            };
        }

        // Start the timer if not already running — don't reset it
        if (!_peekDebounceTimer.IsEnabled)
            _peekDebounceTimer.Start();
    }

    private void RefreshPeekIndex()
    {
        var doc = _vm?.Controller.ActiveDocument;
        if (doc is null)
        {
            PeekEntryList.ItemsSource = null;
            PeekProgress.Text = "";
            return;
        }

        var index = PeekIndexBuilder.Build(doc.AnalysisCache, doc.PageCount);
        if (index.ScannedPages >= index.TotalPages)
            PeekProgress.Text = $"All {index.TotalPages} pages scanned";
        else if (doc.Rail.Active)
            PeekProgress.Text = $"{index.ScannedPages} of {index.TotalPages} pages scanned (paused in rail mode)";
        else
            PeekProgress.Text = $"{index.ScannedPages} of {index.TotalPages} pages scanned";

        var entries = new List<PeekEntryViewModel>();
        bool showFigures = ShowFiguresToggle.IsChecked == true;
        bool showTables = ShowTablesToggle.IsChecked == true;
        bool showEquations = ShowEquationsToggle.IsChecked == true;

        if (showFigures)
            AddEntries(entries, index.Figures, "Figure");
        if (showTables)
            AddEntries(entries, index.Tables, "Table");
        if (showEquations)
            AddEntries(entries, index.Equations, "Equation");

        // Sort by page, then reading order within page
        entries.Sort((a, b) =>
        {
            int cmp = a.Entry.PageIndex.CompareTo(b.Entry.PageIndex);
            return cmp != 0 ? cmp : a.Entry.BlockIndex.CompareTo(b.Entry.BlockIndex);
        });

        // Generate thumbnails before assigning to ItemsSource
        GenerateThumbnails(entries, doc);

        PeekEntryList.ItemsSource = entries;
    }

    private static void AddEntries(List<PeekEntryViewModel> list, IReadOnlyList<PeekEntry> entries, string category)
    {
        foreach (var entry in entries)
        {
            var className = LayoutConstants.LayoutClasses[entry.ClassId];
            list.Add(new PeekEntryViewModel
            {
                Label = category,
                PageDisplay = $"Page {entry.PageIndex + 1} \u2014 {className}",
                Entry = entry,
            });
        }
    }

    private void GenerateThumbnails(List<PeekEntryViewModel> entries, DocumentState doc)
    {
        foreach (var vm in entries)
        {
            int cacheKey = vm.Entry.PageIndex * 10000 + vm.Entry.BlockIndex;
            if (_thumbnailCache.TryGetValue(cacheKey, out var cached))
            {
                vm.Thumbnail = cached;
                continue;
            }

            var thumb = CropBlockThumbnail(doc, vm.Entry);
            _thumbnailCache[cacheKey] = thumb;
            vm.Thumbnail = thumb;
        }
    }

    /// <summary>
    /// Gets or renders a page thumbnail SKBitmap, cached per page.
    /// Avoids calling RenderThumbnail (PDFium) multiple times for the same page.
    /// </summary>
    private SKBitmap? GetPageThumb(DocumentState doc, int pageIndex)
    {
        if (_pageThumbCache.TryGetValue(pageIndex, out var cached))
            return cached;

        SKBitmap? result = null;
        try
        {
            var rendered = doc.Pdf.RenderThumbnail(pageIndex);
            if (rendered is SkiaRenderedPage skiaPage)
            {
                // Copy the bitmap so we own it (RenderThumbnail result may be disposed)
                result = skiaPage.Bitmap.Copy();
            }
            rendered?.Dispose();
        }
        catch { }

        _pageThumbCache[pageIndex] = result;
        return result;
    }

    private Bitmap? CropBlockThumbnail(DocumentState doc, PeekEntry entry)
    {
        var bitmap = GetPageThumb(doc, entry.PageIndex);
        if (bitmap is null) return null;

        try
        {
            var (pageW, pageH) = doc.Pdf.GetPageSize(entry.PageIndex);
            float scaleX = bitmap.Width / (float)pageW;
            float scaleY = bitmap.Height / (float)pageH;

            var cropRect = new SKRectI(
                Math.Max(0, (int)(entry.BBox.X * scaleX)),
                Math.Max(0, (int)(entry.BBox.Y * scaleY)),
                Math.Min(bitmap.Width, (int)((entry.BBox.X + entry.BBox.W) * scaleX)),
                Math.Min(bitmap.Height, (int)((entry.BBox.Y + entry.BBox.H) * scaleY)));

            if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

            using var cropped = new SKBitmap();
            if (!bitmap.ExtractSubset(cropped, cropRect)) return null;

            using var data = cropped.Encode(SKEncodedImageFormat.Png, 90);
            if (data is null) return null;

            using var stream = new MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void ClearThumbnailCache()
    {
        foreach (var bmp in _thumbnailCache.Values)
            bmp?.Dispose();
        _thumbnailCache.Clear();

        foreach (var bmp in _pageThumbCache.Values)
            bmp?.Dispose();
        _pageThumbCache.Clear();
    }

    private void OnPeekFilterChanged(object? sender, RoutedEventArgs e)
    {
        RefreshPeekIndex();
    }

    private void OnPeekEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Button { DataContext: PeekEntryViewModel entry }) return;
        _vm.GoToPage(entry.Entry.PageIndex);
    }

    // --- Search events ---

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_debounceTimer is null)
        {
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _debounceTimer.Tick += (_, _) =>
            {
                _debounceTimer.Stop();
                RunSearch();
            };
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnSearchOptionChanged(object? sender, RoutedEventArgs e) => RunSearch();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                OnPrevMatchClick(null, e);
                e.Handled = true;
                break;
            case Key.Enter:
                OnNextMatchClick(null, e);
                e.Handled = true;
                break;
            case Key.Escape:
                _vm?.CloseSearch();
                _vm?.InvalidateSearchLayer();
                SearchInput.Text = "";
                SearchResultsList.ItemsSource = null;
                SearchMatchCount.Text = "";
                e.Handled = true;
                break;
        }
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        _vm?.CloseSearch();
        _vm?.InvalidateSearchLayer();
        SearchInput.Text = "";
        SearchResultsList.ItemsSource = null;
        SearchMatchCount.Text = "";
    }

    private void OnPrevMatchClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.PreviousMatch();
        UpdateMatchDisplay();
    }

    private void OnNextMatchClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.NextMatch();
        UpdateMatchDisplay();
    }

    private void OnSearchResultClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is Button { Tag: int matchIndex })
        {
            _vm.GoToMatch(matchIndex);
            UpdateMatchDisplay();
        }
    }

    private async void RunSearch()
    {
        if (_vm is null) return;
        string query = SearchInput.Text ?? "";

        // Cancel and dispose any in-progress search
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // Require minimum 2 characters (or any length for regex)
        bool useRegex = RegexToggle.IsChecked == true;
        if (query.Length < 2 && !useRegex)
        {
            _vm.CloseSearch();
            SearchResultsList.ItemsSource = null;
            SearchMatchCount.Text = query.Length > 0 ? "Type to search..." : "";
            return;
        }

        bool caseSensitive = CaseSensitiveToggle.IsChecked == true;
        var doc = _vm.Controller.ActiveDocument;
        if (doc is null) return;

        var (regex, comparison, regexError) = SearchService.PrepareSearchParams(query, caseSensitive, useRegex);
        if (useRegex && regex is null)
        {
            SearchMatchCount.Text = regexError ?? "Invalid regex";
            SearchResultsList.ItemsSource = null;
            return;
        }

        // Clear previous results
        _vm.Controller.Search.CloseSearch();
        _vm.InvalidateSearchLayer();
        SearchMatchCount.Text = "Searching...";

        var allMatches = new List<SearchMatch>();
        const int batchSize = 20;

        for (int page = 0; page < doc.PageCount; page++)
        {
            if (token.IsCancellationRequested) return;

            SearchService.SearchPage(doc, page, query, regex, comparison, allMatches);

            // Yield to UI every batch to stay responsive
            if (page % batchSize == batchSize - 1)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        if (token.IsCancellationRequested) return;

        _vm.Controller.Search.FinalizeSearch(doc, allMatches);
        _vm.InvalidateSearchLayer();
        BuildResultGroups();
        UpdateMatchDisplay();
    }

    private void BuildResultGroups()
    {
        if (_vm is null) { SearchResultsList.ItemsSource = null; return; }

        var matches = _vm.SearchMatches;
        if (matches.Count == 0)
        {
            SearchResultsList.ItemsSource = null;
            return;
        }

        var groups = new List<SearchResultGroup>();
        var currentGroup = (SearchResultGroup?)null;

        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (currentGroup is null || currentGroup.PageIndex != m.PageIndex)
            {
                currentGroup = new SearchResultGroup
                {
                    PageHeader = $"Page {m.PageIndex + 1}",
                    PageIndex = m.PageIndex,
                    Items = [],
                };
                groups.Add(currentGroup);
            }

            var (pre, match, post) = _vm.Controller.Search.GetMatchSnippet(m);
            currentGroup.Items.Add(new SearchResultItem
            {
                MatchIndex = i,
                PreText = pre,
                MatchText = match,
                PostText = post,
            });
        }

        SearchResultsList.ItemsSource = groups;
    }

    private void UpdateMatchDisplay()
    {
        if (_vm is null) return;
        int total = _vm.SearchMatches.Count;
        int current = total > 0 ? _vm.ActiveMatchIndex + 1 : 0;
        SearchMatchCount.Text = total > 0 ? $"{current} of {total}" : "";
    }
}
