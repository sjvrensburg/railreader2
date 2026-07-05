using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Per-document view-rotation persistence (Core 0.47.0: persisting <c>DocumentModel.ViewRotation</c>
/// across sessions is the host's job). One shell-managed sidecar map (<c>ConfigDir/view_rotations.json</c>,
/// full path → clockwise quarter-turns) rather than a file per document — a rotation is a single int,
/// and unlike portals/annotations there is no per-document payload worth sharding. Entries are removed
/// when a document returns to rotation 0, so the map only ever holds the exceptions.
/// </summary>
public static class ViewRotationStore
{
    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "view_rotations.json");

    /// <summary>The saved rotation for a document in clockwise quarter-turns (0 when none saved).</summary>
    public static int Load(string pdfPath)
    {
        var map = LoadMap();
        return map.TryGetValue(System.IO.Path.GetFullPath(pdfPath), out int turns) ? turns : 0;
    }

    /// <summary>Saves (or, at rotation 0, forgets) a document's view rotation.</summary>
    public static void Save(string pdfPath, int quarterTurns)
    {
        var map = LoadMap();
        var key = System.IO.Path.GetFullPath(pdfPath);
        if (quarterTurns == 0)
        {
            if (!map.Remove(key)) return;
        }
        else
        {
            map[key] = quarterTurns;
        }
        JsonSidecar.Save(Path, map, ViewRotationJsonContext.Default.DictionaryStringInt32);
    }

    private static Dictionary<string, int> LoadMap()
        => JsonSidecar.Load(Path, ViewRotationJsonContext.Default.DictionaryStringInt32,
            static () => new Dictionary<string, int>());
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal partial class ViewRotationJsonContext : JsonSerializerContext;
