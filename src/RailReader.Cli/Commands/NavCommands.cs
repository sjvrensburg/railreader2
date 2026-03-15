using System.CommandLine;
using RailReader.Cli.Output;

namespace RailReader.Cli.Commands;

public static class NavCommands
{
    public static Command Create()
    {
        var cmd = new Command("nav", "Navigate pages within a document");

        var gotoCmd = new Command("goto", "Go to a specific page");
        var pageArg = new Argument<int>("page") { Description = "Page number (1-based)" };
        gotoCmd.Add(pageArg);
        gotoCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            int p = session.ValidatePage(doc, pr.GetValue(pageArg));
            session.Controller.GoToPage(p);
            fmt.WriteMessage($"Page {doc.CurrentPage + 1} of {doc.PageCount}");
            fmt.WriteResult(new { page = doc.CurrentPage + 1, page_count = doc.PageCount });
        });

        var nextCmd = new Command("next", "Go to the next page");
        nextCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            if (doc.CurrentPage >= doc.PageCount - 1) { fmt.WriteError("Already on last page"); return; }
            session.Controller.GoToPage(doc.CurrentPage + 1);
            fmt.WriteMessage($"Page {doc.CurrentPage + 1} of {doc.PageCount}");
            fmt.WriteResult(new { page = doc.CurrentPage + 1, page_count = doc.PageCount });
        });

        var prevCmd = new Command("prev", "Go to the previous page");
        prevCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            if (doc.CurrentPage <= 0) { fmt.WriteError("Already on first page"); return; }
            session.Controller.GoToPage(doc.CurrentPage - 1);
            fmt.WriteMessage($"Page {doc.CurrentPage + 1} of {doc.PageCount}");
            fmt.WriteResult(new { page = doc.CurrentPage + 1, page_count = doc.PageCount });
        });

        var statusCmd = new Command("status", "Show current navigation status");
        statusCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            session.RequireActiveDocument();
            var info = session.Controller.GetDocumentInfo()!;
            fmt.WriteResult(new
            {
                title = info.Title,
                page = info.CurrentPage + 1,
                page_count = info.PageCount,
                rail_active = info.RailActive,
                has_analysis = info.HasAnalysis,
                navigable_blocks = info.NavigableBlocks,
            });
        });

        cmd.Add(gotoCmd);
        cmd.Add(nextCmd);
        cmd.Add(prevCmd);
        cmd.Add(statusCmd);
        return cmd;
    }
}
