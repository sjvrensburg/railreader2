using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class LineDetectionTests
{
    /// <summary>
    /// Simulate a density profile for a block with N lines. Each line occupies lineHeightPx rows
    /// at the given density, with gapPx empty rows between lines.
    /// Individual lines can override density via lineDensities (sparse: index -> density).
    /// </summary>
    private static float[] MakeDensityProfile(
        int lineCount, int lineHeightPx, int gapPx, float density,
        Dictionary<int, float>? lineDensities = null)
    {
        int totalRows = lineCount * lineHeightPx + (lineCount - 1) * gapPx;
        var profile = new float[totalRows];

        for (int line = 0; line < lineCount; line++)
        {
            int start = line * (lineHeightPx + gapPx);
            float d = lineDensities?.GetValueOrDefault(line, density) ?? density;
            for (int r = 0; r < lineHeightPx; r++)
                profile[start + r] = d;
        }
        return profile;
    }

    [Fact]
    public void DetectsAllFullWidthLines()
    {
        var profile = MakeDensityProfile(5, lineHeightPx: 10, gapPx: 6, density: 0.3f);
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Equal(5, runs.Count);
    }

    [Fact]
    public void DenseTextNotMerged()
    {
        // 14 lines of dense text — should detect all lines, not merge into fewer
        var profile = MakeDensityProfile(14, lineHeightPx: 10, gapPx: 4, density: 0.35f);
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Equal(14, runs.Count);
    }

    [Fact]
    public void RecoverShortTrailingLine()
    {
        // 5 full lines + trailing short line (low density, e.g. "the latter.")
        // Primary pass misses it (0.02 < 0.3*0.15=0.045), recovery pass catches it
        var profile = MakeDensityProfile(6, lineHeightPx: 10, gapPx: 6, density: 0.3f,
            lineDensities: new() { [5] = 0.02f });
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Equal(6, runs.Count);
    }

    [Fact]
    public void RecoverShortLeadingLine()
    {
        // Short leading line + 5 full lines
        var profile = MakeDensityProfile(6, lineHeightPx: 10, gapPx: 6, density: 0.3f,
            lineDensities: new() { [0] = 0.02f });
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Equal(6, runs.Count);
    }

    [Fact]
    public void MidBlockShortLineNotRecovered()
    {
        // A short line in the middle is below the density-fraction threshold and won't
        // be recovered (recovery only targets top/bottom). This is acceptable — mid-block
        // short lines are rare in typeset text.
        var profile = MakeDensityProfile(5, lineHeightPx: 10, gapPx: 6, density: 0.3f,
            lineDensities: new() { [2] = 0.02f });
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Equal(4, runs.Count); // line #2 missed, acceptable
    }

    [Fact]
    public void FiltersNoiseBelowMinHeight()
    {
        var profile = new float[30];
        for (int i = 0; i < 8; i++) profile[i] = 0.3f;
        // 2-row noise at rows 15-16 (below MinLineHeightPx=3)
        profile[15] = 0.01f;
        profile[16] = 0.01f;
        // Line at rows 22-29
        for (int i = 22; i < 30; i++) profile[i] = 0.3f;

        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void EmptyProfileReturnsNoLines()
    {
        var profile = new float[50];
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Empty(runs);
    }

    [Fact]
    public void SingleShortLineDetected()
    {
        // Block contains only one short line with low density — primary pass
        // uses 0.005 fallback threshold since no non-zero rows exceed it
        var profile = new float[20];
        for (int i = 5; i < 15; i++) profile[i] = 0.015f;
        var runs = LayoutAnalyzer.FindLineRuns(profile);
        Assert.Single(runs);
    }
}
