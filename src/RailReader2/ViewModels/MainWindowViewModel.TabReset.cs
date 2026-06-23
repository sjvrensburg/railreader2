namespace RailReader2.ViewModels;

// Per-tab "armed gesture" hygiene. Some interactions are armed-then-click (start rail here, freeze
// placement); a tab switch must not carry one document's pending arm onto another. Freeze panes used
// to live in an auto-opening "Table Reading" side-panel section, but the controls moved to the
// toolbar's Freeze flyout (freeze is page-wide and table-independent), so no panel auto-open remains.
public sealed partial class MainWindowViewModel
{
    /// <summary>Disarm "start rail here" / freeze placement on a tab switch, so a pending click-to-place
    /// from one document can't apply to another. Called from SelectTab after the new tab's sidebar state
    /// is restored.</summary>
    private void ResetArmStateForTabSwitch()
    {
        ArmActivateRailClick = false;      // a pending click-to-rail shouldn't carry to another tab
        FreezeArmMode = FreezeMode.None;   // ditto a pending freeze placement
        // Freeze is per-viewport (railreader2#180) and survives tab switches — each view keeps its own
        // frozen page — so it is NOT cleared here.
    }
}
