using System;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailReader2.Views;

/// <summary>
/// Immutable snapshot of all state needed to render the rail overlay for one frame.
/// </summary>
internal sealed record RailOverlayRenderState(
    SKMatrix Camera,
    float PageW,
    float PageH,
    LayoutBlock? CurrentBlock,
    LineInfo CurrentLine,
    bool DebugOverlay,
    PageAnalysis? DebugAnalysis,
    string? DebugModelLabel,
    ColourEffect Effect,
    bool LineFocusBlur,
    bool LineHighlightEnabled,
    float LinePadding,
    LineHighlightTint Tint,
    float TintOpacity,
    // Table cell focus aids — set only when the rail is on a table row with cells. When TableCell is
    // non-null the overlay draws the scoped tint/dim itself, and LineHighlightEnabled/LineFocusBlur
    // above are passed false so the package line highlight + page dim don't double up.
    Services.TableFocusScope TableScope = Services.TableFocusScope.Cell,
    bool TableHighlight = false,
    bool TableDim = false,
    float TableDimIntensity = 0f,
    CellInfo? TableCell = null,
    Services.ColumnBand? TableColumn = null);

/// <summary>
/// Hosts a CompositionCustomVisual for the rail overlay (dim, block outline, line highlight).
/// Camera transform is applied inside Skia alongside the draw calls, eliminating
/// the intermediate compositing step that caused jitter on Windows/ANGLE.
/// </summary>
internal class RailOverlayLayer : CompositionLayerControl<RailOverlayVisualHandler>;

internal sealed class RailOverlayVisualHandler : CompositionCustomVisualHandler
{
    private RailOverlayRenderState? _state;

    // ColourEffect.GetOverlayPalette() allocates a fresh OverlayPalette (reference type)
    // each call; this runs on the composition thread every frame while rail-reading
    // (the camera animates per line advance). Cache by effect — the palette only changes
    // when the user cycles colour effects (C). Matches the ThreadStatic-cache pattern in
    // PdfPageVisualHandler.
    [ThreadStatic] private static OverlayPalette? s_cachedPalette;
    [ThreadStatic] private static ColourEffect s_cachedPaletteEffect;

    // Reused per-frame paints for the scoped table focus aids (tint fill + dim wash).
    [ThreadStatic] private static SKPaint? s_tableTintPaint;
    [ThreadStatic] private static SKPaint? s_tableDimPaint;

