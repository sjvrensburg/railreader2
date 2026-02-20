using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateStatus();
    }

    private TabViewModel? _subscribedTab;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainWindowViewModel.ActiveTab) or
                    nameof(MainWindowViewModel.ActiveTabIndex))
                {
                    SubscribeToTab(vm.ActiveTab);
                    UpdateStatus();
                }
            };
            SubscribeToTab(vm.ActiveTab);
        }
    }

    private void SubscribeToTab(TabViewModel? tab)
    {
        if (_subscribedTab is not null)
            _subscribedTab.PropertyChanged -= OnTabPropertyChanged;
        _subscribedTab = tab;
        if (tab is not null)
            tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TabViewModel.CurrentPage) or nameof(TabViewModel.PendingRailSetup))
            UpdateStatus();
    }

    private static Button MakeNavButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        var btn = new Button
        {
            Content = text,
            Padding = new Avalonia.Thickness(6, 0),
            MinWidth = 0,
        };
        btn.Click += handler;
        return btn;
    }

    private void UpdateStatus()
    {
        StatusPanel.Children.Clear();
        var vm = DataContext as MainWindowViewModel;
        var tab = vm?.ActiveTab;
        if (tab is null)
        {
            StatusPanel.Children.Add(new TextBlock { Text = "No document open" });
            return;
        }

        int zoomPct = (int)Math.Round(tab.Camera.Zoom * 100);
        StatusPanel.Children.Add(MakeNavButton("◀", (_, _) =>
        { if (vm?.ActiveTab is { } t) vm.GoToPage(t.CurrentPage - 1); }));
        StatusPanel.Children.Add(new TextBlock
        {
            Text = $"Page {tab.CurrentPage + 1}/{tab.PageCount}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        StatusPanel.Children.Add(MakeNavButton("▶", (_, _) =>
        { if (vm?.ActiveTab is { } t) vm.GoToPage(t.CurrentPage + 1); }));
        StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });
        StatusPanel.Children.Add(new TextBlock { Text = $"Zoom: {zoomPct}%" });

        if (tab.PendingRailSetup)
        {
            StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });
            StatusPanel.Children.Add(new TextBlock
            {
                Text = "Analyzing…",
                Opacity = 0.6,
                FontStyle = Avalonia.Media.FontStyle.Italic,
            });
        }
        else if (tab.Rail.Active)
        {
            StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });
            StatusPanel.Children.Add(new TextBlock
            {
                Text = $"Block {tab.Rail.CurrentBlock + 1}/{tab.Rail.NavigableCount} | " +
                       $"Line {tab.Rail.CurrentLine + 1}/{tab.Rail.CurrentLineCount}"
            });
            StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });
            StatusPanel.Children.Add(new TextBlock
            {
                Text = "Rail Mode",
                Foreground = new SolidColorBrush(Color.FromRgb(66, 133, 244)),
                FontWeight = FontWeight.Bold,
            });
        }
    }
}
