using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// App-level portal behaviour preferences — currently just the automatic-pinning toggle. Shell-managed
/// sidecar (<c>ConfigDir/portal_prefs.json</c>) like <see cref="PortalWindowSettings"/>, since Core's
/// <see cref="AppConfig"/> is a NuGet type we don't extend.
/// </summary>
public sealed class PortalPreferences
{
    /// <summary>When true, rail-reading a line that mentions "Figure N" / "Table N" automatically
    /// shows the matching figure/table in the portal preview, no manual link needed.</summary>
    public bool AutoPinFiguresTables { get; set; } = true;

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "portal_prefs.json");

    public static PortalPreferences Load()
        => JsonSidecar.Load(Path, PortalPreferencesJsonContext.Default.PortalPreferences,
            static () => new PortalPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, PortalPreferencesJsonContext.Default.PortalPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(PortalPreferences))]
internal partial class PortalPreferencesJsonContext : JsonSerializerContext;
