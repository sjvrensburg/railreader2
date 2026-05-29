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
    /// Flattens the outline tree internally — use <see cref="ResolveWithFlatOutline"/>
    /// to avoid repeated flattening across pages.
    /// </summary>
    public static Dictionary<int, int> Resolve(
        IReadOnlyList<LayoutBlock> blocks,
        PageText? pageText,
        IReadOnlyList<OutlineEntry> outline,
        int pageIndex)
    {
        var flatOutline = FlattenOutline(outline);

        var blockTexts = new Dictionary<int, string>();
        if (pageText != null)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                var text = pageText.ExtractBlockText(blocks[i]);
                if (!string.IsNullOrEmpty(text))
                    blockTexts[i] = text;
            }
        }

        return ResolveWithFlatOutline(blocks, blockTexts, flatOutline, pageIndex);
    }

    /// <summary>
    /// Resolves heading levels using a pre-flattened outline and pre-extracted block texts.
    /// Avoids redundant outline flattening and text extraction when called per-page.
    /// </summary>
    public static Dictionary<int, int> ResolveWithFlatOutline(
        IReadOnlyList<LayoutBlock> blocks,
        IReadOnlyDictionary<int, string> blockTexts,
        IReadOnlyList<FlatOutlineEntry> flatOutline,
        int pageIndex)
    {
        var pageEntries = flatOutline
            .Where(e => e.Page == pageIndex)
            .ToList();
        return ResolveForPage(blocks, blockTexts, pageEntries);
    }

    /// <summary>
    /// Resolves heading levels using the outline entries already filtered to this
    /// page. Callers that process many pages should pre-bucket the flat outline via
    /// <see cref="BucketByPage"/> once and pass the per-page list here, avoiding the
    /// O(pages × outline) re-scan that <see cref="ResolveWithFlatOutline"/> performs.
    /// </summary>
    public static Dictionary<int, int> ResolveForPage(
        IReadOnlyList<LayoutBlock> blocks,
        IReadOnlyDictionary<int, string> blockTexts,
        IReadOnlyList<FlatOutlineEntry> pageEntries)
    {
        var result = new Dictionary<int, int>();

        for (int i = 0; i < blocks.Count; i++)
        {
            var role = blocks[i].Role;
            if (role is not (BlockRole.Title or BlockRole.Heading))
                continue;

            blockTexts.TryGetValue(i, out var blockText);
            int? matchedDepth = null;

            if (!string.IsNullOrWhiteSpace(blockText) && pageEntries.Count > 0)
                matchedDepth = FuzzyMatchOutline(blockText, pageEntries);

            if (matchedDepth.HasValue)
                result[i] = Math.Clamp(matchedDepth.Value, 1, 6);
            else
                result[i] = role == BlockRole.Title ? 1 : 2;
        }

        return result;
    }

    public record FlatOutlineEntry(string Title, int? Page, int Depth)
    {
        /// <summary>Whitespace-collapsed, lowercased title — computed once for fuzzy matching.</summary>
        internal string Normalized { get; } = NormalizeForMatch(Title);
    }

    internal static List<FlatOutlineEntry> FlattenOutline(IReadOnlyList<OutlineEntry> entries)
    {
        var result = new List<FlatOutlineEntry>();
        FlattenRecursive(entries, 1, result);
        return result;
    }

    /// <summary>
    /// Buckets a flattened outline by page index so per-page heading resolution
    /// is an O(1) lookup instead of a full re-scan per page.
    /// </summary>
    internal static Dictionary<int, List<FlatOutlineEntry>> BucketByPage(IReadOnlyList<FlatOutlineEntry> flatOutline)
    {
        var buckets = new Dictionary<int, List<FlatOutlineEntry>>();
        foreach (var entry in flatOutline)
        {
            if (entry.Page is not { } page) continue;
            if (!buckets.TryGetValue(page, out var list))
                buckets[page] = list = new List<FlatOutlineEntry>();
            list.Add(entry);
        }
        return buckets;
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

        // Outline titles are pre-normalized (lowercased + whitespace-collapsed) on
        // FlatOutlineEntry, so both sides are already lowercase — use Ordinal.

        // Try exact containment first
        foreach (var entry in pageEntries)
        {
            var entryNorm = entry.Normalized;
            if (entryNorm.Length == 0) continue;
            if (normalized.Contains(entryNorm, StringComparison.Ordinal) ||
                entryNorm.Contains(normalized, StringComparison.Ordinal))
            {
                return entry.Depth;
            }
        }

        // Try Levenshtein similarity (threshold: 80% of longer string)
        int? bestDepth = null;
        double bestSimilarity = 0;

        foreach (var entry in pageEntries)
        {
            var entryNorm = entry.Normalized;
            if (entryNorm.Length == 0) continue;

            int maxLen = Math.Max(normalized.Length, entryNorm.Length);
            // distance >= |lenA - lenB|, so similarity can only clear 0.8 when the
            // length gap is within 20% of the longer string. Skip the O(n·m) DP otherwise.
            if (Math.Abs(normalized.Length - entryNorm.Length) > 0.2 * maxLen)
                continue;

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
                chars[len++] = char.ToLowerInvariant(ch);
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

        var prev = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) prev[j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            int prevDiag = prev[0];
            prev[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int temp = prev[j];
                // Inputs are pre-lowercased by NormalizeForMatch; compare directly.
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                prev[j] = Math.Min(Math.Min(prev[j] + 1, prev[j - 1] + 1), prevDiag + cost);
                prevDiag = temp;
            }
        }
        return prev[t.Length];
    }
}
