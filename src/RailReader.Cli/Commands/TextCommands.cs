using System.CommandLine;
using RailReader.Cli.Output;

namespace RailReader.Cli.Commands;

public static class TextCommands
{
    public static Command Create()
    {
        var cmd = new Command("text", "Extract text and search PDF content");

        var extractCmd = new Command("extract", "Extract text from pages");
        var pageOpt = new Option<int?>("--page") { Description = "Page number (1-based)" };
        var fromOpt = new Option<int?>("--from") { Description = "Start page (1-based, inclusive)" };
        var toOpt = new Option<int?>("--to") { Description = "End page (1-based, inclusive)" };
        extractCmd.Add(pageOpt);
        extractCmd.Add(fromOpt);
        extractCmd.Add(toOpt);
        extractCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var doc = session.RequireActiveDocument();
            var page = pr.GetValue(pageOpt);
            var from = pr.GetValue(fromOpt);
            var to = pr.GetValue(toOpt);

            if (page.HasValue)
            {
                int p = session.ValidatePage(doc, page.Value);
                var text = session.Controller.GetPageText(p);
                if (text is null) { fmt.WriteError("Could not extract text"); return; }
                if (fmt is JsonFormatter) fmt.WriteResult(new { page = page.Value, text = text.Text });
                else Console.WriteLine(text.Text);
                return;
            }

            int startPage = from.HasValue ? session.ValidatePage(doc, from.Value) : 0;
            int endPage = to.HasValue ? session.ValidatePage(doc, to.Value) : doc.PageCount - 1;

            if (fmt is JsonFormatter)
            {
                var pages = new List<object>();
                for (int p = startPage; p <= endPage; p++)
                {
                    var text = session.Controller.GetPageText(p);
                    pages.Add(new { page = p + 1, text = text?.Text ?? "" });
                }
                fmt.WriteResult(new { pages });
                return;
            }

            for (int p = startPage; p <= endPage; p++)
            {
                var text = session.Controller.GetPageText(p);
                if (endPage > startPage) Console.WriteLine($"--- Page {p + 1} ---");
                Console.WriteLine(text?.Text ?? "");
                if (p < endPage) Console.WriteLine();
            }
        });

        var searchCmd = new Command("search", "Search for text across all pages");
        var queryArg = new Argument<string>("query") { Description = "Search query" };
        var caseOpt = new Option<bool>("--case-sensitive") { Description = "Case-sensitive search" };
        var regexOpt = new Option<bool>("--regex") { Description = "Use regular expression" };
        searchCmd.Add(queryArg);
        searchCmd.Add(caseOpt);
        searchCmd.Add(regexOpt);
        searchCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fmt = SessionBinder.Formatter;
            var query = pr.GetValue(queryArg);
            var caseSensitive = pr.GetValue(caseOpt);
            var regex = pr.GetValue(regexOpt);
            session.RequireActiveDocument();
            session.Controller.ExecuteSearch(query, caseSensitive, regex);
            var result = session.Controller.GetSearchState();

            if (fmt is JsonFormatter)
            {
                var snippets = session.Controller.SearchMatches
                    .Select((m, i) =>
                    {
                        var (pre, match, post) = session.Controller.GetMatchSnippet(m);
                        return new { index = i, page = m.PageIndex + 1, pre, match, post };
                    });
                fmt.WriteResult(new
                {
                    query,
                    total_matches = result.TotalMatches,
                    matches_per_page = result.MatchesPerPage.ToDictionary(kv => kv.Key + 1, kv => kv.Value),
                    snippets,
                });
                return;
            }

            if (result.TotalMatches == 0) { fmt.WriteMessage($"No matches for \"{query}\""); return; }

            fmt.WriteMessage($"Found {result.TotalMatches} matches across {result.MatchesPerPage.Count} pages\n");
            var headers = new[] { "#", "Page", "Context" };
            var rows = session.Controller.SearchMatches.Select((m, i) =>
            {
                var (pre, match, post) = session.Controller.GetMatchSnippet(m, 30);
                var context = $"{pre}**{match}**{post}";
                if (context.Length > 70) context = context[..67] + "...";
                return new object[] { i + 1, m.PageIndex + 1, context };
            });
            HumanFormatter.WriteTable(rows, headers);
        });

        cmd.Add(extractCmd);
        cmd.Add(searchCmd);
        return cmd;
    }
}
