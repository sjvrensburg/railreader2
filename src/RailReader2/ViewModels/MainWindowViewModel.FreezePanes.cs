using System;
using System.Collections.Generic;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.ViewModels;

/// <summary>Which axis (or axes) a freeze placement sets, and what the guide-line cursor shows.</summary>
public enum FreezeMode { None, Rows, Columns, Both }

// Excel-style "Freeze Panes", page-wide and free-placement: the user picks a mode (rows / columns /
// both), the pointer becomes a matching guide line (horizontal → freeze everything ABOVE it,
// vertical → everything LEFT of it, both → a crossing pair), and a click drops the split at exactly
// that point — no snapping to detected boundaries (which are sometimes wrong) and no dependence on
// table detection at all. The frozen page strips pin in screen space while the body scrolls; they are
// captured at the FREEZE-TIME camera (zoom + offsets), so they show exactly what was above / left of
// the split when frozen (not the page's absolute top-left → no jump) and the top/left bands track the
// body's live pan on their free axis so a table's header/labels stay aligned with the cell being read.
// To keep that alignment, ZOOM IS DISABLED while frozen (every zoom entry point toasts and no-ops); the
// crops therefore render once and any zoom that escapes the lock self-clears the freeze (GetFreezeTiles).
// Three crop images are rendered from the page bitmap and composited by FreezePaneLayer; the SKImage
// lifecycle mirrors PdfPageLayer (retire on the composition thread).
//
// Freeze state is PER-VIEWPORT (keyed by Core Viewport): with several views on one document (split
// panes / tear-offs / duplicate tabs) each view freezes independently. The split (FreezeX/FreezeY,
// page-space; 0 = that axis not frozen) is bound to the page + zoom it was set on and clears when the
// view leaves that page, zooms, is resized, or on Unfreeze. Axes compose: freeze rows, then add cols.
public sealed partial class MainWindowViewModel
{
    /// <summary>Pre-rendered frozen-pane crops + their page-space regions for one frame, plus the
    /// freeze-time camera that anchors them. Any crop may be null (a rows-only / columns-only freeze has
    /// no left / top region). The dst rects are derived in the view layer: the corner pins at the
    /// freeze-time screen position, the top band tracks the body's live horizontal pan, the left band
    /// the live vertical pan — all at the freeze-time zoom (zoom is locked while frozen).</summary>
    internal readonly record struct FreezeTiles(
        SKImage? Corner, SKImage? Top, SKImage? Left,
        BBox CornerBox, BBox TopBox, BBox LeftBox,
        float Zoom, float OffsetX, float OffsetY);

    /// <summary>One viewport's active freeze: the page it applies to, the two page-space split lines,
    /// the freeze-time camera (the panes are anchored to the view as it was when frozen, not to the page
    /// origin — so freezing never yanks the page-top into view), and the lazily-rendered crops + DPI.</summary>
    private sealed class FreezeState
    {
        public required int Page;
        public required float FreezeX;   // page-space: columns left of this freeze (0 = none)
        public required float FreezeY;   // page-space: rows above this freeze (0 = none)
        public required float Zoom;      // camera zoom at freeze time (locked while frozen)
        public required float OffsetX;   // camera offset at freeze time (screen-space)
        public required float OffsetY;
        public SKImage? Corner, Top, Left;
        public BBox CornerBox, TopBox, LeftBox;
        public int Dpi; // 0 = not yet rendered for this anchor
    }

    private readonly Dictionary<Viewport, FreezeState> _freezeByVp = new();
    // Crops awaiting disposal, per viewport — drained by that viewport's GetFreezeTiles and disposed on
    // its FreezePaneLayer's composition thread (where OnRender isn't concurrently using them).
    private readonly Dictionary<Viewport, List<SKImage>> _freezeRetired = new();

    private Viewport? FreezeVp => _controller.FocusedViewport;

    /// <summary>True when the focused view has an active freeze (drives the Unfreeze control).</summary>
    public bool IsFrozen => FreezeVp is { } vp && _freezeByVp.ContainsKey(vp);

