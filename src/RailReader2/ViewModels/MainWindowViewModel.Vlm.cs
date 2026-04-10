using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

using static RailReader.Core.Services.VlmService;

namespace RailReader2.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async void CopyBlockAsLatex()
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;

        LayoutBlock? block = doc.Rail.HasAnalysis ? doc.Rail.CurrentNavigableBlock : null;
        if (block is null)
        {
            ShowStatusToast("No block selected");
            return;
        }

        var action = DefaultActionForBlock(block.ClassId);
        await SendBlockToVlm(doc, block, action);
    }

    public LayoutBlock? FindBlockAt(double pageX, double pageY)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return null;

        if (!doc.AnalysisCache.TryGetValue(doc.CurrentPage, out var analysis))
            return null;

        foreach (var b in analysis.Blocks)
        {
            if (pageX >= b.BBox.X && pageX <= b.BBox.X + b.BBox.W
                && pageY >= b.BBox.Y && pageY <= b.BBox.Y + b.BBox.H)
                return b;
        }
        return null;
    }

    public async void CopyBlockWithAction(LayoutBlock block, BlockAction action)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;
        await SendBlockToVlm(doc, block, action);
    }

    public async void CopyBlockAsImage(LayoutBlock block)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;

        var pngBytes = BlockCropRenderer.RenderBlockAsPng(
            doc.Pdf, doc.CurrentPage, block.BBox, doc.PageWidth, doc.PageHeight);
        if (pngBytes is null)
        {
            ShowStatusToast("Failed to render block");
            return;
        }

        if (CopyImageToClipboard is not null)
            await CopyImageToClipboard(pngBytes);
        ShowStatusToast("Image copied to clipboard!");
    }

    public Func<byte[], Task>? CopyImageToClipboard { get; set; }

    private static BlockAction DefaultActionForBlock(int classId)
    {
        if (LayoutConstants.TableClasses.Contains(classId)) return BlockAction.Markdown;
        if (LayoutConstants.FigureClasses.Contains(classId)) return BlockAction.Description;
        return BlockAction.LaTeX;
    }

    private async Task SendBlockToVlm(DocumentState doc, LayoutBlock block, BlockAction action)
    {
        if (string.IsNullOrWhiteSpace(Config.VlmEndpoint))
        {
            ShowStatusToast("VLM not configured \u2014 check Settings");
            return;
        }

        string label = action switch
        {
            BlockAction.LaTeX => "LaTeX",
            BlockAction.Markdown => "Markdown",
            _ => "description",
        };
        ShowStatusToast($"Sending to VLM ({label})...");

        var pngBytes = BlockCropRenderer.RenderBlockAsPng(
            doc.Pdf, doc.CurrentPage, block.BBox, doc.PageWidth, doc.PageHeight);
        if (pngBytes is null)
        {
            ShowStatusToast("Failed to render block");
            return;
        }

        var result = await VlmService.DescribeBlockAsync(
            pngBytes, action, Config,
            structuredOutput: Config.VlmStructuredOutput);

        if (result.Error is not null)
        {
            ShowStatusToast(result.Error);
            return;
        }

        if (result.Text is not null)
        {
            CopyToClipboard?.Invoke(result.Text);
            ShowStatusToast($"{label} copied to clipboard!");
        }
        else
        {
            ShowStatusToast("VLM returned empty result");
        }
    }
}
