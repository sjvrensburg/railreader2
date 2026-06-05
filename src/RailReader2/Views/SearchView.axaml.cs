using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RailReader.Core.Models;
using RailReader.Core.Services;
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

/// <summary>
/// Search pane — incremental full-document text search with case/regex toggles, a grouped
/// results list, and match navigation. Self-contained: wires its ViewModel subscriptions on
/// load and tears them down on unload. Reports its input focus to the ViewModel so the
/// window key handler can let text keys through, and consumes the pending search activation
/// (prefill + focus) the ViewModel records when Ctrl+F / menu search is invoked.
/// </summary>
public partial class SearchView : UserControl
{
    private MainWindowViewModel? _vm;
    private DispatcherTimer? _debounceTimer;
    private CancellationTokenSource? _searchCts;

    public SearchView()
    {
        InitializeComponent();

        SearchInput.TextChanged += OnSearchTextChanged;
        SearchInput.KeyDown += OnSearchKeyDown;
        SearchInput.GotFocus += OnSearchInputGotFocus;
        SearchInput.LostFocus += OnSearchInputLostFocus;
        CaseSensitiveToggle.IsCheckedChanged += OnSearchOptionChanged;
        RegexToggle.IsCheckedChanged += OnSearchOptionChanged;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Attach(DataContext as MainWindowViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Detach();
        base.OnUnloaded(e);
    }

    private void Attach(MainWindowViewModel? vm)
    {
        Detach();
        _vm = vm;
        if (_vm is null) return;

        _vm.SearchInvalidated += UpdateMatchDisplay;
        _vm.SearchActivated += HandleSearchActivation;
        _vm.SearchCleared += ClearSearchUi;
        UpdateMatchDisplay();
        HandleSearchActivation();
    }

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.IsSearchInputFocused = false;
            _vm.SearchInvalidated -= UpdateMatchDisplay;
            _vm.SearchActivated -= HandleSearchActivation;
            _vm.SearchCleared -= ClearSearchUi;
            _vm = null;
        }
        _debounceTimer?.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }

    /// <summary>Apply any pending search activation recorded by the ViewModel (prefill text
    /// and/or focus request). One-shot — consumed so a manual tab switch later doesn't refocus.</summary>
    private void HandleSearchActivation()
    {
        if (_vm is null) return;
        var (prefill, focus) = _vm.ConsumeSearchActivation();
        if (prefill is not null)
            SearchInput.Text = prefill;
        if (focus)
            Dispatcher.UIThread.Post(FocusSearch, DispatcherPriority.Input);
    }

    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnSearchInputGotFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.IsSearchInputFocused = true;
    }

    private void OnSearchInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.IsSearchInputFocused = false;
    }

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
                _vm?.ClearSearch();
                e.Handled = true;
                break;
        }
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        _vm?.ClearSearch();
    }

    /// <summary>Reset the input box and results list. Invoked locally and via the ViewModel's
    /// <see cref="MainWindowViewModel.SearchCleared"/> (so window-level Escape clears the box too).</summary>
    private void ClearSearchUi()
    {
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
            _vm.RequestViewportFocus();
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
        // FinalizeSearch auto-navigates the document to the first match's page, so we
        // need a full invalidation (page bitmap + camera + overlays), not just the
        // search layer — otherwise the new page's highlights paint over the old page.
        _vm.InvalidateAfterSearch();
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
