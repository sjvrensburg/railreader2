using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// One of the analyzer implementations shipped with the app. The custom-model
/// path is selected separately via <see cref="CustomLayoutModelConfig.Enabled"/>
/// and takes precedence when active. Serialised as a string in
/// <c>custom_layout_model.json</c> so the user-facing docs can reference
/// readable names (<c>"PpDocLayoutV3"</c> / <c>"PpDocLayoutS"</c> / <c>"Heron"</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BuiltinAnalyzer>))]
public enum BuiltinAnalyzer
{
    /// <summary>Bundled with all release packages as a fallback.</summary>
    PpDocLayoutV3 = 0,
    /// <summary>Docling Heron INT8 (RT-DETRv2). Default model — see docs/heron-layout-model.md.</summary>
    Heron = 1,
    /// <summary>PP-DocLayout-S (PicoDet/GFL, ~4.7 MB). Lightweight detector intended for low-resource targets. Must be downloaded separately — see docs/pp-doclayout-s.md.</summary>
    PpDocLayoutS = 2,
}

/// <summary>
/// User-supplied layout-detection model. Lives alongside <c>config.json</c>
/// in railreader2's config dir as <c>custom_layout_model.json</c>. Kept
/// separate from the upstream <see cref="AppConfig"/> so the Core package
/// stays general-purpose.
///
/// When <see cref="Enabled"/> is true and both files resolve, startup loads
/// the user's ONNX with the role mapping defined in
/// <see cref="MappingPath"/>. Otherwise the analyzer named in
/// <see cref="BuiltinAnalyzer"/> is loaded (defaulting to Heron).
/// </summary>
public sealed class CustomLayoutModelConfig
{
    public bool Enabled { get; set; }
    public string? ModelPath { get; set; }
    public string? MappingPath { get; set; }
    /// <summary>Which shipped analyzer to use when the custom model is disabled or unavailable.</summary>
    public BuiltinAnalyzer BuiltinAnalyzer { get; set; } = BuiltinAnalyzer.Heron;

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "custom_layout_model.json");

    public static CustomLayoutModelConfig Load()
        => JsonSidecar.Load(Path, CustomLayoutModelJsonContext.Default.CustomLayoutModelConfig,
            static () => new CustomLayoutModelConfig());

    public void Save()
        => JsonSidecar.Save(Path, this, CustomLayoutModelJsonContext.Default.CustomLayoutModelConfig);
}

/// <summary>
/// On-disk format for the user-supplied class mapping JSON. One entry per
/// model class; <c>role</c> must parse as a <see cref="BlockRole"/> name
/// (case-sensitive — matches the enum's PascalCase).
///
/// Example:
/// <code>
/// {
///   "name": "DocLayout-YOLO custom",
///   "input_size": 1024,
///   "provides_reading_order": false,
///   "classes": [
///     { "id": 0, "name": "title",      "role": "Title" },
///     { "id": 1, "name": "plain_text", "role": "Text" },
///     { "id": 2, "name": "figure",     "role": "Figure" }
///   ]
/// }
/// </code>
/// </summary>
public sealed class LayoutModelMappingFile
{
    public string? Name { get; set; }
    public int InputSize { get; set; } = 800;
    public bool ProvidesReadingOrder { get; set; }
    public List<LayoutClassMappingEntry> Classes { get; set; } = [];
}

public sealed class LayoutClassMappingEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(CustomLayoutModelConfig))]
[JsonSerializable(typeof(LayoutModelMappingFile))]
[JsonSerializable(typeof(LayoutClassMappingEntry))]
internal partial class CustomLayoutModelJsonContext : JsonSerializerContext;
