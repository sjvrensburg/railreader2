using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;

using static RailReader.Core.Services.VlmService;

namespace RailReader2.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Copies the current rail block via VLM, auto-selecting the action by block type.
    /// </summary>
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

    /// <summary>
    /// Finds the layout block at the given page coordinates on the current page.
    /// </summary>
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

    /// <summary>
    /// Sends a block to the VLM with a specific action chosen by the user.
    /// </summary>
    public async void CopyBlockWithAction(LayoutBlock block, BlockAction action)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;
        await SendBlockToVlm(doc, block, action);
    }

    /// <summary>
    /// Copies a block region as an image to the clipboard (no VLM needed).
    /// </summary>
    public void CopyBlockAsImage(LayoutBlock block)
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

        CopyImageToClipboard?.Invoke(pngBytes);
        ShowStatusToast("Image copied to clipboard!");
    }

    /// <summary>
    /// Callback for copying image bytes to clipboard. Set by the view.
    /// </summary>
    public Func<byte[], Task>? CopyImageToClipboard { get; set; }

    private static BlockAction DefaultActionForBlock(int classId) => classId switch
    {
        LayoutConstants.ClassTable => BlockAction.Markdown,
        LayoutConstants.ClassChart or LayoutConstants.ClassFooterImage
            or LayoutConstants.ClassHeaderImage or LayoutConstants.ClassImage
            => BlockAction.Description,
        _ => BlockAction.LaTeX,
    };

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

        var result = await VlmService.DescribeBlockAsync(pngBytes, action, Config);

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
