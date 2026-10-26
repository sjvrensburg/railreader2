using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

/// <summary>Generic destructive-action confirmation. The safe choice owns the keyboard:
/// initial focus is Cancel and there is no window-wide Enter-to-confirm binding, so Enter
/// can only confirm when the user has explicitly tabbed to the confirm button.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        DialogStacking.KeepAboveFullscreenOwner(this);
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        DialogKeyboard.EnableEscEnterClose(this, cancelResult: false, confirmResult: null);
        DialogKeyboard.FocusOnOpen(this, CancelButton);
    }

    public ConfirmDialog() : this("Confirm", "", "OK") { }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
