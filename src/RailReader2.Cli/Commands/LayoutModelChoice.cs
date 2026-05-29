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
    private const string HeronLegacyFileName = "docling-layout-heron.onnx";
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
    internal static string? FindHeronModelPath()
    {
        var path = LayoutModelLocator.FindModelPath(HeronFileName);
        if (path != null) return path;

        // Backward compat: probe for the old FP32 filename
        return LayoutModelLocator.FindModelPath(HeronLegacyFileName);
    }

    /// <summary>Probe locations for the PP-DocLayout-S model, in priority order.</summary>
    internal static string? FindPpsModelPath() => LayoutModelLocator.FindModelPath(PpsFileName);

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
