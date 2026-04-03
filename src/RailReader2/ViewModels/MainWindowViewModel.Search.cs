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
        _controller.Search.ExecuteSearch(query, caseSensitive, useRegex);
        InvalidateSearch();
    }

    public void InvalidateSearchLayer() => InvalidateSearch();

    public void NextMatch()
    {
        _controller.Search.NextMatch();
        InvalidateAfterNavigation();
    }

    public void PreviousMatch()
    {
        _controller.Search.PreviousMatch();
        InvalidateAfterNavigation();
    }

    public void GoToMatch(int matchIndex)
    {
        _controller.Search.GoToMatch(matchIndex);
        InvalidateAfterNavigation();
    }

    public void UpdateCurrentPageMatches() => _controller.Search.UpdateCurrentPageMatches();
}
