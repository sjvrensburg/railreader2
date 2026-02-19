using System.Threading.Channels;
using RailReader2.Models;

namespace RailReader2.Services;

public sealed class AnalysisRequest
{
    public required string FilePath { get; init; }
    public required int Page { get; init; }
    public required byte[] RgbBytes { get; init; }
    public required int PxW { get; init; }
    public required int PxH { get; init; }
    public required double PageW { get; init; }
    public required double PageH { get; init; }
}

public sealed class AnalysisResult
{
    public required string FilePath { get; init; }
    public required int Page { get; init; }
    public required PageAnalysis Analysis { get; init; }
}

public sealed class AnalysisWorker : IDisposable
{
    private readonly Channel<AnalysisRequest> _requestChannel;
    private readonly Channel<AnalysisResult> _resultChannel;
    private readonly HashSet<(string FilePath, int Page)> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    /// <summary>Set to true once the worker loop has initialized the ONNX session.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Set if the worker loop failed to start (e.g. ONNX model load failure).</summary>
    public string? StartupError { get; private set; }

    public AnalysisWorker(string modelPath)
    {
        _requestChannel = Channel.CreateUnbounded<AnalysisRequest>();
        _resultChannel = Channel.CreateUnbounded<AnalysisResult>();

        _workerTask = Task.Run(() => WorkerLoop(modelPath, _cts.Token));
        // Observe the task to prevent UnobservedTaskException
        _workerTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                Console.Error.WriteLine($"[Worker] Task faulted: {t.Exception?.InnerException?.Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task WorkerLoop(string modelPath, CancellationToken ct)
    {
        LayoutAnalyzer analyzer;
        try
        {
            analyzer = new LayoutAnalyzer(modelPath);
            IsReady = true;
            Console.Error.WriteLine("[Worker] ONNX session ready, waiting for requests...");
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
            Console.Error.WriteLine($"[Worker] FATAL: Failed to create ONNX session: {ex.Message}");
            // Unwrap inner exceptions for TypeInitializationException etc.
            var inner = ex.InnerException;
            while (inner is not null)
            {
                Console.Error.WriteLine($"[Worker]   Inner: {inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
            }
            Console.Error.WriteLine($"[Worker] Stack: {ex.StackTrace}");
            // Drain any pending requests and return fallback results
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(ct))
            {
                Console.Error.WriteLine($"[Worker] Returning fallback for page {request.Page} (no ONNX)");
                var fallback = LayoutAnalyzer.FallbackAnalysis(request.PageW, request.PageH);
                await _resultChannel.Writer.WriteAsync(new AnalysisResult
                {
                    FilePath = request.FilePath,
                    Page = request.Page,
                    Analysis = fallback,
                }, ct);
            }
            return;
        }

        using (analyzer)
        {
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(ct))
            {
                Console.Error.WriteLine($"[Worker] Running ONNX for {Path.GetFileName(request.FilePath)} page {request.Page}...");
                PageAnalysis analysis;
                try
                {
                    analysis = analyzer.RunAnalysis(
                        request.RgbBytes, request.PxW, request.PxH, request.PageW, request.PageH);
                    Console.Error.WriteLine($"[Worker] Page {request.Page}: {analysis.Blocks.Count} blocks detected");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Worker] Analysis failed for page {request.Page}: {ex.Message}\n{ex.StackTrace}");
                    analysis = LayoutAnalyzer.FallbackAnalysis(request.PageW, request.PageH);
                }

                await _resultChannel.Writer.WriteAsync(new AnalysisResult
                {
                    FilePath = request.FilePath,
                    Page = request.Page,
                    Analysis = analysis,
                }, ct);
            }
        }
    }

    public bool Submit(AnalysisRequest request)
    {
        var key = (request.FilePath, request.Page);
        if (!_inFlight.Add(key))
            return false;
        return _requestChannel.Writer.TryWrite(request);
    }

    public AnalysisResult? Poll()
    {
        if (_resultChannel.Reader.TryRead(out var result))
        {
            _inFlight.Remove((result.FilePath, result.Page));
            return result;
        }
        return null;
    }

    public bool IsInFlight(string filePath, int page) => _inFlight.Contains((filePath, page));
    public bool IsIdle => _inFlight.Count == 0;

    public void Dispose()
    {
        _requestChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}
