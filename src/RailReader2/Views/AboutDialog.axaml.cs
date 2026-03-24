using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailReader2.Views;

public partial class AboutDialog : Window
{
    private string? _logFilePath;

    public AboutDialog()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        VersionText.Text = $"Version {version}";
    }

    /// <summary>
    /// Sets the log file path displayed in the dialog. Call before ShowDialog.
    /// </summary>
    public void SetLogFilePath(string? path)
    {
        _logFilePath = path;
        if (path is not null)
        {
            LogPathPanel.IsVisible = true;
            LogPathText.Text = path;
        }
    }

    private async void OnCopyLogPath(object? sender, RoutedEventArgs e)
    {
        if (_logFilePath is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_logFilePath);
            if (sender is Button btn && btn.Content is TextBlock tb)
            {
                tb.Opacity = 1.0;
                await Task.Delay(500);
                tb.Opacity = 0.5;
            }
        }
    }
}
