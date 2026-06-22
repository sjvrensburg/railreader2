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
// table detection at all. The frozen page strips pin in screen space while the body scrolls. Three
// crop images are rendered from the page bitmap and composited by FreezePaneLayer; the SKImage
// lifecycle mirrors PdfPageLayer (retire on the composition thread).
//
// Freeze state is PER-VIEWPORT (keyed by Core Viewport): with several views on one document (split
// panes / tear-offs / duplicate tabs) each view freezes independently. The split (FreezeX/FreezeY,
// page-space; 0 = that axis not frozen) is bound to the page it was set on and clears when the view
// leaves that page or on Unfreeze. Axes compose: freeze rows, then add a column freeze, etc.
public sealed partial class MainWindowViewModel
{
    /// <summary>Pre-rendered frozen-pane crops + their page-space regions for one frame.
    /// Any field may be null (a rows-only / columns-only freeze has no left / top region).</summary>
    internal readonly record struct FreezeTiles(
        SKImage? Corner, SKImage? Top, SKImage? Left,
        BBox CornerBox, BBox TopBox, BBox LeftBox);

    /// <summary>One viewport's active freeze: the page it applies to, the two page-space split lines,
    /// and the lazily-rendered crops + the DPI they were rendered at.</summary>
    private sealed class FreezeState
    {
        public required int Page;
        public required float FreezeX;   // page-space: columns left of this freeze (0 = none)
        public required float FreezeY;   // page-space: rows above this freeze (0 = none)
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

    /// <summary>Arm (or toggle off) a freeze placement in <paramref name="mode"/>. While armed the
    /// pointer shows the matching guide line(s); the next viewport click drops the split there.</summary>
    public void ArmFreeze(FreezeMode mode)
    {
        FreezeArmMode = FreezeArmMode == mode ? FreezeMode.None : mode;
        if (FreezeArmMode != FreezeMode.None)
            ShowStatusToast(FreezeArmMode switch
            {
                FreezeMode.Rows => "Click to freeze everything above the line",
                FreezeMode.Columns => "Click to freeze everything left of the line",
                _ => "Click to freeze everything above and left of the lines",
            });
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
        _freezeByVp[vp] = new FreezeState { Page = page, FreezeX = freezeX, FreezeY = freezeY };
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
            if (vp.CurrentPage != f.Page) // a page-wide freeze belongs to the page it was set on
            {
                ClearFreeze(vp);
                RaiseFreezeStateIfFocused(vp);
            }
            else
            {
                // Render at a zoom-proportional DPI so the pinned strips are as crisp as the live page;
                // re-render only when the zoom diverges by >1.5x (page-tier hysteresis).
                int desiredDpi = Math.Clamp((int)(72f * (float)vp.Camera.Zoom), 150, 600);
                if (f.Dpi == 0 || desiredDpi > f.Dpi * 1.5f || desiredDpi * 1.5f < f.Dpi)
                    RenderFreezeCrops(vp, f, desiredDpi);
            }
        }

        retired = DrainRetired(vp);
        return _freezeByVp.TryGetValue(vp, out var ff)
            ? new FreezeTiles(ff.Corner, ff.Top, ff.Left, ff.CornerBox, ff.TopBox, ff.LeftBox)
            : null;
    }

    private void RenderFreezeCrops(Viewport vp, FreezeState f, int dpi)
    {
        RetireCrops(vp, f); // retire the previous DPI's crops
        float pageW = (float)vp.PageWidth, pageH = (float)vp.PageHeight;
        float fx = f.FreezeX, fy = f.FreezeY;

        // Page-space regions: corner (above ∧ left of the split), top strip (above, right of the
        // column split), left strip (left of the column split, below the row split). Full page extent —
        // a rows-only freeze (fx == 0) has no corner/left; a columns-only freeze (fy == 0) has no
        // corner/top.
        f.CornerBox = new BBox(0, 0, fx, fy);
        f.TopBox = new BBox(fx, 0, pageW - fx, fy);
        f.LeftBox = new BBox(0, fy, fx, pageH - fy);

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
