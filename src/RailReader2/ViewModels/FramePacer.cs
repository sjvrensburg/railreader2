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
/// </summary>
internal sealed class FramePacer
{
    private const int WindowSize = 61;   // ~1 s at 60 Hz
    private const int MinSamples = 5;
    private const double Smoothing = 0.05;
    private const double MinPeriod = 1.0 / 240.0;
    private const double MaxPeriod = 1.0 / 20.0;

    private readonly double[] _window = new double[WindowSize];
    private readonly double[] _sortScratch = new double[WindowSize];
    private int _count;
    private int _next;

    /// <summary>Current frame-period estimate in seconds (1/60 s until enough samples exist).</summary>
    public double Period { get; private set; } = 1.0 / 60.0;

    /// <summary>Returns <paramref name="rawDt"/> rounded to a whole number (at least one) of frame
    /// periods. Non-positive input returns 0.</summary>
    public double Pace(double rawDt)
    {
        if (rawDt <= 0) return 0;

        // Only plausible single-frame-ish intervals feed the estimate; a multi-second idle gap or a
        // stall must not drag the median.
        if (rawDt is >= MinPeriod and <= MaxPeriod)
        {
            _window[_next] = rawDt;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize) _count++;
            if (_count >= MinSamples)
                Period = _count == MinSamples ? Median() : Period + Smoothing * (Median() - Period);
        }

        if (_count < MinSamples) return rawDt; // no estimate yet — pass through rather than guess a period

        int frames = Math.Max(1, (int)Math.Round(rawDt / Period));
        return frames * Period;
    }

    private double Median()
    {
        Array.Copy(_window, _sortScratch, _count);
        Array.Sort(_sortScratch, 0, _count);
        return _sortScratch[_count / 2];
    }
}
