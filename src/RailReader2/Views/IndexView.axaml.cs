using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailReader2.ViewModels;
using SkiaSharp;

namespace RailReader2.Views;

public class PeekEntryViewModel
{
    public required string Label { get; init; }
    public required string PageDisplay { get; init; }
    public Bitmap? Thumbnail { get; set; }
    public string? ExtractedText { get; set; }
    public bool HasThumbnail => Thumbnail is not null;
    public bool HasExtractedText => ExtractedText is not null;
    public required PeekEntry Entry { get; init; }
}

/// <summary>
/// Index pane (figures, tables, equations) — a debounced index of detected visual blocks
/// with thumbnails (figures/tables) and extracted text (equations), plus the whole-document
/// "Scan All" sweep. Self-contained: wires its analysis-cache and ViewModel subscriptions on
/// load and tears them down on unload so it behaves as a lazily-realised tab / dockable tool.
/// </summary>
public partial class IndexView : PaneRefreshView
{
    private MainWindowViewModel? _vm;

    private DispatcherTimer? _peekDebounceTimer;
    private DocumentState? _peekWatchedDoc;
    private readonly Dictionary<int, Bitmap?> _thumbnailCache = [];
    private readonly Dictionary<int, SKBitmap?> _pageThumbCache = [];
    private readonly Dictionary<int, string?> _textCache = [];
    private bool _peekDirty;
    private int _lastPeekSignature;

    public IndexView()
    {
        InitializeComponent();
    }

