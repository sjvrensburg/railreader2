namespace RailReader.Core.Services;

public static class CleanupService
{
    public static (int FilesRemoved, long BytesFreed) RunCleanup()
    {
        int filesRemoved = 0;
        long bytesFreed = 0;

        // Clean cache/ and temp files in the app config directory
        var configDir = AppConfig.ConfigDir;

        var cacheDir = Path.Combine(configDir, "cache");
        if (Directory.Exists(cacheDir))
            CleanDirectory(cacheDir, ref filesRemoved, ref bytesFreed);

        // Remove *.tmp files in config dir
        RemoveMatchingFiles(configDir, ".tmp", null, ref filesRemoved, ref bytesFreed);

        // Remove *.log files older than 7 days
        RemoveMatchingFiles(configDir, ".log", TimeSpan.FromDays(7), ref filesRemoved, ref bytesFreed);

        // Clean orphaned annotation files (source PDF no longer exists)
        var (orphansRemoved, orphanBytes) = AnnotationService.CleanOrphaned();
        filesRemoved += orphansRemoved;
        bytesFreed += orphanBytes;

#if DEBUG
        if (filesRemoved > 0)
            Console.Error.WriteLine($"Cleanup: removed {filesRemoved} files, freed {bytesFreed} bytes");
#endif

        return (filesRemoved, bytesFreed);
    }

    public static string FormatReport(int filesRemoved, long bytesFreed)
    {
        if (filesRemoved == 0) return "Nothing to clean up.";
        return $"Removed {filesRemoved} file(s), freed {bytesFreed / 1024.0:F1} KB.";
    }

    private static bool IsProtectedFile(string name) =>
        name is "config.json" || name.EndsWith(".lock") || name.EndsWith(".onnx");

    private static void CleanDirectory(string dir, ref int filesRemoved, ref long bytesFreed)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (IsProtectedFile(Path.GetFileName(entry)))
                    continue;

                try
                {
                    var info = new FileInfo(entry);
                    if (info.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        CleanDirectory(entry, ref filesRemoved, ref bytesFreed);
                        Directory.Delete(entry);
                    }
                    else
                    {
                        bytesFreed += info.Length;
                        info.Delete();
                        filesRemoved++;
                    }
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
    }

    private static void RemoveMatchingFiles(
        string dir, string extension, TimeSpan? maxAge, ref int filesRemoved, ref long bytesFreed)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsProtectedFile(Path.GetFileName(file))) continue;

                try
                {
                    var info = new FileInfo(file);
                    if (maxAge is not null && DateTime.UtcNow - info.LastWriteTimeUtc < maxAge.Value)
                        continue;
                    bytesFreed += info.Length;
                    info.Delete();
                    filesRemoved++;
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
    }
}
