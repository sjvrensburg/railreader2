using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
            OnPropertyChanged(nameof(PortalWindowLabel));
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

    // Id of the portal whose crop the tracking preview is currently showing (pinned), or null when
    // nothing is shown. The preview stays on this portal until the reading position reaches a different
    // portal's source; delete/rename also key off it.
    private string? _displayedPortalId;
    // True when the active portal's target page was not yet analysed at render time, so a forced pass
    // (target page just analysed) should retry instead of being debounced away.
    private bool _portalTargetPending;
    // Reading position (page, block, line) at the last evaluation — lets steady reading on one line
    // skip the whole match. Includes the line because line-precise sources can change the match as the
    // reading position advances line-by-line within a block.
    private (int Page, int Block, int Line)? _lastEvalReadingBlock;
    // Per-page size cache for the active document (PDFium GetPageSize is an immutable-per-page P/Invoke
    // under the global lock; ResolveAnchorBlock/Line now run per line advance). Cleared on tab switch.
    private readonly Dictionary<int, (double W, double H)> _pageSizeCache = [];
    // Right-click "Set as portal target" stashes a target here; "Link from current reading position"
    // then consumes it.
    private (int Page, int Block)? _pendingTargetForLink;

    private (double W, double H) PageSize(DocumentState doc, int page)
    {
        if (_pageSizeCache.TryGetValue(page, out var size)) return size;
        size = doc.Pdf.GetPageSize(page);
        _pageSizeCache[page] = size;
        return size;
    }

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
        if (_controller.ActiveDocument is not { } doc || ActiveTab is not { } tab) return;
        if (!IsBlockResolvable(doc, page, block))
        {
            ShowStatusToast("No detected block here to open in a portal");
            return;
        }

        var bitmap = RenderBlockCrop(doc, page, block);
        if (bitmap is null)
        {
            ShowStatusToast("Could not render block");
            return;
        }

        // Adopt the current tab as the image owner so the next EvaluatePortals does not treat this
        // freshly-opened peek as belonging to a stale tab and immediately dismiss it.
        _portalImageOwner = tab;
        SetPeekImage(bitmap);   // raises ShouldShowPortalWindow → MainWindow opens the window
        PortalPeekLabel = DefaultPortalLabel(doc, page, block);
        // The peek holds while you stay in the same block (not line) — it shows a whole target block.
        var (rp, rb, _) = CurrentReadingBlock(doc);
        _peekAnchorReadingBlock = (rp, rb);
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

    /// <summary>Clear the saved-portal tracking preview (image, label, hint, debounce/display ids).</summary>
    private void ClearActivePortal()
    {
        _displayedPortalId = null;
        _portalTargetPending = false;
        SetPortalImage(null);
        ActivePortalLabel = null;
        PortalHint = NoActivePortalHint;
    }

    /// <summary>The page-level (page, block, line) the reading position currently sits on; block/line
    /// are -1 when not rail-reading. Read straight from RailNav — no text extraction (see
    /// <see cref="EvaluatePortals"/>).</summary>
    private static (int Page, int Block, int Line) CurrentReadingBlock(DocumentState doc)
        => doc.Rail is { Active: true, HasAnalysis: true } rail
            ? (doc.CurrentPage, rail.CurrentNavigableArrayIndex, rail.CurrentLine)
            : (doc.CurrentPage, -1, -1);

    // --- Sync loop ---

    /// <summary>Re-evaluate which saved portal (if any) the reading position is inside and render its
    /// target into the tracking preview; also auto-dismiss a temporary peek once reading leaves the
    /// block it was opened over. UI thread only (PDFium). Steady reading within one block returns
    /// immediately (memoized on the reading block); <paramref name="forceRender"/> (portal mutation /
    /// pending-target retry) bypasses that fast path.</summary>
    internal void EvaluatePortals(bool forceRender = false)
    {
        if (_controller.ActiveDocument is not { } doc || ActiveTab is not { } tab)
        {
            ClearPortalPeek();
            if (_displayedPortalId is not null || ActivePortalImage is not null)
                ClearActivePortal();
            _portalImageOwner = null;
            _lastEvalReadingBlock = null;
            _pageSizeCache.Clear();
            return;
        }

        var (srcPage, srcBlock, srcLine) = CurrentReadingBlock(doc);
        bool ownerSwitched = !ReferenceEquals(_portalImageOwner, tab);

        // Fast path: nothing that affects portals changed since the last evaluation (same tab, same
        // reading block, not forced) — the active portal and peek state cannot have changed.
        if (!forceRender && !ownerSwitched && _lastEvalReadingBlock == (srcPage, srcBlock, srcLine))
            return;
        _lastEvalReadingBlock = (srcPage, srcBlock, srcLine);

        // Auto-dismiss a temporary peek when reading leaves the block it was opened over, or on tab switch.
        if (_peekAnchorReadingBlock is { } anchor && (anchor != (srcPage, srcBlock) || ownerSwitched))
            ClearPortalPeek();

        // Tab switch: reset tracking state. The tracking IMAGE is cleared by the single render-or-clear
        // below (not here) so one EvaluatePortals never swaps the tracking bitmap twice in a row (which
        // would defeat the one-behind deferred-disposal guarantee).
        if (ownerSwitched)
        {
            _portalImageOwner = tab;
            _displayedPortalId = null;
            _portalTargetPending = false;
            _pendingTargetForLink = null;
            _pageSizeCache.Clear();
        }

        // No portals: clear any tracking image (e.g. last portal deleted, or a stale crop on tab switch).
        if (tab.Portals.Portals.Count == 0)
        {
            if (_displayedPortalId is not null || ActivePortalImage is not null)
                ClearActivePortal();
            return;
        }

        // The saved portal whose source the reading position has reached (source page checked first so
        // the per-portal resolve work runs only for same-page sources).
        Portal? active = null;
        int bestThreshold = -1;
        if (srcBlock >= 0)
        {
            foreach (var p in tab.Portals.Portals)
            {
                if (p.Source.Page != srcPage) continue;
                if (ResolveAnchorBlock(doc, p.Source) != srcBlock) continue;
                // Whole-block source (Line < 0) → threshold 0 (matches anywhere in the block); a
                // line-precise source matches once the reading line reaches it. Most-recently-passed
                // line wins, so several references in one paragraph each take over in turn.
                int threshold = Math.Max(0, ResolveAnchorLine(doc, p.Source, srcBlock));
                if (srcLine >= threshold && threshold > bestThreshold)
                {
                    active = p;
                    bestThreshold = threshold;
                }
            }
        }

        // Pin-until-different: switch the preview only when the reading position reaches a DIFFERENT
        // portal's source. The current target otherwise stays up — moving off the source line, leaving
        // the block, scrolling around — until another portal's source is crossed.
        if (active is not null && active.Id != _displayedPortalId)
        {
            _displayedPortalId = active.Id;
            ActivePortalLabel = active.Label;
            RenderPortalTarget(doc, active.Target.Page, ResolveAnchorBlock(doc, active.Target));
            return;
        }

        // Nothing pinned (tab switch with no active source on the new tab, or never activated) → clear.
        if (_displayedPortalId is null)
        {
            if (ActivePortalImage is not null || _portalTargetPending || ActivePortalLabel is not null)
                ClearActivePortal();
            return;
        }

        // A target is pinned and unchanged. Retry it only if it was still resolving when first shown and
        // its page just finished analysing (a forced poll pass).
        if (forceRender && _portalTargetPending
            && tab.Portals.Portals.FirstOrDefault(p => p.Id == _displayedPortalId) is { } shown)
            RenderPortalTarget(doc, shown.Target.Page, ResolveAnchorBlock(doc, shown.Target));
    }

    /// <summary>Render a target block crop into <see cref="ActivePortalImage"/>. UI thread only.
    /// A still-unanalysed target page (block &lt; 0) leaves the panel waiting and sets
    /// <see cref="_portalTargetPending"/> so the analysis-complete poll retries via forceRender.</summary>
    private void RenderPortalTarget(DocumentState doc, int page, int block)
    {
        bool resolvable = block >= 0 && page >= 0 && page < doc.PageCount
            && doc.AnalysisCache.TryGetValue(page, out var analysis) && block < analysis.Blocks.Count;
        if (!resolvable)
        {
            _portalTargetPending = true;
            SetPortalImage(null);
            PortalHint = "Resolving target…";
            return;
        }

        _portalTargetPending = false;
        var bitmap = RenderBlockCrop(doc, page, block);
        if (bitmap is null)
        {
            SetPortalImage(null);
            PortalHint = "Could not render portal target.";
            return;
        }
        SetPortalImage(bitmap);
        PortalHint = null;
    }

    /// <summary>Rasterise a detected block region and decode it to an Avalonia <see cref="Bitmap"/>,
    /// or null if the page/block is unavailable or rendering fails. UI thread only (PDFium).</summary>
    private Bitmap? RenderBlockCrop(DocumentState doc, int page, int block)
    {
        if (block < 0 || page < 0 || page >= doc.PageCount
            || !doc.AnalysisCache.TryGetValue(page, out var analysis)
            || block >= analysis.Blocks.Count)
            return null;

        var (pageW, pageH) = PageSize(doc, page);
        byte[]? png = BlockCropRenderer.RenderBlockAsPng(doc.Pdf, page, analysis.Blocks[block].BBox, pageW, pageH);
        if (png is null || png.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(png);
            return new Bitmap(ms);
        }
        catch (Exception ex)
        {
            _logger.Error("[Portals] Failed to decode block crop", ex);
            return null;
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
        // Parse the stored role name once (enum compare avoids a per-block Role.ToString() allocation).
        if (!Enum.TryParse<BlockRole>(a.Role, out var anchorRole)) return -1;

        var (pageW, pageH) = PageSize(doc, a.Page);

        // Fast path: the stored index still points at the same block.
        if (a.Block >= 0 && a.Block < blocks.Count)
        {
            var b = blocks[a.Block];
            if (b.Role == anchorRole && BBoxClose(b.BBox, a, pageW, pageH))
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
            if (b.Role != anchorRole) continue;
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

    /// <summary>Resolve a source anchor's line index on the current analysis of its already-resolved
    /// block, or -1 for a whole-block source. Mirrors <see cref="ResolveAnchorBlock"/>: trusts the
    /// stored index while its normalized Y still matches, else falls back to the nearest-centre line.</summary>
    private int ResolveAnchorLine(DocumentState doc, PortalAnchor a, int resolvedBlock)
    {
        if (a.Line < 0) return -1;   // whole-block source
        if (!doc.AnalysisCache.TryGetValue(a.Page, out var analysis)
            || resolvedBlock < 0 || resolvedBlock >= analysis.Blocks.Count)
            return -1;
        var lines = analysis.Blocks[resolvedBlock].Lines;
        if (lines.Count == 0) return -1;
        if (a.Ly < 0)   // no stored position (legacy / unexpected) → clamp the stored index
            return Math.Min(a.Line, lines.Count - 1);

        var (_, pageH) = PageSize(doc, a.Page);
        const float eps = 0.02f;
        // Fast path: the stored index still lines up by position.
        if (a.Line < lines.Count && Math.Abs((float)(lines[a.Line].Y / pageH) - a.Ly) < eps)
            return a.Line;
        // Fallback: nearest-centre line.
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            float d = Math.Abs((float)(lines[i].Y / pageH) - a.Ly);
            if (d < bestDist) { bestDist = d; best = i; }
        }
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

    /// <summary>Build an anchor for a block; <paramref name="line"/> &gt;= 0 makes it a line-precise
    /// source anchor (also captures the line centre's normalized Y). Targets pass line -1 (whole block).</summary>
    private PortalAnchor MakeAnchor(DocumentState doc, int page, int block, int line = -1)
    {
        var (pageW, pageH) = PageSize(doc, page);
        var b = doc.AnalysisCache[page].Blocks[block];
        float ly = line >= 0 && line < b.Lines.Count ? (float)(b.Lines[line].Y / pageH) : -1f;
        return new PortalAnchor
        {
            Page = page,
            Block = block,
            Line = line,
            Role = b.Role.ToString(),
            Nx = (float)(b.BBox.X / pageW),
            Ny = (float)(b.BBox.Y / pageH),
            Nw = (float)(b.BBox.W / pageW),
            Nh = (float)(b.BBox.H / pageH),
            Ly = ly,
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

        int sp, sb, sl;   // source page, block, line (-1 = whole-block source)
        if (sourcePage is { } ep && sourceBlock is { } eb) { sp = ep; sb = eb; sl = -1; }
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
            sl = pos.LineIndex;   // anchor the source to the line you're on, so it fires at the reference
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
            Source = MakeAnchor(doc, sp, sb, sl),
            Target = MakeAnchor(doc, targetPage, targetBlock),   // line -1: a target is shown whole-block
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
        // Clear the preview if the deleted portal is the one currently shown (pinned).
        if (_displayedPortalId == id)
            ClearActivePortal();
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
        if (_displayedPortalId == id) ActivePortalLabel = label;
        NotifyPortalsChanged();
    }

    // Frame-zoom duration for "Go to source"; the line seek is deferred just past it.
    private const double GoToSourceFrameMs = 320.0;

    /// <summary>Navigate to a portal's source block, frame it, and seat the rail on the exact source
    /// line (the list's "Go to source" action).</summary>
    public void GoToPortalSource(string id)
    {
        if (_controller.ActiveDocument is not { } doc || ActiveTab is not { } tab) return;
        var portal = tab.Portals.Portals.FirstOrDefault(p => p.Id == id);
        if (portal is null) return;

        GoToPage(portal.Source.Page);
        int block = ResolveAnchorBlock(doc, portal.Source);
        if (block < 0) { RequestViewportFocus(); return; }

        int line = ResolveAnchorLine(doc, portal.Source, block);   // -1 for a whole-block source
        bool framed = SmoothlyFrameBlock(block, durationMs: GoToSourceFrameMs);
        if (framed && line > 0)
            SeekRailLineAfterFrame(line);
        RequestViewportFocus();
    }

    /// <summary>SmoothlyFrameBlock seats line 0 and re-zeroes the line when the rail activates mid-zoom,
    /// so defer the line seek until the frame settles, then snap to the source line.</summary>
    private void SeekRailLineAfterFrame(int line)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GoToSourceFrameMs + 80) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_controller.ActiveDocument is not { Rail: { Active: true, HasAnalysis: true } rail } d) return;
            rail.CurrentLine = Math.Clamp(line, 0, Math.Max(0, rail.CurrentNavigableBlock.Lines.Count - 1));
            var (ww, wh) = _controller.GetViewportSize();
            d.StartSnap(ww, wh);
            InvalidateCameraAndTab();
            RequestAnimationFrame();
        };
        timer.Start();
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
                SourceText = p.Source.Line >= 0
                    ? $"Source: p.{p.Source.Page + 1}, line {p.Source.Line + 1}"
                    : $"Source: p.{p.Source.Page + 1}",
                TargetText = $"Target: {p.Target.Role} · p.{p.Target.Page + 1}",
            });
        return rows;
    }

    private static string DefaultPortalLabel(DocumentState doc, int page, int block)
    {
        if (!doc.AnalysisCache.TryGetValue(page, out var analysis) || block < 0 || block >= analysis.Blocks.Count)
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
