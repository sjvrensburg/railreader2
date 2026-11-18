using System.Text;
using System.Text.Json;
using RailReader.Core;

namespace RailReader.Cli.Mcp;

/// <summary>A minimal Model Context Protocol server over stdio: newline-delimited JSON-RPC 2.0, tools only.
/// Hand-written (no SDK) so the CLI stays NativeAOT-clean: every message is read with <see cref="JsonDocument"/>
/// and written with <see cref="Utf8JsonWriter"/>, no reflection serialisation.
/// <para>stdout carries protocol messages only; anything else (logs) goes to stderr.</para></summary>
public sealed class McpServer(LibraryTools tools, string version, ILogger? log = null)
{
    /// <summary>Protocol revisions this server speaks. A client asking for one of these gets it back;
    /// otherwise the server answers with the newest, as the spec asks.</summary>
    public static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    const string Instructions =
        "Read-only access to the reader's RailReader2 library: their notes (highlights, comments, replies, bookmarks) " +
        "and the page text of PDFs they have opened. Use search_notes for what the reader noted, search_pages to quote a " +
        "document, list_documents to see what is there, get_document_notes for all notes in one document. Pages are 1-based. " +
        "Documents are identified by title, file name and a short id; file paths are never given.";

    static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serves requests from <paramref name="input"/> until it ends. Returns the process exit code.</summary>
    public int Run(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? reply = Handle(line);
            if (reply is null) continue;
            output.Write(reply);
            output.Write('\n');
            output.Flush();
        }
        return 0;
    }

    /// <summary>Handles one message; returns the reply line, or null for a notification (no reply).</summary>
    public string? Handle(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }

        using (doc)
        {
            var msg = doc.RootElement;
            if (msg.ValueKind != JsonValueKind.Object)
                return Error(null, -32600, "Invalid request: expected a JSON object");
            msg.TryGetProperty("id", out var idEl);
            JsonElement? id = idEl.ValueKind is JsonValueKind.String or JsonValueKind.Number ? idEl.Clone() : null;

            if (!msg.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
                return null; // a response (we send no requests) or junk: nothing to answer
            string method = m.GetString()!;
            msg.TryGetProperty("params", out var prms);

            // Notifications (no id) never get a reply, whatever the method.
            if (id is null) return null;

            try
            {
                return method switch
                {
                    "initialize" => Result(id, w => Initialize(w, prms)),
                    "ping" => Result(id, w => { w.WriteStartObject(); w.WriteEndObject(); }),
                    "tools/list" => Result(id, ListTools),
                    "tools/call" => CallTool(id.Value, prms),
                    _ => Error(id, -32601, $"Method not found: {method}"),
                };
            }
            catch (Exception ex)
            {
                log?.Error($"[mcp] {method} failed", ex);
                return Error(id, -32603, "Internal error");
            }
        }
    }

    void Initialize(Utf8JsonWriter w, JsonElement prms)
    {
        string requested = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("protocolVersion", out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        string chosen = ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[0];
        w.WriteStartObject();
        w.WriteString("protocolVersion", chosen);
        w.WritePropertyName("capabilities");
        w.WriteStartObject();
        w.WritePropertyName("tools");
        w.WriteStartObject();
        w.WriteBoolean("listChanged", false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WritePropertyName("serverInfo");
        w.WriteStartObject();
        w.WriteString("name", "railreader2");
        w.WriteString("title", "RailReader2 library");
        w.WriteString("version", version);
        w.WriteEndObject();
        w.WriteString("instructions", Instructions);
        w.WriteEndObject();
    }

    static void ListTools(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");
        foreach (var t in LibraryTools.Specs)
        {
            w.WriteStartObject();
            w.WriteString("name", t.Name);
            w.WriteString("title", t.Title);
            w.WriteString("description", t.Description);
            w.WritePropertyName("inputSchema");
            using (var schema = JsonDocument.Parse(t.InputSchema)) schema.RootElement.WriteTo(w);
            w.WritePropertyName("annotations");
            w.WriteStartObject();
            w.WriteBoolean("readOnlyHint", true);
            w.WriteBoolean("destructiveHint", false);
            w.WriteBoolean("idempotentHint", true);
            w.WriteBoolean("openWorldHint", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    string CallTool(JsonElement id, JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object || !prms.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            return Error(id, -32602, "Invalid params: tools/call needs a tool name");
        string name = n.GetString()!;
        if (!LibraryTools.Specs.Any(s => s.Name == name))
            return Error(id, -32602, $"Unknown tool: {name}");
        prms.TryGetProperty("arguments", out var args);

        string text;
        bool isError = false;
        try { text = tools.Call(name, args); }
        catch (LibraryTools.ToolException ex) { text = ex.Message; isError = true; }
        catch (Exception ex)
        {
            // An unreadable index, a locked database: report it to the agent as a failed call, not a protocol error.
            log?.Error($"[mcp] {name} failed", ex);
            text = $"The library could not be read: {ex.Message}";
            isError = true;
        }

        return Result(id, w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("isError", isError);
            w.WriteEndObject();
        });
    }

    static string Result(JsonElement? id, Action<Utf8JsonWriter> writeResult) => Message(id, w =>
    {
        w.WritePropertyName("result");
        writeResult(w);
    });

    static string Error(JsonElement? id, int code, string message) => Message(id, w =>
    {
        w.WritePropertyName("error");
        w.WriteStartObject();
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    static string Message(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is { } i) i.WriteTo(w); else w.WriteNullValue();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Logs to stderr only: stdout belongs to the protocol.</summary>
sealed class StderrLogger : ILogger
{
    public void Debug(string message) { }
    public void Info(string message) => Console.Error.WriteLine(message);
    public void Warn(string message) => Console.Error.WriteLine($"Warning: {message}");
    public void Error(string message, Exception? ex = null)
        => Console.Error.WriteLine(ex is null ? $"Error: {message}" : $"Error: {message}: {ex.Message}");
}
