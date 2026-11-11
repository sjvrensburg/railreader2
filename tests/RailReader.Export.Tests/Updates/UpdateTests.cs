using System.Security.Cryptography;
using System.Text;
using RailReader2.Services.Updates;
using Xunit;

namespace RailReader.Export.Tests.Updates;

public class UpdateManifestTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Updates", "Fixtures", name);

    private static (byte[] Manifest, byte[] Sig, string Key) LoadFixture()
        => (File.ReadAllBytes(Fixture("update-manifest.json")), File.ReadAllBytes(Fixture("update-manifest.json.sig")),
            File.ReadAllText(Fixture("test-signing-key.pub.pem")));

    [Fact]
    public void OpensslSignedManifest_FromTheReleaseScript_Verifies()
    {
        var (manifest, sig, key) = LoadFixture();
        var m = UpdateManifestReader.ReadVerified(manifest, sig, key);
        Assert.Equal(1, m.FormatVersion);
        Assert.Equal("99.0.0.0", m.Version);
        Assert.Equal("railreader2-x86_64.AppImage", m.Assets["appimage"].Name);
        Assert.Equal(64, m.Assets["inno"].Sha256.Length);
        Assert.True(m.Assets["tarball"].Size > 0);
    }

    [Fact]
    public void TamperedManifest_IsRejected()
    {
        var (manifest, sig, key) = LoadFixture();
        var text = Encoding.UTF8.GetString(manifest).Replace("99.0.0.0", "99.0.0.1");
        Assert.Throws<UpdateVerificationException>(() =>
            UpdateManifestReader.ReadVerified(Encoding.UTF8.GetBytes(text), sig, key));
    }

    [Fact]
    public void SignatureFromAnotherKey_IsRejected()
    {
        var (manifest, _, key) = LoadFixture();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sig = other.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Throws<UpdateVerificationException>(() => UpdateManifestReader.ReadVerified(manifest, sig, key));
    }

    [Fact]
    public void GarbageSignature_IsRejected()
    {
        var (manifest, _, key) = LoadFixture();
        Assert.Throws<UpdateVerificationException>(() =>
            UpdateManifestReader.ReadVerified(manifest, [1, 2, 3], key));
    }

    [Theory]
    [InlineData("http://example.com/x.AppImage")]
    [InlineData("file:///tmp/x.AppImage")]
    [InlineData("not a url")]
    public void SignedManifest_WithNonHttpsAsset_IsRejected(string url)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = "{\"format_version\":1,\"version\":\"9.0.0.0\",\"tag\":\"v9.0.0.0\",\"release_url\":\"https://x\","
            + "\"assets\":{\"appimage\":{\"name\":\"a\",\"url\":\"" + url + "\",\"size\":5,\"sha256\":\""
            + new string('a', 64) + "\"}}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var sig = k.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Throws<UpdateVerificationException>(() =>
            UpdateManifestReader.ReadVerified(bytes, sig, k.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void SignedManifest_WithUnreadableVersion_IsRejected()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Encoding.UTF8.GetBytes("""{"format_version":1,"version":"latest","assets":{}}""");
        var sig = k.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Throws<UpdateVerificationException>(() =>
            UpdateManifestReader.ReadVerified(bytes, sig, k.ExportSubjectPublicKeyInfoPem()));
    }
}

public class UpdateVersionTests
{
    [Theory]
    [InlineData("3.84.4.0", "3.84.4.0")]
    [InlineData("v3.84.4.0", "3.84.4.0")]
    [InlineData("3.84.4.0+023f0fd3", "3.84.4.0")]
    [InlineData("3.85", "3.85.0.0")]
    public void Parse_Normalises(string text, string expected)
        => Assert.Equal(Version.Parse(expected), UpdateVersion.Parse(text));

    [Theory]
    [InlineData("3.84.5.0", "3.84.4.0", true)]
    [InlineData("3.85", "3.84.4.0", true)]
    [InlineData("3.84.4.0", "3.84.4.0", false)]
    [InlineData("3.84.4", "3.84.4.0", false)]
    [InlineData("3.84.3.9", "3.84.4.0", false)]
    [InlineData("garbage", "3.84.4.0", false)]
    public void IsNewer(string candidate, string current, bool expected)
        => Assert.Equal(expected, UpdateVersion.IsNewer(candidate, Version.Parse(current)));

    [Fact]
    public void Current_IsTheCsprojVersion()
        => Assert.True(UpdateVersion.Current > new Version(3, 0, 0, 0));
}

