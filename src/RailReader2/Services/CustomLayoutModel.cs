using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// User-supplied layout-detection model. Lives alongside <c>config.json</c>
/// in railreader2's config dir as <c>custom_layout_model.json</c>. Kept
/// separate from the upstream <see cref="AppConfig"/> so the Core package
/// stays general-purpose.
///
/// When <see cref="Enabled"/> is true and both files resolve, startup loads
/// the user's ONNX with the role mapping defined in
/// <see cref="MappingPath"/>. Otherwise the analyzer falls back to the
/// bundled PP-DocLayoutV3 model.
/// </summary>
public sealed class CustomLayoutModelConfig
{
    public bool Enabled { get; set; }
    public string? ModelPath { get; set; }
    public string? MappingPath { get; set; }

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "custom_layout_model.json");

    public static CustomLayoutModelConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize(json, CustomLayoutModelJsonContext.Default.CustomLayoutModelConfig)
                    ?? new CustomLayoutModelConfig();
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to load custom_layout_model.json", ex);
        }
        return new CustomLayoutModelConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, CustomLayoutModelJsonContext.Default.CustomLayoutModelConfig);
            File.WriteAllText(Path, json);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to save custom_layout_model.json", ex);
        }
    }
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
