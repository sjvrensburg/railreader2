using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private bool _applyingActivation;
    private int? _pendingStartPage; // a library hit's page: show its first match once the search has run
    // The reader's own Regex / Case toggles, saved when a library hit set them; restored on their next search.
    private (bool? Regex, bool? Case)? _readerToggles;
    // The text an activation put in the box. TextBox can raise TextChanged later than the assignment (a
    // freshly created pane does), after _applyingActivation is cleared, so a change to exactly this text is
    // the activation's own echo, not the reader typing.
    private string? _activationText;

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
        _vm.ActiveSearchMatchChanged += OnActiveMatchChanged;
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
            _vm.ActiveSearchMatchChanged -= OnActiveMatchChanged;
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
        var (prefill, focus, useRegex, startPage) = _vm.ConsumeSearchActivation();
        // Ctrl+F / search-selection: the reader's own search. Not on a bare re-attach (nothing pending),
        // which would flip the toggles under the library hit's pattern still in the box and its results.
        if (useRegex is null && (prefill is not null || focus)) RestoreReaderToggles();
        if (startPage is not null || useRegex is not null)
        {
            _readerToggles ??= (RegexToggle.IsChecked, CaseSensitiveToggle.IsChecked);
            // Set the box and toggles without each change starting its own search, then run once.
            _applyingActivation = true;
            try
            {
                if (useRegex is { } rx) { RegexToggle.IsChecked = rx; CaseSensitiveToggle.IsChecked = false; }
                if (prefill is not null) SearchInput.Text = _activationText = prefill;
            }
            finally { _applyingActivation = false; }
            _debounceTimer?.Stop();
            _pendingStartPage = startPage;
            RunSearch();
        }
        else if (prefill is not null)
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
        if (_applyingActivation) return;
        if (_activationText is not null)
        {
            if (SearchInput.Text == _activationText) return; // the activation's own (late) TextChanged
            _activationText = null;
        }
        _pendingStartPage = null; // the reader typed: their own search, not the library hit's
        RestoreReaderToggles();
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

    private void OnSearchOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (!_applyingActivation) RunSearch();
    }

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

    // Carries the query over to the Library window (design §7.1); a regex is not a library query.
    private void OnSearchLibraryClick(object? sender, RoutedEventArgs e)
        => _vm?.ShowLibrary(RegexToggle.IsChecked == true ? null : SearchInput.Text);

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        _vm?.ClearSearch();
    }

    /// <summary>Reset the input box and results list. Invoked locally and via the ViewModel's
    /// <see cref="MainWindowViewModel.SearchCleared"/> (so window-level Escape clears the box too).</summary>
    private void ClearSearchUi()
    {
        RestoreReaderToggles();
        SearchInput.Text = "";
        SearchResultsList.ItemsSource = null;
        SearchMatchCount.Text = "";
    }

    private void RestoreReaderToggles()
    {
        if (_readerToggles is not { } saved) return;
        _readerToggles = null;
        _applyingActivation = true; // no search of its own: the caller's change runs one
        try
        {
            RegexToggle.IsChecked = saved.Regex;
            CaseSensitiveToggle.IsChecked = saved.Case;
        }
        finally { _applyingActivation = false; }
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
        try
        {
            await RunSearchAsync();
        }
        catch (Exception ex)
        {
            RailReader.Core.RailReaderLogging.Logger.Error("[Search] RunSearch failed", ex);
        }
    }

    // Diagnostic (opt-in, read once): RR_SEARCH_TIMING=1 logs where a search's time goes to session.log.
    private static readonly bool SearchTiming = Environment.GetEnvironmentVariable("RR_SEARCH_TIMING") == "1";

    private async Task RunSearchAsync()
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
        var doc = _vm.Controller.FocusedViewport?.Owner;
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

        using var searchWork = _vm.BeginSearchWork();
        var allMatches = new List<SearchMatch>();
        const int batchSize = 20;
        var total = SearchTiming ? System.Diagnostics.Stopwatch.StartNew() : null;
        var inSearch = new System.Diagnostics.Stopwatch();
        var inYield = new System.Diagnostics.Stopwatch();
        var pageMs = SearchTiming ? new List<double>(doc.PageCount) : null;

        for (int page = 0; page < doc.PageCount; page++)
        {
            if (token.IsCancellationRequested) return;

            long before = inSearch.ElapsedTicks;
            inSearch.Start();
            SearchService.SearchPage(doc, page, query, regex, comparison, allMatches);
            inSearch.Stop();
            pageMs?.Add((inSearch.ElapsedTicks - before) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            // Yield to UI every batch to stay responsive
            if (page % batchSize == batchSize - 1)
            {
                inYield.Start();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                inYield.Stop();
            }
        }
        if (total is not null && pageMs is { Count: > 0 })
            RailReader.Core.RailReaderLogging.Logger.Info(
                $"[Search] {doc.PageCount} pages, {allMatches.Count} matches: total {total.ElapsedMilliseconds} ms, " +
                $"SearchPage {inSearch.ElapsedMilliseconds} ms, yields {inYield.ElapsedMilliseconds} ms; per page: " +
                $"median {pageMs.Order().ElementAt(pageMs.Count / 2):F1} ms, max {pageMs.Max():F0} ms, " +
                $"{pageMs.Count(t => t > 50)} pages > 50 ms ({pageMs.Where(t => t > 50).Sum():F0} ms)");

        if (token.IsCancellationRequested) return;

        // Opened from a library hit: Core activates the first match on or after the hit's page
        // (not the one after the view's current page, which hasn't moved there yet).
        int? startPage = _pendingStartPage;
        _pendingStartPage = null;
        _vm.Controller.Search.FinalizeSearch(doc, allMatches, startPage);
        // FinalizeSearch auto-navigates the document to the first match's page, so we
        // need a full invalidation (page bitmap + camera + overlays), not just the
        // search layer — otherwise the new page's highlights paint over the old page.
        _vm.InvalidateAfterSearch();
        // Core wraps to the document's first match when none is on or after the hit's page (the
        // highlight pattern didn't match the PDF's own text there): show the hit's page instead.
        if (startPage is int hitPage && !allMatches.Any(m => m.PageIndex >= hitPage))
            _vm.GoToPage(hitPage);
        BuildResultGroups();
        UpdateMatchDisplay();
        ScrollToActiveMatch(); // a library hit's match may be hundreds of results down (#300)
    }

    private void OnActiveMatchChanged()
    {
        UpdateMatchDisplay();
        ScrollToActiveMatch();
    }

    /// <summary>Scrolls the result list so the active match's row is visible (#300). Runs after layout:
    /// right after a search the rows don't exist yet.</summary>
    private void ScrollToActiveMatch()
    {
        if (_vm is null || !_vm.HasActiveSearchMatch) return;
        int active = _vm.ActiveMatchIndex;
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null || _vm.ActiveMatchIndex != active
                || SearchResultsList.ItemsSource is not IReadOnlyList<SearchResultGroup> groups) return;
            for (int g = 0; g < groups.Count; g++)
            {
                int i = groups[g].Items.FindIndex(it => it.MatchIndex == active);
                if (i < 0) continue;
                if (SearchResultsList.ContainerFromIndex(g) is not Control groupContainer) return;
                var inner = groupContainer.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault();
                (inner?.ContainerFromIndex(i) as Control ?? groupContainer).BringIntoView();
                return;
            }
        }, DispatcherPriority.Loaded);
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
        if (total == 0) { SearchMatchCount.Text = ""; return; }
        // A block-confined (portal) view can have matches but none reachable → ActiveMatchIndex == -1.
        // Guard on Core's HasActiveMatch so the 1-based counter never reads "0 of N" (RailReaderCore 0.45.2).
        SearchMatchCount.Text = _vm.HasActiveSearchMatch
            ? $"{_vm.ActiveMatchIndex + 1} of {total}"
            : $"0 of {total} reachable";
    }
}
