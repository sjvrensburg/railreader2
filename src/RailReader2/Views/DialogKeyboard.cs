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

/// <summary>
/// A fullscreen main window can end up stacked above its own modal dialog on some window managers
/// (the dialog opens hidden behind the page, so the app looks frozen waiting on it).
/// </summary>
internal static class DialogStacking
{
    /// <summary>
    /// Call once from a modal dialog's constructor. While the dialog is active and its owner is
    /// fullscreen it is topmost, so it cannot hide behind the page; the flag is dropped when the dialog
    /// loses activation (alt-tab) so it never floats above other applications.
    /// </summary>
    public static void KeepAboveFullscreenOwner(Window dialog)
    {
        dialog.Opened += (_, _) => Raise(dialog);
        dialog.Activated += (_, _) => Raise(dialog);
        dialog.Deactivated += (_, _) => dialog.Topmost = false;
    }

    private static void Raise(Window dialog)
    {
        // Any ancestor counts: a dialog opened from another (topmost) dialog has that dialog as its owner.
        bool fullscreen = false;
        for (var o = dialog.Owner as Window; o is not null && !fullscreen; o = o.Owner as Window)
            fullscreen = o.WindowState == WindowState.FullScreen;
        if (!fullscreen) return;
        dialog.Topmost = true;
        dialog.Activate();
    }
}
