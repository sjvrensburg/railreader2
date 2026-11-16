using RailReader2.Library;
using Xunit;

namespace RailReader.Export.Tests.Library;

public class LibraryQueryTests
{
    [Theory]
    [InlineData("bootstrap", "\"bootstrap\"")]
    [InlineData("bootstrap band", "\"bootstrap\" \"band\"")]
    [InlineData("\"confidence band\" bootstrap", "\"confidence band\" \"bootstrap\"")]
    [InlineData("estimat*", "\"estimat\"*")]
    [InlineData("self-attention", "\"self-attention\"")]
    [InlineData("C++ NEAR/5 AND", "\"C++\" \"NEAR/5\" \"AND\"")]   // operators are literal words
    [InlineData("say \"hi", "\"say\" \"hi\"")]                       // unclosed quote runs to the end
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
    [InlineData("", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(input));
}
