using RailReader.Core.Commands;
using RailReader.Core.Models;

namespace RailReader2.ViewModels;

// External-control surface: thin wrappers over the Core motion primitives plus the
// state queries consumed by IRailReaderControl / DBusControlServer. Every wrapper routes
// through Dispatch(..., animate: true) so the eased motion is driven by RequestAnimationFrame
// — the same path real user input takes (mirrors HandleZoomKey).
public sealed partial class MainWindowViewModel
{
    /// <summary>Raised when the active document's current page changes (1-based). Multicast
    /// companion to Core's settable <c>PageChanged</c> callback, so the control surface can
    /// observe page crosses without clobbering the accessibility hook.</summary>
    public event Action<int>? PageChangedNotification;

    /// <summary>Smoothly frame a block by index on the current page using rail's exact framing
    /// and the app-native eased zoom. Returns true if the block could be framed.</summary>
    public bool SmoothlyFrameBlock(int pageBlockIndex, double? zoom = null)
    {
        bool ok = false;
        Dispatch(() => ok = _controller.SmoothlyFrameBlock(pageBlockIndex, zoom),
            InvalidateCameraAndTab, animate: true);
        return ok;
    }

    /// <summary>Smoothly frame the n-th block of a semantic role. Returns true if a matching
    /// block was found and framed.</summary>
    public bool SmoothlyFrameRole(BlockRole role, int occurrence = 0, double? zoom = null)
    {
        bool ok = false;
        Dispatch(() => ok = _controller.SmoothlyFrameRole(role, occurrence, zoom),
            InvalidateCameraAndTab, animate: true);
        return ok;
    }

    /// <summary>Animate the camera to an absolute zoom + offset via the app-native easing.</summary>
    public void AnimateCameraTo(double targetZoom, double targetOffsetX, double targetOffsetY)
        => Dispatch(() => _controller.AnimateCameraTo(targetZoom, targetOffsetX, targetOffsetY),
            InvalidateCameraAndTab, animate: true);

    // --- State queries for the control surface ---

    /// <summary>Aggregate document state (path, page count, current page, zoom, …) or null
    /// when no document is open.</summary>
    public DocumentInfo? GetDocumentInfo() => _controller.ActiveDocument is null ? null : _controller.GetDocumentInfo();

    /// <summary>True while an eased camera animation is in flight.</summary>
    public bool IsAnimating => _controller.IsAnimating;
}
