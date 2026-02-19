namespace RailReader2.Models;

public sealed class LayoutBlock
{
    public BBox BBox { get; set; }
    public int ClassId { get; set; }
    public float Confidence { get; set; }
    public int Order { get; set; }
    public List<LineInfo> Lines { get; set; } = [];
}
