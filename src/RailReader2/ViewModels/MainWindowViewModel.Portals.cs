using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Renderer.Skia;
using RailReader2.Services;

namespace RailReader2.ViewModels;

// Portals — linked context viewports. When the reading position enters a saved portal's source block,
// the portal's target is rendered (BlockCropRenderer, UI thread) into the docked Portals-pane preview
// (ActivePortalImage) and, when tracking is detached, the floating PortalWindow. A separate "Open in
// Portal (Temporary)" peek renders an arbitrary block into the pop-out window only (PortalPeekImage),
// so it never replaces the docked tracking preview. Shell-side model + persistence
// (Services/PortalSet.cs); see docs/portals-design.md.
public sealed partial class MainWindowViewModel
{
    // The saved-portal tracking crop. Manual property (not [ObservableProperty]) so the swap can defer
    // disposal of the replaced Bitmap one step behind — it is the Source of the docked Image and (when
    // no peek is up) the pop-out window Image, so the just-replaced one may still be mid-paint.
    private Bitmap? _activePortalImage;
    public Bitmap? ActivePortalImage
    {
        get => _activePortalImage;
        private set
        {
            _activePortalImage = value;
            OnPropertyChanged(nameof(ActivePortalImage));
            OnPropertyChanged(nameof(PortalWindowImage));
        }
    }
    private Bitmap? _portalImageBehind;       // one-behind, disposed on the following swap
    private TabViewModel? _portalImageOwner;   // tab the pinned image belongs to (clears on switch)

    // Temporary peek crop ("Open in Portal"). Shown in the pop-out window only — never in the docked
    // pane — so it cannot replace a saved portal that is tracking the reading position.
    private Bitmap? _portalPeekImage;
    public Bitmap? PortalPeekImage
    {
        get => _portalPeekImage;
        private set
        {
            _portalPeekImage = value;
            OnPropertyChanged(nameof(PortalPeekImage));
            OnPropertyChanged(nameof(PortalWindowImage));
            OnPropertyChanged(nameof(PortalWindowHint));
            OnPropertyChanged(nameof(ShouldShowPortalWindow));
        }
    }
    private Bitmap? _portalPeekImageBehind;
    // Reading block a peek was opened over; the peek auto-dismisses once the reading position leaves it.
    private (int Page, int Block)? _peekAnchorReadingBlock;

    [ObservableProperty] private string? _activePortalLabel;
    [ObservableProperty] private string? _portalPeekLabel;
    [ObservableProperty] private string? _portalHint = NoActivePortalHint;
    [ObservableProperty] private bool _isPortalPoppedOut;

    private const string NoActivePortalHint = "No active portal — read into a linked source.";

    // What the pop-out window shows: the peek when one is up, otherwise the saved-portal tracking view.
    public Bitmap? PortalWindowImage => PortalPeekImage ?? ActivePortalImage;
    public string? PortalWindowLabel => PortalPeekImage is not null ? PortalPeekLabel : ActivePortalLabel;
    public string? PortalWindowHint => PortalPeekImage is not null ? null : PortalHint;

    /// <summary>The pop-out window is shown while tracking is detached (the "Pop out" button) or a
    /// temporary peek is up. MainWindow opens/closes the window in response to this.</summary>
    public bool ShouldShowPortalWindow => IsPortalPoppedOut || PortalPeekImage is not null;

    partial void OnActivePortalLabelChanged(string? value) => OnPropertyChanged(nameof(PortalWindowLabel));
    partial void OnPortalPeekLabelChanged(string? value) => OnPropertyChanged(nameof(PortalWindowLabel));
    partial void OnPortalHintChanged(string? value) => OnPropertyChanged(nameof(PortalWindowHint));
    partial void OnIsPortalPoppedOutChanged(bool value) => OnPropertyChanged(nameof(ShouldShowPortalWindow));

    // Debounce: id of the saved portal rendered into the tracking preview. A re-render only happens on
    // a change of active source (or forceRender when the target page finishes analysing).
    private string? _activePortalKey;
    // Right-click "Set as portal target" stashes a target here; "Link from current reading position"
    // then consumes it.
    private (int Page, int Block)? _pendingTargetForLink;

    /// <summary>Raised when the portal list changes (add/delete/rename/tab switch) so
    /// <c>PortalsView</c> rebuilds its rows.</summary>
    public event Action? PortalsChanged;
    internal void NotifyPortalsChanged() => PortalsChanged?.Invoke();

    public void PopOutPortal() => IsPortalPoppedOut = true;
    public void DockPortal() => IsPortalPoppedOut = false;

