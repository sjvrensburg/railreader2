using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.Services;

namespace RailReader2.ViewModels;

// Reading guide — pointer-driven word + line focus for low-magnification readers
// (docs/reading-guide-design.md). A pointer MODE beside Browse / Text-Select. Two ways to drive it:
//
//   STEER (default, §10): Shift-down plants an invisible joystick where the pointer is (and seats the
//   guide on the word under it if none is parked on this page). The pointer's displacement from that
//   origin is a throttle, not a position: right flows the guide word by word along reading order
//   (wrapping lines, blocks, and the page), left reverses, up/down steps lines. The view scrolls only
//   when the guide leaves the middle band of the viewport (keep-in-view). Release Shift to park; click
//   a word to re-seat.
//
//   POINT (WhileShiftHeld / Always): the guide sits on the word under the pointer, snapped to text
//   geometry (Core's hit-tester, with hysteresis and a carriage return at line ends).
//
//   RAIL (§11): with the rail active and drawing (above the zoom threshold, or "start rail here"), the
//   same stick drives the rail instead — horizontal deflection is the rail's scroll velocity along the
//   line (Core's steered scroll: hold-to-scroll's speed curve over deflection, edge-hold line advance,
//   page turns, park on stop units), vertical steps lines. No word guide is drawn; the rail's own line
//   visuals are what the reader sees, plus the word at the reading point while steering (WordHighlight).
//
// State is PER-VIEWPORT (like freeze panes): each split pane / tear-off / duplicate tab keeps its own
// hit-tester, stick and parked position. Following acts on the view UNDER THE POINTER, not
// FocusedViewport — hover never focuses a pane. The camera is only moved by steer mode's keep-in-view.
//
// Shift chords (Shift+D debug overlay, Shift+H/G/T/E jumps, Shift+O/B/I panes, …) all begin with a
// Shift-down. Rule (design §7): Shift-down only acts where the pointer actually rests over a view, and
// a non-Shift key pressed while Shift is held cancels the follow and restores every position parked
// before the chord, so a chord never moves a reader's place-keeper.
/// <summary>
/// The steer stick as the reader needs to see it, in viewport pixels: where it was planted, where the
/// (hidden) pointer is now, the still-zone radius, the radius of full deflection, and the current
/// throttle magnitude 0…1. Drawn by the reading-guide layer.
/// </summary>
public readonly record struct SteerIndicator(
    double OriginX, double OriginY, double PointerX, double PointerY,
    double DeadZonePx, double FullDeflectionPx, double Throttle, bool Parked = false);

public sealed partial class MainWindowViewModel
{
    private sealed class GuideState
    {
        public readonly ReadingGuideHitTester Tester = new();
        public readonly ReadingGuideSteer Steer = new();
        public int Page = -1;
        public ReadingGuidePosition? Position;
        /// <summary>The analysis instance <see cref="Position"/> was resolved against, so a replaced
        /// analysis (re-run, OCR landed) can be detected and the parked indices re-read against it.</summary>
        public PageAnalysis? Analysis;
        /// <summary>True while the pointer is over this viewport's surface (so a Shift press with a still
        /// pointer can act on its last position); cleared on pointer exit.</summary>
        public bool PointerOver;
        public double PointerX, PointerY;   // last pointer position, page points on the anchor page
        public double ScreenX, ScreenY;     // last pointer position, viewport pixels (the stick's frame)
        public long LastMoveTicks;          // Stopwatch ticks of the last pointer move (sweep gate)
        public int NoAnalysisToastPage = -1;
        /// <summary>After a steer page turn: seat on the new page's first (+1) / last (−1) word as soon
        /// as its analysis is available; 0 = nothing pending.</summary>
        public int PendingSeat;
        /// <summary>True while this view's stick drives the rail (§11) rather than the word guide.</summary>
        public bool RailSteering;
        /// <summary>Steer parked after moving to a new line / page on its own: the stick is ignored until the
        /// pointer returns to its still zone (then a fresh push resumes).</summary>
        public bool Parked;
        /// <summary>Rail steering: (page, block, line) seen on the previous tick — a change that did not come from
        /// a vertical nudge is the rail advancing by itself, and parks. Null = take a fresh snapshot.</summary>
        public (int Page, int Block, int Line)? RailSnapshot;
    }

    private readonly Dictionary<Viewport, GuideState> _guideByVp = new();
    private readonly ReadingGuidePreferences _guidePrefs = ReadingGuidePreferences.Load();
    private bool _guideShiftHeld;
    private bool _guideChordCancelled;
    private List<(Viewport Vp, int Page, ReadingGuidePosition? Position, PageAnalysis? Analysis)>? _guidePreChord;

