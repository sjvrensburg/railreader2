using System.Net.Http;
using System.Reflection;
using RailReader.Core;

namespace RailReader2.Services.Updates;

public enum UpdateCheckStatus { UpToDate, Available, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest, string? Error)
{
    public static UpdateCheckResult Fail(string error) => new(UpdateCheckStatus.Failed, null, error);
}

/// <summary>
/// Fetches and verifies the latest release's <c>update-manifest.json</c>. Two environment variables
/// exist for testing: <c>RR_UPDATE_FEED</c> (a URL prefix, or a local directory, holding the manifest
/// and its <c>.sig</c>) and <c>RR_UPDATE_PUBLIC_KEY_FILE</c> (a PEM public key to verify against instead
/// of the one built in).
/// </summary>
public static class UpdateFeed
{
    public const string ManifestName = "update-manifest.json";
    public const string SignatureName = "update-manifest.json.sig";
    public const string DefaultFeed = "https://github.com/sjvrensburg/railreader2/releases/latest/download/";
    public const string ReleasesPage = "https://github.com/sjvrensburg/railreader2/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RailReader2/{UpdateVersion.Current}");
        return http;
    }

    internal static HttpClient Client => Http;

    /// <param name="feed">URL prefix or local directory holding the manifest; default <c>RR_UPDATE_FEED</c>,
    /// else the latest GitHub release.</param>
    /// <param name="publicKeyPem">Key to verify against; default <c>RR_UPDATE_PUBLIC_KEY_FILE</c>, else the built-in one.</param>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct, string? feed = null, string? publicKeyPem = null)
    {
        try
        {
            feed ??= Environment.GetEnvironmentVariable("RR_UPDATE_FEED") is { Length: > 0 } f ? f : DefaultFeed;
            var (manifestBytes, signature) = await FetchAsync(feed, ct);
            var manifest = UpdateManifestReader.ReadVerified(manifestBytes, signature, publicKeyPem ?? PublicKeyPem());
            RailReaderLogging.Logger.Info($"[Update] Latest release {manifest.Version}, this build {UpdateVersion.Current}");
            if (manifest.FormatVersion > UpdateManifest.SupportedFormat)
                return new(UpdateCheckStatus.Available, manifest,
                    "This version can't install the update itself; download it from the release page.");
            return UpdateVersion.IsNewer(manifest.Version, UpdateVersion.Current)
                ? new(UpdateCheckStatus.Available, manifest, null)
                : new(UpdateCheckStatus.UpToDate, manifest, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UpdateVerificationException ex)
        {
            RailReaderLogging.Logger.Warn($"[Update] Verification failed: {ex.Message}");
            return UpdateCheckResult.Fail(ex.Message);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            RailReaderLogging.Logger.Warn($"[Update] No update information at the feed: {ex.Message}");
            return UpdateCheckResult.Fail("The latest release has no update information. Check the release page instead.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException)
        {
            RailReaderLogging.Logger.Warn($"[Update] Check failed: {ex.Message}");
            return UpdateCheckResult.Fail("Couldn't reach GitHub. Check your connection and try again.");
        }
    }

    private static async Task<(byte[] Manifest, byte[] Signature)> FetchAsync(string feed, CancellationToken ct)
    {
        if (Directory.Exists(feed))
            return (await File.ReadAllBytesAsync(Path.Combine(feed, ManifestName), ct),
                await File.ReadAllBytesAsync(Path.Combine(feed, SignatureName), ct));
        if (!feed.EndsWith('/')) feed += "/";
        var manifest = await Http.GetByteArrayAsync(feed + ManifestName, ct);
        var signature = await Http.GetByteArrayAsync(feed + SignatureName, ct);
        return (manifest, signature);
    }

    internal static string PublicKeyPem()
    {
        if (Environment.GetEnvironmentVariable("RR_UPDATE_PUBLIC_KEY_FILE") is { Length: > 0 } path)
            return File.ReadAllText(path);
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("RailReader2.UpdateSigningKey.pem")
            ?? throw new UpdateVerificationException("This build has no update signing key.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
