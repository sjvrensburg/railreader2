using RailReader.Core.Services;

namespace RailReader.Core;

/// <summary>
/// Manages auto-scroll and jump mode state.
/// Extracted from DocumentController for testability and separation of concerns.
/// </summary>
internal sealed class AutoScrollController
{
    private readonly AppConfig _config;

    public AutoScrollController(AppConfig config)
    {
        _config = config;
    }

    /// <summary>Whether auto-scroll is currently active.</summary>
    public bool AutoScrollActive { get; private set; }

    /// <summary>Whether jump mode is currently active.</summary>
    public bool JumpMode { get; set; }

    /// <summary>
    /// Fired when a property changes. UI can subscribe to update bindings.
    /// </summary>
    public Action<string>? StateChanged;

    /// <summary>
    /// Toggles auto-scroll on/off. Requires an active document in rail mode to activate.
    /// </summary>
    public void ToggleAutoScroll(DocumentState? doc)
    {
        if (AutoScrollActive)
        {
            StopAutoScroll(doc);
            return;
        }
        if (doc is null || !doc.Rail.Active) return;

        doc.Rail.StartAutoScroll(_config.DefaultAutoScrollSpeed);
        AutoScrollActive = true;
        StateChanged?.Invoke(nameof(AutoScrollActive));
    }

    /// <summary>
    /// Stops auto-scroll and notifies the UI.
    /// </summary>
    public void StopAutoScroll(DocumentState? doc)
    {
        doc?.Rail.StopAutoScroll();
        AutoScrollActive = false;
        StateChanged?.Invoke(nameof(AutoScrollActive));
    }

    /// <summary>
    /// Toggles auto-scroll, disabling jump mode first if active.
    /// </summary>
    public void ToggleAutoScrollExclusive(DocumentState? doc)
    {
        if (JumpMode) JumpMode = false;
        ToggleAutoScroll(doc);
    }

    /// <summary>
    /// Toggles jump mode, stopping auto-scroll first if active.
    /// </summary>
    public void ToggleJumpModeExclusive(DocumentState? doc)
    {
        if (AutoScrollActive) StopAutoScroll(doc);
        JumpMode = !JumpMode;
    }

    /// <summary>
    /// Activates auto-scroll directly (used by TickRailSnap when auto-scroll trigger fires).
    /// </summary>
    public void ActivateAutoScroll()
    {
        AutoScrollActive = true;
        StateChanged?.Invoke(nameof(AutoScrollActive));
    }

    /// <summary>
    /// The configured auto-scroll speed.
    /// </summary>
    public double AutoScrollSpeed => _config.DefaultAutoScrollSpeed;
}