    // Keep-in-view band (fractions of the viewport): the guide word is scrolled back inside it only
    // when it leaves — never recentred — so the page stays still while reading inside the band.
    private const double KeepInViewTop = 0.18, KeepInViewBottom = 0.82;
    private const double KeepInViewLeft = 0.08, KeepInViewRight = 0.92;
    private const double KeepInViewTauSecs = 0.14;   // exponential ease time constant
    // Point mode: a leftward sweep faster than this (viewport px per ms) is the return sweep, not reading;
    // the word highlight holds instead of flickering backwards through the line.
    private const double SweepGatePxPerMs = 1.2;
    // Steer pace from the reading-pace settings (Relaxed 10/28 → 4/11 chars/s, Normal 14/42 → 5.6/17,
    // Brisk 20/60 → 8/24): the same preset that sets hold-to-scroll speed sets the flow.
    private const double CharsPerSecondPerScrollSpeed = 0.4;
    // Steer stick geometry at sensitivity 1 (Core's defaults, 16/160 px, were found too twitchy: a hand
    // resting on the mouse drifts past 16 px, and the pace at the dead-zone edge was already a walk).
    // The pace at the edge of the dead zone is this fraction of the preset's slow end, so a nudge starts
    // gently and the ramp to full speed has room.
    private const double SteerDeadZoneBasePx = 28, SteerFullDeflectionBasePx = 260;
    private const double SteerEdgePaceFraction = 0.5;

    /// <summary>The reading-guide pointer mode (toolbar button, Rail ▸ Reading Guide, <c>K</c>). Mutually
    /// exclusive with Text-Select and the annotation tools, like any pointer mode.</summary>
    [ObservableProperty] private bool _isReadingGuideMode;

    public ReadingGuidePreferences ReadingGuidePrefs => _guidePrefs;

    private bool GuideUsesShift => _guidePrefs.Follow != ReadingGuideFollow.Always;
    private bool GuideIsSteer => _guidePrefs.Follow == ReadingGuideFollow.Steer;

    /// <summary>True while pointer moves should drive the guide: the mode is on and either the
    /// preference is "always" or Shift is held (and no chord has cancelled the hold).</summary>
    public bool IsGuideFollowing =>
        IsReadingGuideMode && (!GuideUsesShift || (_guideShiftHeld && !_guideChordCancelled));

    /// <summary>True while the pointer glyph should be hidden over a page: following under a held Shift
    /// (point or steer). In "always" mode the pointer also serves ordinary clicking, so it stays visible.</summary>
    public bool IsGuideCursorHidden => IsGuideFollowing && GuideUsesShift;

    /// <summary>Whether the pointer glyph over <paramref name="vp"/> should be hidden. In steer mode only
    /// once this view's stick is planted — the indicator then stands in for the cursor; if planting
    /// bailed out (no analysis yet / nothing under the pointer) the reader keeps their pointer.</summary>
    public bool IsGuideCursorHiddenOver(Viewport? vp)
    {
        if (!IsGuideCursorHidden) return false;
        if (!GuideIsSteer) return true;
        return vp is not null && _guideByVp.TryGetValue(vp, out var s) && s.Steer.Engaged;
    }

    public void ToggleReadingGuideMode()
    {
        if (!IsReadingGuideMode && ActiveTab is null) return;
        IsReadingGuideMode = !IsReadingGuideMode;
    }

    partial void OnIsReadingGuideModeChanged(bool value)
    {
        if (value)
        {
            // A pointer mode: leave annotation mode (which drops its tool) or a bare Text-Select, and
            // disarm any one-shot viewport-click gesture so the next click isn't consumed by it.
            if (IsAnnotationMode) IsAnnotationMode = false;
            else if (ActiveTool != AnnotationTool.None) SetAnnotationTool(AnnotationTool.None);
            ArmActivateRailClick = false;
            FreezeArmMode = FreezeMode.None;
            ShowStatusToast(_guidePrefs.Follow switch
            {
                ReadingGuideFollow.Steer => "Reading guide on — hold Shift and nudge the pointer to read; click a word to start there (K or Escape to leave)",
                ReadingGuideFollow.Always => "Reading guide on — move the pointer over the text (K or Escape to leave)",
                _ => "Reading guide on — hold Shift and move the pointer (K or Escape to leave)",
            });
        }
        else
        {
            ClearAllGuides();
            _guideShiftHeld = false;
            _guideChordCancelled = false;
            _guidePreChord = null;
        }
        OnPropertyChanged(nameof(IsGuideFollowing));
        OnPropertyChanged(nameof(IsGuideCursorHidden));
        InvalidateReadingGuide();
    }