    /// <summary>True when a freeze can be placed on the focused view (a page is loaded). Freeze is
    /// page-wide and needs no table, so this is just "a document/page is available".</summary>
    public bool CanFreeze => FreezeVp is { PageWidth: > 0 };

    /// <summary>True while a freeze placement is armed. The toolbar overlay dims and stops hit-testing
    /// during this window so it doesn't block the viewport click that drops the split.</summary>
    public bool FreezePlacementArmed => FreezeArmMode != FreezeMode.None;

    /// <summary>Toolbar opacity: dimmed while a freeze placement is armed, opaque otherwise.</summary>
    public double ToolBarOpacity => FreezePlacementArmed ? 0.35 : 1.0;

    /// <summary>Arm (or toggle off) a freeze placement in <paramref name="mode"/>. While armed the
    /// pointer shows the matching guide line(s); the next viewport click drops the split there.</summary>
    public void ArmFreeze(FreezeMode mode)
    {
        FreezeArmMode = FreezeArmMode == mode ? FreezeMode.None : mode;
        if (FreezeArmMode != FreezeMode.None)
        {
            // Only one viewport-click gesture may be armed at a time; disarm "start rail here".
            ArmActivateRailClick = false;
            ShowStatusToast(FreezeArmMode switch
            {
                FreezeMode.Rows => "Click to freeze everything above the line",
                FreezeMode.Columns => "Click to freeze everything left of the line",
                _ => "Click to freeze everything above and left of the lines",
            });
        }
    }

    /// <summary>Commit an armed placement at a page-space point in <paramref name="vp"/>: set the row
    /// split (rows above pin) and/or column split (columns left pin) per the armed mode — free, clamped
    /// only to the page bounds (no snapping). Axes compose with an existing freeze on the same page.
    /// Disarms.</summary>
    public void PlaceFreeze(Viewport? vp, double pageX, double pageY)
    {
        var mode = FreezeArmMode;
        FreezeArmMode = FreezeMode.None;
        if (vp is null || mode == FreezeMode.None) return;

        // Start from the existing freeze on this page so Rows-then-Columns builds up to "both".
        float fx = 0f, fy = 0f;
        if (_freezeByVp.TryGetValue(vp, out var prev) && prev.Page == vp.CurrentPage)
        {
            fx = prev.FreezeX;
            fy = prev.FreezeY;
        }
        if (mode is FreezeMode.Rows or FreezeMode.Both)
            fy = Math.Clamp((float)pageY, 0f, (float)vp.PageHeight);
        if (mode is FreezeMode.Columns or FreezeMode.Both)
            fx = Math.Clamp((float)pageX, 0f, (float)vp.PageWidth);

        SetFreeze(vp, vp.CurrentPage, fx, fy);
    }

    private void SetFreeze(Viewport vp, int page, float freezeX, float freezeY)
    {
        ClearFreeze(vp); // retire any previous crops on this view
        if (freezeX <= 0.5f && freezeY <= 0.5f) // nothing frozen → leave cleared
        {
            RaiseFreezeStateIfFocused(vp);
            InvalidateNavigation();
            return;
        }
        // Anchor to the camera as it is right now: the frozen panes capture whatever is currently above /
        // left of the split (not the page's absolute top-left) and pin it where it sits, so freezing a
        // mid-page table's header freezes just that header and the view never jumps.
        _freezeByVp[vp] = new FreezeState
        {
            Page = page, FreezeX = freezeX, FreezeY = freezeY,
            Zoom = (float)vp.Camera.Zoom,
            OffsetX = (float)vp.Camera.OffsetX,
            OffsetY = (float)vp.Camera.OffsetY,
        };
        RaiseFreezeStateIfFocused(vp);
        InvalidateNavigation();
    }

    /// <summary>Whether a specific view currently has a freeze (drives each pane's Unfreeze chip).</summary>
    public bool IsViewportFrozen(Viewport? vp) => vp is not null && _freezeByVp.ContainsKey(vp);

    /// <summary>Clear a specific view's freeze (the per-pane Unfreeze chip). The calling pane refreshes
    /// its own freeze layer; here we just drop the state and keep the focused-view toggle in sync.</summary>
    public void UnfreezeViewport(Viewport? vp)
    {
        if (vp is null || !_freezeByVp.ContainsKey(vp)) return;
        ClearFreeze(vp);
        RaiseFreezeStateIfFocused(vp);
        InvalidateNavigation();
    }

