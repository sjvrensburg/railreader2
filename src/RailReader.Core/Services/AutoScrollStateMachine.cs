using System.Diagnostics;

namespace RailReader.Core.Services;

internal enum AutoScrollState
{
    Inactive,
    Scrolling,
    WaitingForSnap,
    Paused,
    Dwelling,
}

/// <summary>
/// Provides camera clamping to the auto-scroll state machine.
/// Implemented by <see cref="RailNav"/> to avoid per-frame delegate allocation.
/// </summary>
internal interface ICameraClamp { double ClampX(double cameraX, double zoom, double windowWidth); }

internal readonly struct AutoScrollContext
{
    public required bool SnapInProgress { get; init; }
    /// <summary>Right edge of the current block (with margin).</summary>
    public required double BlockRight { get; init; }
    /// <summary>Raw block width in pixels (without margin).</summary>
    public required double RawBlockWidthPx { get; init; }
    public required int CurrentLine { get; init; }
    public required int BlockLineCount { get; init; }
    public required double LinePauseMs { get; init; }
    public required double WindowWidth { get; init; }
    public required double Zoom { get; init; }
    public required double ReferenceSpeed { get; init; }
    public required double MaxSpeed { get; init; }
}

/// <summary>
/// Manages auto-scroll state as an explicit state machine with well-defined transitions.
/// Replaces the implicit state previously encoded across multiple boolean flags in RailNav.
///
/// State transitions:
///   Inactive ──Start()──────────────────────────→ Scrolling
///   Scrolling ──reached mid-block line end──────→ Paused (advances)
///   Scrolling ──block fits on screen, not dwelt─→ Dwelling (advances)
///   Scrolling ──reached block end───────────────→ returns reachedEnd
///   Any ──RequestDeferredPause()────────────────→ WaitingForSnap
///   WaitingForSnap ──snap completes─────────────→ Paused (non-advancing)
///   Paused ──timer expires, non-advancing───────→ Scrolling
///   Paused ──timer expires, advancing───────────→ Scrolling + returns reachedEnd
///   Dwelling ──timer expires────────────────────→ Scrolling + returns reachedEnd
///   Any ──Stop()────────────────────────────────→ Inactive
/// </summary>
internal sealed class AutoScrollStateMachine
{
    private readonly ICameraClamp _clamp;

    public AutoScrollState CurrentState { get; private set; } = AutoScrollState.Inactive;
    public bool IsActive => CurrentState != AutoScrollState.Inactive;

    // Speed
    private double _speed;
    private bool _boost;

    // WaitingForSnap: deferred pause duration
    private double _pendingPauseMs;

    // Paused / Dwelling: countdown timer
    private Stopwatch? _pauseTimer;
    private double _pauseDurationMs;
    private bool _pauseAdvances; // true = pause triggers line advance on completion

    // Dwell tracking: prevents repeated dwell on the same block
    private bool _dwelt;

    /// <summary>Normalized scroll speed (0-1) for UI display.</summary>
    public double NormalizedSpeed { get; private set; }

    public AutoScrollStateMachine(ICameraClamp clamp)
    {
        _clamp = clamp;
    }

    public void Start(double speed)
    {
        Reset();
        CurrentState = AutoScrollState.Scrolling;
        _speed = speed;
    }

    public void Stop()
    {
        Reset();
        CurrentState = AutoScrollState.Inactive;
    }

    private void Reset()
    {
        _speed = 0;
        _boost = false;
        _pauseTimer = null;
        _pendingPauseMs = 0;
        _dwelt = false;
        NormalizedSpeed = 0;
    }

    /// <summary>Set/clear the boost flag (user holding D/Right during auto-scroll).</summary>
    public void SetBoost(bool boost) => _boost = boost;

    /// <summary>
    /// Request a pause that starts after the current snap animation completes.
    /// Used when entering a new block (block entry pause).
    /// Transition: current state -> WaitingForSnap.
    /// </summary>
    public void RequestDeferredPause(double durationMs)
    {
        if (CurrentState == AutoScrollState.Inactive || durationMs <= 0) return;
        _pendingPauseMs = durationMs;
        _dwelt = false; // reset for the new block
        CurrentState = AutoScrollState.WaitingForSnap;
    }

    /// <summary>
    /// Update the scroll speed without resetting state (e.g. config change via [ / ] keys).
    /// </summary>
    public void UpdateSpeed(double speed)
    {
        if (IsActive) _speed = speed;
    }

    /// <summary>
    /// Advance the state machine by one frame. Modifies camera position when scrolling.
    /// Returns true when the line end has been reached and the caller should advance.
    /// </summary>
    public bool Tick(ref double cameraX, double dtSecs, in AutoScrollContext ctx)
    {
        return CurrentState switch
        {
            AutoScrollState.WaitingForSnap => TickWaitingForSnap(in ctx),
            AutoScrollState.Paused or AutoScrollState.Dwelling => TickPause(),
            AutoScrollState.Scrolling => TickScrolling(ref cameraX, dtSecs, in ctx),
            _ => false,
        };
    }

    private bool TickWaitingForSnap(in AutoScrollContext ctx)
    {
        if (ctx.SnapInProgress) return false; // still snapping

        // Snap completed -> activate the deferred pause
        BeginPause(_pendingPauseMs, advances: false, AutoScrollState.Paused);
        _pendingPauseMs = 0;
        return false;
    }

    private bool TickPause()
    {
        if (_pauseTimer is null) return false;

        if (_pauseTimer.Elapsed.TotalMilliseconds >= _pauseDurationMs)
        {
            bool advance = _pauseAdvances;
            _pauseTimer = null;
            CurrentState = AutoScrollState.Scrolling;
            return advance;
        }
        return false; // still pausing
    }

    private bool TickScrolling(ref double cameraX, double dtSecs, in AutoScrollContext ctx)
    {
        double speed = _boost ? _speed * 2.0 : _speed;
        cameraX -= speed * ctx.ReferenceSpeed * dtSecs;
        NormalizedSpeed = ctx.MaxSpeed > 0 ? Math.Clamp(speed / ctx.MaxSpeed, 0.0, 1.0) : 0.0;
        cameraX = _clamp.ClampX(cameraX, ctx.Zoom, ctx.WindowWidth);

        // Check if we've reached the right edge of the line
        double visibleRight = (-cameraX + ctx.WindowWidth) / ctx.Zoom;
        if (visibleRight < ctx.BlockRight)
            return false; // still scrolling

        // Reached right edge -- determine next action

        // Dwell: block fits on screen and we haven't dwelt yet
        if (ctx.RawBlockWidthPx <= ctx.WindowWidth && !_dwelt && ctx.LinePauseMs > 0)
        {
            _dwelt = true;
            BeginPause(ctx.LinePauseMs * ctx.BlockLineCount, advances: true, AutoScrollState.Dwelling);
            return false;
        }

        // Mid-block line end: pause then advance
        bool isBlockEnd = ctx.CurrentLine + 1 >= ctx.BlockLineCount;
        if (!isBlockEnd && ctx.LinePauseMs > 0)
        {
            _dwelt = false; // reset for the next line
            BeginPause(ctx.LinePauseMs, advances: true, AutoScrollState.Paused);
            return false;
        }

        // Block end (or no pause configured): advance immediately
        return true;
    }

    private void BeginPause(double durationMs, bool advances, AutoScrollState state)
    {
        _pauseTimer = Stopwatch.StartNew();
        _pauseDurationMs = durationMs;
        _pauseAdvances = advances;
        NormalizedSpeed = 0;
        CurrentState = state;
    }
}