    /// <summary>The guide position to draw for <paramref name="vp"/>, if the mode is on and the parked /
    /// followed position belongs to the page the viewport is showing.</summary>
    public bool TryGetGuide(Viewport vp, out ReadingGuidePosition position)
    {
        position = default;
        if (!IsReadingGuideMode || !_guideByVp.TryGetValue(vp, out var s)
            || s.Position is not { } p || s.Page != vp.CurrentPage)
            return false;
        position = p;
        return true;
    }

    /// <summary>Pointer moved over <paramref name="vp"/>'s surface: <paramref name="pageX"/>/<paramref name="pageY"/>
    /// on the anchor page, <paramref name="screenX"/>/<paramref name="screenY"/> in viewport pixels.
    /// Always records the point (a later Shift press acts on it without a wiggle). While following:
    /// steer mode feeds the stick, point mode resolves the word under the pointer.</summary>
    public void TrackGuidePointer(Viewport vp, double pageX, double pageY, double screenX, double screenY)
    {
        if (!IsReadingGuideMode) return;
        if (!_guideByVp.TryGetValue(vp, out var s))
            _guideByVp[vp] = s = new GuideState();
        long now = Stopwatch.GetTimestamp();
        double dxPx = screenX - s.ScreenX, dyPx = screenY - s.ScreenY;
        double ms = s.PointerOver ? (now - s.LastMoveTicks) * 1000.0 / Stopwatch.Frequency : double.PositiveInfinity;
        s.PointerOver = true;
        s.PointerX = pageX;
        s.PointerY = pageY;
        s.ScreenX = screenX;
        s.ScreenY = screenY;
        s.LastMoveTicks = now;
        if (!IsGuideFollowing) return;

        if (GuideIsSteer)
        {
            // Shift went down with the pointer elsewhere (or off the window): plant the stick on first contact.
            if (!s.Steer.Engaged) EngageSteer(vp, s);
            s.Steer.SetPointer(screenX, screenY);
            if (s.Steer.Engaged) RequestAnimationFrame();
            return;
        }

        // Point mode. Return sweep: a fast leftward move (the hand flying back to the next line's start)
        // keeps the current word instead of lighting every word it crosses in reverse.
        if (ms > 0 && ms < 80 && dxPx < 0 && Math.Abs(dxPx) > Math.Abs(dyPx)
            && -dxPx / ms > SweepGatePxPerMs && s.Position is not null)
            return;
        ResolveGuide(vp, s);
    }

    /// <summary>Pointer left <paramref name="vp"/>'s surface: the guide stays parked (and a steering
    /// stick keeps its throttle until Shift is released); only the "pointer is here" flag drops so a Shift
    /// press elsewhere doesn't act on this view.</summary>
    public void GuidePointerExited(Viewport vp)
    {
        if (_guideByVp.TryGetValue(vp, out var s)) s.PointerOver = false;
    }

    /// <summary>A click on a word while the mode is on (no drag): seat the guide there — the way to jump
    /// in steer mode, where the pointer's position otherwise means nothing. Re-plants an engaged stick
    /// at the click so the throttle restarts from rest. Returns false — click not consumed — at rail
    /// zoom (§11), where the click is the rail's own (Core's <c>HandleClick</c> seats the block / line).</summary>
    public bool SeatGuideAt(Viewport? vp, double pageX, double pageY, double screenX, double screenY)
    {
        if (vp is null || !IsReadingGuideMode) return false;
        if (!_guideByVp.TryGetValue(vp, out var s))
            _guideByVp[vp] = s = new GuideState();
        s.PointerOver = true;
        s.PointerX = pageX;
        s.PointerY = pageY;
        s.ScreenX = screenX;
        s.ScreenY = screenY;
        if (vp.Rail.VisualsVisible)
        {
            // The click falls through to the rail's own seat (block + line under it); the steered reading
            // point lands on the clicked word once that line is current.
            vp.Rail.SeatSteerReadingPoint(pageX);
            if (s.Steer.Engaged) s.Steer.Engage(screenX, screenY);
            InvalidateGuideLayer(vp);
            return false;
        }
        s.PendingSeat = 0;
        // Re-plant the stick first so the repaint ResolveGuide triggers already has the new origin.
        if (s.Steer.Engaged) s.Steer.Engage(screenX, screenY);
        ResolveGuide(vp, s);
        return true;
    }

