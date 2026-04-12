namespace RailReader.Core.Services;

public record ExportProgress(int CurrentPage, int TotalPages, string Status);

public record MarkdownExportOptions
{
    public bool EnableVlm { get; init; } = true;
    public bool IncludeAnnotations { get; init; } = true;
    public bool IncludeFigureImages { get; init; } = true;
    public bool InsertPageBreaks { get; init; } = true;
    public string? FigureOutputDir { get; init; }
    public string? PageRange { get; init; }
    public int VlmConcurrency { get; init; } = 2;
    public VlmEndpointConfig? VlmEndpoint { get; init; }
    public VlmService.PromptStyle VlmPromptStyle { get; init; }
    public bool VlmStructuredOutput { get; init; } = true;
}

public interface IMarkdownExportService
{
    Task ExportAsync(
        string pdfPath,
        TextWriter output,
        MarkdownExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default);
}
