using System.Text.Json.Serialization;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Spell-check preferences for the note editor. Shell-managed sidecar
/// (<c>ConfigDir/spellcheck_prefs.json</c>) like <see cref="OcrPreferences"/>, since Core's
/// <see cref="AppConfig"/> is a NuGet type we don't extend and has no field for this.
/// The personal word list lives beside it as plain text (<see cref="SpellCheckService.PersonalWordsPath"/>)
/// so it can be hand-edited or copied between machines.
/// </summary>
public sealed class SpellCheckPreferences
{
    public bool Enabled { get; set; } = true;

    /// <summary>Hunspell dictionary name (e.g. <c>en_GB</c>), or null to pick one from the
    /// UI culture on first use (<see cref="SpellCheckService.DefaultLanguage"/>).</summary>
    public string? Language { get; set; }

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "spellcheck_prefs.json");

    public static SpellCheckPreferences Load()
        => JsonSidecar.Load(Path, SpellCheckPreferencesJsonContext.Default.SpellCheckPreferences,
            static () => new SpellCheckPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, SpellCheckPreferencesJsonContext.Default.SpellCheckPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(SpellCheckPreferences))]
internal partial class SpellCheckPreferencesJsonContext : JsonSerializerContext;
