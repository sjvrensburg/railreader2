using RailReader.Core;
using RailReader2.Services.Updates;

namespace RailReader.Cli.Commands;

/// <summary>
/// <c>update</c>: the app's update check and download, headless. <c>check</c> fetches and verifies the latest
/// release's signed <c>update-manifest.json</c>; <c>download</c> also fetches one channel's asset and checks it
/// against the signed size and SHA-256. Installs nothing: the app does that for its own installation.
/// </summary>
public static class UpdateCommand
{
    public static int Execute(string[] args, ILogger logger)
    {
        if (args.Length == 0 || Program.HasHelpFlag(args))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var feed = Program.GetOption(args, "feed");
        string? key = null;
        if (Program.GetOption(args, "public-key") is { } keyPath)
        {
            if (!File.Exists(keyPath)) return Program.Fail($"Public key not found: {keyPath}");
            key = File.ReadAllText(keyPath);
        }

        var result = UpdateFeed.CheckAsync(CancellationToken.None, feed, key).GetAwaiter().GetResult();
        if (result.Status == UpdateCheckStatus.Failed || result.Manifest is not { } manifest)
            return Program.Fail(result.Error ?? "The update check failed.");

        Console.WriteLine($"This version:   {UpdateVersion.Current}");
        Console.WriteLine($"Latest release: {manifest.Version} ({manifest.ReleaseUrl})");
        Console.WriteLine("Signature:      verified");
        foreach (var (name, asset) in manifest.Assets.OrderBy(a => a.Key))
            Console.WriteLine($"  {name,-9} {asset.Name}  {asset.Size:N0} bytes  sha256 {asset.Sha256[..16]}…");
        if (result.Error is { } note) Console.WriteLine(note);

        switch (args[0])
        {
            case "check":
                Console.WriteLine(result.Status == UpdateCheckStatus.Available ? "An update is available." : "Up to date.");
                return result.Status == UpdateCheckStatus.Available ? 2 : 0;

            case "download":
            {
                var channel = Program.GetOption(args, "channel");
                if (channel is null) return Program.Fail("--channel is required (appimage, inno or tarball).");
                if (!manifest.Assets.TryGetValue(channel, out var asset))
                    return Program.Fail($"The release has no '{channel}' asset. Channels: {string.Join(", ", manifest.Assets.Keys)}.");
                var outDir = Path.GetFullPath(Program.GetOption(args, "output-dir") ?? ".");
                Directory.CreateDirectory(outDir);
                var path = Path.Combine(outDir, asset.Name);
                if (File.Exists(path)) return Program.Fail($"{path} already exists.");

                int lastTenth = -1;
                var progress = new SyncProgress(p =>
                {
                    int tenth = (int)(p * 10);
                    if (tenth != lastTenth) { lastTenth = tenth; Console.Error.Write($"\r{p:P0}   "); }
                });
                try
                {
                    UpdateInstaller.DownloadVerifiedAsync(asset, path, progress, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException)
                {
                    Console.Error.WriteLine();
                    return Program.Fail(ex.Message);
                }
                Console.Error.WriteLine();
                Console.WriteLine($"Downloaded and verified: {path}");
                return 0;
            }

            default:
                return Program.Fail($"Unknown update action '{args[0]}'. Use 'check' or 'download'.");
        }
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool, out of order).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    static void PrintHelp()
    {
        Console.WriteLine("railreader2-cli update — Check for, and download, a new RailReader2 release");
        Console.WriteLine();
        Console.WriteLine("Usage: railreader2-cli update check [options]");
        Console.WriteLine("       railreader2-cli update download --channel <appimage|inno|tarball> [options]");
        Console.WriteLine();
        Console.WriteLine("Reads the latest release's signed update-manifest.json from GitHub and verifies it.");
        Console.WriteLine("'download' also fetches one install channel's file and checks its size and SHA-256.");
        Console.WriteLine("Nothing is installed: the RailReader2 app updates its own installation.");
        Console.WriteLine("Exit codes for 'check': 0 up to date, 2 update available, 1 error.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --channel <name>      Asset to download: appimage, inno or tarball");
        Console.WriteLine("  --output-dir <dir>    Where to save the download (default: current directory)");
        Console.WriteLine("  --feed <url|dir>      Read the manifest from here instead (testing)");
        Console.WriteLine("  --public-key <pem>    Verify with this key instead of the built-in one (testing)");
    }
}
