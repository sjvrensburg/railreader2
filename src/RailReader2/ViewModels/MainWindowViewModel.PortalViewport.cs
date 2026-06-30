using System.Linq;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader2.ViewModels;

// Live confined portal viewport (Core 0.45.0 FocusBlock). When the pop-out PortalWindow is open and a
// target is pinned, MainWindow hosts a chrome-less DocumentView bound to _portalViewport — a viewport
// ADDED to the active document's model and confined to the target block via Viewport.Focus, so rail,
// freeze panes, and annotation all work inside the portal (the DocumentView carries the full toolbar).
//
// Why focusing the portal can't hijack the reading sync: EvaluatePortals reads the reading position off
// doc.Primary (DocumentModel facades), never FocusedViewport — and the portal viewport is never Primary
// — so clicking into the portal (which focuses it, routing the toolbar/keys to it) leaves the
// reading-position-driven pin loop tracking the main view. No ReadingViewport indirection needed.
//
// The docked PortalsView preview and the marker layer keep using the crop pipeline unchanged; this is a
// purely additive renderer for the pop-out window only.
public sealed partial class MainWindowViewModel
{
    // The viewport hosted in the pop-out window, or null when docked/closed. Lifecycle (Add/Remove on
    // the model) is driven from here; the DocumentView that renders it is owned by MainWindow.
    private Viewport? _portalViewport;
    public Viewport? PortalViewport => _portalViewport;

    // The page+block the peek ("Open in Portal (Temporary)") is showing, so ComputePortalLiveTarget can
    // give it precedence over saved/auto pins (mirrors PortalWindowImage = peek ?? active). Set by
    // ShowBlockInPortal, cleared by ClearPortalPeek.
    private (int Page, int Block)? _peekShownBlock;

    private bool _isPortalLive;
    /// <summary>True while the pop-out window is hosting the live confined viewport (vs. the static
    /// crop / hint). Set by MainWindow as it hosts/tears down the DocumentView; bound by PortalWindow to
    /// toggle the live host vs. the crop.</summary>
    public bool IsPortalLive
    {
        get => _isPortalLive;
        private set
        {
            if (_isPortalLive == value) return;
            _isPortalLive = value;
            OnPropertyChanged(nameof(IsPortalLive));
            OnPropertyChanged(nameof(ShowPortalHint));
            // Going live while popped-out makes the already-materialised tracking crop redundant (the
            // docked pane is hidden, the pop-out shows the confined viewport) — free it now (#5/#190).
            // Self-guards on PortalCropRedundant, so the not-live transition is a no-op.
            ReleaseRedundantTrackingCrop();
        }
    }

    /// <summary>The "no active portal" hint shows only when not live, there is no crop to show, and no
    /// temporary peek is logically up (a peek's crop can be elided to null while it is shown live).</summary>
    public bool ShowPortalHint => !_isPortalLive && !HasActivePeek && PortalWindowImage is null;

    /// <summary>True when the saved-tracking / auto-pin crop (<see cref="ActivePortalImage"/>) has no
    /// on-screen consumer: the pop-out is popped out (the docked preview is hidden, showing the Dock
    /// button) AND hosting the live confined viewport (the pop-out's static crop is hidden). Rasterising
    /// the 300-DPI block crop is then pure waste (#190) — the render paths skip it and docking
    /// re-materialises the current target. NB: a docked temporary peek IS live but NOT popped out, so the
    /// visible docked preview still renders its saved-tracking crop.</summary>
    private bool PortalCropRedundant => IsPortalPoppedOut && _isPortalLive;

    /// <summary>Raised when the pop-out's desired live target may have changed (a pin, peek, clear, or
    /// pop-out/dock). MainWindow re-syncs the hosted DocumentView (creating, re-aiming, or tearing it
    /// down). Raised only at genuine content-change points — never per animation frame — so the user can
    /// freely pan/zoom/rail inside the portal without it snapping back.</summary>
    public event Action? PortalViewChanged;

    private void RaisePortalView()
    {
        OnPropertyChanged(nameof(ShowPortalHint));
        PortalViewChanged?.Invoke();
    }

    /// <summary>Raised to ask MainWindow to synchronously tear down the hosted portal DocumentView
    /// (unregister its surface + remove its viewport) RIGHT NOW. Used just before a model that owns the
    /// portal viewport is disposed (last tab closing), so teardown never races a disposed model and the
    /// surface stops ticking before its Core viewport vanishes.</summary>
    internal event Action? PortalViewTeardownRequested;
    private void RequestPortalViewTeardown() => PortalViewTeardownRequested?.Invoke();

    /// <summary>MainWindow flips this as it hosts/tears down the live DocumentView.</summary>
    internal void UpdatePortalLive(bool live) => IsPortalLive = live;

    // A re-aim of the live portal viewport requested DURING an animation frame (a pin fired from inside a
    // surface tick). Applied once at the END of the frame — after every surface, INCLUDING the portal
    // viewport itself, has ticked — so the aim's camera/focus mutation can't collide with the portal
    // viewport's own same-frame clamp/snap (#193). Last-write-wins: the action re-computes the target, so
    // collapsing several requests in one frame to the final one is correct.
    private Action? _deferredPortalReaim;

    /// <summary>Queue a portal-viewport re-aim to run at the end of the current animation frame (see
    /// <see cref="_deferredPortalReaim"/>). The host supplies the re-aim work; the frame loop drains it
    /// via <see cref="ApplyDeferredPortalReaim"/> once all surfaces have ticked this frame.</summary>
    internal void DeferPortalReaim(Action reaim) => _deferredPortalReaim = reaim;