    /// <summary>Shift pressed / released (MainWindow key handling, forwarded from tear-off windows too).
    /// Press: snapshot the parked positions (for a chord cancel), then — steer — plant the stick where
    /// the pointer rests over a view (seating the guide there first if nothing is parked on that page),
    /// or — point — resolve under the pointer. Release: park — nothing moves, the position simply stops
    /// following (steer: the stick lets go).</summary>
    public void SetGuideShiftHeld(bool held)
    {
        if (!IsReadingGuideMode || _guideShiftHeld == held) return;
        _guideShiftHeld = held;
        if (held)
        {
            _guideChordCancelled = false;
            _guidePreChord = new();
            foreach (var (vp, s) in _guideByVp)
                _guidePreChord.Add((vp, s.Page, s.Position, s.Analysis));
            if (GuideUsesShift)
                foreach (var (vp, s) in _guideByVp)
                {
                    if (!s.PointerOver) continue;
                    if (GuideIsSteer) EngageSteer(vp, s);
                    else ResolveGuide(vp, s);
                }
        }
        else
        {
            _guidePreChord = null;
            _guideChordCancelled = false;
            ReleaseSteer();
        }
        OnPropertyChanged(nameof(IsGuideFollowing));
        OnPropertyChanged(nameof(IsGuideCursorHidden));
        InvalidateReadingGuide();
    }

    /// <summary>A non-Shift key went down while Shift is held: that is a chord, not a follow. Restore the
    /// positions parked before the Shift press and stop following until Shift is released and pressed
    /// again. No-op in "always" mode, where Shift has no meaning.</summary>
    public void CancelGuideChord()
    {
        if (!IsReadingGuideMode || !_guideShiftHeld || _guideChordCancelled || !GuideUsesShift)
            return;
        _guideChordCancelled = true;
        ReleaseSteer();
        if (_guidePreChord is { } snapshot)
            foreach (var (vp, page, pos, analysis) in snapshot)
                if (_guideByVp.TryGetValue(vp, out var s))
                {
                    s.Page = page;
                    s.Position = pos;
                    s.Analysis = analysis;
                    s.PendingSeat = 0;
                    s.Tester.Clear();
                }
        _guidePreChord = null;
        OnPropertyChanged(nameof(IsGuideFollowing));
        OnPropertyChanged(nameof(IsGuideCursorHidden));
        InvalidateReadingGuide();
    }

    /// <summary>Settings changed (follow mode / word highlight / dead zone): persist and repaint.</summary>
    public void OnReadingGuidePrefsChanged()
    {
        _guidePrefs.Save();
        ReleaseSteer();
        OnPropertyChanged(nameof(IsGuideFollowing));
        OnPropertyChanged(nameof(IsGuideCursorHidden));
        InvalidateReadingGuide();
    }

    // ── Steer ──────────────────────────────────────────────────────────────────

    /// <summary>Apply the sensitivity preference to a stick's geometry (called each time it is planted).</summary>
    private void ConfigureSteerGeometry(Viewport vp, GuideState s)
    {
        double k = Math.Clamp(_guidePrefs.SteerSensitivity, 0.25, 4.0);
        double full = SteerFullDeflectionBasePx / k, dead = SteerDeadZoneBasePx / k;
        // A small pane / tear-off / portal view can't hold the full throw (the pointer leaving the pane
        // stops reporting): keep full deflection within ~45 % of its short side.
        double limit = 0.45 * Math.Min(vp.Width, vp.Height);
        if (limit > 0 && full > limit) full = Math.Max(limit, 60);
        s.Steer.FullDeflectionPx = full;
        s.Steer.DeadZonePx = Math.Min(dead, full * 0.4);
    }

    /// <summary>The on-screen steering indicator for <paramref name="vp"/> while its stick is planted:
    /// the pointer glyph is hidden while steering, and a hand with no visible cursor loses track of where
    /// the stick's centre is and how far it has pushed. Null when the stick isn't engaged.</summary>
    public SteerIndicator? TryGetSteerIndicator(Viewport vp)
    {
        if (!IsReadingGuideMode || !_guideByVp.TryGetValue(vp, out var s) || !s.Steer.Engaged)
            return null;
        return new SteerIndicator(s.Steer.OriginX, s.Steer.OriginY, s.ScreenX, s.ScreenY,
            s.Steer.DeadZonePx, s.Steer.FullDeflectionPx, Math.Abs(s.Steer.Throttle), s.Parked);
    }

