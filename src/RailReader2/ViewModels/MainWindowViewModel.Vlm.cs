using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Core.Vlm.OpenAI;
using RailReader.Renderer.Skia;

using static RailReader.Core.Services.VlmService;

namespace RailReader2.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>The current rail block (the seated navigable block copy actions operate on), or
    /// null — with a "No block selected" toast — when nothing is seated. Shared by the block-copy
    /// entry points (keyboard, context menu, and the Edit menu).</summary>
    private LayoutBlock? CurrentRailBlockOrToast()
    {
        var doc = _controller.ActiveDocument;
        LayoutBlock? block = doc?.Rail.HasAnalysis == true ? doc.Rail.CurrentNavigableBlock : null;
        if (block is null)
            ShowStatusToast("No block selected");
        return block;
    }

    public async Task CopyBlockAsLatex()
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;
        if (CurrentRailBlockOrToast() is not { } block) return;
        await SendBlockToVlm(doc, block, DefaultActionForBlock(block.Role));
    }

    /// <summary>Copy the current rail block via a specific VLM action (Edit-menu / agent entry
    /// point — unlike <see cref="CopyBlockAsLatex"/>, which uses the block's default action).</summary>
    public async Task CopyCurrentBlock(BlockAction action)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;
        if (CurrentRailBlockOrToast() is not { } block) return;
        await SendBlockToVlm(doc, block, action);
    }

    /// <summary>Copy the current rail block as a PNG image (Edit-menu / agent entry point).</summary>
    public async Task CopyCurrentBlockAsImage()
    {
        if (CurrentRailBlockOrToast() is not { } block) return;
        await CopyBlockAsImage(block);
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

    /// <summary>Page-block index — the value <see cref="SmoothlyFrameBlock"/> takes — of the
    /// detected block at the given page-space point, or -1 if none. Shares its index space with
    /// <see cref="FindBlockAt"/> and Core's <c>analysis.Blocks</c>.</summary>
    public int FindBlockIndexAt(double pageX, double pageY)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return -1;

        if (!doc.AnalysisCache.TryGetValue(doc.CurrentPage, out var analysis))
            return -1;

        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var b = analysis.Blocks[i];
            if (pageX >= b.BBox.X && pageX <= b.BBox.X + b.BBox.W
                && pageY >= b.BBox.Y && pageY <= b.BBox.Y + b.BBox.H)
                return i;
        }
        return -1;
    }

    public async Task CopyBlockWithAction(LayoutBlock block, BlockAction action)
    {
        var doc = _controller.ActiveDocument;
        if (doc is null) return;
        await SendBlockToVlm(doc, block, action);
    }

    public async Task CopyBlockAsImage(LayoutBlock block)
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

    private static BlockAction DefaultActionForBlock(BlockRole role) =>
        VlmService.GetBlockAction(role) ?? BlockAction.LaTeX;

    private async Task SendBlockToVlm(DocumentState doc, LayoutBlock block, BlockAction action)
    {
        if (string.IsNullOrWhiteSpace(AppConfig.VlmEndpoint))
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

        IVlmService vlm = new OpenAIVlmClient();
        var result = await vlm.DescribeBlockAsync(
            pngBytes, action, VlmEndpointConfig.FromCoreSettings(AppConfig.ToCoreSettings()),
            structuredOutput: AppConfig.VlmStructuredOutput);

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
