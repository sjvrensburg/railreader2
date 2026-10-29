using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Per-document view-rotation persistence (Core 0.47.0: persisting <c>DocumentModel.ViewRotation</c>
/// across sessions is the host's job). One shell-managed sidecar map (<c>ConfigDir/view_rotations.json</c>,
/// content id (or full path for pre-identity entries) → clockwise quarter-turns) rather than a file per document — a rotation is a single int,
/// and unlike portals/annotations there is no per-document payload worth sharding. Entries are removed
/// when a document returns to rotation 0, so the map only ever holds the exceptions.
///
/// Every call is a read-modify-write of the whole map, serialised by a lock within the process. Lives in
/// Shared so the CLI can read it (the reading package). <c>keyOf</c> maps a PDF path to its content id
/// and defaults to <see cref="DocumentIdentity.Peek"/>; the CLI passes <see cref="DocumentIdentity.Lookup"/>.
/// </summary>
public static class ViewRotationStore
{
    private static readonly Lock s_lock = new();

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "view_rotations.json");

    /// <summary>The saved rotation for a document in clockwise quarter-turns (0 when none saved).</summary>
    public static int Load(string pdfPath, Func<string, string?>? keyOf = null)
    {
        lock (s_lock)
        {
            var map = LoadMap();
            return map.TryGetValue(KeyFor(pdfPath, keyOf), out int turns) ? turns
                : map.TryGetValue(System.IO.Path.GetFullPath(pdfPath), out turns) ? turns : 0;   // path key: pre-identity entries
        }
    }

    /// <summary>Saves (or, at rotation 0, forgets) a document's view rotation.</summary>
    public static void Save(string pdfPath, int quarterTurns, Func<string, string?>? keyOf = null)
    {
        lock (s_lock)
        {
            var map = LoadMap();
            var key = KeyFor(pdfPath, keyOf);
            if (quarterTurns == 0)
            {
                bool removed = map.Remove(key);
                removed |= map.Remove(System.IO.Path.GetFullPath(pdfPath));
                if (!removed) return;
            }
            else
            {
                map[key] = quarterTurns;
            }
            JsonSidecar.Save(Path, map, ViewRotationJsonContext.Default.DictionaryStringInt32);
        }
    }

    /// <summary>Content id when the document has been resolved, else its full path.</summary>
    private static string KeyFor(string pdfPath, Func<string, string?>? keyOf)
        => (keyOf ?? DocumentIdentity.Peek)(pdfPath) ?? System.IO.Path.GetFullPath(pdfPath);

    private static Dictionary<string, int> LoadMap()
        => JsonSidecar.Load(Path, ViewRotationJsonContext.Default.DictionaryStringInt32,
            static () => new Dictionary<string, int>());
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal partial class ViewRotationJsonContext : JsonSerializerContext;
