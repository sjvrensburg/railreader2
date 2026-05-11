using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// XY-Cut++ reading-order sorter — recursive geometric partitioning that handles
/// multi-column layouts and adaptive horizontal-vs-vertical axis selection.
///
/// Port of XYCutPlusPlusSorter (Apache 2.0, Hancom Inc.) from the
/// opendataloader-pdf project, adapted to screen-style Y-down coordinates.
/// Source: github.com/opendataloader-project/opendataloader-pdf
/// Reference: arXiv:2504.10258
/// </summary>
internal static class XYCutSorter
{
    // 0.7 = element must be ≥70% as wide/tall as the widest/tallest element to
    // qualify for cross-layout pre-mask. Java reference defaults to 2.0 (off);
    // we enable at 0.7 because academic papers reliably have full-width titles
    // and page-margin vertical text that break naive XY-cut.
    private const double DefaultBeta = 0.7;
    private const double DefaultDensityThreshold = 0.9;
    private const double OverlapThreshold = 0.1;
    private const int MinOverlapCount = 2;
    // Minimum gap in PDF points to count as a cut. ICASSP/IEEE conference
    // templates use ~4pt column gaps, so 5pt (the Java reference's default)
    // misses them — verified on Distribution-Aware NAMs (ICASSP 2026) where
    // the left/right column gap is 4.3pt. 2pt is conservative enough to still
    // ignore measurement noise (sub-pixel detection box jitter) but catches
    // real layout boundaries.
    private const double MinGapThreshold = 2.0;            // PDF points
    private const double NarrowElementWidthRatio = 0.1;
    // Tall-narrow margin element (e.g. rotated conference info on IEEE/ICASSP
    // papers): nearly as tall as the tallest block but ≤10% the widest block.
    private const double TallNarrowHeightRatio = 0.7;
    private const double TallNarrowWidthRatio = 0.1;
    // Wide cross-layout elements must also be short: titles/headers are wide
    // but only a few lines tall. Without this, every column body block whose
    // width matches the widest block on the page would be (wrongly) caught.
    private const double WideShortHeightRatio = 0.3;

    private readonly record struct CutInfo(double Position, double Gap);

    /// <summary>
    /// Sort blocks in reading order and assign sequential <see cref="LayoutBlock.Order"/>
    /// values 0..N-1. The list itself is reordered to match.
    /// </summary>
    public static void SortInPlace(List<LayoutBlock> blocks)
    {
        if (blocks.Count == 0) return;
        if (blocks.Count == 1)
        {
            blocks[0].Order = 0;
            return;
        }

        var sorted = SortRecursive(blocks, DefaultBeta, DefaultDensityThreshold);
        for (int i = 0; i < sorted.Count; i++) sorted[i].Order = i;
        blocks.Clear();
        blocks.AddRange(sorted);
    }

    internal static List<LayoutBlock> Sort(IReadOnlyList<LayoutBlock> blocks)
        => SortRecursive(blocks, DefaultBeta, DefaultDensityThreshold);

    private static List<LayoutBlock> SortRecursive(
        IReadOnlyList<LayoutBlock> blocks, double beta, double densityThreshold)
    {
        // Phase 1: pre-mask cross-layout elements
        var crossLayout = IdentifyCrossLayoutElements(blocks, beta);
        List<LayoutBlock> remaining;
        if (crossLayout.Count > 0)
        {
            remaining = new List<LayoutBlock>(blocks.Count - crossLayout.Count);
            foreach (var b in blocks)
                if (!crossLayout.Contains(b)) remaining.Add(b);
        }
        else
        {
            remaining = new List<LayoutBlock>(blocks);
        }

        if (remaining.Count == 0)
            return SortByYThenX(blocks);

        // Phase 2: density ratio guides initial axis preference
        double density = ComputeDensityRatio(remaining);
        bool preferHorizontalFirst = density > densityThreshold;

        // Phase 3: recursive segmentation
        var sortedMain = RecursiveSegment(remaining, preferHorizontalFirst);

        // Phase 4: merge cross-layout elements back in by Y position
        return MergeCrossLayoutElements(sortedMain, crossLayout);
    }

    // ===== Phase 1: cross-layout detection =====

