using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader2.Services;

namespace RailReader2.ViewModels;

// Reading guide — pointer-driven word + line focus for low-magnification readers
// (docs/reading-guide-design.md). A pointer MODE beside Browse / Text-Select: while it is on, holding
// Shift (or always, per preference) and moving the pointer over a page highlights the word under it
// and its line, snapped to the text geometry Core already knows; releasing Shift parks the guide
// where it is (page-anchored, so it scrolls with the text). The camera is never moved.
//
// State is PER-VIEWPORT (like freeze panes): each split pane / tear-off / duplicate tab keeps its own
// hit-tester (Core's ReadingGuideHitTester owns the line hysteresis) and parked position. Following
// acts on the view UNDER THE POINTER, not FocusedViewport — hover never focuses a pane.
//
// Shift chords (Shift+D debug overlay, Shift+H/G/T/E jumps, Shift+O/B/I panes, …) all begin with a
// Shift-down. Rule (design §7): Shift-down only re-resolves where the pointer actually rests over a
// view, and a non-Shift key pressed while Shift is held cancels the follow and restores every position
// parked before the chord, so a chord never moves a reader's place-keeper.
public sealed partial class MainWindowViewModel
{
    private sealed class GuideState
    {
        public readonly ReadingGuideHitTester Tester = new();
        public int Page = -1;
        public ReadingGuidePosition? Position;
        /// <summary>The analysis instance <see cref="Position"/> was resolved against, so a replaced
        /// analysis (re-run, OCR landed) can be detected and the parked indices re-read against it.</summary>
        public PageAnalysis? Analysis;
        /// <summary>True while the pointer is over this viewport's surface (so a Shift press with a still
        /// pointer can resolve its last position); cleared on pointer exit.</summary>
        public bool PointerOver;
        public double PointerX, PointerY; // last pointer position, page points on the anchor page
        public int NoAnalysisToastPage = -1;
    }

    private readonly Dictionary<Viewport, GuideState> _guideByVp = new();
    private readonly ReadingGuidePreferences _guidePrefs = ReadingGuidePreferences.Load();
    private bool _guideShiftHeld;
    private bool _guideChordCancelled;
    private List<(Viewport Vp, int Page, ReadingGuidePosition? Position, PageAnalysis? Analysis)>? _guidePreChord;

    /// <summary>The reading-guide pointer mode (toolbar button, Rail ▸ Reading Guide, <c>K</c>). Mutually
    /// exclusive with Text-Select and the annotation tools, like any pointer mode.</summary>
    [ObservableProperty] private bool _isReadingGuideMode;

    public ReadingGuidePreferences ReadingGuidePrefs => _guidePrefs;

    /// <summary>True while pointer moves should drive the guide: the mode is on and either the
    /// preference is "always" or Shift is held (and no chord has cancelled the hold).</summary>
    public bool IsGuideFollowing =>
        IsReadingGuideMode
        && (_guidePrefs.Follow == ReadingGuideFollow.Always || (_guideShiftHeld && !_guideChordCancelled));

    /// <summary>True while the pointer glyph should be hidden over a page: following under a held Shift.
    /// In "always" mode the pointer also serves ordinary clicking, so it stays visible.</summary>
    public bool IsGuideCursorHidden =>
        IsGuideFollowing && _guidePrefs.Follow == ReadingGuideFollow.WhileShiftHeld;

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
            ShowStatusToast(_guidePrefs.Follow == ReadingGuideFollow.Always
                ? "Reading guide on — move the pointer over the text (K or Escape to leave)"
                : "Reading guide on — hold Shift and move the pointer (K or Escape to leave)");
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

    /// <summary>Pointer moved over <paramref name="vp"/>'s surface at the given page point (anchor page).
    /// Always records the point (a later Shift press resolves it without a wiggle); resolves the guide
    /// only while <see cref="IsGuideFollowing"/>.</summary>
    public void TrackGuidePointer(Viewport vp, double pageX, double pageY)
    {
        if (!IsReadingGuideMode) return;
        if (!_guideByVp.TryGetValue(vp, out var s))
            _guideByVp[vp] = s = new GuideState();
        s.PointerOver = true;
        s.PointerX = pageX;
        s.PointerY = pageY;
        if (IsGuideFollowing) ResolveGuide(vp, s);
    }

