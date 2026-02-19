namespace RailReader2.Models;

public sealed class PageAnalysis
{
    public List<LayoutBlock> Blocks { get; set; } = [];
    public double PageWidth { get; set; }
    public double PageHeight { get; set; }
}