    // The rebuild renders figure/table thumbnails via PDFium on the UI thread, so deferring it
    // for a hidden pane during background analysis avoids hitching the UI for no visible benefit.
    protected override void Refresh() => RefreshPeekIndex();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Attach(DataContext as MainWindowViewModel);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Detach();
        base.OnUnloaded(e);
    }

    private void Attach(MainWindowViewModel? vm)
    {
        Detach();
        _vm = vm;
        if (_vm is null) return;

        _vm.PropertyChanged += OnVmPropertyChanged;
        SubscribePeekUpdates();
        RefreshIfVisible();
        UpdateScanAllBanner();
    }

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
        // Stop reacting to analysis updates while unloaded, but do NOT dispose the rendered
        // thumbnails: they stay bound to PeekEntryList.ItemsSource (this view instance and its
        // caches persist across tab switches), and disposing them here would crash the next
        // layout pass when the tab is shown again. They are disposed on document change instead.
        if (_peekWatchedDoc is not null)
        {
            _peekWatchedDoc.AnalysisCacheUpdated -= OnAnalysisCacheUpdated;
            _peekWatchedDoc = null;
        }
        _peekDebounceTimer?.Stop();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MainWindowViewModel.ActiveTab):
                // Only reset peek state when the actual document changes (tab switch),
                // not on every page navigation within the same document.
                var newDoc = _vm?.Controller.ActiveDocument;
                if (newDoc != _peekWatchedDoc)
                {
                    SubscribePeekUpdates();
                    // Unbind before disposing the previous document's thumbnails so no live
                    // Image references a disposed bitmap during the next layout pass.
                    PeekEntryList.ItemsSource = null;
                    ClearThumbnailCache();
                    RefreshIfVisible();
                }
                break;
            case nameof(MainWindowViewModel.IsScanAllActive):
                UpdateScanAllBanner();
                // After the sweep completes or is cancelled, surface the final index.
                if (_vm?.IsScanAllActive == false)
                    RefreshIfVisible();
                break;
            case nameof(MainWindowViewModel.ScanAllProgress):
                if (_vm?.IsScanAllActive == true)
                    ScanAllProgressText.Text = _vm.ScanAllProgress;
                break;
        }
    }

    private void UpdateScanAllBanner()
    {
        if (_vm is null) return;
        bool active = _vm.IsScanAllActive;
        ScanAllBanner.IsVisible = active;
        // Only animate while the banner is shown. An attached (collapsed) indeterminate
        // ProgressBar keeps Avalonia's animation clock running, which busy-loops the
        // X11/GLib dispatcher at ~100% CPU until the app is restarted.
        ScanAllProgressBar.IsIndeterminate = active;
        ScanAllButton.IsEnabled = !active;
        if (active)
            ScanAllProgressText.Text = _vm.ScanAllProgress;
    }

    // --- Peek index ---

    private void SubscribePeekUpdates()
    {
        var doc = _vm?.Controller.ActiveDocument;
        if (doc == _peekWatchedDoc) return;

        if (_peekWatchedDoc is not null)
            _peekWatchedDoc.AnalysisCacheUpdated -= OnAnalysisCacheUpdated;

        _peekWatchedDoc = doc;

        if (_peekWatchedDoc is not null)
            _peekWatchedDoc.AnalysisCacheUpdated += OnAnalysisCacheUpdated;
    }

    private void OnAnalysisCacheUpdated()
    {
        // In the accordion this view stays loaded even when its section is collapsed, so guard on
        // visibility: rendering thumbnails (PDFium, UI thread) for a hidden pane on every
        // background-analysis tick is the perf regression we're avoiding. Defer instead.
        if (!IsEffectivelyVisible) { DeferRefresh(); return; }

        _peekDirty = true;

        if (_peekDebounceTimer is null)
        {
            _peekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _peekDebounceTimer.Tick += (_, _) =>
            {
                if (_peekDirty)
                {
                    _peekDirty = false;
                    RefreshIfVisible();
                }
                else
                {
                    _peekDebounceTimer.Stop();
                }
            };
        }

        if (!_peekDebounceTimer.IsEnabled)
            _peekDebounceTimer.Start();
    }

    private void RefreshPeekIndex()
    {
        var doc = _vm?.Controller.ActiveDocument;
        if (doc is null)
        {
            PeekEntryList.ItemsSource = null;
            PeekProgress.Text = "";
            return;
        }

        var index = PeekIndexBuilder.Build(doc.AnalysisCache, doc.PageCount);
        var fullScan = _vm?.ActiveTab?.FullScanPeekIndex;

        // Progress text: if we have a full-scan index, report coverage from that
        if (fullScan is not null)
            PeekProgress.Text = $"All {doc.PageCount} pages indexed ({fullScan.Figures.Count + fullScan.Tables.Count + fullScan.Equations.Count} entries)";
        else if (index.ScannedPages >= index.TotalPages)
            PeekProgress.Text = $"All {index.TotalPages} pages scanned";
        else if (doc.Rail.Active)
            PeekProgress.Text = $"{index.ScannedPages} of {index.TotalPages} pages scanned (paused in rail mode)";
        else
            PeekProgress.Text = $"{index.ScannedPages} of {index.TotalPages} pages scanned";

        var entries = new List<PeekEntryViewModel>();
        int filter = Math.Max(0, CategoryFilter.SelectedIndex);
        bool showFigures = filter == 0 || filter == 1;
        bool showTables = filter == 0 || filter == 2;
        bool showEquations = filter == 0 || filter == 3;

        // Live data from current analysis cache
        if (showFigures)
            AddEntries(entries, index.Figures, "Figure");
        if (showTables)
            AddEntries(entries, index.Tables, "Table");
        if (showEquations)
            AddEntries(entries, index.Equations, "Equation");

        // Supplement with full-scan entries for pages not in the live cache
        if (fullScan is not null)
            AddFullScanSupplement(entries, fullScan, doc, showFigures, showTables, showEquations);

        entries.Sort((a, b) =>
        {
            int cmp = a.Entry.PageIndex.CompareTo(b.Entry.PageIndex);
            return cmp != 0 ? cmp : a.Entry.BlockIndex.CompareTo(b.Entry.BlockIndex);
        });

        // Only rebuild the visual tree if entries actually changed — replacing
        // ItemsSource mid-click destroys the button the user pressed. A content
        // signature (page + block + role of every entry) is used rather than a
        // bare count so that a same-count-but-different-content re-analysis
        // (e.g. a block reclassified at the same index) still triggers a rebuild.
        int signature = ComputePeekSignature(entries);
        if (signature != _lastPeekSignature)
        {
            _lastPeekSignature = signature;
            GenerateThumbnails(entries, doc);
            PeekEntryList.ItemsSource = entries;
        }
    }

    /// <summary>Order-sensitive hash of the entry list's identifying fields.</summary>
    private static int ComputePeekSignature(List<PeekEntryViewModel> entries)
    {
        var hash = new HashCode();
        hash.Add(entries.Count);
        foreach (var e in entries)
        {
            hash.Add(e.Entry.PageIndex);
            hash.Add(e.Entry.BlockIndex);
            hash.Add(e.Entry.Role);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Adds entries from the full-scan index for pages not already covered
    /// by live analysis data. This ensures figures from distant (evicted)
    /// pages remain visible in the Figures pane.
    /// </summary>
    private void AddFullScanSupplement(List<PeekEntryViewModel> entries, PeekIndex fullScan,
        DocumentState doc, bool showFigures, bool showTables, bool showEquations)
    {
        // Live entries already cover every page present in the analysis cache
        // (the live index is built solely from AnalysisCache), so supplement only
        // with full-scan entries on pages NOT in the live cache. That single
        // page-membership check fully deduplicates against the live entries.
        if (showFigures)
            AddEntries(entries, FilterUncached(fullScan.Figures, doc), "Figure");
        if (showTables)
            AddEntries(entries, FilterUncached(fullScan.Tables, doc), "Table");
        if (showEquations)
            AddEntries(entries, FilterUncached(fullScan.Equations, doc), "Equation");
    }

    private static List<PeekEntry> FilterUncached(IReadOnlyList<PeekEntry> scanEntries, DocumentState doc)
    {
        var result = new List<PeekEntry>();
        foreach (var entry in scanEntries)
            if (!doc.AnalysisCache.ContainsKey(entry.PageIndex))
                result.Add(entry);
        return result;
    }

    private static void AddEntries(List<PeekEntryViewModel> list, IReadOnlyList<PeekEntry> entries, string category)
    {
        foreach (var entry in entries)
        {
            list.Add(new PeekEntryViewModel
            {
                Label = category,
                PageDisplay = $"Page {entry.PageIndex + 1} — {entry.Role}",
                Entry = entry,
            });
        }
    }

    private void GenerateThumbnails(List<PeekEntryViewModel> entries, DocumentState doc)
    {
        foreach (var vm in entries)
        {
            int cacheKey = vm.Entry.PageIndex * 10000 + vm.Entry.BlockIndex;

            if (vm.Entry.Role == BlockRole.DisplayMath)
            {
                if (!_textCache.TryGetValue(cacheKey, out var cachedText))
                {
                    cachedText = ExtractEntryText(doc, vm.Entry);
                    // Only cache non-empty results; empty text for evicted pages
                    // should be re-extracted when the page is re-analyzed.
                    if (!string.IsNullOrEmpty(cachedText))
                        _textCache[cacheKey] = cachedText;
                }
                vm.ExtractedText = cachedText;
                continue;
            }

            if (_thumbnailCache.TryGetValue(cacheKey, out var cached))
            {
                vm.Thumbnail = cached;
                continue;
            }

            var thumb = CropBlockThumbnail(doc, vm.Entry);
            _thumbnailCache[cacheKey] = thumb;
            vm.Thumbnail = thumb;
        }
    }

    private static string? ExtractEntryText(DocumentState doc, PeekEntry entry)
    {
        var pageText = doc.GetOrExtractText(entry.PageIndex);
        var bbox = entry.BBox;
        return pageText.ExtractTextInRect(bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H);
    }

    /// <summary>
    /// Renders or returns a cached page thumbnail SKBitmap. Avoids calling
    /// PDFium's RenderThumbnail multiple times for the same page.
    /// </summary>
    private SKBitmap? GetPageThumb(DocumentState doc, int pageIndex)
    {
        if (_pageThumbCache.TryGetValue(pageIndex, out var cached))
            return cached;

        SKBitmap? result = null;
        try
        {
            var rendered = doc.Pdf.RenderThumbnail(pageIndex);
            if (rendered is SkiaRenderedPage skiaPage)
                result = skiaPage.Bitmap.Copy();
            rendered?.Dispose();
        }
        catch { }

        _pageThumbCache[pageIndex] = result;
        return result;
    }

    private Bitmap? CropBlockThumbnail(DocumentState doc, PeekEntry entry)
    {
        var bitmap = GetPageThumb(doc, entry.PageIndex);
        if (bitmap is null) return null;

        try
        {
            var (pageW, pageH) = doc.Pdf.GetPageSize(entry.PageIndex);
            float scaleX = bitmap.Width / (float)pageW;
            float scaleY = bitmap.Height / (float)pageH;

            var cropRect = new SKRectI(
                Math.Max(0, (int)(entry.BBox.X * scaleX)),
                Math.Max(0, (int)(entry.BBox.Y * scaleY)),
                Math.Min(bitmap.Width, (int)((entry.BBox.X + entry.BBox.W) * scaleX)),
                Math.Min(bitmap.Height, (int)((entry.BBox.Y + entry.BBox.H) * scaleY)));

            if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;

            using var cropped = new SKBitmap();
            if (!bitmap.ExtractSubset(cropped, cropRect)) return null;

            using var data = cropped.Encode(SKEncodedImageFormat.Png, 90);
            if (data is null) return null;

            using var stream = new MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private void ClearThumbnailCache()
    {
        foreach (var bmp in _thumbnailCache.Values)
            bmp?.Dispose();
        _thumbnailCache.Clear();
        _textCache.Clear();
        _lastPeekSignature = 0;

        foreach (var bmp in _pageThumbCache.Values)
            bmp?.Dispose();
        _pageThumbCache.Clear();
    }

    private void OnPeekFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null) RefreshPeekIndex();
    }

    private void OnPeekEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Button { DataContext: PeekEntryViewModel entry }) return;
        _vm.GoToPage(entry.Entry.PageIndex);
        _vm.RequestViewportFocus();
        if (_vm.IsScanAllActive)
            _vm.ShowStatusToast("Navigation unavailable during scan");
    }

    // Right-click a result to open it in the temporary detached portal window (auto-opens; the user can
    // Lock it there to keep it pinned, otherwise it dismisses on read-on/tab-switch). Needs no rail mode.
    private void OnPeekEntryContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Control { DataContext: PeekEntryViewModel entry }) return;
        _vm.ShowBlockInPortal(entry.Entry.PageIndex, entry.Entry.BlockIndex);
        e.Handled = true;
    }

    // --- Scan All ---

    private void OnScanAllClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.StartScanAllCommand.Execute(null);
    }

    private void OnScanAllCancelClick(object? sender, RoutedEventArgs e)
    {
        _vm?.CancelScanAll();
    }
}
