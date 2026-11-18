using System.Text.Json.Nodes;
using RailReader2.Library.Mcp;
using Xunit;

namespace RailReader.Export.Tests.Library;

/// <summary>Settings ▸ Library ▸ AI tools writes RailReader2's entry into other programs' configs (roadmap M8).
/// The rules: touch only our own entry, keep everything else, refuse a file we can't read safely.</summary>
public sealed class AgentConfigsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_agents_").FullName;
    private static readonly McpServerCommand Server = new(@"C:\Program Files\railreader2\RailReader2.Mcp.exe", []);
    private static readonly McpServerCommand AppImage = new("/home/me/Apps/railreader2-x86_64.AppImage", ["--mcp"]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string File_(string name, string? text = null)
    {
        string path = Path.Combine(_dir, name);
        if (text is not null) File.WriteAllText(path, text);
        return path;
    }

    // ------------------------------------------------------------ Claude Desktop

    [Fact]
    public void ClaudeDesktop_AddsAndRemovesOnlyItsEntry()
    {
        var cfg = new ClaudeDesktopConfig(File_("claude_desktop_config.json",
            """{"mcpServers":{"other":{"command":"x","args":[]}},"theme":"dark"}"""));
        Assert.Equal(AgentConnection.NotConnected, cfg.Status(Server));

        cfg.Connect(AppImage);
        var root = JsonNode.Parse(File.ReadAllText(cfg.Path))!;
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.Equal("x", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(AppImage.Command, root["mcpServers"]!["railreader2"]!["command"]!.GetValue<string>());
        Assert.Equal("--mcp", root["mcpServers"]!["railreader2"]!["args"]![0]!.GetValue<string>());
        Assert.Equal(AgentConnection.Connected, cfg.Status(AppImage));
        Assert.Equal(AgentConnection.ConnectedElsewhere, cfg.Status(Server)); // another copy's path

        cfg.Disconnect();
        root = JsonNode.Parse(File.ReadAllText(cfg.Path))!;
        Assert.Null(root["mcpServers"]!["railreader2"]);
        Assert.NotNull(root["mcpServers"]!["other"]);
    }

    [Fact]
    public void ClaudeDesktop_CreatesTheFileInAnExistingFolder_ButNeverTheFolder()
    {
        var cfg = new ClaudeDesktopConfig(File_("claude_desktop_config.json"));
        cfg.Connect(Server);
        Assert.Equal(AgentConnection.Connected, cfg.Status(Server));

        var missing = new ClaudeDesktopConfig(Path.Combine(_dir, "NoClaude", "claude_desktop_config.json"));
        Assert.Equal(AgentConnection.NotInstalled, missing.Status(Server));
        Assert.Throws<AgentConfigException>(() => missing.Connect(Server));
        Assert.False(Directory.Exists(Path.Combine(_dir, "NoClaude")));
    }

    [Fact]
    public void ClaudeDesktop_RefusesAFileItCantParse_AndLeavesItAlone()
    {
        const string broken = "{ \"mcpServers\": { oops ";
        var cfg = new ClaudeDesktopConfig(File_("claude_desktop_config.json", broken));
        Assert.Throws<AgentConfigException>(() => cfg.Connect(Server));
        Assert.Equal(broken, File.ReadAllText(cfg.Path));
    }

    // ------------------------------------------------------------ Codex

    [Fact]
    public void Codex_AppendsAMarkedBlock_AndRemovesExactlyIt()
    {
        const string original = "model = \"o4\"\n\n[mcp_servers.other]\ncommand = \"x\"\n";
        var cfg = new CodexConfig(File_("config.toml", original));
        Assert.Equal(AgentConnection.NotConnected, cfg.Status(Server));

        cfg.Connect(Server);
        string text = File.ReadAllText(cfg.Path);
        Assert.StartsWith(original.TrimEnd('\n'), text);
        Assert.Contains("[mcp_servers.railreader2]", text);
        Assert.Contains(@"command = 'C:\Program Files\railreader2\RailReader2.Mcp.exe'", text); // literal string: no escapes
        Assert.Contains("args = []", text);
        Assert.Equal(AgentConnection.Connected, cfg.Status(Server));

        cfg.Connect(Server); // idempotent: still one block
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(cfg.Path), @"\[mcp_servers\.railreader2\]"));

        cfg.Disconnect();
        Assert.Equal(original, File.ReadAllText(cfg.Path));
    }

    [Fact]
    public void Codex_KeepsTextTheReaderAddedAfterTheBlock()
    {
        var cfg = new CodexConfig(File_("config.toml", "a = 1\n"));
        cfg.Connect(AppImage);
        File.AppendAllText(cfg.Path, "\n[profiles.fast]\nmodel = \"x\"\n");
        cfg.Disconnect();
        string text = File.ReadAllText(cfg.Path);
        Assert.Contains("a = 1", text);
        Assert.Contains("[profiles.fast]", text);
        Assert.DoesNotContain("railreader2", text);
    }

    [Theory]
    [InlineData("[mcp_servers.railreader2]\ncommand = \"mine\"\n")]
    [InlineData("[mcp_servers.\"railreader2\"]\ncommand = \"mine\"\n")]
    [InlineData("[mcp_servers]\nrailreader2 = { command = \"mine\" }\n")]
    [InlineData("mcp_servers.railreader2.command = \"mine\"\n")]
    public void Codex_LeavesAHandWrittenEntryAlone(string toml)
    {
        var cfg = new CodexConfig(File_("config.toml", toml));
        Assert.Equal(AgentConnection.ConnectedElsewhere, cfg.Status(Server));
        Assert.Throws<AgentConfigException>(() => cfg.Connect(Server));
        Assert.Equal(toml, File.ReadAllText(cfg.Path));
        cfg.Disconnect(); // nothing of ours to remove
        Assert.Equal(toml, File.ReadAllText(cfg.Path));
    }

    [Fact]
    public void Codex_EscapesAPathTheLiteralFormCantHold()
    {
        var cfg = new CodexConfig(File_("config.toml", ""));
        cfg.Connect(new McpServerCommand("/home/o'brien/rr.AppImage", ["--mcp"]));
        string text = File.ReadAllText(cfg.Path);
        Assert.Contains("command = \"/home/o'brien/rr.AppImage\"", text);
        Assert.Contains("args = ['--mcp']", text);
    }

    // ------------------------------------------------------------ Claude Code

    [Fact]
    public void ClaudeCode_UsesItsOwnCli_ReplacingAnOlderEntry()
    {
        var calls = new List<string>();
        bool added = false;
        var cfg = new ClaudeCodeConfig("/usr/bin/claude", (prog, args) =>
        {
            string line = string.Join(' ', args);
            calls.Add(line);
            if (line.StartsWith("mcp add")) { added = true; return (0, ""); }
            if (line.StartsWith("mcp get")) return added ? (0, $"railreader2:\n  Command: {AppImage.Command}\n  Args: --mcp") : (1, "No MCP server found");
            return (0, "");
        });
        Assert.Equal(AgentConnection.NotConnected, cfg.Status(AppImage));
        cfg.Connect(AppImage);
        Assert.Equal(AgentConnection.Connected, cfg.Status(AppImage));
        Assert.Contains($"mcp add --scope user railreader2 -- {AppImage.Command} --mcp", calls);
        Assert.True(calls.IndexOf("mcp remove --scope user railreader2") < calls.FindIndex(c => c.StartsWith("mcp add")));
    }

    [Fact]
    public void ClaudeCode_NotFound_IsNotInstalled()
        => Assert.Equal(AgentConnection.NotInstalled, new ClaudeCodeConfig(null, (_, _) => (0, "")).Status(Server));

    // ------------------------------------------------------------ locating the server

    [Fact]
    public void Locate_PrefersTheStoreAlias_ThenTheAppImage_ThenTheExeBesideTheApp()
    {
        string appDir = Directory.CreateDirectory(Path.Combine(_dir, "mnt", "usr", "bin")).FullName;
        string appImage = File_("rr.AppImage", "");
        var env = new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
            ["APPIMAGE"] = appImage,
            ["APPDIR"] = Path.Combine(_dir, "mnt"),
        };
        string? Env(string k) => env.GetValueOrDefault(k);

        var store = McpServerCommand.Locate(Env, appDir, msixPackaged: true)!;
        Assert.EndsWith("railreader2-mcp.exe", store.Command);
        Assert.Contains("WindowsApps", store.Command);

        var ai = McpServerCommand.Locate(Env, appDir, msixPackaged: false)!;
        Assert.Equal((appImage, "--mcp"), (ai.Command, ai.Args.Single()));

        env.Remove("APPIMAGE");
        Assert.Null(McpServerCommand.Locate(Env, appDir, false)); // a dev build: no server beside it
        string exe = File_(Path.Combine(appDir, OperatingSystem.IsWindows() ? "RailReader2.Mcp.exe" : "RailReader2.Mcp"), "");
        Assert.Equal(exe, McpServerCommand.Locate(Env, appDir, false)!.Command);
    }

    [Fact]
    public void Snippet_IsTheStandardMcpServersShape()
    {
        var root = JsonNode.Parse(AppImage.ToJsonSnippet())!;
        Assert.Equal(AppImage.Command, root["mcpServers"]!["railreader2"]!["command"]!.GetValue<string>());
        Assert.Equal("claude mcp add --scope user railreader2 -- /home/me/Apps/railreader2-x86_64.AppImage --mcp", AppImage.ClaudeCodeAddCommand());
    }

    [Fact]
    public void Locate_FindsTheRepositoryBuild_ForADevelopmentBuildOfTheApp()
    {
        string exe = OperatingSystem.IsWindows() ? "RailReader2.Mcp.exe" : "RailReader2.Mcp";
        string appBin = Directory.CreateDirectory(Path.Combine(_dir, "src", "RailReader2", "bin", "Release", "net10.0")).FullName;
        string? Env(string _) => null;
        Assert.Null(McpServerCommand.Locate(Env, appBin, false)); // not built yet

        string mcpBin = Directory.CreateDirectory(Path.Combine(_dir, "src", "RailReader2.Mcp", "bin", "Release", "net10.0")).FullName;
        File.WriteAllText(Path.Combine(mcpBin, exe), "");
        var dev = McpServerCommand.Locate(Env, appBin + Path.DirectorySeparatorChar, false)!;
        Assert.Equal(Path.Combine(mcpBin, exe), dev.Command);
        Assert.True(dev.IsDevelopmentBuild);
    }
}
