using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Whether features still rough around the edges are exposed. Shell-managed sidecar
/// (<c>ConfigDir/experimental_prefs.json</c>) like <see cref="SettingsViewPreferences"/>. Off by
/// default: an experimental feature stays hidden, and behaves as if absent, until the reader opts in.
/// Currently gates reading-guide steer mode (and with it steering the rail).
/// </summary>
public sealed class ExperimentalPreferences
{
    public bool Enabled { get; set; }

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "experimental_prefs.json");

    public static ExperimentalPreferences Load()
        => JsonSidecar.Load(Path, ExperimentalPreferencesJsonContext.Default.ExperimentalPreferences,
            static () => new ExperimentalPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, ExperimentalPreferencesJsonContext.Default.ExperimentalPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(ExperimentalPreferences))]
internal partial class ExperimentalPreferencesJsonContext : JsonSerializerContext;