    /// <summary>Pointer left <paramref name="vp"/>'s surface: the guide stays parked; only the "pointer
    /// is here" flag drops so a Shift press elsewhere doesn't re-resolve this view.</summary>
    public void GuidePointerExited(Viewport vp)
    {
        if (_guideByVp.TryGetValue(vp, out var s)) s.PointerOver = false;
    }

    /// <summary>Shift pressed / released (MainWindow key handling, forwarded from tear-off windows too).
    /// Press: snapshot the parked positions (for a chord cancel) and resolve wherever the pointer rests
    /// over a view. Release: park — nothing moves, the position simply stops following.</summary>
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
            if (_guidePrefs.Follow == ReadingGuideFollow.WhileShiftHeld)
                foreach (var (vp, s) in _guideByVp)
                    if (s.PointerOver) ResolveGuide(vp, s);
        }
        else
        {
            _guidePreChord = null;
            _guideChordCancelled = false;
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
        if (!IsReadingGuideMode || !_guideShiftHeld || _guideChordCancelled
            || _guidePrefs.Follow != ReadingGuideFollow.WhileShiftHeld)
            return;
        _guideChordCancelled = true;
        if (_guidePreChord is { } snapshot)
            foreach (var (vp, page, pos, analysis) in snapshot)
                if (_guideByVp.TryGetValue(vp, out var s))
                {
                    s.Page = page;
                    s.Position = pos;
                    s.Analysis = analysis;
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
        OnPropertyChanged(nameof(IsGuideFollowing));
        OnPropertyChanged(nameof(IsGuideCursorHidden));
        InvalidateReadingGuide();
    }

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

    /// <summary>After analysis results land: a page's analysis instance may have been replaced (re-run,
    /// OCR, config change). Re-read each parked position's indices against the new instance so the
    /// rects refresh; a position whose indices no longer exist clears rather than pointing at the wrong
    /// line (design §3.3 — Core's Resolve is point-based, so this is shell work).</summary>
    private void RefreshGuidesAfterAnalysis()
    {
        if (!IsReadingGuideMode || _guideByVp.Count == 0) return;
        foreach (var (vp, s) in _guideByVp)
        {
            if (s.Position is not { } pos || s.Page != vp.CurrentPage) continue;
            var doc = vp.Owner;
            if (!doc.TryGetAnalysis(s.Page, vp.AnalysisParams, out var analysis)
                || ReferenceEquals(analysis, s.Analysis))
                continue;
            SetGuide(vp, s, s.Page, ReanchorGuide(doc, s.Page, analysis, pos), analysis);
        }
    }

    private static ReadingGuidePosition? ReanchorGuide(DocumentModel doc, int page, PageAnalysis analysis,
        ReadingGuidePosition pos)
    {
        if ((uint)pos.BlockIndex >= (uint)analysis.Blocks.Count) return null;
        var block = analysis.Blocks[pos.BlockIndex];
        int lineCount = Math.Max(1, block.Lines.Count);
        if ((uint)pos.LineIndex >= (uint)lineCount) return null;
        var line = block.Lines.Count == 0
            ? new LineInfo(block.BBox.Y + block.BBox.H / 2f, block.BBox.H, block.BBox.X, block.BBox.W)
            : block.Lines[pos.LineIndex];
        var lineRect = new RectF(line.X, line.Y - line.Height / 2f, line.X + line.Width, line.Y + line.Height / 2f);

        int? wordIndex = null;
        RectF? wordRect = null;
        if (pos.WordIndex is { } w && doc.ViewRotation == 0)
        {
            var words = WordIndex.For(analysis, doc.GetOrExtractText(page)).WordsOf(pos.BlockIndex, pos.LineIndex);
            if (w < words.Count) { wordIndex = w; wordRect = words[w].Rect; }
        }
        return new ReadingGuidePosition(pos.BlockIndex, pos.LineIndex, wordIndex, lineRect, wordRect);
    }

    private void ClearAllGuides()
    {
        foreach (var s in _guideByVp.Values)
        {
            s.Tester.Clear();
            s.Position = null;
            s.Analysis = null;
            s.Page = -1;
        }
    }

    /// <summary>A viewport is going away (tab / pane / window closed): drop its guide state so the
    /// dictionary doesn't pin the disposed viewport (and, via Owner, its document).</summary>
    private void DropGuideState(Viewport vp) => _guideByVp.Remove(vp);

    private void InvalidateReadingGuide(Viewport? vp = null)
    {
        foreach (var s in _surfaces)
            if (vp is null || ReferenceEquals(s.SurfaceViewport, vp))
                s.RenderReadingGuide();
    }
}
