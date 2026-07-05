using RailReader.Core;
using RailReader.Core.Models;
using RailReader2.Services;

namespace RailReader2.ViewModels;

// View rotation (Core 0.47.0): quarter-turn rotation for sideways scans/tables,
// rotate-to-read for sideways blocks, per-document persistence, annotation lockout.
public sealed partial class MainWindowViewModel
{
    /// <summary>True while the focused document's view is rotated. Annotation authoring is refused by
    /// Core while rotated (stored geometry is the rotation-0 frame), and the shell additionally hides
    /// annotation display + disables the tools — see <see cref="SetAnnotationTool"/> and
    /// <see cref="Views.DocumentView"/>'s annotation state build.</summary>
    public bool IsViewRotated => (_controller.FocusedViewport?.Owner.ViewRotation ?? 0) != 0;

    /// <summary>The focused document's view rotation in clockwise degrees (0/90/180/270), for display.</summary>
    public int ViewRotationDegrees => (_controller.FocusedViewport?.Owner.ViewRotation ?? 0) * 90;

    public void RotateViewClockwise()
    {
        if (_controller.FocusedViewport?.Owner is { } doc)
            SetViewRotation(doc.ViewRotation + 1);
    }

    public void RotateViewCounterClockwise()
    {
        if (_controller.FocusedViewport?.Owner is { } doc)
            SetViewRotation(doc.ViewRotation - 1);
    }

    public void ResetViewRotation() => SetViewRotation(0);

    /// <summary>
    /// Rotate-to-read: when the current rail block is sideways
    /// (<see cref="RailReader.Core.DocumentController.CurrentBlockUprightTurns"/>), rotates the whole
    /// view so it reads upright — analysis re-runs in the new frame, giving the block real per-line
    /// detection. Pressing again once the block is upright (or with no sideways block) resets a
    /// rotated view back to 0, so the key toggles in and out.
    /// </summary>
    public void RotateToReadBlock()
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;

        if (_controller.CurrentBlockUprightTurns != 0)
        {
            PrepareForRotation();
            if (_controller.RotateViewToReadBlock())
                OnViewRotationApplied(doc, "Rotated to read — press U to reset");
        }
        else if (doc.ViewRotation != 0)
        {
            SetViewRotation(0);
        }
        else
        {
            ShowStatusToast("No sideways block at the reading position");
        }
    }

    /// <summary>Sets the focused document's view rotation (absolute clockwise quarter-turns; Core
    /// normalises). Central chokepoint: exits annotation mode / freeze first, persists, invalidates.</summary>
    public void SetViewRotation(int quarterTurns)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;
        if (ViewRotationMath.Normalize(quarterTurns) == doc.ViewRotation) return;

        PrepareForRotation();
        _controller.SetViewRotation(quarterTurns);
        OnViewRotationApplied(doc,
            doc.ViewRotation == 0 ? "Rotation reset" : $"View rotated {doc.ViewRotation * 90}°");
    }

    /// <summary>Rotation invalidates every displayed frame the current gestures assume: leave
    /// annotation mode (authoring is refused while rotated, and display geometry is the old frame)
    /// and release any freeze (its crops + split are old-frame screen geometry).</summary>
    private void PrepareForRotation()
    {
        if (IsAnnotationMode) IsAnnotationMode = false;
        SelectedAnnotation = null;
        OnPropertyChanged(nameof(SelectedAnnotation));
        if (IsFrozen) Unfreeze();
    }

    private void OnViewRotationApplied(DocumentModel doc, string toast)
    {
        ViewRotationStore.Save(doc.FilePath, doc.ViewRotation);
        NotifyViewRotationChanged();
        ShowStatusToast(toast);
        OnPropertyChanged(nameof(ActiveTab));
        InvalidateAll();
        RequestAnimationFrame();
    }

    internal void NotifyViewRotationChanged()
    {
        OnPropertyChanged(nameof(IsViewRotated));
        OnPropertyChanged(nameof(ViewRotationDegrees));
    }

    // --- "Sideways text" rotate-to-read affordance ---

    private (int Page, int Block)? _lastSidewaysHint;

    /// <summary>Called from the reading-context convergence point: when rail reading lands on a block
    /// that Core says is sideways, surface the rotate-to-read affordance once per block (a toast; the
    /// status bar separately shows a persistent badge while actually rotated).</summary>
    private void MaybeHintSidewaysBlock()
    {
        // Only while actually rail-reading: CurrentBlockUprightTurns reads the seated analysis'
        // current block even when rail is idle, and the hint is about the reading position.
        if (_controller.FocusedViewport is not { } vp || !vp.Rail.Active
            || _controller.CurrentBlockUprightTurns == 0)
        {
            _lastSidewaysHint = null;
            return;
        }
        var key = (vp.CurrentPage, vp.Rail.CurrentBlock);
        if (_lastSidewaysHint == key) return;
        _lastSidewaysHint = key;
        ShowStatusToast("Sideways text — press U to rotate to read");
    }
}
