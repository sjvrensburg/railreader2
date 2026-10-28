using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RailReader.Core;
using RailReader2.ViewModels;

namespace RailReader2.Views;

/// <summary>
/// "Add a Text Layer…" (railreader2#273): pick a language pack, see a rough time, then follow the job's
/// progress. Shown non-modally so the reader can keep reading. Closing it cancels a running job.
/// <see cref="Completion"/> yields the copy's path on success, else null, and only once the job has
/// stopped reading the document's PDF (the caller's tab-close guard depends on that).
/// </summary>
public partial class TextLayerDialog : Window
{
    private readonly MainWindowViewModel? _vm;
    private readonly TabViewModel? _tab;
    private readonly IReadOnlyList<int> _pages = [];
    private readonly string _outputPath = "";
    private readonly TaskCompletionSource<string?> _completion = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public Task<string?> Completion => _completion.Task;

    public TextLayerDialog() => InitializeComponent();

    internal TextLayerDialog(MainWindowViewModel vm, TabViewModel tab, IReadOnlyList<int> pages,
        List<TextLayerPack> packs, int selected) : this()
    {
        _vm = vm;
        _tab = tab;
        _pages = pages;
        _outputPath = MainWindowViewModel.FreeTextLayerPath(tab.FilePath);
        DialogStacking.KeepAboveFullscreenOwner(this);

        int total = tab.Pdf.PageCount;
        string which = pages.Count == total
            ? (total == 1 ? "the page" : $"all {total} pages")
            : $"the {pages.Count} of {total} pages that have no text";
        IntroText.Text = $"Reads {which} and saves the text, invisibly, in a copy named " +
            $"{Path.GetFileName(_outputPath)}, which then opens in this tab. The copy can be searched, " +
            "selected and highlighted in any PDF reader. The original is not changed.";
        PackCombo.ItemsSource = packs;
        PackCombo.SelectedIndex = selected;

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Closed += (_, _) =>
        {
            if (_running) _cts?.Cancel(); // OnStartClick completes once the job has unwound
            else _completion.TrySetResult(null);
        };
    }

    private void OnPackChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PackCombo.SelectedItem is not TextLayerPack pack) return;
        EstimateText.Text = $"Rough estimate: {Duration(pack.SecondsPerPage * _pages.Count)}, depending on " +
            "the computer. Larger packs read more scripts and read them better, but are much slower.";
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _tab is null || PackCombo.SelectedItem is not TextLayerPack pack) return;
        StartButton.IsEnabled = false;
        PackCombo.IsEnabled = false;
        JobProgress.IsVisible = true;
        StatusText.IsVisible = true;
        StatusText.Text = "Loading the OCR models…";

        _cts = new CancellationTokenSource();
        _running = true;
        long started = Stopwatch.GetTimestamp();
        // Created on the UI thread, so reports are posted back to it.
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            JobProgress.Value = (double)p.Done / p.Total;
            if (p.Done == 0) return;
            double perPage = Stopwatch.GetElapsedTime(started).TotalSeconds / p.Done;
            StatusText.Text = p.Done < p.Total
                ? $"Read {p.Done} of {p.Total} pages · {Duration(perPage * (p.Total - p.Done))} left"
                : "Saving the copy…";
        });

        string? result = null;
        try
        {
            var written = await _vm.RunTextLayerJob(_tab, _pages, pack, _outputPath, progress, _cts.Token);
            if (written.PagesWritten == 0)
            {
                ShowEnded("No text was recognised, so no copy was saved.");
                return;
            }
            _vm.ShowStatusToast(written.SourceWasSigned
                ? "Text layer added. The original is digitally signed; the copy's signatures are no longer valid."
                : $"Text layer added to {Pages(written.PagesWritten)}.");
            result = _outputPath;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("[TextLayer] Job failed", ex);
            ShowEnded($"Couldn't add the text layer: {ex.Message}");
            return;
        }
        finally
        {
            _running = false;
            _completion.TrySetResult(result);
        }
        if (IsVisible) Close(); // not when the reader closed the dialog, which cancelled the job
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            Close();
            return;
        }
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling…";
    }

    /// <summary>The job ended without a copy: say why and leave only a Close button.</summary>
    private void ShowEnded(string message)
    {
        JobProgress.IsVisible = false;
        StatusText.Text = message;
        StartButton.IsVisible = false;
        CancelButton.Content = "Close";
        CancelButton.IsEnabled = true;
    }

    private static string Pages(int n) => n == 1 ? "1 page" : $"{n} pages";

    private static string Duration(double seconds) => seconds switch
    {
        < 60 => "under a minute",
        < 90 * 60 => $"about {Math.Max(1, (int)Math.Round(seconds / 60))} min",
        _ => $"about {seconds / 3600:0.#} hours",
    };
}