    /// <summary>Release the focused view's freeze and repaint.</summary>
    public void Unfreeze()
    {
        if (FreezeVp is not { } vp || !_freezeByVp.ContainsKey(vp)) return;
        ClearFreeze(vp);
        OnPropertyChanged(nameof(IsFrozen));
        OnPropertyChanged(nameof(CanFreeze));
        InvalidateNavigation();
    }

    /// <summary>Zoom is disabled while the focused view is frozen: the frozen panes are captured at the
    /// freeze-time zoom, so zooming the body would slide the header/labels out of alignment with the
    /// cells they describe — defeating the feature. Returns true (and toasts) when a zoom request should
    /// be swallowed. Gated on every zoom entry point (wheel/keys/fit/percent).</summary>
    public bool ZoomBlockedByFreeze()
    {
        if (!IsFrozen) return false;
        ShowStatusToast("Zoom is disabled while panes are frozen — unfreeze (Z) to zoom");
        return true;
    }

    /// <summary>After a pan, keep the focused frozen view from scrolling its body back past the split.</summary>
    public void ClampFrozenCameraAfterPan()
    {
        if (FreezeVp is { } vp) ClampFrozenCamera(vp);
    }

    /// <summary>Keep a frozen view from scrolling its body back past the split — the body pane only ever
    /// shows content beyond the split, so the frozen rows/columns are never revealed (duplicated) in the
    /// body. Zoom is locked while frozen, so the freeze-time offsets are the exact limits; pans/snaps
    /// toward the page end are unaffected. Applied after a manual pan AND after every per-frame tick
    /// (rail line-snap and auto-scroll re-aim the camera each frame, including the horizontal snap-to-
    /// line-start that would otherwise slide the row labels out from behind the frozen column). No-op
    /// unless <paramref name="vp"/> is frozen on its current page.</summary>
    internal void ClampFrozenCamera(Viewport vp)
    {
        if (!_freezeByVp.TryGetValue(vp, out var f) || f.Page != vp.CurrentPage) return;
        if (f.FreezeY > 0.5f && vp.Camera.OffsetY > f.OffsetY) vp.Camera.OffsetY = f.OffsetY;
        if (f.FreezeX > 0.5f && vp.Camera.OffsetX > f.OffsetX) vp.Camera.OffsetX = f.OffsetX;
    }

    private void RaiseFreezeStateIfFocused(Viewport vp)
    {
        if (!ReferenceEquals(vp, FreezeVp)) return;
        OnPropertyChanged(nameof(IsFrozen));
        OnPropertyChanged(nameof(CanFreeze));
    }

    /// <summary>Drop <paramref name="vp"/>'s freeze, moving its crops to that view's retire queue.</summary>
    private void ClearFreeze(Viewport vp)
    {
        if (!_freezeByVp.Remove(vp, out var f)) return;
        RetireCrops(vp, f);
    }

    /// <summary>Current frozen-pane crops for <paramref name="vp"/>, rendered lazily and refreshed when
    /// the zoom changes enough to matter. Returns null when this view isn't frozen or the anchor no
    /// longer applies (left the page); the caller still drains <paramref name="retired"/> for disposal.</summary>
    internal FreezeTiles? GetFreezeTiles(Viewport vp, out IReadOnlyList<SKImage> retired)
    {
        if (_freezeByVp.TryGetValue(vp, out var f))
        {
            // A freeze belongs to the page AND zoom it was set on. Leaving the page invalidates it; so does
            // any zoom change that escaped the freeze zoom-lock (a block-frame, portal jump, rail-zoom snap,
            // or an in-flight zoom animation that outlived the freeze click) — clear rather than draw the
            // captured panes misaligned with the now-differently-zoomed body. The 0.1% epsilon ignores
            // float noise; while genuinely locked the zoom stays bit-identical so this never fires.
            if (vp.CurrentPage != f.Page || Math.Abs(vp.Camera.Zoom - f.Zoom) > f.Zoom * 0.001)
            {
                ClearFreeze(vp);
                RaiseFreezeStateIfFocused(vp);
            }
            else if (f.Dpi == 0)
            {
                // Render once at the freeze-time zoom (locked, so this never re-renders — panes never rescale).
                RenderFreezeCrops(vp, f, Math.Clamp((int)(72f * f.Zoom), 150, 600));
            }
        }

        retired = DrainRetired(vp);
        return _freezeByVp.TryGetValue(vp, out var ff)
            ? new FreezeTiles(ff.Corner, ff.Top, ff.Left, ff.CornerBox, ff.TopBox, ff.LeftBox,
                ff.Zoom, ff.OffsetX, ff.OffsetY)
            : null;
    }

