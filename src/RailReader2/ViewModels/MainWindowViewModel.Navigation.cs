using CommunityToolkit.Mvvm.Input;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader2.ViewModels;

// Navigation, camera, rail, auto-scroll, line focus, colour effects
public sealed partial class MainWindowViewModel
{
    // --- Navigation ---

    public void NavigateToBookmark(int index)
    {
        _controller.NavigateToBookmark(index);
        InvalidateAfterNavigation();
    }

    public void NavigateBack()
    {
        _controller.NavigateBack();
        InvalidateAfterNavigation();
    }

    public void NavigateForward()
    {
        _controller.NavigateForward();
        InvalidateAfterNavigation();
    }

    [RelayCommand]
    public void GoToPage(int page)
        => Dispatch(() => _controller.GoToPage(page), InvalidateAfterNavigation);

    [RelayCommand]
    public void FitPage()
        => Dispatch(_controller.FitPage, InvalidateCameraAndTab);

    [RelayCommand]
    public void FitWidth()
        => Dispatch(_controller.FitWidth, InvalidateCameraAndTab);

    // --- Camera ---

    public void HandleZoom(double scrollDelta, double cursorX, double cursorY, bool ctrlHeld)
        => Dispatch(() => _controller.HandleZoom(scrollDelta, cursorX, cursorY, ctrlHeld), InvalidateCameraAndTab, animate: true);

    public void HandlePan(double dx, double dy, bool ctrlHeld = false)
        => Dispatch(() => _controller.HandlePan(dx, dy, ctrlHeld), InvalidateCameraAndTab);

    public void HandleZoomKey(bool zoomIn)
        => Dispatch(() => _controller.HandleZoomKey(zoomIn), InvalidateCameraAndTab, animate: true);

    public void HandleResetZoom() => FitPage();

    // --- Rail navigation ---

    public void HandleArrowDown()
        => Dispatch(_controller.HandleArrowDown, InvalidateNavigation);

    public void HandleArrowUp()
        => Dispatch(_controller.HandleArrowUp, InvalidateNavigation);

    public void HandleArrowRight(bool shortJump = false)
    {
        _controller.HandleArrowRight(shortJump);
        // In rail mode, the animation loop drives all camera updates via Tick().
        // Key repeats only keep StartScroll alive (a no-op when direction unchanged).
        // Calling InvalidateCamera here would redundantly update the MatrixTransform
        // at key-repeat rate (~30-40 Hz) on top of the animation frame updates.
        if (ActiveTab?.Rail.Active != true)
            InvalidateCamera();
        RequestAnimationFrame();
    }

    public void HandleArrowLeft(bool shortJump = false)
    {
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

    public void HandleClick(double canvasX, double canvasY)
    {
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
        if (_controller.ActiveDocument is { } doc)
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
        if (_controller.ActiveDocument is not { } doc) return;
        if (doc.MarginCropping == enabled) return;

        var (ww, wh) = _controller.GetViewportSize();
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
        if (_controller.ActiveDocument is { } doc)
            ApplyMarginCropping(!doc.MarginCropping);
    }

    public void ToggleLineHighlight()
    {
        if (_controller.ActiveDocument is { } doc)
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
