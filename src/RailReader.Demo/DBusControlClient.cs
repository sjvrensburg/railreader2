using Tmds.DBus.Protocol;

namespace RailReader.Demo;

/// <summary>
/// <see cref="IControlClient"/> over the GUI's <c>org.railreader.Control1</c> D-Bus surface
/// (the Phase A server). Calls the bus methods and re-raises the <c>Settled</c>/<c>PageChanged</c>/
/// <c>DocumentOpened</c> signals as .NET events. Uses the low-level Tmds.DBus.Protocol client
/// (manual message read/write) so it stays AOT-safe.
/// </summary>
public sealed class DBusControlClient : IControlClient
{
    public const string DefaultBusName = "org.railreader.Control";
    private const string ObjectPath = "/org/railreader/Control";
    private const string Interface = "org.railreader.Control1";

    private readonly DBusConnection _conn;
    private readonly string _busName;

    public event Action? Settled;
    public event Action<int>? PageChanged;
    public event Action<string>? DocumentOpened;

    private DBusControlClient(DBusConnection conn, string busName)
    {
        _conn = conn;
        _busName = busName;
    }

    /// <summary>Connect to the session bus and subscribe to the control signals.</summary>
    public static async Task<DBusControlClient> CreateAsync(string? busName = null)
    {
        var address = DBusAddress.Session
            ?? throw new InvalidOperationException("No D-Bus session bus address (DBUS_SESSION_BUS_ADDRESS is unset).");
        var conn = new DBusConnection(address);
        await conn.ConnectAsync().ConfigureAwait(false);
        var client = new DBusControlClient(conn, busName ?? DefaultBusName);
        await client.SubscribeAsync().ConfigureAwait(false);
        return client;
    }

    private async Task SubscribeAsync()
    {
        await Watch("Settled", static (Message m, object? s) => 0,
            (Exception? e, int _, object? _, object? _) => { if (e is null) Settled?.Invoke(); }).ConfigureAwait(false);
        await Watch("PageChanged", static (Message m, object? s) => m.GetBodyReader().ReadInt32(),
            (Exception? e, int page, object? _, object? _) => { if (e is null) PageChanged?.Invoke(page); }).ConfigureAwait(false);
        await Watch("DocumentOpened", static (Message m, object? s) => m.GetBodyReader().ReadString(),
            (Exception? e, string path, object? _, object? _) => { if (e is null) DocumentOpened?.Invoke(path); }).ConfigureAwait(false);
    }

    private ValueTask<IDisposable> Watch<T>(string member, MessageValueReader<T> reader, Action<Exception?, T, object?, object?> handler)
        => _conn.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = Interface, Path = ObjectPath, Member = member },
            reader, handler, ObserverFlags.None, readerState: null, handlerState: null, emitOnCapturedContext: false);

    // --- verbs ---

    public Task<bool> OpenDocumentAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "OpenDocument", "s", MessageFlags.None);
        w.WriteString(path);
        return _conn.CallMethodAsync(w.CreateMessage(), static (Message m, object? s) => m.GetBodyReader().ReadBool(), null);
    }

    public Task GoToPageAsync(int page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "GoToPage", "i", MessageFlags.None);
        w.WriteInt32(page);
        return _conn.CallMethodAsync(w.CreateMessage());
    }

    public Task FitPageAsync(CancellationToken ct) => CallVoidNoArgsAsync("FitPage", ct);
    public Task FitWidthAsync(CancellationToken ct) => CallVoidNoArgsAsync("FitWidth", ct);

    public Task SetFullScreenAsync(bool on, CancellationToken ct) => CallBoolArgVoidAsync("SetFullScreen", on, ct);
    public Task SetLineHighlightAsync(bool on, CancellationToken ct) => CallBoolArgVoidAsync("SetLineHighlight", on, ct);
    public Task SetLineFocusBlurAsync(bool on, CancellationToken ct) => CallBoolArgVoidAsync("SetLineFocusBlur", on, ct);

    public Task SetZoomAsync(double percent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "SetZoom", "d", MessageFlags.None);
        w.WriteDouble(percent);
        return _conn.CallMethodAsync(w.CreateMessage());
    }

    public Task<bool> SetColourEffectAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "SetColourEffect", "s", MessageFlags.None);
        w.WriteString(name);
        return _conn.CallMethodAsync(w.CreateMessage(), static (Message m, object? s) => m.GetBodyReader().ReadBool(), null);
    }

    public Task<bool> NavigateRoleAsync(string role, bool forward, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "NavigateRole", "sb", MessageFlags.None);
        w.WriteString(role);
        w.WriteBool(forward);
        return _conn.CallMethodAsync(w.CreateMessage(), static (Message m, object? s) => m.GetBodyReader().ReadBool(), null);
    }

    public Task<bool> RailAdvanceLineAsync(bool forward, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "RailAdvanceLine", "b", MessageFlags.None);
        w.WriteBool(forward);
        return _conn.CallMethodAsync(w.CreateMessage(), static (Message m, object? s) => m.GetBodyReader().ReadBool(), null);
    }

    private Task CallBoolArgVoidAsync(string member, bool arg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, member, "b", MessageFlags.None);
        w.WriteBool(arg);
        return _conn.CallMethodAsync(w.CreateMessage());
    }

    private Task CallVoidNoArgsAsync(string member, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, member, "", MessageFlags.None);
        return _conn.CallMethodAsync(w.CreateMessage());
    }

    public Task<bool> FrameRoleAsync(string role, int occurrence, double zoom, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "FrameRole", "sid", MessageFlags.None);
        w.WriteString(role);
        w.WriteInt32(occurrence);
        w.WriteDouble(zoom);
        return _conn.CallMethodAsync(w.CreateMessage(), static (Message m, object? s) => m.GetBodyReader().ReadBool(), null);
    }

    public Task<bool> FrameBlockAsync(int pageBlockIndex, double zoom, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var w = _conn.GetMessageWriter();
        w.WriteMethodCallHeader(_busName, ObjectPath, Interface, "FrameBlock", "id", MessageFlags.None);
        w.WriteInt32(pageBlockIndex);
        w.WriteDouble(zoom);
        return _conn.CallMethodAsync(w.CreateMessage(), static (Message m, object? s) => m.GetBodyReader().ReadBool(), null);
    }

    public ValueTask DisposeAsync()
    {
        _conn.Dispose();
        return default;
    }
}
