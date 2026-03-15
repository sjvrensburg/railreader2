using System.CommandLine;

namespace RailReader.Cli.Commands;

public static class ReplCommand
{
    public static Command Create(RootCommand rootCommand)
    {
        var cmd = new Command("repl", "Enter interactive REPL mode");
        cmd.SetAction(pr =>
        {
            Console.WriteLine("railreader CLI — interactive mode");
            Console.WriteLine("Type 'help' for commands, 'exit' to quit.\n");

            while (true)
            {
                Console.Write("railreader> ");
                var line = Console.ReadLine();
                if (line is null) break; // EOF
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line is "exit" or "quit") break;

                if (line == "help")
                {
                    PrintHelp(rootCommand);
                    continue;
                }

                // Parse and invoke as if it were a command-line invocation
                // Split respecting quoted strings
                var args = SplitArgs(line);
                rootCommand.Parse(args).Invoke();
            }

            Console.WriteLine("Goodbye.");
        });

        return cmd;
    }

    private static void PrintHelp(RootCommand root)
    {
        Console.WriteLine("Command groups:");
        foreach (var sub in root.Subcommands)
        {
            if (sub.Name == "repl") continue;
            Console.Write($"  {sub.Name,-16}");
            Console.WriteLine(sub.Description ?? "");
            foreach (var child in sub.Subcommands)
                Console.WriteLine($"    {child.Name,-14}{child.Description ?? ""}");
        }
        Console.WriteLine();
    }

    private static string[] SplitArgs(string line)
    {
        var args = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            if (line[i] == '"' || line[i] == '\'')
            {
                var quote = line[i++];
                int start = i;
                while (i < line.Length && line[i] != quote) i++;
                args.Add(line[start..i]);
                if (i < line.Length) i++; // skip closing quote
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                args.Add(line[start..i]);
            }
        }
        return args.ToArray();
    }
}
