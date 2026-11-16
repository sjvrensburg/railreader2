using System.Text.Json;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>Paths of the PDFs the reader has opened, for the backfill (design §5.3).
/// <para>Core's <see cref="DocumentIdentity"/> has no public "list every document" call, so this reads
/// its map file (<see cref="DocumentIdentity.MapPath"/>) tolerantly: the <c>files</c> array of
/// <c>[path, {…}]</c> pairs. If Core changes the format this finds nothing rather than failing, and the
/// caller still has the recent-files list. Read-only; never writes the map.</para></summary>
public static class KnownDocuments
{
    public static IReadOnlyList<string> FromIdentityMap(string? mapPath = null)
    {
        mapPath ??= DocumentIdentity.MapPath;
        var paths = new List<string>();
        try
        {
            if (!File.Exists(mapPath)) return paths;
            using var stream = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array)
                return paths;
            foreach (var entry in files.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() > 0
                    && entry[0].ValueKind == JsonValueKind.String && entry[0].GetString() is { Length: > 0 } p)
                    paths.Add(p);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return paths;
    }
}
