using System.CommandLine;
using RailReader.Cli.Output;

namespace RailReader.Cli.Commands;

public static class DocumentCommands
{
    public static Command Create()
    {
        var cmd = new Command("document", "Open, inspect, and manage PDF documents");

        var openCmd = new Command("open", "Open a PDF document");
        var pathArg = new Argument<string>("path") { Description = "Path to the PDF file" };
        openCmd.Add(pathArg);
        openCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var path = Path.GetFullPath(pr.GetValue(pathArg));
            if (!File.Exists(path)) { fmt.WriteError($"File not found: {path}"); return; }

            var state = session.Controller.CreateDocument(path);
            state.LoadPageBitmap();
            state.LoadAnnotations();
            session.Controller.AddDocument(state);

            var info = session.Controller.GetDocumentInfo()!;
            fmt.WriteMessage($"Opened {info.Title} ({info.PageCount} pages)");
            fmt.WriteResult(new
            {
                info.FilePath, info.Title, info.PageCount,
                current_page = info.CurrentPage + 1,
                analysis_available = session.AnalysisAvailable,
            });
        });

        var infoCmd = new Command("info", "Show info about the active document");
        var indexOpt = new Option<int?>("--index") { Description = "Document index (default: active)" };
        infoCmd.Add(indexOpt);
        infoCmd.SetAction(pr =>
        {
            var fmt = SessionBinder.Formatter;
            var index = pr.GetValue(indexOpt);
            var info = SessionBinder.Session.Controller.GetDocumentInfo(index);
            if (info is null) { fmt.WriteError(index.HasValue ? $"No document at index {index}" : "No document open"); return; }
            fmt.WriteResult(info);
        });

        var listCmd = new Command("list", "List all open documents");
        listCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var list = session.Controller.ListDocuments();
            if (list.Documents.Count == 0) { fmt.WriteMessage("No documents open."); return; }

            if (fmt is JsonFormatter) { fmt.WriteResult(list); return; }

            fmt.WriteMessage($"Open documents (active: {list.ActiveIndex}):\n");
            var headers = new[] { "#", "Title", "Pages", "Page" };
            var rows = list.Documents.Select(d => new object[]
            {
                d.Index == list.ActiveIndex ? $"*{d.Index}" : $" {d.Index}",
                d.Title.Length > 50 ? d.Title[..47] + "..." : d.Title,
                d.PageCount, d.CurrentPage + 1,
            });
            HumanFormatter.WriteTable(rows, headers);
        });

        var closeCmd = new Command("close", "Close a document");
        var closeIndexOpt = new Option<int?>("--index") { Description = "Document index to close (default: active)" };
        closeCmd.Add(closeIndexOpt);
        closeCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var index = pr.GetValue(closeIndexOpt);
            var idx = index ?? session.Controller.ActiveDocumentIndex;
            if (idx < 0 || idx >= session.Controller.Documents.Count) { fmt.WriteError("No document to close"); return; }
            var title = session.Controller.Documents[idx].Title;
            session.Controller.CloseDocument(idx);
            fmt.WriteMessage($"Closed {title}");
            fmt.WriteResult(new { closed = title, remaining = session.Controller.Documents.Count });
        });

        var pagesCmd = new Command("pages", "List all pages with dimensions");
        pagesCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();

            if (fmt is JsonFormatter)
            {
                var pageList = Enumerable.Range(0, doc.PageCount).Select(i =>
                {
                    var size = doc.Pdf.GetPageSize(i);
                    return new { page = i + 1, width = Math.Round(size.Width, 1), height = Math.Round(size.Height, 1) };
                });
                fmt.WriteResult(new { doc.Title, page_count = doc.PageCount, pages = pageList });
                return;
            }

            fmt.WriteMessage($"{doc.Title} — {doc.PageCount} pages\n");
            var headers = new[] { "Page", "Width", "Height" };
            var rows = Enumerable.Range(0, doc.PageCount).Select(i =>
            {
                var size = doc.Pdf.GetPageSize(i);
                return new object[] { i + 1, $"{size.Width:F1} pt", $"{size.Height:F1} pt" };
            });
            HumanFormatter.WriteTable(rows, headers);
        });

        var outlineCmd = new Command("outline", "Show the PDF table of contents");
        outlineCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var outline = doc.Outline;
            if (outline is null || outline.Count == 0) { fmt.WriteMessage("No outline/table of contents available."); return; }

            if (fmt is JsonFormatter) { fmt.WriteResult(new { entries = outline }); return; }

            void PrintEntry(RailReader.Core.Models.OutlineEntry entry, int depth)
            {
                var indent = new string(' ', depth * 2);
                var pageStr = entry.Page.HasValue ? $"p.{entry.Page.Value + 1}" : "";
                Console.WriteLine($"  {indent}{entry.Title}  {pageStr}");
                foreach (var child in entry.Children)
                    PrintEntry(child, depth + 1);
            }

            fmt.WriteMessage("Table of Contents:\n");
            foreach (var entry in outline) PrintEntry(entry, 0);
        });

        cmd.Add(openCmd);
        cmd.Add(infoCmd);
        cmd.Add(listCmd);
        cmd.Add(closeCmd);
        cmd.Add(pagesCmd);
        cmd.Add(outlineCmd);
        return cmd;
    }
}
