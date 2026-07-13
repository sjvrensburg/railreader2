using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RailReader.Core;

namespace RailReader2.Services;

/// <summary>
/// Shared load/save for the shell's small JSON sidecar files (config-dir-resident, source-gen
/// serialised). Centralises the "read → deserialize-or-default" / "create dir → write" boilerplate +
/// try/catch logging that <see cref="CustomLayoutModelConfig"/>, <see cref="PortalSet"/>, and
/// <see cref="PortalWindowSettings"/> would otherwise each copy.
/// </summary>
public static class JsonSidecar
{
    /// <summary>Load <typeparamref name="T"/> from <paramref name="path"/>, returning
    /// <paramref name="fallback"/>() when the file is absent, empty, or unreadable.</summary>
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback) where T : class
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, typeInfo) ?? fallback();
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to load {Path.GetFileName(path)}", ex);
        }
        return fallback();
    }

    /// <summary>Serialise <paramref name="value"/> to <paramref name="path"/>, creating its directory.
    /// Writes to a temp file and renames over the target so a crash or IO error mid-write can't leave
    /// a truncated/empty file in place (matches <see cref="LayoutModelDownloader"/>'s download pattern).</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var tmpPath = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(value, typeInfo));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error($"Failed to save {Path.GetFileName(path)}", ex);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
        }
    }
}
