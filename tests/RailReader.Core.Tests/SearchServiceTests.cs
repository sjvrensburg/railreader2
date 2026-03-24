using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class SearchServiceTests : IDisposable
{
    private readonly string _pdfPath;
    private readonly DocumentState _state;
    private readonly SearchService _search;
    private int _lastGoToPage = -1;

    public SearchServiceTests()
    {
        var config = new AppConfig();
        var factory = TestFixtures.CreatePdfFactory();
        _pdfPath = TestFixtures.GetTestPdfPath();
        _state = new DocumentState(_pdfPath, factory.CreatePdfService(_pdfPath),
            factory.CreatePdfTextService(), config, new SynchronousThreadMarshaller());
        _state.LoadPageBitmap();

        _search = new SearchService(
            () => _state,
            () => (800.0, 600.0),
            page => _lastGoToPage = page);
    }

    public void Dispose() => _state.Dispose();

    // ---------------------------------------------------------------
    // PrepareSearchParams
    // ---------------------------------------------------------------

    [Fact]
    public void PrepareSearchParams_PlainText_NullRegex()
    {
        var (regex, comparison) = SearchService.PrepareSearchParams("hello", caseSensitive: true, useRegex: false);
        Assert.Null(regex);
        Assert.Equal(StringComparison.Ordinal, comparison);
    }

    [Fact]
    public void PrepareSearchParams_ValidRegex_Compiles()
    {
        var (regex, _) = SearchService.PrepareSearchParams(@"test\d+", caseSensitive: true, useRegex: true);
        Assert.NotNull(regex);
        Assert.Matches(regex, "test123");
        Assert.DoesNotMatch(regex, "hello");
    }

    [Fact]
    public void PrepareSearchParams_InvalidRegex_NullRegex()
    {
        var (regex, _) = SearchService.PrepareSearchParams("[", caseSensitive: true, useRegex: true);
        Assert.Null(regex);
    }

    [Fact]
    public void PrepareSearchParams_CaseInsensitive_SetsComparison()
    {
        var (_, comparison) = SearchService.PrepareSearchParams("test", caseSensitive: false, useRegex: false);
        Assert.Equal(StringComparison.OrdinalIgnoreCase, comparison);
    }

    // ---------------------------------------------------------------
    // FindAllOccurrences
    // ---------------------------------------------------------------

    [Fact]
    public void FindAllOccurrences_MultipleHits()
    {
        var hits = SearchService.FindAllOccurrences("ababab", "ab", StringComparison.Ordinal).ToList();
        Assert.Equal(3, hits.Count);
        Assert.Equal(0, hits[0].Index);
        Assert.Equal(2, hits[1].Index);
        Assert.Equal(4, hits[2].Index);
        Assert.All(hits, h => Assert.Equal(2, h.Length));
    }

    [Fact]
    public void FindAllOccurrences_Overlapping()
    {
        var hits = SearchService.FindAllOccurrences("aaa", "aa", StringComparison.Ordinal).ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0].Index);
        Assert.Equal(1, hits[1].Index);
    }

    [Fact]
    public void FindAllOccurrences_CaseInsensitive()
    {
        var hits = SearchService.FindAllOccurrences("ABAB", "ab", StringComparison.OrdinalIgnoreCase).ToList();
        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0].Index);
        Assert.Equal(2, hits[1].Index);
    }

    [Fact]
    public void FindAllOccurrences_NoMatch_Empty()
    {
        var hits = SearchService.FindAllOccurrences("abc", "xyz", StringComparison.Ordinal).ToList();
        Assert.Empty(hits);
    }

    // ---------------------------------------------------------------
    // Navigation via FinalizeSearch
    // ---------------------------------------------------------------

    private static List<SearchMatch> MakeMatches(params int[] pages)
    {
        var matches = new List<SearchMatch>();
        foreach (var page in pages)
        {
            matches.Add(new SearchMatch(page, 0, 4, [new RectF(10, 10, 50, 20)]));
        }
        return matches;
    }

    [Fact]
    public void NextMatch_WrapsAround()
    {
        var matches = MakeMatches(0, 0, 0);
        _search.FinalizeSearch(_state, matches);

        // Move to the last match
        _search.ActiveMatchIndex = 2;
        _search.NextMatch();

        Assert.Equal(0, _search.ActiveMatchIndex);
    }

    [Fact]
    public void PreviousMatch_WrapsAround()
    {
        var matches = MakeMatches(0, 0, 0);
        _search.FinalizeSearch(_state, matches);

        _search.ActiveMatchIndex = 0;
        _search.PreviousMatch();

        Assert.Equal(2, _search.ActiveMatchIndex);
    }

    [Fact]
    public void CloseSearch_ClearsState()
    {
        var matches = MakeMatches(0, 1, 2);
        _search.FinalizeSearch(_state, matches);

        Assert.Equal(3, _search.SearchMatches.Count);

        _search.CloseSearch();

        Assert.Empty(_search.SearchMatches);
        Assert.Null(_search.CurrentPageSearchMatches);
        Assert.Equal(0, _search.ActiveMatchIndex);
    }

    [Fact]
    public void GoToMatch_OutOfRange_NoOp()
    {
        var matches = MakeMatches(0, 0);
        _search.FinalizeSearch(_state, matches);

        int indexBefore = _search.ActiveMatchIndex;

        // Negative index: should not crash or change state
        _search.GoToMatch(-1);
        Assert.Equal(indexBefore, _search.ActiveMatchIndex);

        // Way out of range: should not crash or change state
        _search.GoToMatch(999);
        Assert.Equal(indexBefore, _search.ActiveMatchIndex);
    }

    // ---------------------------------------------------------------
    // GetMatchSnippet
    // ---------------------------------------------------------------

    [Fact]
    public void GetMatchSnippet_MidText_HasEllipsis()
    {
        // Inject known text into TextCache for page 0
        string longText = new string('x', 60) + "MATCH" + new string('y', 60);
        _state.TextCache[0] = new PageText(longText, []);

        var match = new SearchMatch(0, 60, 5, [new RectF(10, 10, 50, 20)]);
        var (pre, matchStr, post) = _search.GetMatchSnippet(match, contextChars: 40);

        // Match text should be extracted correctly
        Assert.Equal("MATCH", matchStr);

        // Pre should start with ellipsis since match is well into the text
        Assert.StartsWith("\u2026", pre);

        // Post should end with ellipsis since there is text remaining
        Assert.EndsWith("\u2026", post);
    }

    [Fact]
    public void GetMatchSnippet_ReplacesNewlines()
    {
        string text = "before\nthe\nmatch\nHERE\nafter\nthe\nmatch";
        _state.TextCache[0] = new PageText(text, []);

        // "HERE" starts at index 18, length 4
        int matchStart = text.IndexOf("HERE");
        var match = new SearchMatch(0, matchStart, 4, [new RectF(10, 10, 50, 20)]);
        var (pre, matchStr, post) = _search.GetMatchSnippet(match, contextChars: 40);

        Assert.Equal("HERE", matchStr);

        // All newlines should be replaced with spaces
        Assert.DoesNotContain("\n", pre);
        Assert.DoesNotContain("\n", matchStr);
        Assert.DoesNotContain("\n", post);

        // Verify the newlines were replaced (not stripped) — spaces should be present
        Assert.Contains(" ", pre);
        Assert.Contains(" ", post);
    }
}
