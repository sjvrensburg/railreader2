using Avalonia.Threading;
using RailReader.Core.Services;
using RailReader2.ViewModels;

namespace RailReader2.ControlBus;

/// <summary>
/// The one real <see cref="IRailReaderControl"/> implementation. Marshals every verb and
/// query onto the UI thread (<see cref="Dispatcher.UIThread"/>) and routes it through
/// <see cref="MainWindowViewModel"/> — so external automation drives exactly the same
/// VM → DocumentController.Tick → RequestAnimationFrame path real input uses, and the
/// on-screen result is identical to what a user sees. Bus-free, so it is unit-testable
/// against a real VM under Avalonia.Headless.
/// </summary>
public sealed class ViewModelControl : IRailReaderControl, IDisposable
{
    private readonly MainWindowViewModel _vm;

    public ViewModelControl(MainWindowViewModel vm)
    {
        _vm = vm;
        _vm.AnimationSettled += OnSettled;
        _vm.PageChangedNotification += OnPageChanged;
    }

    // --- Verbs ---

    public async Task<bool> OpenDocumentAsync(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => _vm.OpenDocument(path));
        // OpenDocument swallows failures (it only toasts), so confirm by checking that the
        // active document is now the one we asked for.
        bool ok = string.Equals(
            Dispatcher.UIThread.Invoke(() => _vm.GetDocumentInfo()?.FilePath),
            path, StringComparison.Ordinal);
        if (ok) DocumentOpened?.Invoke(path);
        return ok;
    }

    public void GoToPage(int page)
        => Dispatcher.UIThread.Invoke(() => _vm.GoToPage(page));

    public void FitPage()
        => Dispatcher.UIThread.Invoke(_vm.FitPage);

    public void FitWidth()
        => Dispatcher.UIThread.Invoke(_vm.FitWidth);

    public bool FrameRole(string role, int occurrence, double zoom)
    {
        // Resolve the friendly token to its detector roles (e.g. "figure" → Figure, Chart) via
        // the shared Core vocabulary, and frame the first role that has a matching block.
        var roles = BlockRoleAliases.Resolve(role);
        if (roles.Count == 0) return false;
        double? z = ToZoom(zoom);
        return Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var r in roles)
                if (_vm.SmoothlyFrameRole(r, occurrence, z)) return true;
            return false;
        });
    }

    public bool FrameBlock(int pageBlockIndex, double zoom)
        => Dispatcher.UIThread.Invoke(
            () => _vm.SmoothlyFrameBlock(pageBlockIndex, ToZoom(zoom)));

    // --- Queries ---

    public ControlSnapshot Snapshot() => Dispatcher.UIThread.Invoke(() =>
    {
        var info = _vm.GetDocumentInfo();
        var pos = _vm.GetReadingPosition();
        return new ControlSnapshot(
            DocumentPath: info?.FilePath ?? "",
            PageCount: info?.PageCount ?? 0,
            CurrentPage: info?.CurrentPage ?? 0,
            Zoom: info?.Zoom ?? 0.0,
            IsAnimating: _vm.IsAnimating,
            CurrentBlockIndex: pos?.BlockIndex ?? -1,
            CurrentRole: pos?.Role.ToString() ?? "");
    });

    // --- Events ---

    public event Action? Settled;
    public event Action<int>? PageChanged;
    public event Action<string>? DocumentOpened;

    private void OnSettled() => Settled?.Invoke();
    private void OnPageChanged(int page) => PageChanged?.Invoke(page);

    public void Dispose()
    {
        _vm.AnimationSettled -= OnSettled;
        _vm.PageChangedNotification -= OnPageChanged;
    }

    // zoom <= 0 ⇒ auto-fit (null to the Core primitive).
    private static double? ToZoom(double zoom) => zoom > 0 ? zoom : null;
}
