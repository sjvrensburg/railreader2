using Avalonia.Controls;

namespace RailReader2.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    /// <summary>
    /// Stops the indeterminate ProgressBar animation before the window goes away. Avalonia leaves a
    /// looping style animation subscribed to the global animation clock after a window is closed
    /// (AvaloniaUI/Avalonia #16648 / #13139); that subscription keeps <c>MediaContext</c>'s 16 ms
    /// animation timer running forever, and on X11 a perpetually armed dispatcher timer spins the UI
    /// thread at 100% CPU — every input event and animation tick then competes with the spin, which
    /// reads as jank throughout the app. Flipping <c>IsIndeterminate</c> off while the control is
    /// still attached lets the <c>:indeterminate</c> style selector cancel the animation cleanly.
    /// Same rule as the other gated ProgressBars (LoadingOverlay, DocumentView, IndexView).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Bar.IsIndeterminate = false;
        base.OnClosing(e);
    }
}
