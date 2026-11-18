using System.Text;

namespace RailReader2.Library;

/// <summary>One note for <see cref="NotesMarkdown"/>: an annotation as the Comments pane shows it.
/// <see cref="Page"/> is 0-based; <see cref="IsReply"/> notes follow their thread's first note.</summary>
public sealed record NoteForExport(
    int Page, string Kind, string? MarkedText, string? Comment, string? Author, bool IsReviewer,
    string? Category, bool IsReply, DateTime? DateUtc);

/// <summary>"Copy Notes for AI" (Comments pane, roadmap M8): a document's notes as Markdown, to paste into an
/// AI chat or any document when no MCP client is connected. Grouped by page (1-based), threads kept together,
/// each note labelled with its kind, colour category and whether it is the reader's or a reviewer's.</summary>
public static class NotesMarkdown
{
    public static string Build(string documentTitle, IReadOnlyList<NoteForExport> notes)
    {
        var sb = new StringBuilder();
        sb.Append("# Notes on ").AppendLine(OneLine(documentTitle));
        sb.AppendLine();
        int threads = notes.Count(n => !n.IsReply), replies = notes.Count - threads;
        sb.Append(Count(threads, "note", "notes"));
        if (replies > 0) sb.Append(" and ").Append(Count(replies, "reply", "replies"));
        sb.AppendLine(", from RailReader2. Quoted lines are the text the note marks; pages count from 1.");

        int? page = null;
        foreach (var n in notes)
        {
            if (!n.IsReply && n.Page != page)
            {
                page = n.Page;
                sb.AppendLine().Append("## Page ").Append(n.Page + 1).AppendLine().AppendLine();
            }
            string indent = n.IsReply ? "  " : "";
            sb.Append(indent).Append("- **").Append(n.IsReply ? "Reply" : n.Kind).Append("**");
            if (n.Category is { Length: > 0 } c) sb.Append(" · ").Append(OneLine(c));
            sb.Append(" · ").Append(Who(n));
            if (n.DateUtc is { } d) sb.Append(" · ").Append(d.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine();
            if (n.MarkedText is { Length: > 0 } m)
                AppendBlock(sb, indent + "  > ", m);
            if (n.Comment is { Length: > 0 } body)
                AppendBlock(sb, indent + "  ", body);
        }
        if (notes.Count == 0) sb.AppendLine().AppendLine("(No notes.)");
        return sb.ToString();
    }

    static string Who(NoteForExport n)
    {
        string? name = string.IsNullOrWhiteSpace(n.Author) ? null : OneLine(n.Author);
        return n.IsReviewer ? (name is null ? "reviewer" : $"{name} (reviewer)") : (name is null ? "mine" : $"{name} (mine)");
    }

    /// <summary>Every line of <paramref name="text"/> with <paramref name="prefix"/>, blank lines dropped, so a
    /// multi-line comment stays inside its list item.</summary>
    static void AppendBlock(StringBuilder sb, string prefix, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            if (line.Trim() is { Length: > 0 } t) sb.Append(prefix).AppendLine(t);
    }

    static string OneLine(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
