using System.CommandLine;
using RailReader.Cli.Output;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Cli.Commands;

public static class AnnotationCommands
{
    public static Command Create()
    {
        var cmd = new Command("annotation", "Manage annotations on PDF pages");

        var listCmd = new Command("list", "List annotations");
        var listPageOpt = new Option<int?>("--page") { Description = "Filter by page (1-based)" };
        listCmd.Add(listPageOpt);
        listCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var page = pr.GetValue(listPageOpt);
            if (doc.Annotations.Pages.Count == 0) { fmt.WriteMessage("No annotations."); return; }

            var pages = doc.Annotations.Pages;
            if (page.HasValue)
            {
                int p = session.ValidatePage(doc, page.Value);
                pages = pages.Where(kv => kv.Key == p).ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            if (fmt is JsonFormatter)
            {
                var result = pages.SelectMany(kv => kv.Value.Select((a, i) => new
                {
                    page = kv.Key + 1, index = i,
                    type = a.GetType().Name.Replace("Annotation", ""),
                    details = Describe(a),
                }));
                fmt.WriteResult(new { annotations = result });
                return;
            }

            var headers = new[] { "Page", "#", "Type", "Details" };
            var rows = pages.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value.Select((a, i) => new object[]
            {
                kv.Key + 1, i, a.GetType().Name.Replace("Annotation", ""), Describe(a),
            }));
            HumanFormatter.WriteTable(rows, headers);
        });

        var addHighlightCmd = new Command("add-highlight", "Add a highlight annotation");
        var hPage = new Option<int>("--page") { Description = "Page number (1-based)", Required = true };
        var hx = new Option<float>("--x") { Description = "X coordinate", Required = true };
        var hy = new Option<float>("--y") { Description = "Y coordinate", Required = true };
        var hw = new Option<float>("--w") { Description = "Width", Required = true };
        var hh = new Option<float>("--h") { Description = "Height", Required = true };
        var hColor = new Option<string>("--color") { Description = "Highlight color", DefaultValueFactory = _ => "#FFFF00" };
        var hOpacity = new Option<float>("--opacity") { Description = "Highlight opacity", DefaultValueFactory = _ => 0.35f };
        addHighlightCmd.Add(hPage); addHighlightCmd.Add(hx); addHighlightCmd.Add(hy);
        addHighlightCmd.Add(hw); addHighlightCmd.Add(hh);
        addHighlightCmd.Add(hColor); addHighlightCmd.Add(hOpacity);
        addHighlightCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var pg = pr.GetValue(hPage);
            var x = pr.GetValue(hx);
            var y = pr.GetValue(hy);
            var w = pr.GetValue(hw);
            var h = pr.GetValue(hh);
            var color = pr.GetValue(hColor);
            var opacity = pr.GetValue(hOpacity);
            int p = session.ValidatePage(doc, pg);
            doc.AddAnnotation(p, new HighlightAnnotation
            {
                Rects = [new HighlightRect(x, y, w, h)], Color = color, Opacity = opacity,
            });
            fmt.WriteMessage($"Added highlight on page {pg}");
            fmt.WriteResult(new { page = pg, x, y, w, h, color, opacity });
        });

        var addNoteCmd = new Command("add-note", "Add a text note annotation");
        var nPage = new Option<int>("--page") { Description = "Page number (1-based)", Required = true };
        var nx = new Option<float>("--x") { Description = "X coordinate", Required = true };
        var ny = new Option<float>("--y") { Description = "Y coordinate", Required = true };
        var nText = new Argument<string>("text") { Description = "Note text" };
        var nColor = new Option<string>("--color") { Description = "Note color", DefaultValueFactory = _ => "#FFCC00" };
        addNoteCmd.Add(nPage); addNoteCmd.Add(nx); addNoteCmd.Add(ny);
        addNoteCmd.Add(nText); addNoteCmd.Add(nColor);
        addNoteCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var pg = pr.GetValue(nPage);
            var x = pr.GetValue(nx);
            var y = pr.GetValue(ny);
            var text = pr.GetValue(nText);
            var color = pr.GetValue(nColor);
            int p = session.ValidatePage(doc, pg);
            doc.AddAnnotation(p, new TextNoteAnnotation { X = x, Y = y, Text = text, Color = color, Opacity = 0.9f });
            fmt.WriteMessage($"Added note on page {pg}: \"{text}\"");
            fmt.WriteResult(new { page = pg, x, y, text, color });
        });

        var removeCmd = new Command("remove", "Remove an annotation by page and index");
        var rPage = new Option<int>("--page") { Description = "Page number (1-based)", Required = true };
        var rIndex = new Option<int>("--index") { Description = "Annotation index", Required = true };
        removeCmd.Add(rPage); removeCmd.Add(rIndex);
        removeCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var pg = pr.GetValue(rPage);
            var index = pr.GetValue(rIndex);
            int p = session.ValidatePage(doc, pg);
            if (!doc.Annotations.Pages.TryGetValue(p, out var list) || index < 0 || index >= list.Count)
            { fmt.WriteError($"No annotation at page {pg}, index {index}"); return; }
            doc.RemoveAnnotation(p, list[index]);
            fmt.WriteMessage($"Removed annotation {index} from page {pg}");
            fmt.WriteResult(new { page = pg, index, removed = true });
        });

        var exportPdfCmd = new Command("export-pdf", "Export document with annotations baked into a new PDF");
        var exportPath = new Argument<string>("output") { Description = "Output PDF path" };
        exportPdfCmd.Add(exportPath);
        exportPdfCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var output = Path.GetFullPath(pr.GetValue(exportPath));
            AnnotationExportService.Export(doc.Pdf, doc.Annotations, output);
            var size = new FileInfo(output).Length;
            fmt.WriteMessage($"Exported to {output} ({size:N0} bytes)");
            fmt.WriteResult(new { path = output, size });
        });

        var saveCmd = new Command("save", "Save annotations");
        saveCmd.SetAction(pr =>
        {
            SessionBinder.Session.RequireActiveDocument().SaveAnnotations();
            SessionBinder.Formatter.WriteMessage("Annotations saved.");
            SessionBinder.Formatter.WriteResult(new { saved = true });
        });

        var undoCmd = new Command("undo", "Undo last annotation action");
        undoCmd.SetAction(pr =>
        {
            var fmt = SessionBinder.Formatter;
            var doc = SessionBinder.Session.RequireActiveDocument();
            if (doc.UndoStack.Count == 0) { fmt.WriteError("Nothing to undo"); return; }
            doc.Undo();
            fmt.WriteMessage("Undone.");
            fmt.WriteResult(new { undone = true });
        });

        var redoCmd = new Command("redo", "Redo last undone annotation action");
        redoCmd.SetAction(pr =>
        {
            var fmt = SessionBinder.Formatter;
            var doc = SessionBinder.Session.RequireActiveDocument();
            if (doc.RedoStack.Count == 0) { fmt.WriteError("Nothing to redo"); return; }
            doc.Redo();
            fmt.WriteMessage("Redone.");
            fmt.WriteResult(new { redone = true });
        });

        cmd.Add(listCmd); cmd.Add(addHighlightCmd); cmd.Add(addNoteCmd);
        cmd.Add(removeCmd); cmd.Add(exportPdfCmd); cmd.Add(saveCmd);
        cmd.Add(undoCmd); cmd.Add(redoCmd);
        return cmd;
    }

    private static string Describe(Annotation a) => a switch
    {
        HighlightAnnotation h => $"color={h.Color} rects={h.Rects.Count}",
        TextNoteAnnotation n => $"\"{(n.Text.Length > 30 ? n.Text[..27] + "..." : n.Text)}\" at ({n.X:F0},{n.Y:F0})",
        FreehandAnnotation p => $"color={p.Color} points={p.Points.Count}",
        RectAnnotation r => $"({r.X:F0},{r.Y:F0},{r.W:F0},{r.H:F0}) color={r.Color}",
        _ => a.GetType().Name,
    };
}
