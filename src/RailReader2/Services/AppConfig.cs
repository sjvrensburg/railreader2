using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader2.Models;

namespace RailReader2.Services;

public sealed class AppConfig
{
    public double RailZoomThreshold { get; set; } = 3.0;
    public double SnapDurationMs { get; set; } = 300.0;
    public double ScrollSpeedStart { get; set; } = 10.0;
    public double ScrollSpeedMax { get; set; } = 30.0;
    public double ScrollRampTime { get; set; } = 1.5;
    public int AnalysisLookaheadPages { get; set; } = 2;
    public float UiFontScale { get; set; } = 1.25f;
    public ColourEffect ColourEffect { get; set; } = ColourEffect.None;
    public double ColourEffectIntensity { get; set; } = 1.0;
    public bool MotionBlur { get; set; } = true;
    public double MotionBlurIntensity { get; set; } = 0.33;

    public List<string> RecentFiles { get; set; } = [];

    [JsonConverter(typeof(NavigableClassesConverter))]
    public HashSet<int> NavigableClasses { get; set; } = LayoutConstants.DefaultNavigableClasses();

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string ConfigDir
    {
        get
        {
            var baseDir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var dir = Path.Combine(baseDir, "railreader2");
            Directory.CreateDirectory(dir);
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
            Console.Error.WriteLine($"Failed to load config: {ex.Message}");
        }

        var config = new AppConfig();
        config.Save();
        return config;
    }

    public void AddRecentFile(string filePath)
    {
        RecentFiles.Remove(filePath);
        RecentFiles.Insert(0, filePath);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
        Save();
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
            Console.Error.WriteLine($"Failed to save config: {ex.Message}");
        }
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
