using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Ocr.RapidOcr;
using RailReader.Core.Services;
using RailReader2.Services;
using RailReader2.Views;
using RapidOcrNet;

namespace RailReader2.ViewModels;

/// <summary>An installed OCR language pack the text-layer job can use, with a rough per-page time.</summary>
/// <param name="Id"><see cref="OcrModelDescriptor.Id"/>, or null for the bundled default.</param>
internal sealed record TextLayerPack(string? Id, string DisplayName, RapidOcrModelSet? Models, double SecondsPerPage)
{
    public override string ToString() => DisplayName;
}

// "Add a Text Layer…" (railreader2#273): recognise a scanned PDF once and save the text as an invisible
// layer in a copy, which then opens in place of the original. The job runs on the thread pool with its own
// RapidOcrService (the analysis worker's isn't thread-safe), on the CPU (two WebGPU sessions running at once
// crash, see onnxruntime#32561). Shared logic lives in RailReader2.Shared's OcrTextLayer, which the CLI's
// `ocr` command also uses.
public partial class MainWindowViewModel
{
    // Tabs whose PDF a text-layer job is reading on a background thread; CloseTab refuses to dispose the
    // model under one, as it does for an annotated export.
    private readonly HashSet<TabViewModel> _textLayerTabs = [];

    // Seconds per page for the bundled set at 300 dpi: a dense scanned journal page on a 20-thread laptop
    // CPU (2026-10-28), so a magnitude, not a promise. The PP-OCRv6 packs scale from it by Core's published
    // full-pass cost (Tiny = 1) and Tiny's measured cost relative to the bundled set on the same page
    // (2254 vs 1827 ms in RailReaderCore's tools/ocr-cost-probe). The dialog replaces the estimate with
    // one from the measured pace once the first page is done.
    private const double BundledSecondsPerPage = 4.0;
    private const double TinyOverBundled = 2254.0 / 1827.0;

    /// <summary>The installed packs, bundled default first; the second value is the one to preselect
    /// (the pack chosen for scanned pages, when installed).</summary>
    internal (List<TextLayerPack> Packs, int Selected) InstalledTextLayerPacks()
    {
        var packs = new List<TextLayerPack>();
        if (OcrModelLocator.LocateDefault() is not null)
            packs.Add(new(null, BundledOcrPackDisplayName, null, BundledSecondsPerPage));
        foreach (var desc in OcrModelRegistry.All)
            if (OcrModelLocator.Locate(desc.ModelSet) is { } models)
                packs.Add(new(desc.Id, desc.DisplayName, models,
                    BundledSecondsPerPage * TinyOverBundled * desc.RelativeFullCost));
        int selected = Math.Max(0, packs.FindIndex(p => p.Id == OcrPreferences.Load().ModelSetId));
        return (packs, selected);
    }

    [RelayCommand]
    public async Task AddTextLayer()
    {
        if (_window is null || ActiveTab is not { } tab) return;
        if (!_textLayerTabs.Add(tab))
        {
            ShowStatusToast("A text layer is already being added to this document.");
            return;
        }
        try
        {
            var pages = await Task.Run(() => OcrTextLayer.PagesNeedingOcr(tab.Pdf, tab.State.PdfText));
            if (pages.Count == 0)
            {
                ShowStatusToast("Every page of this document already has a text layer.");
                return;
            }
            var (packs, selected) = InstalledTextLayerPacks();
            if (packs.Count == 0)
            {
                ShowStatusToast("The OCR models are missing from this installation — see the log.");
                _logger.Error("[TextLayer] No OCR model set found (bundled PP-OCRv5 or a downloaded pack).");
                return;
            }

            var dialog = new TextLayerDialog(this, tab, pages, packs, selected) { FontSize = CurrentFontSize };
            dialog.Show(_window); // not modal: the reader can keep reading while it runs
            var outputPath = await dialog.Completion;
            if (outputPath is not null) await ReplaceTabWithCopy(tab, outputPath);
        }
        catch (Exception ex)
        {
            _logger.Error("[TextLayer] Failed", ex);
            ShowStatusToast("Adding a text layer failed — see the log for details.");
        }
        finally
        {
            _textLayerTabs.Remove(tab);
        }
    }

    /// <summary>Runs the job for <see cref="TextLayerDialog"/> on the thread pool. Annotations are flushed
    /// first, as the copy is made from the file on disk.</summary>
    internal Task<TextLayerWriteResult> RunTextLayerJob(TabViewModel tab, IReadOnlyList<int> pages,
        TextLayerPack pack, string outputPath, IProgress<(int Done, int Total)> progress, CancellationToken ct)
    {
        SaveAllReadingPositions();
        _logger.Info($"[TextLayer] {pages.Count} page(s) of {tab.FilePath} with {pack.DisplayName} → {outputPath}");
        return Task.Run(() =>
        {
            using var ocr = new RapidOcrService(pack.Models, logger: _logger);
            return OcrTextLayer.Run(tab.Pdf, tab.FilePath, ocr, pages, outputPath, progress: progress, ct: ct);
        }, ct);
    }

    /// <summary>Opens the finished copy where <paramref name="tab"/> was and closes the original's tab.</summary>
    private async Task ReplaceTabWithCopy(TabViewModel tab, string outputPath)
    {
        int count = Tabs.Count;
        await OpenDocument(outputPath);
        if (Tabs.Count != count + 1) return; // didn't open (the error was already reported)
        var copy = Tabs[^1];

        int index = Tabs.IndexOf(tab);
        if (index < 0) return; // the original was closed meanwhile; the copy stays where it opened
        _textLayerTabs.Remove(tab); // CloseTab refuses while the job's guard is set
        CloseTab(index);
        MoveTab(Tabs.IndexOf(copy), index);
    }

    /// <summary>A free name for the copy: <c>&lt;name&gt;_ocr.pdf</c>, else <c>&lt;name&gt;_ocr (2).pdf</c>,
    /// and so on, so an earlier copy (perhaps open in another tab) is never overwritten.</summary>
    internal static string FreeTextLayerPath(string sourcePath)
    {
        var path = OcrTextLayer.DefaultOutputPath(sourcePath);
        var stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
        for (int n = 2; File.Exists(path); n++) path = $"{stem} ({n}).pdf";
        return path;
    }
}
