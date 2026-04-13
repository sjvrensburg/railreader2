using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Tick (animation frame logic) ---

    /// <summary>
    /// Advance one animation frame. Returns what needs repainting.
    /// </summary>
    public TickResult Tick(double dt)
    {
        dt = Math.Min(dt, 1.0 / 30.0);

        var doc = ActiveDocument;
        if (doc is null) return default;

        var (ww, wh) = GetViewportSize();
        bool cameraChanged = false;
        bool pageChanged = false;
        bool overlayChanged = false;
        bool animating = false;

        TickZoomAnimation(doc, ww, wh, ref cameraChanged, ref animating);

        if (!RailPaused)
            TickRailSnap(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Snap Y to integer pixel when rail mode is stable or nearly so.
        // Snapping during the snap animation tail (progress > 0.95) eliminates
        // the last few frames of sub-pixel text shimmer before full stop.
        if (_config.PixelSnapping && doc.Rail.Active
            && (!animating || doc.Rail.SnapProgress > 0.95))
        {
            double snapped = Math.Round(doc.Camera.OffsetY);
            if (snapped != doc.Camera.OffsetY)
            {
                doc.Camera.OffsetY = snapped;
                cameraChanged = true;
            }
        }

        if (!RailPaused)
            TickAutoScroll(doc, dt, ww, wh, ref cameraChanged, ref pageChanged, ref overlayChanged, ref animating);

        // Decay zoom blur speed
        if (doc.Camera.ZoomSpeed > 0)
        {
            doc.Camera.DecayZoomSpeed(dt);
            animating = true;
            if (doc.Camera.ZoomSpeed > 0)
                cameraChanged = true;
        }

        // Poll analysis results
        var (gotResults, needsAnim) = PollAnalysisResults();
        animating |= needsAnim;
        overlayChanged |= gotResults;

        if (!animating)
        {
            if (!doc.SubmitPendingLookahead(_worker)
                && !doc.Rail.Active
                && _worker is not null && _worker.IsIdle)
                TrySubmitBackgroundReadAhead();
        }

        // DPI bitmap swap
        if (doc.DpiRenderReady)
        {
            doc.DpiRenderReady = false;
            pageChanged = true;
        }

        // Retry any DPI re-render that was skipped because scroll was active.
        // UpdateRenderDpiIfNeeded gates on Rail.ScrollSpeed/AutoScrolling and
        // does nothing if the cached DPI is already within hysteresis, so this
        // poll is cheap.
        if (!animating)
            doc.UpdateRenderDpiIfNeeded();

        return new TickResult(cameraChanged, pageChanged, overlayChanged, false, false, animating);
    }

    /// <summary>Smooth zoom animation step (delegated to ZoomAnimationController).</summary>
    private void TickZoomAnimation(DocumentState doc, double ww, double wh,
        ref bool cameraChanged, ref bool animating)
    {
        _zoom.Tick(doc, ww, wh, ref cameraChanged, ref animating);
    }

    /// <summary>Rail snap animation and edge-hold line advance (skipped while zoom is animating).</summary>
    private void TickRailSnap(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (!_zoom.IsAnimating)
        {
            double cx = doc.Camera.OffsetX, cy = doc.Camera.OffsetY;
            bool railAnimating = doc.Rail.Tick(ref cx, ref cy, dt, doc.Camera.Zoom, ww);
            if (cx != doc.Camera.OffsetX || cy != doc.Camera.OffsetY)
            {
                doc.Camera.OffsetX = cx;
                doc.Camera.OffsetY = cy;
                cameraChanged = true;
            }
            animating |= railAnimating;

            if (doc.Rail.ConsumeAutoScrollTrigger())
            {
                _autoScroll.ActivateAutoScroll();
                StatusMessage?.Invoke("Auto-scroll activated");
            }

            HandleEdgeAdvance(doc, ww, wh, ref pageChanged, ref cameraChanged, ref overlayChanged);
        }
    }

    /// <summary>
    /// Handles edge-hold line advances: D/Right held at line end → NextLine;
    /// A/Left held at line start → PrevLine.
    /// </summary>
    private void HandleEdgeAdvance(DocumentState doc, double ww, double wh,
        ref bool pageChanged, ref bool cameraChanged, ref bool overlayChanged)
    {
        if (doc.Rail.AutoScrolling) return;
        if (doc.Rail.ConsumePendingEdgeAdvance() is not { } edgeDir) return;

        bool forward = edgeDir == ScrollDirection.Forward;
        var adv = AdvanceLine(doc, forward, ww, wh);
        if (adv is LineAdvanceResult.PageChanged or LineAdvanceResult.PageChangedRailLost)
        {
            pageChanged = true;
            if (!forward && adv == LineAdvanceResult.PageChanged)
                doc.StartSnapToEnd(ww, wh);
        }
        else if (adv == LineAdvanceResult.LineAdvanced)
        {
            if (forward) doc.StartSnap(ww, wh);
            else doc.StartSnapToEnd(ww, wh);
        }
        overlayChanged = true;
        cameraChanged = true;
    }

    /// <summary>Auto-scroll tick: advance along the current line, then advance to the next line/page.</summary>
    private void TickAutoScroll(DocumentState doc, double dt, double ww, double wh,
        ref bool cameraChanged, ref bool pageChanged, ref bool overlayChanged, ref bool animating)
    {
        if (doc.Rail.AutoScrolling)
        {
            if (doc.Rail.NavigableCount > 0
                && doc.Rail.CurrentBlock >= doc.Rail.NavigableCount - 2
                && doc.CurrentPage + 1 < doc.PageCount)
            {
                doc.PrefetchPage(doc.CurrentPage + 1);
            }

            double cx = doc.Camera.OffsetX;
            bool reachedEnd = doc.Rail.TickAutoScroll(ref cx, dt, doc.Camera.Zoom, ww);
            if (cx != doc.Camera.OffsetX)
            {
                doc.Camera.OffsetX = cx;
                cameraChanged = true;
            }
            animating = true;

            if (reachedEnd)
            {
                int prevBlock = doc.Rail.CurrentBlock;
                int prevLine = doc.Rail.CurrentLine;
                var adv = AdvanceLine(doc, forward: true, ww, wh);
                switch (adv)
                {
                    case LineAdvanceResult.PageChanged:
                        pageChanged = true;
                        doc.Rail.StartAutoScroll(_autoScroll.AutoScrollSpeed);
                        doc.Rail.PauseAutoScroll(GetBlockEntryPause(doc));
                        break;
                    case LineAdvanceResult.PageChangedRailLost:
                        pageChanged = true;
                        StopAutoScroll();
                        break;
                    case LineAdvanceResult.LineAdvanced:
                        if (doc.Rail.CurrentBlock == prevBlock && doc.Rail.CurrentLine == prevLine)
                        {
                            StopAutoScroll();
                            break;
                        }
                        doc.StartSnap(ww, wh);
                        bool enteredNewBlock = doc.Rail.CurrentBlock != prevBlock;
                        doc.Rail.PauseAutoScroll(enteredNewBlock ? GetBlockEntryPause(doc) : 0);
                        break;
                }
                overlayChanged = true;
            }
        }
    }

    /// <summary>
    /// Returns the auto-scroll pause duration for entering the current block,
    /// based on its class (equation, header, or default).
    /// </summary>
    private double GetBlockEntryPause(DocumentState doc) =>
        doc.Rail.CurrentNavigableBlock.ClassId switch
        {
            LayoutConstants.ClassDisplayFormula => _config.AutoScrollEquationPauseMs,
            LayoutConstants.ClassDocTitle or LayoutConstants.ClassParagraphTitle => _config.AutoScrollHeaderPauseMs,
            _ => _config.AutoScrollLinePauseMs,
        };

    /// <summary>
    /// Poll the analysis worker for completed results. Can also be called
    /// from a low-frequency timer when not animating.
    /// </summary>
    public (bool GotResults, bool NeedsAnimation) PollAnalysisResults()
    {
        bool got = false;
        bool needsAnim = false;
        if (_worker is null) return (false, false);
        var (ww, wh) = GetViewportSize();
        while (_worker.Poll() is { } result)
        {
            got = true;
            _logger.Debug($"[Analysis] Got result for {Path.GetFileName(result.FilePath)} page {result.Page}: {result.Analysis.Blocks.Count} blocks");
            foreach (var doc in Documents)
            {
                if (doc.IsDisposed || doc.FilePath != result.FilePath) continue;

                doc.SetAnalysis(result.Page, result.Analysis);

                if (doc.CurrentPage != result.Page)
                    continue;

                if (doc.PendingRailSetup)
                {
                    doc.Rail.SetAnalysis(result.Analysis, _config.NavigableClasses);
                    doc.PendingRailSetup = false;
                    doc.UpdateRailZoom(ww, wh);
                    _logger.Debug($"[Analysis] Rail has {doc.Rail.NavigableCount} navigable blocks, Active={doc.Rail.Active}");
                    if (doc.Rail.Active)
                    {
                        if (doc.PendingSkip is { } pendingSkip)
                            ApplySkipLanding(doc, pendingSkip.Forward, pendingSkip.SavedVerticalBias);
                        doc.PendingSkip = null;
                        doc.StartSnap(ww, wh);
                        needsAnim = true;
                    }
                    else if (doc.PendingSkip is not null)
                    {
                        if (doc == ActiveDocument)
                            needsAnim |= TryResumeSkip(doc, ww, wh);
                        else
                            doc.PendingSkip = null;
                    }
                }
            }
        }
        return (got, needsAnim);
    }

    /// <summary>
    /// Returns true if any document has unanalysed pages remaining.
    /// </summary>
    public bool HasBackgroundAnalysisWork
    {
        get
        {
            for (int i = 0; i < Documents.Count; i++)
            {
                var doc = Documents[i];
                if (!doc.IsDisposed && doc.HasPendingBackgroundWork)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Submits background analysis work when the lookahead queue is empty.
    /// Active document gets priority; other tabs are served round-robin.
    /// Can be called from the poll timer when the animation loop isn't running.
    /// </summary>
    public bool TrySubmitBackgroundReadAhead()
    {
        if (_worker is null) return false;

        var active = ActiveDocument;

        if (active is not null && !active.IsDisposed
            && active.SubmitBackgroundAnalysis(_worker))
            return true;

        for (int i = 0; i < Documents.Count; i++)
        {
            var doc = Documents[i];
            if (doc == active || doc.IsDisposed) continue;
            if (doc.SubmitBackgroundAnalysis(_worker))
                return true;
        }
        return false;
    }
}
