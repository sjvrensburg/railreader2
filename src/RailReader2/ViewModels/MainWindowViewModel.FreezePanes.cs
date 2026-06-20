using System;
using System.Collections.Generic;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.ViewModels;

// Excel-style "Freeze Panes" for table reading: freezing at the active cell pins the rows *above* and
// columns *left of* that cell while the body keeps scrolling. The anchor is captured once (two
// page-space split lines) and stays fixed until Unfreeze or leaving the table. Three crop images are
// rendered once via BlockCropRenderer and composited screen-space by FreezePaneLayer; the SKImage
// lifecycle mirrors PdfPageLayer (retire on the composition thread).
public sealed partial class MainWindowViewModel
{
    /// <summary>Pre-rendered frozen-pane crops + their page-space regions for one frame.
    /// Any field may be null (a freeze at row 0 / column 0 has no top / left region).</summary>
    internal readonly record struct FreezeTiles(
        SKImage? Corner, SKImage? Top, SKImage? Left,
        BBox CornerBox, BBox TopBox, BBox LeftBox);

    // Active freeze anchor (transient — not persisted). Null = not frozen.
    private LayoutBlock? _freezeBlock;
    private int _freezePage = -1;
    private float _freezeX, _freezeY; // page-space split lines: cols left of X and rows above Y freeze

    // Cached crops for the active freeze + the page-space regions they cover.
    private SKImage? _freezeCorner, _freezeTop, _freezeLeft;
    private BBox _freezeCornerBox, _freezeTopBox, _freezeLeftBox;
    private int _freezeDpi; // 0 = not yet rendered for the current anchor

    // Crops awaiting disposal, handed to FreezePaneLayer (composition thread) by GetFreezeTiles.
    private readonly List<SKImage> _freezeRetired = new();

    /// <summary>True when a freeze is active (drives the Table Reading panel's Freeze/Unfreeze toggle).</summary>
    public bool IsFrozen => _freezeBlock is not null;

    /// <summary>True when the rail is on a table cell, so a freeze can be set here.</summary>
    public bool CanFreeze =>
        ActiveTab?.Rail is { Active: true, HasAnalysis: true, HasCells: true } rail
        && rail.CurrentNavigableBlock.Role == BlockRole.Table
        && rail.CurrentCellInfo is not null;

    /// <summary>Toggle freeze: freeze at the current cell if not frozen, else release.</summary>
    public void ToggleFreeze()
    {
        if (IsFrozen) Unfreeze();
        else FreezeAtCurrentCell();
    }

    /// <summary>Capture the active cell as the freeze anchor: rows above and columns left of it pin.</summary>
    public void FreezeAtCurrentCell()
    {
        if (ActiveTab is not { } tab) return;
        if (tab.Rail is not { Active: true, HasAnalysis: true, HasCells: true } rail) return;
        if (rail.CurrentNavigableBlock.Role != BlockRole.Table) return;
        if (rail.CurrentCellInfo is not { } cell) return;

        var line = rail.CurrentLineInfo;
        _freezeBlock = rail.CurrentNavigableBlock;
        _freezePage = tab.State.CurrentPage;
        _freezeX = cell.X;
        _freezeY = line.Y - line.Height / 2f;
        RetireFreezeCrops(); // force a fresh render at next GetFreezeTiles
        OnPropertyChanged(nameof(IsFrozen));
        InvalidateNavigation();
    }

    /// <summary>Release the freeze and repaint.</summary>
    public void Unfreeze()
    {
        if (_freezeBlock is null) return;
        ClearFreeze();
        InvalidateNavigation();
    }

    /// <summary>Drop the freeze without triggering a repaint — for callers already inside an
    /// invalidation pass (the table-leave / tab-switch paths).</summary>
    internal void ClearFreeze()
    {
        if (_freezeBlock is null) return;
        _freezeBlock = null;
        _freezePage = -1;
        RetireFreezeCrops();
        OnPropertyChanged(nameof(IsFrozen));
        OnPropertyChanged(nameof(CanFreeze));
    }

