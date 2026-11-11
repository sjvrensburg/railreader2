using RailReader2.Services.Updates;
using RailReader2.Views;

namespace RailReader2.ViewModels;

// In-app updates. A background check runs at most once a day, only after the reader has opted in through a
// one-time prompt (the privacy page promises no network requests by default), and never for the Store build
// (Store policy) or a dev build. Help ▸ Check for Updates… works on every channel. Installing in place is
// AppImage (replace own file) and Inno (silent reinstall after exit) only; the tarball and dev builds get the
// release page, the Store build the Store page. Mechanics: src/RailReader2.Shared/Updates/.
public partial class MainWindowViewModel
{
    internal const string StoreProductUri = "ms-windows-store://pdp/?ProductId=9P9J8KZ6RVZP";

    private UpdatePreferences? _updatePrefs;
    internal UpdatePreferences UpdatePrefs => _updatePrefs ??= UpdatePreferences.Load();

    private UpdateDialog? _updateDialog;

    /// <summary>Called once the main window has opened. Asks the one-time question if it is still
    /// unanswered, then checks if a check is due. Runs as one task: no timer.</summary>
    public async Task RunStartupUpdateCheckAsync()
    {
        var kind = UpdateChannel.Current;
        if (!UpdateChannel.SupportsBackgroundCheck(kind)) return;

        // Let startup (document open, first analysis) settle before asking or touching the network.
        await Task.Delay(TimeSpan.FromSeconds(5));

        var prefs = UpdatePrefs;
        if (prefs.CheckAutomatically is null)
        {
            var owner = DialogOwner;
            if (owner is null) return;
            var answer = await new ConfirmDialog("Updates",
                    "Should RailReader2 check for a new version once a day? It asks GitHub for the latest " +
                    "release and sends nothing about you or your documents. You can change this in " +
                    "Settings ▸ Reading, and check by hand any time from Help ▸ Check for Updates.",
                    "Check Automatically", "No Thanks")
                { FontSize = CurrentFontSize }
                .ShowDialog<bool?>(owner);
            if (answer is null) return; // closed without answering: ask again next launch
            prefs.CheckAutomatically = answer;
            prefs.Save();
        }

        if (!prefs.IsCheckDue(DateTime.UtcNow)) return;
        var result = await UpdateFeed.CheckAsync(CancellationToken.None);
        if (result.Status != UpdateCheckStatus.Failed)
        {
            prefs.LastCheckUtc = DateTime.UtcNow;
            prefs.Save();
        }
        if (result is { Status: UpdateCheckStatus.Available, Manifest: { } m } && m.Version != prefs.SkippedVersion)
            ShowStatusToast($"RailReader2 {m.Version} is available.", "Update…", () => OpenUpdateDialog(result));
    }

    /// <summary>Help ▸ Check for Updates…</summary>
    public void CheckForUpdates()
    {
        if (UpdateChannel.Current == InstallKind.Msix)
        {
            OpenUri(StoreProductUri);
            return;
        }
        OpenUpdateDialog(null);
    }

    private void OpenUpdateDialog(UpdateCheckResult? result)
    {
        if (_updateDialog is { } open)
        {
            open.Activate();
            return;
        }
        var dialog = new UpdateDialog(this, UpdateChannel.Current, result) { FontSize = CurrentFontSize };
        _updateDialog = dialog;
        dialog.Closed += (_, _) => _updateDialog = null;
        if (DialogOwner is { } owner) dialog.Show(owner);
        else dialog.Show();
    }

    internal void SkipUpdateVersion(string version)
    {
        UpdatePrefs.SkippedVersion = version;
        UpdatePrefs.Save();
    }

    internal void SetCheckForUpdatesAutomatically(bool on)
    {
        UpdatePrefs.CheckAutomatically = on;
        UpdatePrefs.Save();
    }

    /// <summary>Queues <paramref name="update"/> to run after exit; with <paramref name="restartNow"/>
    /// the app closes now (the normal close path, so annotations and settings are saved first) and the
    /// new version starts with the document that was active.</summary>
    internal void FinishUpdate(PreparedUpdate update, bool restartNow)
    {
        string[] args = restartNow && ActiveTab?.FilePath is { Length: > 0 } path && File.Exists(path) ? [path] : [];
        UpdateRelaunch.SetPending(UpdateInstaller.ExitCommand(update, restartNow, args));
        if (restartNow) _window?.Close();
    }

    internal void OpenUri(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Update] Couldn't open {uri}: {ex.Message}");
            ShowStatusToast($"Couldn't open {uri}");
        }
    }
}
