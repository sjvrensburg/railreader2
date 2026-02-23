namespace RailReader2.Models;

public class RecentFileEntry
{
    public string FilePath { get; set; } = "";
    public int Page { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
}
