using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Core.Vlm.OpenAI;
using RailReader.Renderer.Skia;

using static RailReader.Core.Services.VlmService;

namespace RailReader2.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>The active document plus its seated navigable rail block — what the block-copy entry
    /// points (keyboard, context menu, Edit menu) operate on. Null when there is no open document
    /// (silent), or with a "No block selected" toast when a document is open but no block is seated.</summary>
    private (DocumentModel Doc, Viewport Vp, LayoutBlock Block)? CurrentRailBlockOrToast()
    {
        // The focused viewport's rail (a split pane / tear-off can be rail-reading a different
        // block/page than the primary), not the document's Primary facade.
        if (_controller.FocusedViewport?.Owner is not { } doc || _controller.FocusedViewport is not { } vp) return null;
        if (!vp.Rail.HasAnalysis || vp.Rail.CurrentNavigableBlock is not { } block)
        {
            ShowStatusToast("No block selected");
            return null;
        }
        return (doc, vp, block);
    }

    public async Task CopyBlockAsLatex()
    {
        if (CurrentRailBlockOrToast() is not ({ } doc, { } vp, { } block)) return;
        await SendBlockToVlm(doc, vp, block, DefaultActionForBlock(block.Role));
    }

    /// <summary>Copy the current rail block via a specific VLM action (Edit-menu / agent entry
    /// point — unlike <see cref="CopyBlockAsLatex"/>, which uses the block's default action).</summary>
    public async Task CopyCurrentBlock(BlockAction action)
    {
        if (CurrentRailBlockOrToast() is not ({ } doc, { } vp, { } block)) return;
        await SendBlockToVlm(doc, vp, block, action);
    }

    /// <summary>Copy the current rail block as a PNG image (Edit-menu / agent entry point).</summary>
    public async Task CopyCurrentBlockAsImage()
    {
        if (CurrentRailBlockOrToast() is not (_, _, { } block)) return;
        await CopyBlockAsImage(block);
    }

    /// <summary>A detected block on the focused view's page, with its page-block index — the value
    /// <see cref="SmoothlyFrameBlock"/> and the portal actions take (Core's <c>analysis.Blocks</c>
    /// index space).</summary>
    public readonly record struct BlockHit(int Index, LayoutBlock Block);

    /// <summary>The detected block at the given page-space point on the focused view's page, or null
    /// when there is none (or no analysis). One analysis lookup gives both the block and its index,
    /// so callers needing either or both hit-test once. Where block boxes overlap, the first in
    /// analysis order wins — unless <paramref name="preferNavigable"/> is set, in which case the first
    /// navigable block (one the rail can seat — what Core's own click seating picks from) under the
    /// point wins over a figure/table whose box also covers it.</summary>
    public BlockHit? FindBlockAt(double pageX, double pageY, bool preferNavigable = false)
    {
        var vp = _controller.FocusedViewport;
        if (vp is null || !vp.Owner.TryGetAnalysis(vp.CurrentPage, vp.AnalysisParams, out var analysis))
            return null;

        BlockHit? first = null;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var b = analysis.Blocks[i];
            if (pageX < b.BBox.X || pageX > b.BBox.X + b.BBox.W
                || pageY < b.BBox.Y || pageY > b.BBox.Y + b.BBox.H)
                continue;
            if (!preferNavigable || IsNavigableBlock(b)) return new BlockHit(i, b);
            first ??= new BlockHit(i, b);
        }
        return first;
    }

    /// <summary>True when <paramref name="block"/> has a navigable role — one the rail can seat
    /// (text, headings, …) rather than a figure/table/chart, which framing centres geometrically
    /// instead.</summary>
    public bool IsNavigableBlock(LayoutBlock block)
        => _controller.Config.NavigableRoles.Contains(block.Role);

    public async Task CopyBlockWithAction(LayoutBlock block, BlockAction action)
    {
        var doc = _controller.FocusedViewport?.Owner;
        var vp = _controller.FocusedViewport;
        if (doc is null || vp is null) return;
        await SendBlockToVlm(doc, vp, block, action);
    }

    public async Task CopyBlockAsImage(LayoutBlock block)
    {
        var doc = _controller.FocusedViewport?.Owner;
        var vp = _controller.FocusedViewport;
        if (doc is null || vp is null) return;

        var pngBytes = BlockCropRenderer.RenderBlockAsPng(
            doc.Pdf, vp.CurrentPage, block.BBox, vp.PageWidth, vp.PageHeight);
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

    private async Task SendBlockToVlm(DocumentModel doc, Viewport vp, LayoutBlock block, BlockAction action)
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
            doc.Pdf, vp.CurrentPage, block.BBox, vp.PageWidth, vp.PageHeight);
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
