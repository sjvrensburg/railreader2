using System.Diagnostics;
using RailReader.Core.Models;

namespace RailReader.Core;

internal enum EdgeHoldState
{
    Idle,
    Holding,
    Cooldown,
}

/// <summary>
/// Manages edge-hold advance for both non-rail vertical navigation and
/// rail-mode horizontal navigation. When the user holds a key at the
/// content edge for long enough, fires an advance signal, then enters
/// a cooldown to suppress key-repeat from immediately retriggering.
/// </summary>
internal sealed class EdgeHoldStateMachine
{
    public EdgeHoldState CurrentState { get; private set; } = EdgeHoldState.Idle;

    private Stopwatch? _timer;
    private bool _forward;

    // Output signals: set when OnEdgeHit fires, consumed by the caller.
    private ScrollDirection? _pendingAdvance;
    private bool _advanceJustFired;

    /// <summary>
    /// Called when the camera is at the content edge after a nav attempt.
    /// Returns true when the hold threshold has been reached and the caller
    /// should advance. Also sets a pending advance direction and the
    /// advance-suppression flag.
    /// </summary>
    public bool OnEdgeHit(bool forward)
    {
        switch (CurrentState)
        {
            case EdgeHoldState.Cooldown:
                if (_timer!.Elapsed.TotalMilliseconds < CoreTuning.EdgeCooldownMs) return false;
                // Cooldown expired, fall through to start a new hold
                goto case EdgeHoldState.Idle;

            case EdgeHoldState.Idle:
                _timer = Stopwatch.StartNew();
                _forward = forward;
                CurrentState = EdgeHoldState.Holding;
                return false;

            case EdgeHoldState.Holding:
                if (_forward != forward)
                {
                    // Direction changed, restart
                    _timer = Stopwatch.StartNew();
                    _forward = forward;
                    return false;
                }
                if (_timer!.Elapsed.TotalMilliseconds >= CoreTuning.EdgeHoldMs)
                {
                    _timer = Stopwatch.StartNew();
                    CurrentState = EdgeHoldState.Cooldown;
                    _pendingAdvance = forward ? ScrollDirection.Forward : ScrollDirection.Backward;
                    _advanceJustFired = true;
                    return true; // fire advance
                }
                return false;

            default:
                return false;
        }
    }

    public void OnMoved()
    {
        if (CurrentState != EdgeHoldState.Idle)
        {
            CurrentState = EdgeHoldState.Idle;
            _timer = null;
        }
    }

    /// <summary>
    /// True during the cooldown period after an advance fires.
    /// Used by non-rail navigation to suppress key-repeat panning.
    /// </summary>
    public bool ShouldSuppressInput =>
        CurrentState == EdgeHoldState.Cooldown
        && _timer!.Elapsed.TotalMilliseconds < CoreTuning.EdgeCooldownMs;

    /// <summary>
    /// Returns the direction of a pending edge advance and clears it.
    /// Used by rail-mode navigation where the advance is consumed
    /// asynchronously in the tick loop.
    /// </summary>
    public ScrollDirection? ConsumePendingAdvance()
    {
        var result = _pendingAdvance;
        _pendingAdvance = null;
        return result;
    }

    /// <summary>
    /// True after an advance fires, until the caller clears it.
    /// Used by rail-mode navigation to suppress input while a
    /// post-advance snap animation completes.
    /// </summary>
    public bool AdvanceJustFired => _advanceJustFired;

    public void ClearAdvanceFlag() => _advanceJustFired = false;

    /// <summary>Resets all state including output signals.</summary>
    public void Reset()
    {
        CurrentState = EdgeHoldState.Idle;
        _timer = null;
        _pendingAdvance = null;
        _advanceJustFired = false;
    }
}
