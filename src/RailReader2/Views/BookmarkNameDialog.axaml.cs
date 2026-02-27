using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class BookmarkNameDialog : Window
{
    public BookmarkNameDialog(int currentPage)
    {
        InitializeComponent();
        NameInput.Text = $"Page {currentPage}";
        Opened += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
        KeyDown += OnKeyDown;
    }

    public BookmarkNameDialog() : this(1) { }

    public void SetName(string name)
    {
        NameInput.Text = name;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameInput.Text?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null as string);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close(null as string);
                e.Handled = true;
                break;
            case Key.Enter:
                OnOkClick(sender, e);
                e.Handled = true;
                break;
        }
    }
}
