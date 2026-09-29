using Avalonia.Controls;
using Avalonia.Interactivity;
using RailReader2.Controls;
using RailReader2.Services;

namespace RailReader2.Views;

public partial class TextNoteDialog : Window
{
    private readonly TextBoxSpellCheck _spellCheck;

    public TextNoteDialog()
    {
        InitializeComponent();
        _spellCheck = TextBoxSpellCheck.Attach(NoteTextBox, SpellCheckService.Shared);
        _spellCheck.Changed += UpdateSpellingStatus;
        DialogKeyboard.FocusOnOpen(this, NoteTextBox);
        // No Enter handler — the note box is multiline.
        DialogKeyboard.EnableEscEnterClose<string?>(this, cancelResult: null, confirmResult: null);
    }

    public TextNoteDialog(string existingText) : this()
    {
        NoteTextBox.Text = existingText;
    }

    private void UpdateSpellingStatus()
    {
        var service = SpellCheckService.Shared;
        string text;
        if (!service.Enabled || string.IsNullOrWhiteSpace(NoteTextBox.Text))
            text = "";
        else if (service.LoadError is not null)
            text = "Spell check unavailable (see Settings ▸ Spelling)";
        else if (!service.IsReady)
            text = "";
        else
            text = _spellCheck.Misspellings.Count switch
            {
                0 => "No spelling issues",
                1 => "1 possible misspelling · F7 to review",
                var n => $"{n} possible misspellings · F7 to review",
            };
        if (SpellingStatus.Text != text) SpellingStatus.Text = text;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(NoteTextBox.Text);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null as string);
}
