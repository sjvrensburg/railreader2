using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
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
/// Peek sidebar (Figures tab) — debounced refresh of the figure/table/equation
/// index, thumbnail rendering, and per-entry text extraction. Split from the
/// main OutlinePanel partial to keep the figures concern self-contained.
/// </summary>
public partial class OutlinePanel
{
    private DispatcherTimer? _peekDebounceTimer;
    private DocumentState? _peekWatchedDoc;
    private readonly Dictionary<int, Bitmap?> _thumbnailCache = [];
    private readonly Dictionary<int, SKBitmap?> _pageThumbCache = [];
    private readonly Dictionary<int, string?> _textCache = [];
    private bool _peekDirty;
    private int _lastPeekEntryCount;
    private int _lastPeekScannedPages;

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

    private void UnsubscribePeekUpdates()
    {
        if (_peekWatchedDoc is not null)
        {
            _peekWatchedDoc.AnalysisCacheUpdated -= OnAnalysisCacheUpdated;
            _peekWatchedDoc = null;
        }
        _peekDebounceTimer?.Stop();
        ClearThumbnailCache();
    }

    private void OnAnalysisCacheUpdated()
    {
        if (!IsFiguresTabActive) return;

        _peekDirty = true;

        if (_peekDebounceTimer is null)
        {
            _peekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _peekDebounceTimer.Tick += (_, _) =>
            {
                if (_peekDirty)
                {
                    _peekDirty = false;
                    RefreshPeekIndex();
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
        if (index.ScannedPages >= index.TotalPages)
            PeekProgress.Text = $"All {index.TotalPages} pages scanned";
        else if (doc.Rail.Active)
            PeekProgress.Text = $"{index.ScannedPages} of {index.TotalPages} pages scanned (paused in rail mode)";
        else
            PeekProgress.Text = $"{index.ScannedPages} of {index.TotalPages} pages scanned";

        var entries = new List<PeekEntryViewModel>();
        bool showFigures = ShowFiguresToggle.IsChecked == true;
        bool showTables = ShowTablesToggle.IsChecked == true;
        bool showEquations = ShowEquationsToggle.IsChecked == true;

        if (showFigures)
            AddEntries(entries, index.Figures, "Figure");
        if (showTables)
            AddEntries(entries, index.Tables, "Table");
        if (showEquations)
            AddEntries(entries, index.Equations, "Equation");

        entries.Sort((a, b) =>
        {
            int cmp = a.Entry.PageIndex.CompareTo(b.Entry.PageIndex);
            return cmp != 0 ? cmp : a.Entry.BlockIndex.CompareTo(b.Entry.BlockIndex);
        });

        // Only rebuild the visual tree if entries changed — replacing
        // ItemsSource mid-click destroys the button the user pressed.
        int totalEntries = entries.Count;
        if (totalEntries != _lastPeekEntryCount || index.ScannedPages != _lastPeekScannedPages)
        {
            _lastPeekEntryCount = totalEntries;
            _lastPeekScannedPages = index.ScannedPages;
            GenerateThumbnails(entries, doc);
            PeekEntryList.ItemsSource = entries;
        }
    }

    private static void AddEntries(List<PeekEntryViewModel> list, IReadOnlyList<PeekEntry> entries, string category)
    {
        foreach (var entry in entries)
        {
            var className = LayoutConstants.LayoutClasses[entry.ClassId];
            list.Add(new PeekEntryViewModel
            {
                Label = category,
                PageDisplay = $"Page {entry.PageIndex + 1} \u2014 {className}",
                Entry = entry,
            });
        }
    }

    private void GenerateThumbnails(List<PeekEntryViewModel> entries, DocumentState doc)
    {
        foreach (var vm in entries)
        {
            int cacheKey = vm.Entry.PageIndex * 10000 + vm.Entry.BlockIndex;

            if (PeekIndexBuilder.EquationClasses.Contains(vm.Entry.ClassId))
            {
                if (!_textCache.TryGetValue(cacheKey, out var cachedText))
                {
                    cachedText = ExtractEntryText(doc, vm.Entry);
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
        _lastPeekEntryCount = 0;
        _lastPeekScannedPages = 0;

        foreach (var bmp in _pageThumbCache.Values)
            bmp?.Dispose();
        _pageThumbCache.Clear();
    }

    private void OnPeekFilterChanged(object? sender, RoutedEventArgs e)
    {
        RefreshPeekIndex();
    }

    private void OnPeekEntryClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Button { DataContext: PeekEntryViewModel entry }) return;
        _vm.GoToPage(entry.Entry.PageIndex);
    }
}
