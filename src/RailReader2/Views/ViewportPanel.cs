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

    private bool IsClick(Point pos)
    {
        double dx = pos.X - _pressPos.X;
        double dy = pos.Y - _pressPos.Y;
        return dx * dx + dy * dy < ClickThresholdSq;
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