    private void EngageSteer(Viewport vp, GuideState s)
    {
        s.Parked = false;
        s.RailSnapshot = null;
        ConfigureSteerGeometry(vp, s);
        if (vp.Rail.VisualsVisible)
        {
            // §11: at rail zoom the stick drives the rail itself — nothing to seat. Planting stops a running
            // (semi-)auto-scroll: the stick supersedes it, as A/Left does.
            if (_controller.AutoScrollActive && ReferenceEquals(vp, _controller.FocusedViewport))
                _controller.StopAutoScroll();
            // The pointer resting on a word of the current line: read from that word (the camera eases to
            // centre it); elsewhere, continue from where the rail is.
            vp.Rail.TrySeatSteerReadingPointAt(s.PointerX, s.PointerY);
            // A line step is a camera snap here, so repeat more slowly than the word guide's line stepping.
            s.Steer.MinLinesPerSecond = 0.6;
            s.Steer.MaxLinesPerSecond = 2.0;
            s.RailSteering = true;
            s.Steer.Engage(s.ScreenX, s.ScreenY);
            RequestAnimationFrame();
            return;
        }
        s.RailSteering = false;
        if (s.Position is null || s.Page != vp.CurrentPage)
        {
            // Nothing parked on this page: start on the word under the pointer.
            s.PendingSeat = 0;
            ResolveGuide(vp, s);
            if (s.Position is null) return; // no analysis yet (toasted) — try again on the next Shift press
        }
        var cfg = _controller.Config;
        s.Steer.MinLinesPerSecond = 0.8;
        s.Steer.MaxLinesPerSecond = 3.0;
        s.Steer.MinCharsPerSecond = Math.Max(1.0, cfg.ScrollSpeedStart * CharsPerSecondPerScrollSpeed * SteerEdgePaceFraction);
        s.Steer.MaxCharsPerSecond = Math.Max(s.Steer.MinCharsPerSecond, cfg.ScrollSpeedMax * CharsPerSecondPerScrollSpeed);
        s.Steer.Engage(s.ScreenX, s.ScreenY);
        RequestAnimationFrame();
    }

    private void ReleaseSteer()
    {
        foreach (var (vp, s) in _guideByVp)
        {
            if (s.Steer.Engaged) s.Steer.Release();
            ReleaseRailSteer(vp, s);
        }
    }

    private void ReleaseRailSteer(Viewport vp, GuideState s)
    {
        if (!s.RailSteering) return;
        s.RailSteering = false;
        _controller.SteerRail(vp, 0);
    }

    /// <summary>Per frame while the stick drives the rail (§11): the stick's horizontal throttle becomes the
    /// rail's steered scroll velocity (Core moves the camera in its own tick — line ends, snaps, page turns
    /// and stop-unit parks all happen there), a vertical step is ↑/↓. Returns true while the stick is
    /// deflected so the frame loop keeps running.</summary>
    private bool TickRailSteer(Viewport vp, GuideState s, double dt)
    {
        if (!vp.Rail.VisualsVisible)
        {
            // The rail went away under the stick (zoomed out, free pan, page without analysis): let go;
            // the next Shift press re-plants in whichever regime applies then.
            s.Steer.Release();
            ReleaseRailSteer(vp, s);
            return false;
        }
        var step = s.Steer.Tick(dt, 0, false);   // word steps are ignored here — only the throttle and line steps matter
        var here = (vp.CurrentPage, vp.Rail.CurrentNavigableArrayIndex, vp.Rail.CurrentLine);
        if (s.Parked)
        {
            // Parked after the rail advanced by itself: hold still until the pointer is back in the still zone.
            if (s.Steer.Axis == SteerAxis.None) s.Parked = false;
            _controller.SteerRail(vp, 0);
            s.RailSnapshot = here;
            return false;
        }
        // The rail moved to another line / block / page since the last tick without a vertical nudge: it
        // advanced by itself at a line end (Core's edge-hold advance) — park there.
        if (_guidePrefs.ParkAtNewLine && s.RailSnapshot is { } before && before != here)
        {
            s.Parked = true;
            _controller.SteerRail(vp, 0);
            s.RailSnapshot = here;
            return false;
        }
        _controller.SteerRail(vp, s.Steer.Axis == SteerAxis.Horizontal ? s.Steer.Throttle : 0);
        if (step.LineSteps != 0)
        {
            _controller.StepRailLine(vp, step.LineSteps > 0);
            here = (vp.CurrentPage, vp.Rail.CurrentNavigableArrayIndex, vp.Rail.CurrentLine);   // the nudge's own move
        }
        s.RailSnapshot = here;
        // The word marker follows Core's reading point, which moves while the camera is pinned at a line's
        // head or tail; TickReadingGuide re-sends the guide layer after every tick, so it is redrawn then.
        return s.Steer.IsMoving;
    }

