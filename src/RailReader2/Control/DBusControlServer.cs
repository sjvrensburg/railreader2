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

    private static readonly string[] PropertyNames =
    [
        "DocumentPath", "PageCount", "CurrentPage", "Zoom",
        "IsAnimating", "CurrentBlockIndex", "CurrentRole",
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
                // Not a 'using' var: MessageWriter is a struct passed by ref to the writer helper,
                // which a using-variable forbids — dispose explicitly instead.
                var w = context.CreateReplyWriter("v");
                try
                {
                    if (!WritePropertyVariant(ref w, prop))
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", prop);
                        return;
                    }
                    context.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                break;
            }
            case "GetAll":
            {
                var w = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var name in PropertyNames)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(name);
                        WritePropertyVariant(ref w, name);
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

    private bool WritePropertyVariant(ref MessageWriter w, string prop)
    {
        switch (prop)
        {
            case "DocumentPath": w.WriteVariantString(_control.DocumentPath); return true;
            case "PageCount": w.WriteVariantInt32(_control.PageCount); return true;
            case "CurrentPage": w.WriteVariantInt32(_control.CurrentPage); return true;
            case "Zoom": w.WriteVariantDouble(_control.Zoom); return true;
            case "IsAnimating": w.WriteVariantBool(_control.IsAnimating); return true;
            case "CurrentBlockIndex": w.WriteVariantInt32(_control.CurrentBlockIndex); return true;
            case "CurrentRole": w.WriteVariantString(_control.CurrentRole); return true;
            default: return false;
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

    private const string IntrospectXml = """
        <interface name="org.railreader.Control1">
          <method name="OpenDocument">
            <arg type="s" name="path" direction="in"/>
            <arg type="b" name="ok" direction="out"/>
          </method>
          <method name="GoToPage">
            <arg type="i" name="page" direction="in"/>
          </method>
          <method name="FitPage"/>
          <method name="FitWidth"/>
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
          <property name="DocumentPath" type="s" access="read"/>
          <property name="PageCount" type="i" access="read"/>
          <property name="CurrentPage" type="i" access="read"/>
          <property name="Zoom" type="d" access="read"/>
          <property name="IsAnimating" type="b" access="read"/>
          <property name="CurrentBlockIndex" type="i" access="read"/>
          <property name="CurrentRole" type="s" access="read"/>
          <signal name="Settled"/>
          <signal name="PageChanged">
            <arg type="i" name="page"/>
          </signal>
          <signal name="DocumentOpened">
            <arg type="s" name="path"/>
          </signal>
        </interface>
        """;

    private static readonly ReadOnlyMemory<byte> IntrospectXmlUtf8 =
        System.Text.Encoding.UTF8.GetBytes(IntrospectXml);
}