    public override void OnMessage(object message)
    {
        if (message is RailOverlayRenderState state)
        {
            _state = state;
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var state = _state;
        if (state is null) return;

        // No overlay content when rail is inactive and debug is off
        if (state.CurrentBlock is null && !state.DebugOverlay) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature leaseFeature)
            return;
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();
        canvas.Concat(state.Camera);

        if (state.CurrentBlock is { } block)
        {
            if (s_cachedPalette is null || s_cachedPaletteEffect != state.Effect)
            {
                s_cachedPalette = state.Effect.GetOverlayPalette();
                s_cachedPaletteEffect = state.Effect;
            }
            var palette = s_cachedPalette;
            OverlayRenderer.DrawRailOverlays(
                canvas, block, state.CurrentLine,
                state.PageW, state.PageH, palette,
                state.LineFocusBlur, state.LineHighlightEnabled,
                state.LinePadding, state.Tint, state.TintOpacity,
                OverlayRenderer.GetDimPaint(), OverlayRenderer.GetRevealPaint(),
                OverlayRenderer.GetOutlinePaint(), OverlayRenderer.GetLinePaint());

            // Scoped table focus aids (cell/row/column/row+column) drawn shell-side, after the package
            // overlays. Active only when seated on a table cell; the package line highlight + page dim
            // are suppressed for this frame (BuildOverlayState/BuildPageState pass their flags false).
            if (state.TableCell is { } tableCell)
                DrawTableFocus(canvas, state, block, palette, tableCell);
        }

        if (state.DebugOverlay && state.DebugAnalysis is { } analysis)
        {
            OverlayRenderer.DrawDebugOverlay(
                canvas, analysis,
                OverlayRenderer.GetDebugFont(),
                OverlayRenderer.GetDebugFillPaint(),
                OverlayRenderer.GetDebugStrokePaint(),
                OverlayRenderer.GetDebugBgPaint(),
                OverlayRenderer.GetDebugTextPaint());
        }

        if (state.DebugOverlay && state.DebugModelLabel is { Length: > 0 } modelLabel)
        {
            DrawModelBadge(canvas, modelLabel);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Draws the scoped table focus aids in page space: a tint over the focus region and/or a dim
    /// wash over everything outside it, for the cell / row / column / row+column scopes. The row band
    /// comes from the current line, the cell from <paramref name="cell"/>, and the column band (inferred
    /// shell-side) from <c>state.TableColumn</c> spanning the table block's height.
    /// </summary>
    private static void DrawTableFocus(SKCanvas canvas, RailOverlayRenderState state,
        LayoutBlock block, OverlayPalette palette, CellInfo cell)
    {
        var line = state.CurrentLine;
        float rowTop = line.Y - line.Height / 2f;
        float rowBottom = line.Y + line.Height / 2f;
        var rowRect = new SKRect(line.X, rowTop, line.X + line.Width, rowBottom);
        var cellRect = new SKRect(cell.X, rowTop, cell.X + cell.Width, rowBottom);

        // Column band spans the whole table block vertically; falls back to the cell column when no
        // band was inferred (so Column/Row+Column still frame something sensible). Clamp the inferred
        // X-range to the page so a ragged/over-wide band can't produce inverted dim rects.
        float bTop = block.BBox.Y, bBottom = block.BBox.Y + block.BBox.H;
        float colLeft = state.TableColumn is { } col ? col.X : cell.X;
        float colRight = state.TableColumn is { } col2 ? col2.X + col2.Width : cell.X + cell.Width;
        colLeft = Math.Clamp(colLeft, 0f, state.PageW);
        colRight = Math.Clamp(colRight, colLeft, state.PageW);
        var columnRect = new SKRect(colLeft, bTop, colRight, bBottom);

        if (state.TableHighlight)
        {
            var tintColor = palette.ResolveLineHighlight(state.Tint, state.TintOpacity).ToSKColor();
            s_tableTintPaint ??= new SKPaint();
            s_tableTintPaint.Color = tintColor;
            switch (state.TableScope)
            {
                case Services.TableFocusScope.Cell: canvas.DrawRect(cellRect, s_tableTintPaint); break;
                case Services.TableFocusScope.Row: canvas.DrawRect(rowRect, s_tableTintPaint); break;
                case Services.TableFocusScope.Column: canvas.DrawRect(columnRect, s_tableTintPaint); break;
                case Services.TableFocusScope.RowAndColumn:
                    // Row band, then the column only ABOVE and BELOW the row band — drawing the full
                    // column would tint the intersection cell twice (semi-transparent → too dark).
                    canvas.DrawRect(rowRect, s_tableTintPaint);
                    if (columnRect.Top < rowTop)
                        canvas.DrawRect(new SKRect(colLeft, columnRect.Top, colRight, rowTop), s_tableTintPaint);
                    if (columnRect.Bottom > rowBottom)
                        canvas.DrawRect(new SKRect(colLeft, rowBottom, colRight, columnRect.Bottom), s_tableTintPaint);
                    break;
            }
        }

        if (state.TableDim && state.TableDimIntensity > 0f)
        {
            byte alpha = (byte)Math.Clamp(state.TableDimIntensity * 255f, 0f, 255f);
            s_tableDimPaint ??= new SKPaint();
            s_tableDimPaint.Color = palette.Dim.WithAlpha(alpha).ToSKColor();
            float pw = state.PageW, ph = state.PageH;
            switch (state.TableScope)
            {
                case Services.TableFocusScope.Cell: DrawDimAround(canvas, s_tableDimPaint, cellRect, pw, ph); break;
                case Services.TableFocusScope.Row: DrawDimAround(canvas, s_tableDimPaint, rowRect, pw, ph); break;
                case Services.TableFocusScope.Column:
                    // Keep the full-height column strip clear; dim left and right of it.
                    canvas.DrawRect(new SKRect(0, 0, columnRect.Left, ph), s_tableDimPaint);
                    canvas.DrawRect(new SKRect(columnRect.Right, 0, pw, ph), s_tableDimPaint);
                    break;
                case Services.TableFocusScope.RowAndColumn:
                    // Keep the full-width row band and full-height column band clear (a cross); dim the
                    // four corner rectangles outside it.
                    canvas.DrawRect(new SKRect(0, 0, columnRect.Left, rowTop), s_tableDimPaint);
                    canvas.DrawRect(new SKRect(columnRect.Right, 0, pw, rowTop), s_tableDimPaint);
                    canvas.DrawRect(new SKRect(0, rowBottom, columnRect.Left, ph), s_tableDimPaint);
                    canvas.DrawRect(new SKRect(columnRect.Right, rowBottom, pw, ph), s_tableDimPaint);
                    break;
            }
        }
    }

    /// <summary>Dim the page outside a single focus rect — top/bottom full-width bands plus left/right
    /// fillers level with the rect.</summary>
    private static void DrawDimAround(SKCanvas canvas, SKPaint dim, SKRect f, float pw, float ph)
    {
        if (f.Top > 0) canvas.DrawRect(new SKRect(0, 0, pw, f.Top), dim);
        if (f.Bottom < ph) canvas.DrawRect(new SKRect(0, f.Bottom, pw, ph), dim);
        if (f.Left > 0) canvas.DrawRect(new SKRect(0, f.Top, f.Left, f.Bottom), dim);
        if (f.Right < pw) canvas.DrawRect(new SKRect(f.Right, f.Top, pw, f.Bottom), dim);
    }

    /// <summary>
    /// Renders a small "Model: <name>" badge in the top-left of the page so
    /// users can tell at a glance which layout analyzer they're looking at.
    /// Reuses the same Skia primitives the rest of the debug overlay uses
    /// (so the badge inherits any future restyling). Drawn inside the camera
    /// transform, in page coordinates — pinned to (8, 8) in page space.
    /// </summary>
    private static void DrawModelBadge(SKCanvas canvas, string label)
    {
        var text = $"Model: {label}";
        var font = OverlayRenderer.GetDebugFont();
        var textPaint = OverlayRenderer.GetDebugTextPaint();
        var bgPaint = OverlayRenderer.GetDebugBgPaint();

        var width = font.MeasureText(text);
        var metrics = font.Metrics;
        var lineHeight = metrics.Descent - metrics.Ascent;

        const float padX = 6f, padY = 3f;
        const float originX = 8f, originY = 8f;

        var bgRect = new SKRect(
            originX,
            originY,
            originX + width + 2 * padX,
            originY + lineHeight + 2 * padY);

        canvas.DrawRect(bgRect, bgPaint);
        canvas.DrawText(text, originX + padX, originY + padY - metrics.Ascent, font, textPaint);
    }
}
