using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader2.ViewModels;

// Navigation, camera, rail, auto-scroll, line focus, colour effects
public sealed partial class MainWindowViewModel
{
    // --- Navigation ---

    /// <summary>Jump the rail to the next/previous block of a given semantic role (heading, figure,
    /// table, equation). Available while rail-reading; surfaces as Navigation-menu items + shortcuts and
    /// is the AT-SPI-actionable form of "go to next figure" for assistive tech and automation.</summary>
    public void JumpToRole(BlockRole role, bool forward = true)
    {
        if (IsScanAllActive) return;
        if (ActiveTab?.Rail.Active != true)
        {
            ShowStatusToast("Jump by section is available while rail-reading (zoom in)");
            return;
        }
        if (_controller.NavigateToRole(role, forward))
            InvalidateNavigation();
        else
            ShowStatusToast($"No {(forward ? "next" : "previous")} {RoleDisplayName(role)}");
    }

    private static string RoleDisplayName(BlockRole role) => role switch
    {
        BlockRole.Heading => "heading",
        BlockRole.Figure => "figure",
        BlockRole.Table => "table",
        BlockRole.DisplayMath => "equation",
        _ => "section",
    };

    public void NavigateToBookmark(int index)
    {
        if (IsScanAllActive) return;
        _controller.NavigateToBookmark(index);
        InvalidateAfterNavigation();
    }

    public void NavigateBack()
    {
        if (IsScanAllActive) return;
        _controller.NavigateBack();
        InvalidateAfterNavigation();
    }

    public void NavigateForward()
    {
        if (IsScanAllActive) return;
        _controller.NavigateForward();
        InvalidateAfterNavigation();
    }

    [RelayCommand]
    public void GoToPage(int page)
        => Dispatch(() => _controller.GoToPage(page), InvalidateAfterNavigation);

    [RelayCommand]
    public void FitPage()
    {
        if (ZoomBlockedByFreeze()) return;
        Dispatch(_controller.FitPage, InvalidateCameraAndTab);
    }

    [RelayCommand]
    public void FitWidth()
    {
        if (ZoomBlockedByFreeze()) return;
        Dispatch(_controller.FitWidth, InvalidateCameraAndTab);
    }

    // --- Camera ---

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
    {
        if (ZoomBlockedByFreeze()) return;
        Dispatch(() => _controller.HandleZoom(scrollDelta, cursorX, cursorY, ctrlHeld), InvalidateCameraAndTab, animate: true);
    }

    public void HandlePan(double dx, double dy, bool ctrlHeld = false)
        => Dispatch(() => _controller.HandlePan(dx, dy, ctrlHeld), () => { ClampFrozenCameraAfterPan(); InvalidateCameraAndTab(); });

    public void HandleZoomKey(bool zoomIn)
    {
        if (ZoomBlockedByFreeze()) return;
        Dispatch(() => _controller.HandleZoomKey(zoomIn), InvalidateCameraAndTab, animate: true);
    }

    public void HandleResetZoom() => FitPage();

    // Mirrors the per-notch scale inside DocumentController.HandleZoom
    // (newZoom = base * (1 + scrollDelta * 0.003)). Kept in sync manually because Core exposes no
    // absolute zoom entry point; if Core changes the scale, update this too.
    private const double ZoomDeltaScale = 0.003;

    /// <summary>Zoom to an absolute percentage, anchored at the viewport centre (used by the status-bar
    /// zoom editor). Core has only a relative <see cref="HandleZoom"/>, so we invert its formula to
    /// derive the scroll delta that lands on the requested zoom; the target is clamped to Core's
    /// 50–2000% range.</summary>
    public void SetZoomPercent(double percent)
    {
        if (ZoomBlockedByFreeze()) return;
        if (ActiveTab is not { } tab) return;
        double current = tab.Camera.Zoom;
        if (current <= 0) return;
        double target = Math.Clamp(percent / 100.0, 0.5, 20.0); // mirrors Core's HandleZoom clamp
        double delta = ((target / current) - 1.0) / ZoomDeltaScale;
        var (ww, wh) = FocusedViewportSize();
        Dispatch(() => _controller.HandleZoom(delta, ww / 2.0, wh / 2.0, ctrlHeld: false),
            InvalidateCameraAndTab, animate: true);
    }

