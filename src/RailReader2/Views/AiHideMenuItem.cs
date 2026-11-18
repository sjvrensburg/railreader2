using Avalonia.Controls;
using RailReader.Core;
using RailReader2.Library;

namespace RailReader2.Views;

/// <summary>"Hide from AI Tools", shared by the Library window's row menu and the welcome screen's recent-document
/// cards (roadmap M8). Preferences are read fresh and written load-change-save (<see cref="AiToolsPreferences.Update"/>),
/// since the CLI and Settings write the same file.</summary>
internal static class AiHideMenuItem
{
    public static string Header(bool byFolder) => byFolder ? "Hidden from AI Tools by a Folder Rule" : "Hide from AI Tools";

    public static string Tip(bool byFolder) => byFolder
        ? "Its folder is hidden in Settings ▸ Library ▸ AI Tools."
        : "Connected AI tools never see this document's text or notes.";

    /// <summary>A menu item for a menu built in code (the Library window's).</summary>
    /// <param name="path">The document's last known path, for folder rules.</param>
    /// <param name="report">Told the outcome, as a sentence for a status line or toast.</param>
    /// <param name="changed">Called after a successful change, to refresh the caller's view.</param>
    public static MenuItem Create(string contentId, string? path, string displayName, Action<string> report, Action? changed = null)
    {
        var prefs = AiToolsPreferences.Load();
        bool byId = prefs.HiddenDocuments.Contains(contentId);
        bool byFolder = !byId && prefs.IsHidden(contentId, path);
        var item = new MenuItem
        {
            Header = Header(byFolder),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = byId || byFolder,
            IsEnabled = !byFolder, // a folder rule is lifted in Settings ▸ Library, not per document
        };
        ToolTip.SetTip(item, Tip(byFolder));
        item.Click += (_, _) => Toggle(contentId, displayName, report, changed);
        return item;
    }

    /// <summary>Hides the document if it isn't hidden by its own id, else shows it again, going by the preferences
    /// as they are now rather than as a menu showed them.</summary>
    public static void Toggle(string contentId, string displayName, Action<string> report, Action? changed = null)
    {
        bool hide = false;
        try
        {
            AiToolsPreferences.Update(p =>
            {
                hide = !p.HiddenDocuments.Contains(contentId);
                if (hide) p.Hide(contentId); else p.Unhide(contentId);
            });
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[AI tools] saving preferences failed", ex);
            report("Couldn't save that change — see the log for details.");
            return;
        }
        report(hide
            ? $"{displayName} is hidden from AI tools: they can't read its text or notes."
            : $"{displayName} is visible to AI tools again (while AI tools are on).");
        changed?.Invoke();
    }
}
