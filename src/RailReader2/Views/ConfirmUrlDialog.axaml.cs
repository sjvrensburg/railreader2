using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class ConfirmUrlDialog : Window
{
    public ConfirmUrlDialog(string url)
    {
        InitializeComponent();
        UrlDisplay.Text = url;
        KeyDown += OnKeyDown;
    }

    public ConfirmUrlDialog() : this("") { }

    private void OnOpenClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close(false);
                e.Handled = true;
                break;
            case Key.Enter:
                Close(true);
                e.Handled = true;
                break;
        }
    }
}
