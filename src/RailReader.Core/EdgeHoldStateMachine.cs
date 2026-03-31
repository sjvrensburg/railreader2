using System.Diagnostics;

namespace RailReader.Core;

internal enum EdgeHoldState
{
    Idle,
    Holding,
    Cooldown,
}

/// <summary>
/// Manages edge-hold page advance for non-rail vertical navigation.
/// When the user holds an arrow key at the page edge for long enough,
/// advances to the next/previous page, then enters a cooldown to
/// suppress key-repeat from immediately panning away.
/// </summary>
internal sealed class EdgeHoldStateMachine
{
    private const double HoldMs = 400.0;
    private const double CooldownMs = 300.0;

    public EdgeHoldState CurrentState { get; private set; } = EdgeHoldState.Idle;

    private Stopwatch? _timer;
    private bool _forward;

    /// <summary>
    /// Called when the camera is at the page edge after a vertical nav attempt.
    /// Returns true when the hold threshold has been reached and the caller should advance the page.
    /// </summary>
    public bool OnEdgeHit(bool forward)
    {
        switch (CurrentState)
        {
            case EdgeHoldState.Cooldown:
                if (_timer!.Elapsed.TotalMilliseconds < CooldownMs) return false;
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
                if (_timer!.Elapsed.TotalMilliseconds >= HoldMs)
                {
                    _timer = Stopwatch.StartNew();
                    CurrentState = EdgeHoldState.Cooldown;
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

    public bool ShouldSuppressInput =>
        CurrentState == EdgeHoldState.Cooldown
        && _timer!.Elapsed.TotalMilliseconds < CooldownMs;

    public void Reset()
    {
        CurrentState = EdgeHoldState.Idle;
        _timer = null;
    }
}
