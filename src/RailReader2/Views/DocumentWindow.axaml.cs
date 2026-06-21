using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace RailReader2.Views;

/// <summary>
/// A tear-off floating window hosting a single <see cref="DocumentView"/> — a detached viewport of
/// the active document, coexisting with the docked split panes. Native window chrome handles
/// resize/move/multi-monitor; key events forward to the main window's shared handler so a focused
/// detached pane gets the same shortcuts (Core routes input through the focused viewport).
/// Created/destroyed by <c>MainWindow</c>'s document-window management (following the PortalWindow
/// lifecycle precedent).
/// </summary>
public partial class DocumentWindow : Window
{
    private DocumentView? _view;

    /// <summary>Forwards a key event to the main window's <c>TryHandleKey</c>; returns true if handled.</summary>
    public Func<KeyEventArgs, bool>? KeyHandler { get; set; }

    /// <summary>The hosted DocumentView (its surface), or null before <see cref="Host"/>.</summary>
    public DocumentView? HostedView => _view;

    public DocumentWindow() => InitializeComponent();

    /// <summary>Place a DocumentView (already bound to its viewport) into this window's content.</summary>
    public void Host(DocumentView view)
    {
        _view = view;
        HostPanel.Children.Add(view);
    }

    /// <summary>Apply the app's scaled font size (<c>MainWindowViewModel.CurrentFontSize</c>); the hosted
    /// DocumentView's chrome (toolbar / status text) inherits it. Called on creation and again from
    /// MainWindow's CurrentFontSize property-change case so a live Settings change reaches the window.</summary>
    public void ApplyFontScale(double uiFontSize) => FontSize = uiFontSize;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (KeyHandler?.Invoke(e) == true) return;
        base.OnKeyDown(e);
    }
}
