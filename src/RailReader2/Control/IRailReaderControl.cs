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

    /// <summary>Smoothly frame the n-th block of a semantic role (e.g. "figure", "table",
    /// "equation", "heading") using rail's exact framing and the app-native eased zoom.
    /// <paramref name="zoom"/> &lt;= 0 means auto-fit. Returns true if a matching block was framed.</summary>
    bool FrameRole(string role, int occurrence, double zoom);

    /// <summary>Smoothly frame a block by its index on the current page. <paramref name="zoom"/> &lt;= 0
    /// means auto-fit. Returns true if the block could be framed.</summary>
    bool FrameBlock(int pageBlockIndex, double zoom);

    // --- Queries (read current state) ---

    /// <summary>Absolute path of the active document, or empty when none is open.</summary>
    string DocumentPath { get; }

    /// <summary>Page count of the active document, or 0 when none is open.</summary>
    int PageCount { get; }

    /// <summary>Current 1-based page of the active document, or 0 when none is open.</summary>
    int CurrentPage { get; }

    /// <summary>Current camera zoom factor (1.0 == 100%).</summary>
    double Zoom { get; }

    /// <summary>True while an eased camera animation is in flight.</summary>
    bool IsAnimating { get; }

    /// <summary>Index of the block under the current rail reading position, or -1.</summary>
    int CurrentBlockIndex { get; }

    /// <summary>Semantic role at the current rail reading position (e.g. "Figure"), or empty.</summary>
    string CurrentRole { get; }

    // --- Events (sync backbone for the runner) ---

    /// <summary>Raised once when an eased camera animation completes (the StillAnimating
    /// true→false transition). The runner cuts on this.</summary>
    event Action? Settled;

    /// <summary>Raised when the current page changes (1-based).</summary>
    event Action<int>? PageChanged;

    /// <summary>Raised after a document finishes opening (its absolute path).</summary>
    event Action<string>? DocumentOpened;
}
