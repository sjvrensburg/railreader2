using System.Threading.Channels;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed record AnalysisRequest(
    string FilePath, int Page, byte[] RgbBytes,
    int PxW, int PxH, double PageW, double PageH);

public sealed record AnalysisResult(
    string FilePath, int Page, PageAnalysis Analysis);

public sealed class AnalysisWorker : IDisposable
{
    private readonly Channel<AnalysisRequest> _requestChannel;
    private readonly Channel<AnalysisResult> _resultChannel;
    private readonly HashSet<(string FilePath, int Page)> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly ILogger _logger;

    /// <summary>Set to true once the worker loop has initialized the ONNX session.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Set if the worker loop failed to start (e.g. ONNX model load failure).</summary>
    public string? StartupError { get; private set; }

    public AnalysisWorker(string modelPath, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _requestChannel = Channel.CreateUnbounded<AnalysisRequest>();
        _resultChannel = Channel.CreateUnbounded<AnalysisResult>();

        _workerTask = Task.Run(() => WorkerLoop(modelPath, _cts.Token));
        // Observe the task to prevent UnobservedTaskException
        _workerTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.Error("[Worker] Task faulted", t.Exception?.InnerException);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task WorkerLoop(string modelPath, CancellationToken ct)
    {
        LayoutAnalyzer analyzer;
        try
        {
            analyzer = new LayoutAnalyzer(modelPath);
            IsReady = true;
            _logger.Debug("[Worker] ONNX session ready, waiting for requests...");
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
            _logger.Error("[Worker] FATAL: Failed to create ONNX session", ex);
            _resultChannel.Writer.TryComplete();
            return;
        }

        using (analyzer)
        {
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(ct))
            {
                _logger.Debug($"[Worker] Running ONNX for {Path.GetFileName(request.FilePath)} page {request.Page}...");
                var analysis = analyzer.RunAnalysis(
                    request.RgbBytes, request.PxW, request.PxH, request.PageW, request.PageH, ct);
                _logger.Debug($"[Worker] Page {request.Page}: {analysis.Blocks.Count} blocks detected");

                await _resultChannel.Writer.WriteAsync(
                    new AnalysisResult(request.FilePath, request.Page, analysis), ct);
            }
        }
    }

    public bool Submit(AnalysisRequest request)
    {
        var key = (request.FilePath, request.Page);
        if (!_inFlight.Add(key))
            return false;

        if (!_requestChannel.Writer.TryWrite(request))
        {
            _inFlight.Remove(key);
            return false;
        }
        return true;
    }

    public AnalysisResult? Poll()
    {
        if (!_resultChannel.Reader.TryRead(out var result))
            return null;

        _inFlight.Remove((result.FilePath, result.Page));
        return result;
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
