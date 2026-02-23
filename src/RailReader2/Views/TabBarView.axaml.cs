using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class TabBarView : UserControl
{
    private static readonly IBrush ActiveTabBg = new SolidColorBrush(Color.FromRgb(66, 133, 244));
    private static readonly IBrush ActiveTabFg = Brushes.White;
    private static readonly IBrush InactiveTabBg = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
    private static readonly IBrush InactiveTabFg = new SolidColorBrush(Color.FromRgb(80, 80, 80));
    private static readonly IBrush ActiveIndicator = new SolidColorBrush(Color.FromRgb(66, 133, 244));

    public TabBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
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

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
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
            var container = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };

            // Tab content panel with bottom indicator
            var tabContent = new DockPanel { Tag = tab };

            // Bottom indicator bar
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
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
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
                    Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
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

            // Find the DockPanel (tabContent) -> indicator + button
            if (container.Children[0] is DockPanel dock)
            {
                // First child of DockPanel is the indicator
                if (dock.Children[0] is Border indicator)
                    indicator.Background = isActive ? ActiveIndicator : Brushes.Transparent;

                // Second child is the tab button
                if (dock.Children[1] is Button tabBtn)
                {
                    tabBtn.Background = isActive ? ActiveTabBg : InactiveTabBg;
                    tabBtn.Foreground = isActive ? ActiveTabFg : InactiveTabFg;
                    tabBtn.FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal;
                }
            }

            // Close button opacity
            if (container.Children[1] is Button closeBtn)
                closeBtn.Opacity = isActive ? 1.0 : 0.5;

            idx++;
        }
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabViewModel tab } && _vm is { } vm)
        {
            int idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) vm.SelectTab(idx);
        }
    }

    private void OnTabClose(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabViewModel tab } && _vm is { } vm)
        {
            int idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) vm.CloseTab(idx);
        }
    }
}
