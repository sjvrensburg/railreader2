using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class BookmarkNameDialog : Window
{
    public BookmarkNameDialog(int currentPage)
    {
        InitializeComponent();
        NameInput.Text = $"Page {currentPage}";
        DialogKeyboard.FocusOnOpen(this, NameInput, selectAll: true);
        DialogKeyboard.EnableEscEnterClose<string?>(this, cancelResult: null, confirmResult: GetName);
    }

    public BookmarkNameDialog() : this(1) { }

    public void SetName(string name) => NameInput.Text = name;

    private string? GetName()
    {
        var name = NameInput.Text?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(GetName());

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null as string);
}
