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

    /// <summary>A parsed reference label, e.g. (Figure, "3.2"). Numbers are normalized to lowercase
    /// (suffix letters, appendix prefixes, roman numerals) so an in-text mention and a caption label
    /// compare equal.</summary>
    internal readonly record struct Reference(RefKind Kind, string Number)
    {
        public override string ToString() => $"{Kind} {Number}";
    }

    /// <summary>A resolved reference target: the figure/table block plus the caption that named it.</summary>
    internal readonly record struct Target(int Page, int TargetBlock, int CaptionBlock);

    // Number forms a figure/table label can take: arabic with an optional appendix-letter prefix and
    // sub-figure suffix ("3", "2.1", "A.4", "4b"), or — case-sensitively, to avoid swallowing common
    // words — uppercase roman numerals / a bare uppercase letter (IEEE "TABLE II", appendix "Figure A").
    private const string NumberPattern = @"(?:(?:[A-Za-z]\.)?\d+(?:\.\d+)*[a-z]?|(?-i:[IVXLC]+|[A-Z]))\b";

    // In-text mentions: "Figure 3", "Fig. 2.1", "Figs 4a", "Table 12", "Tab. 3", "TABLE II".
    [GeneratedRegex(@"\b(?<kind>fig(?:ure)?s?|tab(?:le)?s?)\.?\s*(?<num>" + NumberPattern + ")",
        RegexOptions.IgnoreCase)]
    private static partial Regex LineReferenceRegex();

    // Follow-on numbers of a plural/range mention — "Figures 2 and 3", "Tables 1, 4", "Figs. 3–5"
    // (ranges yield their endpoints). \G anchors each match at the previous one's end. Only applied
    // after a PLURAL kind word: gating on the plural keeps ordinary prose numbers out ("Table 1, 95%
    // of cases" must not produce a phantom Table 95).
    [GeneratedRegex(@"\G\s*(?:,|;|and|&|–|—|to|through|-)\s*(?<num>" + NumberPattern + ")",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationRegex();

    // Caption labels must LEAD the caption text ("Figure 3: ..." / "Tab. 2 — ..."), allowing a little
    // leading punctuation/whitespace noise from text extraction.
    [GeneratedRegex(@"^[\s\p{P}]{0,3}(?<kind>fig(?:ure)?|tab(?:le)?)\.?\s*(?<num>" + NumberPattern + ")",
        RegexOptions.IgnoreCase)]
    private static partial Regex CaptionLabelRegex();

    // A detected Caption block binds to a float only when it overlaps it horizontally within this
    // vertical gap (fraction of page height) — a caption whose own float was misdetected must drop
    // its label rather than bind to an unrelated float across the page.
    private const double CaptionMaxGapFraction = 0.15;
    // A Text-role block only counts as a misclassified caption under a tighter version of the same
    // gate (plus caption-style punctuation, below).
    private const double PseudoCaptionMaxGapFraction = 0.08;

    // Cap on synchronous per-call page-index builds: each build may extract a page's text via PDFium
    // on the UI thread, so an unresolved reference on a large fully-analysed document must not build
    // the whole index in one pass. Resolve reports `incomplete` instead; retries (analysis polls /
    // later line advances) continue from the cached progress, a few pages at a time.
    private const int MaxPageBuildsPerResolve = 8;

    // Per-page parsed caption labels, keyed to the exact PageAnalysis instance they were built from so
    // a re-analysed page (different instance in AnalysisCache) is transparently rebuilt.
    private sealed record PageLabels(PageAnalysis Analysis,
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> Labels);
    private readonly Dictionary<int, PageLabels> _pages = [];

    // Negative cache: references that matched nothing when the index covered N analysed pages. A
    // changed analysed-page count (new background results, or a re-analysis trim) invalidates the
    // entry, so late-arriving captions are still found — but steady re-reads of a dangling mention
    // ("see Figure 99" with no such caption) cost a dictionary probe instead of a full scan.
    private readonly Dictionary<Reference, int> _noMatch = [];

    public void Clear()
    {
        _pages.Clear();
        _noMatch.Clear();
    }

    /// <summary>All figure/table references mentioned in a text run, in reading order. Plural
    /// mentions expand their continuations ("Figures 2 and 3" yields both); singular mentions do
    /// not, so ordinary prose numbers after a reference stay out. Matches starting at or beyond
    /// <paramref name="startLimit"/> are ignored — pass the current line's length when the run is
    /// "current line + next line", so a mention split across the line break is caught without firing
    /// early for mentions that wholly belong to the next line.</summary>
    public static List<Reference> ParseLine(string text, int startLimit = int.MaxValue)
    {
        List<Reference> refs = [];
        foreach (Match m in LineReferenceRegex().Matches(text))
        {
            if (m.Index >= startLimit) break;
            string kindWord = m.Groups["kind"].Value;
            if (!AcceptNumber(kindWord, m.Groups["num"].Value)) continue;
            var kind = char.ToLowerInvariant(kindWord[0]) == 'f' ? RefKind.Figure : RefKind.Table;
            refs.Add(new Reference(kind, NormalizeNumber(m.Groups["num"].Value)));

            if (!kindWord.EndsWith('s') && !kindWord.EndsWith('S')) continue;   // continuations: plural only
            for (int pos = m.Index + m.Length;;)
            {
                var c = ContinuationRegex().Match(text, pos);
                if (!c.Success || !AcceptNumber(kindWord, c.Groups["num"].Value)) break;
                refs.Add(new Reference(kind, NormalizeNumber(c.Groups["num"].Value)));
                pos = c.Index + c.Length;
            }
        }
        return refs;
    }

    /// <summary>The reference a caption's leading label declares ("Figure 3: ..."), or null when the
    /// text does not start with a figure/table label. With <paramref name="requirePunctuation"/>, the
    /// label must also be followed by caption-style punctuation (":", ".", a dash, or end of text) —
    /// used to accept Text-role blocks as captions without swallowing body sentences like
    /// "Figure 3 shows that...". A plain hyphen counts only as a separator ("Fig. 2 - Sample"), not
    /// a compound ("Figure 3-D printed...").</summary>
    internal static Reference? ParseCaptionLabel(string captionText, bool requirePunctuation = false)
    {
        var m = CaptionLabelRegex().Match(captionText);
        if (!m.Success || !AcceptNumber(m.Groups["kind"].Value, m.Groups["num"].Value)) return null;
        if (requirePunctuation)
        {
            int i = m.Index + m.Length;
            while (i < captionText.Length && char.IsWhiteSpace(captionText[i])) i++;
            if (i < captionText.Length)
            {
                char c = captionText[i];
                bool punctuated = c is ':' or '.' or '—' or '–'
                    || (c == '-' && (i + 1 >= captionText.Length || char.IsWhiteSpace(captionText[i + 1])));
                if (!punctuated) return null;
            }
        }
        return new Reference(
            char.ToLowerInvariant(m.Groups["kind"].Value[0]) == 'f' ? RefKind.Figure : RefKind.Table,
            NormalizeNumber(m.Groups["num"].Value));
    }

    /// <summary>Digitless numbers (roman numerals, bare letters) are accepted only after a
    /// capitalised kind word — keeps IEEE "Table II" and appendix "Figure B" while rejecting the
    /// pronoun in "the table I made".</summary>
    private static bool AcceptNumber(string kindWord, string number)
    {
        foreach (char c in number)
            if (char.IsAsciiDigit(c))
                return true;
        return char.IsUpper(kindWord[0]);
    }

    private static string NormalizeNumber(string number) => number.ToLowerInvariant();

    /// <summary>Resolve a reference to its defining caption + figure/table block, scanning analysed
    /// pages outward from <paramref name="nearPage"/> (nearest match wins; the preceding page is tried
    /// before the following one at each distance, since a referenced float most often sits before or
    /// at the reference). Returns null when no analysed page carries the label;
    /// <paramref name="incomplete"/> is true when the scan ran out of its per-call page-build budget
    /// before covering every analysed page — the caller should retry (progress is cached). A
    /// complete miss is negative-cached until the set of analysed pages changes.</summary>
    public Target? Resolve(DocumentState doc, Reference reference, int nearPage, out bool incomplete)
    {
        incomplete = false;
        int analysed = doc.AnalysisCache.Count;
        if (_noMatch.TryGetValue(reference, out int seenAt) && seenAt == analysed)
            return null;

        int budget = MaxPageBuildsPerResolve;
        int pageCount = doc.PageCount;
        for (int d = 0; d < pageCount; d++)
        {
            int before = nearPage - d, after = nearPage + d;
            if (before >= 0 && FindOnPage(doc, before, reference, ref budget, ref incomplete) is { } t1)
                return t1;
            if (d > 0 && after < pageCount && FindOnPage(doc, after, reference, ref budget, ref incomplete) is { } t2)
                return t2;
        }
        if (!incomplete)
            _noMatch[reference] = analysed;
        return null;
    }

    private Target? FindOnPage(DocumentState doc, int page, Reference reference,
        ref int budget, ref bool incomplete)
    {
        if (!doc.AnalysisCache.TryGetValue(page, out var analysis)) return null;
        if (!_pages.TryGetValue(page, out var entry) || !ReferenceEquals(entry.Analysis, analysis))
        {
            if (budget <= 0)
            {
                incomplete = true;   // not built yet and out of budget — a later call picks it up
                return null;
            }
            budget--;
            _pages[page] = entry = BuildPage(doc, page, analysis);
        }
        foreach (var (r, target, caption) in entry.Labels)
            if (r == reference)
                return new Target(page, target, caption);
        return null;
    }

    /// <summary>Parse every caption block on a page into (label → adjacent figure/table) entries.
    /// Detected <see cref="BlockRole.Caption"/> blocks are taken first; a second pass accepts
    /// Text-role blocks that read like a caption AND hug a float, covering detector misclassification.
    /// Both passes require the float to overlap the caption horizontally within a bounded vertical
    /// gap, so a label whose own float was misdetected is dropped rather than bound to an unrelated
    /// block across the page. Text extraction is cached per page (DocumentState.GetOrExtractText),
    /// so rebuilds after the first touch are regex + geometry only.</summary>
    private static PageLabels BuildPage(DocumentState doc, int page, PageAnalysis analysis)
    {
        List<(Reference Ref, int TargetBlock, int CaptionBlock)> labels = [];
        PageText? pageText = null;
        PageText Text() => pageText ??= doc.GetOrExtractText(page);

        double captionMaxGap = analysis.PageHeight * CaptionMaxGapFraction;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (analysis.Blocks[i].Role != BlockRole.Caption) continue;
            if (ParseCaptionLabel(Text().ExtractBlockText(analysis.Blocks[i])) is not { } reference)
                continue;
            var (target, gap, overlapX) = FindNearestTarget(analysis, i, reference.Kind);
            if (target >= 0 && overlapX && gap <= captionMaxGap)
                labels.Add((reference, target, i));
        }

        double pseudoMaxGap = analysis.PageHeight * PseudoCaptionMaxGapFraction;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (analysis.Blocks[i].Role != BlockRole.Text) continue;
            // Geometry gate first (cheap): must hug a float of either kind before any text work.
            var fig = FindNearestTarget(analysis, i, RefKind.Figure);
            var tab = FindNearestTarget(analysis, i, RefKind.Table);
            bool nearFig = fig.Index >= 0 && fig.OverlapX && fig.Gap <= pseudoMaxGap;
            bool nearTab = tab.Index >= 0 && tab.OverlapX && tab.Gap <= pseudoMaxGap;
            if (!nearFig && !nearTab) continue;

            if (ParseCaptionLabel(Text().ExtractBlockText(analysis.Blocks[i]), requirePunctuation: true)
                is not { } reference)
                continue;
            if (reference.Kind == RefKind.Figure ? !nearFig : !nearTab) continue;   // kind must match the hugged float
            if (labels.Any(l => l.Ref == reference)) continue;                      // detected captions win
            labels.Add((reference, reference.Kind == RefKind.Figure ? fig.Index : tab.Index, i));
        }

        return new PageLabels(analysis, labels);
    }

    /// <summary>Back-compat shim for tests: index of the nearest kind-matching float.</summary>
    internal static int NearestTargetBlock(PageAnalysis analysis, int captionIndex, RefKind kind)
        => FindNearestTarget(analysis, captionIndex, kind).Index;

    /// <summary>The figure/table block a caption belongs to: the role-matching block with the smallest
    /// vertical gap to the caption, preferring blocks that overlap it horizontally (captions sit
    /// directly above or below their float; the no-overlap fallback covers side captions and detector
    /// quirks). Index is -1 when the page has no candidate block of the right kind; Gap/OverlapX
    /// describe the chosen candidate.</summary>
    internal static (int Index, double Gap, bool OverlapX) FindNearestTarget(
        PageAnalysis analysis, int captionIndex, RefKind kind)
    {
        var cap = analysis.Blocks[captionIndex].BBox;
        int best = -1;
        double bestGap = 0;
        bool bestOverlap = false;
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
                bestGap = gap;
                bestOverlap = overlapX;
            }
        }
        return (best, bestGap, bestOverlap);
    }
}
