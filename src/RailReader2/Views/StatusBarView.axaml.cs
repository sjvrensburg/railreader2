using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RailReader2.Models;
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
                    nameof(MainWindowViewModel.ActiveTabIndex) or
                    nameof(MainWindowViewModel.ActiveTool) or
                    nameof(MainWindowViewModel.AutoScrollActive) or
                    nameof(MainWindowViewModel.JumpMode))
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

    private static Button MakeNavButton(string text, EventHandler<RoutedEventArgs> handler, string? tooltip = null)
    {
        var btn = new Button { Content = text, Padding = new Avalonia.Thickness(6, 0), MinWidth = 0 };
        if (tooltip is not null)
            ToolTip.SetTip(btn, tooltip);
        btn.Click += handler;
        return btn;
    }

    private void AddSeparator() =>
        StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });

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
        { if (vm?.ActiveTab is { } t) vm.GoToPage(t.CurrentPage - 1); }, "Previous page (PgUp)"));
        StatusPanel.Children.Add(new TextBlock
        {
            Text = $"Page {tab.CurrentPage + 1}/{tab.PageCount}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        StatusPanel.Children.Add(MakeNavButton("▶", (_, _) =>
        { if (vm?.ActiveTab is { } t) vm.GoToPage(t.CurrentPage + 1); }, "Next page (PgDn)"));
        AddSeparator();
        StatusPanel.Children.Add(new TextBlock { Text = $"Zoom: {zoomPct}%" });

        if (tab.PendingRailSetup)
        {
            AddSeparator();
            StatusPanel.Children.Add(new TextBlock
            {
                Text = "Analyzing…",
                Opacity = 0.6,
                FontStyle = Avalonia.Media.FontStyle.Italic,
            });
        }
        else if (tab.Rail.Active)
        {
            AddSeparator();
            StatusPanel.Children.Add(new TextBlock
            {
                Text = $"Block {tab.Rail.CurrentBlock + 1}/{tab.Rail.NavigableCount} | " +
                       $"Line {tab.Rail.CurrentLine + 1}/{tab.Rail.CurrentLineCount}"
            });
            AddSeparator();
            StatusPanel.Children.Add(new TextBlock
            {
                Text = "Rail Mode",
                Foreground = new SolidColorBrush(Color.FromRgb(66, 133, 244)),
                FontWeight = FontWeight.Bold,
            });

            if (vm is { AutoScrollActive: true })
            {
                AddSeparator();
                StatusPanel.Children.Add(new TextBlock
                {
                    Text = "Auto-Scroll",
                    Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    FontWeight = FontWeight.Bold,
                });
                var stopBtn = MakeNavButton("\u23f8", (_, _) => vm.StopAutoScroll(), "Stop auto-scroll (P)");
                stopBtn.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                StatusPanel.Children.Add(stopBtn);
            }

            if (vm is { JumpMode: true })
            {
                AddSeparator();
                StatusPanel.Children.Add(new TextBlock
                {
                    Text = "Jump",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 0)),
                    FontWeight = FontWeight.Bold,
                });
                var exitBtn = MakeNavButton("\u2715", (_, _) => vm.JumpMode = false, "Exit jump mode (J)");
                exitBtn.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                StatusPanel.Children.Add(exitBtn);
            }
        }

        if (vm is { IsAnnotating: true })
        {
            AddSeparator();
            string toolName = vm.ActiveTool switch
            {
                AnnotationTool.Highlight => "Highlight",
                AnnotationTool.Pen => "Pen",
                AnnotationTool.TextNote => "Text Note",
                AnnotationTool.Rectangle => "Rectangle",
                AnnotationTool.Eraser => "Eraser",
                AnnotationTool.TextSelect => "Text Select",
                _ => "Annotating",
            };
            StatusPanel.Children.Add(new TextBlock
            {
                Text = $"{toolName} Tool",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 0)),
                FontWeight = FontWeight.Bold,
            });
            var exitBtn = MakeNavButton("\u2715", (_, _) => vm.CancelAnnotationTool(), "Cancel tool (Escape)");
            exitBtn.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
            StatusPanel.Children.Add(exitBtn);
        }
    }
}
