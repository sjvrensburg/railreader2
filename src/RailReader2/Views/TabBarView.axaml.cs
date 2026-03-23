using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class TabBarView : UserControl
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(66, 133, 244));
    private static readonly IBrush[] LinkBrushes =
    [
        new SolidColorBrush(Color.Parse("#4A9EFF")),
        new SolidColorBrush(Color.Parse("#FB923C")),
        new SolidColorBrush(Color.Parse("#00B4C5")),
        new SolidColorBrush(Color.Parse("#E879A8")),
    ];
    private static readonly IBrush ActiveTabFg = Brushes.White;
    private static readonly IBrush InactiveTabBg = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
    private static readonly IBrush InactiveTabFgLight = new SolidColorBrush(Color.FromRgb(80, 80, 80));
    private static readonly IBrush InactiveTabFgDark = new SolidColorBrush(Color.FromRgb(180, 180, 180));
    private static readonly IBrush SeparatorBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));

    private const double DragThreshold = 5.0;

    private int _dragIndex = -1;
    private bool _isDragging;
    private Point _dragStartPoint;
    private Border? _dropIndicator;

    private const double MaxTabWidth = 200;

    public TabBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
        ActualThemeVariantChanged += (_, _) => UpdateTabStyles();

        // Use tunnelling events on the panel so we see pointer events before buttons consume them
        TabPanel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerReleasedEvent, OnPanelPointerReleased, RoutingStrategies.Tunnel);
        TabPanel.AddHandler(PointerCaptureLostEvent, OnPanelPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // Horizontal scroll on mouse wheel over tab bar
        TabScroller.AddHandler(PointerWheelChangedEvent, OnTabScrollerWheel, RoutingStrategies.Tunnel);

        // Update overflow button visibility when size changes
        TabScroller.SizeChanged += (_, _) => UpdateOverflowButton();
        TabPanel.SizeChanged += (_, _) => UpdateOverflowButton();
    }

    private MainWindowViewModel? _vm;
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _collectionHandler;
    private readonly List<(TabViewModel Tab, PropertyChangedEventHandler Handler)> _tabSubscriptions = [];

    private void WireViewModel()
    {
        // Unsubscribe from old VM
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            if (_collectionHandler is not null)
                _vm.Tabs.CollectionChanged -= _collectionHandler;
        }
        ClearTabSubscriptions();

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _collectionHandler = (_, _) => RebuildTabs();
            _vm.Tabs.CollectionChanged += _collectionHandler;
            RebuildTabs();
        }
    }

    private void ClearTabSubscriptions()
    {
        foreach (var (tab, handler) in _tabSubscriptions)
            tab.PropertyChanged -= handler;
        _tabSubscriptions.Clear();
    }

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
        // Convert vertical wheel to horizontal scroll
        double delta = e.Delta.Y * 60;
        TabScroller.Offset = TabScroller.Offset.WithX(TabScroller.Offset.X - delta);
        e.Handled = true;
    }

    private void UpdateOverflowButton()
    {
        bool overflow = TabPanel.Bounds.Width > TabScroller.Bounds.Width + 1;
        OverflowButton.IsVisible = overflow;
    }

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
        // Scroll so the active tab is visible
        double left = container.Bounds.X;
        double right = left + container.Bounds.Width;
        double viewLeft = TabScroller.Offset.X;
        double viewRight = viewLeft + TabScroller.Bounds.Width;
        if (left < viewLeft)
            TabScroller.Offset = TabScroller.Offset.WithX(left);
        else if (right > viewRight)
            TabScroller.Offset = TabScroller.Offset.WithX(right - TabScroller.Bounds.Width);
    }

    private void RebuildTabs()
    {
        ClearTabSubscriptions();
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

            // Build tab button content: optional link indicator + title
            var tabContentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

            // Link indicator (chain icon + colored dot) — hidden when not linked
            var linkDot = new Border
            {
                Name = "LinkDot",
                Width = 8, Height = 8,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var linkIcon = new TextBlock
            {
                Text = "\U0001F517", // chain link emoji
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            };
            var linkPanel = new StackPanel
            {
                Name = "LinkPanel",
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                IsVisible = tab.IsLinked,
            };
            linkPanel.Children.Add(linkIcon);
            linkPanel.Children.Add(linkDot);

            var titleText = new TextBlock
            {
                Text = tab.Title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            tabContentPanel.Children.Add(linkPanel);
            tabContentPanel.Children.Add(titleText);

            var tabBtn = new Button
            {
                Content = tabContentPanel,
                Tag = tab,
                Padding = new Thickness(12, 4),
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                MinHeight = 28,
                MaxWidth = MaxTabWidth,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(tabBtn, tab.FilePath);
            tabBtn.Click += OnTabClick;

            // Subscribe to link changes to update the visual
            PropertyChangedEventHandler linkHandler = (_, args) =>
            {
                if (args.PropertyName is nameof(TabViewModel.LinkGroupId) or nameof(TabViewModel.IsLinked))
                {
                    linkPanel.IsVisible = tab.IsLinked;
                    UpdateLinkDotColor(linkDot, tab);
                }
            };
            tab.PropertyChanged += linkHandler;
            _tabSubscriptions.Add((tab, linkHandler));
            UpdateLinkDotColor(linkDot, tab);

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
                    var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
                    var inactiveFg = isDark ? InactiveTabFgDark : InactiveTabFgLight;
                    tabBtn.Background = isActive ? AccentBrush : InactiveTabBg;
                    tabBtn.Foreground = isActive ? ActiveTabFg : inactiveFg;
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

    /// <summary>Returns all tab indices in the same link group as the given index, or just the index itself if unlinked.</summary>
    private List<int> GetGroupIndices(int index)
    {
        if (_vm is null || index < 0 || index >= _vm.Tabs.Count)
            return [index];
        var gid = _vm.Tabs[index].State.LinkGroupId;
        if (!gid.HasValue) return [index];
        var indices = new List<int>();
        for (int i = 0; i < _vm.Tabs.Count; i++)
            if (_vm.Tabs[i].State.LinkGroupId == gid)
                indices.Add(i);
        return indices;
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

        if (e.Source is Button { Content: "\u00d7" }) return;

        _dragIndex = idx;
        _dragStartPoint = pos;
        _isDragging = false;
    }

    private void UpdateLinkDotColor(Border dot, TabViewModel tab)
    {
        if (_vm is not null && tab.LinkGroupId is { } gid)
            dot.Background = LinkBrushes[_vm.GetLinkGroupColorIndex(gid)];
        else
            dot.Background = Brushes.Transparent;
    }

    private void ShowTabContextMenu(int tabIndex)
    {
        if (_vm is not { } vm) return;

        var menu = new ContextMenu();

        var duplicateItem = new MenuItem { Header = "Duplicate Tab" };
        duplicateItem.Click += (_, _) =>
        {
            vm.SelectTab(tabIndex);
            vm.DuplicateTab();
        };
        menu.Items.Add(duplicateItem);

        var duplicateLinkedItem = new MenuItem { Header = "Duplicate Tab (Linked)" };
        duplicateLinkedItem.Click += (_, _) =>
        {
            vm.SelectTab(tabIndex);
            vm.DuplicateTabLinked();
        };
        menu.Items.Add(duplicateLinkedItem);

        // "Link to..." submenu with candidates
        var candidates = vm.GetLinkCandidates(tabIndex);
        if (candidates.Count > 0)
        {
            var linkToItem = new MenuItem { Header = "Link to..." };
            foreach (var (idx, title) in candidates)
            {
                int targetIdx = idx;
                var candidate = new MenuItem { Header = title };
                candidate.Click += (_, _) => vm.LinkTabTo(tabIndex, targetIdx);
                linkToItem.Items.Add(candidate);
            }
            menu.Items.Add(linkToItem);
        }

        // Unlink option (only if linked)
        if (tabIndex < vm.Tabs.Count && vm.Tabs[tabIndex].IsLinked)
        {
            var unlinkItem = new MenuItem { Header = "Unlink Tab" };
            unlinkItem.Click += (_, _) => vm.UnlinkTab(tabIndex);
            menu.Items.Add(unlinkItem);
        }

        menu.Items.Add(new Separator());

        var detachItem = new MenuItem { Header = "Detach Tab" };
        detachItem.Click += (_, _) => vm.DetachTab(tabIndex);
        menu.Items.Add(detachItem);

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

            // Dim all group members when dragging a linked tab
            foreach (int idx in GetGroupIndices(_dragIndex))
            {
                var container = GetContainerByIndex(idx);
                if (container is not null)
                    container.Opacity = 0.5;
            }
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
        // Restore opacity on all group members
        if (_dragIndex >= 0)
        {
            foreach (int idx in GetGroupIndices(_dragIndex))
            {
                var container = GetContainerByIndex(idx);
                if (container is not null)
                    container.Opacity = 1.0;
            }
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
