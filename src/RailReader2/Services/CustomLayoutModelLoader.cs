using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Resolves which layout-detection model the analysis worker should load at
/// startup. If <see cref="CustomLayoutModelConfig.Enabled"/> is set and both
/// the model + mapping files parse cleanly, returns the user's custom model
/// with the mapped <see cref="LayoutModelCapabilities"/>. Otherwise falls
/// back to the bundled PP-DocLayoutV3 model located via
/// <see cref="LayoutModelLocator"/>.
/// </summary>
public static class CustomLayoutModelLoader
{
    public static (string? ModelPath, LayoutModelCapabilities? Capabilities) ResolveModel(
        AppConfig appConfig, ILogger logger)
    {
        var custom = CustomLayoutModelConfig.Load();
        if (custom.Enabled
            && !string.IsNullOrWhiteSpace(custom.ModelPath)
            && !string.IsNullOrWhiteSpace(custom.MappingPath))
        {
            if (!File.Exists(custom.ModelPath))
            {
                logger.Warn($"[ONNX] Custom model file not found: {custom.ModelPath} — falling back to default.");
            }
            else if (!File.Exists(custom.MappingPath))
            {
                logger.Warn($"[ONNX] Custom mapping file not found: {custom.MappingPath} — falling back to default.");
            }
            else
            {
                var (caps, error) = LoadCapabilities(custom.MappingPath);
                if (error != null)
                {
                    logger.Warn($"[ONNX] Custom mapping invalid ({error}) — falling back to default.");
                }
                else
                {
                    return (custom.ModelPath, caps);
                }
            }
        }

        var bundled = LayoutModelLocator.FindModelPath();
        if (bundled == null)
        {
            logger.Warn("[ONNX] Bundled PP-DocLayoutV3 model not found.");
            return (null, null);
        }
        return (bundled, RailReader.Core.Analysis.PPDocLayoutV3Roles.Capabilities);
    }

    /// <summary>
    /// Parses a class-mapping JSON file into <see cref="LayoutModelCapabilities"/>.
    /// Returns either the capabilities or a human-readable error message.
    /// </summary>
    public static (LayoutModelCapabilities? Capabilities, string? Error) LoadCapabilities(string mappingPath)
    {
        LayoutModelMappingFile? file;
        try
        {
            var json = File.ReadAllText(mappingPath);
            file = JsonSerializer.Deserialize(json, CustomLayoutModelJsonContext.Default.LayoutModelMappingFile);
        }
        catch (JsonException ex)
        {
            return (null, $"JSON parse error: {ex.Message}");
        }
        catch (IOException ex)
        {
            return (null, $"I/O error: {ex.Message}");
        }

        if (file == null) return (null, "empty mapping file");
        if (file.Classes.Count == 0) return (null, "mapping has no classes");
        if (file.InputSize <= 0) return (null, $"invalid input_size {file.InputSize}");

        var descriptors = new List<LayoutClassDescriptor>(file.Classes.Count);
        foreach (var c in file.Classes)
        {
            if (!Enum.TryParse<BlockRole>(c.Role, ignoreCase: false, out var role))
                return (null, $"class id {c.Id} has unknown role '{c.Role}' (expected one of: {string.Join(", ", Enum.GetNames<BlockRole>())})");
            descriptors.Add(new LayoutClassDescriptor(c.Id, c.Name, role));
        }

        // Sort by Id and verify the table is contiguous from 0 — the analyzer
        // indexes into capabilities.Classes by raw model class id.
        descriptors.Sort((a, b) => a.Id.CompareTo(b.Id));
        for (int i = 0; i < descriptors.Count; i++)
        {
            if (descriptors[i].Id != i)
                return (null, $"class table must be contiguous 0..N-1 (got gap at index {i}, id={descriptors[i].Id})");
        }

        return (new LayoutModelCapabilities(file.InputSize, descriptors, file.ProvidesReadingOrder), null);
    }
}
