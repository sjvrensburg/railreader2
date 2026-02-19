using Avalonia.Controls;
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

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainWindowViewModel.ActiveTab) or
                    nameof(MainWindowViewModel.ActiveTabIndex))
                    UpdateStatus();
            };
        }
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
        StatusPanel.Children.Add(new TextBlock { Text = $"Page {tab.CurrentPage + 1}/{tab.PageCount}" });
        StatusPanel.Children.Add(new TextBlock { Text = "|", Opacity = 0.5 });
        StatusPanel.Children.Add(new TextBlock { Text = $"Zoom: {zoomPct}%" });

        if (tab.Rail.Active)
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
