using Avalonia.Controls;
using Avalonia.Input;

namespace RailReader2.Views;

/// <summary>
/// Helpers for the keyboard/focus boilerplate shared by simple modal dialogs
/// (<see cref="ConfirmUrlDialog"/>, <see cref="BookmarkNameDialog"/>,
/// <see cref="TextNoteDialog"/>, <see cref="GoToPageDialog"/>).
/// </summary>
internal static class DialogKeyboard
{
    /// <summary>
    /// Wires Escape → close with <paramref name="cancelResult"/>, and (when
    /// <paramref name="confirmResult"/> is non-null) Enter → close with its return value.
    /// Pass a null <paramref name="confirmResult"/> for dialogs that should not commit on
    /// Enter (e.g. multiline text input).
    /// </summary>
    public static void EnableEscEnterClose<TResult>(
        Window dialog,
        TResult cancelResult,
        System.Func<TResult>? confirmResult)
    {
        dialog.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    dialog.Close(cancelResult);
                    e.Handled = true;
                    break;
                case Key.Enter when confirmResult is not null:
                    dialog.Close(confirmResult());
                    e.Handled = true;
                    break;
            }
        };
    }

    /// <summary>
    /// Focuses <paramref name="input"/> once the dialog has opened, optionally selecting
    /// its existing text. No-op until <c>Opened</c> fires (otherwise the focus can lose
    /// the race with the window activation).
    /// </summary>
    public static void FocusOnOpen(Window dialog, Control input, bool selectAll = false)
    {
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            if (selectAll && input is TextBox tb) tb.SelectAll();
        };
    }
}