public class UpdateChannelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr-upd-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private InstallKind Detect(Dictionary<string, string>? env = null, bool msix = false, bool debug = false, string? baseDir = null)
        => UpdateChannel.Detect(k => env is not null && env.TryGetValue(k, out var v) ? v : null,
            baseDir ?? _dir, msix, debug);

    [Fact] public void NoMarkers_IsDevelopment() => Assert.Equal(InstallKind.Development, Detect());

    [Fact] public void DebugBuild_IsDevelopment_EvenWithMarkers()
    {
        File.WriteAllText(Path.Combine(_dir, "unins000.exe"), "");
        Assert.Equal(InstallKind.Development, Detect(debug: true));
    }

    [Fact] public void Disabled_WinsOverEverything()
        => Assert.Equal(InstallKind.Disabled, Detect(new() { ["RR_DISABLE_UPDATES"] = "1" }, msix: true));

    [Fact] public void Msix() => Assert.Equal(InstallKind.Msix, Detect(msix: true));

    [Fact] public void Inno()
    {
        File.WriteAllText(Path.Combine(_dir, "unins000.exe"), "");
        Assert.Equal(InstallKind.Inno, Detect());
    }

    [Fact] public void Tarball()
    {
        File.WriteAllText(Path.Combine(_dir, UpdateChannel.MarkerFileName), "tarball\n");
        Assert.Equal(InstallKind.Tarball, Detect());
    }

    [Fact] public void AppImage_RequiresBinaryInsideAppDir()
    {
        var image = Path.Combine(_dir, "railreader2-x86_64.AppImage");
        File.WriteAllText(image, "");
        var appDir = Path.Combine(_dir, "mount");
        var bin = Path.Combine(appDir, "usr", "bin");
        Directory.CreateDirectory(bin);
        var env = new Dictionary<string, string> { ["APPIMAGE"] = image, ["APPDIR"] = appDir };
        Assert.Equal(InstallKind.AppImage, Detect(env, baseDir: bin));
        // Inherited $APPIMAGE in a shell, binary elsewhere: not an AppImage.
        Assert.Equal(InstallKind.Development, Detect(env));
    }

    [Fact]
    public void OnlyInPlaceChannels_HaveAssetsToInstall()
    {
        Assert.True(UpdateChannel.CanInstallInPlace(InstallKind.AppImage));
        Assert.True(UpdateChannel.CanInstallInPlace(InstallKind.Inno));
        Assert.False(UpdateChannel.CanInstallInPlace(InstallKind.Tarball));
        Assert.False(UpdateChannel.SupportsBackgroundCheck(InstallKind.Msix));
        Assert.False(UpdateChannel.SupportsBackgroundCheck(InstallKind.Development));
    }
}

public class UpdatePreferencesTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void NotAnswered_NeverDue() => Assert.False(new UpdatePreferences().IsCheckDue(Now));
    [Fact] public void Declined_NeverDue() => Assert.False(new UpdatePreferences { CheckAutomatically = false }.IsCheckDue(Now));
    [Fact] public void OptedIn_NeverChecked_Due() => Assert.True(new UpdatePreferences { CheckAutomatically = true }.IsCheckDue(Now));
    [Fact] public void CheckedAnHourAgo_NotDue()
        => Assert.False(new UpdatePreferences { CheckAutomatically = true, LastCheckUtc = Now.AddHours(-1) }.IsCheckDue(Now));
    [Fact] public void CheckedYesterday_Due()
        => Assert.True(new UpdatePreferences { CheckAutomatically = true, LastCheckUtc = Now.AddHours(-25) }.IsCheckDue(Now));
    [Fact] public void ClockWentBack_Due()
        => Assert.True(new UpdatePreferences { CheckAutomatically = true, LastCheckUtc = Now.AddDays(3) }.IsCheckDue(Now));
}

public class UpdateInstallerTests
{
    [Fact]
    public void InnoArguments_PerUser_WithRelaunch()
    {
        var args = UpdateInstaller.InnoArguments(4242, relaunch: true, allUsers: false, logPath: "C:\\t\\u.log");
        Assert.Contains("/VERYSILENT", args);
        Assert.Contains("/SUPPRESSMSGBOXES", args);
        Assert.Contains("/CURRENTUSER", args);
        Assert.Contains("/WAITPID=4242", args);
        Assert.Contains("/RELAUNCH=1", args);
        Assert.DoesNotContain("/ALLUSERS", args);
    }

    [Fact]
    public void InnoArguments_AllUsers_InstallOnExit()
    {
        var args = UpdateInstaller.InnoArguments(1, relaunch: false, allUsers: true, logPath: "x");
        Assert.Contains("/ALLUSERS", args);
        Assert.DoesNotContain("/RELAUNCH=1", args);
    }

    [Fact]
    public void AppImageRelaunch_DropsOldMountVariables()
    {
        var psi = UpdateInstaller.ExitCommand(new PreparedUpdate.ReplacedAppImage("9.0", "/opt/x.AppImage"),
            relaunch: true, ["/home/u/doc.pdf"]);
        Assert.NotNull(psi);
        Assert.Equal("/opt/x.AppImage", psi!.FileName);
        Assert.Equal(["/home/u/doc.pdf"], psi.ArgumentList);
        Assert.False(psi.Environment.ContainsKey("APPDIR"));
        Assert.False(psi.Environment.ContainsKey("APPIMAGE"));
        Assert.Null(UpdateInstaller.ExitCommand(new PreparedUpdate.ReplacedAppImage("9.0", "/opt/x.AppImage"),
            relaunch: false, []));
    }

    [Fact]
    public async Task DownloadVerified_RejectsChecksumMismatch_AndDeletesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rr-upd-{Guid.NewGuid():N}.bin");
        var asset = new UpdateAsset
        {
            Name = "x", Size = 5, Sha256 = new string('0', 64),
            Url = "data:application/octet-stream;base64,aGVsbG8=", // not used: see below
        };
        // No network in tests: exercise the verification path through a local HTTP listener.
        using var server = new LocalServer("hello"u8.ToArray());
        asset.Url = server.Url;
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            UpdateInstaller.DownloadVerifiedAsync(asset, path, null, CancellationToken.None));
        Assert.False(File.Exists(path));

        asset.Sha256 = Convert.ToHexString(SHA256.HashData("hello"u8));
        await UpdateInstaller.DownloadVerifiedAsync(asset, path, null, CancellationToken.None);
        Assert.Equal("hello", File.ReadAllText(path));
        File.Delete(path);

        asset.Size = 3; // server sends more than the manifest promised
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            UpdateInstaller.DownloadVerifiedAsync(asset, path, null, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        public string Url { get; }

        public LocalServer(byte[] body)
        {
            int port = System.Net.IPEndPoint.MinPort;
            using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            }
            Url = $"http://127.0.0.1:{port}/asset";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch { return; }
                }
            });
        }

        public void Dispose() => _listener.Close();
    }
}