    /// <summary>The rail word marker (§11, on with <see cref="ReadingGuidePreferences.WordHighlight"/>):
    /// while <paramref name="vp"/>'s stick drives the rail, the word on the current rail line under Core's
    /// steered reading point (<c>RailNav.SteerReadingPointPageX</c> — it sweeps the line's head and tail
    /// while the camera is pinned, so every word gets its turn; before the first tick, the window's reading
    /// point). Rotated view: none.</summary>
    public bool TryGetRailWordMarker(Viewport vp, out RectF rect)
    {
        rect = default;
        if (!IsReadingGuideMode || !_guidePrefs.WordHighlight || !_guideByVp.TryGetValue(vp, out var s)
            || !s.RailSteering || !s.Steer.Engaged || !vp.Rail.VisualsVisible)
            return false;
        var doc = vp.Owner;
        if (doc.ViewRotation != 0 || !doc.TryGetAnalysis(vp.CurrentPage, vp.AnalysisParams, out var analysis))
            return false;
        var words = WordIndex.For(analysis, doc.GetOrExtractText(vp.CurrentPage));
        var list = words.WordsOf(vp.Rail.CurrentNavigableArrayIndex, vp.Rail.CurrentLine);
        if (list.Count == 0) return false;
        double px = vp.Rail.SteerReadingPointPageX
            ?? (vp.Width * vp.Rail.SteerReadingPointFraction - vp.Camera.OffsetX) / vp.Camera.Zoom;
        double bestD = double.MaxValue;
        foreach (var w in list)
        {
            double d = px < w.Rect.Left ? w.Rect.Left - px : px > w.Rect.Right ? px - w.Rect.Right : 0;
            if (d < bestD) { bestD = d; rect = w.Rect; }
        }
        return true;
    }

    /// <summary>Per animation frame: advance every engaged stick — driving the rail where it is active
    /// (§11) — otherwise apply its word / line steps along reading order (turning the page at the ends),
    /// and ease the camera to keep the guide in view.
    /// Returns true while anything is still moving (the frame loop keeps running).</summary>
    private bool TickReadingGuide(double dt)
    {
        if (!IsReadingGuideMode || _guideByVp.Count == 0) return false;
        bool animating = false;
        foreach (var (vp, s) in _guideByVp)
        {
            if (!s.Steer.Engaged) continue;
            if (TickStick(vp, s, dt)) animating = true;
            // One repaint of the stick indicator per frame, AFTER the tick: the pointer moves while the page
            // may not, and a release inside the tick (document end, rail gone) must clear it too.
            InvalidateGuideLayer(vp);
        }
        return animating;
    }

    /// <summary>One engaged stick's frame: drive the rail (§11) or step the word guide, then keep it in view.
    /// Returns true while it is still moving.</summary>
    private bool TickStick(Viewport vp, GuideState s, double dt)
    {
        if (s.RailSteering) return TickRailSteer(vp, s, dt);
        if (s.PendingSeat != 0) return true;   // page turned; waiting for analysis
        if (s.Position is not { } pos || s.Page != vp.CurrentPage) return false;

        var doc = vp.Owner;
        if (!doc.TryGetAnalysis(s.Page, vp.AnalysisParams, out var analysis)) return false;
        var words = doc.ViewRotation == 0 ? WordIndex.For(analysis, doc.GetOrExtractText(s.Page)) : null;
        var roles = _controller.Config.NavigableRoles;

        int chars = 0;
        bool sentenceEnd = false;
        if (words is not null && pos.WordIndex is { } wi)
        {
            var list = words.WordsOf(pos.BlockIndex, pos.LineIndex);
            if (wi < list.Count)
            {
                string w = words.TextOf(list[wi]);
                chars = w.Length;
                sentenceEnd = w.Length > 0 && w[^1] is '.' or '!' or '?' or ':' or ';';
            }
        }

        var step = s.Steer.Tick(dt, chars, sentenceEnd);
        if (s.Parked)
        {
            // Parked after a new line: ignore the stick until the pointer is back in the still zone.
            if (s.Steer.Axis == SteerAxis.None) s.Parked = false;
            return KeepGuideInView(vp, s, dt);
        }
        if (step.Any)
        {
            bool forward = (step.WordSteps + step.LineSteps) > 0;
            var next = step.WordSteps != 0
                ? ReadingGuideFlow.StepWord(pos, forward, analysis, words, roles)
                : ReadingGuideFlow.StepLine(pos, forward, analysis, words, roles);
            if (next is { } n)
            {
                // The flow ran off the end of a line (or into another block) by itself: park there. A
                // vertical nudge asked for its line, so it doesn't park.
                if (step.WordSteps != 0 && _guidePrefs.ParkAtNewLine
                    && (n.BlockIndex != pos.BlockIndex || n.LineIndex != pos.LineIndex))
                    s.Parked = true;
                SetGuide(vp, s, s.Page, n, analysis);
            }
            else
                TurnGuidePage(vp, s, forward);
        }
        bool animating = s.Steer.IsMoving;
        if (KeepGuideInView(vp, s, dt)) animating = true;
        return animating;
    }

