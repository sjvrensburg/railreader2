using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class ConfirmUrlDialog : Window
{
    public ConfirmUrlDialog(string url)
    {
        InitializeComponent();
        DialogStacking.KeepAboveFullscreenOwner(this);
        UrlDisplay.Text = url;
        // This dialog guards an external launch, so Enter must never confirm from anywhere:
        // no window-wide Enter binding, and initial focus sits on Cancel (Enter/Space then
        // activates Cancel). Opening requires an explicit click / Tab+Enter on Open.
        DialogKeyboard.EnableEscEnterClose(this, cancelResult: false, confirmResult: null);
        DialogKeyboard.FocusOnOpen(this, CancelButton);
    }

    public ConfirmUrlDialog() : this("") { }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
