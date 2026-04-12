using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Shared drawing logic for rail overlays, debug overlays, and search highlights.
/// Used by both the live Avalonia layers and the ScreenshotCompositor.
/// </summary>
public static class OverlayRenderer
{
    public const float BlockMargin = 4f;

    private static readonly SKColor[] DebugColors =
    [
        new(220, 70, 180), new(33, 150, 243), new(76, 175, 80),
        new(255, 152, 0), new(156, 39, 176), new(0, 188, 212),
    ];

    // --- ThreadStatic paint caches (matching AnnotationRenderer pattern) ---
    // Color/StrokeWidth are mutated per call; Style is stable.

    [ThreadStatic] private static SKPaint? s_dimPaint;
    [ThreadStatic] private static SKPaint? s_revealPaint;
    [ThreadStatic] private static SKPaint? s_outlinePaint;
    [ThreadStatic] private static SKPaint? s_linePaint;
    [ThreadStatic] private static SKPaint? s_debugFillPaint;
    [ThreadStatic] private static SKPaint? s_debugStrokePaint;
    [ThreadStatic] private static SKPaint? s_debugBgPaint;
    [ThreadStatic] private static SKPaint? s_debugTextPaint;
    [ThreadStatic] private static SKFont? s_debugFont;
    [ThreadStatic] private static SKPaint? s_highlightPaint;
    [ThreadStatic] private static SKPaint? s_activePaint;

