using System.Text;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

namespace RailReader.Export;

/// <summary>
/// Exports a PDF to structured Markdown using layout analysis, VLM transcription,
/// and annotation extraction. Implements the <see cref="IMarkdownExportService"/> interface.
/// </summary>
public sealed class MarkdownExportService : IMarkdownExportService
{
    private readonly IPdfServiceFactory _factory;
    private readonly ILogger _logger;

    public MarkdownExportService(IPdfServiceFactory factory, ILogger? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task ExportAsync(
        string pdfPath,
        TextWriter output,
        MarkdownExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var pdf = _factory.CreatePdfService(pdfPath);
        var (pages, err) = PageRangeParser.Parse(options.PageRange, pdf.PageCount);
        if (err != null)
            throw new ArgumentException(err);

        // Try to create layout analyzer
        var modelPath = DocumentController.FindModelPath();
        using var analyzer = modelPath != null ? new LayoutAnalyzer(modelPath) : null;

        // Load annotations if requested
        AnnotationFile? annotationFile = null;
        if (options.IncludeAnnotations)
            annotationFile = AnnotationService.Load(pdfPath);

        // Resolve VLM endpoint configuration
        var vlmEndpoint = ResolveVlmEndpoint(options);
        bool vlmAvailable = options.EnableVlm && vlmEndpoint != null
            && !string.IsNullOrWhiteSpace(vlmEndpoint.Endpoint)
            && !string.IsNullOrWhiteSpace(vlmEndpoint.Model);

        bool firstPage = true;

        for (int pi = 0; pi < pages!.Count; pi++)
        {
            ct.ThrowIfCancellationRequested();

            int pageIdx = pages[pi];
            progress?.Report(new ExportProgress(pi + 1, pages.Count,
                $"Processing page {pageIdx + 1}"));

            string pageMd;

            if (analyzer != null)
            {
                pageMd = await ExportPageWithLayout(
                    pdf, pageIdx, analyzer, vlmAvailable, vlmEndpoint, options,
                    annotationFile, ct);
            }
            else
            {
                pageMd = ExportPagePlainText(
                    pdf, pageIdx, annotationFile, options);
            }

            if (string.IsNullOrWhiteSpace(pageMd))
                continue;

            // Page break between pages
            if (!firstPage && options.InsertPageBreaks)
                await output.WriteLineAsync("---");

            await output.WriteAsync(pageMd);
            firstPage = false;
        }

        await output.FlushAsync(ct);
        progress?.Report(new ExportProgress(pages.Count, pages.Count, "Complete"));
    }

    private async Task<string> ExportPageWithLayout(
        IPdfService pdf,
        int pageIdx,
        LayoutAnalyzer analyzer,
        bool vlmAvailable,
        VlmEndpointConfig? vlmEndpoint,
        MarkdownExportOptions options,
        AnnotationFile? annotationFile,
        CancellationToken ct)
    {
        var (pageW, pageH) = pdf.GetPageSize(pageIdx);

        // Run layout analysis
        var (rgbBytes, pxW, pxH) = pdf.RenderPagePixmap(pageIdx, LayoutConstants.InputSize);
        var analysis = analyzer.RunAnalysis(rgbBytes, pxW, pxH, pageW, pageH, ct);
        var blocks = analysis.Blocks;

        if (blocks.Count == 0)
        {
            // No blocks detected — fall back to plain text
            return ExportPagePlainText(pdf, pageIdx, annotationFile, options);
        }

        // Extract page text
        var pageText = PdfTextService.ExtractPageText(pdf.PdfBytes, pageIdx);

        // Resolve heading levels
        var headingLevels = HeadingLevelResolver.Resolve(blocks, pageText, pdf.Outline, pageIdx);

        // VLM transcriptions
        var vlmResults = new Dictionary<int, PageMarkdownBuilder.VlmBlockResult>();
        var figurePaths = new Dictionary<int, string>();

        if (vlmAvailable && vlmEndpoint != null)
        {
            await RunVlmForPage(
                pdf, pageIdx, pageW, pageH, blocks,
                vlmEndpoint, options, vlmResults, figurePaths, ct);
        }

        // Annotations
        var pageAnnotations = ExtractPageAnnotations(annotationFile, pageIdx);

        // Build page markdown
        var sb = new StringBuilder();
        var basicMd = PageMarkdownBuilder.Build(
            blocks, headingLevels, pageText, vlmResults, null, figurePaths);
        sb.Append(basicMd);

        // Append annotations with text extraction for highlights
        if (pageAnnotations != null)
        {
            PageMarkdownBuilder.AppendAnnotationsWithText(
                sb, pageAnnotations, pageText, pageW, pageH);
        }

        return sb.ToString();
    }

    private string ExportPagePlainText(
        IPdfService pdf,
        int pageIdx,
        AnnotationFile? annotationFile,
        MarkdownExportOptions options)
    {
        var pageText = PdfTextService.ExtractPageText(pdf.PdfBytes, pageIdx);
        var pageAnnotations = options.IncludeAnnotations
            ? ExtractPageAnnotations(annotationFile, pageIdx)
            : null;

        return PageMarkdownBuilder.BuildPlainText(
            pageText.Text, pdf.Outline, pageIdx, pageAnnotations);
    }

    private async Task RunVlmForPage(
        IPdfService pdf,
        int pageIdx,
        double pageW,
        double pageH,
        IReadOnlyList<LayoutBlock> blocks,
        VlmEndpointConfig vlmEndpoint,
        MarkdownExportOptions options,
        Dictionary<int, PageMarkdownBuilder.VlmBlockResult> vlmResults,
        Dictionary<int, string> figurePaths,
        CancellationToken ct)
    {
        // Collect blocks that need VLM
        var vlmTargets = new List<(int Index, LayoutBlock Block, VlmService.BlockAction Action)>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var action = GetVlmAction(block.ClassId);
            if (action == null) continue;
            vlmTargets.Add((i, block, action.Value));
        }

        if (vlmTargets.Count == 0) return;

        // Render all crops for this page in one batch
        var bboxes = vlmTargets.Select(t => t.Block.BBox).ToList();
        List<byte[]?> pngs;
        try
        {
            pngs = BlockCropRenderer.RenderBlocksAsPng(pdf, pageIdx, bboxes, pageW, pageH, 300);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Export] Error rendering crops for page {pageIdx + 1}: {ex.Message}");
            return;
        }

