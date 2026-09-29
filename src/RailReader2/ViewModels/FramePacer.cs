namespace RailReader2.ViewModels;

/// <summary>
/// Turns the raw interval between two animation-frame callbacks into a frame-aligned <c>dt</c>: a
/// whole number of compositor frame periods.
///
/// Why: Avalonia's <c>RequestAnimationFrame</c> timestamp is NOT a presentation time. It is a
/// Stopwatch read when the UI-thread render op happens to run (<c>MediaContext.RenderCore</c>),
/// which is posted after the compositor finishes the previous batch — so it carries dispatcher
/// latency. The compositor itself presents on its own steady tick (on X11 a 60 fps
/// <c>SleepLoopRenderTimer</c>). Since Core 0.62.2 auto-scroll integrates <c>dt</c> exactly, so
/// that latency jitter became per-frame position jitter of <c>speed × zoom × jitter</c> — a small,
/// constant judder on crisp text. Each UI tick is displayed for a whole number of compositor frames
/// (normally one; two after a missed frame), so rounding <c>dt</c> to whole periods gives every
/// displayed frame the same step.
///
/// The period is a smoothed running median of recent raw intervals, not a fixed 1/60 s: the
/// compositor tick is slightly longer than nominal (sleep overshoot), and a fixed period would make
/// scrolling drift fast or slow; the median also adapts to other refresh rates and ignores the odd
/// long frame. The median alone still moves a few percent frame to frame under jittery input, which
/// would leak straight back into the step size, hence the extra exponential smoothing on top
/// (simulated at ±4 ms stamp jitter: raw steps 0.77–1.94 frames → paced steps within ±2%, drifting
/// only slowly, with missed frames still paced as two).
///
/// Two guards for a render-bound compositor (e.g. a full-screen viewport whose frames take ~45 ms):
/// such frames are presented when rendering finishes, not on whole ticks, so an interval that isn't
/// close to a whole number of periods is passed through unpaced; and a sudden regime change (say
/// 16.7 ms → 45 ms) snaps the estimate straight to the new median instead of easing towards it,
/// which otherwise swung the step size for over a second (seen in a real frame log).
///
/// Only intervals ending a frame that actually presented something may feed the estimate. When a
/// frame invalidates nothing (e.g. an auto-scroll line pause), the compositor has nothing to present
/// and Avalonia paces the loop off its fallback ~60 Hz timer instead of the display: on a 75 Hz
/// monitor those idle intervals are ~16 ms against a 13.3 ms period, and letting them in dragged the
/// estimate ~7% high by the end of every line pause, so each line then started visibly too fast.
/// </summary>
internal sealed class FramePacer
{
    private const int WindowSize = 31;   // ~0.5 s at 60 Hz
    private const int MinSamples = 5;
    private const double Smoothing = 0.05;
    private const double RegimeChange = 0.25;   // median this far (relative) from Period → snap to it
    private const double MultipleTolerance = 0.25; // raw within this many periods of n·Period → pace
    private const double MinPeriod = 1.0 / 240.0;
    private const double MaxPeriod = 1.0 / 20.0;

    private readonly double[] _window = new double[WindowSize];
    private readonly double[] _sortScratch = new double[WindowSize];
    private int _count;
    private int _next;

    /// <summary>Current frame-period estimate in seconds (1/60 s until enough samples exist).</summary>
    public double Period { get; private set; } = 1.0 / 60.0;

    /// <summary>Returns <paramref name="rawDt"/> rounded to a whole number (at least one) of frame
    /// periods, or unchanged when it isn't close to one. Non-positive input returns 0.
    /// <paramref name="presented"/> is false when the interval followed a frame that drew nothing: it
    /// then neither feeds the estimate nor gets rounded (no motion to keep even, and timers such as a
    /// line pause should see real time).</summary>
    public double Pace(double rawDt, bool presented = true)
    {
        if (rawDt <= 0) return 0;
        if (!presented) return rawDt;

        // Only plausible single-frame-ish intervals feed the estimate; a multi-second idle gap or a
        // stall must not drag the median.
        if (rawDt is >= MinPeriod and <= MaxPeriod)
        {
            _window[_next] = rawDt;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize) _count++;
            if (_count >= MinSamples)
            {
                double median = Median();
                Period = _count == MinSamples || Math.Abs(median - Period) > RegimeChange * Period
                    ? median
                    : Period + Smoothing * (median - Period);
            }
        }

        if (_count < MinSamples) return rawDt; // no estimate yet — pass through rather than guess a period

        int frames = Math.Max(1, (int)Math.Round(rawDt / Period));
        double paced = frames * Period;
        // Not a whole number of ticks: the frame was presented when rendering finished — raw is truth.
        return Math.Abs(rawDt - paced) <= MultipleTolerance * Period ? paced : rawDt;
    }

    private double Median()
    {
        Array.Copy(_window, _sortScratch, _count);
        Array.Sort(_sortScratch, 0, _count);
        return _sortScratch[_count / 2];
    }
}
