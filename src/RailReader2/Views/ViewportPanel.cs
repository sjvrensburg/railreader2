using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RailReader.Core.Models;
using RailReader2.ViewModels;
using static RailReader.Core.Services.VlmService;

namespace RailReader2.Views;

public class ViewportPanel : Panel
{
    public MainWindowViewModel? ViewModel { get; set; }
    private bool _dragging;
    private Point _lastPos;
    private Point _pressPos;

    // Browse-mode annotation drag state
    private bool _browseAnnotationDrag;
    private int _pressClickCount;

    // Track the last tool so we only update cursor when it changes
    private AnnotationTool _lastCursorTool = AnnotationTool.None;

    // Last position at which a link hover hit-test ran. Pointer moves arrive at
    // ~60–125Hz; gating the hit-test on a small movement delta avoids running it
    // (a per-page link scan) on every event without a perceptible cursor lag.
    private Point _lastLinkHitTestPos = new(double.NegativeInfinity, double.NegativeInfinity);
    private const double LinkHitTestMinMoveSq = 9.0; // 3px squared

    public ViewportPanel()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(128, 128, 128));
    }

    // Expose the viewport's live state (page / zoom / rail mode / current line text) to the platform
    // accessibility tree, since the page itself is a GPU canvas the a11y stack can't otherwise see.
    private DocumentViewportAutomationPeer? _automationPeer;

    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => _automationPeer = new DocumentViewportAutomationPeer(this);

    /// <summary>Tell the accessibility peer (if an AT-SPI/UIA client is connected) to re-evaluate the
    /// document state and announce page / rail-line / mode changes. No-op when nothing is listening, so
    /// it is cheap to call from the render path.</summary>
    public void NotifyAccessibilityStateChanged() => _automationPeer?.NotifyStateChanged();

    /// <summary>
    /// Update the cursor to reflect the active annotation tool.
    /// Called when the ActiveTool property changes.
    /// </summary>
    public void UpdateAnnotationCursor()
    {
        var tool = ViewModel?.ActiveTool ?? AnnotationTool.None;
        if (tool == _lastCursorTool) return;
        _lastCursorTool = tool;
        _showingLinkCursor = false;

        Cursor = tool switch
        {
            AnnotationTool.Highlight or AnnotationTool.Pen
                or AnnotationTool.Rectangle or AnnotationTool.TextNote
                => new Cursor(StandardCursorType.Cross),
            AnnotationTool.Eraser => new Cursor(StandardCursorType.No),
            AnnotationTool.TextSelect => new Cursor(StandardCursorType.Ibeam),
            _ => null, // inherit default from parent
        };
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ViewModel is null) return;

        double scrollY = e.Delta.Y * 30.0;
        var pos = e.GetPosition(this);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        ViewModel.HandleZoom(scrollY, pos.X, pos.Y, ctrl);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);

        // Right-click: text-selection menu, cancel an in-progress tool, or the
        // viewport context menu (block actions + Annotation Mode toggle).
        if (point.Properties.IsRightButtonPressed && ViewModel is { } vm)
        {
            if (vm.ActiveTool == AnnotationTool.TextSelect && vm.SelectedText is not null)
            {
                ShowTextSelectionContextMenu();
            }
            else if (vm.IsAnnotating)
            {
                vm.CancelAnnotationTool();
            }
            else
            {
                var (pageX, pageY) = ScreenToPage(e.GetPosition(this));
                ShowViewportContextMenu(vm, pageX, pageY);
            }
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _pressPos = e.GetPosition(this);
            _lastPos = _pressPos;
            _dragging = true;
            _browseAnnotationDrag = false;
            _pressClickCount = e.ClickCount;
            e.Handled = true;

            var (pageX, pageY) = ScreenToPage(_pressPos);
            if (ViewModel!.IsAnnotating)
            {
                ViewModel.HandleAnnotationPointerDown(pageX, pageY);
            }
            else
            {
                if (ViewModel.HandleBrowsePointerDown((float)pageX, (float)pageY))
                    _browseAnnotationDrag = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ViewModel is null) return;

        if (!_dragging)
        {
            // Not dragging — update cursor for link hover (only in browse mode)
            if (!ViewModel.IsAnnotating)
            {
                var pos = e.GetPosition(this);
                double mdx = pos.X - _lastLinkHitTestPos.X;
                double mdy = pos.Y - _lastLinkHitTestPos.Y;
                if (mdx * mdx + mdy * mdy >= LinkHitTestMinMoveSq)
                {
                    _lastLinkHitTestPos = pos;
                    var (pageX, pageY) = ScreenToPage(pos);
                    bool overLink = ViewModel.IsOverLink(pageX, pageY);
                    UpdateLinkCursor(overLink);
                }
            }
            return;
        }

        // If left button was released outside the viewport (e.g. on toolbar),
        // we never got OnPointerReleased — cancel the drag now.
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            _browseAnnotationDrag = false;
            return;
        }

        var dragPos = e.GetPosition(this);

        if (ViewModel.IsAnnotating)
        {
            var (pageX, pageY) = ScreenToPage(dragPos);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            ViewModel.HandleAnnotationPointerMove(pageX, pageY, shift);
        }
        else if (_browseAnnotationDrag)
        {
            var (pageX, pageY) = ScreenToPage(dragPos);
            ViewModel.HandleBrowsePointerMove((float)pageX, (float)pageY);
        }
        else
        {
            double dx = dragPos.X - _lastPos.X;
            double dy = dragPos.Y - _lastPos.Y;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            ViewModel.HandlePan(dx, dy, ctrl);
        }

        _lastPos = dragPos;
        e.Handled = true;
    }

    private bool _showingLinkCursor;

    private void UpdateLinkCursor(bool overLink)
    {
        if (overLink == _showingLinkCursor) return;
        _showingLinkCursor = overLink;

        // Only override cursor when no annotation tool is active
        if (_lastCursorTool != AnnotationTool.None) return;

        Cursor = overLink ? new Cursor(StandardCursorType.Hand) : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging && ViewModel is not null)
        {
            var pos = e.GetPosition(this);
            bool isClick = IsClick(pos);

            if (ViewModel.IsAnnotating)
            {
                var (pageX, pageY) = ScreenToPage(pos);
                ViewModel.HandleAnnotationPointerUp(pageX, pageY);
            }
            else if (_browseAnnotationDrag)
            {
                var (pageX, pageY) = ScreenToPage(pos);
                if (isClick)
                    ViewModel.HandleBrowseClick((float)pageX, (float)pageY, _pressClickCount >= 2);
                else
                    ViewModel.HandleBrowsePointerUp((float)pageX, (float)pageY);
            }
            else if (isClick && _pressClickCount >= 2 && TryFrameBlockAt(pos))
            {
                // Double-click on a detected block → smooth zoom into rail mode at its start.
            }
            else if (isClick)
            {
                ViewModel.HandleClick(pos.X, pos.Y);
            }
        }
        _dragging = false;
        _browseAnnotationDrag = false;
        e.Handled = true;
    }

    private void ShowViewportContextMenu(MainWindowViewModel vm, double pageX, double pageY)
    {
        var menu = new ContextMenu();

        // Block actions (Copy as LaTeX / Markdown / Description / Image) when over a detected block.
        if (vm.FindBlockAt(pageX, pageY) is { } block)
        {
            var latexItem = new MenuItem { Header = "Copy as LaTeX" };
            latexItem.Click += (_, _) => vm.FireAndForget(vm.CopyBlockWithAction(block, BlockAction.LaTeX), nameof(vm.CopyBlockWithAction));
            menu.Items.Add(latexItem);

            var markdownItem = new MenuItem { Header = "Copy as Markdown" };
            markdownItem.Click += (_, _) => vm.FireAndForget(vm.CopyBlockWithAction(block, BlockAction.Markdown), nameof(vm.CopyBlockWithAction));
            menu.Items.Add(markdownItem);

            var descItem = new MenuItem { Header = "Copy Description" };
            descItem.Click += (_, _) => vm.FireAndForget(vm.CopyBlockWithAction(block, BlockAction.Description), nameof(vm.CopyBlockWithAction));
            menu.Items.Add(descItem);

            var imageItem = new MenuItem { Header = "Copy Image" };
            imageItem.Click += (_, _) => vm.FireAndForget(vm.CopyBlockAsImage(block), nameof(vm.CopyBlockAsImage));
            menu.Items.Add(imageItem);

            // Portals authoring path B: link this detected block to a reading position. The block is
            // on the current page; FindBlockIndexAt shares analysis.Blocks' index space. Actions that
            // capture the current reading position are disabled (with an explanatory tooltip) unless
            // rail mode is active, since there is no reading position outside it.
            menu.Items.Add(new Separator());
            int blockIndex = vm.FindBlockIndexAt(pageX, pageY);
            int curPage = vm.Controller.ActiveDocument?.CurrentPage ?? 0;
            bool canCapture = vm.CanCaptureReadingPosition;
            const string railHint = "Rail-read (zoom in) the text that refers to this block first — "
                + "the portal keeps this block in view while you read that paragraph.";

            // Quick peek: show this block in the floating pop-out window now, no saved link, no rail
            // mode needed. It leaves the docked Portals preview free to keep tracking saved portals.
            var peekItem = new MenuItem { Header = "Open in Portal (Temporary)" };
            ToolTip.SetTip(peekItem, "Show this block in the floating portal window now, without "
                + "creating a saved link. It closes (or reverts to tracking) once you read on.");
            peekItem.Click += (_, _) => vm.ShowBlockInPortal(curPage, blockIndex);
            menu.Items.Add(peekItem);

            // Primary one-shot: you're reading the referencing paragraph, right-click the figure.
            var keepInViewItem = new MenuItem
            {
                Header = "Create Portal — Keep This Block In View While Reading",
                IsEnabled = canCapture,
            };
            ToolTip.SetTip(keepInViewItem, canCapture
                ? "Link the paragraph you're rail-reading to this block, so it stays visible in the "
                  + "Portals panel as you read."
                : railHint);
            keepInViewItem.Click += (_, _) => vm.CreatePortal(curPage, blockIndex);
            menu.Items.Add(keepInViewItem);

            // Two-step alternative: stash this block as the target now, link the source later.
            var setTargetItem = new MenuItem { Header = "Set as Portal Target (link later)" };
            ToolTip.SetTip(setTargetItem,
                "Remember this block as a portal target. Then rail-read the text that refers to it and "
                + "choose “Link target to current paragraph”.");
            setTargetItem.Click += (_, _) => vm.SetPortalTarget(curPage, blockIndex);
            menu.Items.Add(setTargetItem);

            var linkFromPosItem = new MenuItem
            {
                Header = "Link Target to Current Paragraph",
                IsEnabled = canCapture && vm.HasPendingPortalTarget,
            };
            ToolTip.SetTip(linkFromPosItem, !vm.HasPendingPortalTarget
                ? "First choose “Set as Portal Target (link later)” on the block you want to keep in view."
                : canCapture
                    ? "Create a portal from the block you marked as target to the paragraph you're now reading."
                    : railHint);
            linkFromPosItem.Click += (_, _) => vm.LinkFromCurrentPosition();
            menu.Items.Add(linkFromPosItem);

            menu.Items.Add(new Separator());
        }

        var annModeItem = new MenuItem
        {
            Header = vm.IsAnnotationMode ? "Exit Annotation Mode" : "Annotation Mode",
        };
        annModeItem.Click += (_, _) => vm.ToggleAnnotationMode();
        menu.Items.Add(annModeItem);

        menu.Open(this);
    }

    private void ShowTextSelectionContextMenu()
    {
        if (ViewModel is not { } vm || vm.SelectedText is null) return;

        var menu = new ContextMenu();

        var copyItem = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copyItem.Click += (_, _) => vm.CopySelectedText();
        menu.Items.Add(copyItem);

        var searchItem = new MenuItem { Header = "Search for Selection" };
        searchItem.Click += (_, _) => vm.SearchForSelectedText();
        menu.Items.Add(searchItem);

        menu.Open(this);
    }

    private const double ClickThresholdSq = 25.0; // 5px squared

    // Double-click frame zoom duration (ms) — gentler than the native 180ms zoom.
    private const double FrameZoomDurationMs = 320.0;

    private bool IsClick(Point pos)
    {
        double dx = pos.X - _pressPos.X;
        double dy = pos.Y - _pressPos.Y;
        return dx * dx + dy * dy < ClickThresholdSq;
    }

    /// <summary>Double-click target: smoothly zoom into rail mode at the start of the detected block
    /// under <paramref name="screenPos"/> (a centred frame for non-navigable figures/tables — the
    /// fallback in <c>SmoothlyFrameBlock</c>). The mouse-driven way to zoom onto a specific block,
    /// which has no keyboard equivalent. Returns false (so the normal click handler runs) when no
    /// block is there.</summary>
    private bool TryFrameBlockAt(Point screenPos)
    {
        if (ViewModel is not { } vm) return false;
        var (pageX, pageY) = ScreenToPage(screenPos);
        int index = vm.FindBlockIndexAt(pageX, pageY);
        if (index < 0) return false;
        // A gentler ease than the native 180ms zoom — a double-click is a deliberate framing
        // gesture, so it reads as intentional rather than a snap.
        return vm.SmoothlyFrameBlock(index, durationMs: FrameZoomDurationMs);
    }

    private (double PageX, double PageY) ScreenToPage(Point screenPos)
    {
        if (ViewModel?.ActiveTab is not { } tab)
            return (screenPos.X, screenPos.Y);
        double pageX = (screenPos.X - tab.Camera.OffsetX) / tab.Camera.Zoom;
        double pageY = (screenPos.Y - tab.Camera.OffsetY) / tab.Camera.Zoom;
        return (pageX, pageY);
    }
}
