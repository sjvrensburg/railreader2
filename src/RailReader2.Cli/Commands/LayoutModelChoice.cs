using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader.Cli.Commands;

/// <summary>
/// CLI-side mirror of the GUI's analyzer-choice config. Reads (read-only) the
/// same <c>custom_layout_model.json</c> sidecar the GUI writes, so the CLI
/// transparently picks up whatever the user selected in Settings → Layout
/// model.
///
/// Kept minimal and self-contained so the CLI does not need to project-ref
/// the Avalonia shell.
/// </summary>
internal static partial class LayoutModelChoice
{
    internal enum Builtin { PpDocLayoutV3 = 0, Heron = 1, PpDocLayoutS = 2 }

    internal const string HeronFileName = "docling-layout-heron-int8.onnx";
    internal const string PpsFileName = "pp_doclayout_s.onnx";

    /// <summary>Returns the user's analyzer choice, or Heron if no config / parse error.</summary>
    internal static Builtin LoadChoice()
    {
        try
        {
            var path = Path.Combine(AppConfig.ConfigDir, "custom_layout_model.json");
            if (!File.Exists(path)) return Builtin.Heron;
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize(json, LayoutModelChoiceJsonContext.Default.ChoiceDocument);
            return cfg?.BuiltinAnalyzer ?? Builtin.Heron;
        }
        catch
        {
            return Builtin.Heron;
        }
    }

    /// <summary>Probe locations for the Heron model, in priority order.</summary>
    internal static string? FindHeronModelPath() => FindModelPath(HeronFileName);

    /// <summary>Probe locations for the PP-DocLayout-S model, in priority order.</summary>
    internal static string? FindPpsModelPath() => FindModelPath(PpsFileName);

    private static string? FindModelPath(string fileName)
    {
        foreach (var p in ProbePaths(fileName))
            if (File.Exists(p)) return p;
        return null;
    }

    private static IEnumerable<string> ProbePaths(string fileName)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models", fileName);

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appDir))
            yield return Path.Combine(appDir, "models", fileName);

        yield return Path.Combine(AppConfig.ConfigDir, "models", fileName);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "railreader2", "models", fileName);

        yield return Path.Combine(Directory.GetCurrentDirectory(), "models", fileName);

        var cwd = Directory.GetCurrentDirectory();
        for (int up = 1; up <= 3; up++)
        {
            var parent = Directory.GetParent(cwd)?.FullName;
            if (parent is null) break;
            yield return Path.Combine(parent, "models", fileName);
            cwd = parent;
        }
    }

    /// <summary>
    /// Subset of the GUI's <c>custom_layout_model.json</c>: only the field
    /// this side needs. Extra fields in the file are ignored.
    /// </summary>
    internal sealed class ChoiceDocument
    {
        [JsonConverter(typeof(JsonStringEnumConverter<Builtin>))]
        public Builtin BuiltinAnalyzer { get; set; } = Builtin.Heron;
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(ChoiceDocument))]
    internal partial class LayoutModelChoiceJsonContext : JsonSerializerContext;
}
