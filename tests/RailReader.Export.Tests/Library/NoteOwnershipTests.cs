using RailReader2.Library;
using Xunit;

namespace RailReader.Export.Tests.Library;

public class NoteOwnershipTests
{
    [Theory]
    // inPdf, marked, author, reader name → reviewer?
    [InlineData(false, false, null, null, false)]          // sidecar-only note: the reader's
    [InlineData(true, false, null, null, true)]            // unmarked, unsigned, in the PDF: unknown origin → reviewer
    [InlineData(true, true, null, null, false)]            // RailReader wrote it, no author: the reader's (Core 0.88.0)
    [InlineData(true, true, null, "Jane", false)]          // written before the reader set a name
    [InlineData(true, false, "jane ", "Jane", false)]      // author matches the reader's name
    [InlineData(true, true, "Sam", "Jane", true)]          // another RailReader user's signed note
    [InlineData(true, true, "Sam", null, true)]            // signed by someone, reader unnamed
    [InlineData(true, false, "Referee 2", "Jane", true)]
    public void IsReviewer(bool inPdf, bool marked, string? author, string? reader, bool expected)
        => Assert.Equal(expected, NoteOwnership.IsReviewer(inPdf, marked, author, reader));

    [Fact]
    public void LibraryLabel_UsesTheMark()
    {
        var mine = new LibraryHit("a", "/p/a.pdf", "a.pdf", 0, HitSource.Note, "x", 1, NoteKind: "textnote",
            NoteOrigin: NoteSource.InPdfByRailReader);
        Assert.Equal("page 1 · your note", LibraryResults.Label(mine, null));
        Assert.Equal("page 1 · reviewer comment", LibraryResults.Label(mine with { NoteOrigin = NoteSource.InPdf }, null));
    }
}