    /// <summary>Close the pop-out window entirely: dismiss any temporary peek and re-dock tracking.
    /// Drives <see cref="ShouldShowPortalWindow"/> false, which MainWindow reacts to by closing it.</summary>
    public void DismissPortalWindow()
    {
        ClearPortalPeek();
        IsPortalPoppedOut = false;
    }

    /// <summary>True while a reading position is available to use as a portal source — i.e. rail mode
    /// is active with layout analysis. Authoring actions that capture the current reading position
    /// gate on this (the right-click menu disables them, with an explanatory tooltip, otherwise).</summary>
    public bool CanCaptureReadingPosition
        => _controller.ActiveDocument is { Rail: { Active: true, HasAnalysis: true } };

    /// <summary>True when a block has been stashed via <see cref="SetPortalTarget"/> and is waiting to
    /// be linked to a reading position.</summary>
    public bool HasPendingPortalTarget => _pendingTargetForLink is not null;

    /// <summary>Open a one-off "temporary portal": render an arbitrary detected block into the floating
    /// pop-out window (auto-opening it), leaving the docked saved-portal preview untouched. The peek
    /// auto-dismisses once you read on to a different block. Not persisted; needs no rail mode.</summary>
    public void ShowBlockInPortal(int page, int block)
    {
        if (_controller.ActiveDocument is not { } doc) return;
        if (!IsBlockResolvable(doc, page, block))
        {
            ShowStatusToast("No detected block here to open in a portal");
            return;
        }

        var (pageW, pageH) = doc.Pdf.GetPageSize(page);
        byte[]? png = BlockCropRenderer.RenderBlockAsPng(
            doc.Pdf, page, doc.AnalysisCache[page].Blocks[block].BBox, pageW, pageH);
        if (png is null || png.Length == 0)
        {
            ShowStatusToast("Could not render block");
            return;
        }

        try
        {
            using var ms = new MemoryStream(png);
            // Setting the peek image flips ShouldShowPortalWindow true → MainWindow opens the window.
            SetPeekImage(new Bitmap(ms));
        }
        catch (Exception ex)
        {
            _logger.Error("[Portals] Failed to decode peek crop", ex);
            ShowStatusToast("Could not render block");
            return;
        }
        PortalPeekLabel = DefaultPortalLabel(doc, page, block);
        _peekAnchorReadingBlock = CurrentReadingBlock(doc);
    }

    private void SetPeekImage(Bitmap? next)
    {
        // Defer disposal one swap behind, like the tracking image: the pop-out window may still be
        // painting the just-replaced peek bitmap.
        var toDispose = _portalPeekImageBehind;
        _portalPeekImageBehind = PortalPeekImage;
        PortalPeekImage = next;
        toDispose?.Dispose();
    }

    /// <summary>Dismiss the temporary peek (read-on, tab switch, dock, or a new peek). The pop-out
    /// window then shows saved-portal tracking again, or closes if tracking was not detached.</summary>
    private void ClearPortalPeek()
    {
        if (PortalPeekImage is null && PortalPeekLabel is null && _peekAnchorReadingBlock is null) return;
        _peekAnchorReadingBlock = null;
        PortalPeekLabel = null;
        SetPeekImage(null);
    }

    /// <summary>The page-level (page, block) the reading position currently sits on, or block -1 when
    /// not rail-reading. Read straight from RailNav — no text extraction (see <see cref="EvaluatePortals"/>).</summary>
    private static (int Page, int Block) CurrentReadingBlock(DocumentState doc)
        => (doc.CurrentPage,
            doc.Rail is { Active: true, HasAnalysis: true } rail ? rail.CurrentNavigableArrayIndex : -1);

    // --- Sync loop ---

