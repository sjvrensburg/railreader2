using RailReader.Core;
using RailReader.Core.Services;

namespace RailReader.Cli;

/// <summary>
/// Shared session state across CLI commands.
/// Holds the DocumentController, config, and ONNX worker status.
/// </summary>
public sealed class CliSession : IDisposable
{
    public DocumentController Controller { get; }
    public AppConfig Config { get; }
    public bool AnalysisAvailable { get; private set; }

    public CliSession()
    {
        Config = AppConfig.Load();
        Controller = new DocumentController(Config, new SynchronousThreadMarshaller());
        Controller.SetViewportSize(1200, 900);

        try
        {
            Controller.InitializeWorker();
            AnalysisAvailable = true;
        }
        catch (FileNotFoundException)
        {
            AnalysisAvailable = false;
        }
    }

    /// <summary>
    /// Returns the active document or throws with a clear message.
    /// </summary>
    public DocumentState RequireActiveDocument()
    {
        return Controller.ActiveDocument
            ?? throw new InvalidOperationException("No document open. Use 'document open <path>' first.");
    }

    /// <summary>
    /// Validates a 1-based page number and returns the 0-based index.
    /// </summary>
    public int ValidatePage(DocumentState doc, int page1Based)
    {
        int page = page1Based - 1;
        if (page < 0 || page >= doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(page1Based),
                $"Page {page1Based} is out of range (document has {doc.PageCount} pages).");
        return page;
    }

    public void Dispose()
    {
        foreach (var doc in Controller.Documents.ToList())
            doc.Dispose();
    }
}