    /// <summary>Flow reached the page end / start: turn this viewport's page and seat on the new page's
    /// first / last word once its analysis is in (<see cref="RefreshGuidesAfterAnalysis"/>).</summary>
    private void TurnGuidePage(Viewport vp, GuideState s, bool forward)
    {
        var doc = vp.Owner;
        int target = vp.CurrentPage + (forward ? 1 : -1);
        if (target < 0 || target >= doc.PageCount || vp.Focus is not null)
        {
            s.Steer.Release(); // nowhere to flow: let go so the stick doesn't spin at the document end
            return;
        }
        if (!doc.GoToPage(vp, target, _controller.Worker, _controller.Config.NavigableRoles, vp.Width, vp.Height))
            return;
        doc.QueueLookahead(vp, _appConfig.AnalysisLookaheadPages);
        s.Parked = _guidePrefs.ParkAtNewLine;   // a new page is the biggest change of place there is
        s.PendingSeat = forward ? 1 : -1;
        s.Page = target;
        s.Position = null;
        s.Analysis = null;
        s.Tester.Clear();
        foreach (var surface in _surfaces)
            if (ReferenceEquals(surface.SurfaceViewport, vp))
            {
                surface.RenderPage();
                surface.RenderSearch();
                surface.RenderAnnotations();
                surface.RenderCamera();
            }
        OnPropertyChanged(nameof(ActiveTab));
        TrySeatPending(vp, s);
    }

    private void TrySeatPending(Viewport vp, GuideState s)
    {
        if (s.PendingSeat == 0 || vp.CurrentPage != s.Page) return;
        var doc = vp.Owner;
        if (!doc.TryGetAnalysis(s.Page, vp.AnalysisParams, out var analysis)) return;
        var words = doc.ViewRotation == 0 ? WordIndex.For(analysis, doc.GetOrExtractText(s.Page)) : null;
        var roles = _controller.Config.NavigableRoles;
        var pos = s.PendingSeat > 0
            ? ReadingGuideFlow.First(analysis, words, roles)
            : ReadingGuideFlow.Last(analysis, words, roles);
        s.PendingSeat = 0;
        if (pos is null) { s.Steer.Release(); InvalidateGuideLayer(vp); return; }   // a page with no text to flow through
        SetGuide(vp, s, s.Page, pos, analysis);
        if (s.Steer.Engaged) RequestAnimationFrame();
    }

    /// <summary>Ease this viewport's camera so the guide word (or line) sits inside the keep-in-view band,
    /// by the minimum amount and never recentring. Returns true while the camera is still easing.</summary>
    private bool KeepGuideInView(Viewport vp, GuideState s, double dt)
    {
        if (s.Position is not { } pos || s.Page != vp.CurrentPage || IsViewportFrozen(vp)) return false;
        var cam = vp.Camera;
        double z = cam.Zoom;
        var rect = pos.WordRect ?? pos.LineRect;
        double top = rect.Top * z + cam.OffsetY, bottom = rect.Bottom * z + cam.OffsetY;
        double left = rect.Left * z + cam.OffsetX, right = rect.Right * z + cam.OffsetX;
        double H = vp.Height, W = vp.Width;

        double targetY = cam.OffsetY, targetX = cam.OffsetX;
        if (bottom > H * KeepInViewBottom) targetY -= bottom - H * KeepInViewBottom;
        else if (top < H * KeepInViewTop) targetY += H * KeepInViewTop - top;
        if (right > W * KeepInViewRight) targetX -= right - W * KeepInViewRight;
        else if (left < W * KeepInViewLeft) targetX += W * KeepInViewLeft - left;

        double dy = targetY - cam.OffsetY, dx = targetX - cam.OffsetX;
        if (Math.Abs(dy) < 0.5 && Math.Abs(dx) < 0.5) return false;

        double k = 1 - Math.Exp(-dt / KeepInViewTauSecs);
        cam.OffsetY += Math.Abs(dy) < 1 ? dy : dy * k;
        cam.OffsetX += Math.Abs(dx) < 1 ? dx : dx * k;
        vp.ClampCamera(W, H);
        foreach (var surface in _surfaces)
            if (ReferenceEquals(surface.SurfaceViewport, vp))
                surface.RenderCamera();
        return true;
    }

