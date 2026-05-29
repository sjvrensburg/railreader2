using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Resolves which layout-detection model the analysis worker should load at
/// startup, plus how to construct the analyzer for it.
///
/// Precedence:
/// <list type="number">
///   <item>User-supplied custom model (PP-style I/O contract) if
///         <see cref="CustomLayoutModelConfig.Enabled"/> and both files resolve.</item>
///   <item>The <see cref="BuiltinAnalyzer"/> named in the config (defaults to
///         Heron). If Heron or PP-DocLayout-S is selected but its
///         .onnx file is not found at any locator probe path, falls back to
///         PP-DocLayoutV3 with a warning rather than dropping into layout-less
///         mode.</item>
///   <item>Layout-less mode (analyzer not initialised) only when *no* model
///         file can be located.</item>
/// </list>
/// </summary>
public static class CustomLayoutModelLoader
{
    public readonly record struct Resolution(
        string? ModelPath,
        LayoutModelCapabilities? Capabilities,
        Func<ILayoutAnalyzer>? Factory,
        string? DisplayName);

    public static Resolution ResolveModel(AppConfig appConfig, ILogger logger)
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
                    var customPath = custom.ModelPath!;
                    var customCaps = caps!;
                    var customName = $"Custom: {Path.GetFileName(customPath)}";
                    return new Resolution(customPath, customCaps,
                        () => new LayoutAnalyzer(customPath, customCaps),
                        customName);
                }
            }
        }

        return ResolveBuiltin(custom.BuiltinAnalyzer, logger);
    }

    private static Resolution ResolveBuiltin(BuiltinAnalyzer choice, ILogger logger)
    {
        if (choice == BuiltinAnalyzer.Heron)
        {
            var heronPath = HeronModelLocator.FindModelPath();
            if (heronPath != null)
            {
                var desc = LayoutModelRegistry.HeronInt8;
                return new Resolution(heronPath,
                    LayoutAnalyzerFactory.CapabilitiesFor(desc.Architecture),
                    () => LayoutAnalyzerFactory.Create(desc, heronPath),
                    desc.DisplayName);
            }
            logger.Warn($"[ONNX] Docling Heron model not found ({HeronModelLocator.FileName}) — falling back to PP-DocLayoutV3. See docs/heron-layout-model.md.");
            // fall through to PP
        }
        else if (choice == BuiltinAnalyzer.PpDocLayoutS)
        {
            var ppsPath = PPDocLayoutSModelLocator.FindModelPath();
            if (ppsPath != null)
            {
                var desc = LayoutModelRegistry.PPDocLayoutS;
                return new Resolution(ppsPath,
                    LayoutAnalyzerFactory.CapabilitiesFor(desc.Architecture),
                    () => LayoutAnalyzerFactory.Create(desc, ppsPath),
                    desc.DisplayName);
            }
            logger.Warn($"[ONNX] PP-DocLayout-S model not found ({PPDocLayoutSModelLocator.FileName}) — falling back to PP-DocLayoutV3. See docs/pp-doclayout-s.md.");
            // fall through to PP
        }

        // Final fallback: PP-DocLayoutV3 (bundled)
        var v3Desc = LayoutModelRegistry.PPDocLayoutV3;
        var bundled = LayoutModelLocator.FindModelPath(v3Desc);
        if (bundled == null)
        {
            logger.Warn("[ONNX] Bundled PP-DocLayoutV3 model not found.");
            return new Resolution(null, null, null, null);
        }
        return new Resolution(bundled,
            LayoutAnalyzerFactory.CapabilitiesFor(v3Desc.Architecture),
            () => LayoutAnalyzerFactory.Create(v3Desc, bundled),
            v3Desc.DisplayName);
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
