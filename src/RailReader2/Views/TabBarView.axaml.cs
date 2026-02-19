using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class TabBarView : UserControl
{
    public TabBarView() => InitializeComponent();

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabViewModel tab } && Vm is { } vm)
        {
            int idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) vm.SelectTab(idx);
        }
    }

    private void OnTabClose(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabViewModel tab } && Vm is { } vm)
        {
            int idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) vm.CloseTab(idx);
        }
    }
}
