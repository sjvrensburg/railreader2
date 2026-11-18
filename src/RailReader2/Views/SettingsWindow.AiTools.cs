using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RailReader.Core;
using RailReader2.Library;
using RailReader2.Library.Mcp;
using RailReader2.Services.Updates;

namespace RailReader2.Views;

// Settings ▸ Library ▸ AI Tools (roadmap M8): the reader's consent (AiToolsPreferences.Enabled), one switch per
// agent that adds or removes only RailReader2's entry in that agent's own config (Library/Mcp/AgentConfigs.cs),
// Copy Setup for any other client, and hidden folders. Preferences are re-read and written load-change-save,
// since the CLI (`railreader2-mcp hide …`) and the Library window write the same file.
public partial class SettingsWindow
{
    private bool _aiLoading;
    private McpServerCommand? _mcpServer;

    private sealed record Agent(string Name, Func<McpServerCommand?, AgentConnection> Status,
        Action<McpServerCommand> Connect, Action Disconnect);

    private static Agent[] Agents()
    {
        var code = ClaudeCodeConfig.Default();
        var desktop = ClaudeDesktopConfig.Default();
        var codex = CodexConfig.Default();
        return
        [
            new("Claude Code", code.Status, code.Connect, code.Disconnect),
            new("Claude Desktop", desktop.Status, desktop.Connect, desktop.Disconnect),
            new("Codex", codex.Status, codex.Connect, codex.Disconnect),
        ];
    }

    private void LoadAiToolsSettings()
    {
        _mcpServer = McpServerCommand.Locate(Environment.GetEnvironmentVariable, AppContext.BaseDirectory,
            OperatingSystem.IsWindows() && UpdateChannel.IsMsixPackaged());
        var prefs = AiToolsPreferences.Load();
        _aiLoading = true;
        AiToolsEnabledCheck.IsChecked = prefs.Enabled;
        _aiLoading = false;
        AiServerText.Text = _mcpServer is { } s
            ? $"Server: {s.Command}{(s.Args.Count > 0 ? " " + string.Join(' ', s.Args) : "")}"
            : "This build has no MCP server beside it (a development build). Use `railreader2-cli mcp` from the command-line tools instead.";
        AiCopySetupButton.IsEnabled = _mcpServer is not null;
        RefreshHiddenFolders(prefs);
        RefreshAgents();
    }

    private async void RefreshAgents()
    {
        AiAgentsPanel.Children.Clear();
        AiAgentsPanel.Children.Add(new TextBlock { Text = "Checking…", Classes = { "status" } });
        var server = _mcpServer;
        (Agent Agent, AgentConnection State)[] states;
        try
        {
            // Claude Code's status runs its CLI: off the UI thread.
            states = await Task.Run(() => Agents().Select(a => (a, Safe(() => a.Status(server)))).ToArray());
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[AI tools] status failed", ex);
            AiAgentsPanel.Children.Clear();
            AiToolsStatusText.Text = "Couldn't check the AI tools' settings — see the log for details.";
            return;
        }

        AiAgentsPanel.Children.Clear();
        foreach (var (agent, state) in states)
        {
            var box = new CheckBox
            {
                Content = agent.Name,
                IsChecked = state is AgentConnection.Connected or AgentConnection.ConnectedElsewhere,
                IsEnabled = state != AgentConnection.NotInstalled && server is not null,
            };
            Avalonia.Automation.AutomationProperties.SetName(box, $"Connect RailReader2 to {agent.Name}");
            var note = new TextBlock
            {
                Classes = { "status" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = state switch
                {
                    AgentConnection.NotInstalled => "not found on this computer",
                    AgentConnection.ConnectedElsewhere => "set up already, but not for this copy of RailReader2 (by hand, or by another install); check to point it here",
                    AgentConnection.Connected => "connected",
                    _ => "",
                },
            };
            box.IsCheckedChanged += (_, _) => OnAgentToggled(agent, box);
            AiAgentsPanel.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { box, note } });
        }
    }

    private static AgentConnection Safe(Func<AgentConnection> status)
    {
        try { return status(); }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Warn($"[AI tools] status: {ex.Message}");
            return AgentConnection.NotConnected;
        }
    }

    private async void OnAgentToggled(Agent agent, CheckBox box)
    {
        if (_mcpServer is not { } server) return;
        bool connect = box.IsChecked == true;
        box.IsEnabled = false;
        try
        {
            await Task.Run(() => { if (connect) agent.Connect(server); else agent.Disconnect(); });
            AiToolsStatusText.Text = connect
                ? $"{agent.Name} is connected. Start a new {agent.Name} session to use it."
                  + (AiToolsPreferences.Load().Enabled ? "" : " AI tools are still off above, so it is refused until you turn them on.")
                : $"{agent.Name} is no longer connected.";
        }
        catch (Exception ex)
        {
            if (ex is not AgentConfigException) RailReaderLogging.Logger.Error($"[AI tools] {agent.Name}", ex);
            AiToolsStatusText.Text = ex is AgentConfigException ? ex.Message : $"Couldn't change {agent.Name}'s settings: {ex.Message}";
        }
        RefreshAgents();
    }

    private void OnAiToolsEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_aiLoading) return;
        bool on = AiToolsEnabledCheck.IsChecked == true;
        try
        {
            AiToolsPreferences.Update(p => p.Enabled = on);
            AiToolsStatusText.Text = on
                ? "AI tools can read your library (not hidden or encrypted documents)."
                : "AI tools are off: every request is refused, including from tools already connected.";
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[AI tools] saving preferences failed", ex);
            AiToolsStatusText.Text = "Couldn't save that change — see the log for details.";
        }
    }

    private async void OnAiCopySetup(object? sender, RoutedEventArgs e)
    {
        if (_mcpServer is not { } s || Clipboard is not { } clipboard) return;
        string text = "For Claude Desktop and most MCP clients (their mcpServers setting):\n\n" + s.ToJsonSnippet()
            + "\n\nFor Claude Code:\n\n" + s.ClaudeCodeAddCommand() + "\n";
        await clipboard.SetTextAsync(text);
        AiToolsStatusText.Text = "Copied the setup to the clipboard.";
    }

    private async void OnAiHideFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Hide a folder from AI tools",
            AllowMultiple = false,
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
        try { RefreshHiddenFolders(AiToolsPreferences.Update(p => p.HideFolder(folder))); }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[AI tools] saving preferences failed", ex);
            AiToolsStatusText.Text = "Couldn't save that change — see the log for details.";
        }
    }

    private void RefreshHiddenFolders(AiToolsPreferences prefs)
    {
        AiHiddenFoldersPanel.Children.Clear();
        if (prefs.HiddenFolders.Count == 0)
            AiHiddenFoldersPanel.Children.Add(new TextBlock { Text = "None", Classes = { "status" } });
        foreach (var folder in prefs.HiddenFolders)
        {
            var remove = new Button { Content = "Remove", Padding = new Avalonia.Thickness(8, 2) };
            Avalonia.Automation.AutomationProperties.SetName(remove, $"Stop hiding {folder}");
            remove.Click += (_, _) =>
            {
                try { RefreshHiddenFolders(AiToolsPreferences.Update(p => p.UnhideFolder(folder))); }
                catch (Exception ex)
                {
                    RailReaderLogging.Logger.Error("[AI tools] saving preferences failed", ex);
                    AiToolsStatusText.Text = "Couldn't save that change — see the log for details.";
                }
            };
            AiHiddenFoldersPanel.Children.Add(new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = folder, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, remove },
            });
        }
    }
}
