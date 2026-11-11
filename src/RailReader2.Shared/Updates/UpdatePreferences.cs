using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services.Updates;

/// <summary>
/// Update-check preferences (<c>ConfigDir/update_prefs.json</c>). <see cref="CheckAutomatically"/> is
/// null until the reader has answered the one-time prompt: by default the app makes no network requests.
/// </summary>
public sealed class UpdatePreferences
{
    public bool? CheckAutomatically { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    /// <summary>A version the reader chose to skip; the background check stays quiet about it.</summary>
    public string? SkippedVersion { get; set; }

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public bool IsCheckDue(DateTime nowUtc)
        => CheckAutomatically == true && (LastCheckUtc is not { } last || nowUtc - last >= CheckInterval || last > nowUtc);

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "update_prefs.json");

    public static UpdatePreferences Load()
        => JsonSidecar.Load(Path, UpdatePreferencesJsonContext.Default.UpdatePreferences,
            static () => new UpdatePreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, UpdatePreferencesJsonContext.Default.UpdatePreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(UpdatePreferences))]
internal partial class UpdatePreferencesJsonContext : JsonSerializerContext;
