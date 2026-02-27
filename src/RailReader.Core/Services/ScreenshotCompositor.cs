using RailReader.Core.Commands;
using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

/// <summary>
/// Composes all visual layers (PDF page, colour effects, line focus blur,
/// rail overlay, search highlights, annotations, debug overlay) onto a
/// single SKBitmap. Pure SkiaSharp — no Avalonia dependency.
///
/// When SimulateViewport is set, the output is cropped to show exactly what
/// the user would see on screen at the current camera position and zoom.
/// </summary>
public static class ScreenshotCompositor
{
    private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// Renders the current page of a document with all requested overlays.
    /// </summary>
    public static SKBitmap RenderPage(
        DocumentState doc,
        DocumentController controller,
        ScreenshotOptions options)
    {
        // Render PDF page at requested DPI
        int dpi = Math.Clamp(options.Dpi, 72, 600);
        using var pageBitmap = doc.Pdf.RenderPage(doc.CurrentPage, dpi);

        int bitmapW = pageBitmap.Width;
        int bitmapH = pageBitmap.Height;
        float pageW = (float)doc.PageWidth;
        float pageH = (float)doc.PageHeight;

        if (pageW <= 0 || pageH <= 0)
            return pageBitmap.Copy();

        float scaleX = bitmapW / pageW;
        float scaleY = bitmapH / pageH;

        // Create a surface at the full page bitmap size, draw everything in
        // bitmap coordinates, then crop to the viewport at the end.
        var info = new SKImageInfo(bitmapW, bitmapH);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Failed to create surface ({bitmapW}x{bitmapH})");
        var canvas = surface.Canvas;

        using var pageImage = SKImage.FromBitmap(pageBitmap);
        var destRect = SKRect.Create(0, 0, bitmapW, bitmapH);
        var colourEffects = controller.ColourEffects;

        // --- Layer 1: Page bitmap with optional line focus blur ---
        // Line focus blur and colour effect are composed together:
        // colour effect wraps the page draw (including blur passes).
        using var effectPaint = colourEffects.HasActiveEffect ? colourEffects.CreatePaint() : null;
        bool needsColourLayer = effectPaint is not null;

        if (needsColourLayer)
            canvas.SaveLayer(effectPaint);

        bool didLineFocusBlur = false;
        if (options.LineFocusBlur && options.LineFocusBlurIntensity > 0
            && doc.Rail is { Active: true, NavigableCount: > 0 })
        {
            var line = doc.Rail.CurrentLineInfo;
            float pad = line.Height * 0.25f;
            // Line rect in page-point space
            float lineTop = line.Y - line.Height / 2f - pad;
            float lineHeight = line.Height + pad * 2;
            // Convert to bitmap coordinates
            var lineRect = SKRect.Create(0, lineTop * scaleY, bitmapW, lineHeight * scaleY);

            float sigma = (float)(4.0 * options.LineFocusBlurIntensity) * ((scaleX + scaleY) / 2f);
            if (sigma >= 0.5f)
            {
                didLineFocusBlur = true;

                // Pass 1: Draw entire page blurred, clipping out the active line
                canvas.Save();
                canvas.ClipRect(lineRect, SKClipOperation.Difference);
                using var focusBlur = SKImageFilter.CreateBlur(sigma, sigma);
                using var focusPaint = new SKPaint { ImageFilter = focusBlur };
                canvas.SaveLayer(focusPaint);
                canvas.DrawImage(pageImage, destRect, s_sampling);
                canvas.Restore(); // layer
                canvas.Restore(); // clip

                // Pass 2: Draw just the active line sharp
                canvas.Save();
                canvas.ClipRect(lineRect);
                canvas.DrawImage(pageImage, destRect, s_sampling);
                canvas.Restore(); // clip
            }
        }

        if (!didLineFocusBlur)
            canvas.DrawImage(pageImage, destRect, s_sampling);

        if (needsColourLayer)
            canvas.Restore();

        // --- Switch to page-point coordinate space for overlays ---
        canvas.Save();
        canvas.Scale(scaleX, scaleY);

        // --- Layer 2: Rail overlay ---
        if (options.RailOverlay && doc.Rail.Active && doc.Rail.HasAnalysis)
        {
            var effect = colourEffects.Effect;
            DrawRailOverlay(canvas, doc, effect.GetOverlayPalette(), options.LineFocusBlur,
                options.LineHighlightTint, options.LineHighlightOpacity);
        }

        // --- Layer 3: Search highlights ---
        if (options.SearchHighlights)
            DrawSearchHighlights(canvas, controller);

        // --- Layer 4: Annotations ---
        if (options.Annotations && doc.Annotations is not null)
        {
            var pageAnnotations = doc.Annotations.Pages.TryGetValue(doc.CurrentPage, out var list) ? list : null;
            if (pageAnnotations is not null && pageAnnotations.Count > 0)
                AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, null);
        }

        // --- Layer 5: Debug overlay ---
        if (options.DebugOverlay && doc.AnalysisCache.TryGetValue(doc.CurrentPage, out var analysis))
            DrawDebugOverlay(canvas, analysis);

        canvas.Restore(); // undo scale

