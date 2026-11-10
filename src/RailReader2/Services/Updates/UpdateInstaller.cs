using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using RailReader.Core;

namespace RailReader2.Services.Updates;

/// <summary>What <see cref="UpdateInstaller.PrepareAsync"/> got ready.</summary>
public abstract record PreparedUpdate(string Version)
{
    /// <summary>The AppImage file has already been replaced; relaunching it starts the new version.</summary>
    public sealed record ReplacedAppImage(string Version, string AppImagePath) : PreparedUpdate(Version);

    /// <summary>The new installer is downloaded and verified; it runs once the app has exited.</summary>
    public sealed record DownloadedInstaller(string Version, string InstallerPath) : PreparedUpdate(Version);
}

/// <summary>
/// Downloads a release asset, verifies it against the signed manifest (size + SHA-256), and installs
/// it the way the channel allows: the AppImage replaces its own file (atomic rename in the same
/// directory); the Inno installer is staged and run silently after exit (<see cref="UpdateRelaunch"/>).
/// </summary>
public static class UpdateInstaller
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Thrown when the update can't be installed in place but the release page still works.</summary>
    public sealed class CannotInstallException(string message) : Exception(message);

    public static async Task<PreparedUpdate> PrepareAsync(UpdateManifest manifest, InstallKind kind,
        IProgress<double>? progress, CancellationToken ct)
    {
        var key = UpdateChannel.AssetKey(kind);
        if (!UpdateChannel.CanInstallInPlace(kind) || key is null || !manifest.Assets.TryGetValue(key, out var asset))
            throw new CannotInstallException("This copy of RailReader2 can't install updates itself.");
        if (manifest.FormatVersion > UpdateManifest.SupportedFormat)
            throw new CannotInstallException("This version can't install the update itself.");

        switch (kind)
        {
            case InstallKind.AppImage:
            {
                var target = Environment.GetEnvironmentVariable("APPIMAGE")
                    ?? throw new CannotInstallException("Couldn't find the AppImage file.");
                var dir = Path.GetDirectoryName(target)!;
                // Same directory as the target, so the final rename is atomic and stays on one filesystem.
                var part = Path.Combine(dir, $".{Path.GetFileName(target)}.update-{manifest.Version}.part");
                try
                {
                    await DownloadVerifiedAsync(asset, part, progress, ct);
                }
                catch (UnauthorizedAccessException)
                {
                    throw new CannotInstallException($"RailReader2 can't write to {dir}. Download the new AppImage from the release page.");
                }
                try
                {
                    File.SetUnixFileMode(part, File.GetUnixFileMode(target) | UnixFileMode.UserExecute
                        | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    // The running instance keeps its open (now unlinked) file; the next launch gets the new one.
                    File.Move(part, target, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDelete(part);
                    throw new CannotInstallException($"Couldn't replace {Path.GetFileName(target)}: {ex.Message}");
                }
                RailReaderLogging.Logger.Info($"[Update] Replaced {target} with {manifest.Version}");
                return new PreparedUpdate.ReplacedAppImage(manifest.Version, target);
            }
            case InstallKind.Inno:
            {
                var dir = Path.Combine(Path.GetTempPath(), "railreader2-update");
                Directory.CreateDirectory(dir);
                foreach (var stale in Directory.EnumerateFiles(dir)) TryDelete(stale);
                var path = Path.Combine(dir, asset.Name);
                await DownloadVerifiedAsync(asset, path, progress, ct);
                RailReaderLogging.Logger.Info($"[Update] Downloaded installer {path} for {manifest.Version}");
                return new PreparedUpdate.DownloadedInstaller(manifest.Version, path);
            }
            default:
                throw new CannotInstallException("This copy of RailReader2 can't install updates itself.");
        }
    }

    /// <summary>The command that installs <paramref name="update"/> once this process has exited, or
    /// null if nothing needs to run (an AppImage that isn't relaunched).</summary>
    public static ProcessStartInfo? ExitCommand(PreparedUpdate update, bool relaunch, string[] relaunchArgs)
    {
        switch (update)
        {
            case PreparedUpdate.ReplacedAppImage a when relaunch:
            {
                var psi = new ProcessStartInfo(a.AppImagePath) { UseShellExecute = false };
                foreach (var arg in relaunchArgs) psi.ArgumentList.Add(arg);
                // The old runtime's mount variables must not leak into the new instance.
                foreach (var v in new[] { "APPIMAGE", "APPDIR", "ARGV0", "OWD" }) psi.Environment.Remove(v);
                return psi;
            }
            case PreparedUpdate.ReplacedAppImage:
                return null;
            case PreparedUpdate.DownloadedInstaller i:
            {
                // UseShellExecute so an all-users install can raise its UAC prompt.
                var psi = new ProcessStartInfo(i.InstallerPath) { UseShellExecute = true };
                foreach (var arg in InnoArguments(Environment.ProcessId, relaunch,
                             IsAllUsersInstall(AppContext.BaseDirectory),
                             Path.Combine(Path.GetTempPath(), "railreader2-update.log")))
                    psi.ArgumentList.Add(arg);
                return psi;
            }
            default:
                return null;
        }
    }

    /// <summary>Silent-install switches. <c>/WAITPID</c> and <c>/RELAUNCH</c> are read by
    /// <c>installer/railreader2.iss</c>'s [Code] section.</summary>
    internal static IReadOnlyList<string> InnoArguments(int waitPid, bool relaunch, bool allUsers, string logPath)
    {
        var args = new List<string>
        {
            "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
            allUsers ? "/ALLUSERS" : "/CURRENTUSER",
            $"/WAITPID={waitPid}",
            $"/LOG={logPath}",
        };
        if (relaunch) args.Add("/RELAUNCH=1");
        return args;
    }

    /// <summary>An all-users install lives under Program Files; a per-user one under LocalAppData.</summary>
    internal static bool IsAllUsersInstall(string baseDirectory)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && Path.GetFullPath(baseDirectory)
                    .StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static async Task DownloadVerifiedAsync(UpdateAsset asset, string path,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            stallCts.CancelAfter(StallTimeout);
            using var resp = await UpdateFeed.Client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
            resp.EnsureSuccessStatusCode();
            stallCts.CancelAfter(StallTimeout);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(stallCts.Token))
            await using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                double lastReported = -1;
                int n;
                while ((n = await src.ReadAsync(buffer, stallCts.Token)) > 0)
                {
                    stallCts.CancelAfter(StallTimeout);
                    read += n;
                    if (read > asset.Size) throw new UpdateVerificationException("The download is larger than expected.");
                    sha.AppendData(buffer, 0, n);
                    await dst.WriteAsync(buffer.AsMemory(0, n), stallCts.Token);
                    double pct = (double)read / asset.Size;
                    if (pct - lastReported >= 0.01) { lastReported = pct; progress?.Report(pct); }
                }
            }
            if (read != asset.Size) throw new UpdateVerificationException("The download is incomplete.");
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateVerificationException("The download doesn't match the signed checksum.");
            progress?.Report(1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(path);
            throw new HttpRequestException("The download stalled.");
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A command to run after the app has shut down normally (annotation and config saves done): the
/// relaunched AppImage, or the silent Inno installer. Run from <c>Program.Main</c> once
/// <c>StartWithClassicDesktopLifetime</c> returns, so two instances never write the config at once.
/// </summary>
public static class UpdateRelaunch
{
    private static ProcessStartInfo? _pending;

    public static bool HasPending => _pending is not null;

    public static void SetPending(ProcessStartInfo? psi) => _pending = psi;

    public static void RunPending()
    {
        var psi = Interlocked.Exchange(ref _pending, null);
        if (psi is null) return;
        try
        {
            RailReaderLogging.Logger.Info($"[Update] Starting {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
            if (!OperatingSystem.IsWindows()) MarkInheritedDescriptorsCloseOnExec();
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Update] Couldn't start the update after exit", ex);
        }
    }

    /// <summary>
    /// Native libraries (GPU driver, PulseAudio, inotify) open descriptors without O_CLOEXEC, and the
    /// AppImage runtime hands the app its keep-alive pipe; .NET's Process.Start passes all of them to the
    /// child. A relaunched AppImage that inherits the old runtime's keep-alive pipe keeps the old FUSE
    /// mount (and its own runtime) alive forever after both apps have exited. This process is exiting,
    /// so marking every descriptor above stderr close-on-exec loses nothing.
    /// </summary>
    private static void MarkInheritedDescriptorsCloseOnExec()
    {
        const int F_SETFD = 2, FD_CLOEXEC = 1;
        try
        {
            foreach (var entry in Directory.GetFiles("/proc/self/fd"))
                if (int.TryParse(Path.GetFileName(entry), out int fd) && fd > 2)
                    _ = fcntl(fd, F_SETFD, FD_CLOEXEC);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
        {
            RailReaderLogging.Logger.Warn($"[Update] Couldn't mark descriptors close-on-exec: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
