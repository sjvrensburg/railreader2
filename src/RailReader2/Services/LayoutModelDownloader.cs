using System.Net.Http;
using System.Security.Cryptography;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Downloads a built-in layout-detection model to a writable, search-path
/// location so the app can use it without a manual file copy. Crucially this
/// targets <see cref="AppConfig.ConfigDir"/><c>/models/</c> — the one model
/// search path that is writable inside a self-contained AppImage (the bundle's
/// own <c>$APPDIR/models</c> and <c>BaseDirectory/models</c> are read-only).
///
/// Downloads atomically (to a <c>.tmp</c> then rename), verifies the published
/// <see cref="LayoutModelDescriptor.Sha256"/> when present (else a size sanity
/// check), and reports progress for a determinate UI bar.
/// </summary>
public static class LayoutModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Maps a built-in analyzer choice to its model descriptor.</summary>
    public static LayoutModelDescriptor? DescriptorFor(BuiltinAnalyzer choice) => choice switch
    {
        BuiltinAnalyzer.PpDocLayoutV3 => LayoutModelRegistry.PPDocLayoutV3,
        BuiltinAnalyzer.Heron => LayoutModelRegistry.HeronInt8,
        BuiltinAnalyzer.PpDocLayoutS => LayoutModelRegistry.PPDocLayoutS,
        _ => null,
    };

    /// <summary>Writable install target on the model search path.</summary>
    public static string TargetPath(LayoutModelDescriptor desc)
        => Path.Combine(AppConfig.ConfigDir, "models", desc.FileName);

    public readonly record struct DownloadResult(bool Ok, string? Path, string? Error);

    public static async Task<DownloadResult> DownloadAsync(
        LayoutModelDescriptor desc, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppConfig.ConfigDir, "models");
        var finalPath = Path.Combine(dir, desc.FileName);
        var tmpPath = finalPath + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);

            using var resp = await Http.GetAsync(desc.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0) progress?.Report((double)read / total.Value);
                }
            }

            if (!string.IsNullOrEmpty(desc.Sha256))
            {
                var actual = await ComputeSha256Async(tmpPath, ct);
                if (!string.Equals(actual, desc.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tmpPath);
                    return new(false, null,
                        $"Checksum mismatch (expected {desc.Sha256[..12]}…, got {actual[..12]}…). Not installed.");
                }
            }
            else
            {
                var len = new FileInfo(tmpPath).Length;
                if (len < 1_000_000)
                {
                    TryDelete(tmpPath);
                    return new(false, null, $"Downloaded file is implausibly small ({len:N0} bytes). Not installed.");
                }
            }

            File.Move(tmpPath, finalPath, overwrite: true);
            return new(true, finalPath, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            return new(false, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            return new(false, null, ex.Message);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