    /// <summary>Run and clear any re-aim queued by <see cref="DeferPortalReaim"/> this frame. Called by
    /// the frame loop after the per-surface tick + render passes complete.</summary>
    private void ApplyDeferredPortalReaim()
    {
        if (_deferredPortalReaim is not { } reaim) return;
        _deferredPortalReaim = null;
        reaim();
    }

    /// <summary>The document the portal viewport must live on (the active/focused viewport's owner).</summary>
    internal DocumentModel? PortalReadingDoc => _controller.FocusedViewport?.Owner;

    /// <summary>The block the pop-out should show live (page, block index into that page's analysis, and
    /// the page-space bounds the camera confines to), or null when nothing is pinned or the block is not
    /// resolvable yet. Peek takes precedence, then a saved portal's target, then an auto pin's float.</summary>
    internal (int Page, int Block, BBox Bounds)? ComputePortalLiveTarget()
    {
        if (PortalReadingDoc is not { } doc || ActiveTab is not { } tab) return null;

        if (_peekShownBlock is { } pk && BlockBounds(doc, pk.Page, pk.Block) is { } pb)
            return (pk.Page, pk.Block, pb);

        if (_displayedPortalId is null) return null;

        // Auto pin: frame the SAME float+caption union the docked crop shows, so a multi-part figure
        // (e.g. two diagrams side by side under one full-width "Figure N" caption) isn't clipped to a
        // single sub-figure in the live view. The FocusBlock index stays the float (a focused figure has
        // no rail lines anyway); only the framed/clamped bounds widen. Falls back to the float bounds when
        // the caption block isn't resolvable.
        if (_autoPin is { } a && BlockBounds(doc, a.TgtPage, a.TgtBlock) is { } ab)
        {
            var bounds = BlockBounds(doc, a.TgtPage, a.CaptionBlock) is { } cb ? Union(ab, cb) : ab;
            return (a.TgtPage, a.TgtBlock, bounds);
        }

        // Saved portal: resolve the stored target anchor to a live block on its page.
        if (tab.Portals.Portals.FirstOrDefault(p => p.Id == _displayedPortalId) is { } portal)
        {
            int block = ResolveAnchorBlock(doc, portal.Target);
            if (BlockBounds(doc, portal.Target.Page, block) is { } b)
                return (portal.Target.Page, block, b);
        }
        return null;
    }

    // Layers over the single resolvability predicate (IsBlockResolvable) so the live-target and the
    // crop/marker pipelines can never disagree about whether a (page, block) is addressable.
    private static BBox? BlockBounds(DocumentModel doc, int page, int block)
        => IsBlockResolvable(doc, page, block) && doc.TryGetAnalysis(page, out var a)
            ? a.Blocks[block].BBox
            : null;

    /// <summary>Add the portal viewport to <paramref name="doc"/> and return it. MainWindow binds a
    /// DocumentView + registers the surface; the view re-sizes the viewport on its first layout.</summary>
    internal Viewport CreatePortalViewport(DocumentModel doc)
    {
        var vp = doc.AddViewport();
        vp.IsLive = true;
        vp.SetSize(420, 280);   // seed so the first frame has sane bounds; refined on layout
        _portalViewport = vp;
        return vp;
    }

    /// <summary>Confine the portal viewport to <paramref name="target"/> and frame it: set
    /// <see cref="Viewport.Focus"/> (camera + rail confinement), move to the target page, seat the
    /// confined rail, and centre the block. Called only when the displayed target changes, so user
    /// pan/zoom inside the portal is preserved between target switches.</summary>
    internal void AimPortalViewport((int Page, int Block, BBox Bounds) target)
    {
        if (_portalViewport is not { } vp || vp.Owner is not { } doc) return;

        vp.Focus = new FocusBlock(target.Page, target.Block, target.Bounds);
        // Ensure the target page is loaded — including when the fresh viewport's default page (0) already
        // equals the target (a peek on page 1), where a `!=` guard would skip the only LoadPageBitmap and
        // leave the live view blank.
        bool needLoad = vp.CurrentPage != target.Page || vp.CachedPage is null;
        vp.CurrentPage = target.Page;
        if (needLoad) vp.LoadPageBitmap();

        // Seat the (now-confined) rail for this viewport: the seat sites pass vp.CurrentFocusBlockIndex,
        // collapsing the navigable set to the focus block. Cache hit seats synchronously; a miss seats
        // when the worker result arrives.
        doc.SubmitAnalysis(vp, _controller.Worker, _controller.Config.NavigableRoles);

        if (vp.Width > 0 && vp.Height > 0)
        {
            var (z, ox, oy) = vp.ComputeCenteredFrame(target.Bounds, vp.Width, vp.Height);
            vp.Camera.Zoom = z;
            vp.Camera.OffsetX = ox;
            vp.Camera.OffsetY = oy;
            vp.ClampCamera(vp.Width, vp.Height);   // focus-aware: floors zoom + confines pan to the block
        }
        vp.RequestAnimation?.Invoke();
    }

    /// <summary>Drop the VM's reference to the portal viewport. The Core viewport removal is owned by the
    /// shared surface teardown (<c>DisposeSecondarySurface</c>) on the normal path; the bare-viewport-undo
    /// path in <c>SyncPortalView</c> (no surface was built) removes it explicitly before calling this, so
    /// this no longer double-removes (#192/#8). Idempotent.</summary>
    internal void TeardownPortalViewport() => _portalViewport = null;
}
