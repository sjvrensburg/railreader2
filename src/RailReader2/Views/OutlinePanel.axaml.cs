using Avalonia.Controls;
using RailReader2.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class OutlinePanel : UserControl
{
    public OutlinePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.ActiveTab))
                    UpdateOutlineSource();
            };
            UpdateOutlineSource();
        }
    }

    private void UpdateOutlineSource()
    {
        OutlineTree.ItemsSource = Vm?.ActiveTab?.Outline;
    }

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is { } vm && OutlineTree.SelectedItem is OutlineEntry { Page: { } page })
            vm.GoToPage(page);
    }
}
