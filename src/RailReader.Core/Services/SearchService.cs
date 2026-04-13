using System.Text.RegularExpressions;
using RailReader.Core.Commands;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Full-text search over the active document.
///
/// <b>Thread-safety:</b> single-threaded. All public members (ExecuteSearch,
/// CloseSearch, NextMatch, PreviousMatch, SearchMatches, CurrentPageSearchMatches,
/// ActiveMatchIndex) must be called from the UI thread. Internal state
/// (<c>SearchMatches</c>, <c>_searchMatchesByPage</c>, <c>CurrentPageSearchMatches</c>)
/// is mutated without synchronisation; concurrent access from background threads
/// will race. Background work (e.g. a future async search) must marshal back to
/// the UI thread before touching this service.
/// </summary>
public sealed class SearchService
{
    private readonly Func<DocumentState?> _getActiveDocument;
    private readonly Func<(double Width, double Height)> _getViewportSize;
    private readonly Action<int> _goToPage;

    public SearchService(
        Func<DocumentState?> getActiveDocument,
        Func<(double Width, double Height)> getViewportSize,
        Action<int> goToPage)
    {
        _getActiveDocument = getActiveDocument;
        _getViewportSize = getViewportSize;
        _goToPage = goToPage;
    }

    public List<SearchMatch> SearchMatches { get; private set; } = [];
    private Dictionary<int, List<SearchMatch>> _searchMatchesByPage = [];
    public List<SearchMatch>? CurrentPageSearchMatches { get; private set; }
    public int ActiveMatchIndex { get; set; }

    public void CloseSearch()
    {
        SearchMatches = [];
        _searchMatchesByPage = [];
        CurrentPageSearchMatches = null;
        ActiveMatchIndex = 0;
    }

    public void ExecuteSearch(string query, bool caseSensitive, bool useRegex)
    {
        CloseSearch();

        if (string.IsNullOrEmpty(query) || _getActiveDocument() is not { } doc)
            return;

        var (regex, comparison, _) = PrepareSearchParams(query, caseSensitive, useRegex);
        if (useRegex && regex is null) return; // invalid regex — caller shows error via RegexError

        var allMatches = new List<SearchMatch>();
        for (int page = 0; page < doc.PageCount; page++)
            SearchPage(doc, page, query, regex, comparison, allMatches);

        FinalizeSearch(doc, allMatches);
    }

