using Avalonia.Controls;
using RailReader2.Models;
using RailReader2.ViewModels;

namespace RailReader2.Views;

public partial class ToolBarView : UserControl
{
    private MainWindowViewModel? _vm;

    public MainWindowViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = value;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                UpdateToggleState();
                UpdateCopyVisibility();
            }
        }
    }

    public ToolBarView()
    {
        InitializeComponent();

        BrowseButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.None);
        SelectButton.Click += (_, _) => _vm?.SetAnnotationTool(AnnotationTool.TextSelect);
        CopyButton.Click += (_, _) => _vm?.CopySelectedText();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainWindowViewModel.ActiveTool))
        {
            UpdateToggleState();
            UpdateCopyVisibility();
        }
        else if (args.PropertyName is "SelectedText")
        {
            UpdateCopyVisibility();
        }
    }

    private void UpdateToggleState()
    {
        if (_vm is null) return;
        BrowseButton.IsChecked = _vm.ActiveTool == AnnotationTool.None;
        SelectButton.IsChecked = _vm.ActiveTool == AnnotationTool.TextSelect;
    }

    private void UpdateCopyVisibility()
    {
        CopyButton.IsVisible = _vm?.SelectedText is not null;
    }
}
