using RailReader.Core;
using Tmds.DBus.Protocol;

namespace RailReader2.ControlBus;

/// <summary>
/// Thin D-Bus (session bus) adapter over <see cref="IRailReaderControl"/>. Contains no app
/// logic: it decodes incoming method calls, forwards them to the control, and broadcasts the
/// control's events as D-Bus signals. Because all behaviour lives behind the interface, this
/// adapter is swappable for another transport (named pipe / gRPC on Windows) without touching
/// the VM, and the app logic is testable without a live bus.
///
/// Contract:
///   Bus name:  org.railreader.Control   (overridable via --control-bus=name)
///   Object:    /org/railreader/Control
///   Interface: org.railreader.Control1
/// </summary>
public sealed class DBusControlServer : IPathMethodHandler, IDisposable
{
    public const string ObjectPath = "/org/railreader/Control";
    public const string InterfaceName = "org.railreader.Control1";
    public const string DefaultBusName = "org.railreader.Control";

    private readonly IRailReaderControl _control;
    private readonly string _busName;
    private readonly ILogger _logger;
    private DBusConnection? _connection;

    public DBusControlServer(IRailReaderControl control, string? busName, ILogger logger)
    {
        _control = control;
        _busName = string.IsNullOrWhiteSpace(busName) ? DefaultBusName : busName!;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        var address = DBusAddress.Session
            ?? throw new InvalidOperationException(
                "No D-Bus session bus address (DBUS_SESSION_BUS_ADDRESS is unset).");

        _connection = new DBusConnection(address);
        await _connection.ConnectAsync().ConfigureAwait(false);
        _connection.AddMethodHandler(this);
        await _connection.RequestNameAsync(_busName, RequestNameOptions.None).ConfigureAwait(false);

        _control.Settled += EmitSettled;
        _control.PageChanged += EmitPageChanged;
        _control.DocumentOpened += EmitDocumentOpened;

        _logger.Info($"[control-bus] Serving {_busName} {ObjectPath} ({InterfaceName})");
    }

