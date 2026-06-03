namespace RailReader2.ViewModels;

// Search: find, match navigation
public sealed partial class MainWindowViewModel
{
    public event Action<string?>? SearchRequested;

    public void OpenSearch() => RequestSearch(null);

    public void SearchForSelectedText()
    {
        if (SelectedText?.Trim() is not { Length: > 0 } text) return;
        RequestSearch(text);
    }

    private void RequestSearch(string? prefill)
    {
        ShowOutline = true;
        SearchRequested?.Invoke(prefill);
    }

    public void CloseSearch()
    {
        _controller.Search.CloseSearch();
        InvalidateSearch();
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
