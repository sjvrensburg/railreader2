using System;
using System.Collections.Generic;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader2.Services;

namespace RailReader2.ViewModels;

// Table cell navigation + scoped focus aids (the "Table Reading" feature).
public sealed partial class MainWindowViewModel
{
    private readonly TableNavigationPreferences _tableNavPrefs = TableNavigationPreferences.Load();

    /// <summary>Best-effort column inference for the current table block (Core models no columns).
    /// Owned per VM; reference-keyed by block so it self-invalidates on re-analysis.</summary>
    private readonly TableColumnIndex _tableColumns = new();

    // --- Persisted table-reading preferences (app-wide sidecar; surfaced for the Table Reading panel) ---

    public TableNavMode TableNavMode
    {
        get => _tableNavPrefs.Mode;
        set
        {
            if (_tableNavPrefs.Mode == value) return;
            _tableNavPrefs.Mode = value;
            _tableNavPrefs.Save();
            OnPropertyChanged(nameof(TableNavMode));
            // The effective focus scope depends on the mode (row mode collapses to Row), so repaint.
            InvalidatePage();
            InvalidateOverlay();
        }
    }

    public TableFocusScope TableFocusScope
    {
        get => _tableNavPrefs.FocusScope;
        set
        {
            if (_tableNavPrefs.FocusScope == value) return;
            _tableNavPrefs.FocusScope = value;
            _tableNavPrefs.Save();
            OnPropertyChanged(nameof(TableFocusScope));
            InvalidatePage();
            InvalidateOverlay();
        }
    }

    // --- Auto-open the Table Reading panel when the rail enters/leaves a table ---

    private bool _wasRailOnTable;
    private bool _autoOpenedTablePanel;
    private SidePane? _paneBeforeTable;

    /// <summary>Re-baseline the auto-open tracking to the (new) active tab and disarm "start rail here",
    /// so a tab switch can't apply one document's panel/arm state to another. Called from SelectTab
    /// after the new tab's sidebar state is restored.</summary>
    private void ResetTableStateForTabSwitch()
    {
        _wasRailOnTable = IsRailOnTable;   // baseline to the new tab without firing enter/leave
        _autoOpenedTablePanel = false;     // don't auto-close a panel we didn't open for this tab
        _paneBeforeTable = null;
        ArmActivateRailClick = false;      // a pending click-to-rail shouldn't carry to another tab
        FreezeArmMode = FreezeMode.None;   // ditto a pending freeze placement
        // Freeze is now per-viewport (railreader2#180) and survives tab switches — each view keeps its
        // own frozen table — so it is NOT cleared here. The IsFrozen/CanFreeze notifications for the
        // newly-focused view are raised by FocusViewport.
    }

    /// <summary>Open the side panel to the Table Reading section when the rail seats on a table, and
    /// restore the prior panel state when it leaves. Called from the rail-state invalidation paths;
    /// the edge guard makes repeated calls cheap.</summary>
    private void SyncTablePanelAutoOpen()
    {
        bool now = IsRailOnTable;
        if (now == _wasRailOnTable) return;
        _wasRailOnTable = now;

        if (now)
        {
            _paneBeforeTable = ActivePane;
            _autoOpenedTablePanel = !ShowOutline;
            ActivePane = SidePane.TableReading;
            ShowOutline = true;
        }
        else
        {
            // Leaving the table: drop the now-hidden section as the active pane, and close the panel
            // again only if we were the one who opened it (don't fight a panel the user had open).
            if (ActivePane == SidePane.TableReading)
                ActivePane = _paneBeforeTable ?? SidePane.Outline;
            if (_autoOpenedTablePanel)
                ShowOutline = false;
            _autoOpenedTablePanel = false;
            // Freeze is no longer rail-bound (it's a spatial, mouse-picked, per-viewport aid) — leaving
            // the table via the rail no longer releases it; it clears when the view leaves the page or
            // the user unfreezes.
        }

        OnPropertyChanged(nameof(CanFreeze));
    }

    /// <summary>True when the rail is currently seated on a <see cref="BlockRole.Table"/> block — drives
    /// the auto-appearing "Table Reading" panel. Re-raised from <c>InvalidateNavigation</c>.</summary>
    public bool IsRailOnTable =>
        ActiveTab?.Rail is { Active: true, HasAnalysis: true, NavigableCount: > 0 } rail
        && rail.CurrentNavigableBlock.Role == BlockRole.Table;

