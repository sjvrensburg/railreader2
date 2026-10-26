using RailReader.Core.Models;

namespace RailReader2.References;

/// <summary>One distinct figure/table mention found in running text, with how the resolver handled it.</summary>
internal sealed class MentionResult
{
    public int Page { get; set; }
    public int Block { get; set; }
    public int Line { get; set; }
    public string Kind { get; set; } = "";
    public string Number { get; set; } = "";
    /// <summary>"resolved" or "abstained".</summary>
    public string Status { get; set; } = "abstained";
    public int? TargetPage { get; set; }
    public int? TargetBlock { get; set; }
    public int? CaptionBlock { get; set; }
    /// <summary>Scoring outcome when ground truth is supplied: tp, fp, tn, unlabelled.</summary>
    public string? Outcome { get; set; }
}

/// <summary>Ground-truth label: a mention and the block it must resolve to. Omitting the target
/// means the resolver must abstain (a dangling or ambiguous reference).</summary>
internal sealed class LabelledMention
{
    public int Page { get; set; }
    public string Kind { get; set; } = "";
    public string Number { get; set; } = "";
    public int? TargetPage { get; set; }
    public int? TargetBlock { get; set; }
    /// <summary>Further blocks on <see cref="TargetPage"/> that also count as correct, for floats the
    /// layout model splits into several blocks (multi-panel figures, a colour-bar strip).</summary>
    public List<int> AlsoAcceptBlocks { get; set; } = [];
}

internal sealed class GroundTruth
{
    public List<LabelledMention> Mentions { get; set; } = [];
    /// <summary>Pages that must produce no resolved reference at all (hard negatives).</summary>
    public List<int> EmptyPages { get; set; } = [];
}

internal sealed class Metrics
{
    public int TruePositives { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }
    public int CorrectAbstentions { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1 { get; set; }
    /// <summary>Share of labelled mentions the resolver declined to answer.</summary>
    public double AbstentionRate { get; set; }
}

internal sealed class BenchmarkMetrics
{
    public Metrics Overall { get; set; } = new();
    public Dictionary<string, Metrics> ByKind { get; set; } = [];
}

internal sealed class ReferenceReport
{
    public string Source { get; set; } = "";
    public string? Layout { get; set; }
    public int PageCount { get; set; }
    public List<MentionResult> Mentions { get; set; } = [];
    public BenchmarkMetrics? Metrics { get; set; }
    public Dictionary<string, BenchmarkMetrics> Baselines { get; set; } = [];
    public List<string> Failures { get; set; } = [];
}

internal static class ReferenceBenchmark
{
    /// <summary>Find every distinct figure/table mention on the document's analysed pages (scanning
    /// lines the way the rail's auto-pin does) and resolve each. One result per (page, kind, number),
    /// located at its first occurrence.</summary>
    internal static List<MentionResult> Scan(IReferenceDocument doc, ReferenceIndex index)
    {
        var results = new List<MentionResult>();
        var seen = new HashSet<(int, ReferenceIndex.Reference)>();
        for (int page = 0; page < doc.PageCount; page++)
        {
            if (!doc.TryGetAnalysis(page, out var analysis)) continue;
            PageText? text = null;
            for (int b = 0; b < analysis.Blocks.Count; b++)
            {
                var block = analysis.Blocks[b];
                if (block.Role == BlockRole.Caption) continue;
                for (int l = 0; l < block.Lines.Count; l++)
                {
                    text ??= doc.GetText(page);
                    string? lineText = ReferenceIndex.LineTextWithLookahead(text, block.Lines, l, out int limit);
                    if (lineText is null) continue;
                    foreach (var reference in ReferenceIndex.ParseLine(lineText, limit))
                    {
                        if (!seen.Add((page, reference))) continue;
                        var target = ResolveFully(index, doc, reference, page);
                        // Reading the (pseudo-)caption itself isn't a mention of it.
                        if (target is { } self && self.Page == page && self.CaptionBlock == b) continue;
                        results.Add(ToResult(page, b, l, reference, target));
                    }
                }
            }
        }
        return results;
    }

    private static ReferenceIndex.Target? ResolveFully(ReferenceIndex index, IReferenceDocument doc,
        ReferenceIndex.Reference reference, int page)
    {
        // Resolve builds a bounded number of page indexes per call; keep asking until it is complete.
        for (int i = 0; i <= doc.PageCount; i++)
        {
            var t = index.Resolve(doc, reference, page, out bool incomplete);
            if (t is not null || !incomplete) return t;
        }
        return null;
    }

    private static MentionResult ToResult(int page, int block, int line,
        ReferenceIndex.Reference reference, ReferenceIndex.Target? target) => new()
    {
        Page = page, Block = block, Line = line,
        Kind = reference.Kind.ToString(), Number = reference.Number,
        Status = target is null ? "abstained" : "resolved",
        TargetPage = target?.Page, TargetBlock = target?.TargetBlock, CaptionBlock = target?.CaptionBlock,
    };

    // --- Baselines: same detected mentions, naive resolution -----------------------------------

