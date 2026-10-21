using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>An open link preview: a rendered crop of where an internal link goes, shown in a popup at
/// the click by the <see cref="Views.ViewportPanel"/> that was clicked. The popup owns
/// <see cref="Image"/> and disposes it when it closes.</summary>
public sealed record LinkPeek(Bitmap Image, int Page, PageDestination Destination);

// Link peek (LinkClickAction.Peek): a plain click on an internal link previews its destination instead
// of jumping there, so a stray click costs nothing and a reference or equation can be read in place.
public sealed partial class MainWindowViewModel
{
    // Width of the preview, as a fraction of the view, and its cap in pixels.
    private const double LinkPeekWidthFraction = 0.6;
    private const double LinkPeekMaxWidthPx = 900;
    private const int LinkPeekMaxDpi = 200;

    /// <summary>The open link preview, or null. Set to null to close it (Escape, Go, a new peek).</summary>
    [ObservableProperty] private LinkPeek? _openLinkPeek;

    public bool IsLinkPeekOpen => OpenLinkPeek is not null;

    public void CloseLinkPeek() => OpenLinkPeek = null;

    /// <summary>The popup's "Go to page" button: closes the preview and follows the link the way a click
    /// would have, with the same toast and Back.</summary>
    public void FollowPeekedLink()
    {
        var peek = OpenLinkPeek;
        OpenLinkPeek = null;
        if (peek is null || _controller.FocusedViewport is not { } vp) return;
        int pageBefore = vp.CurrentPage, backBefore = vp.BackStackCount;
        if (!_controller.FollowLink(peek.Destination))
        {
            // A confined (portal) view never follows internal links, or the page failed to render.
            ShowStatusToast($"Can't go to page {peek.Page + 1} from this view");
            return;
        }
        ToastIfLinkFollowed(vp, pageBefore, backBefore);
        InvalidateNavigation();
    }

    /// <summary>Render where <paramref name="dest"/> lands: the detected block there when the page is
    /// analysed, else a band from the destination down (Core's <c>ResolveDestinationPreview</c>), in the
    /// document's colour effect so a dark-mode reader isn't flashed with a white page. Null if nothing
    /// could be rendered. UI thread only (PDFium).</summary>
    private LinkPeek? BuildLinkPeek(Viewport vp, PageDestination dest)
    {
        if (_controller.ResolveDestinationPreview(dest) is not { } preview) return null;
        var doc = vp.Owner;
        int rotation = doc.ViewRotation;
        var (pageW, pageH) = doc.Pdf.GetPageSize(preview.Page, rotation);
        if (pageW <= 0 || pageH <= 0) return null;

        // A little context around a block; the band fallback is already full width.
        var rect = preview.BlockIndex is null ? preview.Rect : preview.Rect.Inflated(6, 6);
        rect = new RectF(
            Math.Max(0, rect.Left), Math.Max(0, rect.Top),
            (float)Math.Min(pageW, rect.Right), (float)Math.Min(pageH, rect.Bottom));
        if (rect.Width < 1 || rect.Height < 1) return null;

        // The view's size is in DIPs; render device pixels so a HiDPI screen gets a sharp, full-size preview.
        double scaling = _window?.RenderScaling ?? 1.0;
        double targetPx = scaling * Math.Min(LinkPeekMaxWidthPx, Math.Max(320, vp.Width * LinkPeekWidthFraction));
        // The whole page is rasterised (on the UI thread) to crop from it, so cap the DPI: a tiny target
        // (an equation number) would otherwise ask for a huge page for a few hundred pixels.
        int dpi = (int)Math.Clamp(targetPx / (rect.Width / 72.0), 96, LinkPeekMaxDpi * scaling);

        try
        {
            using var rendered = doc.Pdf.RenderPage(preview.Page, dpi, rotation);
            if (rendered is not SkiaRenderedPage skiaPage) return null;
            var bitmap = skiaPage.Bitmap;
            float sx = bitmap.Width / (float)pageW, sy = bitmap.Height / (float)pageH;
            var src = SKRectI.Round(new SKRect(rect.Left * sx, rect.Top * sy, rect.Right * sx, rect.Bottom * sy));
            src.Intersect(new SKRectI(0, 0, bitmap.Width, bitmap.Height));
            if (src.Width < 1 || src.Height < 1) return null;

            using var surface = SKSurface.Create(new SKImageInfo(src.Width, src.Height));
            using var paint = ColourEffects.CreatePaint(doc.ColourEffect, _controller.ActiveColourIntensity);
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawBitmap(bitmap, src, new SKRect(0, 0, src.Width, src.Height), paint);
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = png.AsStream();
            return new LinkPeek(new Bitmap(stream), preview.Page, dest);
        }
        catch (Exception ex)
        {
            _logger.Error("[LinkPeek] Failed to render link preview", ex);
            return null;
        }
    }
}
