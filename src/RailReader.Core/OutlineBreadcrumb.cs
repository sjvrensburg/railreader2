using RailReader.Core.Models;

namespace RailReader.Core;

/// <summary>
/// Resolves the breadcrumb path from a PDF outline tree to a given page.
/// Walks the tree top-down, picking at each level the entry with the largest
/// Page value not exceeding currentPage, then descending into its children.
/// </summary>
public static class OutlineBreadcrumb
{
    /// <summary>
    /// Returns the breadcrumb path (root → leaf) for the given page. Empty if
    /// no entry at any level has Page ≤ currentPage.
    /// </summary>
    public static List<OutlineEntry> BuildPath(IReadOnlyList<OutlineEntry> outline, int currentPage)
    {
        var path = new List<OutlineEntry>();
        WalkPath(outline, currentPage, path);
        return path;
    }

    private static void WalkPath(IReadOnlyList<OutlineEntry> entries, int currentPage, List<OutlineEntry> path)
    {
        var best = FindBestAtLevel(entries, currentPage);
        if (best is null) return;
        path.Add(best);
        WalkPath(best.Children, currentPage, path);
    }

    private static OutlineEntry? FindBestAtLevel(IReadOnlyList<OutlineEntry> entries, int currentPage)
    {
        OutlineEntry? best = null;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Page is { } p && p <= currentPage)
            {
                if (best is null || p >= best.Page!.Value)
                    best = entry;
            }
        }
        return best;
    }
}
