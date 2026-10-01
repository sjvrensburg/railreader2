using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// How the Settings window presents itself — currently just whether the advanced view is shown.
/// Shell-managed sidecar (<c>ConfigDir/settings_view_prefs.json</c>) like
/// <see cref="PortalPreferences"/>, since Core's <see cref="AppConfig"/> is a NuGet type we don't
/// extend. Defaults to the simple view so a first-time reader isn't met with every knob at once.
/// </summary>
public sealed class SettingsViewPreferences
{
    public bool ShowAdvanced { get; set; }

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "settings_view_prefs.json");

    public static SettingsViewPreferences Load()
        => JsonSidecar.Load(Path, SettingsViewPreferencesJsonContext.Default.SettingsViewPreferences,
            static () => new SettingsViewPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, SettingsViewPreferencesJsonContext.Default.SettingsViewPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(SettingsViewPreferences))]
internal partial class SettingsViewPreferencesJsonContext : JsonSerializerContext;
