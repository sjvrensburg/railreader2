using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>How the reading guide follows the pointer while the mode is on.</summary>
public enum ReadingGuideFollow
{
    /// <summary>Follow only while Shift is held; releasing Shift parks the guide where it is.</summary>
    WhileShiftHeld,
    /// <summary>Follow every pointer move (for readers who can't hold a key). The cursor stays visible.</summary>
    Always,
}

/// <summary>
/// App-level reading-guide preferences (docs/reading-guide-design.md §4). Shell-managed sidecar
/// (<c>ConfigDir/reading_guide_prefs.json</c>) like <see cref="PortalPreferences"/>, since Core's
/// <see cref="AppConfig"/> is a NuGet type we don't extend. The line tint / opacity / padding and the
/// line-focus dim are the existing "Current Line" settings — the guide reuses them, no duplicates.
/// </summary>
public sealed class ReadingGuidePreferences
{
    public ReadingGuideFollow Follow { get; set; } = ReadingGuideFollow.WhileShiftHeld;

    /// <summary>Draw the word under the pointer (the line band is always drawn).</summary>
    public bool WordHighlight { get; set; } = true;

    /// <summary>Vertical hysteresis of the guide line, as a fraction of the line's height on each side
    /// (Core's <c>ReadingGuideHitTester.DeadZoneFraction</c>). 0 = always the nearest line.</summary>
    public double DeadZone { get; set; } = 0.6;

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "reading_guide_prefs.json");

    public static ReadingGuidePreferences Load()
        => JsonSidecar.Load(Path, ReadingGuidePreferencesJsonContext.Default.ReadingGuidePreferences,
            static () => new ReadingGuidePreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, ReadingGuidePreferencesJsonContext.Default.ReadingGuidePreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ReadingGuidePreferences))]
internal partial class ReadingGuidePreferencesJsonContext : JsonSerializerContext;
