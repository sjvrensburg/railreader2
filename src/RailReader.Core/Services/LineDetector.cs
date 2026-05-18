using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Detects text lines inside layout blocks.
///
/// Three strategies are applied in order of preference:
///   1. <b>Atomic classes</b> — equation, figure, table blocks collapse to a single
///      line spanning the full block. Multi-line equations and figures should
///      advance in rail mode as one unit, not be fragmented row-by-row.
///   2. <b>Char-box clustering</b> — when PDFium per-character bounding boxes are
///      available, cluster them by vertical position. Robust to subscripts,
///      superscripts, and inline math; gives true baselines rather than the
///      smoothed peaks of pixel projection.
///   3. <b>Pixel projection</b> — fall back to row-density analysis of the
///      rasterized page. Used for scanned PDFs and any block where char
///      clustering produced nothing.
/// </summary>
public static class LineDetector
{
    /// <summary>
    /// Block classes treated as a single atomic line in rail mode. Only purely
    /// visual blocks belong here — they have no meaningful per-line structure
    /// and should advance as one unit. Equations (<c>display_formula</c>,
    /// <c>inline_formula</c>, <c>algorithm</c>) deliberately stay line-detectable
    /// because stepwise derivations and algorithm pseudocode read line-by-line;
    /// char-box clustering handles those without fragmenting sub/superscripts.
    /// </summary>
    public static readonly HashSet<int> AtomicLineClasses =
    [
        LayoutConstants.ClassChart,
        LayoutConstants.ClassFooterImage,
        LayoutConstants.ClassHeaderImage,
        LayoutConstants.ClassImage,
        LayoutConstants.ClassTable,
    ];

    /// <summary>
    /// Returns line runs (in block-relative pixel coordinates) for a block,
    /// using char clustering when available and pixel projection as fallback.
    /// </summary>
    public static List<LineInfo> DetectLines(
        LayoutBlock block,
        IReadOnlyList<CharBox>? charBoxes,
        byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        if (AtomicLineClasses.Contains(block.ClassId))
            return [new LineInfo(block.BBox.Y + block.BBox.H / 2f, block.BBox.H)];

        if (charBoxes is { Count: > 0 })
        {
            var charLines = DetectLinesFromChars(block.BBox, charBoxes);
            if (charLines.Count > 0)
                return charLines;
        }

        return DetectLinesFromPixels(block, rgbBytes, imgW, imgH, scaleX, scaleY);
    }

    /// <summary>
    /// Clusters character bounding boxes by vertical position into text lines.
    /// Returns lines in page-point space (Y measured from page top).
    /// </summary>
    /// <remarks>
    /// Algorithm: filter chars whose midpoint falls inside the block, sort by
    /// mid-Y, then greedily split clusters when the gap between a char's mid-Y
    /// and the current cluster's median mid-Y exceeds a fraction of the median
    /// char height. Each cluster becomes one LineInfo whose height spans from
    /// the cluster's min-top to max-bottom — this includes ascenders, descenders,
    /// and sub/superscripts within the line.
    /// </remarks>
    internal static List<LineInfo> DetectLinesFromChars(BBox bbox, IReadOnlyList<CharBox> charBoxes)
    {
        float left = bbox.X;
        float right = bbox.X + bbox.W;
        float top = bbox.Y;
        float bottom = bbox.Y + bbox.H;

        var chars = new List<(float MidY, float Top, float Bottom, float Height)>(charBoxes.Count);
        foreach (var c in charBoxes)
        {
            float h = c.Bottom - c.Top;
            if (h <= 0) continue; // skip whitespace / degenerate boxes

            float midX = (c.Left + c.Right) * 0.5f;
            float midY = (c.Top + c.Bottom) * 0.5f;
            if (midX < left || midX > right) continue;
            if (midY < top || midY > bottom) continue;

            chars.Add((midY, c.Top, c.Bottom, h));
        }

        if (chars.Count == 0) return [];

        var heightsSorted = chars.Select(c => c.Height).OrderBy(h => h).ToArray();
        float refHeight = heightsSorted[heightsSorted.Length / 2];
        if (refHeight <= 0) return [];

        chars.Sort((a, b) => a.MidY.CompareTo(b.MidY));

        // Greedy clustering. Threshold deliberately generous (1.0 × refHeight) so
        // sub/superscripts and inline math don't fragment a single visual line.
        float splitThreshold = refHeight * 1.0f;
        var lines = new List<LineInfo>();
        int clusterStart = 0;
        float clusterSumMidY = chars[0].MidY;

        for (int i = 1; i < chars.Count; i++)
        {
            int clusterCount = i - clusterStart;
            float clusterAvgMidY = clusterSumMidY / clusterCount;
            if (chars[i].MidY - clusterAvgMidY > splitThreshold)
            {
                lines.Add(MakeLine(chars, clusterStart, i));
                clusterStart = i;
                clusterSumMidY = chars[i].MidY;
            }
            else
            {
                clusterSumMidY += chars[i].MidY;
            }
        }
        lines.Add(MakeLine(chars, clusterStart, chars.Count));

        return lines;

        static LineInfo MakeLine(
            List<(float MidY, float Top, float Bottom, float Height)> sorted,
            int start, int endExclusive)
        {
            float minTop = float.PositiveInfinity;
            float maxBottom = float.NegativeInfinity;
            for (int i = start; i < endExclusive; i++)
            {
                if (sorted[i].Top < minTop) minTop = sorted[i].Top;
                if (sorted[i].Bottom > maxBottom) maxBottom = sorted[i].Bottom;
            }
            float h = maxBottom - minTop;
            float y = (minTop + maxBottom) * 0.5f;
            return new LineInfo(y, h);
        }
    }