    /// <summary>Smoothly frame a block by index on the current page using rail's exact framing
    /// and the app-native eased zoom (the double-click-to-zoom-into-rail gesture; also used by the
    /// VLM "frame this block" path). <paramref name="line"/> seats the rail on that line as part of the
    /// framing (clamped to the block's line range; 0 = first line). Returns true if the block could be
    /// framed. Routes through Dispatch(..., animate: true) so the eased motion is driven by
    /// RequestAnimationFrame — the same path real user input takes.</summary>
    public bool SmoothlyFrameBlock(int pageBlockIndex, double? zoom = null, double? durationMs = null, int line = 0)
    {
        // Framing eases the zoom, which would slide the frozen panes out of alignment with the body —
        // gate it like every other zoom entry point. Reachable while frozen via double-click block-framing
        // and the portal "Go to source" action.
        if (ZoomBlockedByFreeze()) return false;
        bool ok = false;
        Dispatch(() => ok = _controller.SmoothlyFrameBlock(pageBlockIndex, zoom, durationMs, line),
            InvalidateCameraAndTab, animate: true);
        return ok;
    }

    // --- Rail navigation ---

    public void HandleArrowDown()
        => Dispatch(_controller.HandleArrowDown, InvalidateNavigation);

    public void HandleArrowUp()
        => Dispatch(_controller.HandleArrowUp, InvalidateNavigation);

    public void HandleArrowRight(bool shortJump = false)
    {
        if (IsScanAllActive) return;
        _controller.HandleArrowRight(shortJump);
        if (ActiveTab?.Rail.Active != true)
            InvalidateCamera();
        RequestAnimationFrame();
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
        if (IsScanAllActive) return;
        _controller.HandleArrowLeft(shortJump);
        if (ActiveTab?.Rail.Active != true)
            InvalidateCamera();
        RequestAnimationFrame();
    }

    public void HandleLineHome()
        => Dispatch(_controller.HandleLineHome, InvalidateCameraAndTab);

    public void HandleLineEnd()
        => Dispatch(_controller.HandleLineEnd, InvalidateCameraAndTab);

    public void HandleArrowRelease(bool isHorizontal)
        => Dispatch(() => _controller.HandleArrowRelease(isHorizontal), animate: true);

    /// <summary>Toggle the "start rail here" armed state (the toolbar button). When on, the next
    /// viewport click force-activates rail mode at that point at the current zoom. Arming disarms any
    /// pending freeze placement — only one viewport-click gesture may be armed at a time.</summary>
    public void ToggleArmActivateRailClick()
    {
        ArmActivateRailClick = !ArmActivateRailClick;
        if (ArmActivateRailClick)
            FreezeArmMode = FreezeMode.None;
    }

    /// <summary>Click-free "start rail here" for the keyboard shortcut, Rail menu, screen readers, and
    /// automation. The toolbar gesture arms a pointer click to pick the point; this resolves the point
    /// itself — the viewport centre — so no pointer is needed (the accessibility/agentic entry point).
    /// Toggles: if a forced activation is already in effect it is released (mirrors the Escape exit),
    /// otherwise rail force-activates on the block nearest the viewport centre at the current zoom.</summary>
    public void StartRailHere()
    {
        if (ActiveTab is null) return;
        if (ForcedRailActive) { ExitForcedRail(); return; }
        var (ww, wh) = FocusedViewportSize();
        ActivateRailAtClick(ww / 2.0, wh / 2.0);
    }

