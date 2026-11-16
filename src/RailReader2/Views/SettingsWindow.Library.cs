using Avalonia.Interactivity;
using Avalonia.Threading;
using RailReader2.Library;

namespace RailReader2.Views;

// Settings ▸ Library (advanced view, design §6): index size, Rebuild, Clear. Writes go through the app's
// index writer (MainWindowViewModel.RebuildLibrary/ClearLibrary); the status is read with its own
// read-only connection, refreshed when the writer reports a change.
public partial class SettingsWindow
{
    private bool _libraryHooked;

    private void LoadLibrarySettings()
    {
        if (Vm is not { } vm) return;
        if (!_libraryHooked)
        {
            _libraryHooked = true;
            vm.LibraryChanged += RefreshLibraryStatus;
            Closed += (_, _) => vm.LibraryChanged -= RefreshLibraryStatus;
        }
        LibraryPathText.Text = LibraryIndex.DefaultPath;
        LibraryRebuildButton.IsEnabled = LibraryClearButton.IsEnabled = vm.IsLibraryIndexing;
        RefreshLibraryStatus();
    }

    private async void RefreshLibraryStatus()
    {
        try
        {
            var status = await Task.Run(() =>
            {
                using var index = LibraryIndex.OpenReadOnly(LibraryIndex.DefaultPath);
                return index?.GetStatus();
            });
            string text = status is null
                ? "The index is empty."
                : $"{status.Documents} documents, {status.Pages:N0} pages and {status.Notes:N0} notes · " +
                  $"{status.DatabaseBytes / (1024.0 * 1024.0):0.0} MB on disk";
            if (Vm is { } vm)
            {
                if (vm.LibraryBackfillProgress is (var done, > 0 and var total))
                    text += $" · indexing {done} of {total}…";
                if (!vm.IsLibraryIndexing) text += " · indexing is off this session";
            }
            LibraryStatusText.Text = text;
        }
        catch (Exception ex)
        {
            RailReader.Core.RailReaderLogging.Logger.Error("[Library] status failed", ex);
            LibraryStatusText.Text = "Couldn't read the index — see the log for details.";
        }
    }

    private async void OnLibraryRebuild(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ok = await new ConfirmDialog("Rebuild the library index?",
            "Every document you have opened is read again in the background. Search keeps working meanwhile, " +
            "but finds less until it has finished.", "Rebuild").ShowDialog<bool?>(this);
        if (ok != true) return;
        vm.RebuildLibrary();
        LibraryStatusText.Text = "Rebuilding…";
    }

    private async void OnLibraryClear(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ok = await new ConfirmDialog("Clear the library index?",
            "The index is deleted. Documents you have opened before are no longer found by library search until " +
            "you open them again (or rebuild the index). Your documents and notes are not touched.", "Clear").ShowDialog<bool?>(this);
        if (ok != true) return;
        vm.ClearLibrary();
        LibraryStatusText.Text = "Clearing…";
    }
}
