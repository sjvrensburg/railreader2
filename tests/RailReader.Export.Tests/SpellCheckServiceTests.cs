using System.Globalization;
using RailReader2.Services;
using WeCantSpell.Hunspell;
using Xunit;

namespace RailReader.Export.Tests;

public sealed class SpellCheckServiceTests : IDisposable
{
    // The app project's bundled dictionaries are copied into this test project's output too.
    private static readonly Lazy<WordList> s_enGb = new(() => WordList.CreateFromFiles(
        Path.Combine(AppContext.BaseDirectory, "Dictionaries", "en_GB.dic"),
        Path.Combine(AppContext.BaseDirectory, "Dictionaries", "en_GB.aff")));

    private readonly string _wordsPath = Path.Combine(Path.GetTempPath(), $"rr-spell-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_wordsPath)) File.Delete(_wordsPath);
    }

    private static List<string> Words(string text)
        => SpellCheckService.Tokenize(text).Select(t => text.Substring(t.Start, t.Length)).ToList();

    private List<string> Misspelled(SpellCheckService service, string text)
        => service.Check(text).Select(m => m.Word).ToList();

    [Fact]
    public void Tokenize_SplitsPlainWordsAndHyphens()
    {
        // Single letters ("A", "I", maths variables) are never checked.
        Assert.Equal(["well", "known", "result"], Words("A well-known result."));
    }

    [Fact]
    public void Tokenize_KeepsInnerApostrophesAndTrimsQuotes()
    {
        Assert.Equal(["don't", "Fisher’s", "quoted"], Words("don't Fisher’s 'quoted'"));
    }

    [Theory]
    [InlineData("see https://example.com/some/pathh")]
    [InlineData("mail someone@example.com")]
    [InlineData("H0 x_i 2nd")]
    [InlineData("let x be")]
    [InlineData("ANOVA OLS")]
    [InlineData("LaTeX SciPy")]
    [InlineData("$\\alpha + \\hat{\\beta}$")]
    public void Tokenize_SkipsNonProse(string text)
    {
        // Every token left over is an ordinary word — none of the technical fragments survive.
        var words = Words(text);
        Assert.DoesNotContain(words, w => w is "pathh" or "someone" or "H0" or "x_i" or "2nd"
            or "x" or "ANOVA" or "OLS" or "LaTeX" or "SciPy" or "alpha" or "hat" or "beta"
            or "nd" or "th");
    }

    [Fact]
    public void Tokenize_SkipsOrdinalSuffixes()
    {
        Assert.Equal(["the", "and", "items"], Words("the 2nd and 10th items"));
    }

    [Fact]
    public void Check_FlagsOnlyTheMisspelling_WithCorrectOffsets()
    {
        var service = new SpellCheckService(s_enGb.Value, _wordsPath);
        const string text = "This sentance has one mistake.";
        var m = Assert.Single(service.Check(text));
        Assert.Equal("sentance", m.Word);
        Assert.Equal(text.IndexOf("sentance", StringComparison.Ordinal), m.Start);
        Assert.Equal("sentance".Length, m.Length);
    }

    [Fact]
    public void Check_AcceptsBritishSpellingAndCurlyApostrophes()
    {
        var service = new SpellCheckService(s_enGb.Value, _wordsPath);
        Assert.Empty(Misspelled(service, "The colour of the regression line doesn’t matter."));
    }

    [Fact]
    public void Check_AcceptsPossessiveOfKnownWord()
    {
        var service = new SpellCheckService(WordList.CreateFromWords(["Tukey", "test"]), _wordsPath);
        Assert.Empty(Misspelled(service, "Tukey's test"));
    }

    [Fact]
    public void Suggest_OffersTheIntendedWord()
    {
        var service = new SpellCheckService(s_enGb.Value, _wordsPath);
        Assert.Contains("sentence", service.Suggest("sentance"));
    }

    [Fact]
    public void Suggest_KeepsTheTypistsCurlyApostrophe()
    {
        var service = new SpellCheckService(s_enGb.Value, _wordsPath);
        Assert.Contains("doesn’t", service.Suggest("dosn’t"));
    }

    [Fact]
    public void PersonalDictionary_AcceptsWordCaseInsensitively_AndPersists()
    {
        var service = new SpellCheckService(s_enGb.Value, _wordsPath);
        Assert.Equal(["heteroscedasticityy"], Misspelled(service, "heteroscedasticityy"));

        service.AddToPersonalDictionary("heteroscedasticityy");
        Assert.Empty(Misspelled(service, "Heteroscedasticityy is common."));

        var reloaded = new SpellCheckService(s_enGb.Value, _wordsPath);
        Assert.Contains("heteroscedasticityy", reloaded.PersonalWords);
        Assert.Empty(Misspelled(reloaded, "heteroscedasticityy"));

        reloaded.RemoveFromPersonalDictionary("heteroscedasticityy");
        Assert.Equal(["heteroscedasticityy"], Misspelled(reloaded, "heteroscedasticityy"));
    }

    [Fact]
    public void Ignore_AcceptsWordWithoutPersisting()
    {
        var service = new SpellCheckService(s_enGb.Value, _wordsPath);
        service.Ignore("frobnicate");
        Assert.Empty(Misspelled(service, "frobnicate"));
        Assert.False(File.Exists(_wordsPath));
    }

    [Theory]
    [InlineData("en-US", "en_US")]
    [InlineData("en-ZA", "en_GB")]   // same language, no exact match → first (British) English
    [InlineData("de-DE", "en_GB")]   // no German dictionary → British English
    [InlineData("", "en_GB")]        // invariant culture (LANG=C)
    public void DefaultLanguage_FollowsUiCulture(string culture, string expected)
    {
        Assert.Equal(expected,
            SpellCheckService.DefaultLanguage(["en_GB", "en_US"], CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void DefaultLanguage_PrefersExactUserDictionary()
    {
        Assert.Equal("en_ZA",
            SpellCheckService.DefaultLanguage(["en_GB", "en_US", "en_ZA"], CultureInfo.GetCultureInfo("en-ZA")));
    }
}
