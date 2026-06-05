namespace RailReader2.ViewModels;

// Search: find, match navigation
public sealed partial class MainWindowViewModel
{
    // Pending activation state, consumed by the Search pane on load / on SearchActivated.
    // Held as VM state (not just an event arg) so the request survives until a lazily
    // loaded Search pane attaches — the pane may not exist yet when Ctrl+F is pressed.
    private string? _pendingSearchPrefill;
    private bool _searchFocusPending;

    /// <summary>Raised when search is requested (Ctrl+F / menu / search-selection) so an
    /// already-loaded Search pane applies the pending prefill/focus immediately.</summary>
    public event Action? SearchActivated;

    /// <summary>Consume the pending search activation: the prefill text to apply (null = leave
    /// the box unchanged) and whether the input should be focused. One-shot.</summary>
    public (string? Prefill, bool Focus) ConsumeSearchActivation()
    {
        var result = (_pendingSearchPrefill, _searchFocusPending);
        _pendingSearchPrefill = null;
        _searchFocusPending = false;
        return result;
    }

    public void OpenSearch() => RequestSearch(null);

    public void SearchForSelectedText()
    {
        if (SelectedText?.Trim() is not { Length: > 0 } text) return;
        RequestSearch(text);
    }

    private void RequestSearch(string? prefill)
    {
        _pendingSearchPrefill = prefill;
        _searchFocusPending = true;
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

    public void NextMatch()
    {
        if (IsScanAllActive) return;
        _controller.Search.NextMatch();
        InvalidateAfterNavigation();
    }

    public void PreviousMatch()
    {
        if (IsScanAllActive) return;
        _controller.Search.PreviousMatch();
        InvalidateAfterNavigation();
    }

    public void GoToMatch(int matchIndex)
    {
        if (IsScanAllActive) return;
        _controller.Search.GoToMatch(matchIndex);
        InvalidateAfterNavigation();
    }
}
