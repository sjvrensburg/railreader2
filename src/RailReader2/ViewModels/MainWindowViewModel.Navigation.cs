using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;

using RailReader2.Services;

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

    /// <summary>Back/Forward availability on the focused view — drives the status-bar buttons, the
    /// Navigation menu items and the Bookmarks pane's Back button. Re-published by
    /// <see cref="RefreshHistoryAvailability"/> only when a value changes.</summary>
    public bool CanGoBack => _controller.CanGoBack;
    public bool CanGoForward => _controller.CanGoForward;
    private bool _gateCanGoBack, _gateCanGoForward;

    /// <summary>Re-publishes <see cref="CanGoBack"/>/<see cref="CanGoForward"/> if either changed. Called
    /// from the focused view's <c>Viewport.HistoryChanged</c> and on every <see cref="ActiveTab"/> raise,
    /// which covers a focus switch to another view (whose stacks differ) without a subscription.</summary>
    private void RefreshHistoryAvailability()
    {
        if (CanGoBack is var back && back != _gateCanGoBack)
        { _gateCanGoBack = back; OnPropertyChanged(nameof(CanGoBack)); }
        if (CanGoForward is var fwd && fwd != _gateCanGoForward)
        { _gateCanGoForward = fwd; OnPropertyChanged(nameof(CanGoForward)); }
    }

    /// <summary>Jumps to <paramref name="page"/> and records it in Back/Forward history (outline,
    /// index, go-to-page, first/last page). For previous/next-page stepping use <see cref="StepPage"/>.</summary>
    [RelayCommand]
    public void GoToPage(int page)
        => Dispatch(() => _controller.GoToPage(page), InvalidateAfterNavigation);

    /// <summary>Moves the focused view <paramref name="delta"/> pages (PgUp/PgDn, the previous/next
    /// buttons). Incremental, so it is not recorded in Back/Forward history.</summary>
    public void StepPage(int delta)
    {
        if (_controller.FocusedViewport is not { } vp) return;
        Dispatch(() => _controller.GoToPage(vp.CurrentPage + delta, recordHistory: false),
            InvalidateAfterNavigation);
    }

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
    public bool SmoothlyFrameBlock(int pageBlockIndex, double? zoom = null, double? durationMs = null, int line = 0,
        double? focusPageX = null)
    {
        // Framing eases the zoom, which would slide the frozen panes out of alignment with the body —
        // gate it like every other zoom entry point. Reachable while frozen via double-click block-framing
        // and the portal "Go to source" action.
        if (ZoomBlockedByFreeze()) return false;
        bool ok = false;
        Dispatch(() => ok = _controller.SmoothlyFrameBlock(pageBlockIndex, zoom, durationMs, line, focusPageX),
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
        // The opening position, not a jump away from somewhere: kept out of history so Back right after
        // opening doesn't go to page 1.
        if (page1Based is { } p)
            Dispatch(() => _controller.GoToPage(Math.Clamp(p - 1, 0, Math.Max(0, tab.PageCount - 1)), recordHistory: false),
                InvalidateAfterNavigation);
        if (zoomPercent is { } z)
            SetZoomPercent(z);
        if (!rail) return;

        StartBackgroundAnalysis();
        // Rail needs the current page analysed; poll briefly, then force rail if the tick hasn't already
        // engaged it (a >threshold zoom would have). Capped so a missing ONNX model can't poll forever.
        // Stored in a field (not a bare local) so Dispose() can stop it — otherwise a window closed within
        // the ~12s poll window would keep this ticking against a disposed controller.
        int attempts = 0;
        _startupRailTimer?.Stop();
        // 250ms * 48 attempts = same ~12s window as the old 150ms * 80 (DispatcherTimer overhead is
        // proportionally worse at faster intervals on X11 — see #224 — and this timer, though
        // short-lived, needn't poll any faster than that).
        _startupRailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _startupRailTimer.Tick += (_, _) =>
        {
            attempts++;
            if (ActiveTab is not { } t || attempts > 48) { _startupRailTimer?.Stop(); return; }
            if (!t.AnalysisCache.ContainsKey(t.State.CurrentPage)) return;
            _startupRailTimer?.Stop();
            if (!t.Rail.Active && !ForcedRailActive) StartRailHere();
        };
        _startupRailTimer.Start();
    }

    private DispatcherTimer? _startupRailTimer;

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

    private readonly LinkPreferences _linkPrefs = LinkPreferences.Load();

    public LinkPreferences LinkPrefs => _linkPrefs;

    public void OnLinkPrefsChanged() => _linkPrefs.Save();

    /// <summary>A click on the focused view. <paramref name="ctrl"/> is the modifier state, which decides
    /// (per <see cref="LinkPreferences"/>) whether a link under the click is followed, previewed
    /// (<see cref="OpenLinkPeek"/>), or ignored; when it isn't followed, the click still seats the rail.</summary>
    public void HandleClick(double canvasX, double canvasY, bool ctrl = false)
    {
        if (IsScanAllActive) return;
        var vp = _controller.FocusedViewport;
        int pageBefore = vp?.CurrentPage ?? -1, backBefore = vp?.BackStackCount ?? 0;
        bool follows = _linkPrefs.Follows(ctrl);
        // Peek: hit-test the link BEFORE Core handles the click, which may re-anchor a continuous view or
        // snap the rail and so move what the click point resolves to.
        PdfLinkDestination? peekLink = null;
        if (vp is not null && _linkPrefs.Peeks(ctrl))
        {
            var (page, pageX, pageY) = vp.ResolvePoint(canvasX, canvasY);
            peekLink = vp.Owner.HitTestLink(page, pageX, pageY)?.Destination;
        }
        // Not following: Core seats the rail as if no link were there.
        var (handled, link) = _controller.HandleClick(canvasX, canvasY, followLinks: follows);
        if (peekLink is not null && vp is not null)
        {
            link = peekLink;
            if (link is PageDestination peekDest)
            {
                OpenLinkPeek = BuildLinkPeek(vp, peekDest);
                if (OpenLinkPeek is null)
                    ShowStatusToast($"Couldn't preview this link to page {peekDest.PageIndex + 1} \u2014 Ctrl+click to follow it");
            }
        }
        if (link is UriDestination uriDest)
        {
            _ = PromptAndOpenUrl(uriDest.Uri);
            return;
        }
        if (follows && link is PageDestination && vp is not null)
            ToastIfLinkFollowed(vp, pageBefore, backBefore);
        if (handled)
            InvalidateNavigation();
    }

    /// <summary>An internal link moved the view: links are easy to hit by accident, so say where it went
    /// and offer the way back. (Not when Core declined to follow it — a confined view or a render failure.)</summary>
    private void ToastIfLinkFollowed(Viewport vp, int pageBefore, int backBefore)
    {
        if (_controller.CanGoBack && (vp.CurrentPage != pageBefore || vp.BackStackCount != backBefore))
            ShowStatusToast($"Followed link to page {vp.CurrentPage + 1}", "Back (Alt+\u2190)", NavigateBack);
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
        var result = await dialog.ShowDialog<bool?>(DialogOwner!);
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

    // The link the pointer was last over (by its rect), so the "how to follow" hint toasts once per
    // entry onto a link rather than on every hover re-test.
    private RectF? _hoverLinkRect;

    /// <summary>True when a click here with the given modifier state would follow or preview a link —
    /// drives the hand cursor, so it only shows where a click actually does something. When the pointer
    /// moves onto a link a plain click does nothing with (Ctrl-only or off), toasts where it goes and how
    /// to follow it, so the setting doesn't make links look broken.</summary>
    public bool IsOverLink(double pageX, double pageY, bool ctrl)
    {
        var link = _controller.HitTestLink(pageX, pageY);
        bool entered = link is not null && link.Rect != _hoverLinkRect;
        _hoverLinkRect = link?.Rect;
        if (link is null) return false;
        if (entered && !_linkPrefs.Follows(ctrl: false) && !_linkPrefs.Peeks(ctrl: false))
            ShowLinkHint(link.Destination);
        return _linkPrefs.Follows(ctrl) || _linkPrefs.Peeks(ctrl);
    }

    private void ShowLinkHint(PdfLinkDestination dest)
    {
        string target = dest switch
        {
            PageDestination p => $"Link to page {p.PageIndex + 1}",
            UriDestination u => $"Link to {u.Uri}",
            _ => "Link",
        };
        if (_linkPrefs.LinkClick == LinkClickAction.CtrlClick)
            ShowStatusToast($"{target} \u2014 Ctrl+click to follow");
        else
            ShowStatusToast($"{target} \u2014 following links is off", "Settings", () => OpenSettingsTab("Reading"));
    }

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

    /// <summary>Saves a display toggle made from a key/menu into the config and hands Core the new
    /// settings snapshot, so the next document opened this session starts with it too (Core builds new
    /// documents from its own copy). The light variant: a toggle changes no analysis-derived state, and
    /// the full <see cref="OnConfigChanged()"/> would also re-invalidate every layer.</summary>
    private void PersistDisplayDefault()
    {
        _controller.OnSliderChanged(AppConfig.ToCoreSettings());
        AppConfig.Save();
    }

    public void ToggleLineFocusBlur()
    {
        if (_controller.FocusedViewport?.Owner is { } doc)
        {
            doc.LineFocusBlur = !doc.LineFocusBlur;
            // The toggle is also the default for new documents and the next session (as the colour
            // effect is) — AppConfig is what Settings edits and what is loaded at startup.
            AppConfig.LineFocusBlur = doc.LineFocusBlur;
            PersistDisplayDefault();
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
    /// changes — the effect becomes visible on the next fit or page flip — unless
    /// <paramref name="refitIfZoomed"/> (the keyboard shortcut) asks to re-fit regardless.
    /// </summary>
    public void ApplyMarginCropping(bool enabled, bool refitIfZoomed = false)
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

        if (atFitWidth || refitIfZoomed)
        {
            Dispatch(() =>
            {
                doc.FitWidthPreservingTop(ww, wh);
                doc.UpdateRailZoom(ww, wh);
            }, InvalidateCameraAndTab);
        }
    }

    /// <summary>
    /// Ctrl+Shift+M. At fit-width (or with cropping off) it toggles cropping and re-fits to the new content
    /// width. But when cropping is already on and the reader has zoomed in past fit-width the press is
    /// "take me back to the margin-fit level":
    /// cropping is switched on if it was off and the view re-fits to the content width (keeping the
    /// page point at the viewport centre), instead of silently flipping a flag whose effect is
    /// invisible until the next page turn. Press again, now at fit-width, to toggle cropping off.
    /// </summary>
    public void ToggleMarginCropping()
    {
        // Margin cropping is a document-level pref. Toggle it on the focused viewport's document; the
        // re-fit inside ApplyMarginCropping sizes off the main view (not a confined portal), so this stays
        // reachable even while the live portal viewport is focused — they share the same document model.
        if (_controller.FocusedViewport?.Owner is not { } doc) return;

        var refVp = _controller.FocusedViewport is { Focus: null } fv ? fv : ActiveTab?.Viewport;
        var (ww, wh) = refVp is { } r ? (r.Width, r.Height) : FocusedViewportSize();
        bool zoomedPastFit = false;
        if (doc.MarginCropping)
        {
            var (_, _, rw, _) = doc.GetFitRect();
            zoomedPastFit = rw > 0 && doc.Camera.Zoom > ww / rw * AtFitWidthHysteresis;
        }

        if (zoomedPastFit)
        {
            Dispatch(() =>
            {
                doc.FitWidthPreservingTop(ww, wh);
                doc.UpdateRailZoom(ww, wh);
            }, InvalidateCameraAndTab);
            return;
        }

        bool enable = !doc.MarginCropping;
        AppConfig.MarginCropping = enable;
        PersistDisplayDefault();
        ApplyMarginCropping(enable, refitIfZoomed: true);
    }

    public void ToggleLineHighlight()
    {
        if (_controller.FocusedViewport?.Owner is { } doc)
        {
            doc.LineHighlightEnabled = !doc.LineHighlightEnabled;
            AppConfig.LineHighlightEnabled = doc.LineHighlightEnabled;
            PersistDisplayDefault();
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
