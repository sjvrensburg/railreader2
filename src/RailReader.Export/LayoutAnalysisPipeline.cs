using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Export;

/// <summary>
/// Convenience wrapper around the full layout-analysis pipeline for consumers
/// that bypass <see cref="AnalysisWorker"/> (CLI commands, the export library).
/// Mirrors the worker's behaviour: detection → reading-order assignment →
/// overlap resolution + line detection.
/// </summary>
public static class LayoutAnalysisPipeline
{
    /// <summary>
    /// Rasterises the page (at the analyzer's required input size), extracts text,
    /// runs detection, assigns reading order, then post-processes for overlaps + lines.
    /// The resolver defaults to <see cref="ModelOrderResolver"/> when the analyzer
    /// provides reading order, otherwise <see cref="TopDownReadingOrderResolver"/>.
    /// </summary>
    public static PageAnalysis Run(
        ILayoutAnalyzer analyzer,
        IPdfService pdf,
        IPdfTextService pdfText,
        int pageIndex,
        IReadingOrderResolver? resolver = null,
        CancellationToken ct = default)
    {
        var (pageW, pageH) = pdf.GetPageSize(pageIndex);
        var (rgbBytes, pxW, pxH) = pdf.RenderPagePixmap(pageIndex, analyzer.Capabilities.InputSize);
        var pageText = pdfText.ExtractPageText(pdf.PdfBytes, pageIndex);

        return RunWithPixmap(analyzer, rgbBytes, pxW, pxH, pageW, pageH, pageText.CharBoxes, resolver, ct);
    }

    /// <summary>
    /// Same as <see cref="Run"/> but lets the caller supply a pre-rendered pixmap
    /// and char boxes. Used when those are already available (e.g. inside the export
    /// loop, which extracts text separately).
    /// </summary>
    public static PageAnalysis RunWithPixmap(
        ILayoutAnalyzer analyzer,
        byte[] rgbBytes, int pxW, int pxH,
        double pageW, double pageH,
        IReadOnlyList<CharBox>? charBoxes,
        IReadingOrderResolver? resolver = null,
        CancellationToken ct = default)
    {
        var analysis = analyzer.RunAnalysis(rgbBytes, pxW, pxH, pageW, pageH, charBoxes, ct);

        resolver ??= analyzer.Capabilities.ProvidesReadingOrder
            ? new ModelOrderResolver()
            : new TopDownReadingOrderResolver();
        resolver.AssignOrder(analysis.Blocks, analysis.PageWidth, analysis.PageHeight);

        float mapScaleX = pxW > 0 ? (float)(pageW / pxW) : 1f;
        float mapScaleY = pxH > 0 ? (float)(pageH / pxH) : 1f;
        BlockPostProcessor.PostProcess(analysis.Blocks, rgbBytes, pxW, pxH, mapScaleX, mapScaleY, charBoxes);

        return analysis;
    }
}
