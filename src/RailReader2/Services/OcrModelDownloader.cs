using System.Net.Http;
using System.Security.Cryptography;
using RailReader.Core;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Downloads an opt-in multilingual OCR recognition model set (<see cref="OcrModelDescriptor"/>,
/// from <see cref="OcrModelRegistry"/>) to a writable, search-path location — mirrors
/// <see cref="LayoutModelDownloader"/> for the same read-only-AppImage reasons, but a set is
/// three files (detector, recognizer, dictionary) instead of one.
///
/// <para>
/// Targets <c>AppConfig.ConfigDir/&lt;file.RelativePath&gt;</c> (e.g.
/// <c>~/.config/railreader2/models/v6/PP-OCRv6_det_tiny.onnx</c>) — <see cref="OcrModelLocator"/>'s
/// probe order includes <c>Environment.SpecialFolder.ApplicationData/railreader2</c>, which is
/// exactly <see cref="AppConfig.ConfigDir"/> on every platform this app ships for, so a file
/// landing there is found with no extra wiring.
/// </para>
/// </summary>
public static class OcrModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    // OcrModelRegistry's URLs resolve through ModelScope's CDN, whose Tengine front-end 403s
    // any request with no User-Agent ("denied by UA ACL = blacklist") — HttpClient sends none
    // by default. A browser-shaped UA is enough to clear the ACL (railreader2#209).
    static OcrModelDownloader()
        => Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

    public readonly record struct DownloadResult(bool Ok, string? Error);

    /// <summary>True when all three files of <paramref name="desc"/> are already resolvable
    /// (already downloaded, or otherwise present on a probed path).</summary>
    public static bool IsInstalled(OcrModelDescriptor desc)
        => OcrModelLocator.Locate(desc.ModelSet) is not null;

    /// <summary>
    /// Downloads and hash-verifies all three files of <paramref name="desc"/> in turn. Each file
    /// downloads atomically (<c>.tmp</c> then rename); a failure partway leaves any
    /// already-completed files in place (re-running only re-fetches what's still missing, since
    /// <see cref="TargetPath"/> is checked before each file starts).
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(
        OcrModelDescriptor desc, IProgress<double>? progress, CancellationToken ct)
    {
        OcrModelFile[] files = [desc.Det, desc.Rec, desc.Dict];
        for (int i = 0; i < files.Length; i++)
        {
            int idx = i;
            var fileProgress = new Progress<double>(p => progress?.Report((idx + p) / files.Length));
            var result = await DownloadOneAsync(files[i], fileProgress, ct);
            if (!result.Ok) return result;
        }
        progress?.Report(1.0);
        return new(true, null);
    }

    public static string TargetPath(OcrModelFile file) => Path.Combine(AppConfig.ConfigDir, file.RelativePath);

    private static async Task<DownloadResult> DownloadOneAsync(
        OcrModelFile file, IProgress<double>? progress, CancellationToken ct)
    {
        var finalPath = TargetPath(file);
        if (File.Exists(finalPath))
        {
            progress?.Report(1.0);
            return new(true, null);
        }

        var dir = Path.GetDirectoryName(finalPath)!;
        var tmpPath = finalPath + ".tmp";
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Directory.CreateDirectory(dir);

            stallCts.CancelAfter(StallTimeout);
            using var resp = await Http.GetAsync(
                file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            stallCts.CancelAfter(StallTimeout);

            await using (var src = await resp.Content.ReadAsStreamAsync(stallCts.Token))
            await using (var dst = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                double lastReported = -1;
                int n;
                while ((n = await src.ReadAsync(buffer, stallCts.Token)) > 0)
                {
                    stallCts.CancelAfter(StallTimeout);
                    await dst.WriteAsync(buffer.AsMemory(0, n), stallCts.Token);
                    read += n;
                    if (total is > 0)
                    {
                        double pct = (double)read / total.Value;
                        if (pct - lastReported >= 0.01 || read == total.Value)
                        {
                            lastReported = pct;
                            progress?.Report(pct);
                        }
                    }
                }
            }

            var actual = await ComputeSha256Async(tmpPath, ct);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmpPath);
                return new(false,
                    $"Checksum mismatch for {Path.GetFileName(file.RelativePath)} (expected {Truncate(file.Sha256, 12)}…, got {Truncate(actual, 12)}…). Not installed.");
            }

            File.Move(tmpPath, finalPath, overwrite: true);
            return new(true, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            return new(false, ct.IsCancellationRequested
                ? "Cancelled."
                : $"Download stalled (no data received for {StallTimeout.TotalSeconds:N0}s).");
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            return new(false, ex.Message);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Truncate(string s, int length) => s.Length <= length ? s : s[..length];

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