    /// <summary>Re-evaluate which saved portal (if any) the reading position is inside and render its
    /// target into the tracking preview; also auto-dismiss a temporary peek once reading leaves the
    /// block it was opened over. UI thread only (PDFium). Cheap while reading within one source
    /// (debounced on the active portal id); <paramref name="forceRender"/> bypasses the debounce so a
    /// pending target re-renders once its page is analysed.</summary>
    internal void EvaluatePortals(bool forceRender = false)
    {
        if (_controller.ActiveDocument is not { } doc || ActiveTab is not { } tab)
        {
            ClearPortalPeek();
            if (ActivePortalImage is not null || _activePortalKey is not null)
            {
                _activePortalKey = null;
                _portalImageOwner = null;
                SetPortalImage(null);
                ActivePortalLabel = null;
                PortalHint = NoActivePortalHint;
            }
            return;
        }

        // The block under the reading position. Read straight from RailNav rather than
        // GetReadingPosition(), whose per-call text extraction would tax every rail line advance: by
        // construction CurrentNavigableArrayIndex == IndexOf(CurrentNavigableBlock) ==
        // ReadingPosition.BlockIndex (all the page-level index into analysis.Blocks — see RailNav), so
        // the seam the design flagged is closed. Block -1 when not rail-reading → no source active.
        var (srcPage, srcBlock) = CurrentReadingBlock(doc);

        // Auto-dismiss a temporary peek when reading leaves the block it was opened over, or on a tab
        // switch. (Saved-portal tracking is separate and continues below.)
        if (_peekAnchorReadingBlock is { } anchor
            && (anchor != (srcPage, srcBlock) || !ReferenceEquals(_portalImageOwner, tab)))
            ClearPortalPeek();

        // The tracking image belongs to whichever tab last rendered it; a tab switch must not leave the
        // previous document's target on screen. Clear and force a fresh evaluation for the new tab.
        if (!ReferenceEquals(_portalImageOwner, tab))
        {
            _portalImageOwner = tab;
            _activePortalKey = null;
            _pendingTargetForLink = null;
            SetPortalImage(null);
            ActivePortalLabel = null;
            PortalHint = NoActivePortalHint;
            forceRender = true;
        }

        // No portals: nothing to auto-match.
        if (tab.Portals.Portals.Count == 0)
        {
            if (_activePortalKey is not null)
            {
                _activePortalKey = null;
                SetPortalImage(null);
                ActivePortalLabel = null;
                PortalHint = NoActivePortalHint;
            }
            return;
        }

        // First portal whose resolved source block is the one being read (source page checked first
        // so ResolveAnchorBlock — which calls into PDFium for page size — runs at most once or twice).
        Portal? active = null;
        if (srcBlock >= 0)
        {
            foreach (var p in tab.Portals.Portals)
            {
                if (p.Source.Page != srcPage) continue;
                if (ResolveAnchorBlock(doc, p.Source) == srcBlock) { active = p; break; }
            }
        }

        string? key = active?.Id;
        if (key == _activePortalKey && !forceRender) return;

        if (active is null)
        {
            // No active source: pin the last target (Sioyek behaviour). Mark inactive so re-entering
            // the same source later registers as a change and re-renders.
            _activePortalKey = null;
            return;
        }

        _activePortalKey = active.Id;
        ActivePortalLabel = active.Label;
        RenderPortalTarget(doc, active.Target.Page, ResolveAnchorBlock(doc, active.Target));
    }

    /// <summary>Render a target block crop into <see cref="ActivePortalImage"/>. UI thread only.
    /// A still-unanalysed target page (block &lt; 0) leaves the panel waiting; the analysis-complete
    /// poll re-runs <see cref="EvaluatePortals"/> with forceRender.</summary>
    private void RenderPortalTarget(DocumentState doc, int page, int block)
    {
        if (block < 0 || page < 0 || page >= doc.PageCount
            || !doc.AnalysisCache.TryGetValue(page, out var analysis)
            || block >= analysis.Blocks.Count)
        {
            SetPortalImage(null);
            PortalHint = "Resolving target…";
            return;
        }

        var (pageW, pageH) = doc.Pdf.GetPageSize(page);
        byte[]? png = BlockCropRenderer.RenderBlockAsPng(doc.Pdf, page, analysis.Blocks[block].BBox, pageW, pageH);
        if (png is null || png.Length == 0)
        {
            SetPortalImage(null);
            PortalHint = "Could not render portal target.";
            return;
        }

        try
        {
            using var ms = new MemoryStream(png);
            SetPortalImage(new Bitmap(ms));
            PortalHint = null;
        }
        catch (Exception ex)
        {
            _logger.Error("[Portals] Failed to decode portal target crop", ex);
            SetPortalImage(null);
            PortalHint = "Could not render portal target.";
        }
    }

    private void SetPortalImage(Bitmap? next)
    {
        // Defer disposal one swap behind so neither the panel nor the pop-out window disposes a
        // bitmap it may still be painting.
        var toDispose = _portalImageBehind;
        _portalImageBehind = ActivePortalImage;
        ActivePortalImage = next;
        toDispose?.Dispose();
    }

    internal void DisposePortalImages()
    {
        _portalImageBehind?.Dispose();
        _portalImageBehind = null;
        var cur = ActivePortalImage;
        ActivePortalImage = null;
        cur?.Dispose();

        _portalPeekImageBehind?.Dispose();
        _portalPeekImageBehind = null;
        var peek = PortalPeekImage;
        PortalPeekImage = null;
        peek?.Dispose();
    }

