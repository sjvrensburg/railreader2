namespace RailReader2.Services;

public static class CleanupService
{
    public static (int FilesRemoved, long BytesFreed) RunCleanup()
    {
        int filesRemoved = 0;
        long bytesFreed = 0;
        var cwd = Directory.GetCurrentDirectory();

        // Remove cache/ directory contents
        var cacheDir = Path.Combine(cwd, "cache");
        if (Directory.Exists(cacheDir))
            CleanDirectory(cacheDir, ref filesRemoved, ref bytesFreed);

        // Remove *.tmp files
        RemoveMatchingFiles(cwd, ".tmp", null, ref filesRemoved, ref bytesFreed);

        // Remove *.log files older than 7 days
        RemoveMatchingFiles(cwd, ".log", TimeSpan.FromDays(7), ref filesRemoved, ref bytesFreed);

        if (filesRemoved > 0)
            Console.Error.WriteLine($"Cleanup: removed {filesRemoved} files, freed {bytesFreed} bytes");

        return (filesRemoved, bytesFreed);
    }

    public static string FormatReport(int filesRemoved, long bytesFreed)
    {
        if (filesRemoved == 0) return "Nothing to clean up.";
        return $"Removed {filesRemoved} file(s), freed {bytesFreed / 1024.0:F1} KB.";
    }

    private static void CleanDirectory(string dir, ref int filesRemoved, ref long bytesFreed)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                if (name is "config.json" || name.EndsWith(".lock") || name.EndsWith(".onnx"))
                    continue;

                if (File.Exists(entry))
                {
                    try
                    {
                        var info = new FileInfo(entry);
                        bytesFreed += info.Length;
                        File.Delete(entry);
                        filesRemoved++;
                    }
                    catch { /* skip */ }
                }
                else if (Directory.Exists(entry))
                {
                    CleanDirectory(entry, ref filesRemoved, ref bytesFreed);
                    try { Directory.Delete(entry); } catch { /* skip */ }
                }
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
                var name = Path.GetFileName(file);
                if (name is "config.json" || name.EndsWith(".lock") || name.EndsWith(".onnx"))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    if (maxAge is not null && DateTime.UtcNow - info.LastWriteTimeUtc < maxAge.Value)
                        continue;
                    bytesFreed += info.Length;
                    File.Delete(file);
                    filesRemoved++;
                }
                catch { /* skip */ }
            }
        }
        catch { /* skip */ }
    }
}
