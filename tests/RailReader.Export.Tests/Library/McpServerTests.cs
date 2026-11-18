using System.Text.Json;
using Microsoft.Data.Sqlite;
using RailReader2.Library.Mcp;
using RailReader2.Library;
using Xunit;

namespace RailReader.Export.Tests.Library;

/// <summary>The <c>mcp</c> server (roadmap M8), driven over its stdio framing: newline-delimited JSON-RPC in,
/// one reply line per request out. Checks the protocol and, above all, what the tools may never return.</summary>
public sealed class McpServerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rr2_mcp_").FullName;
    private string Db => Path.Combine(_dir, "library.db");

    const string Paper = "aaaaaaaaaaaaaaaa1111", Secret = "bbbbbbbbbbbbbbbb2222", Other = "cccccccccccccccc3333";

    public McpServerTests()
    {
        using var index = LibraryIndex.OpenWritable(Db, out _);
        index.ReplaceDocument(new DocumentContent(Paper, "/home/someone/private-folder/card-krueger-1994.pdf", 1, 1, 3, false,
            [new PageEntry(0, "Minimum wages and employment in fast food restaurants", false),
             new PageEntry(2, "The identification strategy compares New Jersey with Pennsylvania", false)],
            [],
            [new NoteEntry(2, "highlight", "compares New Jersey with Pennsylvania", "a clean identification argument", "Reader", "#FFFF00", "n1", NoteSource.RailReader),
             new NoteEntry(1, "textnote", null, "identification relies on parallel trends", "Referee 2", "#FF0000", "n2", NoteSource.InPdf),
             new NoteEntry(0, "bookmark", null, "identification section", null, null, null, NoteSource.RailReader)],
            Info: new DocumentInfo("Minimum Wages and Employment", "Card; Krueger", null, null)),
            categoryOf: c => c == "#FFFF00" ? "Definition" : null);
        // An encrypted document: the index keeps only its name, but even a note row (defence in depth) must
        // never come back from any tool.
        index.ReplaceDocument(new DocumentContent(Secret, "/home/someone/identification-secret.pdf", 1, 1, 5, true,
            [], [],
            [new NoteEntry(0, "textnote", null, "identification secret note", "Reader", null, "s1", NoteSource.RailReader)],
            State: DocumentState.Locked));
        index.ReplaceDocument(new DocumentContent(Other, "/papers/other.pdf", 1, 1, 1, false,
            [new PageEntry(0, "unrelated identification text in another paper", false)], [], []));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AiToolsPreferences _prefs = new() { Enabled = true };

    private McpServer Server() => new(new LibraryTools(() => LibraryIndex.OpenReadOnly(Db), () => "Reader", () => _prefs), "1.2.3");

    /// <summary>Sends <paramref name="requests"/> through <see cref="McpServer.Run"/> and returns the reply lines.</summary>
    private List<JsonElement> Exchange(params string[] requests)
    {
        var output = new StringWriter();
        Assert.Equal(0, Server().Run(new StringReader(string.Join("\n", requests) + "\n"), output));
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static string Call(int id, string tool, string args)
        => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool
           + "\",\"arguments\":" + args + "}}";

    /// <summary>A tool call's text content, parsed, and its isError flag.</summary>
    private (JsonElement Body, bool IsError, string Text) Tool(string tool, string args)
    {
        var reply = Assert.Single(Exchange(Call(1, tool, args)));
        var result = reply.GetProperty("result");
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        bool isError = result.GetProperty("isError").GetBoolean();
        return (isError ? default : JsonDocument.Parse(text).RootElement.Clone(), isError, text);
    }

    [Fact]
    public void Handshake_NegotiatesVersion_ListsReadOnlyTools_AndIgnoresNotifications()
    {
        var replies = Exchange(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":"two","method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":4,"method":"resources/list"}""",
            "not json");
        Assert.Equal(5, replies.Count); // the notification gets no reply

        var init = replies[0].GetProperty("result");
        Assert.Equal("2025-06-18", init.GetProperty("protocolVersion").GetString());
        Assert.Equal("railreader2", init.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(init.GetProperty("capabilities").TryGetProperty("tools", out _));

        Assert.Equal("two", replies[1].GetProperty("id").GetString());
        var tools = replies[1].GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(["search_notes", "search_pages", "list_documents", "get_document_notes"],
            tools.Select(t => t.GetProperty("name").GetString()));
        Assert.All(tools, t =>
        {
            Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        });

        Assert.Equal(JsonValueKind.Object, replies[2].GetProperty("result").ValueKind);
        Assert.Equal(-32601, replies[3].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32700, replies[4].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void Initialize_UnknownVersion_AnswersWithTheNewest()
    {
        var reply = Assert.Single(Exchange("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}"""));
        Assert.Equal(McpServer.ProtocolVersions[0], reply.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void SearchNotes_LabelsOwnerAndCategory_AndUsesOneBasedPages()
    {
        var (body, isError, _) = Tool("search_notes", """{"query":"identification"}""");
        Assert.False(isError);
        var hits = body.GetProperty("hits").EnumerateArray().ToList();
        Assert.Equal(3, hits.Count); // highlight comment, reviewer note, bookmark — never the encrypted note
        Assert.All(hits, h => Assert.Equal(LibraryTools.ShortId(Paper), h.GetProperty("document_id").GetString()));

        var highlight = hits.Single(h => h.GetProperty("kind").GetString() == "highlight");
        Assert.Equal(3, highlight.GetProperty("page").GetInt32());
        Assert.Equal("reader", highlight.GetProperty("owner").GetString());
        Assert.Equal("Definition", highlight.GetProperty("category").GetString());
        Assert.Contains("**identification**", highlight.GetProperty("snippet").GetString());
        Assert.Equal("Minimum Wages and Employment", highlight.GetProperty("title").GetString());

        var reviewer = hits.Single(h => h.GetProperty("kind").GetString() == "textnote");
        Assert.Equal("reviewer", reviewer.GetProperty("owner").GetString());
        Assert.Equal("Referee 2", reviewer.GetProperty("author").GetString());
    }

    [Fact]
    public void SearchNotes_FiltersByKind()
    {
        var (body, _, _) = Tool("search_notes", """{"query":"identification","kind":"bookmark"}""");
        var hit = Assert.Single(body.GetProperty("hits").EnumerateArray());
        Assert.Equal("bookmark", hit.GetProperty("kind").GetString());
        Assert.False(hit.TryGetProperty("owner", out _));
    }

    [Fact]
    public void SearchPages_CanBeLimitedToOneDocument_ByTitleOrId()
    {
        var (all, _, _) = Tool("search_pages", """{"query":"identification"}""");
        Assert.Equal(2, all.GetProperty("hits").GetArrayLength());

        var (byTitle, _, _) = Tool("search_pages", """{"query":"identification","document":"minimum wages and employment"}""");
        var hit = Assert.Single(byTitle.GetProperty("hits").EnumerateArray());
        Assert.Equal(3, hit.GetProperty("page").GetInt32());

        var (byId, _, _) = Tool("search_pages", $$"""{"query":"identification","document":"{{Other[..8]}}"}""");
        Assert.Equal("other.pdf", Assert.Single(byId.GetProperty("hits").EnumerateArray()).GetProperty("file_name").GetString());
    }

    [Fact]
    public void ListDocuments_LeavesOutEncryptedDocuments()
    {
        var (body, _, _) = Tool("list_documents", "{}");
        var docs = body.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(2, docs.Count);
        var paper = docs.Single(d => d.GetProperty("document_id").GetString() == LibraryTools.ShortId(Paper));
        Assert.Equal(3, paper.GetProperty("notes").GetInt32());
        Assert.Equal("Card; Krueger", paper.GetProperty("authors").GetString());
    }

    [Fact]
    public void GetDocumentNotes_ReturnsNotesInPageOrder()
    {
        var (body, isError, _) = Tool("get_document_notes", """{"document":"card-krueger-1994"}""");
        Assert.False(isError);
        Assert.Equal(LibraryTools.ShortId(Paper), body.GetProperty("document").GetProperty("id").GetString());
        var notes = body.GetProperty("notes").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], notes.Select(n => n.GetProperty("page").GetInt32()));
        Assert.Equal("identification section", notes[0].GetProperty("name").GetString());
        Assert.Equal("compares New Jersey with Pennsylvania", notes[2].GetProperty("marked_text").GetString());
        Assert.Equal("a clean identification argument", notes[2].GetProperty("comment").GetString());
    }

    [Fact]
    public void NoTool_EverReturnsAPathOrAnEncryptedDocument()
    {
        string[] calls =
        [
            Call(1, "search_notes", """{"query":"identification secret"}"""),
            Call(2, "search_notes", """{"query":"identification"}"""),
            Call(3, "search_pages", """{"query":"identification"}"""),
            Call(4, "list_documents", "{}"),
            Call(5, "get_document_notes", $$"""{"document":"{{Paper}}"}"""),
            Call(6, "get_document_notes", $$"""{"document":"{{Secret}}"}"""),
            Call(7, "get_document_notes", """{"document":"identification-secret.pdf"}"""),
        ];
        string all = string.Join("\n", Exchange(calls).Select(r => r.GetRawText()));
        Assert.DoesNotContain("/home/someone", all);
        Assert.DoesNotContain("private-folder", all);
        Assert.DoesNotContain("/papers/", all);
        // The agent's own words come back in "query" and in error messages; the document's never do.
        Assert.DoesNotContain("secret note", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"file_name\":\"identification-secret.pdf\"", all);
        Assert.Contains($"id\\\":\\\"{LibraryTools.ShortId(Paper)}", all); // the pattern does match a returned id…
        Assert.DoesNotContain($"id\\\":\\\"{LibraryTools.ShortId(Secret)}", all); // …and never the encrypted one
    }

    [Fact]
    public void BadCalls_AreToolErrors_NotCrashes()
    {
        Assert.True(Tool("search_notes", """{"query":"   "}""").IsError);
        Assert.True(Tool("search_notes", """{}""").IsError);
        Assert.True(Tool("search_notes", """{"query":"x","limit":0}""").IsError);
        var (_, isError, text) = Tool("get_document_notes", """{"document":"no such paper"}""");
        Assert.True(isError);
        Assert.Contains("list_documents", text);

        var unknown = Assert.Single(Exchange(Call(9, "delete_everything", "{}")));
        Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void NoIndex_IsAToolErrorTellingTheReaderWhatToDo()
    {
        var server = new McpServer(new LibraryTools(() => null, () => null, () => new AiToolsPreferences { Enabled = true }), "1");
        var output = new StringWriter();
        server.Run(new StringReader(Call(1, "list_documents", "{}") + "\n"), output);
        var result = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Open some documents", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void TurnedOff_RefusesEveryTool_ButStillAnswersTheHandshake()
    {
        _prefs = new AiToolsPreferences(); // the default: off
        var replies = Exchange(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            Call(3, "list_documents", "{}"),
            Call(4, "search_notes", """{"query":"identification"}"""));
        Assert.True(replies[0].TryGetProperty("result", out _));
        Assert.Equal(4, replies[1].GetProperty("result").GetProperty("tools").GetArrayLength());
        foreach (var r in replies.Skip(2))
        {
            var result = r.GetProperty("result");
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Equal(LibraryTools.TurnedOffMessage, result.GetProperty("content")[0].GetProperty("text").GetString());
        }
    }

    [Fact]
    public void PrefsThatThrow_FailClosed()
    {
        var server = new McpServer(new LibraryTools(() => LibraryIndex.OpenReadOnly(Db), () => null,
            () => throw new IOException("unreadable")), "1");
        var output = new StringWriter();
        server.Run(new StringReader(Call(1, "list_documents", "{}") + "\n"), output);
        Assert.Contains("turned off", output.ToString());
    }

    [Fact]
    public void HiddenDocument_IsAbsentFromEveryTool_EvenByItsId()
    {
        _prefs.Hide(Paper);
        string all = string.Join("\n", Exchange(
            Call(1, "search_notes", """{"query":"identification"}"""),
            Call(2, "search_pages", """{"query":"identification"}"""),
            Call(3, "list_documents", "{}"),
            Call(4, "get_document_notes", "{\"document\":\"" + Paper + "\"}"),
            Call(5, "get_document_notes", """{"document":"Minimum Wages and Employment"}""")).Select(r => r.GetRawText()));
        Assert.DoesNotContain("New Jersey", all);
        Assert.DoesNotContain("parallel trends", all);
        Assert.DoesNotContain("card-krueger", all);
        Assert.DoesNotContain($"id\\\":\\\"{LibraryTools.ShortId(Paper)}", all);
        Assert.Contains("other.pdf", all); // the rest of the library is still there
    }

    [Fact]
    public void HiddenFolder_HidesDocumentsAtAnyDepth_ButNotASiblingWithTheSamePrefix()
    {
        _prefs.HideFolder("/home/someone");
        var (body, _, _) = Tool("list_documents", "{}");
        Assert.Equal(["other.pdf"], body.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("file_name").GetString()));

        var prefs = new AiToolsPreferences();
        prefs.HideFolder("/papers/private");
        Assert.True(prefs.IsHidden("x", "/papers/private/a/b.pdf"));
        Assert.False(prefs.IsHidden("x", "/papers/private-not/b.pdf"));
        Assert.False(prefs.IsHidden("x", null));
        prefs.HideFolder("/");
        Assert.True(prefs.IsHidden("x", "/anything.pdf"));
    }

    [Fact]
    public void Preferences_RoundTrip_AndUpdateKeepsOtherWritersEdits()
    {
        string path = Path.Combine(_dir, "ai_tools_prefs.json");
        Assert.False(AiToolsPreferences.Load(path).Enabled); // missing file: off
        AiToolsPreferences.Update(p => p.Enabled = true, path);
        AiToolsPreferences.Update(p => p.Hide("abc"), path); // a second writer, loading fresh
        var back = AiToolsPreferences.Load(path);
        Assert.True(back.Enabled);
        Assert.Equal(["abc"], back.HiddenDocuments);
        File.WriteAllText(path, "{ not json");
        Assert.False(AiToolsPreferences.Load(path).Enabled); // unreadable: off
    }
}
