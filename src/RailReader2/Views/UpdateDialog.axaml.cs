using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RailReader.Core;
using RailReader2.Services.Updates;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// Help ▸ Check for Updates… (and the "Update…" action on the background check's toast). Checks, shows
/// what is available, downloads with a determinate bar, then offers to restart. Non-modal; closing it
/// cancels a running check or download. Installing in place is AppImage/Inno only; other channels get
/// the release page.
/// </summary>
public partial class UpdateDialog : Window
{
    private enum Step { Checking, UpToDate, Failed, Available, Downloading, Ready }

    private readonly MainWindowViewModel? _vm;
    private readonly InstallKind _kind;
    private readonly CancellationTokenSource _cts = new();
    private UpdateCheckResult? _result;
    private PreparedUpdate? _prepared;
    private Step _step;
    private bool _manualOnly;

    public UpdateDialog() => InitializeComponent();

    internal UpdateDialog(MainWindowViewModel vm, InstallKind kind, UpdateCheckResult? result) : this()
    {
        _vm = vm;
        _kind = kind;
        DialogStacking.KeepAboveFullscreenOwner(this);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) => _cts.Cancel();

        if (result is not null) Show(result);
        else
        {
            Set(Step.Checking);
            Opened += async (_, _) => await CheckAsync();
        }
    }

    private async Task CheckAsync()
    {
        Set(Step.Checking);
        try
        {
            var result = await UpdateFeed.CheckAsync(_cts.Token);
            if (result.Status != UpdateCheckStatus.Failed && _vm is { } vm)
            {
                vm.UpdatePrefs.LastCheckUtc = DateTime.UtcNow;
                vm.UpdatePrefs.Save();
            }
            Show(result);
        }
        catch (OperationCanceledException) { }
    }

    private void Show(UpdateCheckResult result)
    {
        _result = result;
        Set(result.Status switch
        {
            UpdateCheckStatus.UpToDate => Step.UpToDate,
            UpdateCheckStatus.Available => Step.Available,
            _ => Step.Failed,
        });
    }

    private bool InstallsInPlace
        => !_manualOnly && UpdateChannel.CanInstallInPlace(_kind) && _result?.Manifest is { } m
           && m.FormatVersion <= UpdateManifest.SupportedFormat
           && UpdateChannel.AssetKey(_kind) is { } key && m.Assets.ContainsKey(key);

    private void Set(Step step)
    {
        _step = step;
        var current = UpdateVersion.Current.ToString();
        var latest = _result?.Manifest?.Version ?? "";
        BodyText.IsVisible = false;
        NotesLink.IsVisible = false;
        DownloadProgress.IsVisible = false;
        SkipButton.IsVisible = false;
        PrimaryButton.IsVisible = false;
        SecondaryButton.IsVisible = false;
        CloseButton.Content = "Close";
        CloseButton.IsEnabled = true;

        switch (step)
        {
            case Step.Checking:
                HeadingText.Text = "Checking for updates…";
                break;
            case Step.UpToDate:
                HeadingText.Text = $"RailReader2 is up to date ({current}).";
                break;
            case Step.Failed:
                HeadingText.Text = "Couldn't check for updates.";
                Body(_result?.Error ?? "");
                Primary("Try Again");
                Secondary("Open Download Page");
                break;
            case Step.Available:
                HeadingText.Text = $"RailReader2 {latest} is available. You have {current}.";
                NotesLink.IsVisible = true;
                SkipButton.IsVisible = true;
                CloseButton.Content = "Later";
                if (InstallsInPlace)
                {
                    Body(_kind == InstallKind.Inno
                        ? "RailReader2 downloads the new installer, checks it, then installs it when it closes."
                        : "RailReader2 downloads the new AppImage, checks it, and replaces this one.");
                    Primary("Update");
                }
                else
                {
                    Body(_result?.Error ?? (_kind == InstallKind.Tarball
                        ? "Download the new archive from the release page and extract it over this copy."
                        : "Download the new version from the release page."));
                    Primary("Open Download Page");
                }
                break;
            case Step.Downloading:
                HeadingText.Text = $"Downloading RailReader2 {latest}…";
                DownloadProgress.Value = 0;
                DownloadProgress.IsVisible = true;
                CloseButton.Content = "Cancel";
                break;
            case Step.Ready:
                if (_kind == InstallKind.Inno)
                {
                    HeadingText.Text = $"RailReader2 {latest} is ready to install.";
                    Body("RailReader2 will close, install the update and reopen. Your annotations and settings are saved first.");
                    Primary("Restart and Install");
                    Secondary("Install When I Close");
                }
                else
                {
                    HeadingText.Text = $"RailReader2 {latest} is installed.";
                    Body("Restart to start using it. Your annotations and settings are saved first.");
                    Primary("Restart Now");
                    CloseButton.Content = "Later";
                }
                break;
        }
    }

    private void Body(string text)
    {
        BodyText.Text = text;
        BodyText.IsVisible = text.Length > 0;
    }

    private void Primary(string text)
    {
        PrimaryButton.Content = text;
        PrimaryButton.IsVisible = true;
    }

    private void Secondary(string text)
    {
        SecondaryButton.Content = text;
        SecondaryButton.IsVisible = true;
    }

    private string ReleasePage => _result?.Manifest?.ReleaseUrl is { Length: > 0 } url ? url : UpdateFeed.ReleasesPage;

    private async void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Failed:
                await CheckAsync();
                break;
            case Step.Available when InstallsInPlace:
                await DownloadAsync();
                break;
            case Step.Available:
                _vm?.OpenUri(ReleasePage);
                Close();
                break;
            case Step.Ready when _prepared is { } p:
                Close();
                _vm?.FinishUpdate(p, restartNow: true);
                break;
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Failed:
                _vm?.OpenUri(UpdateFeed.ReleasesPage);
                Close();
                break;
            case Step.Ready when _prepared is { } p:
                _vm?.FinishUpdate(p, restartNow: false);
                _vm?.ShowStatusToast($"RailReader2 {p.Version} will install when you close it.");
                Close();
                break;
        }
    }

    private async Task DownloadAsync()
    {
        if (_result?.Manifest is not { } manifest) return;
        Set(Step.Downloading);
        var progress = new Progress<double>(v => DownloadProgress.Value = v);
        try
        {
            _prepared = await Task.Run(() => UpdateInstaller.PrepareAsync(manifest, _kind, progress, _cts.Token));
            Set(Step.Ready);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (UpdateInstaller.CannotInstallException ex)
        {
            // Can't install here (e.g. an unwritable AppImage directory): fall back to the release page.
            _manualOnly = true;
            _result = new UpdateCheckResult(UpdateCheckStatus.Available, manifest, ex.Message);
            Set(Step.Available);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[Update] Download failed", ex);
            _result = UpdateCheckResult.Fail(ex is UpdateVerificationException
                ? ex.Message + " Nothing was installed."
                : "The download failed. Check your connection and try again.");
            Set(Step.Failed);
        }
    }

    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        if (_result?.Manifest?.Version is { Length: > 0 } v) _vm?.SkipUpdateVersion(v);
        Close();
    }

    private void OnNotesClick(object? sender, RoutedEventArgs e) => _vm?.OpenUri(ReleasePage);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
