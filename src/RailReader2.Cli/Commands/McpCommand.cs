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

        var tools = new LibraryTools(
            open: () => File.Exists(db) ? LibraryIndex.OpenReadOnly(db) : null,
            readerName: () => AppConfig.Load().AnnotationAuthor);
        var server = new McpServer(tools, Version(), logger);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        return server.Run(input, output);
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
        Console.WriteLine("  --db <path>   Use this index instead of the app's (for testing)");
    }
}
