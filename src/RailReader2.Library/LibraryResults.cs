namespace RailReader2.Library;

/// <summary>One document's results in the Library window (design §7.1): its best few hits, the number of
/// hits in all, and whether its file is still where the index last saw it.</summary>
public sealed record LibraryResultGroup(
    string ContentId, string? LastPath, string FileName, int HitCount, double BestScore,
    IReadOnlyList<LibraryHit> Hits, bool Missing, int NoteCount);

public static class LibraryResults
{
    /// <summary>Groups <paramref name="hits"/> (best first) by document, best document first. Each group
    /// keeps at most <paramref name="perDocument"/> hits, preferring different pages so three snippets
    /// from one page don't hide the others. <paramref name="exists"/> checks a path (tests pass a fake).</summary>
    public static IReadOnlyList<LibraryResultGroup> Group(
        IReadOnlyList<LibraryHit> hits, int perDocument = 3, Func<string, bool>? exists = null,
        IReadOnlyDictionary<string, int>? noteCounts = null)
    {
        exists ??= File.Exists;
        var order = new List<string>();
        var byDoc = new Dictionary<string, List<LibraryHit>>(StringComparer.Ordinal);
        foreach (var h in hits.OrderByDescending(h => h.Score))
        {
            if (!byDoc.TryGetValue(h.ContentId, out var list))
            {
                byDoc[h.ContentId] = list = [];
                order.Add(h.ContentId);
            }
            list.Add(h);
        }

        var groups = new List<LibraryResultGroup>(order.Count);
        foreach (var id in order)
        {
            var all = byDoc[id];
            var chosen = new List<LibraryHit>();
            var pages = new HashSet<(HitSource, int?)>();
            foreach (var h in all)
                if (chosen.Count < perDocument && pages.Add((h.Source, h.Page))) chosen.Add(h);
            foreach (var h in all)
                if (chosen.Count < perDocument && !chosen.Contains(h)) chosen.Add(h);
            var first = all[0];
            bool missing = first.LastPath is not { Length: > 0 } p || !SafeExists(exists, p);
            groups.Add(new LibraryResultGroup(id, first.LastPath, first.FileName, all.Count, first.Score, chosen,
                missing, noteCounts?.GetValueOrDefault(id) ?? 0));
        }
        return groups;
    }

    static bool SafeExists(Func<string, bool> exists, string path)
    {
        try { return exists(path); }
        catch (Exception) { return false; }
    }

    /// <summary>How a hit is labelled in the results ("page 4", "your note", "reviewer comment", …),
    /// shared by the Library window and the CLI. <paramref name="readerName"/> is the reader's own author
    /// name: an in-PDF note by anyone else is a reviewer's (design Q2).</summary>
    public static string Label(LibraryHit h, string? readerName)
    {
        string page = h.Page is { } p ? $"page {p + 1}" : "";
        return h.Source switch
        {
            HitSource.Page => h.FromOcr ? $"{page} · OCR text, unverified" : page,
            HitSource.Outline => $"{page} · outline",
            HitSource.Note when h.NoteKind == "bookmark" => $"{page} · bookmark",
            HitSource.Note => $"{page} · {(IsReviewer(h, readerName) ? $"reviewer comment{(h.Author is { } a ? $" ({a})" : "")}" : "your note")}"
                + (h.Category is { Length: > 0 } c ? $" · {c}" : ""),
            HitSource.Name => "file name",
            _ => page,
        };
    }

    /// <summary>An in-PDF note counts as the reader's own when its author is the reader's name (their saved
    /// notes read back as in-PDF), as in the Comments pane.</summary>
    public static bool IsReviewer(LibraryHit h, string? readerName)
        => h.NoteOrigin == NoteSource.InPdf
           && !(readerName is { Length: > 0 } me && string.Equals(h.Author?.Trim(), me.Trim(), StringComparison.OrdinalIgnoreCase));
}