    internal static SKPaint GetDimPaint() => s_dimPaint ??= new SKPaint();
    internal static SKPaint GetRevealPaint() => s_revealPaint ??= new SKPaint();
    internal static SKPaint GetOutlinePaint() => s_outlinePaint ??= new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };
    internal static SKPaint GetLinePaint() => s_linePaint ??= new SKPaint();
    internal static SKPaint GetDebugFillPaint() => s_debugFillPaint ??= new SKPaint();
    internal static SKPaint GetDebugStrokePaint() => s_debugStrokePaint ??= new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    internal static SKPaint GetDebugBgPaint() => s_debugBgPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 200) };
    internal static SKPaint GetDebugTextPaint() => s_debugTextPaint ??= new SKPaint { IsAntialias = true };
    internal static SKFont GetDebugFont() => s_debugFont ??= new SKFont(SKTypeface.Default, 8);
    internal static SKPaint GetHighlightPaint() => s_highlightPaint ??= new SKPaint { Color = new SKColor(255, 255, 0, 100), IsAntialias = true };
    internal static SKPaint GetActivePaint() => s_activePaint ??= new SKPaint { Color = new SKColor(255, 165, 0, 160), IsAntialias = true };

    /// <summary>
    /// Draws rail mode overlays: page dim, block reveal, block outline, and line highlight.
    /// Paint objects are passed in so callers can manage lifetime (cached or disposable).
    /// </summary>
    public static void DrawRailOverlays(
        SKCanvas canvas,
        LayoutBlock block,
        LineInfo line,
        float pageWidth,
        float pageHeight,
        OverlayPalette palette,
        bool lineFocusBlur,
        bool lineHighlightEnabled,
        double linePadding,
        LineHighlightTint tint,
        double tintOpacity,
        SKPaint dimPaint,
        SKPaint revealPaint,
        SKPaint outlinePaint,
        SKPaint linePaint)
    {
        var blockRect = SKRect.Create(
            block.BBox.X - BlockMargin, block.BBox.Y - BlockMargin,
            block.BBox.W + BlockMargin * 2, block.BBox.H + BlockMargin * 2);

        var pageRect = SKRect.Create(0, 0, pageWidth, pageHeight);

        // Dim — skip when line focus blur is active because the PdfPageLayer's
        // line focus dim gradient handles all per-line dimming. Drawing both
        // creates a brightness step at the block boundary (DimExcludesBlock)
        // or double-dims non-active lines, producing a visible halo around the
        // active line with dark colour effects.
        if (!lineFocusBlur)
        {
            dimPaint.Color = palette.Dim.ToSKColor();
            if (palette.DimExcludesBlock)
            {
                canvas.Save();
                canvas.ClipRect(blockRect, SKClipOperation.Difference);
                canvas.DrawRect(pageRect, dimPaint);
                canvas.Restore();
            }
            else
            {
                canvas.DrawRect(pageRect, dimPaint);
            }
        }

        // Block reveal (also skipped when line focus blur is active — same rationale as dim)
        if (!lineFocusBlur && !palette.DimExcludesBlock && palette.BlockReveal is var (revealColor, blendMode))
        {
            canvas.Save();
            canvas.ClipRect(blockRect);
            revealPaint.Color = revealColor.ToSKColor();
            revealPaint.BlendMode = blendMode.ToSKBlendMode();
            canvas.DrawRect(blockRect, revealPaint);
            canvas.Restore();
        }

        // Block outline
        var bboxRect = SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H);
        outlinePaint.Color = palette.BlockOutline.ToSKColor();
        outlinePaint.StrokeWidth = palette.BlockOutlineWidth;
        canvas.DrawRect(bboxRect, outlinePaint);

        // Line highlight — full-width coloured bar, independent of line focus blur.
        // Padding matches the line focus dim clear zone so both features align.
        if (lineHighlightEnabled)
        {
            float hlPad = line.Height * (float)linePadding;
            linePaint.Color = palette.ResolveLineHighlight(tint, tintOpacity).ToSKColor();
            canvas.DrawRect(SKRect.Create(
                block.BBox.X, line.Y - line.Height / 2 - hlPad,
                block.BBox.W, line.Height + hlPad * 2), linePaint);
        }
        else if (lineFocusBlur)
        {
            // Thin vertical bar indicator when highlight is off but blur is on
            const float barWidth = 3f;
            float pad = line.Height * 0.15f;
            linePaint.Color = palette.BlockOutline.WithAlpha(200).ToSKColor();
            canvas.DrawRect(SKRect.Create(
                block.BBox.X - BlockMargin - barWidth,
                line.Y - line.Height / 2 - pad,
                barWidth,
                line.Height + pad * 2), linePaint);
        }
    }

    /// <summary>
    /// Draws debug overlay showing detected layout blocks with confidence and reading order.
    /// </summary>
    public static void DrawDebugOverlay(
        SKCanvas canvas,
        PageAnalysis analysis,
        SKFont font,
        SKPaint fillPaint,
        SKPaint strokePaint,
        SKPaint bgPaint,
        SKPaint textPaint)
    {
        foreach (var block in analysis.Blocks)
        {
            var color = DebugColors[block.ClassId % DebugColors.Length];
            var rect = SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H);

            fillPaint.Color = color.WithAlpha(50);
            canvas.DrawRect(rect, fillPaint);

            strokePaint.Color = color.WithAlpha(180);
            canvas.DrawRect(rect, strokePaint);

            string className = block.ClassId < LayoutConstants.LayoutClasses.Length
                ? LayoutConstants.LayoutClasses[block.ClassId] : "unknown";
            string label = $"#{block.Order} {className} ({block.Confidence * 100:F0}%)";

            canvas.DrawRect(SKRect.Create(block.BBox.X, block.BBox.Y - 10, label.Length * 5f, 11), bgPaint);

            textPaint.Color = color;
            canvas.DrawText(label, block.BBox.X + 1, block.BBox.Y - 1, font, textPaint);
        }
    }

    /// <summary>
    /// Draws search match highlights with the active match in a distinct colour.
    /// Matches outside the viewport are skipped to avoid unnecessary draw calls.
    /// </summary>
    public static void DrawSearchHighlights(
        SKCanvas canvas,
        IReadOnlyList<SearchMatch> matches,
        int activeLocalIndex,
        SKPaint highlightPaint,
        SKPaint activePaint,
        SKRect viewport = default)
    {
        bool cull = viewport.Width > 0 && viewport.Height > 0;

        for (int i = 0; i < matches.Count; i++)
        {
            var rects = matches[i].Rects;
            if (rects.Count == 0) continue;

            // Quick viewport cull: skip matches whose vertical extent is entirely off-screen.
            if (cull)
            {
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var r in rects)
                {
                    if (r.Top < minY) minY = r.Top;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }
                if (maxY < viewport.Top || minY > viewport.Bottom)
                    continue;
            }

            var paint = i == activeLocalIndex ? activePaint : highlightPaint;
            foreach (var rect in rects)
                canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), paint);
        }
    }

    /// <summary>
    /// Computes the local (within current page) index of the active search match.
    /// Returns -1 if the active match is not on the given page.
    /// </summary>
    public static int ComputeActiveLocalIndex(
        IReadOnlyList<SearchMatch> allMatches,
        IReadOnlyList<SearchMatch> pageMatches,
        int activeGlobalIndex,
        int pageIndex)
    {
        if (activeGlobalIndex < 0 || activeGlobalIndex >= allMatches.Count)
            return -1;
        var activeMatch = allMatches[activeGlobalIndex];
        if (activeMatch.PageIndex != pageIndex)
            return -1;
        for (int i = 0; i < pageMatches.Count; i++)
        {
            if (ReferenceEquals(pageMatches[i], activeMatch))
                return i;
        }
        return -1;
    }

}
