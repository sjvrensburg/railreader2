using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

/// <summary>Shown when startup bootstrap (config load, cleanup, ONNX init) throws — the only user-facing
/// surface for a fault that would otherwise strand the app on a frozen splash screen with no explanation.</summary>
public partial class StartupErrorWindow : Window
{
    public StartupErrorWindow()
    {
        InitializeComponent();
    }

    public StartupErrorWindow(string message) : this()
    {
        MessageText.Text = message;
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e) => Close();
}
