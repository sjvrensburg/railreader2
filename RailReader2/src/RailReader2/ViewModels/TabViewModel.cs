using CommunityToolkit.Mvvm.ComponentModel;
using RailReader2.Models;
using RailReader2.Services;
using SkiaSharp;

namespace RailReader2.ViewModels;

public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private readonly PdfService _pdf;
    private readonly AppConfig _config;

    [ObservableProperty] private string _title;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private double _pageWidth;
    [ObservableProperty] private double _pageHeight;
    [ObservableProperty] private bool _debugOverlay;
    [ObservableProperty] private bool _pendingRailSetup;

    public string FilePath { get; }
    public int PageCount { get; }
    public PdfService Pdf => _pdf;
    public Camera Camera { get; } = new();
    public RailNav Rail { get; }
    public Dictionary<int, PageAnalysis> AnalysisCache { get; } = [];
    public Queue<int> PendingAnalysis { get; } = new();
    public List<OutlineEntry> Outline { get; }

    // Cached page bitmap and the DPI it was rendered at
    public SKBitmap? CachedBitmap { get; private set; }
    public int CachedDpi { get; private set; }

    public TabViewModel(string filePath, AppConfig config)
    {
        _config = config;
        FilePath = filePath;
        _pdf = new PdfService(filePath);
        PageCount = _pdf.PageCount;
        _title = Path.GetFileName(filePath);
        Rail = new RailNav(config);
        Outline = _pdf.Outline;
    }

    /// <summary>
    /// Renders the current page bitmap. Safe to call from a background thread.
    /// Does NOT submit analysis (which requires UI-thread access to the worker).
    /// </summary>
    public void LoadPageBitmap()
    {
        try
        {
            // Don't dispose old bitmap here — the render thread may still be
            // drawing it. Assign null first so the renderer sees no bitmap,
            // then let GC collect the old one safely.
            CachedBitmap = null;

            var (w, h) = _pdf.GetPageSize(CurrentPage);
            PageWidth = w;
            PageHeight = h;

            int dpi = PdfService.CalculateRenderDpi(Camera.Zoom);
            CachedBitmap = _pdf.RenderPage(CurrentPage, dpi);
            CachedDpi = dpi;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to render page {CurrentPage}: {ex.Message}");
            CachedBitmap = null;
        }
    }

    private bool _dpiRenderPending;

    /// <summary>
    /// Checks if the current zoom demands a different DPI and schedules an
    /// async re-render on a background thread. Returns true if a render was
    /// scheduled (caller should request an animation frame to pick up the result).
    /// </summary>
    public bool UpdateRenderDpiIfNeeded()
    {
        if (_dpiRenderPending) return false;

        int neededDpi = PdfService.CalculateRenderDpi(Camera.Zoom);
        if (neededDpi > CachedDpi * 1.4 || (neededDpi < CachedDpi * 0.4 && CachedDpi > 150))
        {
            _dpiRenderPending = true;
            int page = CurrentPage;
            Task.Run(() =>
            {
                try
                {
                    var newBitmap = _pdf.RenderPage(page, neededDpi);
                    // Marshal back to UI thread for the swap
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (CurrentPage == page) // still on same page
                        {
                            // Don't dispose the old bitmap here — the render thread
                            // (compositor) may still be drawing it. Let GC collect it
                            // via SKBitmap's finalizer to avoid use-after-free.
                            CachedBitmap = newBitmap;
                            CachedDpi = neededDpi;
                        }
                        else
                        {
                            newBitmap.Dispose();
                        }
                        _dpiRenderPending = false;
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to re-render page at {neededDpi} DPI: {ex.Message}");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _dpiRenderPending = false);
                }
            });
            return true;
        }
        return false;
    }

    public void SubmitAnalysis(AnalysisWorker? worker)
    {
        if (AnalysisCache.TryGetValue(CurrentPage, out var cached))
        {
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: cache hit, {cached.Blocks.Count} blocks");
            Rail.SetAnalysis(cached, _config.NavigableClasses);
            PendingRailSetup = false;
            return;
        }

        if (worker is null)
        {
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: no worker, using fallback");
            var fallback = LayoutAnalyzer.FallbackAnalysis(PageWidth, PageHeight);
            AnalysisCache[CurrentPage] = fallback;
            Rail.SetAnalysis(fallback, _config.NavigableClasses);
            PendingRailSetup = false;
            return;
        }

        if (worker.IsInFlight(FilePath, CurrentPage))
        {
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: already in flight");
            PendingRailSetup = true;
            return;
        }

        try
        {
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: preparing pixmap for ONNX...");
            var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(CurrentPage, LayoutConstants.InputSize);
            Console.Error.WriteLine($"[SubmitAnalysis] Page {CurrentPage}: submitting {pxW}x{pxH} to worker");
            worker.Submit(new AnalysisRequest
            {
                FilePath = FilePath,
                Page = CurrentPage,
                RgbBytes = rgb,
                PxW = pxW,
                PxH = pxH,
                PageW = PageWidth,
                PageH = PageHeight,
            });
            PendingRailSetup = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to prepare analysis input: {ex.Message}");
            var fallback = LayoutAnalyzer.FallbackAnalysis(PageWidth, PageHeight);
            AnalysisCache[CurrentPage] = fallback;
            Rail.SetAnalysis(fallback, _config.NavigableClasses);
            PendingRailSetup = false;
        }
    }

    public void ReapplyNavigableClasses()
    {
        if (AnalysisCache.TryGetValue(CurrentPage, out var cached))
            Rail.SetAnalysis(cached, _config.NavigableClasses);
    }

    public void QueueLookahead(int count)
    {
        PendingAnalysis.Clear();
        for (int i = 1; i <= count; i++)
        {
            int page = CurrentPage + i;
            if (page < PageCount && !AnalysisCache.ContainsKey(page))
                PendingAnalysis.Enqueue(page);
        }
    }

    public bool SubmitPendingLookahead(AnalysisWorker? worker)
    {
        if (worker is null || !worker.IsIdle) return false;

        while (PendingAnalysis.Count > 0)
        {
            int page = PendingAnalysis.Dequeue();
            if (AnalysisCache.ContainsKey(page) || worker.IsInFlight(FilePath, page)) continue;

            try
            {
                var (rgb, pxW, pxH) = _pdf.RenderPagePixmap(page, LayoutConstants.InputSize);
                worker.Submit(new AnalysisRequest
                {
                    FilePath = FilePath, Page = page, RgbBytes = rgb, PxW = pxW, PxH = pxH,
                    PageW = PageWidth, PageH = PageHeight,
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Lookahead prepare failed for page {page + 1}: {ex.Message}");
            }
        }
        return false;
    }

    public void GoToPage(int page, AnalysisWorker? worker, double windowWidth, double windowHeight)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == CurrentPage) return;

        double oldZoom = Camera.Zoom;
        CurrentPage = page;
        LoadPageBitmap();
        SubmitAnalysis(worker);
        Camera.Zoom = oldZoom;
        ClampCamera(windowWidth, windowHeight);
    }

    public void CenterPage(double windowWidth, double windowHeight)
    {
        if (PageWidth <= 0 || PageHeight <= 0 || windowWidth <= 0 || windowHeight <= 0) return;
        double scaleX = windowWidth / PageWidth;
        double scaleY = windowHeight / PageHeight;
        Camera.Zoom = Math.Min(scaleX, scaleY);
        double scaledW = PageWidth * Camera.Zoom;
        double scaledH = PageHeight * Camera.Zoom;
        Camera.OffsetX = (windowWidth - scaledW) / 2.0;
        Camera.OffsetY = (windowHeight - scaledH) / 2.0;
    }

    public void ClampCamera(double windowWidth, double windowHeight)
    {
        double scaledW = PageWidth * Camera.Zoom;
        double scaledH = PageHeight * Camera.Zoom;

        if (scaledW <= windowWidth)
            Camera.OffsetX = (windowWidth - scaledW) / 2.0;
        else
            Camera.OffsetX = Math.Clamp(Camera.OffsetX, windowWidth - scaledW, 0);

        if (scaledH <= windowHeight)
            Camera.OffsetY = (windowHeight - scaledH) / 2.0;
        else
            Camera.OffsetY = Math.Clamp(Camera.OffsetY, windowHeight - scaledH, 0);
    }

    public void ApplyZoom(double newZoom, double windowWidth, double windowHeight)
    {
        Camera.Zoom = Math.Clamp(newZoom, Camera.ZoomMin, Camera.ZoomMax);
        UpdateRailZoom(windowWidth, windowHeight);
        if (Rail.Active)
            StartSnap(windowWidth, windowHeight);
        ClampCamera(windowWidth, windowHeight);
    }

    public void UpdateRailZoom(double windowWidth, double windowHeight)
    {
        Rail.UpdateZoom(Camera.Zoom, Camera.OffsetX, Camera.OffsetY, windowWidth, windowHeight);
    }

    public void StartSnap(double windowWidth, double windowHeight)
    {
        Rail.StartSnapToCurrent(Camera.OffsetX, Camera.OffsetY, Camera.Zoom, windowWidth, windowHeight);
    }

    public void Dispose()
    {
        // Null the bitmap reference first so the render thread sees null,
        // then dispose. The tab should already be removed from Tabs by now.
        var bmp = CachedBitmap;
        CachedBitmap = null;
        bmp?.Dispose();
        _pdf.Dispose();
    }
}
