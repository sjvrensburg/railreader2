using RailReader.Core.Models;

namespace RailReader2.ViewModels;

// Table reading: auto-open the "Table Reading" side-panel section (freeze-pane controls) while the
// rail is seated on a table. The rail reads tables row by row via the normal line navigation, with
// the current row highlighted by the usual line highlight — there is no cell-by-cell navigation or
// cell/column focus scoping (removed: the cell machinery proved more trouble than it was worth).
public sealed partial class MainWindowViewModel
{
    // --- Auto-open the Table Reading panel when the rail enters/leaves a table ---

    private bool _wasRailOnTable;
    private bool _autoOpenedTablePanel;
    private SidePane? _paneBeforeTable;

    /// <summary>Re-baseline the auto-open tracking to the (new) active tab and disarm "start rail here"
    /// / freeze placement, so a tab switch can't apply one document's panel/arm state to another.
    /// Called from SelectTab after the new tab's sidebar state is restored.</summary>
    private void ResetTableStateForTabSwitch()
    {
        _wasRailOnTable = IsRailOnTable;   // baseline to the new tab without firing enter/leave
        _autoOpenedTablePanel = false;     // don't auto-close a panel we didn't open for this tab
        _paneBeforeTable = null;
        ArmActivateRailClick = false;      // a pending click-to-rail shouldn't carry to another tab
        FreezeArmMode = FreezeMode.None;   // ditto a pending freeze placement
        // Freeze is per-viewport (railreader2#180) and survives tab switches — each view keeps its own
        // frozen page — so it is NOT cleared here.
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
        }

        OnPropertyChanged(nameof(CanFreeze));
    }

    /// <summary>True when the rail is currently seated on a <see cref="BlockRole.Table"/> block — drives
    /// the auto-appearing "Table Reading" panel. Re-raised from <c>InvalidateNavigation</c>.</summary>
    public bool IsRailOnTable =>
        ActiveTab?.Rail is { Active: true, HasAnalysis: true, NavigableCount: > 0 } rail
        && rail.CurrentNavigableBlock.Role == BlockRole.Table;
}
