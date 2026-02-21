using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader2.Models;

namespace RailReader2.Services;

public static class AnnotationService
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string GetSidecarPath(string pdfPath)
    {
        var dir = Path.GetDirectoryName(pdfPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        return Path.Combine(dir, $"{name}.railreader2.json");
    }

    public static AnnotationFile? Load(string pdfPath)
    {
        var path = GetSidecarPath(pdfPath);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AnnotationFile>(json, s_options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Annotations] Failed to load {path}: {ex.Message}");
            return null;
        }
    }

    public static void Save(string pdfPath, AnnotationFile annotations)
    {
        var path = GetSidecarPath(pdfPath);
        try
        {
            var json = JsonSerializer.Serialize(annotations, s_options);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Annotations] Failed to save {path}: {ex.Message}");
        }
    }
}
