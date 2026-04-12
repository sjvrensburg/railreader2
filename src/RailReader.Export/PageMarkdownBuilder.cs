using System.Text;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReader.Export;

/// <summary>
/// Assembles Markdown output for a single page from classified layout blocks,
/// resolved heading levels, extracted text, VLM results, and annotations.
/// </summary>
public static class PageMarkdownBuilder
{
    public record VlmBlockResult(int BlockIndex, string? Text, string? Error);

    public record PageAnnotations(
        List<HighlightAnnotation> Highlights,
        List<TextNoteAnnotation> Notes);

    /// <summary>
    /// Builds Markdown for a single page from layout blocks.
    /// </summary>
    public static string Build(
        IReadOnlyList<LayoutBlock> blocks,
        IReadOnlyDictionary<int, int> headingLevels,
        IReadOnlyDictionary<int, string> blockTexts,
        IReadOnlyDictionary<int, VlmBlockResult>? vlmResults,
        IReadOnlyDictionary<int, string>? figurePaths)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < blocks.Count; i++)
        {
            var className = LayoutConstants.GetClassName(blocks[i].ClassId);
            var vlm = vlmResults?.GetValueOrDefault(i);
            blockTexts.TryGetValue(i, out var text);

            var blockMd = RenderBlock(className, i, text, headingLevels, vlm, figurePaths);
            if (blockMd != null)
            {
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(blockMd);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds plain-text Markdown for a page with no layout analysis.
    /// Uses outline entries at page boundaries for heading markers.
    /// </summary>
    public static string BuildPlainText(
        PageText pageText,
        IReadOnlyList<HeadingLevelResolver.FlatOutlineEntry> flatOutline,
        int pageIndex,
        PageAnnotations? annotations)
    {
        var sb = new StringBuilder();

        var pageHeadings = flatOutline
            .Where(e => e.Page == pageIndex)
            .ToList();

        foreach (var heading in pageHeadings)
        {
            var prefix = new string('#', Math.Clamp(heading.Depth, 1, 6));
            sb.AppendLine($"{prefix} {heading.Title}");
            sb.AppendLine();
        }

        var trimmed = pageText.Text.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            sb.AppendLine(trimmed);
            sb.AppendLine();
        }

        if (annotations != null)
            AppendAnnotations(sb, annotations, pageText);

        return sb.ToString();
    }

    /// <summary>
    /// Appends annotation blockquotes. When pageText is provided, highlights
    /// include the extracted highlighted text; otherwise a generic colour marker.
    /// </summary>
    internal static void AppendAnnotations(StringBuilder sb, PageAnnotations annotations, PageText? pageText = null)
    {
        if (annotations.Highlights.Count == 0 && annotations.Notes.Count == 0)
            return;

        sb.AppendLine();

        foreach (var highlight in annotations.Highlights)
        {
            string? highlightedText = null;
            if (pageText != null && highlight.Rects.Count > 0)
            {
                var texts = new List<string>();
                foreach (var rect in highlight.Rects)
                {
                    var t = pageText.ExtractTextInRect(rect.X, rect.Y, rect.X + rect.W, rect.Y + rect.H);
                    if (t != null) texts.Add(t);
                }
                if (texts.Count > 0)
                    highlightedText = string.Join(" ", texts);
            }

            if (highlightedText != null)
            {
                sb.AppendLine($"> {highlightedText}");
                sb.AppendLine($"<!-- highlight: {highlight.Color} -->");
            }
            else
            {
                sb.AppendLine($"> [highlight: {highlight.Color}]");
            }
            sb.AppendLine();
        }

        foreach (var note in annotations.Notes)
        {
            if (!string.IsNullOrWhiteSpace(note.Text))
            {
                sb.AppendLine($"> **Note:** {note.Text.Trim()}");
                sb.AppendLine();
            }
        }
    }

    private static string? RenderBlock(
        string className,
        int blockIndex,
        string? text,
        IReadOnlyDictionary<int, int> headingLevels,
        VlmBlockResult? vlm,
        IReadOnlyDictionary<int, string>? figurePaths)
    {
        return className switch
        {
            "doc_title" or "paragraph_title" => RenderHeading(blockIndex, text, headingLevels),
            "text" or "abstract" or "content" or "reference" or "reference_content"
                or "footnote" or "aside_text" or "vertical_text" => RenderTextBlock(text),
            "display_formula" or "inline_formula" or "algorithm" => RenderEquation(vlm, text),
            "formula_number" => null,
            "table" => RenderTable(vlm, text),
            "image" or "chart" or "footer_image" or "header_image" => RenderFigure(blockIndex, vlm, figurePaths),
            "figure_title" => RenderFigureTitle(text),
            "header" or "footer" or "number" or "seal" => null,
            _ => RenderTextBlock(text),
        };
    }

    private static string? RenderHeading(int blockIndex, string? text, IReadOnlyDictionary<int, int> headingLevels)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        int level = headingLevels.GetValueOrDefault(blockIndex, 2);
        var prefix = new string('#', level);
        return $"{prefix} {text.Trim()}";
    }

    private static string? RenderTextBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Trim();
    }

    private static string RenderEquation(VlmBlockResult? vlm, string? fallbackText)
    {
        if (vlm?.Text != null)
            return $"$${vlm.Text.Trim()}$$";

        if (!string.IsNullOrWhiteSpace(fallbackText))
            return $"[equation: {fallbackText.Trim()}]";

        return "[equation]";
    }

    private static string RenderTable(VlmBlockResult? vlm, string? fallbackText)
    {
        if (vlm?.Text != null)
            return vlm.Text.Trim();

        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            var sb = new StringBuilder();
            sb.AppendLine("```");
            sb.AppendLine(fallbackText.Trim());
            sb.Append("```");
            return sb.ToString();
        }

        return "[table]";
    }

    private static string RenderFigure(int blockIndex, VlmBlockResult? vlm, IReadOnlyDictionary<int, string>? figurePaths)
    {
        var path = figurePaths?.GetValueOrDefault(blockIndex);
        var description = vlm?.Text?.Trim();

        if (path != null)
            return $"![{description ?? "figure"}]({path})";

        if (description != null)
            return $"[figure: {description}]";

        return "[figure]";
    }

    private static string? RenderFigureTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return $"*{text.Trim()}*";
    }
}
