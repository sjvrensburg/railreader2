namespace RailReader2.Models;

public sealed class OutlineEntry
{
    public string Title { get; set; } = "";
    public int? Page { get; set; }
    public List<OutlineEntry> Children { get; set; } = [];
}
