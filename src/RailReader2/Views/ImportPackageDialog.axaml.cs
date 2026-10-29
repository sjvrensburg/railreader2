using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

/// <summary>Asked when an opened <c>.railreading</c> package matches a local PDF: add the package to that copy
/// (additive, so Enter may confirm it). There is no "separate copy": a copy of a known document shares its
/// content id, and with it the notes, portals and rotation. The path is shown so the reader can catch a file
/// that has since been replaced by another document.</summary>
public partial class ImportPackageDialog : Window
{
    public ImportPackageDialog(string localPath)
    {
        InitializeComponent();
        DialogStacking.KeepAboveFullscreenOwner(this);
        PathDisplay.Text = localPath;
        DialogKeyboard.EnableEscEnterClose(this, cancelResult: false, confirmResult: () => true);
        DialogKeyboard.FocusOnOpen(this, MergeButton);
    }

    public ImportPackageDialog() : this("") { }

    private void OnMergeClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
