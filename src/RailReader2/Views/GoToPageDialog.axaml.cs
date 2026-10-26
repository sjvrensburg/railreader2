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
        DialogStacking.KeepAboveFullscreenOwner(this);
        PageInput.PlaceholderText = $"Page (1–{maxPage})";
        PageInput.Text = currentPage.ToString();
        DialogKeyboard.FocusOnOpen(this, PageInput, selectAll: true);
        // Enter is handled here (not via EnableEscEnterClose's confirm) so invalid input can
        // keep the dialog open with an inline error instead of closing with -1, which the
        // caller silently ignores. -1 is reserved for Escape/Cancel.
        DialogKeyboard.EnableEscEnterClose(this, cancelResult: -1, confirmResult: null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { TrySubmit(); e.Handled = true; }
        };
    }

    public GoToPageDialog() : this(1, 1) { }

    private int GetPage() =>
        int.TryParse(PageInput.Text, out int page) && page >= 1 && page <= _maxPage ? page : -1;

    private void TrySubmit()
    {
        int page = GetPage();
        if (page > 0) { Close(page); return; }
        ErrorText.Text = $"Enter a page number between 1 and {_maxPage}.";
        ErrorText.IsVisible = true;
        PageInput.SelectAll();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TrySubmit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(-1);
}
