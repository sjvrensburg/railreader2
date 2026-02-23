using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class GoToPageDialog : Window
{
    private readonly int _maxPage;

    public GoToPageDialog(int currentPage, int maxPage)
    {
        _maxPage = maxPage;
        InitializeComponent();
        PageInput.Watermark = $"Page (1–{maxPage})";
        PageInput.Text = currentPage.ToString();
        Opened += (_, _) =>
        {
            PageInput.Focus();
            PageInput.SelectAll();
        };
        KeyDown += OnKeyDown;
    }

    public GoToPageDialog() : this(1, 1) { }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (int.TryParse(PageInput.Text, out int page) && page >= 1 && page <= _maxPage)
            Close(page);
        else
            Close(-1);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(-1);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                OnOkClick(sender, e);
                e.Handled = true;
                break;
        }
    }
}
