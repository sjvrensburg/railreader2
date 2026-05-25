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
    internal enum Builtin { PpDocLayoutV3 = 0, Heron = 1 }

    internal const string HeronFileName = "docling-layout-heron.onnx";

    /// <summary>Returns the user's analyzer choice, or PP-DocLayoutV3 if no config / parse error.</summary>
    internal static Builtin LoadChoice()
    {
        try
        {
            var path = Path.Combine(AppConfig.ConfigDir, "custom_layout_model.json");
            if (!File.Exists(path)) return Builtin.PpDocLayoutV3;
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize(json, LayoutModelChoiceJsonContext.Default.ChoiceDocument);
            return cfg?.BuiltinAnalyzer ?? Builtin.PpDocLayoutV3;
        }
        catch
        {
            return Builtin.PpDocLayoutV3;
        }
    }

    /// <summary>Probe locations for the Heron model, in priority order.</summary>
    internal static string? FindHeronModelPath()
    {
        foreach (var p in HeronProbePaths())
            if (File.Exists(p)) return p;
        return null;
    }

    private static IEnumerable<string> HeronProbePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "models", HeronFileName);

        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (!string.IsNullOrEmpty(appDir))
            yield return Path.Combine(appDir, "models", HeronFileName);

        yield return Path.Combine(AppConfig.ConfigDir, "models", HeronFileName);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "railreader2", "models", HeronFileName);

        yield return Path.Combine(Directory.GetCurrentDirectory(), "models", HeronFileName);

        var cwd = Directory.GetCurrentDirectory();
        for (int up = 1; up <= 3; up++)
        {
            var parent = Directory.GetParent(cwd)?.FullName;
            if (parent is null) break;
            yield return Path.Combine(parent, "models", HeronFileName);
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
        public Builtin BuiltinAnalyzer { get; set; } = Builtin.PpDocLayoutV3;
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(ChoiceDocument))]
    internal partial class LayoutModelChoiceJsonContext : JsonSerializerContext;
}