    /// <summary>Resolve each mention with no geometry gate and no abstention: take the caption with a
    /// matching label (first in the document, or nearest page) and its nearest float.</summary>
    internal static List<MentionResult> NaiveBaseline(IReferenceDocument doc, List<MentionResult> mentions,
        bool nearest)
    {
        var captions = new List<(ReferenceIndex.Reference Ref, int Page, int Float, int Caption)>();
        for (int page = 0; page < doc.PageCount; page++)
        {
            if (!doc.TryGetAnalysis(page, out var analysis)) continue;
            var text = doc.GetText(page);
            for (int i = 0; i < analysis.Blocks.Count; i++)
            {
                if (analysis.Blocks[i].Role != BlockRole.Caption) continue;
                if (ReferenceIndex.ParseCaptionLabel(text.ExtractBlockText(analysis.Blocks[i])) is not { } r) continue;
                int target = ReferenceIndex.FindNearestTarget(analysis, i, r.Kind).Index;
                if (target >= 0) captions.Add((r, page, target, i));
            }
        }

        var output = new List<MentionResult>();
        foreach (var m in mentions)
        {
            var reference = new ReferenceIndex.Reference(Enum.Parse<ReferenceIndex.RefKind>(m.Kind), m.Number);
            var matches = captions.Where(c => c.Ref == reference);
            var pick = nearest
                ? matches.OrderBy(c => Math.Abs(c.Page - m.Page)).ThenBy(c => c.Page > m.Page).Cast<(ReferenceIndex.Reference Ref, int Page, int Float, int Caption)?>().FirstOrDefault()
                : matches.Cast<(ReferenceIndex.Reference Ref, int Page, int Float, int Caption)?>().FirstOrDefault();
            output.Add(new MentionResult
            {
                Page = m.Page, Block = m.Block, Line = m.Line, Kind = m.Kind, Number = m.Number,
                Status = pick is null ? "abstained" : "resolved",
                TargetPage = pick?.Page, TargetBlock = pick?.Float, CaptionBlock = pick?.Caption,
            });
        }
        return output;
    }

    // --- Scoring -------------------------------------------------------------------------------

    /// <summary>Score predictions against ground truth, setting <see cref="MentionResult.Outcome"/>
    /// and appending a line to <paramref name="failures"/> for every miss. Unlabelled predictions
    /// count against precision only on "complete" pages (those with a label or listed empty).</summary>
    internal static BenchmarkMetrics Score(List<MentionResult> predictions, GroundTruth truth, List<string>? failures)
    {
        var complete = new HashSet<int>(truth.EmptyPages);
        foreach (var l in truth.Mentions) complete.Add(l.Page);

        var byKey = predictions.ToDictionary(p => (p.Page, Norm(p.Kind), p.Number.ToLowerInvariant()));
        var counts = new Dictionary<string, int[]>();   // tp, fp, fn, tn, labelled, labelled-and-answered
        int[] Row(string kind) => counts.TryGetValue(kind, out var r) ? r : counts[kind] = new int[6];
        int[] overall = new int[6];
        void Bump(string kind, int idx) { Row(kind)[idx]++; overall[idx]++; }

        var labelled = new HashSet<(int, string, string)>();
        foreach (var l in truth.Mentions)
        {
            string kind = Norm(l.Kind);
            var key = (l.Page, kind, l.Number.ToLowerInvariant());
            labelled.Add(key);
            Bump(kind, 4);
            byKey.TryGetValue(key, out var p);
            bool resolved = p?.Status == "resolved";
            if (resolved) Bump(kind, 5);

            if (l.TargetPage is null)   // must abstain
            {
                if (resolved) { Bump(kind, 1); p!.Outcome = "fp"; failures?.Add($"p{l.Page} {kind} {l.Number}: resolved to p{p.TargetPage}/b{p.TargetBlock}, expected abstention"); }
                else { Bump(kind, 3); if (p != null) p.Outcome = "tn"; }
                continue;
            }

            if (resolved && p!.TargetPage == l.TargetPage
                && (p.TargetBlock == l.TargetBlock || l.AlsoAcceptBlocks.Contains(p.TargetBlock ?? -1)))
            {
                Bump(kind, 0); p.Outcome = "tp";
            }
            else if (resolved)
            {
                Bump(kind, 1); Bump(kind, 2); p!.Outcome = "fp";
                failures?.Add($"p{l.Page} {kind} {l.Number}: resolved to p{p.TargetPage}/b{p.TargetBlock}, expected p{l.TargetPage}/b{l.TargetBlock}");
            }
            else
            {
                Bump(kind, 2);
                failures?.Add($"p{l.Page} {kind} {l.Number}: {(p is null ? "mention not detected" : "abstained")}, expected p{l.TargetPage}/b{l.TargetBlock}");
            }
        }

        foreach (var p in predictions)
        {
            if (labelled.Contains((p.Page, Norm(p.Kind), p.Number.ToLowerInvariant()))) continue;
            if (p.Status == "resolved" && complete.Contains(p.Page))
            {
                Bump(Norm(p.Kind), 1); p.Outcome = "fp";
                failures?.Add($"p{p.Page} {p.Kind} {p.Number}: unlabelled mention resolved to p{p.TargetPage}/b{p.TargetBlock}");
            }
            else p.Outcome = "unlabelled";
        }

        var metrics = new BenchmarkMetrics { Overall = ToMetrics(overall) };
        foreach (var (kind, row) in counts) metrics.ByKind[kind] = ToMetrics(row);
        return metrics;
    }

    private static string Norm(string kind) => kind.ToLowerInvariant() switch
    {
        "figure" => "Figure",
        "table" => "Table",
        _ => kind,
    };

    private static Metrics ToMetrics(int[] r)
    {
        int tp = r[0], fp = r[1], fn = r[2], tn = r[3], labelled = r[4];
        double precision = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
        double recall = tp + fn == 0 ? 1 : (double)tp / (tp + fn);
        double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new Metrics
        {
            TruePositives = tp, FalsePositives = fp, FalseNegatives = fn, CorrectAbstentions = tn,
            Precision = Math.Round(precision, 4), Recall = Math.Round(recall, 4), F1 = Math.Round(f1, 4),
            AbstentionRate = labelled == 0 ? 0 : Math.Round(1 - (double)r[5] / labelled, 4),
        };
    }
}