    /// <summary>Current frozen-pane crops for <paramref name="tab"/>, rendered lazily and refreshed when
    /// the zoom changes enough to matter. Returns null when not frozen or the anchor no longer applies
    /// (page/table changed); the caller still drains <paramref name="retired"/> for disposal.</summary>
    internal FreezeTiles? GetFreezeTiles(TabViewModel tab, out IReadOnlyList<SKImage> retired)
    {
        // Not frozen, or rendering a tab that isn't the active one (transient mid-switch) — skip
        // without disturbing the freeze.
        if (_freezeBlock is null || !ReferenceEquals(tab, ActiveTab))
        {
            retired = TakeRetired();
            return null;
        }

        // The anchor no longer applies — navigated off the frozen page/table, exited rail, or a
        // re-analysis replaced the block. Release the freeze so IsFrozen reflects reality instead of
        // leaving the toggle stuck on "Unfreeze" with nothing pinned.
        if (tab.State.CurrentPage != _freezePage
            || tab.Rail is not { Active: true, NavigableCount: > 0 } rail
            || !ReferenceEquals(rail.CurrentNavigableBlock, _freezeBlock))
        {
            ClearFreeze();
            retired = TakeRetired();
            return null;
        }

        // Render the crops at a zoom-proportional DPI so the pinned tiles are as crisp as the live page;
        // re-render only when the zoom diverges by >1.5x from the DPI we used (hysteresis like the page tiers).
        int desiredDpi = Math.Clamp((int)(72f * (float)tab.Camera.Zoom), 150, 600);
        bool needRender = _freezeDpi == 0
            || desiredDpi > _freezeDpi * 1.5f || desiredDpi * 1.5f < _freezeDpi;
        if (needRender)
            RenderFreezeCrops(tab, desiredDpi);

        retired = TakeRetired();
        return new FreezeTiles(_freezeCorner, _freezeTop, _freezeLeft,
            _freezeCornerBox, _freezeTopBox, _freezeLeftBox);
    }

    private void RenderFreezeCrops(TabViewModel tab, int dpi)
    {
        RetireFreezeCrops();
        var bb = _freezeBlock!.BBox;
        float tableLeft = bb.X, tableTop = bb.Y, tableRight = bb.X + bb.W, tableBottom = bb.Y + bb.H;
        float fx = _freezeX, fy = _freezeY;

        // Page-space regions: corner (above ∧ left), top band (above, cols >= anchor), left band
        // (cols left, rows >= anchor). Zero-extent regions (freeze at row 0 / column 0) are skipped.
        _freezeCornerBox = new BBox(tableLeft, tableTop, fx - tableLeft, fy - tableTop);
        _freezeTopBox = new BBox(fx, tableTop, tableRight - fx, fy - tableTop);
        _freezeLeftBox = new BBox(tableLeft, fy, fx - tableLeft, tableBottom - fy);

        bool hasCorner = _freezeCornerBox is { W: > 0.5f, H: > 0.5f };
        bool hasTop = _freezeTopBox is { W: > 0.5f, H: > 0.5f };
        bool hasLeft = _freezeLeftBox is { W: > 0.5f, H: > 0.5f };

        _freezeDpi = dpi;
        if (!hasCorner && !hasTop && !hasLeft) return;

        // Render the page once and crop the regions *exactly* (no padding). BlockCropRenderer pads each
        // crop by 5%, which would scale/shift the pinned tile — and because the bands span a full table
        // dimension, that error accumulates into visible row/column drift.
        try
        {
            using var page = tab.Pdf.RenderPage(tab.State.CurrentPage, dpi);
            if (page is not SkiaRenderedPage skia) return;
            var bmp = skia.Bitmap;
            float sx = bmp.Width / (float)tab.PageWidth;
            float sy = bmp.Height / (float)tab.PageHeight;
            if (hasCorner) _freezeCorner = CropExact(bmp, _freezeCornerBox, sx, sy);
            if (hasTop) _freezeTop = CropExact(bmp, _freezeTopBox, sx, sy);
            if (hasLeft) _freezeLeft = CropExact(bmp, _freezeLeftBox, sx, sy);
        }
        catch (Exception ex)
        {
            _logger.Error("[Freeze] crop render failed", ex);
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

    private void RetireFreezeCrops()
    {
        if (_freezeCorner is not null) _freezeRetired.Add(_freezeCorner);
        if (_freezeTop is not null) _freezeRetired.Add(_freezeTop);
        if (_freezeLeft is not null) _freezeRetired.Add(_freezeLeft);
        _freezeCorner = _freezeTop = _freezeLeft = null;
        _freezeDpi = 0;
    }

    private IReadOnlyList<SKImage> TakeRetired()
    {
        if (_freezeRetired.Count == 0) return Array.Empty<SKImage>();
        var arr = _freezeRetired.ToArray();
        _freezeRetired.Clear();
        return arr;
    }

    /// <summary>Dispose all freeze crops (active + pending-retired) on VM teardown, so an active
    /// freeze doesn't leak its SKImages at shutdown. Called from <see cref="Dispose"/>.</summary>
    internal void DisposeFreezeImages()
    {
        _freezeCorner?.Dispose();
        _freezeTop?.Dispose();
        _freezeLeft?.Dispose();
        _freezeCorner = _freezeTop = _freezeLeft = null;
        foreach (var img in _freezeRetired) img.Dispose();
        _freezeRetired.Clear();
        _freezeBlock = null;
    }
}
