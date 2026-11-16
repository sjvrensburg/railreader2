using RailReader.Renderer.Skia;
using RailReader2.Library;
using SkiaSharp;

namespace RailReader2.Services;

/// <summary>
/// <c>RailReader2 --library-probe [--output report.txt]</c>: a headless self-check of library search in
/// this build of the app, then exit (0 = passed, 1 = failed). It writes a one-page PDF with SkiaSharp,
/// indexes it through the app's own SQLite and PDFium into a temporary database, and searches it
/// (<see cref="LibraryRoundTrip"/>). No window, no logger, nothing in ConfigDir.
/// <para>CI runs it on the published and the installed Windows app (railreader2#298): the app is
/// ReadyToRun, not AOT like the CLI, and loads its native libraries its own way, and it is a GUI-subsystem
/// executable on Windows, so its console output is lost — hence <c>--output</c> and the exit code.</para>
/// </summary>
internal static class LibraryProbeMode
{
    const string Flag = "--library-probe";
    const string Term = "railreaderprobe";

    /// <summary>Runs the probe when <paramref name="args"/> asks for it; returns the exit code, else null.</summary>
    public static int? TryRun(string[] args)
    {
        if (!args.Contains(Flag)) return null;
        int at = Array.IndexOf(args, "--output");
        string? output = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;

        var dir = Directory.CreateTempSubdirectory("rr2-library-probe-").FullName;
        bool ok;
        string report;
        try
        {
            var pdf = Path.Combine(dir, "probe.pdf");
            WritePdf(pdf);
            (ok, report) = LibraryRoundTrip.Run(pdf, Term, new SkiaPdfServiceFactory(), Path.Combine(dir, "library.db"));
        }
        catch (Exception ex)
        {
            (ok, report) = (false, $"FAILED before the round trip: {ex}");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        report = $"RailReader2 {typeof(LibraryProbeMode).Assembly.GetName().Version} library probe ({AppContext.BaseDirectory})\n" + report;
        Console.Out.Write(report);
        if (output is not null)
        {
            try { File.WriteAllText(output, report); }
            catch (Exception ex) { Console.Error.WriteLine($"Couldn't write {output}: {ex.Message}"); return 1; }
        }
        return ok ? 0 : 1;
    }

    static void WritePdf(string path)
    {
        using var doc = SKDocument.CreatePdf(path);
        using var font = new SKFont { Size = 24 };
        using var paint = new SKPaint { Color = SKColors.Black };
        using (var canvas = doc.BeginPage(612, 792))
            canvas.DrawText($"Library search {Term} check", 72, 100, SKTextAlign.Left, font, paint);
        doc.EndPage();
        doc.Close();
    }
}
