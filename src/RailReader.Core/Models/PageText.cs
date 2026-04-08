namespace RailReader.Core.Models;

public record PageText(string Text, List<CharBox> CharBoxes)
{
    /// <summary>
    /// Extracts text whose character midpoints fall within the given rectangle.
    /// Returns null if no characters match.
    /// </summary>
    public string? ExtractTextInRect(float left, float top, float right, float bottom)
    {
        var chars = new List<(int Index, char Ch)>();
        foreach (var cb in CharBoxes)
        {
            float midX = (cb.Left + cb.Right) / 2f;
            float midY = (cb.Top + cb.Bottom) / 2f;
            if (midX >= left && midX <= right && midY >= top && midY <= bottom
                && cb.Index >= 0 && cb.Index < Text.Length)
            {
                chars.Add((cb.Index, Text[cb.Index]));
            }
        }
        if (chars.Count == 0) return null;
        chars.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new string(chars.Select(c => c.Ch).ToArray()).Trim();
    }

    /// <summary>
    /// Extracts text within a layout block's bounding box.
    /// </summary>
    public string ExtractBlockText(LayoutBlock block)
    {
        var bbox = block.BBox;
        return ExtractTextInRect(bbox.X, bbox.Y, bbox.X + bbox.W, bbox.Y + bbox.H) ?? "";
    }
}

public record struct CharBox(int Index, float Left, float Top, float Right, float Bottom);
