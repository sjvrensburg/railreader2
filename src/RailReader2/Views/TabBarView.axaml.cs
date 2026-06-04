using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RailReader2.Controls;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class TabBarView : UserControl
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(66, 133, 244));

    private const double DragThreshold = 5.0;
    private const double MaxTabWidth = 200;

    private int _dragIndex = -1;
    private bool _isDragging;
    private Point _dragStartPoint;
    private Border? _dropIndicator;

    public TabBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();

        // Tunnelling pointer events on the panel so drag detection sees them before the
        // tab cells / close buttons consume them.
        TabPanel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerReleasedEvent, OnPanelPointerReleased, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerCaptureLostEvent, OnPanelPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // Horizontal scroll on mouse wheel over the tab bar
        TabScroller.AddHandler(PointerWheelChangedEvent, OnTabScrollerWheel, RoutingStrategies.Tunnel);

        TabScroller.SizeChanged += (_, _) => UpdateOverflowButton();
        TabPanel.SizeChanged += (_, _) => UpdateOverflowButton();
    }

    private MainWindowViewModel? _vm;

    private void WireViewModel()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Tabs.CollectionChanged += OnTabsCollectionChanged;
            RebuildTabs();
        }
    }

    private void OnTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildTabs();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveTabIndex))
        {
            UpdateTabStyles();
            ScrollActiveTabIntoView();
        }
    }

    private void OnTabScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        double delta = e.Delta.Y * 60;
        TabScroller.Offset = TabScroller.Offset.WithX(TabScroller.Offset.X - delta);
        e.Handled = true;
    }

    private void UpdateOverflowButton()
        => OverflowButton.IsVisible = TabPanel.Bounds.Width > TabScroller.Bounds.Width + 1;

    private void OnOverflowClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu();
        for (int i = 0; i < _vm.Tabs.Count; i++)
        {
            int idx = i;
            var tab = _vm.Tabs[i];
            var item = new MenuItem
            {
                Header = tab.Title,
                FontWeight = i == _vm.ActiveTabIndex ? FontWeight.Bold : FontWeight.Normal,
            };
            item.Click += (_, _) => _vm.SelectTab(idx);
            menu.Items.Add(item);
        }
        menu.Open(OverflowButton);
    }

    private void ScrollActiveTabIntoView()
    {
        if (_vm is null) return;
        var container = GetContainerByIndex(_vm.ActiveTabIndex);
        if (container is null) return;
        double left = container.Bounds.X;
        double right = left + container.Bounds.Width;
        double viewLeft = TabScroller.Offset.X;
        double viewRight = viewLeft + TabScroller.Bounds.Width;
        if (left < viewLeft)
            TabScroller.Offset = TabScroller.Offset.WithX(left);
        else if (right > viewRight)
            TabScroller.Offset = TabScroller.Offset.WithX(right - TabScroller.Bounds.Width);
    }

    private Geometry? CloseGeometry()
        => Application.Current?.TryGetResource("IconClose", null, out var g) == true ? g as Geometry : null;

    private void RebuildTabs()
    {
        TabPanel.Children.Clear();
        if (_vm is null) return;

        var closeGeo = CloseGeometry();

        for (int i = 0; i < _vm.Tabs.Count; i++)
        {
            var tab = _vm.Tabs[i];

            var indicator = new Border { Classes = { "tabIndicator" }, [DockPanel.DockProperty] = Dock.Top };

            var closeBtn = new Button
            {
                Classes = { "tabClose" },
                Tag = tab,
                // No fixed FontSize, so the icon scales with the UI; SizeFactor keeps it daintier than the title.
                Content = new Icon { Data = closeGeo, SizeFactor = 0.9 },
                Margin = new Thickness(0, 0, 4, 0),
                [DockPanel.DockProperty] = Dock.Right,
            };
            ToolTip.SetTip(closeBtn, "Close tab (Ctrl+W)");
            Avalonia.Automation.AutomationProperties.SetName(closeBtn, $"Close {tab.Title}");
            closeBtn.Click += OnTabClose;

            var titleText = new TextBlock
            {
                Classes = { "tabTitle" },
                Text = tab.Title,
                Margin = new Thickness(10, 5, 4, 5),
            };

            var dock = new DockPanel();
            dock.Children.Add(indicator);
            dock.Children.Add(closeBtn);
            dock.Children.Add(titleText);

            var container = new Border
            {
                Classes = { "tab" },
                Tag = i,
                MaxWidth = MaxTabWidth,
                MinHeight = 28,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = dock,
            };
            ToolTip.SetTip(container, tab.FilePath);

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
            if (child is not Border container || container.Tag is not int) continue;
            bool isActive = idx == _vm.ActiveTabIndex;
            container.Classes.Remove("active");
            if (isActive) container.Classes.Add("active");
            idx++;
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
            if (child is not Border container || container.Tag is not int index) continue;
            if (container.Bounds.Contains(posInPanel))
                return index;
        }
        return -1;
    }

    private Border? GetContainerByIndex(int index)
    {
        foreach (var child in TabPanel.Children)
        {
            if (child is Border b && b.Tag is int i && i == index)
                return b;
        }
        return null;
    }

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(TabPanel);
        var pos = e.GetPosition(TabPanel);

        if (point.Properties.IsRightButtonPressed)
        {
            int index = HitTestTabIndex(pos);
            if (index >= 0 && _vm is not null)
                ShowTabContextMenu(index);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        int idx = HitTestTabIndex(pos);
        if (idx < 0) return;

        // Don't start a drag (or a click-select on release) when pressing the close button.
        // The Icon inside the button is hit-test-invisible, so e.Source is the button itself.
        if (e.Source is Button cb && cb.Classes.Contains("tabClose")) return;

        _dragIndex = idx;
        _dragStartPoint = pos;
        _isDragging = false;
    }

    private void ShowTabContextMenu(int tabIndex)
    {
        if (_vm is not { } vm) return;

        var menu = new ContextMenu();

        var duplicateItem = new MenuItem { Header = "Duplicate Tab" };
        duplicateItem.Click += (_, _) =>
        {
            vm.SelectTab(tabIndex);
            vm.FireAndForget(vm.DuplicateTab(), nameof(vm.DuplicateTab));
        };
        menu.Items.Add(duplicateItem);

        menu.Items.Add(new Separator());

        var closeItem = new MenuItem { Header = "Close Tab" };
        closeItem.Click += (_, _) => vm.CloseTab(tabIndex);
        menu.Items.Add(closeItem);

        menu.Open(TabPanel);
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

            var container = GetContainerByIndex(_dragIndex);
            if (container is not null)
                container.Opacity = 0.5;
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

        ResetDragState(wasDragging ? e.Pointer : null);

        if (_vm is null) return;

        if (wasDragging)
        {
            e.Handled = true;

            var pos = e.GetPosition(TabPanel);
            int targetIndex = GetDropTargetIndex(pos);
            if (targetIndex > fromIndex)
                targetIndex--;
            targetIndex = Math.Clamp(targetIndex, 0, _vm.Tabs.Count - 1);

            if (targetIndex != fromIndex)
                _vm.MoveTab(fromIndex, targetIndex);
        }
        else if (fromIndex >= 0 && fromIndex < _vm.Tabs.Count)
        {
            // Simple click on the tab cell selects it.
            _vm.SelectTab(fromIndex);
        }
    }

    private void OnPanelPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => ResetDragState(null);

    private void ResetDragState(IPointer? pointer)
    {
        if (_dragIndex >= 0)
        {
            var container = GetContainerByIndex(_dragIndex);
            if (container is not null)
                container.Opacity = 1.0;
        }

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
            if (child is not Border b || b.Tag is not int) continue;
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
            if (child is Border b && b.Tag is int) tabsSeen++;
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
