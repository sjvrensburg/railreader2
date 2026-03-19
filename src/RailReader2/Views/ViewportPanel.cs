using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RailReader.Core.Models;
using RailReader2.ViewModels;

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

    public ViewportPanel()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(128, 128, 128));
    }

    /// <summary>
    /// Update the cursor to reflect the active annotation tool.
    /// Called when the ActiveTool property changes.
    /// </summary>
    public void UpdateAnnotationCursor()
    {
        var tool = ViewModel?.ActiveTool ?? AnnotationTool.None;
        if (tool == _lastCursorTool) return;
        _lastCursorTool = tool;

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

        // Right-click: context menu for text selection, radial menu, or cancel tool
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
                var pos = e.GetPosition(this);
                vm.OpenRadialMenu(pos.X, pos.Y);
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
        if (!_dragging || ViewModel is null) return;

        // If left button was released outside the viewport (e.g. on toolbar),
        // we never got OnPointerReleased — cancel the drag now.
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            _browseAnnotationDrag = false;
            return;
        }

        var pos = e.GetPosition(this);

        if (ViewModel.IsAnnotating)
        {
            var (pageX, pageY) = ScreenToPage(pos);
            ViewModel.HandleAnnotationPointerMove(pageX, pageY);
        }
        else if (_browseAnnotationDrag)
        {
            var (pageX, pageY) = ScreenToPage(pos);
            ViewModel.HandleBrowsePointerMove((float)pageX, (float)pageY);
        }
        else
        {
            double dx = pos.X - _lastPos.X;
            double dy = pos.Y - _lastPos.Y;
            ViewModel.HandlePan(dx, dy);
        }

        _lastPos = pos;
        e.Handled = true;
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

    private bool IsClick(Point pos)
    {
        double dx = pos.X - _pressPos.X;
        double dy = pos.Y - _pressPos.Y;
        return dx * dx + dy * dy < 25.0; // 5px threshold squared
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
