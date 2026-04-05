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

    // Wall-clock scroll positioning: camera position is computed as an absolute
    // function of elapsed time rather than accumulated frame deltas. This means
    // each frame shows exactly where the content should be at that moment.
    // A dropped frame (33ms instead of 16ms) produces a clean 2-frame jump
    // instead of a sustained lag-then-catchup, which is perceived as jitter.
    // _scrollInitialized = false signals that the next TickScrolling call must
    // capture the current cameraX as the reference start position.
    private bool _scrollInitialized;
    private Stopwatch? _scrollClock;
    private double _scrollStartX;

    /// <summary>
    /// Inject a controlled elapsed-seconds source for unit tests.
    /// When set, the real Stopwatch is not used.
    /// </summary>
    internal Func<double>? GetScrollElapsedSeconds;

    private double ScrollElapsed => GetScrollElapsedSeconds?.Invoke()
        ?? _scrollClock?.Elapsed.TotalSeconds
        ?? 0.0;

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
        _scrollInitialized = false;
        _scrollClock = null;
        _scrollStartX = 0;
    }

    /// <summary>Set/clear the boost flag (user holding D/Right during auto-scroll).</summary>
    public void SetBoost(bool boost)
    {
        if (_boost == boost) return;
        _boost = boost;
        // Re-capture current position as new reference so the speed change
        // takes effect from the current camera position without a jump.
        _scrollInitialized = false;
    }

    /// <summary>
    /// Request a pause that starts after the current snap animation completes.
    /// Used when entering a new block (block entry pause) or after a mid-block
    /// line advance to prevent autoscroll from fighting the snap animation.
    /// Pass durationMs=0 to wait for snap completion without any display pause.
    /// Transition: current state -> WaitingForSnap.
    /// </summary>
    public void RequestDeferredPause(double durationMs)
    {
        if (CurrentState == AutoScrollState.Inactive) return;
        _pendingPauseMs = durationMs;
        // Only reset dwell tracking for genuine new-block entries (durationMs > 0).
        // Mid-block line advances (durationMs = 0) stay within the same block —
        // resetting _dwelt here caused every line of a narrow block to re-trigger
        // a full LinePauseMs * BlockLineCount dwell, compounding over time.
        if (durationMs > 0) _dwelt = false;
        CurrentState = AutoScrollState.WaitingForSnap;
    }

    /// <summary>
    /// Update the scroll speed without resetting state (e.g. config change via [ / ] keys).
    /// </summary>
    public void UpdateSpeed(double speed)
    {
        if (!IsActive || _speed == speed) return;
        _speed = speed;
        // Re-capture current position so the new speed starts from here.
        _scrollInitialized = false;
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

        // Snap completed -> activate the deferred pause, or resume immediately if none
        double pauseMs = _pendingPauseMs;
        _pendingPauseMs = 0;
        if (pauseMs > 0)
            BeginPause(pauseMs, advances: false, AutoScrollState.Paused);
        else
        {
            CurrentState = AutoScrollState.Scrolling;
            _scrollInitialized = false; // capture new start position from snap target
        }
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
            _scrollInitialized = false; // capture new start position from post-pause camera
            return advance;
        }
        return false; // still pausing
    }

    private bool TickScrolling(ref double cameraX, double dtSecs, in AutoScrollContext ctx)
    {
        // Wall-clock positioning: compute cameraX as an absolute function of elapsed
        // time since scrolling started. This means every frame shows exactly where
        // the content should be at that moment — dropped frames produce a clean jump
        // to the correct position rather than sustained lag followed by catchup jitter.
        if (!_scrollInitialized)
        {
            _scrollStartX = cameraX;
            _scrollClock = GetScrollElapsedSeconds is null ? Stopwatch.StartNew() : null;
            _scrollInitialized = true;
        }

        double speed = _boost ? _speed * 2.0 : _speed;
        cameraX = _scrollStartX - speed * ctx.Zoom * ScrollElapsed;
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
