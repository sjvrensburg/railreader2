using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader2.Services;

/// <summary>What a click on a link in the page does.</summary>
public enum LinkClickAction
{
    /// <summary>A plain click follows the link (the default).</summary>
    Click,
    /// <summary>Only Ctrl+click follows it; a plain click acts as if no link were there.</summary>
    CtrlClick,
    /// <summary>Links are never followed from the page.</summary>
    Off,
}

/// <summary>
/// App-level link preferences. Links are easy to hit by accident while reading (a click also seats the
/// rail), so the reader can make following one need Ctrl, or turn it off. Shell-managed sidecar
/// (<c>ConfigDir/link_prefs.json</c>) like <see cref="PortalPreferences"/>, since Core's
/// <see cref="AppConfig"/> is a NuGet type we don't extend.
/// </summary>
public sealed class LinkPreferences
{
    public LinkClickAction LinkClick { get; set; } = LinkClickAction.Click;

    /// <summary>Whether a click with the given modifier state follows a link under it.</summary>
    public bool Follows(bool ctrl) => LinkClick switch
    {
        LinkClickAction.Click => true,
        LinkClickAction.CtrlClick => ctrl,
        _ => false,
    };

    public static string Path => System.IO.Path.Combine(AppConfig.ConfigDir, "link_prefs.json");

    public static LinkPreferences Load()
        => JsonSidecar.Load(Path, LinkPreferencesJsonContext.Default.LinkPreferences,
            static () => new LinkPreferences());

    public void Save()
        => JsonSidecar.Save(Path, this, LinkPreferencesJsonContext.Default.LinkPreferences);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(LinkPreferences))]
internal partial class LinkPreferencesJsonContext : JsonSerializerContext;