    // --- IMethodHandler ---

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    public async ValueTask HandleMethodAsync(MethodContext context)
    {
        var req = context.Request;

        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([IntrospectXmlUtf8]);
            return;
        }
        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context);
            return;
        }
        if (req.InterfaceIsSet && req.InterfaceAsString != InterfaceName)
        {
            context.ReplyUnknownMethodError();
            return;
        }

        try
        {
            switch (req.MemberAsString)
            {
                case "OpenDocument":
                {
                    var reader = req.GetBodyReader();
                    string path = reader.ReadString();
                    bool ok = await _control.OpenDocumentAsync(path).ConfigureAwait(false);
                    ReplyBool(context, ok);
                    break;
                }
                case "GoToPage":
                {
                    var reader = req.GetBodyReader();
                    _control.GoToPage(reader.ReadInt32());
                    ReplyVoid(context);
                    break;
                }
                case "FitPage":
                    _control.FitPage();
                    ReplyVoid(context);
                    break;
                case "FitWidth":
                    _control.FitWidth();
                    ReplyVoid(context);
                    break;
                case "SetFullScreen":
                {
                    var reader = req.GetBodyReader();
                    _control.SetFullScreen(reader.ReadBool());
                    ReplyVoid(context);
                    break;
                }
                case "SetZoom":
                {
                    var reader = req.GetBodyReader();
                    _control.SetZoom(reader.ReadDouble());
                    ReplyVoid(context);
                    break;
                }
                case "SetColourEffect":
                {
                    var reader = req.GetBodyReader();
                    ReplyBool(context, _control.SetColourEffect(reader.ReadString()));
                    break;
                }
                case "NavigateRole":
                {
                    var reader = req.GetBodyReader();
                    string role = reader.ReadString();
                    bool forward = reader.ReadBool();
                    ReplyBool(context, _control.NavigateRole(role, forward));
                    break;
                }
                case "RailAdvanceLine":
                {
                    var reader = req.GetBodyReader();
                    ReplyBool(context, _control.RailAdvanceLine(reader.ReadBool()));
                    break;
                }
                case "SetLineHighlight":
                {
                    var reader = req.GetBodyReader();
                    _control.SetLineHighlight(reader.ReadBool());
                    ReplyVoid(context);
                    break;
                }
                case "SetLineFocusBlur":
                {
                    var reader = req.GetBodyReader();
                    _control.SetLineFocusBlur(reader.ReadBool());
                    ReplyVoid(context);
                    break;
                }
                case "SendKey":
                {
                    var reader = req.GetBodyReader();
                    string chord = reader.ReadString();
                    bool down = reader.ReadBool();
                    bool up = reader.ReadBool();
                    ReplyBool(context, _control.SendKey(chord, down, up));
                    break;
                }
                case "FrameRole":
                {
                    var reader = req.GetBodyReader();
                    string role = reader.ReadString();
                    int occurrence = reader.ReadInt32();
                    double zoom = reader.ReadDouble();
                    ReplyBool(context, _control.FrameRole(role, occurrence, zoom));
                    break;
                }
                case "FrameBlock":
                {
                    var reader = req.GetBodyReader();
                    int index = reader.ReadInt32();
                    double zoom = reader.ReadDouble();
                    ReplyBool(context, _control.FrameBlock(index, zoom));
                    break;
                }
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[control-bus] {req.MemberAsString} failed", ex);
            if (!context.ReplySent)
                context.ReplyError("org.railreader.Control1.Error.Failed", ex.Message);
        }
    }

    // --- org.freedesktop.DBus.Properties ---

    private delegate void VariantWriter(ref MessageWriter w, in ControlSnapshot s);

    /// <summary>One read-only property: its D-Bus name, type signature, and how to write its value
    /// from a <see cref="ControlSnapshot"/>. This single table is the source of truth for the
    /// Properties.Get/GetAll handlers AND the introspection XML, so they can't drift.</summary>
    private sealed record PropertyDef(string Name, string Signature, VariantWriter Write);

    private static readonly PropertyDef[] Properties =
    [
        new("DocumentPath",      "s", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantString(s.DocumentPath)),
        new("PageCount",         "i", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantInt32(s.PageCount)),
        new("CurrentPage",       "i", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantInt32(s.CurrentPage)),
        new("Zoom",              "d", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantDouble(s.Zoom)),
        new("IsAnimating",       "b", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantBool(s.IsAnimating)),
        new("CurrentBlockIndex", "i", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantInt32(s.CurrentBlockIndex)),
        new("CurrentRole",       "s", (ref MessageWriter w, in ControlSnapshot s) => w.WriteVariantString(s.CurrentRole)),
    ];

    private void HandleProperties(MethodContext context)
    {
        var req = context.Request;
        switch (req.MemberAsString)
        {
            case "Get":
            {
                var reader = req.GetBodyReader();
                _ = reader.ReadString();            // interface name (ignored — single interface)
                string prop = reader.ReadString();
                var def = Array.Find(Properties, p => p.Name == prop);
                if (def is null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", prop);
                    break;
                }
                var snap = _control.Snapshot();
                // Not a 'using' var: MessageWriter is a struct passed by ref to the writer
                // delegate, which a using-variable forbids — dispose explicitly instead.
                var w = context.CreateReplyWriter("v");
                try { def.Write(ref w, in snap); context.Reply(w.CreateMessage()); }
                finally { w.Dispose(); }
                break;
            }
            case "GetAll":
            {
                var snap = _control.Snapshot(); // one UI-thread round-trip for all properties
                var w = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var def in Properties)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(def.Name);
                        def.Write(ref w, in snap);
                    }
                    w.WriteDictionaryEnd(dict);
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "Set":
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly",
                    "All control properties are read-only.");
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    // --- Reply helpers ---

    private static void ReplyBool(MethodContext context, bool value)
    {
        using var w = context.CreateReplyWriter("b");
        w.WriteBool(value);
        context.Reply(w.CreateMessage());
    }

    private static void ReplyVoid(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var w = context.CreateReplyWriter("");
        context.Reply(w.CreateMessage());
    }

    // --- Signals (raised on the UI thread by the control; TrySendMessage is thread-safe) ---

    private void EmitSettled()
    {
        var conn = _connection;
        if (conn is null) return;
        using var w = conn.GetMessageWriter();
        w.WriteSignalHeader(null, ObjectPath, InterfaceName, "Settled", null);
        conn.TrySendMessage(w.CreateMessage());
    }

    private void EmitPageChanged(int page)
    {
        var conn = _connection;
        if (conn is null) return;
        using var w = conn.GetMessageWriter();
        w.WriteSignalHeader(null, ObjectPath, InterfaceName, "PageChanged", "i");
        w.WriteInt32(page);
        conn.TrySendMessage(w.CreateMessage());
    }

    private void EmitDocumentOpened(string path)
    {
        var conn = _connection;
        if (conn is null) return;
        using var w = conn.GetMessageWriter();
        w.WriteSignalHeader(null, ObjectPath, InterfaceName, "DocumentOpened", "s");
        w.WriteString(path);
        conn.TrySendMessage(w.CreateMessage());
    }

    public void Dispose()
    {
        _control.Settled -= EmitSettled;
        _control.PageChanged -= EmitPageChanged;
        _control.DocumentOpened -= EmitDocumentOpened;
        _connection?.Dispose();
        _connection = null;
    }

    // Methods + signals are static; the <property> lines are generated from the Properties table
    // above so the introspection XML and the Get/GetAll handlers share one source of truth.
    private const string MethodsAndSignalsXml = """
          <method name="OpenDocument">
            <arg type="s" name="path" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="GoToPage">
            <arg type="i" name="page" direction="in"/>
          </method>
          <method name="FitPage"/>
          <method name="FitWidth"/>
          <method name="SetFullScreen">
            <arg type="b" name="on" direction="in"/>
          </method>
          <method name="SetZoom">
            <arg type="d" name="percent" direction="in"/>
          </method>
          <method name="SetColourEffect">
            <arg type="s" name="name" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="NavigateRole">
            <arg type="s" name="role" direction="in"/>
            <arg type="b" name="forward" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="RailAdvanceLine">
            <arg type="b" name="forward" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="SetLineHighlight">
            <arg type="b" name="on" direction="in"/>
          </method>
          <method name="SetLineFocusBlur">
            <arg type="b" name="on" direction="in"/>
          </method>
          <method name="SendKey">
            <arg type="s" name="chord" direction="in"/>
            <arg type="b" name="down" direction="in"/>
            <arg type="b" name="up" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="FrameRole">
            <arg type="s" name="role" direction="in"/>
            <arg type="i" name="occurrence" direction="in"/>
            <arg type="d" name="zoom" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="FrameBlock">
            <arg type="i" name="pageBlockIndex" direction="in"/>
            <arg type="d" name="zoom" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <signal name="Settled"/>
          <signal name="PageChanged">
            <arg type="i" name="page"/>
          </signal>
          <signal name="DocumentOpened">
            <arg type="s" name="path"/>
          </signal>
        """;

    private static readonly ReadOnlyMemory<byte> IntrospectXmlUtf8 = BuildIntrospectXml();

    private static ReadOnlyMemory<byte> BuildIntrospectXml()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<interface name=\"").Append(InterfaceName).Append("\">\n");
        sb.Append(MethodsAndSignalsXml).Append('\n');
        foreach (var p in Properties)
            sb.Append("  <property name=\"").Append(p.Name)
              .Append("\" type=\"").Append(p.Signature).Append("\" access=\"read\"/>\n");
        sb.Append("</interface>");
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }
}
