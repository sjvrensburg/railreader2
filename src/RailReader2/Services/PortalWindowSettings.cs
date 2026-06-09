using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>
/// Last bounds + always-on-top state of the detached portal pop-out window. App-level (not
/// per-document), shell-managed — Core's <see cref="AppConfig"/> is a NuGet type we don't extend, so
/// this lives in its own small sidecar (<c>ConfigDir/portal_window.json</c>), mirroring
/// <see cref="CustomLayoutModelConfig"/>. Reapplied on the next pop-out; the window is never
/// auto-reopened on launch.
/// </summary>
public sealed class PortalWindowSettings
{
    public const double DefaultWidth = 420;
    public const double DefaultHeight = 320;

    // int.MinValue is the "no saved position" sentinel → centre on first pop-out.
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
    public bool Topmost { get; set; } = true;

    // Treat (0,0) as "unset" as well as the int.MinValue sentinel: some Linux/Wayland compositors
    // report Window.Position as (0,0) regardless of the real location, which would otherwise pin the
    // window to the top-left corner on every reopen. Falling back to centring is the safer default.
    [JsonIgnore]
    public bool HasPosition => X != int.MinValue && Y != int.MinValue && (X != 0 || Y != 0);

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "portal_window.json");

    public static PortalWindowSettings Load()
    {
        var s = JsonSidecar.Load(Path, PortalWindowJsonContext.Default.PortalWindowSettings,
            static () => new PortalWindowSettings());
        // Guard a corrupt/hand-edited sidecar: a non-positive size would make the pop-out zero-sized.
        // (Save always persists a positive size, so this only bites on manual tampering.)
        if (s.Width <= 0) s.Width = DefaultWidth;
        if (s.Height <= 0) s.Height = DefaultHeight;
        return s;
    }

    public void Save()
        => JsonSidecar.Save(Path, this, PortalWindowJsonContext.Default.PortalWindowSettings);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(PortalWindowSettings))]
internal partial class PortalWindowJsonContext : JsonSerializerContext;
