using RailReader.Core;
using RailReader.Demo;

namespace RailReader.Cli.Commands;

/// <summary>
/// `railreader2-cli demo &lt;script&gt;` — drive a running RailReader2 (launched with
/// <c>--control-bus</c>) through a demo script over D-Bus. Phase B: parse + sequence; the recorder
/// (Phase C) and cursor follow (Phase D) are not wired yet (those script settings are parsed but
/// inert). Use <c>--dry-run</c> to validate a script without a running app.
/// </summary>
static class DemoCommand
{
    public static int Execute(string[] args, ILogger logger)
    {
        var scriptPath = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (scriptPath is null)
            return Program.Fail("demo requires a script path. Usage: railreader2-cli demo <script> [--bus-name N] [--dry-run]");
        if (!File.Exists(scriptPath))
            return Program.Fail($"Script not found: {scriptPath}");

        DemoScript script;
        try
        {
            script = DslParser.Parse(File.ReadAllText(scriptPath));
        }
        catch (DslParseException ex)
        {
            return Program.Fail($"Parse error: {ex.Message}");
        }

        // Resolve the source PDF relative to the script's own directory.
        if (script.Source is { Length: > 0 } src && !Path.IsPathRooted(src))
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? ".";
            script = script with { Source = Path.GetFullPath(Path.Combine(baseDir, src)) };
        }

        if (Program.HasFlag(args, "dry-run"))
        {
            PrintPlan(script);
            return 0;
        }

        if (script.Recorder is { Length: > 0 } rec)
            Console.WriteLine($"Note: recorder '{rec}' is not wired yet (Phase C) — running without capture.");

        var busName = Program.GetOption(args, "bus-name");
        var timeoutMs = Program.GetOption(args, "settle-timeout");
        var settle = int.TryParse(timeoutMs, out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(10);

        try
        {
            return RunAsync(script, busName, settle).GetAwaiter().GetResult();
        }
        catch (DemoRunException ex)
        {
            return Program.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return Program.Fail($"Could not reach the app over D-Bus ({ex.Message}). " +
                "Launch it with: railreader2 --control-bus");
        }
    }

    private static async Task<int> RunAsync(DemoScript script, string? busName, TimeSpan settle)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var client = await DBusControlClient.CreateAsync(busName);
        var sequencer = new DemoSequencer(client, Console.Out, settleTimeout: settle);
        await sequencer.RunAsync(script, cts.Token);
        return 0;
    }

    private static void PrintPlan(DemoScript script)
    {
        Console.WriteLine($"demo:     {script.Name ?? "(unnamed)"}");
        Console.WriteLine($"source:   {script.Source ?? "(none)"}");
        if (script.Fps is { } fps) Console.WriteLine($"fps:      {fps}");
        if (script.Cursor is { } c) Console.WriteLine($"cursor:   {c}");
        if (script.Recorder is { } r) Console.WriteLine($"recorder: {r}");
        if (script.Output is { } o) Console.WriteLine($"output:   {o}");
        Console.WriteLine($"steps:    {script.Steps.Count}");
        foreach (var s in script.Steps)
        {
            var argsStr = s.Args.Count == 0 ? "" : " " + string.Join(" ", s.Args.Select(kv => $"{kv.Key}={kv.Value}"));
            var waitStr = s.Wait is null ? "" : $"  [wait: {s.Wait}]";
            Console.WriteLine($"  - {s.Verb}{argsStr}{waitStr}");
        }
    }
}
