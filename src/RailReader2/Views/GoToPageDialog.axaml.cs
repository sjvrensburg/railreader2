using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class GoToPageDialog : Window
{
    private readonly int _maxPage;

    public GoToPageDialog(int currentPage, int maxPage)
    {
        _maxPage = maxPage;
        InitializeComponent();
        PageInput.PlaceholderText = $"Page (1–{maxPage})";
        PageInput.Text = currentPage.ToString();
        DialogKeyboard.FocusOnOpen(this, PageInput, selectAll: true);
        DialogKeyboard.EnableEscEnterClose(this, cancelResult: -1, confirmResult: GetPage);
    }

    public GoToPageDialog() : this(1, 1) { }

    private int GetPage() =>
        int.TryParse(PageInput.Text, out int page) && page >= 1 && page <= _maxPage ? page : -1;

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(GetPage());

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(-1);
}
