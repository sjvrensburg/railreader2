using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public class ViewportPanel : Panel
{
    public MainWindowViewModel? ViewModel { get; set; }
    private bool _dragging;
    private Point _lastPos;
    private Point _pressPos;

    public ViewportPanel()
    {
        ClipToBounds = true;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(128, 128, 128));
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

        // Right-click: open radial menu or cancel tool
        if (point.Properties.IsRightButtonPressed && ViewModel is { } vm)
        {
            if (vm.IsAnnotating)
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

            if (ViewModel is { IsAnnotating: true } avm)
            {
                // Route to annotation tool — convert to page coords
                var pos = e.GetPosition(this);
                var (pageX, pageY) = ScreenToPage(pos);
                avm.HandleAnnotationPointerDown(pageX, pageY);
                _dragging = true;
                e.Handled = true;
            }
            else
            {
                _dragging = true;
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || ViewModel is null) return;

        var pos = e.GetPosition(this);

        if (ViewModel.IsAnnotating)
        {
            var (pageX, pageY) = ScreenToPage(pos);
            ViewModel.HandleAnnotationPointerMove(pageX, pageY);
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

            if (ViewModel.IsAnnotating)
            {
                var (pageX, pageY) = ScreenToPage(pos);
                ViewModel.HandleAnnotationPointerUp(pageX, pageY);
            }
            else
            {
                double dist = Math.Sqrt(Math.Pow(pos.X - _pressPos.X, 2) + Math.Pow(pos.Y - _pressPos.Y, 2));
                if (dist < 5.0)
                    ViewModel.HandleClick(pos.X, pos.Y);
            }
        }
        _dragging = false;
        e.Handled = true;
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
