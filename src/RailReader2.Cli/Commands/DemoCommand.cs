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

        // Resolve the source PDF and output video relative to the script's own directory.
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? ".";
        if (script.Source is { Length: > 0 } src && !Path.IsPathRooted(src))
            script = script with { Source = Path.GetFullPath(Path.Combine(baseDir, src)) };
        if (script.Output is { Length: > 0 } outp && !Path.IsPathRooted(outp))
            script = script with { Output = Path.GetFullPath(Path.Combine(baseDir, outp)) };

        if (Program.HasFlag(args, "dry-run"))
        {
            PrintPlan(script);
            return 0;
        }

        var busName = Program.GetOption(args, "bus-name");
        var timeoutMs = Program.GetOption(args, "settle-timeout");
        var settle = int.TryParse(timeoutMs, out var ms) ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(10);

        var recorder = SelectRecorder(script);
        if (recorder is null && script.Recorder is { Length: > 0 } rec)
            return Program.Fail($"unknown recorder '{rec}' (supported: portal, gnome, screen, none)");
        if (recorder is not null && string.IsNullOrEmpty(script.Output))
            return Program.Fail("a recorder is set but no 'output:' path was given");

        try
        {
            return RunAsync(script, busName, settle, recorder ?? new NullScreenRecorder()).GetAwaiter().GetResult();
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

    private static async Task<int> RunAsync(DemoScript script, string? busName, TimeSpan settle, IScreenRecorder recorder)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var rec = recorder;
        await using var client = await DBusControlClient.CreateAsync(busName);
        var sequencer = new DemoSequencer(client, Console.Out, settleTimeout: settle);
        await sequencer.RunAsync(script, rec, cts.Token);
        return 0;
    }

    /// <summary>Map the script's <c>recorder:</c> to an implementation. Null only when the value is
    /// unrecognised; an absent setting yields the no-op recorder.</summary>
    private static IScreenRecorder? SelectRecorder(DemoScript script) => script.Recorder?.ToLowerInvariant() switch
    {
        null or "" or "none" => new NullScreenRecorder(),
        "portal" or "gnome" or "screen" => new GnomeScreenRecorder(Console.Out),
        _ => null,
    };

    private static void PrintPlan(DemoScript script)
    {
        Console.WriteLine($"demo:     {script.Name ?? "(unnamed)"}");
        Console.WriteLine($"source:   {script.Source ?? "(none)"}");
        if (script.Fps is { } fps) Console.WriteLine($"fps:      {fps}");
        if (script.Cursor is { } c) Console.WriteLine($"cursor:   {c}");
        if (script.Recorder is { } r) Console.WriteLine($"recorder: {r}");
        if (script.Output is { } o) Console.WriteLine($"output:   {o}");
        if (script.Fullscreen) Console.WriteLine("fullscreen: true");
        Console.WriteLine($"steps:    {script.Steps.Count}");
        foreach (var s in script.Steps)
        {
            var argsStr = s.Args.Count == 0 ? "" : " " + string.Join(" ", s.Args.Select(kv => $"{kv.Key}={kv.Value}"));
            var waitStr = s.Wait is null ? "" : $"  [wait: {s.Wait}]";
            Console.WriteLine($"  - {s.Verb}{argsStr}{waitStr}");
        }
    }
}
