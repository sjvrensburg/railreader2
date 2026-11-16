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
    // True while a search's first match is shown but the rest of the document is still being searched:
    // the counter says "Searching…" rather than a count of the partial list (#301).
    private bool _filling;
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
        _filling = false;
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

        _filling = false;
        using var searchWork = _vm.BeginSearchWork();
        // A library hit's page (null for the reader's own search). Read now: typing a new query clears it.
        int? hitPage = _pendingStartPage;
        _pendingStartPage = null;
        int pageCount = doc.PageCount;
        // Search from the hit's page (or the page the reader is on) forwards, then wrap: the match Core
        // activates — the first on or after that page — is then found first, and shown as soon as it is
        // (#301), instead of after the whole document. The rest is installed without moving the view.
        int first = Math.Clamp(hitPage ?? _vm.Controller.FocusedViewport?.CurrentPage ?? 0, 0, Math.Max(0, pageCount - 1));
        var perPage = new List<SearchMatch>?[pageCount];
        bool shown = false;
        int found = 0;
        const int batchSize = 20;
        var total = SearchTiming ? System.Diagnostics.Stopwatch.StartNew() : null;
        var inSearch = new System.Diagnostics.Stopwatch();
        var inYield = new System.Diagnostics.Stopwatch();
        var pageMs = SearchTiming ? new List<double>(pageCount) : null;
        long firstShownMs = -1;

        for (int k = 0; k < pageCount; k++)
        {
            if (token.IsCancellationRequested) return;
            int page = (first + k) % pageCount;

            var pageMatches = new List<SearchMatch>();
            long before = inSearch.ElapsedTicks;
            inSearch.Start();
            SearchService.SearchPage(doc, page, query, regex, comparison, pageMatches);
            inSearch.Stop();
            pageMs?.Add((inSearch.ElapsedTicks - before) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (pageMatches.Count > 0) { perPage[page] = pageMatches; found += pageMatches.Count; }

            // First match found: show it now. For a library hit, only one on or after its page — a wrapped
            // match would jump to the start of the document and then back to the hit's page at the end.
            if (!shown && pageMatches.Count > 0 && (hitPage is null || page >= first))
            {
                shown = true;
                _vm.Controller.Search.FinalizeSearch(doc, Flatten(perPage), first);
                _vm.InvalidateAfterSearch(); // it navigates: page bitmap + camera + overlays
                _filling = true;
                SearchMatchCount.Text = "Searching\u2026";
                firstShownMs = total?.ElapsedMilliseconds ?? -1;
            }

            // Yield to UI every batch to stay responsive
            if (k % batchSize == batchSize - 1)
            {
                inYield.Start();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                inYield.Stop();
            }
        }
        if (total is not null && pageMs is { Count: > 0 })
            RailReader.Core.RailReaderLogging.Logger.Info(
                $"[Search] {pageCount} pages, {found} matches: total {total.ElapsedMilliseconds} ms, " +
                $"first shown {firstShownMs} ms, SearchPage {inSearch.ElapsedMilliseconds} ms, yields {inYield.ElapsedMilliseconds} ms; per page: " +
                $"median {pageMs.Order().ElementAt(pageMs.Count / 2):F1} ms, max {pageMs.Max():F0} ms, " +
                $"{pageMs.Count(t => t > 50)} pages > 50 ms ({pageMs.Where(t => t > 50).Sum():F0} ms)");

        if (token.IsCancellationRequested) return;

        var allMatches = Flatten(perPage);
        _filling = false;
        if (shown)
        {
            // Core 0.89.0: the full list, keeping the active match, without pulling a reader who has moved
            // on since the first match was shown back to it.
            _vm.Controller.Search.UpdateMatches(doc, allMatches);
            _vm.InvalidateSearchLayer();
        }
        else
        {
            // Nothing shown yet: no matches at all, or a library hit with none on or after its page. Core
            // then wraps to the document's first match (the highlight pattern didn't match the PDF's own
            // text on the hit's page): show the hit's page instead.
            _vm.Controller.Search.FinalizeSearch(doc, allMatches, first);
            _vm.InvalidateAfterSearch();
            if (hitPage is int p)
                _vm.GoToPage(p);
        }
        BuildResultGroups();
        UpdateMatchDisplay();
        ScrollToActiveMatch(); // a library hit's match may be hundreds of results down (#300)
    }

    /// <summary>The matches found so far, in page order (Core needs a page-ordered list).</summary>
    private static List<SearchMatch> Flatten(List<SearchMatch>?[] perPage)
    {
        var all = new List<SearchMatch>();
        foreach (var list in perPage)
            if (list is not null) all.AddRange(list);
        return all;
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
        if (_filling) { SearchMatchCount.Text = "Searching\u2026"; return; }
        int total = _vm.SearchMatches.Count;
        if (total == 0) { SearchMatchCount.Text = ""; return; }
        // A block-confined (portal) view can have matches but none reachable → ActiveMatchIndex == -1.
        // Guard on Core's HasActiveMatch so the 1-based counter never reads "0 of N" (RailReaderCore 0.45.2).
        SearchMatchCount.Text = _vm.HasActiveSearchMatch
            ? $"{_vm.ActiveMatchIndex + 1} of {total}"
            : $"0 of {total} reachable";
    }
}
