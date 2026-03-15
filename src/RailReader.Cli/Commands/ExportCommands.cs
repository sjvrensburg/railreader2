using System.CommandLine;
using RailReader.Cli.Output;
using RailReader.Core.Commands;
using RailReader.Core.Services;

namespace RailReader.Cli.Commands;

public static class ExportCommands
{
    public static Command Create()
    {
        var cmd = new Command("export", "Export pages as images");

        var pageImageCmd = new Command("page-image", "Export a single page as a PNG image");
        var outputArg = new Argument<string>("output") { Description = "Output file path" };
        var pageOpt = new Option<int?>("--page") { Description = "Page number (1-based, default: current)" };
        var dpiOpt = new Option<int>("--dpi") { Description = "Render DPI", DefaultValueFactory = _ => 150 };
        var overlayOpt = new Option<bool>("--overlay") { Description = "Include rail overlay" };
        var annotationsOpt = new Option<bool>("--annotations") { Description = "Include annotations" };
        var debugOpt = new Option<bool>("--debug") { Description = "Include debug overlay" };
        pageImageCmd.Add(outputArg);
        pageImageCmd.Add(pageOpt); pageImageCmd.Add(dpiOpt);
        pageImageCmd.Add(overlayOpt); pageImageCmd.Add(annotationsOpt); pageImageCmd.Add(debugOpt);
        pageImageCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var page = pr.GetValue(pageOpt);
            var dpi = pr.GetValue(dpiOpt);
            var overlay = pr.GetValue(overlayOpt);
            var annotations = pr.GetValue(annotationsOpt);
            var debug = pr.GetValue(debugOpt);
            int p = page.HasValue ? session.ValidatePage(doc, page.Value) : doc.CurrentPage;
            if (p != doc.CurrentPage) session.Controller.GoToPage(p);

            var output = Path.GetFullPath(pr.GetValue(outputArg));
            var options = new ScreenshotOptions
            {
                Dpi = dpi, RailOverlay = overlay, Annotations = annotations,
                SearchHighlights = false, DebugOverlay = debug,
            };

            using var bitmap = ScreenshotCompositor.RenderPage(doc, session.Controller, options);
            ScreenshotCompositor.SavePng(bitmap, output);
            var fi = new FileInfo(output);
            fmt.WriteMessage($"Exported page {p + 1} to {output} ({bitmap.Width}x{bitmap.Height}, {fi.Length:N0} bytes)");
            fmt.WriteResult(new ExportResult(output, bitmap.Width, bitmap.Height, fi.Length));
        });

        var pageRangeCmd = new Command("page-range", "Export multiple pages as numbered PNGs");
        var dirArg = new Argument<string>("output-dir") { Description = "Output directory" };
        var fromOpt = new Option<int>("--from") { Description = "Start page (1-based)", DefaultValueFactory = _ => 1 };
        var toOpt = new Option<int?>("--to") { Description = "End page (1-based, default: last)" };
        var rangeDpiOpt = new Option<int>("--dpi") { Description = "Render DPI", DefaultValueFactory = _ => 150 };
        pageRangeCmd.Add(dirArg);
        pageRangeCmd.Add(fromOpt); pageRangeCmd.Add(toOpt); pageRangeCmd.Add(rangeDpiOpt);
        pageRangeCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var from = pr.GetValue(fromOpt);
            var to = pr.GetValue(toOpt);
            var dpi = pr.GetValue(rangeDpiOpt);
            int startPage = session.ValidatePage(doc, from);
            int endPage = to.HasValue ? session.ValidatePage(doc, to.Value) : doc.PageCount - 1;

            var dir = Path.GetFullPath(pr.GetValue(dirArg));
            Directory.CreateDirectory(dir);
            var options = new ScreenshotOptions { Dpi = dpi, RailOverlay = false, Annotations = false, SearchHighlights = false };
            var exported = new List<object>();

            for (int p = startPage; p <= endPage; p++)
            {
                session.Controller.GoToPage(p);
                var filename = $"page_{p + 1:D4}.png";
                var path = Path.Combine(dir, filename);
                using var bitmap = ScreenshotCompositor.RenderPage(doc, session.Controller, options);
                ScreenshotCompositor.SavePng(bitmap, path);
                var size = new FileInfo(path).Length;
                exported.Add(new { page = p + 1, path, width = bitmap.Width, height = bitmap.Height, size });
                fmt.WriteMessage($"  Page {p + 1}: {filename} ({bitmap.Width}x{bitmap.Height})");
            }
            fmt.WriteResult(new { directory = dir, pages = exported });
        });

        cmd.Add(pageImageCmd);
        cmd.Add(pageRangeCmd);
        return cmd;
    }
}
