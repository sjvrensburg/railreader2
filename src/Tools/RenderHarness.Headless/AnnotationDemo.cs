using RailReader.Core.Models;

namespace RailReader2.RenderHarness.Headless;

/// <summary>
/// Injects a small, representative set of annotations onto a page — placed over
/// real detected text so the result looks like a genuine markup session: a red
/// underline on the title, yellow highlights over a couple of body lines, and a
/// freehand "scribble" in the margin.
/// </summary>
internal static class AnnotationDemo
{
    const string Yellow = "#FFE14D";
    const string Red = "#E0352B";

    /// <summary>Injects the demo annotations and returns the centre of their combined
    /// bounding box (page points) so the camera can be framed to show them all.</summary>
    public static (float Cx, float Cy) Inject(AnnotationFile file, int pageIdx, PageAnalysis? analysis, double pageW, double pageH)
    {
        if (!file.Pages.TryGetValue(pageIdx, out var list))
            file.Pages[pageIdx] = list = [];

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Grow(float x, float y, float w, float h)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + w); maxY = Math.Max(maxY, y + h);
        }

        var blocks = analysis?.Blocks;

        // Title: prefer an explicit title/heading near the top, else the topmost block.
        var title = blocks?
            .Where(b => b.Role is BlockRole.Title or BlockRole.Heading)
            .OrderBy(b => b.BBox.Y)
            .FirstOrDefault()
            ?? blocks?.OrderBy(b => b.BBox.Y).FirstOrDefault();

        // Body: the tallest Text block in the upper two-thirds (the abstract / intro).
        var body = blocks?
            .Where(b => b.Role == BlockRole.Text && b.BBox.Y < pageH * 0.66)
            .OrderByDescending(b => b.BBox.H)
            .FirstOrDefault();

        // Underline the title line(s), in red.
        if (title is { } t)
        {
            var rects = LineRects(t, maxLines: 2);
            list.Add(new UnderlineAnnotation { Rects = rects, Color = Red, Opacity = 1f });
            foreach (var r in rects) Grow(r.X, r.Y, r.W, r.H);
        }

        // Highlight a few body lines, in yellow.
        if (body is { } bd)
        {
            var rects = LineRects(bd, maxLines: 6).Skip(1).Take(3).ToList();
            if (rects.Count > 0)
            {
                list.Add(new HighlightAnnotation { Rects = rects, Color = Yellow, Opacity = 0.4f });
                foreach (var r in rects) Grow(r.X, r.Y, r.W, r.H);
            }
        }

        // Freehand "scribble" in the top-right margin (where a reader jots a note).
        float mx = (float)(pageW * 0.70);
        float my = (float)(title?.BBox.Y - 6 ?? pageH * 0.10);
        var pts = Scribble(mx, my);
        list.Add(new FreehandAnnotation { Color = Red, Opacity = 1f, StrokeWidth = 2.2f, Points = pts });
        foreach (var p in pts) Grow(p.X, p.Y, 0, 0);

        if (maxX < minX) return ((float)(pageW / 2), (float)(pageH * 0.3)); // nothing placed
        return ((minX + maxX) / 2f, (minY + maxY) / 2f);
    }

    /// <summary>Detected line boxes for a block, falling back to the block's own box.</summary>
    static List<HighlightRect> LineRects(LayoutBlock b, int maxLines)
    {
        if (b.Lines is { Count: > 0 } lines)
            return lines.Take(maxLines)
                .Select(l => new HighlightRect(l.X, l.Y, l.Width, l.Height))
                .ToList();
        return [new HighlightRect(b.BBox.X, b.BBox.Y, b.BBox.W, b.BBox.H)];
    }

    /// <summary>A short hand-drawn zig-zag (looks like an emphatic margin mark).</summary>
    static List<PointF> Scribble(float x, float y)
    {
        float[,] d =
        {
            { 0, 8 }, { 14, 0 }, { 26, 12 }, { 40, 2 }, { 52, 14 },
            { 60, 4 }, { 50, 18 }, { 34, 22 }, { 18, 18 }, { 6, 24 },
        };
        var pts = new List<PointF>(d.GetLength(0));
        for (int i = 0; i < d.GetLength(0); i++)
            pts.Add(new PointF(x + d[i, 0], y + d[i, 1]));
        return pts;
    }
}
