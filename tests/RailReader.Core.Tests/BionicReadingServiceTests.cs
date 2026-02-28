using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;
using Xunit;

namespace RailReader.Core.Tests;

public class BionicReadingServiceTests
{
    private static PageText MakePageText(string text, List<CharBox> boxes) => new(text, boxes);

    private static List<CharBox> MakeBoxes(string text, float charWidth = 10f, float height = 12f, float top = 0f)
    {
        var boxes = new List<CharBox>();
        float x = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                // Whitespace: zero-area box
                boxes.Add(new CharBox(i, 0, 0, 0, 0));
            }
            else
            {
                boxes.Add(new CharBox(i, x, top, x + charWidth, top + height));
            }
            x += charWidth;
        }
        return boxes;
    }

    [Fact]
    public void Hello_At40Percent_FadesLastThreeChars()
    {
        var text = "Hello";
        var boxes = MakeBoxes(text);
        var pageText = MakePageText(text, boxes);

        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);

        // fixation = ceil(5 * 0.4) = 2 → "He" kept, "llo" faded
        Assert.Single(rects);
        // "llo" starts at index 2 (x=20), ends at index 4 (x=50)
        Assert.Equal(20f, rects[0].Left);
        Assert.Equal(50f, rects[0].Right);
    }

    [Fact]
    public void SingleCharWord_NoFadeRects()
    {
        var text = "I a";
        var boxes = MakeBoxes(text);
        var pageText = MakePageText(text, boxes);

        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);

        Assert.Empty(rects);
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        var pageText = MakePageText("", []);
        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);
        Assert.Empty(rects);
    }

    [Fact]
    public void WhitespaceOnly_ReturnsEmpty()
    {
        var text = "   ";
        var boxes = MakeBoxes(text);
        var pageText = MakePageText(text, boxes);

        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);
        Assert.Empty(rects);
    }

    [Fact]
    public void MultipleWords_SeparateRectsPerWord()
    {
        // "Hi there" — two words on same line
        var text = "Hi there";
        var boxes = MakeBoxes(text);
        var pageText = MakePageText(text, boxes);

        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);

        // "Hi": fixation=1 ("H"), fade="i" (index 1, x=10..20)
        // "there": fixation=ceil(5*0.4)=2 ("th"), fade="ere" (indices 5,6,7, x=50..80)
        // Different words have a gap → separate rects preserving fixation portions
        Assert.Equal(2, rects.Count);
        Assert.Equal(10f, rects[0].Left);   // "i"
        Assert.Equal(20f, rects[0].Right);
        Assert.Equal(50f, rects[1].Left);   // "ere"
        Assert.Equal(80f, rects[1].Right);
    }

    [Fact]
    public void HighFixation_KeepsMostOfWord()
    {
        var text = "Hello";
        var boxes = MakeBoxes(text);
        var pageText = MakePageText(text, boxes);

        // 80% fixation: ceil(5*0.8) = 4 → only last char faded
        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.8);

        Assert.Single(rects);
        // Only "o" (index 4, x=40..50) is faded
        Assert.Equal(40f, rects[0].Left);
        Assert.Equal(50f, rects[0].Right);
    }

    [Fact]
    public void TwoCharWord_At40Percent_FadesOneChar()
    {
        var text = "Hi";
        var boxes = MakeBoxes(text);
        var pageText = MakePageText(text, boxes);

        // fixation = ceil(2*0.4) = 1 → "H" kept, "i" faded
        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);

        Assert.Single(rects);
        Assert.Equal(10f, rects[0].Left);
        Assert.Equal(20f, rects[0].Right);
    }

    [Fact]
    public void DifferentLines_ProducesSeparateRects()
    {
        var text = "AB CD";
        var boxes = new List<CharBox>
        {
            new(0, 0, 0, 10, 12),    // A — line 1
            new(1, 10, 0, 20, 12),   // B — line 1
            new(2, 0, 0, 0, 0),      // space
            new(3, 0, 50, 10, 62),   // C — line 2 (different Y)
            new(4, 10, 50, 20, 62),  // D — line 2
        };
        var pageText = MakePageText(text, boxes);

        var rects = BionicReadingService.ComputeFadeRects(pageText, 0.4);

        // "AB": fixation=1, fade="B" (line 1)
        // "CD": fixation=1, fade="D" (line 2)
        // Different lines → two separate rects
        Assert.Equal(2, rects.Count);
        Assert.Equal(0f, rects[0].Top);   // line 1
        Assert.Equal(50f, rects[1].Top);  // line 2
    }
}
