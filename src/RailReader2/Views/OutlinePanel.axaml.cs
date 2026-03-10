using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader2.ViewModels;

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

public partial class OutlinePanel : UserControl
{
    private MainWindowViewModel? _vm;
    private DispatcherTimer? _debounceTimer;
    private CancellationTokenSource? _searchCts;

    public OutlinePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        SearchInput.TextChanged += OnSearchTextChanged;
        SearchInput.KeyDown += OnSearchKeyDown;
        CaseSensitiveToggle.IsCheckedChanged += OnSearchOptionChanged;
        RegexToggle.IsCheckedChanged += OnSearchOptionChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.SearchRequested -= OnSearchRequested;
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.SearchRequested += OnSearchRequested;
            UpdateOutlineSource();
            UpdateBookmarkSource();
        }
    }

    private void OnSearchRequested()
    {
        SwitchToSearchTab();
        // Delay focus so the tab switch completes first
        Dispatcher.UIThread.Post(FocusSearch, DispatcherPriority.Input);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
        {
            UpdateOutlineSource();
            UpdateBookmarkSource();
        }
    }

    private void UpdateOutlineSource()
    {
        OutlineTree.ItemsSource = _vm?.ActiveTab?.Outline;
    }

    public void UpdateBookmarkSource()
    {
        var bookmarks = _vm?.ActiveTab?.Annotations?.Bookmarks;
        BookmarkList.ItemsSource = bookmarks is not null ? new List<BookmarkEntry>(bookmarks) : null;
        UpdateBackButton();
    }

    private void UpdateBackButton()
    {
        BackButton.IsVisible = _vm?.Controller.LastPositionPage >= 0;
    }

    public bool IsBookmarksTabActive => PaneTabs.SelectedIndex == 1;
    public bool IsSearchTabActive => PaneTabs.SelectedIndex == 2;
    public bool IsSearchInputFocused => IsSearchTabActive && SearchInput.IsFocused;

    public void SwitchToOutlineTab() => PaneTabs.SelectedIndex = 0;
    public void SwitchToBookmarksTab() => PaneTabs.SelectedIndex = 1;

    public void SwitchToSearchTab()
    {
        PaneTabs.SelectedIndex = 2;
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

        var bookmarks = vm.ActiveTab?.Annotations?.Bookmarks;
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
        var bookmarks = _vm?.ActiveTab?.Annotations?.Bookmarks;
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

        // Cancel any in-progress search
        _searchCts?.Cancel();
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

        var (regex, comparison) = DocumentController.PrepareSearchParams(query, caseSensitive, useRegex);
        if (useRegex && regex is null) return;

        // Clear previous results
        _vm.Controller.CloseSearch();
        _vm.InvalidateSearchLayer();
        SearchMatchCount.Text = "Searching...";

        var allMatches = new List<SearchMatch>();
        const int batchSize = 20;

        for (int page = 0; page < doc.PageCount; page++)
        {
            if (token.IsCancellationRequested) return;

            DocumentController.SearchPage(doc, page, query, regex, comparison, allMatches);

            // Yield to UI every batch to stay responsive
            if (page % batchSize == batchSize - 1)
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        if (token.IsCancellationRequested) return;

        _vm.Controller.FinalizeSearch(doc, allMatches);
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

            var (pre, match, post) = _vm.Controller.GetMatchSnippet(m);
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