        // Save figure images if requested
        if (options.IncludeFigureImages && options.FigureOutputDir != null)
        {
            Directory.CreateDirectory(options.FigureOutputDir);
            for (int i = 0; i < vlmTargets.Count; i++)
            {
                var (idx, block, action) = vlmTargets[i];
                if (action == VlmService.BlockAction.Description && pngs[i] != null)
                {
                    var fileName = $"fig_p{pageIdx + 1}_b{idx}.png";
                    var filePath = Path.Combine(options.FigureOutputDir, fileName);
                    File.WriteAllBytes(filePath, pngs[i]!);
                    figurePaths[idx] = Path.Combine(
                        Path.GetFileName(options.FigureOutputDir), fileName);
                }
            }
        }

        // Dispatch VLM requests concurrently
        using var gate = new SemaphoreSlim(options.VlmConcurrency);
        var tasks = new List<Task>(vlmTargets.Count);

        for (int i = 0; i < vlmTargets.Count; i++)
        {
            var (idx, block, action) = vlmTargets[i];
            var png = pngs[i];

            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    VlmService.VlmResult result;
                    if (png == null)
                    {
                        result = new VlmService.VlmResult(null, "Failed to render crop");
                    }
                    else
                    {
                        result = await VlmService.DescribeBlockAsync(
                            png, action, vlmEndpoint,
                            options.VlmPromptStyle,
                            options.VlmStructuredOutput, ct);
                    }

                    lock (vlmResults)
                    {
                        vlmResults[idx] = new PageMarkdownBuilder.VlmBlockResult(
                            idx, result.Text, result.Error);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    private static VlmService.BlockAction? GetVlmAction(int classId)
    {
        if (LayoutConstants.EquationClasses.Contains(classId))
            return VlmService.BlockAction.LaTeX;
        if (LayoutConstants.TableClasses.Contains(classId))
            return VlmService.BlockAction.Markdown;
        if (LayoutConstants.FigureClasses.Contains(classId))
            return VlmService.BlockAction.Description;
        return null;
    }

    private static PageMarkdownBuilder.PageAnnotations? ExtractPageAnnotations(
        AnnotationFile? annotationFile, int pageIdx)
    {
        if (annotationFile == null) return null;
        if (!annotationFile.Pages.TryGetValue(pageIdx, out var pageAnns) || pageAnns.Count == 0)
            return null;

        var highlights = new List<HighlightAnnotation>();
        var notes = new List<TextNoteAnnotation>();

        foreach (var ann in pageAnns)
        {
            switch (ann)
            {
                case HighlightAnnotation h:
                    highlights.Add(h);
                    break;
                case TextNoteAnnotation tn:
                    notes.Add(tn);
                    break;
            }
        }

        if (highlights.Count == 0 && notes.Count == 0)
            return null;

        return new PageMarkdownBuilder.PageAnnotations(highlights, notes);
    }

    private static VlmEndpointConfig? ResolveVlmEndpoint(MarkdownExportOptions options)
    {
        if (options.VlmEndpoint != null)
            return options.VlmEndpoint;

        // Fall back to AppConfig
        var appConfig = AppConfig.Load();
        var apiKey = string.IsNullOrWhiteSpace(appConfig.VlmApiKey)
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : appConfig.VlmApiKey;

        return new VlmEndpointConfig(appConfig.VlmEndpoint, appConfig.VlmModel, apiKey);
    }
}
