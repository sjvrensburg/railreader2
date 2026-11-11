using System.Runtime.InteropServices;

namespace RailReader2.Services.Updates;

/// <summary>How this copy of RailReader2 was installed, which decides what an update can do.</summary>
public enum InstallKind
{
    /// <summary><c>dotnet run</c>, a Debug build, or a build with no install marker. No background check.</summary>
    Development,
    /// <summary>Turned off with <c>RR_DISABLE_UPDATES=1</c> (e.g. by a repackager).</summary>
    Disabled,
    /// <summary>A Linux AppImage (<c>$APPIMAGE</c> set). Replaces its own file.</summary>
    AppImage,
    /// <summary>The Windows Inno Setup install (<c>unins000.exe</c> beside the exe). Runs the new installer silently.</summary>
    Inno,
    /// <summary>The Microsoft Store MSIX. Never updates itself (Store policy); opens the Store page.</summary>
    Msix,
    /// <summary>The Linux tarball (CI writes an <c>install-kind</c> marker). Notifies and opens the release page.</summary>
    Tarball,
}

public static class UpdateChannel
{
    /// <summary>Name of the marker file CI writes into the tarball's directory.</summary>
    public const string MarkerFileName = "install-kind";

    private static InstallKind? _current;

    public static InstallKind Current => _current ??= Detect(
        Environment.GetEnvironmentVariable,
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() && IsMsixPackaged(),
#if DEBUG
        debugBuild: true);
#else
        debugBuild: false);
#endif

    /// <summary>Whether a once-a-day background check makes sense (never for the Store or a dev build).</summary>
    public static bool SupportsBackgroundCheck(InstallKind kind)
        => kind is InstallKind.AppImage or InstallKind.Inno or InstallKind.Tarball;

    /// <summary>Whether the app can install an update itself (rather than pointing at a web page).</summary>
    public static bool CanInstallInPlace(InstallKind kind)
        => kind is InstallKind.AppImage or InstallKind.Inno;

    /// <summary>Key of this channel's asset in <see cref="UpdateManifest.Assets"/>, or null if it has none.</summary>
    public static string? AssetKey(InstallKind kind) => kind switch
    {
        InstallKind.AppImage => "appimage",
        InstallKind.Inno => "inno",
        InstallKind.Tarball => "tarball",
        _ => null,
    };

    internal static InstallKind Detect(Func<string, string?> env, string baseDirectory, bool msixPackaged, bool debugBuild)
    {
        if (env("RR_DISABLE_UPDATES") is "1" or "true" or "yes" or "on") return InstallKind.Disabled;
        if (msixPackaged) return InstallKind.Msix;
        if (debugBuild) return InstallKind.Development;

        if (env("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
        {
            // A dev build launched from a shell that happens to sit inside an AppImage's environment
            // would inherit $APPIMAGE; only trust it when this binary really lives in the mount.
            var appDir = env("APPDIR");
            if (appDir is { Length: > 0 } && IsUnder(baseDirectory, appDir)) return InstallKind.AppImage;
        }

        if (File.Exists(Path.Combine(baseDirectory, "unins000.exe"))) return InstallKind.Inno;

        var marker = Path.Combine(baseDirectory, MarkerFileName);
        if (File.Exists(marker))
        {
            try
            {
                if (File.ReadAllText(marker).Trim().Equals("tarball", StringComparison.OrdinalIgnoreCase))
                    return InstallKind.Tarball;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return InstallKind.Development;
    }

    private static bool IsUnder(string path, string dir)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(d, StringComparison.Ordinal);
    }

    // APPMODEL_ERROR_NO_PACKAGE (15700) when the process has no package identity;
    // ERROR_INSUFFICIENT_BUFFER (122) for a zero-length query when it has one.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    private static bool IsMsixPackaged()
    {
        try
        {
            int length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch (EntryPointNotFoundException) { return false; } // pre-Windows 8
        catch (DllNotFoundException) { return false; }
    }
}
