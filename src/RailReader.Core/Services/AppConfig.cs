using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed class AppConfig
{
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    public double RailZoomThreshold { get; set; } = 3.0;
    public double SnapDurationMs { get; set; } = 300.0;
    public double ScrollSpeedStart { get; set; } = 14.0;
    public double ScrollSpeedMax { get; set; } = 42.0;
    public double DefaultAutoScrollSpeed => (ScrollSpeedStart + ScrollSpeedMax) / 2.0;
    public double ScrollRampTime { get; set; } = 1.5;
    public int AnalysisLookaheadPages { get; set; } = 2;
    public float UiFontScale { get; set; } = 1.25f;
    public ColourEffect ColourEffect { get; set; } = ColourEffect.None;
    public double ColourEffectIntensity { get; set; } = 1.0;
    public bool MotionBlur { get; set; } = true;
    public double MotionBlurIntensity { get; set; } = 0.33;
    public bool PixelSnapping { get; set; } = true;
    public bool LineFocusBlur { get; set; }
    public double LineFocusBlurIntensity { get; set; } = 0.5;
    public double LinePadding { get; set; } = 0.2;
    public double AutoScrollLinePauseMs { get; set; } = 400.0;
    public double AutoScrollBlockPauseMs { get; set; } = 600.0;
    public double AutoScrollEquationPauseMs { get; set; } = 600.0;
    public double AutoScrollHeaderPauseMs { get; set; } = 600.0;
    public bool AutoScrollTriggerEnabled { get; set; }
    public double AutoScrollTriggerDelayMs { get; set; } = 2000.0;
    public double JumpPercentage { get; set; } = 25.0;
    public bool DarkMode { get; set; }
    public bool LineHighlightEnabled { get; set; } = true;
    public LineHighlightTint LineHighlightTint { get; set; } = LineHighlightTint.Auto;
    public double LineHighlightOpacity { get; set; } = 0.25;
    [JsonConverter(typeof(RecentFilesConverter))]
    public List<RecentFileEntry> RecentFiles { get; set; } = [];

    [JsonConverter(typeof(NavigableClassesConverter))]
    public HashSet<int> NavigableClasses { get; set; } = LayoutConstants.DefaultNavigableClasses();

    [JsonConverter(typeof(NavigableClassesConverter))]
    public HashSet<int> CenteringClasses { get; set; } = LayoutConstants.DefaultCenteringClasses();

    // VLM (Vision Language Model) settings for Copy as LaTeX / Markdown / Description
    public string? VlmEndpoint { get; set; }
    public string? VlmModel { get; set; }
    public string? VlmApiKey { get; set; }

    /// <summary>Creates an independent deep copy via JSON round-trip.</summary>
    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, s_options), s_options)
        ?? new AppConfig();

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string? s_configDir;

    public static string ConfigDir
    {
        get
        {
            if (s_configDir is not null) return s_configDir;

            string baseDir;
            if (OperatingSystem.IsWindows())
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            else if (OperatingSystem.IsMacOS())
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support");
            else
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            var dir = Path.Combine(baseDir, "railreader2");
            Directory.CreateDirectory(dir);
            s_configDir = dir;
            return dir;
        }
    }

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, s_options) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load config", ex);
        }

        var config = new AppConfig();
        config.Save();
        return config;
    }

    public void AddRecentFile(string filePath)
    {
        EnsureRecentEntry(filePath);
        Save();
    }

    public void SaveReadingPosition(string filePath, int page, double zoom, double offsetX, double offsetY,
        ColourEffect? colourEffect = null)
    {
        EnsureRecentEntry(filePath);
        var entry = RecentFiles[0];
        entry.Page = page;
        entry.Zoom = zoom;
        entry.OffsetX = offsetX;
        entry.OffsetY = offsetY;
        entry.ColourEffect = colourEffect;
        Save();
    }

    private void EnsureRecentEntry(string filePath)
    {
        var idx = RecentFiles.FindIndex(e => e.FilePath == filePath);
        RecentFileEntry entry;
        if (idx >= 0)
        {
            entry = RecentFiles[idx];
            RecentFiles.RemoveAt(idx);
        }
        else
        {
            entry = new RecentFileEntry { FilePath = filePath };
        }
        RecentFiles.Insert(0, entry);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    public RecentFileEntry? GetReadingPosition(string filePath)
    {
        return RecentFiles.Find(e => e.FilePath == filePath);
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, s_options);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save config", ex);
        }
    }
}

/// <summary>
/// Handles backward compatibility: deserializes both old-format string arrays
/// and new-format object arrays into List&lt;RecentFileEntry&gt;.
/// </summary>
internal sealed class RecentFilesConverter : JsonConverter<List<RecentFileEntry>>
{
    private static readonly JsonSerializerOptions s_entryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public override List<RecentFileEntry>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<RecentFileEntry>();
        if (reader.TokenType != JsonTokenType.StartArray) return result;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;

            if (reader.TokenType == JsonTokenType.String)
            {
                // Old format: plain string path
                var path = reader.GetString();
                if (path is not null)
                    result.Add(new RecentFileEntry { FilePath = path });
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                // New format: object with file_path, page, zoom, etc.
                var entry = JsonSerializer.Deserialize<RecentFileEntry>(ref reader, s_entryOptions);
                if (entry is not null)
                    result.Add(entry);
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<RecentFileEntry> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
            JsonSerializer.Serialize(writer, entry, s_entryOptions);
        writer.WriteEndArray();
    }
}

internal sealed class NavigableClassesConverter : JsonConverter<HashSet<int>>
{
    public override HashSet<int>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new HashSet<int>();
        if (reader.TokenType != JsonTokenType.StartArray) return result;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();
                if (name is not null && LayoutConstants.ClassNameToIndex(name) is { } idx)
                    result.Add(idx);
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, HashSet<int> value, JsonSerializerOptions options)
    {
        var names = value
            .Where(id => id >= 0 && id < LayoutConstants.LayoutClasses.Length)
            .Select(id => LayoutConstants.LayoutClasses[id])
            .OrderBy(n => n);
        writer.WriteStartArray();
        foreach (var name in names)
            writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
}
