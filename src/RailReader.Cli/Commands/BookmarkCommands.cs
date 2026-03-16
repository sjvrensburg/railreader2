using System.CommandLine;
using RailReader.Cli.Output;

namespace RailReader.Cli.Commands;

public static class BookmarkCommands
{
    public static Command Create()
    {
        var cmd = new Command("bookmark", "Manage named bookmarks");

        var listCmd = new Command("list", "List all bookmarks");
        listCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var bookmarks = doc.Annotations.Bookmarks;
            if (bookmarks.Count == 0) { fmt.WriteMessage("No bookmarks."); return; }

            if (fmt is JsonFormatter)
            {
                fmt.WriteResult(new { bookmarks = bookmarks.Select((b, i) => new { index = i, name = b.Name, page = b.Page + 1 }) });
                return;
            }

            fmt.WriteMessage($"Bookmarks for {doc.Title}:\n");
            HumanFormatter.WriteTable(
                bookmarks.Select((b, i) => new object[] { i, b.Name, b.Page + 1 }),
                "#", "Name", "Page");
        });

        var addCmd = new Command("add", "Add a bookmark");
        var nameArg = new Argument<string>("name") { Description = "Bookmark name" };
        var pageOpt = new Option<int?>("--page") { Description = "Page number (1-based, default: current)" };
        addCmd.Add(nameArg); addCmd.Add(pageOpt);
        addCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var name = pr.GetValue(nameArg);
            var page = pr.GetValue(pageOpt);
            int p = page.HasValue ? session.ValidatePage(doc, page.Value) : doc.CurrentPage;
            doc.AddBookmark(name, p);
            fmt.WriteMessage($"Added bookmark \"{name}\" at page {p + 1}");
            fmt.WriteResult(new { name, page = p + 1 });
        });

        var removeCmd = new Command("remove", "Remove a bookmark by index");
        var indexArg = new Argument<int>("index") { Description = "Bookmark index from 'bookmark list'" };
        removeCmd.Add(indexArg);
        removeCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var index = pr.GetValue(indexArg);
            var bookmarks = doc.Annotations.Bookmarks;
            if (index < 0 || index >= bookmarks.Count) { fmt.WriteError($"Invalid bookmark index: {index}"); return; }
            var name = bookmarks[index].Name;
            doc.RemoveBookmark(index);
            fmt.WriteMessage($"Removed bookmark \"{name}\"");
            fmt.WriteResult(new { removed = name, index });
        });

        cmd.Add(listCmd); cmd.Add(addCmd); cmd.Add(removeCmd);
        return cmd;
    }
}
