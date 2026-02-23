using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class TabBarView : UserControl
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(66, 133, 244));
    private static readonly IBrush ActiveTabFg = Brushes.White;
    private static readonly IBrush InactiveTabBg = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
    private static readonly IBrush InactiveTabFg = new SolidColorBrush(Color.FromRgb(80, 80, 80));
    private static readonly IBrush SeparatorBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));

    private const double DragThreshold = 5.0;

    private int _dragIndex = -1;
    private bool _isDragging;
    private Point _dragStartPoint;
    private Border? _dropIndicator;

    public TabBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();

        // Use tunnelling events on the panel so we see pointer events before buttons consume them
        TabPanel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerReleasedEvent, OnPanelPointerReleased, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerCaptureLostEvent, OnPanelPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private MainWindowViewModel? _vm;

    private void WireViewModel()
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged += (_, _) => RebuildTabs();
            RebuildTabs();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveTabIndex))
            UpdateTabStyles();
    }

    private void RebuildTabs()
    {
        TabPanel.Children.Clear();
        if (_vm is null) return;

        for (int i = 0; i < _vm.Tabs.Count; i++)
        {
            var tab = _vm.Tabs[i];
            var container = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Tag = i };

            var tabContent = new DockPanel { Tag = tab };
            var indicator = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(2, 0),
                [DockPanel.DockProperty] = Dock.Bottom,
            };

            var tabBtn = new Button
            {
                Content = tab.Title,
                Tag = tab,
                Padding = new Thickness(12, 4),
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                MinHeight = 28,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(tabBtn, tab.FilePath);
            tabBtn.Click += OnTabClick;

            var closeBtn = new Button
            {
                Content = "\u00d7",
                Tag = tab,
                FontSize = 12,
                Padding = new Thickness(4, 0),
                MinWidth = 20,
                MinHeight = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-4, 0, 4, 0),
                CornerRadius = new CornerRadius(3),
                Opacity = 0.6,
            };
            ToolTip.SetTip(closeBtn, "Close tab (Ctrl+W)");
            closeBtn.Click += OnTabClose;

            tabContent.Children.Add(indicator);
            tabContent.Children.Add(tabBtn);

            container.Children.Add(tabContent);
            container.Children.Add(closeBtn);

            if (i < _vm.Tabs.Count - 1)
            {
                container.Children.Add(new Border
                {
                    Width = 1,
                    Background = SeparatorBrush,
                    Margin = new Thickness(2, 4),
                    VerticalAlignment = VerticalAlignment.Stretch,
                });
            }

            TabPanel.Children.Add(container);
        }

        UpdateTabStyles();
    }

    private void UpdateTabStyles()
    {
        if (_vm is null) return;

        int idx = 0;
        foreach (var child in TabPanel.Children)
        {
            if (child is not StackPanel container) continue;

            bool isActive = idx == _vm.ActiveTabIndex;

            if (container.Children[0] is DockPanel dock)
            {
                if (dock.Children[0] is Border indicator)
                    indicator.Background = isActive ? AccentBrush : Brushes.Transparent;

                if (dock.Children[1] is Button tabBtn)
                {
                    tabBtn.Background = isActive ? AccentBrush : InactiveTabBg;
                    tabBtn.Foreground = isActive ? ActiveTabFg : InactiveTabFg;
                    tabBtn.FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal;
                }
            }

            if (container.Children[1] is Button closeBtn)
                closeBtn.Opacity = isActive ? 1.0 : 0.5;

            idx++;
        }
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (_isDragging) return;
        if (sender is Button { Tag: TabViewModel tab } && _vm is { } vm)
        {
            int idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) vm.SelectTab(idx);
        }
    }

    private void OnTabClose(object? sender, RoutedEventArgs e)
    {
        if (_isDragging) return;
        if (sender is Button { Tag: TabViewModel tab } && _vm is { } vm)
        {
            int idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) vm.CloseTab(idx);
        }
    }

    private int HitTestTabIndex(Point posInPanel)
    {
        foreach (var child in TabPanel.Children)
        {
            if (child is not StackPanel container) continue;
            if (container.Tag is not int index) continue;
            if (container.Bounds.Contains(posInPanel))
                return index;
        }
        return -1;
    }

    private StackPanel? GetContainerByIndex(int index)
    {
        foreach (var child in TabPanel.Children)
        {
            if (child is StackPanel sp && sp.Tag is int i && i == index)
                return sp;
        }
        return null;
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TabPanel).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(TabPanel);
        int index = HitTestTabIndex(pos);
        if (index < 0) return;

        if (e.Source is Button { Content: "\u00d7" }) return;

        _dragIndex = index;
        _dragStartPoint = pos;
        _isDragging = false;
    }

    private void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragIndex < 0) return;

        var pos = e.GetPosition(TabPanel);

        if (!_isDragging)
        {
            if (Math.Abs(pos.X - _dragStartPoint.X) < DragThreshold)
                return;

            _isDragging = true;
            e.Pointer.Capture(TabPanel);

            var dragContainer = GetContainerByIndex(_dragIndex);
            if (dragContainer is not null)
                dragContainer.Opacity = 0.5;
        }

        e.Handled = true;

        int targetIndex = GetDropTargetIndex(pos);
        ShowDropIndicator(targetIndex);
    }

    private void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragIndex < 0) return;

        int fromIndex = _dragIndex;
        bool wasDragging = _isDragging;

        // Only release pointer capture if we were actually dragging;
        // otherwise the Button's internal capture is disrupted and Click never fires.
        ResetDragState(wasDragging ? e.Pointer : null);

        if (wasDragging && _vm is not null)
        {
            e.Handled = true;

            var pos = e.GetPosition(TabPanel);
            int targetIndex = GetDropTargetIndex(pos);

            // Convert insertion point to move-destination index
            if (targetIndex > fromIndex)
                targetIndex--;

            targetIndex = Math.Clamp(targetIndex, 0, _vm.Tabs.Count - 1);

            if (targetIndex != fromIndex)
                _vm.MoveTab(fromIndex, targetIndex);
        }
    }

    private void OnPanelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetDragState(null);
    }

    private void ResetDragState(IPointer? pointer)
    {
        var dragContainer = _dragIndex >= 0 ? GetContainerByIndex(_dragIndex) : null;
        if (dragContainer is not null)
            dragContainer.Opacity = 1.0;

        pointer?.Capture(null);
        RemoveDropIndicator();
        _dragIndex = -1;
        _isDragging = false;
    }

    private int GetDropTargetIndex(Point posInPanel)
    {
        int insertIndex = 0;
        foreach (var child in TabPanel.Children)
        {
            if (child is not StackPanel) continue;
            var bounds = child.Bounds;
            double midX = bounds.X + bounds.Width / 2;
            if (posInPanel.X > midX)
                insertIndex++;
            else
                break;
        }

        return Math.Clamp(insertIndex, 0, _vm?.Tabs.Count ?? 0);
    }

    private void ShowDropIndicator(int insertIndex)
    {
        RemoveDropIndicator();

        _dropIndicator = new Border
        {
            Width = 2,
            Background = AccentBrush,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(-1, 2, -1, 2),
            CornerRadius = new CornerRadius(1),
        };

        int panelChildIndex = 0;
        int tabsSeen = 0;
        foreach (var child in TabPanel.Children)
        {
            if (tabsSeen == insertIndex) break;
            if (child is StackPanel) tabsSeen++;
            panelChildIndex++;
        }

        panelChildIndex = Math.Clamp(panelChildIndex, 0, TabPanel.Children.Count);
        TabPanel.Children.Insert(panelChildIndex, _dropIndicator);
    }

    private void RemoveDropIndicator()
    {
        if (_dropIndicator is not null)
        {
            TabPanel.Children.Remove(_dropIndicator);
            _dropIndicator = null;
        }
    }
}
