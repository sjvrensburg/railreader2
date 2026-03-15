using System.CommandLine;
using RailReader.Cli.Output;

namespace RailReader.Cli.Commands;

public static class AnalysisCommands
{
    public static Command Create()
    {
        var cmd = new Command("analysis", "Run and inspect AI layout analysis");

        var runCmd = new Command("run", "Run layout analysis on a page");
        var pageOpt = new Option<int?>("--page") { Description = "Page number (1-based, default: current)" };
        var timeoutOpt = new Option<int>("--timeout") { Description = "Timeout in milliseconds", DefaultValueFactory = _ => 15000 };
        runCmd.Add(pageOpt);
        runCmd.Add(timeoutOpt);
        runCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var page = pr.GetValue(pageOpt);
            var timeout = pr.GetValue(timeoutOpt);
            if (!session.AnalysisAvailable)
            {
                fmt.WriteError("Layout analysis unavailable. Run ./scripts/download-model.sh to install the ONNX model.");
                return;
            }

            var doc = session.RequireActiveDocument();
            int p = page.HasValue ? session.ValidatePage(doc, page.Value) : doc.CurrentPage;
            if (p != doc.CurrentPage) session.Controller.GoToPage(p);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeout)
            {
                session.Controller.PollAnalysisResults();
                if (doc.AnalysisCache.ContainsKey(p))
                {
                    var layout = session.Controller.GetLayoutInfo(p);
                    fmt.WriteMessage($"Analysis complete: {layout!.Blocks.Count} blocks detected on page {p + 1}");
                    fmt.WriteResult(layout);
                    return;
                }
                Thread.Sleep(50);
            }
            fmt.WriteError($"Analysis timed out after {timeout}ms");
        });

        var statusCmd = new Command("status", "Show analysis worker status");
        statusCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var doc = session.Controller.ActiveDocument;
            SessionBinder.Formatter.WriteResult(new
            {
                available = session.AnalysisAvailable,
                cached_pages = doc?.AnalysisCache.Count ?? 0,
                total_pages = doc?.PageCount ?? 0,
            });
        });

        var listBlocksCmd = new Command("list-blocks", "List detected layout blocks on a page");
        var listPageOpt = new Option<int?>("--page") { Description = "Page number (1-based, default: current)" };
        listBlocksCmd.Add(listPageOpt);
        listBlocksCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var page = pr.GetValue(listPageOpt);
            int p = page.HasValue ? session.ValidatePage(doc, page.Value) : doc.CurrentPage;

            var layout = session.Controller.GetLayoutInfo(p);
            if (layout is null) { fmt.WriteError($"No analysis for page {p + 1}. Run 'analysis run' first."); return; }

            if (fmt is JsonFormatter) { fmt.WriteResult(layout); return; }

            fmt.WriteMessage($"Page {p + 1}: {layout.Blocks.Count} blocks detected\n");
            var headers = new[] { "#", "Class", "Conf", "Order", "Lines", "Nav", "BBox (x,y,w,h)" };
            var rows = layout.Blocks.Select((b, i) => new object[]
            {
                i, b.ClassName, $"{b.Confidence:F2}", b.ReadingOrder,
                b.LineCount, b.Navigable ? "yes" : "",
                $"({b.X:F0}, {b.Y:F0}, {b.W:F0}, {b.H:F0})",
            });
            HumanFormatter.WriteTable(rows, headers);
        });

        cmd.Add(runCmd);
        cmd.Add(statusCmd);
        cmd.Add(listBlocksCmd);
        return cmd;
    }
}
