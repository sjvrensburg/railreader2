using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Core;

public sealed partial class DocumentController
{
    // --- Rail navigation ---

    private enum LineAdvanceResult { NoChange, LineAdvanced, PageChanged, PageChangedRailLost }

    private LineAdvanceResult AdvanceLine(DocumentState doc, bool forward, double ww, double wh)
    {
        int currentPage = doc.CurrentPage;
        var result = forward ? doc.Rail.NextLine() : doc.Rail.PrevLine();
        var boundary = forward ? NavResult.PageBoundaryNext : NavResult.PageBoundaryPrev;
        if (result == boundary)
        {
            return SkipToNavigablePage(doc, forward, 0, ww, wh) switch
            {
                SkipResult.FoundNavigable => LineAdvanceResult.PageChanged,
                _ => LineAdvanceResult.PageChangedRailLost,
            };
        }
        return result == NavResult.Ok ? LineAdvanceResult.LineAdvanced : LineAdvanceResult.NoChange;
    }

    private enum SkipResult { FoundNavigable, Deferred, Exhausted }

    /// <summary>
    /// Advance through pages in the given direction, skipping pages with no
    /// navigable blocks. Cached analysis is checked without rasterizing.
    /// If analysis is pending (async), stores skip state on the document for
    /// deferred continuation via <see cref="TryResumeSkip"/>.
    /// </summary>
    private SkipResult SkipToNavigablePage(DocumentState doc, bool forward, int skipped, double ww, double wh)
    {
        int step = forward ? 1 : -1;
        int targetPage = doc.CurrentPage + step;

        // Preserve vertical bias across page transitions so the line stays
        // at the same screen position instead of snapping to center.
        double savedBias = doc.Rail.VerticalBias;

        while (targetPage >= 0 && targetPage < doc.PageCount)
        {
            // Fast path: skip cached pages with no navigable blocks without rasterizing
            if (doc.AnalysisCache.TryGetValue(targetPage, out var cached)
                && !HasNavigableBlocks(cached))
            {
                skipped++;
                targetPage += step;
                continue;
            }

            // Either has navigable blocks (land on it) or needs async analysis
            if (!doc.GoToPage(targetPage, _worker, _config.NavigableClasses, ww, wh))
            {
                NotifyRenderFailed(targetPage);
                doc.PendingSkip = null;
                return SkipResult.Exhausted;
            }
            doc.UpdateRailZoom(ww, wh);

            if (doc.Rail.Active)
            {
                doc.PendingSkip = null;
                doc.QueueLookahead(_config.AnalysisLookaheadPages);
                ApplySkipLanding(doc, forward, savedBias);
                doc.StartSnap(ww, wh);
                if (skipped > 0) NotifyPagesSkipped(skipped);
                return SkipResult.FoundNavigable;
            }

            if (doc.PendingRailSetup)
            {
                doc.PendingSkip = new(forward, skipped, savedBias);
                doc.QueueLookahead(_config.AnalysisLookaheadPages);
                return SkipResult.Deferred;
            }

            skipped++;
            targetPage += step;
        }

        doc.PendingSkip = null;
        return SkipResult.Exhausted;
    }

    private bool HasNavigableBlocks(PageAnalysis analysis)
    {
        foreach (var block in analysis.Blocks)
            if (_config.NavigableClasses.Contains(block.ClassId))
                return true;
        return false;
    }

    private void NotifyPagesSkipped(int count)
    {
        StatusMessage?.Invoke(count == 1
            ? "Skipped 1 page (no text blocks)"
            : $"Skipped {count} pages (no text blocks)");
    }

    private static void ApplySkipLanding(DocumentState doc, bool forward, double savedBias)
    {
        if (!forward) doc.Rail.JumpToEnd();
        doc.Rail.VerticalBias = savedBias;
    }

    /// <summary>
    /// Resume a deferred skip after analysis arrived with no navigable blocks.
    /// Called from <see cref="PollAnalysisResults"/>.
    /// </summary>
    private bool TryResumeSkip(DocumentState doc, double ww, double wh)
    {
        var skip = doc.PendingSkip!;
        doc.Rail.VerticalBias = skip.SavedVerticalBias;
        return SkipToNavigablePage(doc, skip.Forward, skip.Skipped + 1, ww, wh) == SkipResult.FoundNavigable;
    }

    public void HandleArrowDown() => HandleVerticalNav(forward: true);
    public void HandleArrowUp() => HandleVerticalNav(forward: false);