    /// <summary>
    /// When cell mode is on and the rail is on a table row with cells, route a Left/Right key to a
    /// cell step and return true. Returns false otherwise so the caller runs the existing horizontal
    /// block-jump / pan handling. Page furniture rows without cells fall through (the row stays the
    /// navigable unit).
    /// </summary>
    public bool TryHandleCellHorizontal(bool forward)
    {
        if (IsScanAllActive) return false;
        if (TableNavMode != TableNavMode.Cell) return false;
        if (ActiveTab?.Rail is not { Active: true, HasAnalysis: true, HasCells: true } rail) return false;
        if (rail.CurrentNavigableBlock.Role != BlockRole.Table) return false;

        bool stepped = false;
        Dispatch(() => stepped = forward ? _controller.HandleCellRight() : _controller.HandleCellLeft(),
            InvalidateNavigation, animate: true);
        // If Core reported the move didn't apply (no cells on this line), let the caller fall back.
        return stepped;
    }

    /// <summary>
    /// Excel-style vertical cell navigation: in cell mode on a table, Down/Up move to the cell in the
    /// *same column* of the next/previous row (rather than the row's first cell). We advance the row
    /// through the normal controller path — which keeps all block/page-edge handling — then re-seat the
    /// cell to the column nearest the one we left and re-snap. Returns true when handled (the caller
    /// then skips its own line nav). Returns false outside cell mode / off a table so the caller runs
    /// the usual <see cref="HandleArrowDown"/>/<see cref="HandleArrowUp"/>. At the table's top/bottom the
    /// row advance leaves the table block and re-seating is skipped, so the rail keeps reading onward.
    /// </summary>
    public bool TryHandleCellVertical(bool forward)
    {
        if (IsScanAllActive) return false;
        if (TableNavMode != TableNavMode.Cell) return false;
        if (ActiveTab?.Rail is not { Active: true, HasAnalysis: true, HasCells: true } rail) return false;
        if (rail.CurrentNavigableBlock.Role != BlockRole.Table) return false;
        if (rail.CurrentCellInfo is not { } cell) return false;

        var block = rail.CurrentNavigableBlock;
        float targetX = cell.CenterX;
        Dispatch(() =>
        {
            if (forward) _controller.HandleArrowDown(); else _controller.HandleArrowUp();

            // Still on the same table row-set with cells? Re-seat to the matching column and re-snap.
            // (Leaving the table → different block → fall through, rail keeps reading.) The
            // NavigableCount guard matters because the advance may land on a not-yet-analyzed page
            // where CurrentNavigableBlock would index an empty list and throw.
            if (rail.NavigableCount > 0
                && ReferenceEquals(rail.CurrentNavigableBlock, block)
                && rail.CurrentCells is { Count: > 0 } cells)
            {
                rail.CurrentCell = NearestCellIndex(cells, targetX);
                var cam = ActiveTab!.Camera;
                var (ww, wh) = FocusedViewportSize();
                rail.StartSnapToCell(cam.OffsetX, cam.OffsetY, cam.Zoom, ww, wh);
            }
        }, InvalidateNavigation, animate: true);
        return true;
    }

    /// <summary>Index of the cell whose centre is horizontally nearest <paramref name="targetX"/> —
    /// preserves the visual column across rows (robust on ragged tables where inferred column bands
    /// may be wrong).</summary>
    private static int NearestCellIndex(IReadOnlyList<CellInfo> cells, float targetX)
    {
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < cells.Count; i++)
        {
            float d = Math.Abs(cells[i].CenterX - targetX);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>The focus scope actually used for rendering: in row-by-row mode there is no current
    /// cell/column, so the cell/column scopes collapse to Row. In cell mode the chosen scope applies.</summary>
    public TableFocusScope EffectiveTableFocusScope =>
        TableNavMode == TableNavMode.Row ? TableFocusScope.Row : TableFocusScope;

    /// <summary>True when the scoped table focus aids apply to <paramref name="vp"/> — rail seated on a
    /// table row with cells. When active the overlay draws scoped tint/dim and the normal line
    /// highlight + page focus-dim are suppressed for that frame. Reads the surface's OWN viewport rail
    /// (a split pane / tear-off can be on a table while the primary isn't, or vice-versa).</summary>
    public bool TableFocusActive(Viewport? vp) =>
        vp?.Rail is { Active: true, HasAnalysis: true, HasCells: true } rail
        && rail.CurrentNavigableBlock.Role == BlockRole.Table;

    /// <summary>The inferred column band under the current rail cell, or null when not on a table cell.
    /// Used by the overlay/page focus aids for the Column / Row+Column scopes.</summary>
    public ColumnBand? CurrentTableColumn(Viewport? vp)
    {
        if (vp?.Rail is not { Active: true, HasAnalysis: true, HasCells: true } rail) return null;
        if (rail.CurrentNavigableBlock.Role != BlockRole.Table) return null;
        if (rail.CurrentCellInfo is not { } cell) return null;

        var bands = _tableColumns.GetColumns(rail.CurrentNavigableBlock);
        int idx = TableColumnIndex.ColumnIndexFor(bands, cell.CenterX);
        return idx >= 0 ? bands[idx] : null;
    }
}
