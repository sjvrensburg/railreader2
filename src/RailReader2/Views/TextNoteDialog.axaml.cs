using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class TextNoteDialog : Window
{
    public TextNoteDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NoteTextBox.Focus();
        KeyDown += OnKeyDown;
    }

    public TextNoteDialog(string existingText) : this()
    {
        NoteTextBox.Text = existingText;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(NoteTextBox.Text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null as string);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(null as string);
            e.Handled = true;
        }
    }
}