    /// <summary>Apply a requested initial view from launch flags (<c>--page</c>/<c>--zoom</c>/<c>--rail</c>),
    /// after the startup document has opened. Page is 1-based; zoom is a percentage (e.g. 300 = 300%).
    /// Rail engages once the page is analysed: a high enough zoom seats it via the tick, otherwise this
    /// forces it at the viewport centre (a short poll waits for analysis, then gives up — no model = no
    /// rail).</summary>
    public void ApplyStartupView(int? page1Based, double? zoomPercent, bool rail)
    {
        if (ActiveTab is not { } tab) return;
        if (page1Based is { } p)
            GoToPage(Math.Clamp(p - 1, 0, Math.Max(0, tab.PageCount - 1)));
        if (zoomPercent is { } z)
            SetZoomPercent(z);
        if (!rail) return;

        StartBackgroundAnalysis();
        // Rail needs the current page analysed; poll briefly, then force rail if the tick hasn't already
        // engaged it (a >threshold zoom would have). Capped so a missing ONNX model can't poll forever.
        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (ActiveTab is not { } t || attempts > 80) { timer.Stop(); return; }
            if (!t.AnalysisCache.ContainsKey(t.State.CurrentPage)) return;
            timer.Stop();
            if (!t.Rail.Active && !ForcedRailActive) StartRailHere();
        };
        timer.Start();
    }

    /// <summary>True while rail is held active below the zoom threshold by a forced "start rail here"
    /// activation. Used to drive an Escape that releases it.</summary>
    public bool ForcedRailActive => _controller.ForcedRailActive;

    /// <summary>Release a forced ("start rail here") rail activation, deactivating rail if the current
    /// zoom is below the threshold.</summary>
    public void ExitForcedRail()
    {
        _controller.ExitForcedRail();
        InvalidateNavigation();
    }

    /// <summary>Consume an armed "start rail here" click: force rail mode active at the clicked point
    /// (canvas/screen coords — the controller maps them to the page) regardless of zoom, seating the
    /// nearest navigable block and the line under the click — no magnification. Disarms afterwards.
    /// Toasts when the page has no analysis yet.</summary>
    public void ActivateRailAtClick(double canvasX, double canvasY)
    {
        ArmActivateRailClick = false;
        if (IsScanAllActive) return;
        bool ok = false;
        Dispatch(() => ok = _controller.ActivateRailAt(canvasX, canvasY), InvalidateCameraAndTab, animate: true);
        if (!ok)
            ShowStatusToast("Rail-reading needs layout analysis here — try again in a moment");
    }

    public void HandleClick(double canvasX, double canvasY)
    {
        if (IsScanAllActive) return;
        var (handled, link) = _controller.HandleClick(canvasX, canvasY);
        if (link is UriDestination uriDest)
        {
            _ = PromptAndOpenUrl(uriDest.Uri);
            return;
        }
        if (handled)
            InvalidateNavigation();
    }

    private async Task PromptAndOpenUrl(string uri)
    {
        if (_window is null) return;

        // Only allow http/https URLs
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusToast("Blocked non-HTTP link");
            return;
        }

        var dialog = new Views.ConfirmUrlDialog(uri);
        var result = await dialog.ShowDialog<bool?>(_window);
        if (result == true)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open URL: {uri}", ex);
                ShowStatusToast("Failed to open link");
            }
        }
    }

    public bool IsOverLink(double pageX, double pageY)
        => _controller.HitTestLink(pageX, pageY) is not null;

    // --- Auto-scroll ---

    public void ToggleAutoScroll()
    {
        _controller.ToggleAutoScroll();
        OnPropertyChanged(nameof(AutoScrollActive));
        RequestAnimationFrame();
    }

    public void StopAutoScroll()
    {
        _controller.StopAutoScroll();
        OnPropertyChanged(nameof(AutoScrollActive));
    }

    /// <summary>Resume semi-auto scroll from a park (the reader's explicit advance keypress).
    /// Routed here from the forward/advance keys while <see cref="AutoScrollParked"/> is set.
    /// Restarting the animation frame lets Core un-park and flow on; the parked-state edge is
    /// picked up by the poll loop, which clears the affordance.</summary>
    public void ResumeAutoScrollFromPark()
    {
        _controller.ResumeAutoScrollFromPark();
        OnPropertyChanged(nameof(AutoScrollParked));
        RequestAnimationFrame();
    }

    public void ToggleAutoScrollExclusive()
    {
        if (ActiveTab?.PendingRailSetup == true)
        {
            ShowStatusToast("Layout analysis in progress\u2026");
            return;
        }
        _controller.ToggleAutoScrollExclusive();
        JumpMode = _controller.JumpMode;
        OnPropertyChanged(nameof(AutoScrollActive));
        RequestAnimationFrame();
    }

    public void ToggleJumpModeExclusive()
    {
        if (ActiveTab?.PendingRailSetup == true)
        {
            ShowStatusToast("Layout analysis in progress\u2026");
            return;
        }
        _controller.ToggleJumpModeExclusive();
        JumpMode = _controller.JumpMode;
        OnPropertyChanged(nameof(AutoScrollActive));
    }

    public void ToggleLineFocusBlur()
    {
        if (_controller.FocusedViewport?.Owner is { } doc)
        {
            doc.LineFocusBlur = !doc.LineFocusBlur;
            InvalidatePage();
            InvalidateOverlay();
        }
    }

    // Slack above fit-width below which the user counts as "at fit-width" for
    // margin-cropping toggle purposes. Above this they're considered zoomed in.
    private const double AtFitWidthHysteresis = 1.1;

    /// <summary>
    /// Applies a margin-cropping setting to the active document. If the user
    /// was approximately at fit-width, re-fits to the new content width while
    /// preserving the page-space point at viewport center (no jump to top).
    /// If the user has deliberately zoomed in past fit-width, only the flag
    /// changes — the effect becomes visible on the next fit or page flip.
    /// </summary>
    public void ApplyMarginCropping(bool enabled)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;
        if (doc.MarginCropping == enabled) return;

        // Size the re-fit off the document's MAIN view, never a confined portal viewport: the fit targets
        // doc.Primary and a confined portal ignores cropping, so the portal's (smaller) dimensions would
        // mis-fit the main view. When the focused view is confined, fall back to the active tab's viewport.
        var refVp = _controller.FocusedViewport is { Focus: null } fv ? fv : ActiveTab?.Viewport;
        var (ww, wh) = refVp is { } r ? (r.Width, r.Height) : FocusedViewportSize();
        var (_, _, preRw, _) = doc.GetFitRect();
        double preFitZoom = preRw > 0 ? ww / preRw : doc.Camera.Zoom;
        bool atFitWidth = doc.Camera.Zoom <= preFitZoom * AtFitWidthHysteresis;

        doc.MarginCropping = enabled;

        if (atFitWidth)
        {
            Dispatch(() =>
            {
                doc.FitWidthPreservingTop(ww, wh);
                doc.UpdateRailZoom(ww, wh);
            }, InvalidateCameraAndTab);
        }
    }

    public void ToggleMarginCropping()
    {
        // Margin cropping is a document-level pref. Toggle it on the focused viewport's document; the
        // re-fit inside ApplyMarginCropping sizes off the main view (not a confined portal), so this stays
        // reachable even while the live portal viewport is focused — they share the same document model.
        if (_controller.FocusedViewport?.Owner is { } doc)
            ApplyMarginCropping(!doc.MarginCropping);
    }

    public void ToggleLineHighlight()
    {
        if (_controller.FocusedViewport?.Owner is { } doc)
        {
            doc.LineHighlightEnabled = !doc.LineHighlightEnabled;
            InvalidateOverlay();
        }
    }

    // --- Colour effects ---

    [RelayCommand]
    public void SetColourEffect(ColourEffect effect)
    {
        _controller.SetColourEffect(effect);
        InvalidatePage();
        InvalidateOverlay();
    }

    public ColourEffect CycleColourEffect()
    {
        var effect = _controller.CycleColourEffect();
        InvalidatePage();
        InvalidateOverlay();
        return effect;
    }
}