    // --- Anchor resolution (used for both source-match and target-render) ---

    /// <summary>Resolve a stored anchor to a live block index on its page, or -1 if the page is not
    /// analysed yet. Fast path: the stored index, validated by role + normalized bbox (ε ≈ 2%). If a
    /// page was re-analysed into a different order, falls back to the nearest-centre same-role block.</summary>
    private int ResolveAnchorBlock(DocumentState doc, PortalAnchor a)
    {
        if (a.Page < 0 || a.Page >= doc.PageCount) return -1;
        if (!doc.AnalysisCache.TryGetValue(a.Page, out var analysis)) return -1;
        var blocks = analysis.Blocks;
        if (blocks.Count == 0) return -1;

        var (pageW, pageH) = doc.Pdf.GetPageSize(a.Page);

        // Fast path: the stored index still points at the same block.
        if (a.Block >= 0 && a.Block < blocks.Count)
        {
            var b = blocks[a.Block];
            if (b.Role.ToString() == a.Role && BBoxClose(b.BBox, a, pageW, pageH))
                return a.Block;
        }

        // Fallback: nearest-centre block of the same role.
        int best = -1;
        float bestDist = float.MaxValue;
        float ax = a.Nx + a.Nw / 2f;
        float ay = a.Ny + a.Nh / 2f;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Role.ToString() != a.Role) continue;
            float cx = (float)((b.BBox.X + b.BBox.W / 2f) / pageW);
            float cy = (float)((b.BBox.Y + b.BBox.H / 2f) / pageH);
            float d = (cx - ax) * (cx - ax) + (cy - ay) * (cy - ay);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (best >= 0 && best != a.Block)
            _logger.Debug($"[Portals] Anchor fallback on page {a.Page}: stored block {a.Block} → "
                + $"nearest-centre {best} (role {a.Role}).");
        return best;
    }

    private static bool BBoxClose(BBox b, PortalAnchor a, double pageW, double pageH)
    {
        const float eps = 0.02f;
        float nx = (float)(b.X / pageW), ny = (float)(b.Y / pageH);
        float nw = (float)(b.W / pageW), nh = (float)(b.H / pageH);
        return Math.Abs(nx - a.Nx) < eps && Math.Abs(ny - a.Ny) < eps
            && Math.Abs(nw - a.Nw) < eps && Math.Abs(nh - a.Nh) < eps;
    }

    private static PortalAnchor MakeAnchor(DocumentState doc, int page, int block)
    {
        var (pageW, pageH) = doc.Pdf.GetPageSize(page);
        var b = doc.AnalysisCache[page].Blocks[block];
        return new PortalAnchor
        {
            Page = page,
            Block = block,
            Role = b.Role.ToString(),
            Nx = (float)(b.BBox.X / pageW),
            Ny = (float)(b.BBox.Y / pageH),
            Nw = (float)(b.BBox.W / pageW),
            Nh = (float)(b.BBox.H / pageH),
        };
    }

    private static bool IsBlockResolvable(DocumentState doc, int page, int block)
        => block >= 0 && doc.AnalysisCache.TryGetValue(page, out var a) && block < a.Blocks.Count;

    // --- Authoring / management ---

    /// <summary>Create a portal from a target (page+block) to a source. Source defaults to the
    /// current reading position when not supplied. Captures role + normalized bbox for both anchors,
    /// persists, refreshes the list, and activates the portal if its source is the current block.</summary>
    public void CreatePortal(int targetPage, int targetBlock, int? sourcePage = null,
        int? sourceBlock = null, string? label = null)
    {
        if (_controller.ActiveDocument is not { } doc || ActiveTab is not { } tab) return;

        int sp, sb;
        if (sourcePage is { } ep && sourceBlock is { } eb) { sp = ep; sb = eb; }
        else
        {
            var pos = _controller.GetReadingPosition();
            if (pos is null)
            {
                ShowStatusToast("Rail-read to the referencing text first to set the portal source");
                return;
            }
            sp = pos.Page;
            sb = pos.BlockIndex;
        }

        if (!IsBlockResolvable(doc, sp, sb) || !IsBlockResolvable(doc, targetPage, targetBlock))
        {
            ShowStatusToast("Block not available yet — wait for layout analysis");
            return;
        }

        var portal = new Portal
        {
            Id = Guid.NewGuid().ToString("n"),
            Label = string.IsNullOrWhiteSpace(label) ? DefaultPortalLabel(doc, targetPage, targetBlock) : label!,
            Source = MakeAnchor(doc, sp, sb),
            Target = MakeAnchor(doc, targetPage, targetBlock),
            CreatedUtc = DateTime.UtcNow.ToString("o"),
        };
        tab.Portals.Portals.Add(portal);
        tab.Portals.Save(tab.FilePath);
        NotifyPortalsChanged();
        ShowStatusToast($"Portal created: {portal.Label}");
        EvaluatePortals(forceRender: true);
    }

    /// <summary>Right-click path: stash a block as the pending portal target.</summary>
    public void SetPortalTarget(int page, int block)
    {
        if (_controller.ActiveDocument is not { } doc) return;
        if (!IsBlockResolvable(doc, page, block))
        {
            ShowStatusToast("No detected block here to set as a portal target");
            return;
        }
        _pendingTargetForLink = (page, block);
        ShowStatusToast("Portal target set — rail-read to the reference, then “Link from current reading position”");
    }

    /// <summary>Right-click path: create the portal from the pending target and the current reading position.</summary>
    public void LinkFromCurrentPosition()
    {
        if (_pendingTargetForLink is not { } t)
        {
            ShowStatusToast("Set a portal target first (right-click a figure → Set as portal target)");
            return;
        }
        CreatePortal(t.Page, t.Block);
        _pendingTargetForLink = null;
    }

    public void DeletePortal(string id)
    {
        if (ActiveTab is not { } tab) return;
        if (tab.Portals.Portals.RemoveAll(p => p.Id == id) == 0) return;
        tab.Portals.Save(tab.FilePath);
        if (_activePortalKey == id)
        {
            _activePortalKey = null;
            SetPortalImage(null);
            ActivePortalLabel = null;
            PortalHint = NoActivePortalHint;
        }
        NotifyPortalsChanged();
        EvaluatePortals(forceRender: true);
    }

    public void RenamePortal(string id, string label)
    {
        if (ActiveTab is not { } tab) return;
        var portal = tab.Portals.Portals.FirstOrDefault(p => p.Id == id);
        if (portal is null) return;
        label = string.IsNullOrWhiteSpace(label) ? portal.Label : label.Trim();
        if (portal.Label == label) return;
        portal.Label = label;
        tab.Portals.Save(tab.FilePath);
        if (_activePortalKey == id) ActivePortalLabel = label;
        NotifyPortalsChanged();
    }

    /// <summary>Navigate to a portal's source block and frame it (the list's "Go to source" action).</summary>
    public void GoToPortalSource(string id)
    {
        if (_controller.ActiveDocument is not { } doc || ActiveTab is not { } tab) return;
        var portal = tab.Portals.Portals.FirstOrDefault(p => p.Id == id);
        if (portal is null) return;
        GoToPage(portal.Source.Page);
        int block = ResolveAnchorBlock(doc, portal.Source);
        if (block >= 0) SmoothlyFrameBlock(block);
        RequestViewportFocus();
    }

    /// <summary>Snapshot the active document's portals as display rows (cheap, no PDFium).</summary>
    public IReadOnlyList<PortalRowViewModel> BuildPortalRows()
    {
        if (ActiveTab is not { } tab) return [];
        var rows = new List<PortalRowViewModel>(tab.Portals.Portals.Count);
        foreach (var p in tab.Portals.Portals)
            rows.Add(new PortalRowViewModel
            {
                Portal = p,
                Label = p.Label,
                SourceText = $"Source: p.{p.Source.Page + 1}",
                TargetText = $"Target: {p.Target.Role} · p.{p.Target.Page + 1}",
            });
        return rows;
    }

    private static string DefaultPortalLabel(DocumentState doc, int page, int block)
    {
        if (!doc.AnalysisCache.TryGetValue(page, out var analysis) || block >= analysis.Blocks.Count)
            return $"Portal (p.{page + 1})";
        var role = analysis.Blocks[block].Role;
        // Ordinal among same-role blocks up to and including this one on the page (1-based).
        int ordinal = 0;
        for (int i = 0; i <= block && i < analysis.Blocks.Count; i++)
            if (analysis.Blocks[i].Role == role) ordinal++;
        string roleName = role switch
        {
            BlockRole.Figure => "Figure",
            BlockRole.Table => "Table",
            BlockRole.DisplayMath => "Equation",
            BlockRole.Heading => "Heading",
            BlockRole.Title => "Title",
            _ => role.ToString(),
        };
        return $"{roleName} {ordinal} (p.{page + 1})";
    }
}
