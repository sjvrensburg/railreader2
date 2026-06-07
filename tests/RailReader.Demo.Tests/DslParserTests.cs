using RailReader.Demo;
using Xunit;

namespace RailReader.Demo.Tests;

public class DslParserTests
{
    [Fact]
    public void ParsesHeaderScalars()
    {
        var s = DslParser.Parse("""
            demo: moment-rail
            source: papers/MOMENT.pdf
            fps: 60
            cursor: park
            recorder: portal
            output: out/moment.mp4
            steps:
              - open
            """);

        Assert.Equal("moment-rail", s.Name);
        Assert.Equal("papers/MOMENT.pdf", s.Source);
        Assert.Equal(60, s.Fps);
        Assert.Equal("park", s.Cursor);
        Assert.Equal("portal", s.Recorder);
        Assert.Equal("out/moment.mp4", s.Output);
        Assert.Single(s.Steps);
        Assert.Equal("open", s.Steps[0].Verb);
    }

    [Fact]
    public void ParsesAllStepForms()
    {
        var s = DslParser.Parse("""
            steps:
              - open
              - goto_page: 1
              - fit_page
              - hold: 800ms
              - frame_role: { role: figure, index: 0, zoom: 2.5 }
            """);

        Assert.Collection(s.Steps,
            x => Assert.Equal("open", x.Verb),
            x => { Assert.Equal("goto_page", x.Verb); Assert.Equal("1", x.Args["value"]); },
            x => Assert.Equal("fit_page", x.Verb),
            x => { Assert.Equal("hold", x.Verb); Assert.Equal("800ms", x.Args["value"]); },
            x =>
            {
                Assert.Equal("frame_role", x.Verb);
                Assert.Equal("figure", x.Args["role"]);
                Assert.Equal("0", x.Args["index"]);
                Assert.Equal("2.5", x.Args["zoom"]);
            });
    }

    [Fact]
    public void WaitParsedFromInlineMapAndContinuationLine()
    {
        var s = DslParser.Parse("""
            steps:
              - frame_role: { role: table, wait: 500ms }
              - frame_block: { index: 3 }
                wait: settled
            """);

        Assert.Equal("500ms", s.Steps[0].Wait);
        Assert.False(s.Steps[0].Args.ContainsKey("wait"));
        Assert.Equal("settled", s.Steps[1].Wait);
        Assert.Equal("3", s.Steps[1].Args["index"]);
    }

    [Fact]
    public void StripsCommentsAndBlankLines()
    {
        var s = DslParser.Parse("""
            # leading comment
            demo: x   # trailing comment

            steps:
              # a comment between items
              - open
              - goto_page: 2  # inline
            """);

        Assert.Equal("x", s.Name);
        Assert.Equal(2, s.Steps.Count);
        Assert.Equal("2", s.Steps[1].Args["value"]);
    }

    [Fact]
    public void VerbIsLowercasedAndArgsCaseInsensitive()
    {
        var s = DslParser.Parse("""
            steps:
              - Frame_Role: { Role: Figure }
            """);
        Assert.Equal("frame_role", s.Steps[0].Verb);
        Assert.Equal("Figure", s.Steps[0].Args["role"]);   // value preserved, key case-insensitive
    }

    [Theory]
    [InlineData("fps: not-a-number\nsteps:\n  - open", "fps")]
    [InlineData("bogus: 1\nsteps:\n  - open", "unknown setting")]
    [InlineData("steps:\n  - frame_role: { role: figure", "inline map")]
    [InlineData("steps:\n  open", "step item")]
    public void ReportsErrorsWithMessage(string text, string fragment)
    {
        var ex = Assert.Throws<DslParseException>(() => DslParser.Parse(text));
        Assert.Contains(fragment, ex.Message);
    }

    [Theory]
    [InlineData("fullscreen: true", true)]
    [InlineData("fullscreen: yes", true)]
    [InlineData("fullscreen: false", false)]
    public void ParsesFullscreenFlag(string line, bool expected)
    {
        var s = DslParser.Parse($"{line}\nsteps:\n  - open");
        Assert.Equal(expected, s.Fullscreen);
    }

    [Fact]
    public void FullscreenDefaultsFalse()
        => Assert.False(DslParser.Parse("steps:\n  - open").Fullscreen);

    [Fact]
    public void EmptyStepsListIsAllowed()
    {
        var s = DslParser.Parse("demo: empty\nsteps:\n");
        Assert.Empty(s.Steps);
    }
}
