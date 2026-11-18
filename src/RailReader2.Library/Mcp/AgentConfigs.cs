using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RailReader2.Library.Mcp;

/// <summary>How an agent starts the MCP server: a program and its arguments.</summary>
public sealed record McpServerCommand(string Command, IReadOnlyList<string> Args)
{
    /// <summary>The name every agent config uses for RailReader2's entry.</summary>
    public const string EntryName = "railreader2";

    /// <summary>Where this copy of RailReader2 keeps its MCP server, or null when it has none (a development
    /// build: use <c>railreader2-cli mcp</c>).
    /// <list type="bullet">
    /// <item>Store (MSIX): the execution alias, a stable path across Store updates (the install folder's name
    /// changes with every version), and the only way into the package's own (virtualised) AppData.</item>
    /// <item>AppImage: the AppImage file itself with <c>--mcp</c> (its mount point changes every run).</item>
    /// <item>Inno install, tarball, published folder: <c>RailReader2.Mcp[.exe]</c> beside the app.</item>
    /// <item>A build from the repository (<c>dotnet run</c>): <c>src/RailReader2.Mcp/bin/&lt;config&gt;/&lt;tfm&gt;/</c>,
    /// if it has been built (<see cref="IsDevelopmentBuild"/> is then true).</item>
    /// </list></summary>
    public static McpServerCommand? Locate(Func<string, string?> env, string baseDirectory, bool msixPackaged)
    {
        string exe = OperatingSystem.IsWindows() ? "RailReader2.Mcp.exe" : "RailReader2.Mcp";
        if (msixPackaged && env("LOCALAPPDATA") is { Length: > 0 } local)
            return new(Path.Combine(local, "Microsoft", "WindowsApps", "railreader2-mcp.exe"), []);
        if (env("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage)
            && env("APPDIR") is { Length: > 0 } appDir && IsUnder(baseDirectory, appDir))
            return new(appImage, ["--mcp"]);
        string beside = Path.Combine(baseDirectory, exe);
        if (File.Exists(beside)) return new(beside, []);
        return DevelopmentBuild(baseDirectory, exe) is { } dev ? new(dev, []) { IsDevelopmentBuild = true } : null;
    }

    /// <summary>True when the server is the repository's own build output rather than an installed copy.</summary>
    public bool IsDevelopmentBuild { get; init; }

    /// <summary>The app runs from <c>…/src/RailReader2/bin/&lt;config&gt;/&lt;tfm&gt;/</c>: the server's build output is
    /// the same <c>bin/&lt;config&gt;/&lt;tfm&gt;</c> under <c>src/RailReader2.Mcp</c>. Null when that isn't the layout
    /// or the server hasn't been built.</summary>
    internal static string? DevelopmentBuild(string baseDirectory, string exe)
    {
        var tfm = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        var config = tfm.Parent;
        var bin = config?.Parent;
        var project = bin?.Parent;
        if (bin?.Name != "bin" || project?.Parent is not { } src) return null;
        string candidate = Path.Combine(src.FullName, "RailReader2.Mcp", "bin", config!.Name, tfm.Name, exe);
        return File.Exists(candidate) ? candidate : null;
    }

    static bool IsUnder(string path, string dir)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(d, StringComparison.Ordinal);
    }

