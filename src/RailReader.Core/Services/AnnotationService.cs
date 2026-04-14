using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Manages annotation persistence. Annotations are stored internally in the
/// app config directory (ConfigDir/annotations/), keyed by a hash of the PDF
/// path. Legacy sidecar files (alongside the PDF) are loaded as a migration
/// fallback but never written to.
/// </summary>
public static class AnnotationService
{
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    private static string? _annotationDir;

    /// <summary>Internal storage directory for annotation files.</summary>
    public static string AnnotationDir => _annotationDir ??= InitAnnotationDir();

    private static string InitAnnotationDir()
    {
        var dir = Path.Combine(AppConfig.ConfigDir, "annotations");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Get the internal storage path for a PDF's annotations.</summary>
    public static string GetInternalPath(string pdfPath)
    {
        var hash = GetPathHash(pdfPath);
        return Path.Combine(AnnotationDir, $"{hash}.json");
    }

    /// <summary>Get the legacy sidecar path (for migration only).</summary>
    public static string GetSidecarPath(string pdfPath)
    {
        var dir = Path.GetDirectoryName(pdfPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        return Path.Combine(dir, $"{name}.railreader2.json");
    }

    /// <summary>
    /// Load annotations for a PDF. Checks internal storage first,
    /// falls back to legacy sidecar file (and migrates it to internal).
    /// </summary>
    public static AnnotationFile? Load(string pdfPath)
    {
        // Try internal storage first
        var internalPath = GetInternalPath(pdfPath);
        if (File.Exists(internalPath))
            return LoadFromFile(internalPath);

        // Fall back to legacy sidecar (migration)
        var sidecarPath = GetSidecarPath(pdfPath);
        if (File.Exists(sidecarPath))
        {
            var annotations = LoadFromFile(sidecarPath);
            if (annotations is not null)
            {
                // Set the full path for orphan detection
                annotations.SourcePdfPath = Path.GetFullPath(pdfPath);
                // Migrate to internal storage
                SaveToFile(GetInternalPath(pdfPath), annotations);
                Logger.Debug($"[Annotations] Migrated sidecar to internal storage: {Path.GetFileName(pdfPath)}");
            }
            return annotations;
        }

        return null;
    }

    /// <summary>Save annotations to internal storage. Returns false if the write fails.</summary>
    public static bool Save(string pdfPath, AnnotationFile annotations)
    {
        annotations.SourcePdfPath = Path.GetFullPath(pdfPath);
        return SaveToFile(GetInternalPath(pdfPath), annotations);
    }

    /// <summary>Export annotations to a JSON file at a user-chosen path. Throws on failure.</summary>
    public static void ExportJson(AnnotationFile annotations, string outputPath)
    {
        var json = JsonSerializer.Serialize(annotations, RailReaderJsonContext.Default.AnnotationFile);
        File.WriteAllText(outputPath, json);
    }

    /// <summary>Import annotations from a JSON file. Returns null if deserialization fails.</summary>
    public static AnnotationFile? ImportJson(string inputPath)
        => LoadFromFile(inputPath);

    /// <summary>
    /// Merges imported annotations into an existing annotation file.
    /// Appends annotations per page and adds bookmarks that don't already exist.
    /// </summary>
    public static int MergeInto(AnnotationFile target, AnnotationFile imported)
    {
        int added = 0;

        foreach (var (page, annotations) in imported.Pages)
        {
            if (!target.Pages.TryGetValue(page, out var existing))
            {
                existing = [];
                target.Pages[page] = existing;
            }
            existing.AddRange(annotations);
            added += annotations.Count;
        }

        // Add bookmarks that don't already exist (by name + page)
        var existingBookmarks = new HashSet<(string, int)>(
            target.Bookmarks.Select(b => (b.Name, b.Page)));
        foreach (var bm in imported.Bookmarks)
        {
            if (existingBookmarks.Add((bm.Name, bm.Page)))
            {
                target.Bookmarks.Add(bm);
                added++;
            }
        }

        return added;
    }

    /// <summary>Delete internal annotation file for a PDF.</summary>
    public static bool Delete(string pdfPath)
    {
        var path = GetInternalPath(pdfPath);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch (Exception ex) { Logger.Debug($"[Annotations] Failed to delete {path}: {ex.Message}"); return false; }
    }

    /// <summary>List all internally stored annotation files with metadata.</summary>
    public static List<AnnotationStorageInfo> ListStored()
    {
        var result = new List<AnnotationStorageInfo>();
        if (!Directory.Exists(AnnotationDir)) return result;

        foreach (var file in Directory.EnumerateFiles(AnnotationDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var af = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AnnotationFile);
                if (af is null) continue;

                var fi = new FileInfo(file);
                result.Add(new AnnotationStorageInfo(
                    file, af.SourcePdf, af.SourcePdfPath,
                    fi.Length, fi.LastWriteTime,
                    af.Pages.Values.Sum(p => p.Count), af.Bookmarks.Count,
                    !string.IsNullOrEmpty(af.SourcePdfPath) && File.Exists(af.SourcePdfPath)));
            }
            catch (Exception ex) { Logger.Debug($"[Annotations] Skip corrupt file {file}: {ex.Message}"); }
        }
        return result;
    }

    /// <summary>Remove annotation files whose source PDFs no longer exist.</summary>
    public static (int Removed, long BytesFreed) CleanOrphaned()
    {
        int removed = 0;
        long bytesFreed = 0;

        foreach (var info in ListStored())
        {
            if (info.SourcePdfExists || string.IsNullOrEmpty(info.SourcePdfPath))
                continue;

            try
            {
                bytesFreed += info.Size;
                File.Delete(info.InternalPath);
                removed++;
            }
            catch (Exception ex) { Logger.Debug($"[Annotations] Failed to clean orphan: {ex.Message}"); }
        }

        return (removed, bytesFreed);
    }

    private static AnnotationFile? LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AnnotationFile);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Annotations] Failed to load {path}", ex);
            return null;
        }
    }

    private static bool SaveToFile(string path, AnnotationFile annotations)
    {
        try
        {
            var json = JsonSerializer.Serialize(annotations, RailReaderJsonContext.Default.AnnotationFile);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Annotations] Failed to save {path}", ex);
            return false;
        }
    }

    private static string GetPathHash(string pdfPath)
    {
        var fullPath = Path.GetFullPath(pdfPath);
        var bytes = Encoding.UTF8.GetBytes(fullPath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>Metadata about a stored annotation file, for display and cleanup.</summary>
public sealed record AnnotationStorageInfo(
    string InternalPath, string SourcePdf, string SourcePdfPath,
    long Size, DateTime LastModified,
    int AnnotationCount, int BookmarkCount, bool SourcePdfExists);