    /// <summary>
    /// Pixel-projection line detection: crops the block region of the page bitmap,
    /// computes per-row dark-pixel density, and finds runs above an adaptive
    /// threshold. Returns lines in page-point space.
    /// </summary>
    internal static List<LineInfo> DetectLinesFromPixels(
        LayoutBlock block, byte[] rgbBytes, int imgW, int imgH, float scaleX, float scaleY)
    {
        int pxX = Math.Min((int)Math.Round(block.BBox.X / scaleX), imgW - 1);
        int pxY = Math.Min((int)Math.Round(block.BBox.Y / scaleY), imgH - 1);
        int pxW = Math.Min((int)Math.Round(block.BBox.W / scaleX), imgW - pxX);
        int pxH = Math.Min((int)Math.Round(block.BBox.H / scaleY), imgH - pxY);

        if (pxW == 0 || pxH == 0)
            return [];

        var densities = ComputeRowDensities(rgbBytes, imgW, pxX, pxY, pxW, pxH);
        var runs = FindLineRuns(densities);

        var lines = new List<LineInfo>(runs.Count);
        foreach (var run in runs)
        {
            float centerYPx = run.Start + run.Height / 2.0f;
            lines.Add(new LineInfo(block.BBox.Y + centerYPx * scaleY, run.Height * scaleY));
        }
        return lines;
    }

    internal static float[] ComputeRowDensities(byte[] rgbBytes, int imgW, int cropX, int cropY, int cropW, int cropH)
    {
        var profile = new float[cropH];
        for (int row = 0; row < cropH; row++)
        {
            int darkCount = 0;
            for (int col = 0; col < cropW; col++)
            {
                int pixelIdx = ((cropY + row) * imgW + (cropX + col)) * 3;
                if (pixelIdx + 2 < rgbBytes.Length)
                {
                    float r = rgbBytes[pixelIdx];
                    float g = rgbBytes[pixelIdx + 1];
                    float b = rgbBytes[pixelIdx + 2];
                    float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                    if (lum < LayoutConstants.DarkLuminanceThreshold)
                        darkCount++;
                }
            }
            profile[row] = (float)darkCount / cropW;
        }

        // Smooth with radius-1 moving average
        var smoothed = new float[cropH];
        for (int r = 0; r < cropH; r++)
        {
            int start = Math.Max(r - 1, 0);
            int end = Math.Min(r + 2, cropH);
            float sum = 0;
            for (int k = start; k < end; k++) sum += profile[k];
            smoothed[r] = sum / (end - start);
        }
        return smoothed;
    }

    /// <summary>
    /// Detects line runs using adaptive density thresholding with recovery for short lines.
    /// The primary pass uses a density-fraction threshold (15% of average) which reliably
    /// segments dense text. A second recovery pass scans any uncovered regions at the top
    /// and bottom of the block with a low absolute threshold to catch short lines (e.g. the
    /// last few words of a paragraph) that fall below the density-fraction threshold.
    /// </summary>
    internal static List<(int Start, int Height)> FindLineRuns(float[] densities)
    {
        // Primary pass: density-fraction threshold — works well for dense text
        var nonZero = densities.Where(v => v > 0.005f).ToArray();
        float threshold = nonZero.Length == 0
            ? 0.005f
            : Math.Max(nonZero.Average() * LayoutConstants.DensityThresholdFraction, 0.005f);

        var runs = FindRunsAboveThreshold(densities, threshold);

        if (runs.Count == 0) return runs;

        // Recovery pass: check for uncovered regions at top and bottom of the block.
        // If the first/last detected line is far from the block edge, re-scan that
        // region with a low absolute threshold to catch short lines.
        int medianHeight = runs.Select(r => r.Height).OrderBy(h => h).ElementAt(runs.Count / 2);
        float recoveryThreshold = 0.005f;

        // Top recovery: region before the first detected line
        int firstLineStart = runs[0].Start;
        if (firstLineStart > medianHeight / 2)
        {
            var topRuns = FindRunsAboveThreshold(densities[..firstLineStart], recoveryThreshold);
            runs.InsertRange(0, topRuns);
        }

        // Bottom recovery: region after the last detected line
        var lastRun = runs[^1];
        int lastLineEnd = lastRun.Start + lastRun.Height;
        int remaining = densities.Length - lastLineEnd;
        if (remaining > medianHeight / 2)
        {
            var bottomRuns = FindRunsAboveThreshold(densities[lastLineEnd..], recoveryThreshold);
            runs.AddRange(bottomRuns.Select(r => (r.Start + lastLineEnd, r.Height)));
        }

        return runs;
    }

    private static List<(int Start, int Height)> FindRunsAboveThreshold(float[] densities, float threshold)
    {
        var runs = new List<(int Start, int Height)>();
        int? runStart = null;

        for (int r = 0; r < densities.Length; r++)
        {
            if (densities[r] > threshold)
            {
                runStart ??= r;
            }
            else if (runStart is not null)
            {
                int runH = r - runStart.Value;
                if (runH >= LayoutConstants.MinLineHeightPx)
                    runs.Add((runStart.Value, runH));
                runStart = null;
            }
        }
        if (runStart is not null)
        {
            int runH = densities.Length - runStart.Value;
            if (runH >= LayoutConstants.MinLineHeightPx)
                runs.Add((runStart.Value, runH));
        }
        return runs;
    }
}