        // --- Viewport cropping ---
        if (options.SimulateViewport)
            return CropToViewport(surface, doc, options, scaleX, scaleY);

        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }

    /// <summary>
    /// Crops the rendered full-page surface to the camera viewport.
    /// </summary>
    private static SKBitmap CropToViewport(
        SKSurface fullPageSurface,
        DocumentState doc,
        ScreenshotOptions options,
        float scaleX, float scaleY)
    {
        double zoom = doc.Camera.Zoom;
        double offsetX = doc.Camera.OffsetX;
        double offsetY = doc.Camera.OffsetY;
        int vpW = options.ViewportWidth;
        int vpH = options.ViewportHeight;

        // The camera maps page coordinates to screen: screenX = pageX * zoom + offsetX
        // So visible page rect starts at: pageX = -offsetX / zoom
        // The visible page rect in page-point space:
        double visiblePageX = -offsetX / zoom;
        double visiblePageY = -offsetY / zoom;
        double visiblePageW = vpW / zoom;
        double visiblePageH = vpH / zoom;

        // Convert to bitmap coordinates
        float srcX = (float)(visiblePageX * scaleX);
        float srcY = (float)(visiblePageY * scaleY);
        float srcW = (float)(visiblePageW * scaleX);
        float srcH = (float)(visiblePageH * scaleY);
        var srcRect = SKRect.Create(srcX, srcY, srcW, srcH);

        // Output at viewport resolution (or scale proportionally)
        int outW = vpW;
        int outH = vpH;
        var outInfo = new SKImageInfo(outW, outH);
        using var outSurface = SKSurface.Create(outInfo)
            ?? throw new InvalidOperationException($"Failed to create viewport surface ({outW}x{outH})");

        using var fullImage = fullPageSurface.Snapshot();
        var dstRect = SKRect.Create(0, 0, outW, outH);
        outSurface.Canvas.DrawImage(fullImage, srcRect, dstRect, s_sampling);

        using var outImage = outSurface.Snapshot();
        return SKBitmap.FromImage(outImage);
    }

    /// <summary>
    /// Saves a bitmap as a PNG file.
    /// </summary>
    public static void SavePng(SKBitmap bitmap, string outputPath, int quality = 90)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    private static void DrawRailOverlay(SKCanvas canvas, DocumentState doc, OverlayPalette palette, bool lineFocusBlur,
        LineHighlightTint tint = LineHighlightTint.Auto, double tintOpacity = 0.25)
    {
        if (doc.Rail.NavigableCount == 0) return;
        var block = doc.Rail.CurrentNavigableBlock;
        float margin = 4f;
        var blockRect = SKRect.Create(
            block.BBox.X - margin, block.BBox.Y - margin,
            block.BBox.W + margin * 2, block.BBox.H + margin * 2);

        var pageRect = SKRect.Create(0, 0, (float)doc.PageWidth, (float)doc.PageHeight);

        using var dimPaint = new SKPaint { Color = palette.Dim };
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

        if (!palette.DimExcludesBlock && palette.BlockReveal is var (revealColor, blendMode))
        {
            canvas.Save();
            canvas.ClipRect(blockRect);
            using var revealPaint = new SKPaint { Color = revealColor, BlendMode = blendMode };
            canvas.DrawRect(blockRect, revealPaint);
            canvas.Restore();
        }

        var bboxRect = SKRect.Create(block.BBox.X, block.BBox.Y, block.BBox.W, block.BBox.H);
        using var outlinePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            Color = palette.BlockOutline,
            StrokeWidth = palette.BlockOutlineWidth,
        };
        canvas.DrawRect(bboxRect, outlinePaint);

        var line = doc.Rail.CurrentLineInfo;
        using var linePaint = new SKPaint();

        if (lineFocusBlur)
        {
            const float barWidth = 3f;
            float pad = line.Height * 0.15f;
            linePaint.Color = palette.BlockOutline.WithAlpha(200);
            canvas.DrawRect(SKRect.Create(
                block.BBox.X - margin - barWidth,
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

    private static readonly SKColor[] s_debugColors =
    [
        new(244, 67, 54), new(33, 150, 243), new(76, 175, 80),
        new(255, 152, 0), new(156, 39, 176), new(0, 188, 212),
    ];

    private static void DrawDebugOverlay(SKCanvas canvas, PageAnalysis analysis)
    {
        using var font = new SKFont(SKTypeface.Default, 8);
        using var fillPaint = new SKPaint();
        using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 200) };
        using var textPaint = new SKPaint { IsAntialias = true };

        foreach (var block in analysis.Blocks)
        {
            var color = s_debugColors[block.ClassId % s_debugColors.Length];
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

    private static void DrawSearchHighlights(SKCanvas canvas, DocumentController controller)
    {
        var matches = controller.CurrentPageSearchMatches;
        if (matches is null || matches.Count == 0) return;

        int activeGlobal = controller.ActiveMatchIndex;
        var doc = controller.ActiveDocument;
        if (doc is null) return;

        using var highlightPaint = new SKPaint { Color = new SKColor(255, 255, 0, 100), IsAntialias = true };
        using var activePaint = new SKPaint { Color = new SKColor(255, 165, 0, 160), IsAntialias = true };

        int activeLocalIndex = -1;
        if (activeGlobal >= 0 && activeGlobal < controller.SearchMatches.Count)
        {
            var activeMatch = controller.SearchMatches[activeGlobal];
            if (activeMatch.PageIndex == doc.CurrentPage)
                activeLocalIndex = matches.IndexOf(activeMatch);
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var paint = i == activeLocalIndex ? activePaint : highlightPaint;
            foreach (var rect in matches[i].Rects)
                canvas.DrawRect(rect, paint);
        }
    }
}
