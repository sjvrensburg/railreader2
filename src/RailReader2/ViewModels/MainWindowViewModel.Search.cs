namespace RailReader2.ViewModels;

// Search: find, match navigation
public sealed partial class MainWindowViewModel
{
    // Pending activation state, consumed by the Search pane on load / on SearchActivated.
    // Held as VM state (not just an event arg) so the request survives until a lazily
    // loaded Search pane attaches — the pane may not exist yet when Ctrl+F is pressed.
    private string? _pendingSearchPrefill;
    private bool _searchFocusPending;
    private bool? _pendingSearchRegex;
    private int? _pendingSearchStartPage;

    /// <summary>Raised when search is requested (Ctrl+F / menu / search-selection) so an
    /// already-loaded Search pane applies the pending prefill/focus immediately.</summary>
    public event Action? SearchActivated;

    /// <summary>A pending search activation: the prefill text to apply (null = leave the box unchanged),
    /// whether to focus the input, the regex toggle to set (null = leave it), and — for a library hit —
    /// the page whose first match to show once the search has run (null = the usual first match).</summary>
    public readonly record struct SearchActivation(string? Prefill, bool Focus, bool? UseRegex, int? StartPage);

    /// <summary>Consume the pending search activation. One-shot.</summary>
    public SearchActivation ConsumeSearchActivation()
    {
        var result = new SearchActivation(_pendingSearchPrefill, _searchFocusPending, _pendingSearchRegex, _pendingSearchStartPage);
        _pendingSearchPrefill = null;
        _searchFocusPending = false;
        _pendingSearchRegex = null;
        _pendingSearchStartPage = null;
        return result;
    }

    public void OpenSearch() => RequestSearch(null);

    public void SearchForSelectedText()
    {
        if (SelectedText?.Trim() is not { Length: > 0 } text) return;
        RequestSearch(text);
    }

    private void RequestSearch(string? prefill, bool focus = true, bool? useRegex = null, int? startPage = null)
    {
        _pendingSearchPrefill = prefill;
        _searchFocusPending = focus;
        _pendingSearchRegex = useRegex;
        _pendingSearchStartPage = startPage;
        ActivePane = SidePane.Search;
        ShowOutline = true;
        SearchActivated?.Invoke();
    }

    public void CloseSearch()
    {
        _controller.Search.CloseSearch();
        InvalidateSearch();
    }

    /// <summary>Raised when search is cleared from outside the Search pane (e.g. the window-level
    /// Escape handler), so the pane resets its input box and results list.</summary>
    public event Action? SearchCleared;

    /// <summary>Close the search and clear the Search pane's input/results — used by entry points
    /// that don't own the search box (so the query doesn't linger stale for next time).</summary>
    public void ClearSearch()
    {
        CloseSearch();
        SearchCleared?.Invoke();
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        if (IsScanAllActive) return;
        _controller.Search.ExecuteSearch(query, caseSensitive, useRegex);
        InvalidateSearch();
    }

    public void InvalidateSearchLayer() => InvalidateSearch();

    /// <summary>
    /// Full invalidation after a search completes. FinalizeSearch auto-navigates the
    /// document to the first match's page (via SearchService.NavigateToActiveMatch),
    /// so the page bitmap, camera and search overlay must all refresh together — just
    /// like NextMatch/GoToMatch do through InvalidateAfterNavigation. Doing only
    /// InvalidateSearch here updated the highlight layer to the new page's rects while
    /// the page image still showed the page the user was on, so (e.g.) page 7's
    /// highlights were painted over page 1.
    /// </summary>
    public void InvalidateAfterSearch() => InvalidateAfterNavigation();

    /// <summary>
    /// Recomputes which search matches belong to the currently displayed page.
    /// SearchService caches this (CurrentPageSearchMatches) and only refreshes it on
    /// match navigation — NOT on scroll/rail page changes. BuildSearchState calls this
    /// before reading the cache so the highlight rects always track the page on screen.
    /// </summary>
    public void RefreshCurrentPageSearchMatches()
        => _controller.Search.UpdateCurrentPageMatches();

    /// <summary>Raised when the active match moves (next/previous from any entry point: the pane, the
    /// search bar, F3/the menu, or a click on a result), so the Search pane can scroll its list to it
    /// (#300). Not raised on page changes or redraws, unlike <see cref="SearchInvalidated"/>.</summary>
    public event Action? ActiveSearchMatchChanged;

    public void NextMatch()
    {
        if (IsScanAllActive) return;
        _controller.Search.NextMatch();
        InvalidateAfterNavigation();
        ActiveSearchMatchChanged?.Invoke();
    }

    public void PreviousMatch()
    {
        if (IsScanAllActive) return;
        _controller.Search.PreviousMatch();
        InvalidateAfterNavigation();
        ActiveSearchMatchChanged?.Invoke();
    }

    public void GoToMatch(int matchIndex)
    {
        if (IsScanAllActive) return;
        _controller.Search.GoToMatch(matchIndex);
        InvalidateAfterNavigation();
        ActiveSearchMatchChanged?.Invoke();
    }
}
