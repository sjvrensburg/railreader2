namespace RailReader.Demo;

/// <summary>
/// Transport seam the <see cref="DemoSequencer"/> drives the running app through. The real
/// implementation (<see cref="DBusControlClient"/>) talks to the GUI's <c>org.railreader.Control1</c>
/// D-Bus surface; a fake implementation lets the sequencer be tested without an app or a bus.
/// Verb methods mirror the bus methods; events mirror its signals.
/// </summary>
public interface IControlClient : IAsyncDisposable
{
    /// <summary>Open a document; resolves to true once it loaded.</summary>
    Task<bool> OpenDocumentAsync(string path, CancellationToken ct);

    Task GoToPageAsync(int page, CancellationToken ct);
    Task FitPageAsync(CancellationToken ct);
    Task FitWidthAsync(CancellationToken ct);
    Task SetFullScreenAsync(bool on, CancellationToken ct);
    Task SetZoomAsync(double percent, CancellationToken ct);
    Task<bool> SetColourEffectAsync(string name, CancellationToken ct);
    Task<bool> NavigateRoleAsync(string role, bool forward, CancellationToken ct);
    Task<bool> RailAdvanceLineAsync(bool forward, CancellationToken ct);
    Task SetLineHighlightAsync(bool on, CancellationToken ct);
    Task SetLineFocusBlurAsync(bool on, CancellationToken ct);

    /// <summary>Frame the occurrence-th block of a role; false if nothing matched (so no animation runs).</summary>
    Task<bool> FrameRoleAsync(string role, int occurrence, double zoom, CancellationToken ct);

    /// <summary>Frame a block by page index; false if it couldn't be framed.</summary>
    Task<bool> FrameBlockAsync(int pageBlockIndex, double zoom, CancellationToken ct);

    /// <summary>Drive a keyboard shortcut by chord; down/up select press phases (both = tap).</summary>
    Task<bool> SendKeyAsync(string chord, bool down, bool up, CancellationToken ct);

    /// <summary>Restrict rail-navigable block roles for this session (comma-separated tokens).</summary>
    Task<bool> SetNavigableRolesAsync(string csv, CancellationToken ct);

    /// <summary>Read live rail reading progress, for syncing on real motion (e.g. the column hand-off).</summary>
    Task<ReadingState> GetReadingStateAsync(CancellationToken ct);

    /// <summary>Raised once when an eased camera animation completes (the cut/sync backbone).</summary>
    event Action? Settled;

    /// <summary>Raised when the current page changes (1-based).</summary>
    event Action<int>? PageChanged;

    /// <summary>Raised after a document finishes opening (its path).</summary>
    event Action<string>? DocumentOpened;
}

/// <summary>Live rail reading progress reported by the app (mirrors the bus GetReadingState).</summary>
public readonly record struct ReadingState(
    bool RailActive,
    bool AutoScrollActive,
    int Page,
    int BlockIndex,
    int LineIndex,
    int LineCount,
    double HorizontalFraction);
