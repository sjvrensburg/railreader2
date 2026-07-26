using System.Text.Json;
using System.Text.Json.Serialization;
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
