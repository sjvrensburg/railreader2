using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// App-level OCR mode preference for scanned pages. Shell-managed sidecar
/// (<c>ConfigDir/ocr_prefs.json</c>) like <see cref="PortalPreferences"/>, since Core's
/// <see cref="AppConfig"/> is a NuGet type we don't extend and has no field for this.
/// </summary>
public sealed class OcrPreferences
{
    /// <summary>Off (default, no OCR cost) / Lines (detection only) / Full (detection + recognition).</summary>
    public OcrMode Mode { get; set; } = OcrMode.Off;

    /// <summary>
    /// <see cref="RailReader.Core.Ocr.RapidOcr.OcrModelDescriptor.Id"/> of the recognition model
    /// set to use, or null for the bundled default (PP-OCRv5, Latin script only). Read once at
    /// startup (<see cref="ViewModels.MainWindowViewModel"/> constructor) — like the layout model
    /// choice, changing it takes effect on next launch, not live.
    /// </summary>
    public string? ModelSetId { get; set; }

    /// <summary>
    /// GPU acceleration preference for OCR detection/recognition, via
    /// <c>RailReader.Core.Analysis.WebGpu</c>'s native WebGPU execution provider — the same OCR
    /// model weights run under either backend, no separate download. <b>Mutually exclusive with
    /// <see cref="CustomLayoutModelConfig.Accelerator"/></b>: calling <c>Session.Run()</c> on two
    /// WebGPU-backed sessions from two threads at once segfaults the process (confirmed,
    /// cross-device and same-device — see <c>WebGpuAccelerator</c>'s doc comment and
    /// <see href="https://github.com/microsoft/onnxruntime/issues/32561">microsoft/onnxruntime#32561</see>),
    /// and <c>AnalysisWorker</c> runs OCR and layout inference on two independent, genuinely
    /// concurrent threads — exactly that shape. Settings enforces "only one of the two on GPU at
    /// a time" by unchecking whichever wasn't just turned on; <see cref="ViewModels.MainWindowViewModel"/>
    /// enforces it again defensively at startup in case a sidecar file was hand-edited. Takes
    /// effect on next launch, like <see cref="Mode"/> and <see cref="ModelSetId"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AcceleratorPreference>))]
    public AcceleratorPreference Accelerator { get; set; } = AcceleratorPreference.Cpu;

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "ocr_prefs.json");

    public static OcrPreferences Load()
        => JsonSidecar.Load(Path, OcrPreferencesJsonContext.Default.OcrPreferences,
            static () => new OcrPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, OcrPreferencesJsonContext.Default.OcrPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(OcrPreferences))]
internal partial class OcrPreferencesJsonContext : JsonSerializerContext;
