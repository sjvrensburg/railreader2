using System;
using System.Collections.Generic;
using RailReader.Core.Models;

namespace RailReader2.Services;

/// <summary>A vertical column band of a table, in page points. Core models cells only as per-row
/// X-bands (<see cref="CellInfo"/>) with no column identity, so columns are inferred shell-side by
/// <see cref="TableColumnIndex"/> for the column / row+column focus scopes.</summary>
public readonly record struct ColumnBand(float X, float Width)
{
    public float CenterX => X + Width / 2f;
    public float Right => X + Width;
}

/// <summary>
/// Best-effort column inference for a table block: clusters the per-row cell X-bands
/// (<see cref="LineInfo.Cells"/>) across all of the block's rows into column bands, so the
/// column under the current cell can be highlighted/dimmed. Imperfect for tables with merged or
/// ragged cells — documented as best-effort, since Core deliberately does not model columns.
///
/// <para>Results are cached by <see cref="LayoutBlock"/> reference, which self-invalidates when a
/// re-analysis replaces the block instances. Owned per document by the view-model, like
/// <c>ReferenceIndex</c>; call <see cref="Clear"/> on tab/document switch to release old blocks.</para>
/// </summary>
public sealed class TableColumnIndex
{
    private readonly Dictionary<LayoutBlock, IReadOnlyList<ColumnBand>> _cache = new();

    /// <summary>Column bands for the table <paramref name="block"/>. Empty when it has no cells.</summary>
    public IReadOnlyList<ColumnBand> GetColumns(LayoutBlock block)
    {
        if (_cache.TryGetValue(block, out var bands)) return bands;
        bands = Build(block.Lines);
        _cache[block] = bands;
        return bands;
    }

    public void Clear() => _cache.Clear();

    /// <summary>Index of the band containing <paramref name="centerX"/> (a cell centre), or the
    /// nearest band by centre distance; -1 when there are no bands.</summary>
    public static int ColumnIndexFor(IReadOnlyList<ColumnBand> bands, float centerX)
    {
        if (bands.Count == 0) return -1;
        for (int i = 0; i < bands.Count; i++)
            if (centerX >= bands[i].X && centerX <= bands[i].Right) return i;

        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < bands.Count; i++)
        {
            float d = Math.Abs(bands[i].CenterX - centerX);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // X-band clustering: collect every cell's [X, X+Width] across all rows, sort by left edge, then
    // greedily merge intervals that overlap or sit within a width-proportional tolerance of each
    // other. Same-column cells overlap heavily in X (the cell splitter already separated real
    // column gaps within a row), so overlap-merging recovers the columns.
    private static IReadOnlyList<ColumnBand> Build(IReadOnlyList<LineInfo> lines)
    {
        var intervals = new List<(float L, float R)>();
        var widths = new List<float>();
        foreach (var line in lines)
        {
            if (line.Cells is not { Count: > 0 } cells) continue;
            foreach (var c in cells)
            {
                if (c.Width <= 0) continue;
                intervals.Add((c.X, c.X + c.Width));
                widths.Add(c.Width);
            }
        }
        if (intervals.Count == 0) return [];

        widths.Sort();
        float median = widths[widths.Count / 2];
        float tol = Math.Clamp(median * 0.35f, 1f, 12f);

        // Cells far wider than the median are likely merged/spanning (e.g. a full-width header), which
        // would otherwise bridge every column into one band. Exclude them from the clustering; if that
        // leaves nothing (a table of uniformly wide cells), fall back to all intervals.
        float maxColWidth = median * 2.5f;
        var clustered = intervals.FindAll(i => i.R - i.L <= maxColWidth);
        if (clustered.Count == 0) clustered = intervals;
        clustered.Sort(static (a, b) => a.L.CompareTo(b.L));

        var bands = new List<ColumnBand>();
        float curL = clustered[0].L, curR = clustered[0].R;
        for (int i = 1; i < clustered.Count; i++)
        {
            var (l, r) = clustered[i];
            if (l <= curR + tol)
            {
                if (r > curR) curR = r;
            }
            else
            {
                bands.Add(new ColumnBand(curL, curR - curL));
                curL = l;
                curR = r;
            }
        }
        bands.Add(new ColumnBand(curL, curR - curL));
        return bands;
    }
}
