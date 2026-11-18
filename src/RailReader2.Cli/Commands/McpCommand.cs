using System.Reflection;
using System.Text;
using RailReader.Cli.Mcp;
using RailReader.Core;
using RailReader.Core.Services;
using RailReader2.Library;

namespace RailReader.Cli.Commands;

/// <summary><c>mcp</c>: serves the library index to AI tools over the Model Context Protocol (stdio).
/// Read-only: the index is opened read-only per tool call and never written (roadmap M8).</summary>
public static class McpCommand
{
    public static int Execute(string[] args)
    {
        if (Program.HasHelpFlag(args)) { PrintHelp(); return 0; }

        var logger = new StderrLogger();
        RailReaderLogging.Logger = logger;
        string db = Program.GetOption(args, "db") ?? LibraryIndex.DefaultPath;
        string? prefsPath = Program.GetOption(args, "prefs");

        string? sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        if (sub is not null)
        {
            try
            {
                return sub switch
                {
                    "status" => Status(db, prefsPath),
                    "enable" => SetEnabled(true, prefsPath),
                    "disable" => SetEnabled(false, prefsPath),
                    "hide" => Hide(args[1..], db, prefsPath, hide: true),
                    "unhide" => Hide(args[1..], db, prefsPath, hide: false),
                    _ => Program.Fail($"Unknown mcp subcommand '{sub}'. Run 'mcp --help'."),
                };
            }
            catch (Exception ex) { return Program.Fail(ex.Message); } // runs outside Program.Main's handler
        }

        var tools = new LibraryTools(
            open: () => File.Exists(db) ? LibraryIndex.OpenReadOnly(db) : null,
            readerName: () => AppConfig.Load().AnnotationAuthor,
            prefs: () => AiToolsPreferences.Load(prefsPath));
        var server = new McpServer(tools, Version(), logger);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        return server.Run(input, output);
    }

    static int Status(string db, string? prefsPath)
    {
        var prefs = AiToolsPreferences.Load(prefsPath);
        Console.WriteLine($"AI tools: {(prefs.Enabled ? "on" : "off")}");
        Console.WriteLine($"Preferences: {prefsPath ?? AiToolsPreferences.DefaultPath}");
        using var index = File.Exists(db) ? LibraryIndex.OpenReadOnly(db) : null;
        Console.WriteLine($"Library index: {db}{(index is null ? " (none yet)" : "")}");
        if (index is not null)
        {
            var all = index.GetDocuments();
            int visible = all.Count(d => d.State == DocumentState.Indexed && !d.Encrypted && !prefs.IsHidden(d.ContentId, d.LastPath));
            Console.WriteLine($"Documents AI tools can read: {visible} of {all.Count}");
        }
        if (prefs.HiddenDocuments.Count > 0)
        {
            Console.WriteLine("Hidden documents:");
            var byId = index?.GetDocuments().ToDictionary(d => d.ContentId, StringComparer.Ordinal);
            foreach (var id in prefs.HiddenDocuments)
                Console.WriteLine($"  {LibraryTools.ShortId(id)}  {(byId?.GetValueOrDefault(id) is { } d ? d.Title ?? d.FileName : "(not in the index)")}");
        }
        if (prefs.HiddenFolders.Count > 0)
        {
            Console.WriteLine("Hidden folders:");
            foreach (var f in prefs.HiddenFolders) Console.WriteLine($"  {f}");
        }
        return 0;
    }

    static int SetEnabled(bool on, string? prefsPath)
    {
        AiToolsPreferences.Update(p => p.Enabled = on, prefsPath);
        Console.WriteLine(on
            ? "AI tools are on: connected tools can read your notes and the text of documents you have opened (not hidden or encrypted ones)."
            : "AI tools are off: every tool call is refused, including from tools already connected.");
        return 0;
    }

    /// <summary><c>hide|unhide &lt;document&gt;</c> (id, title or file name) or <c>hide|unhide --folder &lt;dir&gt;</c>.</summary>
    static int Hide(string[] args, string db, string? prefsPath, bool hide)
    {
        string verb = hide ? "hide" : "unhide";
        if (Program.GetOption(args, "folder") is { } folder)
        {
            bool changed = true;
            AiToolsPreferences.Update(p => { if (hide) p.HideFolder(folder); else changed = p.UnhideFolder(folder); }, prefsPath);
            Console.WriteLine(!changed ? $"{folder} was not a hidden folder."
                : hide ? $"Documents under {Path.GetFullPath(folder)} are hidden from AI tools." : $"{folder} is no longer hidden.");
            return 0;
        }
        string? key = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (key is null) return Program.Fail($"mcp {verb} needs a document (id, title or file name) or --folder <dir>.");
        using var index = File.Exists(db) ? LibraryIndex.OpenReadOnly(db) : null;
        if (index is null) return Program.Fail($"No readable library index at {db}.");
        // Any indexed document, hidden or not: that is the point of this command.
        var docs = index.GetDocuments().ToDictionary(d => d.ContentId, StringComparer.Ordinal);
        var doc = LibraryTools.Resolve(docs, key);
        AiToolsPreferences.Update(p => { if (hide) p.Hide(doc.ContentId); else p.Unhide(doc.ContentId); }, prefsPath);
        Console.WriteLine($"{doc.Title ?? doc.FileName} is {(hide ? "now hidden from" : "visible again to")} AI tools.");
        return 0;
    }

    static string Version()
    {
        string v = typeof(McpCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    static void PrintHelp()
    {
        Console.WriteLine("Usage: railreader2-cli mcp [--db <path>]");
        Console.WriteLine("       railreader2-cli mcp status|enable|disable");
        Console.WriteLine("       railreader2-cli mcp hide|unhide <document> | --folder <dir>");
        Console.WriteLine();
        Console.WriteLine("Runs a Model Context Protocol server on stdin/stdout for AI tools such as Claude Code.");
        Console.WriteLine("It reads the RailReader2 library index read-only: your notes and the page text of the");
        Console.WriteLine("documents you have opened. It never returns file paths, PDF files or images, and nothing");
        Console.WriteLine("from encrypted documents.");
        Console.WriteLine();
        Console.WriteLine("Tools: search_notes, search_pages, list_documents, get_document_notes");
        Console.WriteLine();
        Console.WriteLine("Add it to Claude Code with:");
        Console.WriteLine("  claude mcp add --scope user railreader2 -- <path-to>/railreader2-cli mcp");
        Console.WriteLine();
        Console.WriteLine("AI tools are off until you turn them on (here with 'mcp enable', or in the app). While off,");
        Console.WriteLine("every tool call is refused. Hidden documents and folders never appear in any tool's output.");
        Console.WriteLine();
        Console.WriteLine("  status          Whether AI tools are on, and what is hidden");
        Console.WriteLine("  enable|disable  Turn AI tools on or off (applies to tools already connected)");
        Console.WriteLine("  hide|unhide     Hide a document (id, title or file name) or a folder from AI tools");
        Console.WriteLine("  --db <path>     Use this index instead of the app's (for testing)");
        Console.WriteLine("  --prefs <path>  Use this preferences file instead of the app's (for testing)");
    }
}
