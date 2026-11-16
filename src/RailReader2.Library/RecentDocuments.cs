namespace RailReader2.Library;

/// <summary>One card on the welcome screen's recent documents (design §7.2).</summary>
/// <param name="Page">The saved reading position, 0-based.</param>
/// <param name="PageCount">From the index; null when the document isn't indexed (yet).</param>
public sealed record RecentDocument(
    string Path, string? ContentId, string Title, int Page, int? PageCount, int NoteCount, bool Missing, bool Locked)
{
    /// <summary>"page 41 of 230", or "page 41" without a page count.</summary>
    public string Progress => PageCount is int n && n > 0 ? $"page {Math.Min(Page + 1, n)} of {n}" : $"page {Page + 1}";

    /// <summary>Reading progress 0–1, or null without a page count.</summary>
    public double? Fraction => PageCount is int n && n > 0 ? Math.Clamp((Page + 1.0) / n, 0, 1) : null;
}

public static class RecentDocuments
{
    public const int MaxCards = 12;

    /// <summary>Builds the dashboard's cards from the recent-files list (newest first): one card per
    /// document, de-duplicated by content id so two paths to one file are one card (the newest entry
    /// wins), joined to the index for page and note counts. <paramref name="index"/> may be null (no index
    /// yet); <paramref name="idOf"/> is <c>DocumentIdentity.Peek</c> in the app.</summary>
    public static IReadOnlyList<RecentDocument> Build(
        IEnumerable<(string Path, int Page)> recents, Func<string, string?> idOf, LibraryIndex? index,
        Func<string, bool>? exists = null, int max = MaxCards)
    {
        exists ??= File.Exists;
        var noteCounts = index?.NoteCounts();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cards = new List<RecentDocument>();
        foreach (var (path, page) in recents)
        {
            if (cards.Count >= max) break;
            if (string.IsNullOrEmpty(path)) continue;
            string? id = Safe(() => idOf(path));
            if (!seen.Add(id ?? "path:" + path)) continue;
            var doc = id is null ? null : index?.GetDocument(id);
            bool locked = doc?.State == DocumentState.Locked;
            cards.Add(new RecentDocument(
                path, id, doc?.Title ?? System.IO.Path.GetFileNameWithoutExtension(path), Math.Max(0, page),
                doc is { PageCount: > 0 } ? doc.PageCount : null,
                id is not null && noteCounts is not null ? noteCounts.GetValueOrDefault(id) : 0,
                !Safe(() => exists(path)), locked));
        }
        return cards;
    }

    /// <summary>Where a document's first-page thumbnail is cached (<c>ConfigDir/library/thumbs</c>).</summary>
    public static string ThumbnailPath(string contentId, string? dbPath = null)
        => System.IO.Path.Combine(ThumbnailDirectory(dbPath), contentId + ".png");

    public static string ThumbnailDirectory(string? dbPath = null)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dbPath ?? LibraryIndex.DefaultPath)!, "thumbs");

    /// <summary>A cached thumbnail is current when it exists and is newer than the file it shows.</summary>
    public static bool ThumbnailIsCurrent(string thumbPath, string pdfPath)
    {
        try
        {
            var t = new FileInfo(thumbPath);
            return t.Exists && (!File.Exists(pdfPath) || t.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(pdfPath));
        }
        catch (IOException) { return false; }
    }

    static T Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return default!; }
    }
}
