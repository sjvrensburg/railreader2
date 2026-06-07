using Avalonia.Input;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader2.ControlBus;

namespace RailReader2.ViewModels;

// External-control surface: thin wrappers over the Core motion primitives plus the
// state queries consumed by IRailReaderControl / DBusControlServer. Every wrapper routes
// through Dispatch(..., animate: true) so the eased motion is driven by RequestAnimationFrame
// — the same path real user input takes (mirrors HandleZoomKey).
public sealed partial class MainWindowViewModel
{
    /// <summary>Raised when the active document's current page changes (1-based). Multicast
    /// companion to Core's settable <c>PageChanged</c> callback, so the control surface can
    /// observe page crosses without clobbering the accessibility hook.</summary>
    public event Action<int>? PageChangedNotification;

    /// <summary>Smoothly frame a block by index on the current page using rail's exact framing
    /// and the app-native eased zoom. Returns true if the block could be framed.</summary>
    public bool SmoothlyFrameBlock(int pageBlockIndex, double? zoom = null)
    {
        bool ok = false;
        Dispatch(() => ok = _controller.SmoothlyFrameBlock(pageBlockIndex, zoom),
            InvalidateCameraAndTab, animate: true);
        return ok;
    }

    /// <summary>Smoothly frame the n-th block of a semantic role. Returns true if a matching
    /// block was found and framed.</summary>
    public bool SmoothlyFrameRole(BlockRole role, int occurrence = 0, double? zoom = null)
    {
        bool ok = false;
        Dispatch(() => ok = _controller.SmoothlyFrameRole(role, occurrence, zoom),
            InvalidateCameraAndTab, animate: true);
        return ok;
    }

    /// <summary>Animate the camera to an absolute zoom + offset via the app-native easing.</summary>
    public void AnimateCameraTo(double targetZoom, double targetOffsetX, double targetOffsetY)
        => Dispatch(() => _controller.AnimateCameraTo(targetZoom, targetOffsetX, targetOffsetY),
            InvalidateCameraAndTab, animate: true);

    // --- State queries for the control surface ---

    /// <summary>Aggregate document state (path, page count, current page, zoom, …) or null
    /// when no document is open.</summary>
    public DocumentInfo? GetDocumentInfo() => _controller.ActiveDocument is null ? null : _controller.GetDocumentInfo();

    /// <summary>True while an eased camera animation is in flight.</summary>
    public bool IsAnimating => _controller.IsAnimating;

    // --- Broadened verbs for the control surface (Phase D) ---

    /// <summary>Jump the rail to the next/previous block of a role; returns whether it moved
    /// (false when not rail-reading or there is no such block in that direction).</summary>
    public bool NavigateRoleForControl(BlockRole role, bool forward)
    {
        if (ActiveTab?.Rail.Active != true) return false;
        if (!_controller.NavigateToRole(role, forward)) return false;
        InvalidateNavigation();
        return true;
    }

    /// <summary>Advance the rail one line down/up; returns false when not rail-reading.</summary>
    public bool RailAdvanceLineForControl(bool forward)
    {
        if (ActiveTab?.Rail.Active != true) return false;
        if (forward) HandleArrowDown(); else HandleArrowUp();
        return true;
    }

    /// <summary>Set (not toggle) the rail line highlight.</summary>
    public void SetLineHighlight(bool on)
    {
        if (_controller.ActiveDocument is { } doc && doc.LineHighlightEnabled != on)
            ToggleLineHighlight();
    }

    /// <summary>Set (not toggle) the rail line focus blur.</summary>
    public void SetLineFocusBlur(bool on)
    {
        if (_controller.ActiveDocument is { } doc && doc.LineFocusBlur != on)
            ToggleLineFocusBlur();
    }

    /// <summary>Restrict which block roles rail mode may stop on, for the current session only
    /// (applied to the controller WITHOUT persisting to disk, so the user's saved config is
    /// untouched). Used by the demo runner to e.g. drop captions/footnotes for a clean
    /// column-to-column hand-off.</summary>
    public void SetNavigableRoles(IReadOnlyCollection<BlockRole> roles)
    {
        _appConfig.NavigableRoles = new HashSet<BlockRole>(roles);
        _controller.OnConfigChanged(_appConfig.ToCoreSettings()); // re-applies; no Save()
        InvalidateOverlay();
    }

    /// <summary>Set by the window: drives a synthesized key chord through the real OnKeyDown/OnKeyUp
    /// path, so the control surface can invoke ANY keyboard shortcut. Args: key, modifiers, isDown
    /// (false = key-up, e.g. to end a held rail scroll).</summary>
    public Action<Key, KeyModifiers, bool>? KeyInvoker { get; set; }

    /// <summary>Invoke a keyboard shortcut programmatically. Returns false if no window is wired.</summary>
    public bool InvokeKey(Key key, KeyModifiers mods, bool down)
    {
        if (KeyInvoker is null) return false;
        KeyInvoker(key, mods, down);
        return true;
    }

    /// <summary>Set when launched with <c>--record-script</c> or via the in-app toggle: captures the
    /// session into a demo script. The window feeds it key events; <see cref="OpenDocument"/> feeds
    /// it opens.</summary>
    public ScriptRecorder? ScriptRecorder { get; set; }

    /// <summary>True while a demo session is being recorded (drives any UI indicator).</summary>
    public bool IsRecordingScript => ScriptRecorder is not null;

    /// <summary>Start/stop recording the session to a demo script (Ctrl+Shift+R). Stopping writes
    /// the script and toasts the path.</summary>
    public void ToggleScriptRecording()
    {
        if (ScriptRecorder is null)
            StartScriptRecording(DefaultRecordingPath());
        else
            StopAndSaveRecording();
    }

    public void StartScriptRecording(string savePath)
    {
        var rec = new ScriptRecorder { SavePath = savePath };
        // Seed with the already-open document so the script is self-contained (replay opens it).
        if (_controller.ActiveDocument?.FilePath is { Length: > 0 } open)
            rec.RecordOpen(open);
        ScriptRecorder = rec;
        OnPropertyChanged(nameof(IsRecordingScript));
        ShowStatusToast("● Recording demo script…");
    }

    public void StopAndSaveRecording()
    {
        if (ScriptRecorder is not { } rec) return;
        ScriptRecorder = null;
        OnPropertyChanged(nameof(IsRecordingScript));
        try
        {
            rec.Save();
            ShowStatusToast(rec.SavePath is { } p ? $"Saved demo script → {Path.GetFileName(p)}" : "Recording stopped");
        }
        catch (Exception ex)
        {
            _logger.Error("[record-script] save failed", ex);
            ShowStatusToast("Failed to save demo script");
        }
    }

    private static string DefaultRecordingPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"railreader2-demo-{DateTime.Now:yyyyMMdd-HHmmss}.demo");
}
