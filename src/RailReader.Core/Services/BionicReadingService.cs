using RailReader.Core.Models;
using SkiaSharp;

namespace RailReader.Core.Services;

public static class BionicReadingService
{
    /// <summary>
    /// Computes rectangles covering the non-fixation (faded) portion of each word.
    /// Words are letter/digit runs. For each word, the first ceil(length * fixationPercent)
    /// characters (minimum 1) are kept at full contrast; the rest are returned as fade rects.
    /// Adjacent same-line rects are merged.
    /// </summary>
    public static List<SKRect> ComputeFadeRects(PageText pageText, double fixationPercent)
    {
        var fadeBoxes = new List<CharBox>();
        var text = pageText.Text;
        var boxes = pageText.CharBoxes;
        int len = text.Length;
        int i = 0;

        while (i < len)
        {
            // Skip non-word characters
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            // Found start of a word
            int wordStart = i;
            while (i < len && char.IsLetterOrDigit(text[i]))
                i++;
            int wordLen = i - wordStart;

            // Single-char words: nothing to fade
            if (wordLen <= 1)
                continue;

            int fixationCount = Math.Max(1, (int)Math.Ceiling(wordLen * fixationPercent));
            int fadeStart = wordStart + fixationCount;

            for (int j = fadeStart; j < wordStart + wordLen && j < boxes.Count; j++)
            {
                var cb = boxes[j];
                // Skip zero-area boxes (whitespace placeholders)
                if (cb.Left == 0 && cb.Right == 0 && cb.Top == 0 && cb.Bottom == 0)
                    continue;
                fadeBoxes.Add(cb);
            }
        }

        return MergeAdjacentRects(fadeBoxes);
    }

    /// <summary>
    /// Merges only horizontally adjacent boxes (within the same word's fade run).
    /// A gap between boxes (e.g. fixation portion of the next word) starts a new rect.
    /// </summary>
    private static List<SKRect> MergeAdjacentRects(List<CharBox> boxes)
    {
        var result = new List<SKRect>();
        if (boxes.Count == 0) return result;

        const float lineThreshold = 4f;
        // Max horizontal gap before starting a new rect — prevents merging
        // across fixation portions of adjacent words.
        const float gapThreshold = 2f;

        SKRect? current = null;
        foreach (var cb in boxes)
        {
            var r = new SKRect(cb.Left, cb.Top, cb.Right, cb.Bottom);
            if (current is not { } cur)
            {
                current = r;
            }
            else if (Math.Abs(cb.Top - cur.Top) < lineThreshold && cb.Left - cur.Right < gapThreshold)
            {
                // Same line AND horizontally adjacent — extend
                current = new SKRect(cur.Left, Math.Min(cur.Top, r.Top),
                    Math.Max(cur.Right, r.Right), Math.Max(cur.Bottom, r.Bottom));
            }
            else
            {
                // Different line or horizontal gap — emit and start new
                result.Add(cur);
                current = r;
            }
        }

        if (current is { } last)
            result.Add(last);

        return result;
    }
}