    /// <summary>The <c>mcpServers</c> entry Claude Desktop and most other MCP clients read, as indented JSON.</summary>
    public string ToJsonSnippet()
    {
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [EntryName] = ToJsonEntry() } };
        return root.ToJsonString(Indented);
    }

    internal JsonObject ToJsonEntry()
    {
        var args = new JsonArray();
        foreach (var a in Args) args.Add((JsonNode?)JsonValue.Create(a)); // the non-generic Add: Add<T> is not AOT-safe
        return new JsonObject { ["command"] = Command, ["args"] = args };
    }

    /// <summary>The Claude Code command that adds this server for every project of the user.</summary>
    public string ClaudeCodeAddCommand()
        => $"claude mcp add --scope user {EntryName} -- {Quote(Command)}{string.Concat(Args.Select(a => " " + Quote(a)))}";

    static string Quote(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_./\\:".Contains(c)) ? s : $"\"{s}\"";

    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Whether an agent config has RailReader2's entry.</summary>
public enum AgentConnection { NotInstalled, NotConnected, Connected, ConnectedElsewhere }

/// <summary>Thrown when an agent's config can't safely be changed (unparseable, or an entry RailReader2 didn't
/// write); the message is for the reader.</summary>
public sealed class AgentConfigException(string message) : Exception(message);

/// <summary>Claude Desktop's <c>claude_desktop_config.json</c>: adds or removes only <c>mcpServers.railreader2</c>,
/// keeping everything else, and refuses a file it can't parse.
/// <para>Only offered when Claude Desktop's folder already exists. The Store build must never create it: MSIX
/// redirects a folder a packaged app creates in AppData into the package's private copy, where Claude Desktop
/// would never look (measured by msix/test-msix-install.ps1; writes into an existing folder are real).</para></summary>
public sealed class ClaudeDesktopConfig(string path)
{
    public string Path { get; } = path;

    /// <summary>The usual location: <c>%APPDATA%\Claude</c> on Windows, <c>~/Library/Application Support/Claude</c>
    /// on macOS, <c>~/.config/Claude</c> elsewhere.</summary>
    public static ClaudeDesktopConfig Default()
    {
        string dir = OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude")
            : OperatingSystem.IsMacOS()
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Claude")
                : System.IO.Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "Claude");
        return new ClaudeDesktopConfig(System.IO.Path.Combine(dir, "claude_desktop_config.json"));
    }

    public bool IsInstalled => Directory.Exists(System.IO.Path.GetDirectoryName(Path));

    public AgentConnection Status(McpServerCommand? ours)
    {
        if (!IsInstalled) return AgentConnection.NotInstalled;
        JsonObject? root;
        try { root = Read(); } catch (AgentConfigException) { return AgentConnection.NotConnected; }
        if (root?["mcpServers"] is not JsonObject servers || servers[McpServerCommand.EntryName] is not JsonObject entry)
            return AgentConnection.NotConnected;
        return ours is not null && entry["command"]?.GetValueKind() == JsonValueKind.String
            && entry["command"]!.GetValue<string>() == ours.Command
            ? AgentConnection.Connected : AgentConnection.ConnectedElsewhere;
    }

    public void Connect(McpServerCommand server)
    {
        if (!IsInstalled) throw new AgentConfigException("Claude Desktop doesn't seem to be installed (its settings folder doesn't exist).");
        var root = Read() ?? new JsonObject();
        if (root["mcpServers"] is null) root["mcpServers"] = new JsonObject();
        if (root["mcpServers"] is not JsonObject servers)
            throw new AgentConfigException($"{Path} has an \"mcpServers\" entry that isn't an object; it was left alone.");
        servers[McpServerCommand.EntryName] = server.ToJsonEntry();
        Write(root);
    }

    public void Disconnect()
    {
        if (!File.Exists(Path)) return;
        var root = Read();
        if (root?["mcpServers"] is JsonObject servers && servers.Remove(McpServerCommand.EntryName))
            Write(root);
    }

    JsonObject? Read()
    {
        if (!File.Exists(Path)) return null;
        string text = File.ReadAllText(Path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new AgentConfigException($"{Path} isn't a JSON object; it was left alone.");
        }
        catch (JsonException ex)
        {
            throw new AgentConfigException($"{Path} isn't valid JSON ({ex.Message}); it was left alone.");
        }
    }

    void Write(JsonObject root) => AtomicWrite(Path, root.ToJsonString(McpServerCommand.Indented) + Environment.NewLine);

    internal static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".railreader2.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Codex's <c>config.toml</c> (<c>$CODEX_HOME</c>, else <c>~/.codex</c>). There is no TOML parser here,
/// so RailReader2 owns one marked block, appended at the end (a new table header is valid there whatever came
/// before), and removes exactly that block. Any other mention of <c>railreader2</c> outside the block means the
/// reader set it up by hand: the file is then left alone.</summary>
public sealed class CodexConfig(string path)
{
    const string Begin = "# >>> railreader2 (added by RailReader2 Settings; it removes this block) >>>";
    const string End = "# <<< railreader2 <<<";

    public string Path { get; } = path;

    public static CodexConfig Default()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return new CodexConfig(System.IO.Path.Combine(home, "config.toml"));
    }

    public bool IsInstalled => Directory.Exists(System.IO.Path.GetDirectoryName(Path));

    public AgentConnection Status(McpServerCommand? ours)
    {
        if (!IsInstalled) return AgentConnection.NotInstalled;
        string text = File.Exists(Path) ? File.ReadAllText(Path) : "";
        string? block; string outside;
        try { (block, outside) = Split(text); }
        catch (AgentConfigException) { return AgentConnection.ConnectedElsewhere; }
        if (block is not null)
            return ours is not null && block == BlockFor(ours) ? AgentConnection.Connected : AgentConnection.ConnectedElsewhere;
        return MentionsUs(outside) ? AgentConnection.ConnectedElsewhere : AgentConnection.NotConnected;
    }

    public void Connect(McpServerCommand server)
    {
        if (!IsInstalled) throw new AgentConfigException("Codex doesn't seem to be installed (its ~/.codex folder doesn't exist).");
        string text = File.Exists(Path) ? File.ReadAllText(Path) : "";
        var (_, outside) = Split(text);
        if (MentionsUs(outside))
            throw new AgentConfigException($"{Path} already has a railreader2 server that RailReader2 didn't add; it was left alone.");
        string body = outside.TrimEnd('\r', '\n');
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string result = (body.Length > 0 ? body + nl + nl : "") + BlockFor(server).Replace("\n", nl) + nl;
        ClaudeDesktopConfig.AtomicWrite(Path, result);
    }

    public void Disconnect()
    {
        if (!File.Exists(Path)) return;
        string text = File.ReadAllText(Path);
        var (block, outside) = Split(text);
        if (block is null) return;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string body = outside.TrimEnd('\r', '\n');
        ClaudeDesktopConfig.AtomicWrite(Path, body.Length > 0 ? body + nl : "");
    }

    /// <summary>Our block (normalised to \n, markers included), and the text without it.</summary>
    static (string? Block, string Outside) Split(string text)
    {
        int b = text.IndexOf(Begin, StringComparison.Ordinal);
        if (b < 0) return (null, text);
        int e = text.IndexOf(End, b, StringComparison.Ordinal);
        if (e < 0) throw new AgentConfigException("The railreader2 block in Codex's config.toml has lost its end marker; fix or remove it by hand.");
        e += End.Length;
        string block = text[b..e].Replace("\r\n", "\n");
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string before = text[..b].TrimEnd('\r', '\n'), after = text[e..].TrimStart('\r', '\n');
        string outside = before.Length > 0 && after.Length > 0 ? before + nl + nl + after : before + after;
        return (block, outside);
    }

    /// <summary>Any railreader2 server outside our block: a <c>[mcp_servers.railreader2]</c> table (bare or quoted
    /// key, any spacing) or a <c>railreader2 = …</c> key (inside <c>[mcp_servers]</c>, or dotted). Appending our
    /// table beside one of those would define the key twice and break the whole file for Codex.</summary>
    static bool MentionsUs(string toml) => UsPattern.IsMatch(toml);

    static readonly System.Text.RegularExpressions.Regex UsPattern = new(
        """mcp_servers\s*\.\s*["']?railreader2["']?|^\s*["']?railreader2["']?\s*[=.]""",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    internal static string BlockFor(McpServerCommand s)
        => $"{Begin}\n[mcp_servers.{McpServerCommand.EntryName}]\ncommand = {TomlString(s.Command)}\n"
           + $"args = [{string.Join(", ", s.Args.Select(TomlString))}]\n{End}";

    /// <summary>A TOML literal string ('...', no escapes: Windows paths stay readable) when possible,
    /// else a basic string with backslash and quote escaped.</summary>
    static string TomlString(string s)
        => !s.Contains('\'') && !s.Any(char.IsControl) ? $"'{s}'"
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Claude Code, through its own CLI (<c>claude mcp add|remove|get</c>, user scope): its
/// <c>~/.claude.json</c> is rewritten by every running session, so editing it directly would race them.
/// <paramref name="run"/> runs the CLI (program, arguments) and returns (exit code, stdout); tests fake it.</summary>
public sealed class ClaudeCodeConfig(string? claude, Func<string, IReadOnlyList<string>, (int Exit, string Output)> run)
{
    /// <summary>The <c>claude</c> program found on PATH or in its usual install places, or null.</summary>
    public string? Claude { get; } = claude;

    public static ClaudeCodeConfig Default() => new(Find(), Run);

    public bool IsInstalled => Claude is not null;

    public AgentConnection Status(McpServerCommand? ours)
    {
        if (Claude is null) return AgentConnection.NotInstalled;
        var (exit, output) = run(Claude, ["mcp", "get", McpServerCommand.EntryName]);
        if (exit != 0) return AgentConnection.NotConnected;
        return ours is not null && output.Contains(ours.Command, StringComparison.Ordinal)
            ? AgentConnection.Connected : AgentConnection.ConnectedElsewhere;
    }

    public void Connect(McpServerCommand server)
    {
        if (Claude is null) throw new AgentConfigException("Claude Code's `claude` command wasn't found.");
        // Replace an older entry (say, from a version installed elsewhere): add refuses an existing name.
        run(Claude, ["mcp", "remove", "--scope", "user", McpServerCommand.EntryName]);
        var (exit, output) = run(Claude, ["mcp", "add", "--scope", "user", McpServerCommand.EntryName, "--", server.Command, .. server.Args]);
        if (exit != 0) throw new AgentConfigException($"`claude mcp add` failed: {output.Trim()}");
    }

    public void Disconnect()
    {
        if (Claude is null) return;
        var (exit, output) = run(Claude, ["mcp", "remove", "--scope", "user", McpServerCommand.EntryName]);
        if (exit != 0 && !output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new AgentConfigException($"`claude mcp remove` failed: {output.Trim()}");
    }

    static string? Find()
    {
        string[] names = OperatingSystem.IsWindows() ? ["claude.exe", "claude.cmd"] : ["claude"];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A desktop launcher's PATH often lacks ~/.local/bin, where Claude Code's installer puts it.
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat([System.IO.Path.Combine(home, ".local", "bin"), System.IO.Path.Combine(home, ".claude", "local")]);
        foreach (var dir in dirs)
            foreach (var n in names)
            {
                try
                {
                    string candidate = System.IO.Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    static (int, string) Run(string program, IReadOnlyList<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        if (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(program);
        }
        else psi.FileName = program;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(20_000)) { try { p.Kill(true); } catch (Exception) { } return (-1, "timed out"); }
        return (p.ExitCode, output + err.Result);
    }
}