    private void RenderFreezeCrops(Viewport vp, FreezeState f, int dpi)
    {
        RetireCrops(vp, f); // retire the previous DPI's crops
        float pageW = (float)vp.PageWidth, pageH = (float)vp.PageHeight;
        float fx = f.FreezeX, fy = f.FreezeY;

        // The frozen content is what was VISIBLE above / left of the split when frozen — not the page's
        // absolute top-left. topY0/leftX0 are the page coords at the viewport's top / left edge at freeze
        // time (clamped into the frozen band), so a mid-page table's header freezes just the header and
        // the panes overlay the live view exactly at the instant of freezing (no jump).
        float topY0 = Math.Clamp(-f.OffsetY / f.Zoom, 0f, fy);
        float leftX0 = Math.Clamp(-f.OffsetX / f.Zoom, 0f, fx);

        // Page-space regions: corner (frozen rows ∧ frozen cols), top strip (frozen rows, over the body
        // columns → tracks horizontal pan), left strip (frozen cols, over the body rows → tracks vertical
        // pan). A rows-only freeze (fx == 0) has no corner/left; a columns-only freeze (fy == 0) has no
        // corner/top.
        f.CornerBox = new BBox(leftX0, topY0, fx - leftX0, fy - topY0);
        f.TopBox = new BBox(fx, topY0, pageW - fx, fy - topY0);
        f.LeftBox = new BBox(leftX0, fy, fx - leftX0, pageH - fy);

        bool hasCorner = f.CornerBox is { W: > 0.5f, H: > 0.5f };
        bool hasTop = f.TopBox is { W: > 0.5f, H: > 0.5f };
        bool hasLeft = f.LeftBox is { W: > 0.5f, H: > 0.5f };

        if (!hasCorner && !hasTop && !hasLeft) { f.Dpi = dpi; return; } // nothing to pin; no retry

        // Render the page once and crop the regions *exactly* (no padding): the strips span a full page
        // dimension, so any padding would accumulate into visible drift. Mark f.Dpi only on SUCCESS, so a
        // transient render failure retries next frame instead of sticking at this DPI with null crops.
        try
        {
            using var page = vp.Owner.Pdf.RenderPage(vp.CurrentPage, dpi);
            if (page is not SkiaRenderedPage skia) return; // leave f.Dpi 0 → retry next frame
            var bmp = skia.Bitmap;
            float sx = bmp.Width / pageW;
            float sy = bmp.Height / pageH;
            if (hasCorner) f.Corner = CropExact(bmp, f.CornerBox, sx, sy);
            if (hasTop) f.Top = CropExact(bmp, f.TopBox, sx, sy);
            if (hasLeft) f.Left = CropExact(bmp, f.LeftBox, sx, sy);
            f.Dpi = dpi;
        }
        catch (Exception ex)
        {
            _logger.Error("[Freeze] crop render failed", ex); // leave f.Dpi 0 → retry next frame
        }
    }

