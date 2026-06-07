using Avalonia.Threading;
using RailReader.Core.Models;
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
        if (!TryParseRole(role, out var parsed)) return false;
        return Dispatcher.UIThread.Invoke(
            () => _vm.SmoothlyFrameRole(parsed, occurrence, ToZoom(zoom)));
    }

    public bool FrameBlock(int pageBlockIndex, double zoom)
        => Dispatcher.UIThread.Invoke(
            () => _vm.SmoothlyFrameBlock(pageBlockIndex, ToZoom(zoom)));

    // --- Queries ---

    public string DocumentPath => Dispatcher.UIThread.Invoke(() => _vm.GetDocumentInfo()?.FilePath ?? "");
    public int PageCount => Dispatcher.UIThread.Invoke(() => _vm.GetDocumentInfo()?.PageCount ?? 0);
    public int CurrentPage => Dispatcher.UIThread.Invoke(() => _vm.GetDocumentInfo()?.CurrentPage ?? 0);
    public double Zoom => Dispatcher.UIThread.Invoke(() => _vm.GetDocumentInfo()?.Zoom ?? 0.0);
    public bool IsAnimating => Dispatcher.UIThread.Invoke(() => _vm.IsAnimating);
    public int CurrentBlockIndex => Dispatcher.UIThread.Invoke(() => _vm.GetReadingPosition()?.BlockIndex ?? -1);
    public string CurrentRole => Dispatcher.UIThread.Invoke(() => _vm.GetReadingPosition()?.Role.ToString() ?? "");

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

    /// <summary>Map a human role string ("figure", "equation", "heading", …) to a
    /// <see cref="BlockRole"/>, with the aliases the DSL/CLI accept. Falls back to a
    /// case-insensitive enum parse.</summary>
    private static bool TryParseRole(string role, out BlockRole parsed)
    {
        switch (role?.Trim().ToLowerInvariant())
        {
            case "figure": parsed = BlockRole.Figure; return true;
            case "table": parsed = BlockRole.Table; return true;
            case "equation" or "math" or "displaymath" or "display-math":
                parsed = BlockRole.DisplayMath; return true;
            case "heading" or "section": parsed = BlockRole.Heading; return true;
            case "title" or "doctitle" or "doc-title": parsed = BlockRole.Title; return true;
            case "caption": parsed = BlockRole.Caption; return true;
            case "text" or "paragraph": parsed = BlockRole.Text; return true;
            default:
                return Enum.TryParse(role, ignoreCase: true, out parsed);
        }
    }
}
