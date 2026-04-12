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
        var pdfService = _factory.CreatePdfService(pdfPath);
        var (pages, err) = PageRangeParser.Parse(options.PageRange, pdfService.PageCount);
        if (err != null)
            throw new ArgumentException(err);

        var modelPath = DocumentController.FindModelPath();
        using var analyzer = modelPath != null ? new LayoutAnalyzer(modelPath) : null;

        AnnotationFile? annotationFile = null;
        if (options.IncludeAnnotations)
            annotationFile = AnnotationService.Load(pdfPath);

        var vlmEndpoint = options.VlmEndpoint;
        bool vlmAvailable = options.EnableVlm && vlmEndpoint != null
            && !string.IsNullOrWhiteSpace(vlmEndpoint.Endpoint)
            && !string.IsNullOrWhiteSpace(vlmEndpoint.Model);

        // Flatten outline once for the whole document
        var flatOutline = HeadingLevelResolver.FlattenOutline(pdfService.Outline);

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
                    pdfService, pageIdx, analyzer, flatOutline,
                    vlmAvailable ? vlmEndpoint : null, options,
                    annotationFile, ct);
            }
            else
            {
                pageMd = ExportPagePlainText(
                    pdfService, pageIdx, flatOutline, annotationFile, options);
            }

            if (string.IsNullOrWhiteSpace(pageMd))
                continue;

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
        IReadOnlyList<HeadingLevelResolver.FlatOutlineEntry> flatOutline,
        VlmEndpointConfig? vlmEndpoint,
        MarkdownExportOptions options,
        AnnotationFile? annotationFile,
        CancellationToken ct)
    {
        var (pageW, pageH) = pdf.GetPageSize(pageIdx);

        var (rgbBytes, pxW, pxH) = pdf.RenderPagePixmap(pageIdx, LayoutConstants.InputSize);
        var analysis = analyzer.RunAnalysis(rgbBytes, pxW, pxH, pageW, pageH, ct);
        var blocks = analysis.Blocks;

        if (blocks.Count == 0)
            return ExportPagePlainText(pdf, pageIdx, flatOutline, annotationFile, options);

        var pageText = PdfTextService.ExtractPageText(pdf.PdfBytes, pageIdx);

        // Cache text extraction per block to avoid redundant O(chars) scans
        var blockTexts = new Dictionary<int, string>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var text = pageText.ExtractBlockText(blocks[i]);
            if (!string.IsNullOrEmpty(text))
                blockTexts[i] = text;
        }

        var headingLevels = HeadingLevelResolver.ResolveWithFlatOutline(
            blocks, blockTexts, flatOutline, pageIdx);

        var vlmResults = new Dictionary<int, PageMarkdownBuilder.VlmBlockResult>();
        var figurePaths = new Dictionary<int, string>();

        if (vlmEndpoint != null)
        {
            await RunVlmForPage(
                pdf, pageIdx, pageW, pageH, blocks,
                vlmEndpoint, options, vlmResults, figurePaths, ct);
        }

        var pageAnnotations = ExtractPageAnnotations(annotationFile, pageIdx);

        var md = PageMarkdownBuilder.Build(
            blocks, headingLevels, blockTexts, vlmResults, figurePaths);

        if (pageAnnotations != null)
        {
            var sb = new StringBuilder(md);
            PageMarkdownBuilder.AppendAnnotations(sb, pageAnnotations, pageText);
            return sb.ToString();
        }

        return md;
    }

    private string ExportPagePlainText(
        IPdfService pdf,
        int pageIdx,
        IReadOnlyList<HeadingLevelResolver.FlatOutlineEntry> flatOutline,
        AnnotationFile? annotationFile,
        MarkdownExportOptions options)
    {
        var pageText = PdfTextService.ExtractPageText(pdf.PdfBytes, pageIdx);
        var pageAnnotations = options.IncludeAnnotations
            ? ExtractPageAnnotations(annotationFile, pageIdx)
            : null;

        return PageMarkdownBuilder.BuildPlainText(
            pageText, flatOutline, pageIdx, pageAnnotations);
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
        var vlmTargets = new List<(int Index, LayoutBlock Block, VlmService.BlockAction Action)>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var action = VlmService.GetBlockAction(blocks[i].ClassId);
            if (action == null) continue;
            vlmTargets.Add((i, blocks[i], action.Value));
        }

        if (vlmTargets.Count == 0) return;

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

        if (options.IncludeFigureImages && options.FigureOutputDir != null)
        {
            Directory.CreateDirectory(options.FigureOutputDir);
            for (int i = 0; i < vlmTargets.Count; i++)
            {
                var (idx, _, action) = vlmTargets[i];
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

        using var gate = new SemaphoreSlim(options.VlmConcurrency);
        var tasks = new List<Task>(vlmTargets.Count);

        for (int i = 0; i < vlmTargets.Count; i++)
        {
            var (idx, _, action) = vlmTargets[i];
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
}
