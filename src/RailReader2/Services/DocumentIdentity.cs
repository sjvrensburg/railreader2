using System.Security.Cryptography;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Content-based document identity. A PDF's id is the SHA-256 of its bytes the first time it is seen,
/// so a moved, renamed or copied file keeps the id its portals and view rotation were saved under.
/// Hashing a large PDF is not free, so each path's (size, mtime) → id is cached in
/// <c>ConfigDir/document_ids.json</c> and an unchanged file is never re-read.
///
/// A file whose bytes change in place (annotations written back into the PDF) hashes to something new;
/// it keeps its id because the path was already known, and the new hash is recorded as an alias of that
/// id, so a later copy of the rewritten file is still recognised. Two unrelated files never share an id:
/// an unknown hash at an unknown path always starts a fresh id.
///
/// UI-thread for writers (<see cref="Resolve"/> mutates and saves the map); <see cref="Peek"/> only reads.
/// </summary>
public static class DocumentIdentity
{
    private const int MaxPathsPerId = 8;

    public static string MapPath => MapPathOverride ?? System.IO.Path.Combine(AppConfig.ConfigDir, "document_ids.json");

    internal static string? MapPathOverride { get; set; }

    private static IdentityMap? s_map;
    private static readonly Lock s_lock = new();

    /// <summary>The document's id, hashing the file only when its size or modification time changed.
    /// Blocking file IO — call from a background thread for a large PDF.</summary>
    public static string Resolve(string pdfPath)
    {
        string full = System.IO.Path.GetFullPath(pdfPath);
        var info = new FileInfo(full);
        long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;

        lock (s_lock)
        {
            var map = Map();
            if (map.Files.TryGetValue(full, out var known) && known.Size == size && known.MtimeTicks == mtime)
                return known.Id;
        }

        string hash = HashFile(full);   // outside the lock: this is the slow part

        lock (s_lock)
        {
            var map = Map();
            map.Files.TryGetValue(full, out var known);
            string id = map.Aliases.TryGetValue(hash, out var aliased) ? aliased : known?.Id ?? hash;
            map.Aliases[hash] = id;
            map.Files[full] = new FileEntry { Size = size, MtimeTicks = mtime, Id = id };
            if (!map.Paths.TryGetValue(id, out var paths)) map.Paths[id] = paths = [];
            paths.Remove(full);
            paths.Insert(0, full);
            if (paths.Count > MaxPathsPerId) paths.RemoveRange(MaxPathsPerId, paths.Count - MaxPathsPerId);
            JsonSidecar.Save(MapPath, map, IdentityJsonContext.Default.IdentityMap);
            return id;
        }
    }

    /// <summary>The id last resolved for this path, with no file access (null when never resolved).
    /// Safe after the file has changed or gone.</summary>
    public static string? Peek(string pdfPath)
    {
        string full = System.IO.Path.GetFullPath(pdfPath);
        lock (s_lock)
            return Map().Files.TryGetValue(full, out var e) ? e.Id : null;
    }

    /// <summary>Other paths this document has been opened from, most recent first.</summary>
    public static IReadOnlyList<string> KnownPaths(string id)
    {
        lock (s_lock)
            return Map().Paths.TryGetValue(id, out var p) ? [.. p] : [];
    }

    private static IdentityMap Map()
        => s_map ??= JsonSidecar.Load(MapPath, IdentityJsonContext.Default.IdentityMap, static () => new IdentityMap());

    internal static void ResetForTests() { lock (s_lock) s_map = null; }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

public sealed class FileEntry
{
    public long Size { get; set; }
    public long MtimeTicks { get; set; }
    public string Id { get; set; } = "";
}

public sealed class IdentityMap
{
    public int Version { get; set; } = 1;
    public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> Paths { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(IdentityMap))]
internal partial class IdentityJsonContext : JsonSerializerContext;
