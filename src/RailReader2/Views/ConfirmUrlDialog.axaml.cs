using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class ConfirmUrlDialog : Window
{
    public ConfirmUrlDialog(string url)
    {
        InitializeComponent();
        UrlDisplay.Text = url;
        DialogKeyboard.EnableEscEnterClose(this, cancelResult: false, confirmResult: () => true);
    }

    public ConfirmUrlDialog() : this("") { }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
