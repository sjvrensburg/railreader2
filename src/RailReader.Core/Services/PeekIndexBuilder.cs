using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Scans the analysis cache and builds an index of all detected
/// figures, tables, and equations, sorted by page then reading order.
/// </summary>
public static class PeekIndexBuilder
{
    private static readonly HashSet<int> FigureClasses = [3, 9, 13, 14]; // chart, footer_image, header_image, image
    private static readonly HashSet<int> TableClasses = [21];            // table
    private static readonly HashSet<int> EquationClasses = [5];          // display_formula

    public static PeekIndex Build(IReadOnlyDictionary<int, PageAnalysis> cache, int pageCount)
    {
        var figures = new List<PeekEntry>();
        var tables = new List<PeekEntry>();
        var equations = new List<PeekEntry>();

        for (int page = 0; page < pageCount; page++)
        {
            if (!cache.TryGetValue(page, out var analysis)) continue;

            for (int b = 0; b < analysis.Blocks.Count; b++)
            {
                var block = analysis.Blocks[b];
                PeekEntry? entry = null;

                if (FigureClasses.Contains(block.ClassId))
                {
                    entry = MakeEntry(page, b, block);
                    figures.Add(entry);
                }
                else if (TableClasses.Contains(block.ClassId))
                {
                    entry = MakeEntry(page, b, block);
                    tables.Add(entry);
                }
                else if (EquationClasses.Contains(block.ClassId))
                {
                    entry = MakeEntry(page, b, block);
                    equations.Add(entry);
                }
            }
        }

        return new PeekIndex(figures, tables, equations, cache.Count, pageCount);
    }

    private static PeekEntry MakeEntry(int page, int blockIndex, LayoutBlock block) =>
        new()
        {
            PageIndex = page,
            BlockIndex = blockIndex,
            ClassId = block.ClassId,
            BBox = block.BBox,
            Confidence = block.Confidence,
        };
}
