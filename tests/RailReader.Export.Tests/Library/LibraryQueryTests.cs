using RailReader2.Library;
using Xunit;

namespace RailReader.Export.Tests.Library;

public class LibraryQueryTests
{
    [Theory]
    [InlineData("bootstrap", "\"bootstrap\"")]
    [InlineData("bootstrap band", "\"bootstrap\" AND \"band\"")]
    [InlineData("\"confidence band\" bootstrap", "\"confidence band\" AND \"bootstrap\"")]
    [InlineData("estimat*", "\"estimat\"*")]
    [InlineData("self-attention", "\"self-attention\"")]
    [InlineData("C++ NEAR/5 AND", "\"C++\" AND \"NEAR/5\" AND \"AND\"")]   // operators are literal words
    [InlineData("say \"hi", "\"say\" AND \"hi\"")]                       // unclosed quote runs to the end
    [InlineData("ridge -lasso", "(\"ridge\") NOT (\"lasso\")")]
    [InlineData("ridge -lasso -\"elastic net\"", "(\"ridge\") NOT (\"lasso\" OR \"elastic net\")")]
    [InlineData("ﬁsher", "\"fisher\"")]                              // NFKC folds the ligature
    public void Parse_QuotesEveryTerm(string input, string expected)
        => Assert.Equal(expected, LibraryQuery.Parse(input).MatchExpression);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-only -negatives")]
    [InlineData("*** ... \"\"")]
    public void Parse_NothingSearchable_IsEmpty(string input)
        => Assert.True(LibraryQuery.Parse(input).IsEmpty);

    [Fact]
    public void Parse_QuoteInsideAWord_IsEscaped()
        => Assert.Equal("\"a\"\"b\"\"\"", LibraryQuery.Parse("a\"b\"").MatchExpression);

    [Theory]
    [InlineData("diagnosing", "diagnosis")]     // Porter: "diagnos" vs "diagnosi"
    [InlineData("diagnosis", "diagnosing")]
    [InlineData("analysis", "analysing")]
    [InlineData("estimator", "estimate")]       // Porter: "estim" vs "estim" + "at"
    [InlineData("hypotheses", "hypothesis")]
    public void Family_LinksWhatThePorterStemmerDoesNot(string word, string sibling)
        => Assert.Contains(sibling, LibraryQuery.Family(word));

    [Theory]
    [InlineData("means")]          // root "mean" is too short to be safe
    [InlineData("kernel")]         // no ending to remove
    [InlineData("self-attention")] // the tokenizer splits it
    [InlineData("C++")]
    public void Family_LeavesShortOrNonWordTermsAlone(string word)
        => Assert.Equal([word], LibraryQuery.Family(word));

    [Fact]
    public void Parse_ExpandsSingleWordsOnly()
    {
        var q = LibraryQuery.Parse("diagnosing \"confidence band\" estimat* -analysis");
        Assert.StartsWith("((\"diagnosing\" OR \"diagnos\" OR ", q.MatchExpression);
        Assert.Contains(" AND \"confidence band\" AND \"estimat\"*) NOT (\"analysis\")", q.MatchExpression);
        Assert.Equal(["diagnosing", "confidence band", "estimat*"], q.PlainTerms);
    }

    [Fact]
    public void PlainTerms_ExcludeNegatedTerms()
        => Assert.Equal(["ridge", "elastic net"], LibraryQuery.Parse("ridge \"elastic net\" -lasso").PlainTerms);
}

public class TextNormalizerTests
{
    [Theory]
    [InlineData("estima-\ntion of the", "estimation of the")]
    [InlineData("Marchenko-\nPastur law", "Marchenko- Pastur law")]   // upper case after: keep the hyphen
    [InlineData("a\r\n\r\n  b\t c", "a b c")]
    [InlineData("eﬃcient", "efficient")]
    [InlineData("x-\n", "x-")]
    [InlineData("and Trans\u0002formers [165]", "and Transformers [165]")]   // PDFium's line-end hyphen
    [InlineData("Marchenko\u0002Pastur law", "Marchenko- Pastur law")]
    [InlineData("\u0002lead", "lead")]
    [InlineData("esti­mation of", "estimation of")]   // mid-line soft hyphen: invisible
    [InlineData("estima­\ntion of", "estimation of")]
    [InlineData("a uni\u001Ced view", "a unified view")]     // TeX T1 ligature codes
    [InlineData("coe\u001Ecient di\u001Berent \u001Dat su\u001Fe", "coefficient different flat suffle")]
    [InlineData("switches o\u001B entirely", "switches off entirely")]
    [InlineData("De\u000Cnition", "Definition")]              // OT1
    [InlineData("Eckart\u0015Young \u0010quoted", "Eckart Young quoted")] // T1 dash and quote: separators
    [InlineData("a \u001C b", "a b")]                         // not touching a letter: a separator
    [InlineData("", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(input));
}
