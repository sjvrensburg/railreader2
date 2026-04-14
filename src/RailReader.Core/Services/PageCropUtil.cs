using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// Fractional (0..1) rectangle describing where content sits within a page.
/// Orientation: X/Y are the top-left corner, W/H extend toward the bottom-right.
/// </summary>
public readonly record struct ContentFraction(double X, double Y, double W, double H)
{
    public static ContentFraction Full => new(0.0, 0.0, 1.0, 1.0);

    /// <summary>Union two fractions: the tighter of the two left/top and looser of right/bottom.</summary>
    public ContentFraction Union(ContentFraction other)
    {
        double x = Math.Min(X, other.X);
        double y = Math.Min(Y, other.Y);
        double r = Math.Max(X + W, other.X + other.W);
        double b = Math.Max(Y + H, other.Y + other.H);
        return new ContentFraction(x, y, r - x, b - y);
    }
}

public static class PageCropUtil
{
    /// <summary>
    /// Computes the bounding fraction of the union of all layout blocks on a page.
    /// Returns <see cref="ContentFraction.Full"/> if the page has no analysed blocks
    /// or zero dimensions.
    /// </summary>
    public static ContentFraction ComputeFraction(PageAnalysis analysis)
    {
        if (analysis.Blocks.Count == 0
            || analysis.PageWidth <= 0 || analysis.PageHeight <= 0)
            return ContentFraction.Full;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var b in analysis.Blocks)
        {
            if (b.BBox.X < minX) minX = b.BBox.X;
            if (b.BBox.Y < minY) minY = b.BBox.Y;
            float r = b.BBox.X + b.BBox.W;
            float bot = b.BBox.Y + b.BBox.H;
            if (r > maxX) maxX = r;
            if (bot > maxY) maxY = bot;
        }

        double pw = analysis.PageWidth;
        double ph = analysis.PageHeight;
        double x = Math.Clamp(minX / pw, 0.0, 1.0);
        double y = Math.Clamp(minY / ph, 0.0, 1.0);
        double w = Math.Clamp((maxX - minX) / pw, 0.0, 1.0 - x);
        double h = Math.Clamp((maxY - minY) / ph, 0.0, 1.0 - y);
        return new ContentFraction(x, y, w, h);
    }
}