    private static List<LayoutBlock> IdentifyCrossLayoutElements(
        IReadOnlyList<LayoutBlock> blocks, double beta)
    {
        var result = new List<LayoutBlock>();
        if (blocks.Count < 3) return result;

        float maxWidth = 0, maxHeight = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            maxWidth = Math.Max(maxWidth, blocks[i].BBox.W);
            maxHeight = Math.Max(maxHeight, blocks[i].BBox.H);
        }

        double widthThreshold = beta * maxWidth;
        double shortThreshold = WideShortHeightRatio * maxHeight;
        double tallThreshold = TallNarrowHeightRatio * maxHeight;
        double narrowThreshold = TallNarrowWidthRatio * maxWidth;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i].BBox;
            // Wide cross-layout: full-width title / header / footer. Must be
            // wide AND short (titles span a few lines, not the whole column)
            // AND overlap horizontally with ≥2 other elements (rules out
            // single full-width header above an empty page).
            bool isWide = b.W >= widthThreshold
                && b.H <= shortThreshold
                && HasMinimumHorizontalOverlaps(blocks[i], blocks, MinOverlapCount);
            // Tall-narrow cross-layout: page-margin vertical text.
            // Must be both nearly as tall as the tallest block AND much
            // narrower than the widest block, with ≥2 vertical overlaps.
            bool isTallAndNarrow = !isWide
                && b.H >= tallThreshold
                && b.W <= narrowThreshold
                && HasMinimumVerticalOverlaps(blocks[i], blocks, MinOverlapCount);
            if (isWide || isTallAndNarrow)
                result.Add(blocks[i]);
        }
        return result;
    }

    private static bool HasMinimumHorizontalOverlaps(LayoutBlock element,
        IReadOnlyList<LayoutBlock> blocks, int minCount)
    {
        int count = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (ReferenceEquals(blocks[i], element)) continue;
            if (HorizontalOverlapRatio(element.BBox, blocks[i].BBox) >= OverlapThreshold
                && ++count >= minCount) return true;
        }
        return false;
    }

    private static bool HasMinimumVerticalOverlaps(LayoutBlock element,
        IReadOnlyList<LayoutBlock> blocks, int minCount)
    {
        int count = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (ReferenceEquals(blocks[i], element)) continue;
            if (VerticalOverlapRatio(element.BBox, blocks[i].BBox) >= OverlapThreshold
                && ++count >= minCount) return true;
        }
        return false;
    }

    private static double HorizontalOverlapRatio(BBox a, BBox b)
    {
        float overlapLeft = Math.Max(a.X, b.X);
        float overlapRight = Math.Min(a.X + a.W, b.X + b.W);
        float overlapW = Math.Max(0, overlapRight - overlapLeft);
        if (overlapW <= 0) return 0;
        float smaller = Math.Min(a.W, b.W);
        return smaller > 0 ? overlapW / smaller : 0;
    }

    private static double VerticalOverlapRatio(BBox a, BBox b)
    {
        float overlapTop = Math.Max(a.Y, b.Y);
        float overlapBottom = Math.Min(a.Y + a.H, b.Y + b.H);
        float overlapH = Math.Max(0, overlapBottom - overlapTop);
        if (overlapH <= 0) return 0;
        float smaller = Math.Min(a.H, b.H);
        return smaller > 0 ? overlapH / smaller : 0;
    }

    // ===== Phase 2: density ratio =====

    private static double ComputeDensityRatio(IReadOnlyList<LayoutBlock> blocks)
    {
        if (blocks.Count == 0) return 1.0;
        if (!TryBoundingRegion(blocks, out var region)) return 1.0;
        double area = (double)region.W * region.H;
        if (area <= 0) return 1.0;
        double content = 0;
        for (int i = 0; i < blocks.Count; i++)
            content += (double)blocks[i].BBox.W * blocks[i].BBox.H;
        return Math.Min(1.0, content / area);
    }

    private static bool TryBoundingRegion(IReadOnlyList<LayoutBlock> blocks, out BBox region)
    {
        if (blocks.Count == 0) { region = default; return false; }
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i].BBox;
            if (b.X < minX) minX = b.X;
            if (b.Y < minY) minY = b.Y;
            if (b.X + b.W > maxX) maxX = b.X + b.W;
            if (b.Y + b.H > maxY) maxY = b.Y + b.H;
        }
        region = new BBox(minX, minY, maxX - minX, maxY - minY);
        return region.W > 0 && region.H > 0;
    }

    // ===== Phase 3: recursive segmentation =====

    private static List<LayoutBlock> RecursiveSegment(
        List<LayoutBlock> blocks, bool preferHorizontalFirst)
    {
        if (blocks.Count <= 1) return new List<LayoutBlock>(blocks);

        var horizontal = FindBestHorizontalCut(blocks);
        var vertical = FindBestVerticalCut(blocks);

        bool validH = horizontal.Gap >= MinGapThreshold;
        bool validV = vertical.Gap >= MinGapThreshold;

        bool useHorizontal;
        if (validH && validV) useHorizontal = horizontal.Gap > vertical.Gap;
        else if (validH) useHorizontal = true;
        else if (validV) useHorizontal = false;
        else return SortByYThenX(blocks);

        var (groupA, groupB) = useHorizontal
            ? SplitByHorizontalCut(blocks, horizontal.Position)
            : SplitByVerticalCut(blocks, vertical.Position);

        // Safety: if the split couldn't separate any block, fall back to y-sort
        // rather than recursing on the same set and looping forever.
        if (groupA.Count == 0 || groupB.Count == 0)
            return SortByYThenX(blocks);

        var result = new List<LayoutBlock>(blocks.Count);
        result.AddRange(RecursiveSegment(groupA, preferHorizontalFirst));
        result.AddRange(RecursiveSegment(groupB, preferHorizontalFirst));
        return result;
    }

    /// <summary>
    /// Largest vertical gap between projection of blocks onto the Y-axis.
    /// In screen coords, "above" means smaller Y. Retries without narrow
    /// outliers (vertical margin text etc.) if the first pass finds no gap.
    /// </summary>
    private static CutInfo FindBestHorizontalCut(List<LayoutBlock> blocks)
    {
        if (blocks.Count < 2) return default;

        var edgeCut = FindHorizontalCutByEdges(blocks);
        if (edgeCut.Gap >= MinGapThreshold) return edgeCut;

        if (blocks.Count >= 3 && TryBoundingRegion(blocks, out var region))
        {
            double narrowThreshold = region.W * NarrowElementWidthRatio;
            var filtered = new List<LayoutBlock>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i].BBox.W >= narrowThreshold) filtered.Add(blocks[i]);

            if (filtered.Count >= 2 && filtered.Count < blocks.Count)
            {
                var retry = FindHorizontalCutByEdges(filtered);
                if (retry.Gap > edgeCut.Gap && retry.Gap >= MinGapThreshold)
                    return retry;
            }
        }
        return edgeCut;
    }

    private static CutInfo FindHorizontalCutByEdges(List<LayoutBlock> blocks)
    {
        var sorted = new List<LayoutBlock>(blocks);
        sorted.Sort((a, b) =>
        {
            int c = a.BBox.Y.CompareTo(b.BBox.Y);
            return c != 0 ? c : (a.BBox.Y + a.BBox.H).CompareTo(b.BBox.Y + b.BBox.H);
        });

        double largestGap = 0, cutPosition = 0;
        float? prevBottom = null;
        foreach (var b in sorted)
        {
            float top = b.BBox.Y;
            float bottom = b.BBox.Y + b.BBox.H;
            if (prevBottom.HasValue && prevBottom.Value < top)
            {
                double gap = top - prevBottom.Value;
                if (gap > largestGap)
                {
                    largestGap = gap;
                    cutPosition = (prevBottom.Value + top) / 2.0;
                }
            }
            prevBottom = prevBottom is null ? bottom : Math.Max(prevBottom.Value, bottom);
        }
        return new CutInfo(cutPosition, largestGap);
    }

    /// <summary>
    /// Largest horizontal gap. First-pass uses all blocks; if that gap is
    /// below threshold, retry without narrow outliers (page numbers etc.
    /// that bridge column gaps).
    /// </summary>
    private static CutInfo FindBestVerticalCut(List<LayoutBlock> blocks)
    {
        if (blocks.Count < 2) return default;

        var edgeCut = FindVerticalCutByEdges(blocks);
        if (edgeCut.Gap >= MinGapThreshold) return edgeCut;

        if (blocks.Count >= 3 && TryBoundingRegion(blocks, out var region))
        {
            double narrowThreshold = region.W * NarrowElementWidthRatio;
            var filtered = new List<LayoutBlock>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i].BBox.W >= narrowThreshold) filtered.Add(blocks[i]);

            if (filtered.Count >= 2 && filtered.Count < blocks.Count)
            {
                var retry = FindVerticalCutByEdges(filtered);
                if (retry.Gap > edgeCut.Gap && retry.Gap >= MinGapThreshold)
                    return retry;
            }
        }
        return edgeCut;
    }

    private static CutInfo FindVerticalCutByEdges(List<LayoutBlock> blocks)
    {
        var sorted = new List<LayoutBlock>(blocks);
        sorted.Sort((a, b) =>
        {
            int c = a.BBox.X.CompareTo(b.BBox.X);
            return c != 0 ? c : (a.BBox.X + a.BBox.W).CompareTo(b.BBox.X + b.BBox.W);
        });

        double largestGap = 0, cutPosition = 0;
        float? prevRight = null;
        foreach (var b in sorted)
        {
            float left = b.BBox.X;
            float right = b.BBox.X + b.BBox.W;
            if (prevRight.HasValue && prevRight.Value < left)
            {
                double gap = left - prevRight.Value;
                if (gap > largestGap)
                {
                    largestGap = gap;
                    cutPosition = (prevRight.Value + left) / 2.0;
                }
            }
            prevRight = prevRight is null ? right : Math.Max(prevRight.Value, right);
        }
        return new CutInfo(cutPosition, largestGap);
    }

    private static (List<LayoutBlock> above, List<LayoutBlock> below) SplitByHorizontalCut(
        List<LayoutBlock> blocks, double cutY)
    {
        var above = new List<LayoutBlock>();
        var below = new List<LayoutBlock>();
        foreach (var b in blocks)
        {
            double centerY = b.BBox.Y + b.BBox.H / 2.0;
            if (centerY < cutY) above.Add(b);
            else below.Add(b);
        }
        return (above, below);
    }

    private static (List<LayoutBlock> left, List<LayoutBlock> right) SplitByVerticalCut(
        List<LayoutBlock> blocks, double cutX)
    {
        var left = new List<LayoutBlock>();
        var right = new List<LayoutBlock>();
        foreach (var b in blocks)
        {
            double centerX = b.BBox.X + b.BBox.W / 2.0;
            if (centerX < cutX) left.Add(b);
            else right.Add(b);
        }
        return (left, right);
    }

    // ===== Phase 4: merge cross-layout =====

    private static List<LayoutBlock> MergeCrossLayoutElements(
        List<LayoutBlock> sortedMain, List<LayoutBlock> crossLayout)
    {
        if (crossLayout.Count == 0) return sortedMain;
        if (sortedMain.Count == 0) return SortByYThenX(crossLayout);

        var sortedCross = SortByYThenX(crossLayout);
        var result = new List<LayoutBlock>(sortedMain.Count + sortedCross.Count);
        int m = 0, c = 0;
        while (m < sortedMain.Count || c < sortedCross.Count)
        {
            if (c >= sortedCross.Count) result.Add(sortedMain[m++]);
            else if (m >= sortedMain.Count) result.Add(sortedCross[c++]);
            else
            {
                // In Y-down coords, "above" = smaller Y. Insert the cross-layout
                // element first if its top is at or above the main element's top.
                if (sortedCross[c].BBox.Y <= sortedMain[m].BBox.Y) result.Add(sortedCross[c++]);
                else result.Add(sortedMain[m++]);
            }
        }
        return result;
    }

    // ===== Utility =====

    private static List<LayoutBlock> SortByYThenX(IReadOnlyList<LayoutBlock> blocks)
    {
        var sorted = new List<LayoutBlock>(blocks);
        sorted.Sort((a, b) =>
        {
            int c = a.BBox.Y.CompareTo(b.BBox.Y);
            return c != 0 ? c : a.BBox.X.CompareTo(b.BBox.X);
        });
        return sorted;
    }
}
