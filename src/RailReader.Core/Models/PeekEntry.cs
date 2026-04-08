namespace RailReader.Core.Models;

/// <summary>
/// A single figure, table, or equation detected by layout analysis.
/// </summary>
public sealed class PeekEntry
{
    public required int PageIndex { get; init; }
    public required int BlockIndex { get; init; }
    public required int ClassId { get; init; }
    public required BBox BBox { get; init; }
    public required float Confidence { get; init; }
}

/// <summary>Category grouping for peek entries.</summary>
public enum PeekCategory { Figures, Tables, Equations }

/// <summary>
/// Index of all detected figures, tables, and equations across scanned pages.
/// </summary>
public sealed class PeekIndex
{
    public IReadOnlyList<PeekEntry> Figures { get; }
    public IReadOnlyList<PeekEntry> Tables { get; }
    public IReadOnlyList<PeekEntry> Equations { get; }
    public int ScannedPages { get; }
    public int TotalPages { get; }

    public PeekIndex(IReadOnlyList<PeekEntry> figures, IReadOnlyList<PeekEntry> tables,
        IReadOnlyList<PeekEntry> equations, int scannedPages, int totalPages)
    {
        Figures = figures;
        Tables = tables;
        Equations = equations;
        ScannedPages = scannedPages;
        TotalPages = totalPages;
    }
}