    /// <summary>Extract the exact page region (no padding) from the rendered page bitmap as an
    /// independent SKImage that maps 1:1 to its page-space <paramref name="box"/>.</summary>
    private static SKImage? CropExact(SKBitmap bmp, BBox box, float sx, float sy)
    {
        int left = Math.Clamp((int)MathF.Round(box.X * sx), 0, bmp.Width);
        int top = Math.Clamp((int)MathF.Round(box.Y * sy), 0, bmp.Height);
        int right = Math.Clamp((int)MathF.Round((box.X + box.W) * sx), 0, bmp.Width);
        int bottom = Math.Clamp((int)MathF.Round((box.Y + box.H) * sy), 0, bmp.Height);
        if (right - left <= 0 || bottom - top <= 0) return null;

        using var subset = new SKBitmap();
        if (!bmp.ExtractSubset(subset, new SKRectI(left, top, right, bottom))) return null;
        // ExtractSubset is a view over the page bitmap's pixels; round-trip through PNG to get an
        // image that survives the page bitmap's disposal.
        using var data = subset.Encode(SKEncodedImageFormat.Png, 100);
        return data is null ? null : SKImage.FromEncodedData(data);
    }

    // --- Per-viewport crop disposal ---

    private void RetireCrops(Viewport vp, FreezeState f)
    {
        if (f.Corner is null && f.Top is null && f.Left is null) return;
        if (!_freezeRetired.TryGetValue(vp, out var list))
            _freezeRetired[vp] = list = new List<SKImage>();
        if (f.Corner is not null) list.Add(f.Corner);
        if (f.Top is not null) list.Add(f.Top);
        if (f.Left is not null) list.Add(f.Left);
        f.Corner = f.Top = f.Left = null;
        f.Dpi = 0;
    }

    private IReadOnlyList<SKImage> DrainRetired(Viewport vp)
    {
        if (!_freezeRetired.TryGetValue(vp, out var list) || list.Count == 0)
            return Array.Empty<SKImage>();
        var arr = list.ToArray();
        list.Clear();
        return arr;
    }

    /// <summary>Remove a viewport's freeze (active + retired crops) and RETURN the crop images without
    /// disposing them, so a closing surface can retire them on its FreezePaneLayer's composition thread
    /// (the race-free path — a UI-thread dispose could free a bitmap mid-OnRender). Used by
    /// <c>DocumentView</c> on close of a split pane / tear-off that still has its layer attached.</summary>
    internal IReadOnlyList<SKImage> TakeFreezeCrops(Viewport vp)
    {
        var imgs = new List<SKImage>();
        if (_freezeByVp.Remove(vp, out var f))
        {
            if (f.Corner is not null) imgs.Add(f.Corner);
            if (f.Top is not null) imgs.Add(f.Top);
            if (f.Left is not null) imgs.Add(f.Left);
        }
        if (_freezeRetired.Remove(vp, out var list))
            imgs.AddRange(list);
        return imgs;
    }

    /// <summary>Dispose a removed viewport's freeze crops directly (the view is gone — its layer will
    /// never drain the retire queue). Called when a tab closes (its Document pane has already rebound to
    /// another tab, so its layer no longer references these crops).</summary>
    internal void DisposeFreezeFor(Viewport vp)
    {
        if (_freezeByVp.Remove(vp, out var f))
        {
            f.Corner?.Dispose(); f.Top?.Dispose(); f.Left?.Dispose();
        }
        if (_freezeRetired.Remove(vp, out var list))
            foreach (var img in list) img.Dispose();
    }

    /// <summary>When the armed freeze mode changes — most importantly when it clears to
    /// <see cref="FreezeMode.None"/> (Escape, tab switch, re-pressing the mode) — re-render every
    /// surface's freeze layer so the guide line drops immediately. Without this, a disarm that isn't
    /// followed by a pointer move would leave the last guide painted (the layer only repaints on the
    /// camera path or a SetFreezeGuide change).</summary>
    partial void OnFreezeArmModeChanged(FreezeMode value)
    {
        OnPropertyChanged(nameof(FreezePlacementArmed));
        OnPropertyChanged(nameof(ToolBarOpacity));
        foreach (var s in _surfaces)
            s.RenderFreezePanes();
    }

    /// <summary>Dispose all freeze crops (active + pending-retired) on VM teardown.</summary>
    internal void DisposeFreezeImages()
    {
        foreach (var f in _freezeByVp.Values)
        {
            f.Corner?.Dispose(); f.Top?.Dispose(); f.Left?.Dispose();
        }
        _freezeByVp.Clear();
        foreach (var list in _freezeRetired.Values)
            foreach (var img in list) img.Dispose();
        _freezeRetired.Clear();
    }
}
