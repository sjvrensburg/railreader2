using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

/// <summary>
/// Shared drawing logic for rail overlays, debug overlays, and search highlights.
/// Used by both the live Avalonia layers and the ScreenshotCompositor.
/// </summary>
public static class OverlayRenderer
{
    public const float BlockMargin = 4f;

    private static readonly SKColor[] DebugColors =
    [
        new(244, 67, 54), new(33, 150, 243), new(76, 175, 80),
        new(255, 152, 0), new(156, 39, 176), new(0, 188, 212),
    ];

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

        // Dim
        dimPaint.Color = palette.Dim;
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

        // Block reveal
        if (!palette.DimExcludesBlock && palette.BlockReveal is var (revealColor, blendMode))
        {
            canvas.Save();
            canvas.ClipRect(blockRect);
            revealPaint.Color = revealColor;
            revealPaint.BlendMode = blendMode;
            canvas.DrawRect(blockRect, revealPaint);
            canvas.Restore();
        }

        // Block outline
        var bboxRect = SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H);
        outlinePaint.Color = palette.BlockOutline;
        outlinePaint.StrokeWidth = palette.BlockOutlineWidth;
        canvas.DrawRect(bboxRect, outlinePaint);

        // Line highlight
        if (lineFocusBlur)
        {
            const float barWidth = 3f;
            float pad = line.Height * 0.15f;
            linePaint.Color = palette.BlockOutline.WithAlpha(200);
            canvas.DrawRect(SKRect.Create(
                block.BBox.X - BlockMargin - barWidth,
                line.Y - line.Height / 2 - pad,
                barWidth,
                line.Height + pad * 2), linePaint);
        }
        else
        {
            linePaint.Color = palette.ResolveLineHighlight(tint, tintOpacity);
            canvas.DrawRect(SKRect.Create(block.BBox.X, line.Y - line.Height / 2, block.BBox.W, line.Height), linePaint);
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
    /// </summary>
    public static void DrawSearchHighlights(
        SKCanvas canvas,
        IReadOnlyList<SearchMatch> matches,
        int activeLocalIndex,
        SKPaint highlightPaint,
        SKPaint activePaint)
    {
        for (int i = 0; i < matches.Count; i++)
        {
            var paint = i == activeLocalIndex ? activePaint : highlightPaint;
            foreach (var rect in matches[i].Rects)
                canvas.DrawRect(rect, paint);
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
