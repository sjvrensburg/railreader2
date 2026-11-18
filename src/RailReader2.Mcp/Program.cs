// railreader2-mcp: read-only MCP server over the RailReader2 library index (roadmap M8).
// Everything lives in RailReader2.Library.Mcp so `railreader2-cli mcp` is the same server.
// The name the reader can actually type (help text, the "turned off" message): the execution alias exists only in
// the Store build; an AppImage is reached through its own file with --mcp; elsewhere the program's own path.
string command = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? $"\"{appImage}\" --mcp"
    : Environment.ProcessPath is { } exe && !exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
        ? (exe.Contains(' ') ? $"\"{exe}\"" : exe)
        : "railreader2-mcp";
return RailReader2.Library.Mcp.McpCli.Run(args, command);
