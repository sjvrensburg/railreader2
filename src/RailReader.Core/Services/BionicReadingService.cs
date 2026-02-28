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

        return MergeIntoLineRects(fadeBoxes);
    }

    private static List<SKRect> MergeIntoLineRects(List<CharBox> boxes)
    {
        var result = new List<SKRect>();
        if (boxes.Count == 0) return result;

        const float lineThreshold = 4f;
        float curLeft = 0, curTop = 0, curRight = 0, curBottom = 0;
        bool hasRect = false;

        foreach (var cb in boxes)
        {
            if (!hasRect)
            {
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
                hasRect = true;
            }
            else if (Math.Abs(cb.Top - curTop) < lineThreshold)
            {
                curLeft = Math.Min(curLeft, cb.Left);
                curRight = Math.Max(curRight, cb.Right);
                curTop = Math.Min(curTop, cb.Top);
                curBottom = Math.Max(curBottom, cb.Bottom);
            }
            else
            {
                result.Add(new SKRect(curLeft, curTop, curRight, curBottom));
                curLeft = cb.Left; curTop = cb.Top; curRight = cb.Right; curBottom = cb.Bottom;
            }
        }

        if (hasRect)
            result.Add(new SKRect(curLeft, curTop, curRight, curBottom));

        return result;
    }
}