    // ── Point / shared ─────────────────────────────────────────────────────────

    private void ResolveGuide(Viewport vp, GuideState s)
    {
        var doc = vp.Owner;
        int page = vp.CurrentPage;
        if (!doc.TryGetAnalysis(page, vp.AnalysisParams, out var analysis))
        {
            if (s.NoAnalysisToastPage != page)
            {
                s.NoAnalysisToastPage = page;
                ShowStatusToast("The reading guide needs layout analysis here — try again in a moment");
            }
            SetGuide(vp, s, page, null, null);
            return;
        }

        // The text layer is in the page's own frame while block lines follow the rotated analysis, so a
        // rotated view runs line-only (design §7 decision) — no word index.
        WordIndex? words = doc.ViewRotation == 0
            ? WordIndex.For(analysis, doc.GetOrExtractText(page))
            : null;

        var tester = s.Tester;
        tester.NavigableRoles = _controller.Config.NavigableRoles;
        tester.DeadZoneFraction = (float)_guidePrefs.DeadZone;
        var pos = tester.Resolve((float)s.PointerX, (float)s.PointerY, analysis, words);
        SetGuide(vp, s, page, pos, analysis);
    }

    private void SetGuide(Viewport vp, GuideState s, int page, ReadingGuidePosition? pos, PageAnalysis? analysis)
    {
        bool changed = s.Page != page || s.Position != pos;
        s.Page = page;
        s.Position = pos;
        s.Analysis = analysis;
        if (changed) InvalidateReadingGuide(vp);
    }

    /// <summary>After analysis results land: seat a pending steer page turn, and re-read each parked
    /// position's indices against a replaced analysis instance (re-run, OCR, config change) so the rects
    /// refresh; a position whose indices no longer exist clears rather than pointing at the wrong line
    /// (design §3.3 — Core's Resolve is point-based, so this is shell work).</summary>
    private void RefreshGuidesAfterAnalysis()
    {
        if (!IsReadingGuideMode || _guideByVp.Count == 0) return;
        foreach (var (vp, s) in _guideByVp)
        {
            if (s.PendingSeat != 0) { TrySeatPending(vp, s); continue; }
            if (s.Position is not { } pos || s.Page != vp.CurrentPage) continue;
            var doc = vp.Owner;
            if (!doc.TryGetAnalysis(s.Page, vp.AnalysisParams, out var analysis)
                || ReferenceEquals(analysis, s.Analysis))
                continue;
            var words = doc.ViewRotation == 0 ? WordIndex.For(analysis, doc.GetOrExtractText(s.Page)) : null;
            SetGuide(vp, s, s.Page,
                ReadingGuideFlow.At(analysis, words, pos.BlockIndex, pos.LineIndex, pos.WordIndex), analysis);
        }
    }

    private void ClearAllGuides()
    {
        foreach (var (vp, s) in _guideByVp)
        {
            s.Tester.Clear();
            s.Steer.Release();
            ReleaseRailSteer(vp, s);
            s.Position = null;
            s.Analysis = null;
            s.Page = -1;
            s.PendingSeat = 0;
        }
    }

    /// <summary>A viewport is going away (tab / pane / window closed): drop its guide state so the
    /// dictionary doesn't pin the disposed viewport (and, via Owner, its document).</summary>
    private void DropGuideState(Viewport vp) => _guideByVp.Remove(vp);

    /// <summary>Re-send only the guide layer (no page-state rebuild) — the per-frame path for the steer
    /// indicator, which moves with every pointer move.</summary>
    private void InvalidateGuideLayer(Viewport vp)
    {
        foreach (var s in _surfaces)
            if (ReferenceEquals(s.SurfaceViewport, vp))
                s.RenderReadingGuideLayer();
    }

    private void InvalidateReadingGuide(Viewport? vp = null)
    {
        foreach (var s in _surfaces)
            if (vp is null || ReferenceEquals(s.SurfaceViewport, vp))
                s.RenderReadingGuide();
    }
}