    private void HandleVerticalNav(bool forward)
    {
        if (!forward && AutoScrollActive) StopAutoScroll();
        if (ActiveDocument is not { } doc) return;
        var (ww, wh) = GetViewportSize();

        if (doc.Rail.Active)
        {
            var adv = AdvanceLine(doc, forward, ww, wh);
            if (adv == LineAdvanceResult.LineAdvanced)
            {
                doc.StartSnap(ww, wh);
                // During autoscroll: pause until snap completes + line pause, then resume
                if (AutoScrollActive)
                    doc.Rail.PauseAutoScroll(_config.AutoScrollLinePauseMs);
            }
        }
        else
        {
            if (_pageEdgeHold.ShouldSuppressInput) return;

            double prevY = doc.Camera.OffsetY;
            doc.Camera.OffsetY += forward ? -CoreTuning.PanStep : CoreTuning.PanStep;
            doc.ClampCamera(ww, wh);

            bool atEdge = Math.Abs(doc.Camera.OffsetY - prevY) < 1.0;
            if (atEdge)
            {
                if (_pageEdgeHold.OnEdgeHit(forward))
                {
                    int targetPage = doc.CurrentPage + (forward ? 1 : -1);
                    if (targetPage >= 0 && targetPage < doc.PageCount)
                    {
                        GoToPage(targetPage);
                        // Align the content edge with the viewport edge (reduces to page
                        // edge when margin cropping is off, since GetFitRect returns the
                        // full page).
                        var (_, ry, _, rh) = doc.GetFitRect();
                        double topTarget = -ry * doc.Camera.Zoom;
                        doc.Camera.OffsetY = forward
                            ? topTarget
                            : Math.Min(wh - (ry + rh) * doc.Camera.Zoom, topTarget);
                        doc.ClampCamera(ww, wh);
                    }
                }
            }
            else
            {
                _pageEdgeHold.OnMoved();
            }
        }
    }

    /// <summary>Clear non-rail edge-hold state (call on key release).</summary>
    public void ClearPageEdgeHold() => _pageEdgeHold.Reset();

    public void HandleArrowRight(bool shortJump = false)
    {
        if (AutoScrollActive && ActiveDocument is { } d && d.Rail.Active && d.Rail.AutoScrolling)
        {
            d.Rail.SetAutoScrollBoost(true);
            return;
        }
        if (TryJump(forward: true, half: shortJump)) return;
        HandleHorizontalArrow(ScrollDirection.Forward, -CoreTuning.PanStep);
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
        if (AutoScrollActive) StopAutoScroll();
        if (TryJump(forward: false, half: shortJump)) return;
        HandleHorizontalArrow(ScrollDirection.Backward, CoreTuning.PanStep);
    }

    private bool TryJump(bool forward, bool half = false)
    {
        if (!JumpMode || ActiveDocument is not { } doc || !doc.Rail.Active) return false;
        var (ww, wh) = GetViewportSize();
        doc.Rail.Jump(forward, doc.Camera.Zoom, ww, wh, doc.Camera.OffsetX, doc.Camera.OffsetY, half);
        return true;
    }

    private void HandleHorizontalArrow(ScrollDirection direction, double panDelta)
    {
        if (ActiveDocument is not { } doc) return;
        if (doc.Rail.Active)
            doc.Rail.StartScroll(direction, doc.Camera.OffsetX);
        else
        {
            var (ww, wh) = GetViewportSize();
            doc.Camera.OffsetX += panDelta;
            doc.ClampCamera(ww, wh);
        }
    }

    public void HandleLineHome() => SnapToLineEdge(start: true);
    public void HandleLineEnd() => SnapToLineEdge(start: false);

    private void SnapToLineEdge(bool start)
    {
        if (ActiveDocument is not { } doc || !doc.Rail.Active) return;
        var (ww, _) = GetViewportSize();
        var x = start
            ? doc.Rail.ComputeLineStartX(doc.Camera.Zoom, ww)
            : doc.Rail.ComputeLineEndX(doc.Camera.Zoom, ww);
        if (x is { } val)
        {
            doc.Camera.OffsetX = val;
            // During autoscroll: brief settle pause, then resume from new position
            if (AutoScrollActive)
                doc.Rail.PauseAutoScroll(_config.AutoScrollLinePauseMs);
        }
    }

    public void HandleArrowRelease(bool isHorizontal)
    {
        if (isHorizontal)
        {
            ActiveDocument?.Rail.StopScrollAndEdgeHold();
            if (AutoScrollActive)
                ActiveDocument?.Rail.SetAutoScrollBoost(false);
        }
    }

    /// <summary>
    /// Handles a click on the viewport. Returns link destination if a link was clicked,
    /// otherwise falls through to rail-mode block snapping.
    /// </summary>
    public (bool Handled, PdfLinkDestination? Link) HandleClick(double canvasX, double canvasY)
    {
        if (ActiveDocument is not { } doc) return (false, null);

        double pageX = (canvasX - doc.Camera.OffsetX) / doc.Camera.Zoom;
        double pageY = (canvasY - doc.Camera.OffsetY) / doc.Camera.Zoom;

        // Check for PDF links first (takes priority over rail-mode snap)
        var link = doc.HitTestLink(pageX, pageY);
        if (link is not null)
        {
            if (link.Destination is PageDestination pageDest)
            {
                PushHistory();
                GoToPage(pageDest.PageIndex);
                ScrollToDestination(pageDest);
                return (true, link.Destination);
            }
            return (true, link.Destination);
        }

        // Fall through to rail-mode block snapping
        if (!doc.Rail.Active || !doc.Rail.HasAnalysis) return (false, null);

        doc.Rail.FindBlockNearPoint(pageX, pageY);
        var (ww2, wh2) = GetViewportSize();
        doc.StartSnap(ww2, wh2);
        return (true, null);
    }

    /// <summary>
    /// Hit-tests a point (in page-point space) against PDF links on the active document.
    /// </summary>
    public PdfLink? HitTestLink(double pageX, double pageY)
        => ActiveDocument?.HitTestLink(pageX, pageY);
}
