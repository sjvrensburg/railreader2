namespace RailReader2.ControlBus;

/// <summary>
/// Portable, OS/IPC-agnostic contract for driving the running RailReader2 GUI.
///
/// This is the seam the demo tooling talks to. The only real implementation is
/// <see cref="ViewModelControl"/>, which marshals every verb onto the UI thread and
/// routes it through the same <c>MainWindowViewModel</c> → <c>DocumentController.Tick</c> →
/// <c>RequestAnimationFrame</c> path real user input uses — so on-screen motion is
/// identical to what a user would see. The D-Bus server (<see cref="DBusControlServer"/>)
/// is a thin adapter over this interface; because the contract is bus-free it can be
/// unit-tested against a real VM under Avalonia.Headless and ported to another transport
/// (named pipe / gRPC on Windows) without touching app logic.
///
/// Verbs return as soon as the command is accepted (and any animation has started).
/// Completion of an animated verb is signalled by <see cref="Settled"/>.
/// </summary>
public interface IRailReaderControl
{
    // --- Verbs ---

    /// <summary>Open a PDF document in a new tab. Resolves to true once the document loaded.</summary>
    Task<bool> OpenDocumentAsync(string path);

    /// <summary>Jump to a 1-based page (clamped by the controller).</summary>
    void GoToPage(int page);

    /// <summary>Fit the whole page to the viewport.</summary>
    void FitPage();

    /// <summary>Fit the page width to the viewport.</summary>
    void FitWidth();

    /// <summary>Toggle the window's full-screen mode (hides chrome; the viewport fills the screen).
    /// Used by the demo runner so a recording captures just the app, full-bleed.</summary>
    void SetFullScreen(bool on);

    /// <summary>Zoom to an absolute percentage (100 == fit), anchored at the viewport centre.</summary>
    void SetZoom(double percent);

    /// <summary>Apply a colour effect by name (none, high-contrast, high-visibility, amber, invert).
    /// Returns false for an unknown name.</summary>
    bool SetColourEffect(string name);

    /// <summary>Jump the rail to the next/previous block of a role; false if nothing moved.</summary>
    bool NavigateRole(string role, bool forward);

    /// <summary>Advance the rail one line down (or up); false when not rail-reading.</summary>
    bool RailAdvanceLine(bool forward);

    /// <summary>Set the rail line-highlight overlay on/off.</summary>
    void SetLineHighlight(bool on);

    /// <summary>Set the rail line-focus-blur overlay on/off.</summary>
    void SetLineFocusBlur(bool on);

    /// <summary>Smoothly frame the n-th block of a semantic role (e.g. "figure", "table",
    /// "equation", "heading") using rail's exact framing and the app-native eased zoom.
    /// <paramref name="zoom"/> &lt;= 0 means auto-fit. Returns true if a matching block was framed.</summary>
    bool FrameRole(string role, int occurrence, double zoom);

    /// <summary>Smoothly frame a block by its index on the current page. <paramref name="zoom"/> &lt;= 0
    /// means auto-fit. Returns true if the block could be framed.</summary>
    bool FrameBlock(int pageBlockIndex, double zoom);

    // --- Queries (read current state) ---

    /// <summary>Read all exposed state in one atomic snapshot. A single call keeps the values
    /// mutually consistent and, for the implementation, lets a multi-property read (e.g. D-Bus
    /// GetAll) cross to the UI thread once instead of per property.</summary>
    ControlSnapshot Snapshot();

    // --- Events (sync backbone for the runner) ---

    /// <summary>Raised once when an eased camera animation completes (the StillAnimating
    /// true→false transition). The runner cuts on this.</summary>
    event Action? Settled;

    /// <summary>Raised when the current page changes (1-based).</summary>
    event Action<int>? PageChanged;

    /// <summary>Raised after a document finishes opening (its absolute path).</summary>
    event Action<string>? DocumentOpened;
}

/// <summary>Immutable snapshot of the control surface's readable state (see
/// <see cref="IRailReaderControl.Snapshot"/>).</summary>
/// <param name="DocumentPath">Absolute path of the active document, or "" when none is open.</param>
/// <param name="PageCount">Page count of the active document, or 0.</param>
/// <param name="CurrentPage">Current page of the active document, or 0.</param>
/// <param name="Zoom">Current camera zoom factor (1.0 == 100%).</param>
/// <param name="IsAnimating">True while an eased camera animation is in flight.</param>
/// <param name="CurrentBlockIndex">Block index under the current rail reading position, or -1.</param>
/// <param name="CurrentRole">Semantic role at the current rail reading position, or "".</param>
public readonly record struct ControlSnapshot(
    string DocumentPath,
    int PageCount,
    int CurrentPage,
    double Zoom,
    bool IsAnimating,
    int CurrentBlockIndex,
    string CurrentRole);
