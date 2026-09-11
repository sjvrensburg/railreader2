using System.Text.Json;
using RailReader.Core;
using RailReader.Core.Analysis;
using RailReader.Core.Analysis.WebGpu;
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
    /// <param name="Architecture">Null for the custom-model path (no registry entry) and for "no
    /// model found" — otherwise the resolved built-in architecture, for callers that need more than
    /// the display string (e.g. the debug overlay / Models tab's suboptimal-combo advisory).</param>
    /// <param name="IsGpu">True only when this resolution actually went through <see cref="TryResolveGpu"/>
    /// — i.e. GPU was requested, a device was found, and the GPU model file is on disk. A rare
    /// construction-time failure inside the deferred <c>Factory</c> can still fall back to CPU
    /// (logged, not reflected here) — this is a resolve-time signal, not a live one.</param>
    public readonly record struct Resolution(
        string? ModelPath,
        LayoutModelCapabilities? Capabilities,
        Func<ILayoutAnalyzer>? Factory,
        string? DisplayName,
        LayoutModelArchitecture? Architecture,
        bool IsGpu);

    /// <summary>
    /// A narrow, <em>in-memory-only</em> heuristic — true when <paramref name="custom"/>'s raw
    /// field shape doesn't outright preclude GPU (not the custom-model path, not PP-DocLayout-S)
    /// AND <see cref="CustomLayoutModelConfig.Accelerator"/> says GPU.
    ///
    /// <para>
    /// <b>This is not the GPU mutual-exclusion gate — do not use it to decide whether OCR may
    /// claim the GPU slot.</b> It doesn't know about <see cref="ResolveModel"/>'s fallback
    /// behavior: a custom model whose files are missing/invalid falls through to
    /// <see cref="ResolveBuiltin"/>, which can still land on GPU; PP-DocLayout-S with a missing
    /// file falls through to PP-DocLayoutV3, same story. A caller that needs the real answer —
    /// "would layout actually end up on GPU" — must call <see cref="ResolveModel"/> itself and
    /// read <see cref="Resolution.IsGpu"/>, the single source of truth every such decision now
    /// uses (the worker-init gate, and Settings' equivalent checks/status text). This heuristic
    /// exists only for callers that need to clear an about-to-become-stale <c>Accelerator</c>
    /// <em>before</em> saving a config change (so <see cref="ResolveModel"/>, which always reads
    /// the saved file, isn't safe to call yet) — see <c>SettingsWindow.ClearStaleGpuAcceleratorIfIncompatible</c>.
    /// Getting this heuristic wrong only leaves a harmless stale checkbox/flag, since it no longer
    /// feeds the actual gate — that's what makes it safe to keep this loose.
    /// </para>
    /// </summary>
    public static bool CanConfigShapeUseGpu(CustomLayoutModelConfig custom)
        => custom.Accelerator == AcceleratorPreference.Gpu
           && !custom.Enabled
           && custom.BuiltinAnalyzer is BuiltinAnalyzer.Heron or BuiltinAnalyzer.PpDocLayoutV3;

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
                        customName, Architecture: null, IsGpu: false);
                }
            }
        }

        return ResolveBuiltin(custom.BuiltinAnalyzer, custom.Accelerator, logger);
    }

    private static Resolution ResolveBuiltin(BuiltinAnalyzer choice, AcceleratorPreference accelerator, ILogger logger)
    {
        if (choice == BuiltinAnalyzer.Heron)
        {
            if (TryResolveGpu(LayoutModelArchitecture.Heron, accelerator, logger) is { } gpuHeron)
                return gpuHeron;

            var heronPath = HeronModelLocator.FindModelPath();
            if (heronPath != null)
            {
                var desc = LayoutModelRegistry.HeronInt8;
                return new Resolution(heronPath,
                    LayoutAnalyzerFactory.CapabilitiesFor(desc.Architecture),
                    () => LayoutAnalyzerFactory.Create(desc, heronPath),
                    desc.DisplayName, desc.Architecture, IsGpu: false);
            }
            logger.Warn($"[ONNX] Docling Heron model not found ({HeronModelLocator.FileName}) — falling back to PP-DocLayoutV3. See docs/heron-layout-model.md.");
            // fall through to PP
        }
        else if (choice == BuiltinAnalyzer.PpDocLayoutS)
        {
            // No GPU/FP16 variant exists for this architecture — always CPU.
            var ppsPath = PPDocLayoutSModelLocator.FindModelPath();
            if (ppsPath != null)
            {
                var desc = LayoutModelRegistry.PPDocLayoutS;
                return new Resolution(ppsPath,
                    LayoutAnalyzerFactory.CapabilitiesFor(desc.Architecture),
                    () => LayoutAnalyzerFactory.Create(desc, ppsPath),
                    desc.DisplayName, desc.Architecture, IsGpu: false);
            }
            logger.Warn($"[ONNX] PP-DocLayout-S model not found ({PPDocLayoutSModelLocator.FileName}) — falling back to PP-DocLayoutV3. See docs/pp-doclayout-s.md.");
            // fall through to PP
        }

        if (TryResolveGpu(LayoutModelArchitecture.PPDocLayoutV3, accelerator, logger) is { } gpuV3)
            return gpuV3;

        // Final fallback: PP-DocLayoutV3 (bundled)
        var v3Desc = LayoutModelRegistry.PPDocLayoutV3;
        var bundled = LayoutModelLocator.FindModelPath(v3Desc);
        if (bundled == null)
        {
            logger.Warn("[ONNX] Bundled PP-DocLayoutV3 model not found.");
            return new Resolution(null, null, null, null, Architecture: null, IsGpu: false);
        }
        return new Resolution(bundled,
            LayoutAnalyzerFactory.CapabilitiesFor(v3Desc.Architecture),
            () => LayoutAnalyzerFactory.Create(v3Desc, bundled),
            v3Desc.DisplayName, v3Desc.Architecture, IsGpu: false);
    }

    /// <summary>
    /// Resolves <paramref name="architecture"/> against its GPU (FP16) model descriptor when the
    /// user opted into GPU acceleration, a compatible device is present, and the FP16 file is on
    /// disk. Returns null (fall through to the caller's CPU resolution) for any other case,
    /// including an architecture with no GPU/FP16 variant (<see cref="LayoutModelRegistry.Resolve"/>
    /// then just returns the CPU descriptor, which this treats as "no GPU path").
    ///
    /// The returned <see cref="Resolution.Factory"/> is invoked lazily on the analysis worker's
    /// own thread (<c>AnalysisWorker.LayoutLoop</c>), not here — so the actual
    /// enable/construct/disable sequence around <see cref="WebGpuAccelerator.ConstructionLock"/>
    /// lives inside that deferred delegate, not at resolve time (Core's execution-provider hook
    /// is a static field consumed only at <c>InferenceSession</c> construction, which must not
    /// happen until the factory runs). Also retries CPU inline if GPU construction throws at that
    /// point, so a driver/runtime hiccup degrades gracefully instead of killing the whole worker
    /// (<see cref="AnalysisWorker.StartupError"/> is fatal for the session).
    /// </summary>
    private static Resolution? TryResolveGpu(LayoutModelArchitecture architecture, AcceleratorPreference accelerator, ILogger logger)
    {
        if (accelerator != AcceleratorPreference.Gpu) return null;

        var gpuDesc = LayoutModelRegistry.Resolve(architecture, AcceleratorPreference.Gpu);
        var cpuDesc = LayoutModelRegistry.Resolve(architecture, AcceleratorPreference.Cpu);
        if (gpuDesc.Id == cpuDesc.Id) return null; // no GPU/FP16 variant for this architecture

        if (!WebGpuAccelerator.IsAvailable)
        {
            logger.Warn("[WebGPU] No compatible GPU device found — using CPU.");
            return null;
        }

        var gpuPath = LayoutModelLocator.FindModelPath(gpuDesc);
        if (gpuPath == null)
        {
            logger.Warn($"[WebGPU] {gpuDesc.DisplayName} not found on disk ({gpuDesc.FileName}) — using CPU. Download it in Settings.");
            return null;
        }

        var cpuPath = LayoutModelLocator.FindModelPath(cpuDesc);

        ILayoutAnalyzer Construct()
        {
            lock (WebGpuAccelerator.ConstructionLock)
            {
                if (WebGpuAccelerator.TryEnable(architecture))
                {
                    try
                    {
                        return LayoutAnalyzerFactory.Create(gpuDesc, gpuPath);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn($"[WebGPU] GPU analyzer construction failed, falling back to CPU: {ex.Message}");
                    }
                    finally
                    {
                        WebGpuAccelerator.Disable(architecture);
                    }
                }

                if (cpuPath == null)
                    throw new InvalidOperationException(
                        $"No usable {architecture} model found (GPU construction failed and no CPU model is on disk).");
                return LayoutAnalyzerFactory.Create(cpuDesc, cpuPath);
            }
        }

        return new Resolution(gpuPath, LayoutAnalyzerFactory.CapabilitiesFor(architecture), Construct, gpuDesc.DisplayName, architecture, IsGpu: true);
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
