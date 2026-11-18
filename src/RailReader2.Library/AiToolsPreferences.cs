using System.Text.Json.Serialization;
using RailReader.Core.Services;

namespace RailReader2.Library;

/// <summary>What the reader lets AI tools read through <c>railreader2-cli mcp</c> (roadmap M8):
/// <c>ConfigDir/ai_tools_prefs.json</c>, shared by the app (Settings, Library window) and the CLI.
/// <para>Kept beside the index, not in it: the index is dropped and rebuilt (Rebuild, Clear, an unreadable
/// file), and a privacy choice must not vanish with it. A missing or unreadable file reads as "off", so
/// the server fails closed.</para>
/// <para>Read fresh for every tool call (the server never caches it), so turning tools off or hiding a
/// document applies to an agent that is already connected. Writers load, change and save in one go
/// (<see cref="Update"/>), so the app and the CLI don't overwrite each other's edits.</para></summary>
public sealed class AiToolsPreferences
{
    /// <summary>The reader's consent: off until they turn it on. While off, every tool call is refused.</summary>
    public bool Enabled { get; set; }

    /// <summary>Content ids of documents no tool may mention.</summary>
    public List<string> HiddenDocuments { get; set; } = [];

    /// <summary>Folders whose documents (at any depth, by the path the index last saw) no tool may mention.</summary>
    public List<string> HiddenFolders { get; set; } = [];

    public static string DefaultPath => Path.Combine(AppConfig.ConfigDir, "ai_tools_prefs.json");

    public static AiToolsPreferences Load(string? path = null)
        => JsonSidecar.Load(path ?? DefaultPath, AiToolsPreferencesJsonContext.Default.AiToolsPreferences,
            static () => new AiToolsPreferences());

    public void Save(string? path = null)
        => JsonSidecar.Save(path ?? DefaultPath, this, AiToolsPreferencesJsonContext.Default.AiToolsPreferences);

    /// <summary>Loads the current file, applies <paramref name="change"/>, saves, and returns the result.
    /// Refuses (throws <see cref="IOException"/>) when the file exists but can't be read: saving over it would
    /// silently drop the reader's hidden documents and folders.</summary>
    public static AiToolsPreferences Update(Action<AiToolsPreferences> change, string? path = null)
    {
        string file = path ?? DefaultPath;
        if (!JsonSidecar.TryLoad(file, AiToolsPreferencesJsonContext.Default.AiToolsPreferences,
                static () => new AiToolsPreferences(), out var prefs))
            throw new IOException($"{file} can't be read, so it was left alone (it may list hidden documents). "
                + "Fix or delete it, then try again.");
        change(prefs);
        prefs.Save(path);
        return prefs;
    }

    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when a document is hidden from AI tools, by its id or because its file is in a hidden folder.</summary>
    public bool IsHidden(string contentId, string? lastPath)
    {
        if (HiddenDocuments.Contains(contentId, StringComparer.Ordinal)) return true;
        if (lastPath is not { Length: > 0 }) return false;
        string file;
        try { file = Path.GetFullPath(lastPath); } catch (Exception) { return false; }
        foreach (var folder in HiddenFolders)
        {
            string dir;
            try { dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); } catch (Exception) { continue; }
            if (dir.Length == 0) continue;
            if (file.Length > dir.Length && file.StartsWith(dir, PathComparison)
                && (file[dir.Length] == Path.DirectorySeparatorChar || file[dir.Length] == Path.AltDirectorySeparatorChar
                    || dir.EndsWith(Path.DirectorySeparatorChar))) // a root such as "/" or "C:\"
                return true;
        }
        return false;
    }

    public void Hide(string contentId)
    {
        if (!HiddenDocuments.Contains(contentId, StringComparer.Ordinal)) HiddenDocuments.Add(contentId);
    }

    public void Unhide(string contentId) => HiddenDocuments.RemoveAll(id => id == contentId);

    public void HideFolder(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!HiddenFolders.Any(f => string.Equals(f, full, PathComparison))) HiddenFolders.Add(full);
    }

    public bool UnhideFolder(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return HiddenFolders.RemoveAll(f => string.Equals(Path.TrimEndingDirectorySeparator(f), full, PathComparison)) > 0;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(AiToolsPreferences))]
internal partial class AiToolsPreferencesJsonContext : JsonSerializerContext;
