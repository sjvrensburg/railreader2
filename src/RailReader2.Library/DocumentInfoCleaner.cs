using System.Text.RegularExpressions;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>Turns a PDF's Info-dictionary metadata into <see cref="DocumentInfo"/>. Many PDFs carry a
/// title their authoring tool made up ("Microsoft Word - draft3.docx", "paper.dvi", "Untitled"); such a
/// title would replace the file name on the results and the recents cards with something worse, so it is
/// dropped. Authors, subject and keywords are only normalised: a junk value there is searchable noise,
/// never shown in place of anything.</summary>
public static partial class DocumentInfoCleaner
{
    public static DocumentInfo From(PdfMetadata? meta)
    {
        if (meta is null || meta.IsEmpty) return DocumentInfo.Empty;
        return new DocumentInfo(CleanTitle(meta.Title), CleanAuthor(meta.Author), Clean(meta.Subject), Clean(meta.Keywords));
    }

    /// <summary>The title, or null when it is missing or not really a title.</summary>
    public static string? CleanTitle(string? title)
    {
        if (Clean(title) is not { } t) return null;
        t = ToolPrefix().Replace(t, "");
        if (t.Length < 3 || !t.Any(char.IsLetter)) return null;
        if (FileLike().IsMatch(t) || t.Contains('\\') || t.StartsWith('/')) return null;
        if (Placeholders.Contains(t) || DefaultName().IsMatch(t)) return null;
        return t;
    }

    static string? CleanAuthor(string? author)
        => Clean(author) is { } a && !PlaceholderAuthors.Contains(a) ? a : null;

    static string? Clean(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : TextNormalizer.Normalize(s) is { Length: > 0 } n ? n : null;

    static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "untitled", "untitled document", "no title", "title", "document", "doc", "paper", "slide 1",
        "powerpoint presentation", "presentation", "article", "manuscript", "main", "draft",
    };

    static readonly HashSet<string> PlaceholderAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "admin", "administrator", "owner", "author", "unknown", "anonymous", "windows user", "default",
        "python-docx", // the library's default core-properties author, seen on a real generated report
    };

    // "Microsoft Word - ", "Microsoft PowerPoint - " … the remainder is usually the file name.
    [GeneratedRegex(@"^Microsoft\s+\w+\s+-\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ToolPrefix();

    // An authoring tool's name for a new, unsaved file ("Document1", "Presentation2", "Untitled-3"),
    // what is left after "Microsoft Word - " when the reader never named the file.
    [GeneratedRegex(@"^(document|presentation|book|untitled|slide)[\s_-]*\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultName();

    // A file name: no spaces needed, a document/source extension at the end ("main.tex", "ch3.dvi").
    [GeneratedRegex(@"\.(pdf|docx?|odt|rtf|txt|tex|dvi|e?ps|pptx?|key|pages|indd|qxd|md|html?)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileLike();
}
