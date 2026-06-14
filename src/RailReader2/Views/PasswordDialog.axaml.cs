using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class PasswordDialog : Window
{
    /// <param name="fileName">PDF display name shown in the prompt.</param>
    /// <param name="wrongPassword">
    /// True after a rejected attempt — shows the "incorrect password" retry message.
    /// </param>
    public PasswordDialog(string? fileName = null, bool wrongPassword = false)
    {
        InitializeComponent();

        var name = string.IsNullOrEmpty(fileName) ? "This PDF" : $"“{fileName}”";
        MessageText.Text = wrongPassword
            ? $"Incorrect password. {name} is password-protected — try again."
            : $"{name} is password-protected. Enter its password to open it.";

        DialogKeyboard.FocusOnOpen(this, PasswordInput);
        DialogKeyboard.EnableEscEnterClose<string?>(this, cancelResult: null, confirmResult: GetPassword);
    }

    public PasswordDialog() : this(null, false) { }

    private string? GetPassword()
    {
        var pwd = PasswordInput.Text;
        return string.IsNullOrEmpty(pwd) ? null : pwd;
    }

    private void OnRevealToggled(object? sender, RoutedEventArgs e)
        => PasswordInput.PasswordChar = RevealToggle.IsChecked == true ? '\0' : '•';

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(GetPassword());

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null as string);
}
