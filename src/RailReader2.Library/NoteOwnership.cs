namespace RailReader2.Library;

/// <summary>Whose note is it: the reader's own, or a reviewer's? One rule for the Comments pane, the
/// Library window and the CLI (design Q2).
/// <para>A note only stored in the app's sidecar is the reader's. A note in the PDF is the reader's when
/// its author is the reader's name (their saved notes read back as in-PDF), or, when it carries no author,
/// when Core marked it as written by RailReader (Core 0.88.0's <c>Annotation.WrittenByRailReader</c>, so
/// an unnamed reader's saved notes aren't mistaken for a reviewer's). The mark identifies the app, not the
/// person: a marked note signed with another name is another RailReader user's, so a reviewer's.</para></summary>
public static class NoteOwnership
{
    public static bool IsReviewer(bool inPdf, bool writtenByRailReader, string? author, string? readerName)
    {
        if (!inPdf) return false;
        author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        if (author is not null && readerName is { } me && !string.IsNullOrWhiteSpace(me)
            && string.Equals(author, me.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        return !(writtenByRailReader && author is null);
    }
}
