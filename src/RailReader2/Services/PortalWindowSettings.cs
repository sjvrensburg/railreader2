using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Last bounds + always-on-top state of the detached portal pop-out window. App-level (not
/// per-document), shell-managed — Core's <see cref="AppConfig"/> is a NuGet type we don't extend, so
/// this lives in its own small sidecar (<c>ConfigDir/portal_window.json</c>), mirroring
/// <see cref="CustomLayoutModelConfig"/>. Reapplied on the next pop-out; the window is never
/// auto-reopened on launch.
/// </summary>
public sealed class PortalWindowSettings
{
    // int.MinValue is the "no saved position" sentinel → centre on first pop-out.
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 320;
    public bool Topmost { get; set; } = true;

    [JsonIgnore]
    public bool HasPosition => X != int.MinValue && Y != int.MinValue;

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "portal_window.json");

    public static PortalWindowSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize(json, PortalWindowJsonContext.Default.PortalWindowSettings)
                    ?? new PortalWindowSettings();
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to load portal_window.json", ex);
        }
        return new PortalWindowSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDir);
            var json = JsonSerializer.Serialize(this, PortalWindowJsonContext.Default.PortalWindowSettings);
            File.WriteAllText(Path, json);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to save portal_window.json", ex);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(PortalWindowSettings))]
internal partial class PortalWindowJsonContext : JsonSerializerContext;
