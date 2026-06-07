using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace RailReader.Demo;

/// <summary>
/// Screen recorder backed by GNOME Shell's built-in <c>org.gnome.Shell.Screencast</c> D-Bus API.
/// It encodes to WebM itself (its own GStreamer pipeline), so no PipeWire/ffmpeg is needed to
/// capture and there is no portal picker prompt — ideal for scripted demos. If the requested
/// output extension isn't <c>.webm</c> we transcode with ffmpeg when present, otherwise we keep
/// the <c>.webm</c> and say so.
/// </summary>
public sealed class GnomeScreenRecorder : IScreenRecorder
{
    private const string BusName = "org.gnome.Shell.Screencast";
    private const string ObjectPath = "/org/gnome/Shell/Screencast";
    private const string Interface = "org.gnome.Shell.Screencast";

    private readonly TextWriter _log;
    private DBusConnection? _conn;
    private string? _capturePath;   // the .webm GNOME actually wrote
    private string? _outputPath;    // what the user asked for
    private bool _recording;

    public GnomeScreenRecorder(TextWriter log) => _log = log;

    public async Task StartAsync(string outputPath, int fps, CancellationToken ct)
    {
        _outputPath = Path.GetFullPath(outputPath);
        // Ask GNOME to write straight to the requested path. GNOME picks its own encoder/container
        // (H.264 MP4 on recent versions) and may swap the extension; the actual path it used comes
        // back from the Screencast call, and we reconcile it with the request in StopAsync.
        _capturePath = _outputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        if (File.Exists(_outputPath)) File.Delete(_outputPath);

        var address = DBusAddress.Session
            ?? throw new InvalidOperationException("No D-Bus session bus address.");
        _conn = new DBusConnection(address);
        await _conn.ConnectAsync().ConfigureAwait(false);

        // MessageWriter is a ref struct and can't cross an await — build the buffer, then call.
        MessageBuffer msg;
        using (var w = _conn.GetMessageWriter())
        {
            w.WriteMethodCallHeader(BusName, ObjectPath, Interface, "Screencast", "sa{sv}", MessageFlags.None);
            w.WriteString(_capturePath);
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart(); w.WriteString("framerate"); w.WriteVariantInt32(fps);
            w.WriteDictionaryEntryStart(); w.WriteString("draw-cursor"); w.WriteVariantBool(false);
            w.WriteDictionaryEnd(dict);
            msg = w.CreateMessage();
        }

        var (ok, used) = await _conn.CallMethodAsync(msg,
            static (Message m, object? s) => { var r = m.GetBodyReader(); return (r.ReadBool(), r.ReadString()); }, null)
            .ConfigureAwait(false);

        if (!ok)
            throw new InvalidOperationException("GNOME Shell refused to start the screencast (org.gnome.Shell.Screencast returned false).");

        if (!string.IsNullOrEmpty(used)) _capturePath = used; // GNOME may pick its own filename
        _recording = true;
        _log.WriteLine($"Recording (GNOME screencast) → {_capturePath}");
    }

    public async Task<string> StopAsync(CancellationToken ct)
    {
        if (!_recording || _conn is null) return "";
        _recording = false;

        MessageBuffer msg;
        using (var w = _conn.GetMessageWriter())
        {
            w.WriteMethodCallHeader(BusName, ObjectPath, Interface, "StopScreencast", "", MessageFlags.None);
            msg = w.CreateMessage();
        }
        await _conn.CallMethodAsync(msg,
            static (Message m, object? s) => m.GetBodyReader().ReadBool(), null).ConfigureAwait(false);

        // GNOME finalises the file asynchronously after StopScreencast returns; wait briefly for it.
        var capture = _capturePath!;
        for (int i = 0; i < 50 && (!File.Exists(capture) || new FileInfo(capture).Length == 0); i++)
            await Task.Delay(100, ct).ConfigureAwait(false);

        string final = await FinaliseAsync(capture, _outputPath!).ConfigureAwait(false);
        _log.WriteLine($"Saved recording → {final}");
        return final;
    }

    /// <summary>Reconcile the file GNOME actually wrote with the requested output path: nothing to
    /// do if they match, a move if the container matches, otherwise an ffmpeg transcode (kept as-is
    /// if ffmpeg is absent).</summary>
    private async Task<string> FinaliseAsync(string capture, string output)
    {
        output = Path.GetFullPath(output);
        if (string.Equals(capture, output, StringComparison.Ordinal))
            return output; // GNOME wrote exactly what was requested

        if (string.Equals(Path.GetExtension(capture), Path.GetExtension(output), StringComparison.OrdinalIgnoreCase))
        {
            MoveOverwrite(capture, output);
            return output;
        }

        if (FfmpegPath() is { } ffmpeg)
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                ArgumentList = { "-y", "-i", capture, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync().ConfigureAwait(false);
            if (proc.ExitCode == 0 && File.Exists(output))
            {
                File.Delete(capture);
                return output;
            }
            _log.WriteLine($"  (ffmpeg transcode failed, exit {proc.ExitCode}; keeping {capture})");
            return capture;
        }

        _log.WriteLine($"  (ffmpeg not found; kept WebM instead of {Path.GetFileName(output)})");
        return capture;
    }

    private static void MoveOverwrite(string from, string to)
    {
        if (File.Exists(to)) File.Delete(to);
        File.Move(from, to);
    }

    private static string? FfmpegPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var p = Path.Combine(dir, "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_recording) { try { await StopAsync(CancellationToken.None); } catch { /* best effort */ } }
        _conn?.Dispose();
        _conn = null;
    }
}
