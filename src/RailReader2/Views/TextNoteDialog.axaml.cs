using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class TextNoteDialog : Window
{
    public TextNoteDialog()
    {
        InitializeComponent();
        DialogKeyboard.FocusOnOpen(this, NoteTextBox);
        // No Enter handler — the note box is multiline.
        DialogKeyboard.EnableEscEnterClose<string?>(this, cancelResult: null, confirmResult: null);
    }

    public TextNoteDialog(string existingText) : this()
    {
        NoteTextBox.Text = existingText;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(NoteTextBox.Text);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null as string);
}
