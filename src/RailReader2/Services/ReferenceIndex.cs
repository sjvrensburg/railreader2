using System.Text.RegularExpressions;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader2.Services;

/// <summary>
/// Resolves in-text references like "Figure 3" / "Table 2.1" to the detected figure/table block they
/// name, by parsing the leading label of <see cref="BlockRole.Caption"/> blocks across analysed pages
/// and associating each caption with its adjacent figure/table. Backs the automatic portal pinning:
/// the portal sync loop parses the current rail line for references and shows the resolved target.
/// Per-document; UI thread only (text extraction goes through PDFium-backed caches).
/// </summary>
internal sealed partial class ReferenceIndex
{
    internal enum RefKind { Figure, Table }

    /// <summary>A parsed reference label, e.g. (Figure, "3.2"). Numbers are normalized (lowercase
    /// suffix letter, no trailing dot) so an in-text mention and a caption label compare equal.</summary>
    internal readonly record struct Reference(RefKind Kind, string Number)
    {
        public override string ToString() => $"{Kind} {Number}";
    }

    /// <summary>A resolved reference target: the figure/table block plus the caption that named it.</summary>
    internal readonly record struct Target(int Page, int TargetBlock, int CaptionBlock);

    // In-text mentions: "Figure 3", "Fig. 2.1", "Figs 4a", "Table 12", "Tab. 3". Case-insensitive;
    // the optional letter suffix covers sub-figure references ("Figure 4b").
    [GeneratedRegex(@"\b(?<kind>fig(?:ure)?s?|tab(?:le)?s?)\.?\s*(?<num>\d+(?:\.\d+)*[a-z]?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex LineReferenceRegex();

    // Caption labels must LEAD the caption text ("Figure 3: ..." / "Tab. 2 — ..."), allowing a little
    // leading punctuation/whitespace noise from text extraction.
    [GeneratedRegex(@"^[\s\p{P}]{0,3}(?<kind>fig(?:ure)?|tab(?:le)?)\.?\s*(?<num>\d+(?:\.\d+)*[a-z]?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CaptionLabelRegex();

    // Per-page parsed caption labels, keyed to the exact PageAnalysis instance they were built from so
    // a re-analysed page (different instance in AnalysisCache) is transparently rebuilt.
    private sealed record PageLabels(PageAnalysis Analysis,
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> Labels);
    private readonly Dictionary<int, PageLabels> _pages = [];

    public void Clear() => _pages.Clear();

    /// <summary>All figure/table references mentioned in a line of text, in reading order.</summary>
    public static List<Reference> ParseLine(string text)
    {
        List<Reference> refs = [];
        foreach (Match m in LineReferenceRegex().Matches(text))
            refs.Add(MakeReference(m));
        return refs;
    }

    /// <summary>The reference a caption's leading label declares ("Figure 3: ..."), or null when the
    /// text does not start with a figure/table label.</summary>
    internal static Reference? ParseCaptionLabel(string captionText)
    {
        var m = CaptionLabelRegex().Match(captionText);
        return m.Success ? MakeReference(m) : null;
    }

    private static Reference MakeReference(Match m)
    {
        var kind = char.ToLowerInvariant(m.Groups["kind"].Value[0]) == 'f' ? RefKind.Figure : RefKind.Table;
        return new Reference(kind, m.Groups["num"].Value.ToLowerInvariant());
    }

    /// <summary>Resolve a reference to its defining caption + figure/table block, scanning analysed
    /// pages outward from <paramref name="nearPage"/> (nearest match wins; the preceding page is tried
    /// before the following one at each distance, since a referenced float most often sits before or
    /// at the reference). Returns null when no analysed page carries the label yet — the caller may
    /// retry as background analysis covers more pages.</summary>
    public Target? Resolve(DocumentState doc, Reference reference, int nearPage)
    {
        int pageCount = doc.PageCount;
        for (int d = 0; d < pageCount; d++)
        {
            int before = nearPage - d, after = nearPage + d;
            if (before >= 0 && FindOnPage(doc, before, reference) is { } t1) return t1;
            if (d > 0 && after < pageCount && FindOnPage(doc, after, reference) is { } t2) return t2;
        }
        return null;
    }

    private Target? FindOnPage(DocumentState doc, int page, Reference reference)
    {
        if (!doc.AnalysisCache.TryGetValue(page, out var analysis)) return null;
        if (!_pages.TryGetValue(page, out var entry) || !ReferenceEquals(entry.Analysis, analysis))
            _pages[page] = entry = BuildPage(doc, page, analysis);
        foreach (var (r, target, caption) in entry.Labels)
            if (r == reference)
                return new Target(page, target, caption);
        return null;
    }

    /// <summary>Parse every caption block on a page into (label → adjacent figure/table) entries.
    /// Text extraction is cached per page (DocumentState.GetOrExtractText), so rebuilds after the
    /// first touch are regex + geometry only.</summary>
    private static PageLabels BuildPage(DocumentState doc, int page, PageAnalysis analysis)
    {
        List<(Reference, int, int)> labels = [];
        PageText? pageText = null;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (analysis.Blocks[i].Role != BlockRole.Caption) continue;
            pageText ??= doc.GetOrExtractText(page);
            if (ParseCaptionLabel(pageText.ExtractBlockText(analysis.Blocks[i])) is not { } reference)
                continue;
            int target = NearestTargetBlock(analysis, i, reference.Kind);
            if (target >= 0)
                labels.Add((reference, target, i));
        }
        return new PageLabels(analysis, labels);
    }

    /// <summary>The figure/table block a caption belongs to: the role-matching block with the smallest
    /// vertical gap to the caption, preferring blocks that overlap it horizontally (captions sit
    /// directly above or below their float; the no-overlap fallback covers side captions and detector
    /// quirks). -1 when the page has no candidate block of the right kind.</summary>
    internal static int NearestTargetBlock(PageAnalysis analysis, int captionIndex, RefKind kind)
    {
        var cap = analysis.Blocks[captionIndex].BBox;
        int best = -1;
        double bestScore = double.MaxValue;
        for (int j = 0; j < analysis.Blocks.Count; j++)
        {
            if (j == captionIndex) continue;
            var role = analysis.Blocks[j].Role;
            bool kindMatch = kind == RefKind.Figure
                ? role is BlockRole.Figure or BlockRole.Chart
                : role is BlockRole.Table;
            if (!kindMatch) continue;

            var b = analysis.Blocks[j].BBox;
            double gap = Math.Max(0, Math.Max(cap.Y - (b.Y + b.H), b.Y - (cap.Y + cap.H)));
            bool overlapX = Math.Min(cap.X + cap.W, b.X + b.W) > Math.Max(cap.X, b.X);
            // Horizontally-overlapping candidates always beat non-overlapping ones; within each tier,
            // the smallest vertical gap wins.
            double score = gap + (overlapX ? 0 : 1e6);
            if (score < bestScore)
            {
                bestScore = score;
                best = j;
            }
        }
        return best;
    }
}
