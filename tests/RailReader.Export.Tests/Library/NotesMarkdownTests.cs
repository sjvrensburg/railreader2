using RailReader2.Library;
using Xunit;

namespace RailReader.Export.Tests.Library;

/// <summary>Comments pane ▸ Copy Notes for AI (roadmap M8).</summary>
public class NotesMarkdownTests
{
    static readonly DateTime When = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GroupsByPage_KeepsThreadsTogether_AndLabelsEachNote()
    {
        var md = NotesMarkdown.Build("Minimum Wages\nand Employment",
        [
            new(0, "Note", null, "Check the sample period", null, false, null, false, When),
            new(2, "Highlight", "compares New Jersey with Pennsylvania", "clean identification\n\nargument", "Referee 2", true, "Definition", false, When),
            new(2, "Note", null, "Agreed, see §4", "Stefan", false, null, true, null),
        ]);
        Assert.Equal("""
            # Notes on Minimum Wages and Employment

            2 notes and 1 reply, from RailReader2. Quoted lines are the text the note marks; pages count from 1.

            ## Page 1

            - **Note** · mine · 2026-11-17
              Check the sample period

            ## Page 3

            - **Highlight** · Definition · Referee 2 (reviewer) · 2026-11-17
              > compares New Jersey with Pennsylvania
              clean identification
              argument
              - **Reply** · Stefan (mine)
                Agreed, see §4

            """.Replace("\r\n", "\n"), md.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AReplyNeverStartsANewPageHeading()
    {
        // A reply anchored on another page still belongs under its thread.
        var md = NotesMarkdown.Build("x",
        [
            new(4, "Highlight", "text", null, null, true, null, false, null),
            new(5, "Note", null, "reply", null, false, null, true, null),
        ]);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(md, "^## Page", System.Text.RegularExpressions.RegexOptions.Multiline));
    }

    [Fact]
    public void NoNotes_SaysSo()
        => Assert.Contains("(No notes.)", NotesMarkdown.Build("x", []));
}