    /// <summary>
    /// Prepares search parameters. Returns null regex and an error message for invalid regex patterns.
    /// </summary>
    public static (Regex? Regex, StringComparison Comparison, string? RegexError) PrepareSearchParams(
        string query, bool caseSensitive, bool useRegex)
    {
        Regex? regex = null;
        string? regexError = null;
        if (useRegex)
        {
            try
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options);
            }
            catch (RegexParseException ex) { regexError = ex.Message; }
        }
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return (regex, comparison, regexError);
    }

    /// <summary>
    /// Searches a single page and appends matches to the list.
    /// Uses PDFium's FPDFText_CountRects/GetRect for accurate highlight positioning.
    /// </summary>
    public static void SearchPage(DocumentState doc, int page, string query,
        Regex? regex, StringComparison comparison, List<SearchMatch> results)
    {
        var pageText = doc.GetOrExtractText(page);
        if (string.IsNullOrEmpty(pageText.Text)) return;

        IEnumerable<(int Index, int Length)> hits;
        if (regex is not null)
            hits = regex.Matches(pageText.Text).Select(m => (m.Index, m.Length));
        else
            hits = FindAllOccurrences(pageText.Text, query, comparison);

        // Collect all hits first so we can batch the PDFium rect query
        var hitList = hits.ToList();
        if (hitList.Count == 0) return;

        var allRects = doc.PdfText.GetTextRangeRects(doc.Pdf.PdfBytes, page, hitList);

        for (int i = 0; i < hitList.Count; i++)
        {
            var rects = allRects[i];
            if (rects.Count > 0)
                results.Add(new SearchMatch(page, hitList[i].Index, hitList[i].Length, rects));
        }
    }

    /// <summary>
    /// Finalizes search results: sets active match, navigates, updates current page matches.
    /// </summary>
    public void FinalizeSearch(DocumentState doc, List<SearchMatch> allMatches)
    {
        SearchMatches = allMatches;
        var byPage = new Dictionary<int, List<SearchMatch>>();
        foreach (var m in allMatches)
        {
            if (!byPage.TryGetValue(m.PageIndex, out var list))
            {
                list = [];
                byPage[m.PageIndex] = list;
            }
            list.Add(m);
        }
        _searchMatchesByPage = byPage;
        if (allMatches.Count > 0)
        {
            int firstOnCurrentOrAfter = allMatches.FindIndex(m => m.PageIndex >= doc.CurrentPage);
            ActiveMatchIndex = firstOnCurrentOrAfter >= 0 ? firstOnCurrentOrAfter : 0;
            NavigateToActiveMatch();
        }
        UpdateCurrentPageMatches();
    }

    public void NextMatch()
    {
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex + 1) % SearchMatches.Count;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public void PreviousMatch()
    {
        if (SearchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex - 1 + SearchMatches.Count) % SearchMatches.Count;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public void GoToMatch(int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= SearchMatches.Count) return;
        ActiveMatchIndex = matchIndex;
        NavigateToActiveMatch();
        UpdateCurrentPageMatches();
    }

    public (string Pre, string Match, string Post) GetMatchSnippet(SearchMatch match, int contextChars = 40)
    {
        var text = _getActiveDocument()?.GetOrExtractText(match.PageIndex).Text;
        if (text is null) return ("", "", "");

        int start = Math.Max(0, match.CharStart - contextChars);
        int end = Math.Min(text.Length, match.CharStart + match.CharLength + contextChars);
        int matchEnd = Math.Min(match.CharStart + match.CharLength, text.Length);

        string pre = (start > 0 ? "\u2026" : "") + text[start..match.CharStart];
        string matchStr = text[match.CharStart..matchEnd];
        string post = text[matchEnd..end] + (end < text.Length ? "\u2026" : "");

        // Clean up whitespace for display
        pre = pre.Replace('\n', ' ').Replace('\r', ' ');
        matchStr = matchStr.Replace('\n', ' ').Replace('\r', ' ');
        post = post.Replace('\n', ' ').Replace('\r', ' ');

        return (pre, matchStr, post);
    }

    private void NavigateToActiveMatch()
    {
        if (_getActiveDocument() is not { } doc) return;
        if (ActiveMatchIndex < 0 || ActiveMatchIndex >= SearchMatches.Count) return;
        var match = SearchMatches[ActiveMatchIndex];
        if (match.PageIndex != doc.CurrentPage)
            _goToPage(match.PageIndex);

        if (doc.Rail.Active && doc.Rail.HasAnalysis && match.Rects.Count > 0)
        {
            // Set rail to the block/line containing the match, then snap
            // horizontally to center the match rather than the block start
            var rect = match.Rects[0];
            double matchCenterX = (rect.Left + rect.Right) / 2.0;
            double matchCenterY = (rect.Top + rect.Bottom) / 2.0;
            doc.Rail.FindBlockNearPoint(matchCenterX, matchCenterY);
            var (ww, wh) = _getViewportSize();
            doc.Rail.StartSnapToPoint(doc.Camera.OffsetX, doc.Camera.OffsetY,
                doc.Camera.Zoom, ww, wh, matchCenterX);
        }
        else
        {
            ScrollToMatchRect(doc, match);
        }
    }

    private void ScrollToMatchRect(DocumentState doc, SearchMatch match)
    {
        if (match.Rects.Count == 0) return;
        var (ww, wh) = _getViewportSize();

        // Compute bounding box of all match rects
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var r in match.Rects)
        {
            if (r.Left < minX) minX = r.Left;
            if (r.Top < minY) minY = r.Top;
            if (r.Right > maxX) maxX = r.Right;
            if (r.Bottom > maxY) maxY = r.Bottom;
        }

        // Center the match bounding box in the viewport
        double centerX = (minX + maxX) / 2.0;
        double centerY = (minY + maxY) / 2.0;
        doc.Camera.OffsetX = ww / 2.0 - centerX * doc.Camera.Zoom;
        doc.Camera.OffsetY = wh / 2.0 - centerY * doc.Camera.Zoom;
        doc.ClampCamera(ww, wh);
    }

    public void UpdateCurrentPageMatches()
    {
        if (_getActiveDocument() is not { } doc)
        {
            CurrentPageSearchMatches = null;
            return;
        }
        _searchMatchesByPage.TryGetValue(doc.CurrentPage, out var matches);
        CurrentPageSearchMatches = matches;
    }

    public static IEnumerable<(int Index, int Length)> FindAllOccurrences(string text, string query, StringComparison comparison)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(query, pos, comparison);
            if (idx < 0) break;
            yield return (idx, query.Length);
            pos = idx + 1;
        }
    }

    public SearchResult GetSearchState()
    {
        var perPage = _searchMatchesByPage.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        return new SearchResult(SearchMatches.Count, ActiveMatchIndex, perPage);
    }
}
