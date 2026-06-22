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
            RaisePortalWindowProps();   // a peek change flips every pop-out-window-derived property
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
    /// temporary peek is up — except mid-dismissal, where in-flight property raises must not let
    /// MainWindow re-create the window being torn down. MainWindow opens/closes the window in
    /// response to this.</summary>
    public bool ShouldShowPortalWindow
        => !_dismissingPortalWindow && (IsPortalPoppedOut || PortalPeekImage is not null);
    private bool _dismissingPortalWindow;

    partial void OnActivePortalLabelChanged(string? value) => OnPropertyChanged(nameof(PortalWindowLabel));
    partial void OnPortalPeekLabelChanged(string? value) => OnPropertyChanged(nameof(PortalWindowLabel));
    partial void OnPortalHintChanged(string? value) => OnPropertyChanged(nameof(PortalWindowHint));

    // The view lock's lifetime is the popped-out state itself: every path that re-docks or closes
    // the window (window Dock button, pane Dock button, WM close, dismissal) funnels through
    // IsPortalPoppedOut=false, so releasing here cannot be bypassed — the docked pane has no unlock
    // control and must never be left silently frozen. Unlocking re-evaluates so tracking catches up.
    partial void OnIsPortalPoppedOutChanged(bool value)
    {
        OnPropertyChanged(nameof(ShouldShowPortalWindow));
        if (!value && _isPortalViewLocked)
        {
            ReleasePortalLock();
            EvaluatePortals(forceRender: true);
        }
    }

    // The pop-out window's content (PortalWindowImage/Label/Hint) and visibility (ShouldShowPortalWindow)
    // are all derived from the peek + tracking state, so a peek swap flips all of them at once. The
    // single-source partial hooks above each raise just the one derived prop their own field feeds.
    private void RaisePortalWindowProps()
    {
        OnPropertyChanged(nameof(PortalWindowImage));
        OnPropertyChanged(nameof(PortalWindowLabel));
        OnPropertyChanged(nameof(PortalWindowHint));
        OnPropertyChanged(nameof(ShouldShowPortalWindow));
    }

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

    // --- Automatic pinning (figures/tables referenced by the current line) ---

    // Caption-label index for the active document; reset on tab switch like _pageSizeCache.
    private readonly ReferenceIndex _referenceIndex = new();
    private readonly PortalPreferences _portalPrefs = PortalPreferences.Load();
    // True when the current line mentions a figure/table the index could not resolve yet (caption
    // page unanalysed, or the per-call index-build budget ran out) — the analysis-complete poll then
    // re-runs the evaluation (forceRender) to retry. Reset at the top of every full evaluation pass
    // (the single point all non-memoized passes share), so no early-return path can leak it.
    private bool _autoRefPending;
    // Auto pins whose target crop failed to render — skipped on retries so a deterministic render
    // failure cannot re-trigger a full-page rasterisation on every pass. Cleared on tab switch.
    private readonly HashSet<string> _failedAutoPins = [];
    // Source/target blocks of the currently displayed auto pin, kept so the on-page markers can be
    // drawn for it (auto pins are never saved Portals, so BuildPortalMarkers has nothing else to read).
    // One field, set and cleared in lockstep with the "auto:" _displayedPortalId via SetAutoPin /
    // ClearAutoPin, so the two can never drift out of sync.
    private (int SrcPage, int SrcBlock, int SrcLine, int TgtPage, int TgtBlock)? _autoPin;
    // _displayedPortalId values for auto pins, so they share the pin-until-different machinery with
    // saved portals without ever colliding with a saved portal's guid. The resolved target is part
    // of the identity, so a same-labelled but different float (numbering restarts across chapters)
    // still counts as "different" and replaces a stale pin.
    private static string AutoPinId(ReferenceIndex.Reference r, ReferenceIndex.Target t)
        => $"auto:{r.Kind}:{r.Number}:p{t.Page}b{t.TargetBlock}";

    /// <summary>Toggle for automatic pinning, persisted app-wide (sidecar). Turning it off clears a
    /// currently shown auto pin; saved portals are unaffected either way.</summary>
    public bool AutoPinPortals
    {
        get => _portalPrefs.AutoPinFiguresTables;
        set
        {
            if (_portalPrefs.AutoPinFiguresTables == value) return;
            _portalPrefs.AutoPinFiguresTables = value;
            _portalPrefs.Save();
            OnPropertyChanged(nameof(AutoPinPortals));
            // Toggling off drops a displayed auto pin — unless the view is locked: the lock's
            // freeze contract wins, and the pin then simply stays until something replaces it after
            // unlock. Fields only — the forced evaluation below performs the single image
            // render-or-clear swap, preserving the one-behind disposal guarantee.
            if (!value && !_isPortalViewLocked && _autoPin is not null)
                ResetDisplayedPortal();
            EvaluatePortals(forceRender: true);
        }
    }

    /// <summary>True when the sync loop is waiting on background analysis — for a saved portal's
    /// target page or an automatic reference's caption page — so the analysis poll should force a
    /// re-evaluation when results arrive.</summary>
    internal bool PortalResolvePending => _portalTargetPending || _autoRefPending;

    // --- View lock (pop-out "Lock" toggle) ---

    /// <summary>While true, the currently shown target is frozen: reading on does not switch it —
    /// neither an automatic reference nor a saved portal's source takes over, and a temporary peek
    /// is not auto-dismissed. Released by the toggle, docking/closing the pop-out, or a tab switch;
    /// unlocking immediately re-evaluates so tracking catches up to the reading position.</summary>
    public bool IsPortalViewLocked
    {
        get => _isPortalViewLocked;
        set
        {
            if (_isPortalViewLocked == value) return;
            _isPortalViewLocked = value;
            OnPropertyChanged(nameof(IsPortalViewLocked));
            if (!value) EvaluatePortals(forceRender: true);
        }
    }
    private bool _isPortalViewLocked;

    /// <summary>Drop the lock without re-evaluating — for callers already inside the sync loop
    /// (tab switch) or about to evaluate themselves.</summary>
    private void ReleasePortalLock()
    {
        if (!_isPortalViewLocked) return;
        _isPortalViewLocked = false;
        OnPropertyChanged(nameof(IsPortalViewLocked));
    }

    private (double W, double H) PageSize(DocumentModel doc, int page)
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
        // Guarded teardown: clearing the peek / flipping popped-out raises ShouldShowPortalWindow
        // mid-way, and on the external-close path MainWindow has already nulled its window
        // reference — without the guard it would re-create (flash) the very window being dismissed.
        // The lock is released by the popped-out change hook.
        _dismissingPortalWindow = true;
        try
        {
            ClearPortalPeek();
            IsPortalPoppedOut = false;
        }
        finally
        {
            _dismissingPortalWindow = false;
        }
        OnPropertyChanged(nameof(ShouldShowPortalWindow));
    }

    /// <summary>True while a reading position is available to use as a portal source — i.e. rail mode
    /// is active with layout analysis. Authoring actions that capture the current reading position
    /// gate on this (the right-click menu disables them, with an explanatory tooltip, otherwise).</summary>
    public bool CanCaptureReadingPosition
        => _controller.FocusedViewport?.Owner is { Rail: { Active: true, HasAnalysis: true } };

    /// <summary>True when a block has been stashed via <see cref="SetPortalTarget"/> and is waiting to
    /// be linked to a reading position.</summary>
    public bool HasPendingPortalTarget => _pendingTargetForLink is not null;

    /// <summary>Open a one-off "temporary portal": render an arbitrary detected block into the floating
    /// pop-out window (auto-opening it), leaving the docked saved-portal preview untouched. The peek
    /// auto-dismisses once you read on to a different block. Not persisted; needs no rail mode.</summary>
    public void ShowBlockInPortal(int page, int block)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc || ActiveTab is not { } tab) return;
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

    /// <summary>Clear the saved-portal tracking preview (image, label, hint, debounce/display ids).
    /// Idempotent — a no-op (no notifications, no marker repaint) when nothing is currently shown, so
    /// callers can invoke it unconditionally.</summary>
    private void ClearActivePortal()
    {
        if (_displayedPortalId is null && ActivePortalImage is null
            && !_portalTargetPending && ActivePortalLabel is null)
            return;
        _displayedPortalId = null;
        _autoPin = null;
        _portalTargetPending = false;
        SetPortalImage(null);
        ActivePortalLabel = null;
        PortalHint = NoActivePortalHint;
        InvalidatePortalMarkers();   // no portal active → drop the accent
    }

    /// <summary>Drop the displayed-pin identity (id, label, hint, pending) WITHOUT touching the
    /// tracking image. For callers that immediately force an <see cref="EvaluatePortals"/>: the
    /// evaluation then performs the single render-or-clear image swap, so one pass never swaps the
    /// tracking bitmap twice (which would defeat the one-behind deferred-disposal guarantee).</summary>
    private void ResetDisplayedPortal()
    {
        _displayedPortalId = null;
        _autoPin = null;
        _portalTargetPending = false;
        ActivePortalLabel = null;
        PortalHint = NoActivePortalHint;
        InvalidatePortalMarkers();
    }

    /// <summary>The page-level (page, block, line) the reading position currently sits on; block/line
    /// are -1 when not rail-reading. Read straight from RailNav — no text extraction (see
    /// <see cref="EvaluatePortals"/>).</summary>
    private static (int Page, int Block, int Line) CurrentReadingBlock(DocumentModel doc)
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
        if (_controller.FocusedViewport?.Owner is not { } doc || ActiveTab is not { } tab)
        {
            ClearPortalPeek();
            ClearActivePortal();
            _portalImageOwner = null;
            _lastEvalReadingBlock = null;
            _autoRefPending = false;
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
        // Single reset point for the auto-retry flag: every full pass clears it, and only TryAutoPin
        // re-arms it. Paths that skip TryAutoPin (lock, active saved portal) thus cannot leak a stale
        // pending that would force a re-evaluation on every analysis result.
        _autoRefPending = false;

        // Auto-dismiss a temporary peek when reading leaves the block it was opened over (unless the
        // view is locked), or on tab switch.
        if (_peekAnchorReadingBlock is { } anchor
            && ((anchor != (srcPage, srcBlock) && !_isPortalViewLocked) || ownerSwitched))
            ClearPortalPeek();

        // Tab switch: reset tracking state. The tracking IMAGE is cleared by the single render-or-clear
        // below (not here) so one EvaluatePortals never swaps the tracking bitmap twice in a row (which
        // would defeat the one-behind deferred-disposal guarantee).
        if (ownerSwitched)
        {
            _portalImageOwner = tab;
            _displayedPortalId = null;
            _autoPin = null;
            _portalTargetPending = false;
            _pendingTargetForLink = null;
            _pageSizeCache.Clear();
            _referenceIndex.Clear();
            _failedAutoPins.Clear();
            ReleasePortalLock();   // a lock is per-document; the new tab tracks normally
        }

        // View locked: the shown target is frozen — skip all reading-position-driven switching
        // (saved-portal activation and auto-pinning). The pending-target retry below still runs so a
        // locked, still-resolving target can finish rendering.
        // The saved portal whose source the reading position has reached (source page checked first so
        // the per-portal resolve work runs only for same-page sources).
        Portal? active = null;
        int bestThreshold = -1;
        if (srcBlock >= 0 && !_isPortalViewLocked)
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
            Pin(doc, active);
            return;
        }

        // No saved portal claimed this line — automatic pinning: a line mentioning "Figure N" /
        // "Table N" pins the referenced float, with the same pin-until-different semantics.
        if (active is null && !_isPortalViewLocked)
            TryAutoPin(doc, srcPage, srcBlock, srcLine);

        // Nothing pinned (tab switch with no active source on the new tab, or never activated) → clear.
        if (_displayedPortalId is null)
        {
            ClearActivePortal();
            return;
        }

        // A target is pinned and unchanged. Retry it only if it was still resolving when first shown and
        // its page just finished analysing (a forced poll pass).
        if (forceRender && _portalTargetPending
            && tab.Portals.Portals.FirstOrDefault(p => p.Id == _displayedPortalId) is { } shown)
            RenderPortalTarget(doc, shown.Target.Page, ResolveAnchorBlock(doc, shown.Target));
    }

    /// <summary>Automatic pinning: parse the current rail line for figure/table references and pin
    /// the first one that resolves to a detected caption + float. Skipped while reading a caption
    /// block (the float is right there). An unresolved reference (caption page not analysed yet)
    /// sets <see cref="_autoRefPending"/> so the analysis poll retries; the previously pinned target
    /// stays up meanwhile. UI thread only.</summary>
    private void TryAutoPin(DocumentModel doc, int srcPage, int srcBlock, int srcLine)
    {
        if (!AutoPinPortals || srcBlock < 0 || srcLine < 0) return;
        if (!doc.TryGetAnalysis(srcPage, out var analysis)
            || srcBlock >= analysis.Blocks.Count
            || analysis.Blocks[srcBlock].Role == BlockRole.Caption)
            return;

        var pageText = doc.GetOrExtractText(srcPage);
        var lines = analysis.Blocks[srcBlock].Lines;
        int lineIdx = Math.Min(srcLine, lines.Count - 1);

        static string? LineText(PageText text, LineInfo line)
        {
            float top = line.Y - line.Height / 2f;
            return text.ExtractTextInRect(line.X, top, line.X + line.Width, top + line.Height);
        }

        string? lineText = LineText(pageText, lines[lineIdx]);
        if (string.IsNullOrEmpty(lineText)) return;

        // Append the block's next line so a mention split across the break ("…see Figure ⏎ 3 shows…")
        // is caught; the start limit keeps mentions wholly on the next line from firing a line early.
        int startLimit = lineText.Length;
        if (lineIdx + 1 < lines.Count && LineText(pageText, lines[lineIdx + 1]) is { Length: > 0 } next)
            lineText = lineText + " " + next;

        var refs = ReferenceIndex.ParseLine(lineText, startLimit);
        if (refs.Count == 0) return;

        // Resolve every mention first: if ANY of this line's references is the one already shown,
        // stay pinned (pin-until-different) — never let an earlier-in-line mention that resolves
        // later silently flip the preview. Otherwise the first resolvable mention wins.
        bool anyIncomplete = false;
        var candidates = new List<(ReferenceIndex.Reference Ref, ReferenceIndex.Target Target)>();
        foreach (var reference in refs)
        {
            var target = _referenceIndex.Resolve(doc, reference, srcPage, out bool incomplete);
            anyIncomplete |= incomplete;
            if (target is not { } t) continue;
            if (t.Page == srcPage && t.CaptionBlock == srcBlock) continue;   // reading the (pseudo-)caption itself
            if (AutoPinId(reference, t) == _displayedPortalId) return;       // already showing — stay
            if (!_failedAutoPins.Contains(AutoPinId(reference, t)))
                candidates.Add((reference, t));
        }
        if (candidates.Count > 0)
        {
            PinAutoReference(doc, candidates[0].Ref, candidates[0].Target, srcPage, srcBlock, srcLine);
            return;
        }
        // Nothing pinned: retry while the index is still incomplete (budgeted build in progress) or
        // more analysis results may still bring the caption in. A complete miss on a fully-analysed
        // document is negative-cached by the index, so no retries are scheduled for it.
        _autoRefPending = anyIncomplete || doc.AnalysedPageCount < doc.PageCount;
    }

    /// <summary>Pin an automatically resolved reference: render the float together with its caption
    /// (the union region, so the label under the figure confirms the match) into the tracking
    /// preview. Shares <see cref="_displayedPortalId"/> with saved portals, so a later saved-portal
    /// activation or a different reference takes over normally.</summary>
    private void PinAutoReference(DocumentModel doc, ReferenceIndex.Reference reference,
        ReferenceIndex.Target target, int srcPage, int srcBlock, int srcLine)
    {
        string autoId = AutoPinId(reference, target);
        if (!doc.TryGetAnalysis(target.Page, out var targetAnalysis)) return;
        var blocks = targetAnalysis.Blocks;
        var region = Union(blocks[target.TargetBlock].BBox, blocks[target.CaptionBlock].BBox);
        var bitmap = RenderRegionCrop(doc, target.Page, region);
        if (bitmap is null)
        {
            // Remember the failure: a deterministic render failure must not re-trigger a full-page
            // rasterisation on every line advance / forced pass. Cleared on tab switch.
            _failedAutoPins.Add(autoId);
            return;   // keep whatever was shown
        }

        _displayedPortalId = autoId;
        _autoPin = (srcPage, srcBlock, srcLine, target.Page, target.TargetBlock);
        _portalTargetPending = false;
        ActivePortalLabel = $"{reference} (p.{target.Page + 1}) · auto";
        SetPortalImage(bitmap);
        PortalHint = null;
        InvalidatePortalMarkers();   // a previously accented saved portal loses the accent
    }

    private static BBox Union(BBox a, BBox b)
    {
        float x = Math.Min(a.X, b.X);
        float y = Math.Min(a.Y, b.Y);
        return new BBox(x, y, Math.Max(a.X + a.W, b.X + b.W) - x, Math.Max(a.Y + a.H, b.Y + b.H) - y);
    }

    /// <summary>Render a target block crop into <see cref="ActivePortalImage"/>. UI thread only.
    /// A still-unanalysed target page (block &lt; 0) leaves the panel waiting and sets
    /// <see cref="_portalTargetPending"/> so the analysis-complete poll retries via forceRender.</summary>
    private void RenderPortalTarget(DocumentModel doc, int page, int block)
    {
        bool resolvable = block >= 0 && page >= 0 && page < doc.PageCount
            && doc.TryGetAnalysis(page, out var analysis) && block < analysis.Blocks.Count;
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
    private Bitmap? RenderBlockCrop(DocumentModel doc, int page, int block)
    {
        if (block < 0 || page < 0 || page >= doc.PageCount
            || !doc.TryGetAnalysis(page, out var analysis)
            || block >= analysis.Blocks.Count)
            return null;
        return RenderRegionCrop(doc, page, analysis.Blocks[block].BBox);
    }

    /// <summary>Rasterise an arbitrary page region (e.g. a figure + its caption) and decode it to an
    /// Avalonia <see cref="Bitmap"/>. UI thread only (PDFium).</summary>
    private Bitmap? RenderRegionCrop(DocumentModel doc, int page, BBox region)
    {
        var (pageW, pageH) = PageSize(doc, page);
        byte[]? png = BlockCropRenderer.RenderBlockAsPng(doc.Pdf, page, region, pageW, pageH);
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
    private int ResolveAnchorBlock(DocumentModel doc, PortalAnchor a)
    {
        if (a.Page < 0 || a.Page >= doc.PageCount) return -1;
        if (!doc.TryGetAnalysis(a.Page, out var analysis)) return -1;
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
    private int ResolveAnchorLine(DocumentModel doc, PortalAnchor a, int resolvedBlock)
    {
        if (a.Line < 0) return -1;   // whole-block source
        if (!doc.TryGetAnalysis(a.Page, out var analysis)
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

    /// <summary>Block bbox as fractions of the page size. The single source of the normalization
    /// convention, so the anchor written by <see cref="MakeAnchor"/> and the comparison in
    /// <see cref="BBoxClose"/> can never drift out of step (the ε fast-path validation relies on them
    /// matching to the bit).</summary>
    private static (float Nx, float Ny, float Nw, float Nh) Normalize(BBox b, double pageW, double pageH)
        => ((float)(b.X / pageW), (float)(b.Y / pageH), (float)(b.W / pageW), (float)(b.H / pageH));

    private static bool BBoxClose(BBox b, PortalAnchor a, double pageW, double pageH)
    {
        const float eps = 0.02f;
        var (nx, ny, nw, nh) = Normalize(b, pageW, pageH);
        return Math.Abs(nx - a.Nx) < eps && Math.Abs(ny - a.Ny) < eps
            && Math.Abs(nw - a.Nw) < eps && Math.Abs(nh - a.Nh) < eps;
    }

    /// <summary>Build an anchor for a block; <paramref name="line"/> &gt;= 0 makes it a line-precise
    /// source anchor (also captures the line centre's normalized Y). Targets pass line -1 (whole block).</summary>
    private PortalAnchor MakeAnchor(DocumentModel doc, int page, int block, int line = -1)
    {
        var (pageW, pageH) = PageSize(doc, page);
        if (!doc.TryGetAnalysis(page, out var pageAnalysis))
            throw new KeyNotFoundException($"No analysis cached for page {page}");
        var b = pageAnalysis.Blocks[block];
        float ly = line >= 0 && line < b.Lines.Count ? (float)(b.Lines[line].Y / pageH) : -1f;
        var (nx, ny, nw, nh) = Normalize(b.BBox, pageW, pageH);
        return new PortalAnchor
        {
            Page = page,
            Block = block,
            Line = line,
            Role = b.Role.ToString(),
            Nx = nx,
            Ny = ny,
            Nw = nw,
            Nh = nh,
            Ly = ly,
        };
    }

    private static bool IsBlockResolvable(DocumentModel doc, int page, int block)
        => block >= 0 && doc.TryGetAnalysis(page, out var a) && block < a.Blocks.Count;

    // --- Authoring / management ---

    /// <summary>Create a portal from a target (page+block) to a source. Source defaults to the
    /// current reading position when not supplied. Captures role + normalized bbox for both anchors,
    /// persists, refreshes the list, and activates the portal if its source is the current block.</summary>
    public void CreatePortal(int targetPage, int targetBlock, int? sourcePage = null,
        int? sourceBlock = null, string? label = null)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc || ActiveTab is not { } tab) return;

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
        InvalidatePortalMarkers();
        ShowStatusToast($"Portal created: {portal.Label}");
        EvaluatePortals(forceRender: true);
    }

    /// <summary>Right-click path: stash a block as the pending portal target.</summary>
    public void SetPortalTarget(int page, int block)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;
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
        // Drop the pin identity if the deleted portal is the one shown; the forced evaluation below
        // does the single image render-or-clear swap (never two swaps in one pass).
        if (_displayedPortalId == id)
            ResetDisplayedPortal();
        NotifyPortalsChanged();
        InvalidatePortalMarkers();
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

    // Frame-zoom duration for the list's "Go to source" action.
    private const double GoToSourceFrameMs = 320.0;

    /// <summary>Navigate to a portal's source block, frame it, and seat the rail on the exact source
    /// line (the list's "Go to source" action). Core 0.27.0's SmoothlyFrameBlock seats the line as part
    /// of the framing — line clamps to 0 for a whole-block source — so no post-frame seek is needed.</summary>
    public void GoToPortalSource(string id)
    {
        if (ActiveTab is not { } tab) return;
        if (tab.Portals.Portals.FirstOrDefault(p => p.Id == id) is { } portal)
            GoToPortalSource(portal);
    }

    /// <summary>Navigate to a portal's source anchor. Takes the portal directly (not an id lookup) so a
    /// transient auto-pin portal — which is never in the saved set — can drive it too.</summary>
    public void GoToPortalSource(Portal portal)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;

        GoToPage(portal.Source.Page);
        int block = ResolveAnchorBlock(doc, portal.Source);
        if (block < 0) { RequestViewportFocus(); return; }

        int line = ResolveAnchorLine(doc, portal.Source, block);   // -1 for a whole-block source
        SmoothlyFrameBlock(block, durationMs: GoToSourceFrameMs, line: Math.Max(0, line));
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
                SourceText = p.Source.Line >= 0
                    ? $"Source: p.{p.Source.Page + 1}, line {p.Source.Line + 1}"
                    : $"Source: p.{p.Source.Page + 1}",
                TargetText = $"Target: {p.Target.Role} · p.{p.Target.Page + 1}",
            });
        return rows;
    }

    /// <summary>Id of the portal whose target is currently shown (pinned), or null. Read by the marker
    /// layer to draw the active portal's two markers accented.</summary>
    public string? DisplayedPortalId => _displayedPortalId;

    /// <summary>Pin a portal as the shown one: render its target into the tracking preview, label it,
    /// and accent its markers. Shared by the sync loop's activation and the marker-click path.</summary>
    private void Pin(DocumentModel doc, Portal portal)
    {
        _displayedPortalId = portal.Id;
        _autoPin = null;   // a saved portal takes over — drop any auto-pin coordinates so they can't go stale
        ActivePortalLabel = portal.Label;
        RenderPortalTarget(doc, portal.Target.Page, ResolveAnchorBlock(doc, portal.Target));
        InvalidatePortalMarkers();
    }

    /// <summary>Show a specific portal's target in the portal view — the "click a source marker" action.
    /// Pins it like a normal activation (stays until a different source is reached).</summary>
    public void ShowPortalTarget(Portal portal)
    {
        if (_controller.FocusedViewport?.Owner is not { } doc) return;
        Pin(doc, portal);
        RevealPortalPane();
    }

    /// <summary>Surface the docked Portals pane (no-op while popped out). Used when a source marker is
    /// clicked for a target that is ALREADY pinned — e.g. an auto pin, whose target never needs
    /// re-pinning — so the click still has the expected effect of bringing the preview into view.</summary>
    public void RevealPortalPane()
    {
        if (!IsPortalPoppedOut) ShowPane(SidePane.Portals);
    }

    /// <summary>Portal markers to draw/hit-test on the current page: a gutter marker per source line and
    /// a corner badge per target block, positioned from the stored normalized anchors (so they show even
    /// before a page is analysed). Cheap; no PDFium.</summary>
    public IReadOnlyList<PortalMarker> BuildPortalMarkers(Viewport? vp)
    {
        if (vp is null || _controller.FocusedViewport?.Owner is not { } doc || ActiveTab is not { } tab)
            return [];
        // The active auto pin is not a saved Portal, but BuildAutoPinPortal reifies it into a transient
        // one with Id == _displayedPortalId, so it flows through the same grouping below as any saved
        // portal: it gets a source/target marker, draws accented (its id matches), and naturally MERGES
        // into a saved portal's marker (shared anchor → one multi-portal chooser) instead of overlapping.
        var auto = _autoPin is not null ? BuildAutoPinPortal(doc) : null;
        var portals = auto is null ? tab.Portals.Portals : tab.Portals.Portals.Append(auto);
        if (tab.Portals.Portals.Count == 0 && auto is null)
            return [];

        // This surface's own page (a split pane / tear-off can be on a different page than Primary).
        int page = vp.CurrentPage;
        double pw = vp.PageWidth, ph = vp.PageHeight;
        if (pw <= 0 || ph <= 0) return [];

        var markers = new List<PortalMarker>();

        // One marker per distinct anchor on this page, grouping every portal that shares it (a source
        // line can link several targets; a target block several sources) so a multi-portal marker can
        // open a chooser. The endpoint differs only in which anchor is read; position and group key come
        // from the shared MarkerPos/MarkerKey helpers.
        void AddMarkers(PortalMarkerKind kind, Func<Portal, PortalAnchor> endpoint)
        {
            var groups = new Dictionary<(int, int), List<Portal>>();
            foreach (var p in portals)
            {
                var a = endpoint(p);
                if (a.Page != page) continue;
                var k = MarkerKey(kind, a);
                if (!groups.TryGetValue(k, out var list)) groups[k] = list = [];
                list.Add(p);
            }
            foreach (var list in groups.Values)
            {
                var (x, y) = MarkerPos(kind, endpoint(list[0]), pw, ph);
                markers.Add(new PortalMarker
                {
                    Kind = kind,
                    PageX = x,
                    PageY = y,
                    Portals = list,
                    IsActive = _displayedPortalId is not null && list.Any(p => p.Id == _displayedPortalId),
                });
            }
        }

        AddMarkers(PortalMarkerKind.Source, p => p.Source);
        AddMarkers(PortalMarkerKind.Target, p => p.Target);

        return markers;
    }

    /// <summary>Where a marker glyph sits in page space: a source gutter marker at the block's left
    /// edge by the line centre (block centre if the anchor has no line), a target badge at the block's
    /// top-right corner. Shared by saved-portal and auto-pin markers so the two cannot drift.</summary>
    private static (double X, double Y) MarkerPos(PortalMarkerKind kind, PortalAnchor a, double pw, double ph)
        => kind == PortalMarkerKind.Source
            ? (a.Nx * pw, (a.Ly >= 0 ? a.Ly : a.Ny + a.Nh / 2f) * ph)
            : ((a.Nx + a.Nw) * pw, a.Ny * ph);

    /// <summary>Anchor identity for grouping/deduping markers: a source is keyed by block + line (so
    /// several references on one line share a marker), a target by block alone.</summary>
    private static (int, int) MarkerKey(PortalMarkerKind kind, PortalAnchor a)
        => kind == PortalMarkerKind.Source ? (a.Block, a.Line) : (a.Block, 0);

    /// <summary>Build a transient (never-persisted) Portal for the currently displayed auto pin, so its
    /// on-page markers are interactive in the same way saved portals' are. Returns null if either
    /// endpoint's page is not analysed (so MakeAnchor cannot read the block). The Id equals
    /// <see cref="_displayedPortalId"/>.</summary>
    private Portal? BuildAutoPinPortal(DocumentModel doc)
    {
        if (_displayedPortalId is not { } id || _autoPin is not { } a)
            return null;
        if (!IsBlockResolvable(doc, a.SrcPage, a.SrcBlock) || !IsBlockResolvable(doc, a.TgtPage, a.TgtBlock))
            return null;
        return new Portal
        {
            Id = id,
            Label = ActivePortalLabel ?? "",
            Source = MakeAnchor(doc, a.SrcPage, a.SrcBlock, a.SrcLine),
            Target = MakeAnchor(doc, a.TgtPage, a.TgtBlock),
        };
    }

    private static string DefaultPortalLabel(DocumentModel doc, int page, int block)
    {
        if (!doc.TryGetAnalysis(page, out var analysis) || block < 0 || block >= analysis.Blocks.Count)
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
