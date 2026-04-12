using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Export;

/// <summary>
/// Maps layout blocks classified as headings (doc_title, paragraph_title)
/// to Markdown heading levels by matching against the PDF outline tree.
/// </summary>
public static class HeadingLevelResolver
{
    public record HeadingInfo(int BlockIndex, int Level);

    /// <summary>
    /// Resolves heading levels for all heading blocks on a given page.
    /// </summary>
    /// <param name="blocks">Layout blocks for the page, in reading order.</param>
    /// <param name="pageText">Extracted text for the page.</param>
    /// <param name="outline">Full PDF outline tree.</param>
    /// <param name="pageIndex">0-based page index.</param>
    /// <returns>Map from block index to heading level (1–6).</returns>
    public static Dictionary<int, int> Resolve(
        IReadOnlyList<LayoutBlock> blocks,
        PageText? pageText,
        IReadOnlyList<OutlineEntry> outline,
        int pageIndex)
    {
        var result = new Dictionary<int, int>();

        // Flatten the outline tree into (title, page, depth) tuples
        var flatOutline = FlattenOutline(outline);

        // Filter to entries targeting this page
        var pageEntries = flatOutline
            .Where(e => e.Page == pageIndex)
            .ToList();

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var className = block.ClassId < LayoutConstants.LayoutClasses.Length
                ? LayoutConstants.LayoutClasses[block.ClassId]
                : null;

            if (className is not ("doc_title" or "paragraph_title"))
                continue;

            // Try to extract the block's text for matching
            string? blockText = pageText?.ExtractBlockText(block);
            int? matchedDepth = null;

            if (!string.IsNullOrWhiteSpace(blockText) && pageEntries.Count > 0)
            {
                matchedDepth = FuzzyMatchOutline(blockText, pageEntries);
            }

            if (matchedDepth.HasValue)
            {
                result[i] = Math.Clamp(matchedDepth.Value, 1, 6);
            }
            else
            {
                // Fallback: doc_title → H1, paragraph_title → H2
                result[i] = className == "doc_title" ? 1 : 2;
            }
        }

        return result;
    }

    internal record FlatOutlineEntry(string Title, int? Page, int Depth);

    internal static List<FlatOutlineEntry> FlattenOutline(IReadOnlyList<OutlineEntry> entries)
    {
        var result = new List<FlatOutlineEntry>();
        FlattenRecursive(entries, 1, result);
        return result;
    }

    private static void FlattenRecursive(IReadOnlyList<OutlineEntry> entries, int depth, List<FlatOutlineEntry> result)
    {
        foreach (var entry in entries)
        {
            result.Add(new FlatOutlineEntry(entry.Title, entry.Page, depth));
            if (entry.Children.Count > 0)
                FlattenRecursive(entry.Children, depth + 1, result);
        }
    }

    internal static int? FuzzyMatchOutline(string blockText, IReadOnlyList<FlatOutlineEntry> pageEntries)
    {
        var normalized = NormalizeForMatch(blockText);
        if (string.IsNullOrEmpty(normalized))
            return null;

        // Try exact containment first (case-insensitive)
        foreach (var entry in pageEntries)
        {
            var entryNorm = NormalizeForMatch(entry.Title);
            if (string.IsNullOrEmpty(entryNorm))
                continue;

            if (normalized.Contains(entryNorm, StringComparison.OrdinalIgnoreCase) ||
                entryNorm.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Depth;
            }
        }

        // Try Levenshtein similarity (threshold: 80% of longer string)
        int? bestDepth = null;
        double bestSimilarity = 0;

        foreach (var entry in pageEntries)
        {
            var entryNorm = NormalizeForMatch(entry.Title);
            if (string.IsNullOrEmpty(entryNorm))
                continue;

            int maxLen = Math.Max(normalized.Length, entryNorm.Length);
            int distance = LevenshteinDistance(normalized, entryNorm);
            double similarity = 1.0 - (double)distance / maxLen;

            if (similarity > 0.8 && similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestDepth = entry.Depth;
            }
        }

        return bestDepth;
    }

    private static string NormalizeForMatch(string text)
    {
        // Collapse whitespace and trim
        var chars = new char[text.Length];
        int len = 0;
        bool prevSpace = true;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && len > 0)
                {
                    chars[len++] = ' ';
                    prevSpace = true;
                }
            }
            else
            {
                chars[len++] = ch;
                prevSpace = false;
            }
        }
        if (len > 0 && chars[len - 1] == ' ') len--;
        return new string(chars, 0, len);
    }

    internal static int LevenshteinDistance(string s, string t)
    {
        if (s.Length == 0) return t.Length;
        if (t.Length == 0) return s.Length;

        // Single-row approach
        var prev = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) prev[j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            int prevDiag = prev[0];
            prev[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int temp = prev[j];
                int cost = char.ToLowerInvariant(s[i - 1]) == char.ToLowerInvariant(t[j - 1]) ? 0 : 1;
                prev[j] = Math.Min(Math.Min(prev[j] + 1, prev[j - 1] + 1), prevDiag + cost);
                prevDiag = temp;
            }
        }
        return prev[t.Length];
    }
}
