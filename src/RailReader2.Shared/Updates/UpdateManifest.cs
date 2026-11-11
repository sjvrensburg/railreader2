using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailReader2.Services.Updates;

/// <summary>
/// <c>update-manifest.json</c>, published by CI as a release asset next to its detached ECDSA P-256
/// signature (<c>update-manifest.json.sig</c>, DER, over the manifest's exact bytes). Written by
/// <c>scripts/write-update-manifest.py</c>.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>The format this build reads. A higher number means "update by hand".</summary>
    public const int SupportedFormat = 1;

    public int FormatVersion { get; set; }
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string? PublishedUtc { get; set; }
    public Dictionary<string, UpdateAsset> Assets { get; set; } = new();
}

public sealed class UpdateAsset
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(UpdateManifest))]
internal partial class UpdateManifestJsonContext : JsonSerializerContext;

public sealed class UpdateVerificationException(string message) : Exception(message);

public static class UpdateManifestReader
{
    /// <summary>Verifies <paramref name="signature"/> over <paramref name="manifestBytes"/> with the
    /// SPKI PEM public key, then parses and sanity-checks the manifest. Throws
    /// <see cref="UpdateVerificationException"/> on any failure; nothing unverified is parsed.</summary>
    public static UpdateManifest ReadVerified(byte[] manifestBytes, byte[] signature, string publicKeyPem)
    {
        using (var key = ECDsa.Create())
        {
            try { key.ImportFromPem(publicKeyPem); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new UpdateVerificationException("The update signing key in this build is unreadable.");
            }
            bool ok;
            try
            {
                ok = key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (CryptographicException) { ok = false; }
            if (!ok) throw new UpdateVerificationException("The update information is not signed by RailReader2's release key.");
        }

        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize(manifestBytes, UpdateManifestJsonContext.Default.UpdateManifest); }
        catch (JsonException) { manifest = null; }
        if (manifest is null) throw new UpdateVerificationException("The update information could not be read.");
        if (UpdateVersion.Parse(manifest.Version) is null)
            throw new UpdateVerificationException($"The update information names an unreadable version ({manifest.Version}).");
        foreach (var (key, asset) in manifest.Assets)
        {
            // https only; plain http just for a loopback test feed (the asset is checked against the signed hash anyway).
            if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
                || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                || asset.Size <= 0 || asset.Sha256.Length != 64)
                throw new UpdateVerificationException($"The update information has a malformed '{key}' entry.");
        }
        return manifest;
    }
}

public static class UpdateVersion
{
    /// <summary>This build's version, from the assembly's informational version (the csproj
    /// <c>&lt;Version&gt;</c>) with any <c>+commit</c> suffix stripped.</summary>
    public static Version Current { get; } = Parse(
        typeof(UpdateVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0, 0);

    /// <summary>Parses "3.84.4.0", "v3.84.4.0" or "3.84.4.0+abc"; missing parts count as 0, so
    /// "3.85" equals "3.85.0.0".</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOfAny(['+', '-']);
        if (plus >= 0) s = s[..plus];
        if (!System.Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    public static bool IsNewer(string candidate, Version current)
        => Parse(candidate) is { } v && v > current;
}
