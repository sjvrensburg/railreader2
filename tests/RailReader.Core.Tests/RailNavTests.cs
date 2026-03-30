using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class RailNavTests
{
    private readonly AppConfig _config;
    private readonly RailNav _nav;

    // Standard viewport dimensions for tests
    private const double WindowWidth = 800;
    private const double WindowHeight = 600;
    private const double Zoom = 4.0; // above default RailZoomThreshold of 3.0

    // Class IDs
    private const int TextClass = 22;
    private const int ImageClass = 14;
    private const int HeaderClass = 12;

    public RailNavTests()
    {
        _config = new AppConfig
        {
            SnapDurationMs = 1, // near-instant snaps for testing
            PixelSnapping = false, // avoid rounding complications in assertions
        };
        _nav = new RailNav(_config);
    }

    /// <summary>
    /// Creates a PageAnalysis with the given number of blocks, each containing
    /// the specified number of lines. Blocks are 468pt wide text blocks (ClassId 22)
    /// stacked vertically with 20pt gaps. Each block's lines are evenly spaced.
    /// </summary>
    private static PageAnalysis CreateAnalysis(int blockCount, int linesPerBlock, int classId = TextClass)
    {
        var blocks = new List<LayoutBlock>();
        float yOffset = 72f; // top margin
        const float blockWidth = 468f;
        const float lineHeight = 16f;
        const float blockGap = 20f;
        const float xOffset = 72f;

        for (int b = 0; b < blockCount; b++)
        {
            float blockHeight = linesPerBlock * lineHeight;
            var lines = new List<LineInfo>();
            for (int l = 0; l < linesPerBlock; l++)
            {
                lines.Add(new LineInfo(yOffset + l * lineHeight, lineHeight));
            }

            blocks.Add(new LayoutBlock
            {
                BBox = new BBox(xOffset, yOffset, blockWidth, blockHeight),
                ClassId = classId,
                Confidence = 0.95f,
                Order = b,
                Lines = lines,
            });

            yOffset += blockHeight + blockGap;
        }

        return new PageAnalysis
        {
            Blocks = blocks,
            PageWidth = 612,
            PageHeight = 792,
        };
    }

    /// <summary>
    /// Creates an analysis with blocks of mixed class IDs.
    /// Alternates between the given class IDs for each block.
    /// </summary>
    private static PageAnalysis CreateMixedAnalysis(int blockCount, int linesPerBlock, params int[] classIds)
    {
        var analysis = CreateAnalysis(blockCount, linesPerBlock);
        for (int i = 0; i < analysis.Blocks.Count; i++)
            analysis.Blocks[i].ClassId = classIds[i % classIds.Length];
        return analysis;
    }

    private void ActivateWithAnalysis(int blockCount, int linesPerBlock)
    {
        var analysis = CreateAnalysis(blockCount, linesPerBlock);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });
        _nav.Active = true;
    }

    // ===== Line Navigation (7 tests) =====

    [Fact]
    public void NextLine_AdvancesWithinBlock()
    {
        ActivateWithAnalysis(1, 3);

        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);

        var result = _nav.NextLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);
    }

    [Fact]
    public void NextLine_CrossesBlockBoundary()
    {
        ActivateWithAnalysis(2, 2);

        // Advance to last line of first block
        _nav.NextLine(); // line 0 -> 1
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);

        // Cross to next block
        var result = _nav.NextLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void NextLine_AtEnd_ReturnsPageBoundary()
    {
        ActivateWithAnalysis(1, 2);

        _nav.NextLine(); // line 0 -> 1 (last line of last block)

        var result = _nav.NextLine();

        Assert.Equal(NavResult.PageBoundaryNext, result);
        // Position should not change
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);
    }

    [Fact]
    public void PrevLine_DecrementsWithinBlock()
    {
        ActivateWithAnalysis(1, 3);
        _nav.NextLine(); // 0 -> 1
        _nav.NextLine(); // 1 -> 2

        var result = _nav.PrevLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(1, _nav.CurrentLine);
    }

    [Fact]
    public void PrevLine_CrossesBlockBoundary()
    {
        ActivateWithAnalysis(2, 3);

        // Move to block 1, line 0
        _nav.NextLine(); // b0 l0 -> l1
        _nav.NextLine(); // b0 l1 -> l2
        _nav.NextLine(); // b0 l2 -> b1 l0
        Assert.Equal(1, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);

        var result = _nav.PrevLine();

        Assert.Equal(NavResult.Ok, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(2, _nav.CurrentLine); // last line of previous block
    }

    [Fact]
    public void PrevLine_AtStart_ReturnsPageBoundary()
    {
        ActivateWithAnalysis(1, 3);

        var result = _nav.PrevLine();

        Assert.Equal(NavResult.PageBoundaryPrev, result);
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void NextLine_WhenInactive_NoStateChange()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });
        _nav.Active = false; // explicitly inactive

        var result = _nav.NextLine();

        Assert.Equal(NavResult.Ok, result); // returns Ok but does nothing
        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    // ===== SetAnalysis (4 tests) =====

    [Fact]
    public void SetAnalysis_FiltersNavigableBlocks()
    {
        // 4 blocks: text, image, text, header — only text (22) is navigable
        var analysis = CreateMixedAnalysis(4, 2, TextClass, ImageClass, TextClass, HeaderClass);
        var navigable = new HashSet<int> { TextClass };

        _nav.SetAnalysis(analysis, navigable);

        Assert.Equal(2, _nav.NavigableCount); // only 2 text blocks
    }

    [Fact]
    public void SetAnalysis_ResetsPosition()
    {
        ActivateWithAnalysis(2, 3);

        // Advance position
        _nav.NextLine();
        _nav.NextLine();
        Assert.Equal(2, _nav.CurrentLine);

        // Set a NEW analysis (different object) — should reset
        var newAnalysis = CreateAnalysis(2, 4);
        _nav.SetAnalysis(newAnalysis, new HashSet<int> { TextClass });

        Assert.Equal(0, _nav.CurrentBlock);
        Assert.Equal(0, _nav.CurrentLine);
    }

    [Fact]
    public void HasAnalysis_TrueWithBlocks()
    {
        var analysis = CreateAnalysis(2, 3);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });

        Assert.True(_nav.HasAnalysis);
    }

    [Fact]
    public void SetAnalysis_EmptyBlocks_HasAnalysisFalse()
    {
        // All blocks are images — none match the navigable set
        var analysis = CreateAnalysis(3, 2, ImageClass);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });

        Assert.False(_nav.HasAnalysis);
        Assert.Equal(0, _nav.NavigableCount);
    }

    // ===== Snap Animation (4 tests) =====

    [Fact]
    public void StartSnap_CreatesAnimation()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // Tick should report animating (snap in progress)
        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        Assert.True(animating);
    }

    [Fact]
    public void Tick_SnapCompletes()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // With SnapDurationMs=1, a brief sleep ensures the stopwatch advances past the duration
        Thread.Sleep(5);

        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        // Snap should have completed (t >= 1.0), so Tick returns false
        Assert.False(animating);
    }

    [Fact]
    public void StartSnap_WhenInactive_NoEffect()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });
        _nav.Active = false;

        double cx = 100, cy = 200;
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        double origX = cx, origY = cy;
        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        Assert.False(animating);
        Assert.Equal(origX, cx);
        Assert.Equal(origY, cy);
    }

    [Fact]
    public void MultipleSnaps_LastWins()
    {
        ActivateWithAnalysis(2, 3);

        double cx = 0, cy = 0;

        // First snap: to block 0, line 0 (current position)
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // Advance to block 1 and start a second snap
        _nav.NextLine(); // b0 l0 -> l1
        _nav.NextLine(); // b0 l1 -> l2
        _nav.NextLine(); // b0 l2 -> b1 l0
        Assert.Equal(1, _nav.CurrentBlock);

        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);

        // Let it complete
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        // Camera Y should reflect block 1's first line position, not block 0's.
        // Block 1 starts at a lower Y (further down the page), so cameraY
        // should be different from the centered position of block 0 line 0.
        var block1Line0 = _nav.CurrentLineInfo;
        double expectedY = WindowHeight / 2.0 - block1Line0.Y * Zoom;
        Assert.Equal(expectedY, cy, precision: 1);
    }

    // ===== Scroll Hold (4 tests) =====

    [Fact]
    public void StartScroll_SetsDirection()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        // Position camera at line start so there's room to scroll
        _nav.StartSnapToCurrent(cx, cy, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        _nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        Assert.True(_nav.ScrollSpeed > 0);
    }

    [Fact]
    public void StopScroll_ClearsSpeed()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        Assert.True(_nav.ScrollSpeed > 0);

        _nav.StopScroll();

        Assert.Equal(0.0, _nav.ScrollSpeed);
    }

    [Fact]
    public void ScrollHold_SpeedIncreases()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartScroll(ScrollDirection.Forward, cx);

        // Sample speed early
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        double earlySpeed = _nav.ScrollSpeed;

        // Wait for ramp to increase speed — the ramp formula is quadratic
        // so even a short additional wait should yield higher speed.
        // We use a generous sleep to ensure the stopwatch advances enough.
        Thread.Sleep(200);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        double laterSpeed = _nav.ScrollSpeed;

        Assert.True(laterSpeed > earlySpeed,
            $"Expected later speed ({laterSpeed}) > early speed ({earlySpeed})");
    }

    [Fact]
    public void StopScrollAndEdgeHold_ClearsAll()
    {
        ActivateWithAnalysis(1, 3);

        double cx = 0, cy = 0;
        _nav.StartScroll(ScrollDirection.Forward, cx);
        Thread.Sleep(5);
        _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);

        _nav.StopScrollAndEdgeHold();

        Assert.Equal(0.0, _nav.ScrollSpeed);

        // After stopping, Tick should not report scroll animation
        bool animating = _nav.Tick(ref cx, ref cy, 0.016, Zoom, WindowWidth);
        Assert.False(animating);
    }

    // ===== Auto-scroll (3 tests) =====

    [Fact]
    public void StartAutoScroll_Activates()
    {
        ActivateWithAnalysis(1, 3);

        _nav.StartAutoScroll(100.0);

        Assert.True(_nav.AutoScrolling);
    }

    [Fact]
    public void StopAutoScroll_Deactivates()
    {
        ActivateWithAnalysis(1, 3);

        _nav.StartAutoScroll(100.0);
        Assert.True(_nav.AutoScrolling);

        _nav.StopAutoScroll();

        Assert.False(_nav.AutoScrolling);
    }

    [Fact]
    public void SetAutoScrollBoost_AffectsState()
    {
        ActivateWithAnalysis(1, 3);
        _nav.StartAutoScroll(100.0);

        // With boost, auto-scroll moves at 2x speed. We verify by ticking
        // with and without boost and comparing displacement.
        double cx1 = 0, cx2 = 0;

        // Position both at same start via snap
        _nav.StartSnapToCurrent(0, 0, Zoom, WindowWidth, WindowHeight);
        Thread.Sleep(5);
        double cy = 0;
        _nav.Tick(ref cx1, ref cy, 0.016, Zoom, WindowWidth);
        cx2 = cx1;

        // Tick without boost
        _nav.SetAutoScrollBoost(false);
        _nav.TickAutoScroll(ref cx1, 0.1, Zoom, WindowWidth);

        // Tick with boost
        _nav.SetAutoScrollBoost(true);
        _nav.TickAutoScroll(ref cx2, 0.1, Zoom, WindowWidth);

        // Boosted should have moved further (more negative = scrolled more)
        Assert.True(cx2 < cx1,
            $"Expected boosted position ({cx2}) < non-boosted ({cx1})");
    }

    [Fact]
    public void PendingPause_SuppressesScrollDuringSnap()
    {
        // Simulate entering a narrow block (like an equation) where the entire
        // block fits on screen. A deferred pause is set, and the snap is running.
        // Auto-scroll must NOT advance past the block while the snap is in progress.
        ActivateWithAnalysis(2, 1);
        _nav.StartAutoScroll(100.0);

        // Start a snap (simulates entering a new block)
        _nav.StartSnapToCurrent(0, 0, Zoom, WindowWidth, WindowHeight);

        // Set a deferred pause (as the controller does on block entry)
        _nav.PauseAutoScroll(600);

        // Tick auto-scroll while snap is still running — should NOT scroll
        double cx = 0;
        bool reachedEnd = _nav.TickAutoScroll(ref cx, 0.1, Zoom, WindowWidth);
        Assert.False(reachedEnd, "Should not advance while snap is running with pending pause");
        Assert.Equal(0, cx); // camera X should not have moved
    }

    [Fact]
    public void PendingPause_ActivatesAfterSnapCompletes()
    {
        ActivateWithAnalysis(2, 1);
        _nav.StartAutoScroll(100.0);

        // Start snap and deferred pause
        _nav.StartSnapToCurrent(0, 0, Zoom, WindowWidth, WindowHeight);
        _nav.PauseAutoScroll(50); // short pause for test speed

        // Complete the snap
        double cx = 0, cy = 0;
        Thread.Sleep(10);
        _nav.Tick(ref cx, ref cy, 1.0, Zoom, WindowWidth); // large dt to finish snap

        // Now tick auto-scroll — pause should be active (not scrolling)
        double cxBefore = cx;
        bool reachedEnd = _nav.TickAutoScroll(ref cx, 0.1, Zoom, WindowWidth);
        Assert.False(reachedEnd, "Should be pausing, not advancing");
        Assert.Equal(cxBefore, cx); // camera X should not move during pause

        // After pause expires, first tick clears the timer, second tick scrolls
        Thread.Sleep(60);
        _nav.TickAutoScroll(ref cx, 0.01, Zoom, WindowWidth); // clears pause timer
        reachedEnd = _nav.TickAutoScroll(ref cx, 0.01, Zoom, WindowWidth); // actually scrolls
        // For a narrow block that already fits on screen, reachedEnd may be
        // immediately true — the key assertion is that the pause was applied.
        Assert.True(reachedEnd || cx != cxBefore,
            "After pause expires, auto-scroll should resume or reach block end");
    }

    // ===== UpdateZoom (3 tests) =====

    [Fact]
    public void UpdateZoom_ActivatesAboveThreshold()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });
        Assert.False(_nav.Active);

        // Zoom above threshold (default 3.0)
        _nav.UpdateZoom(4.0, 0, 0, WindowWidth, WindowHeight);

        Assert.True(_nav.Active);
    }

    [Fact]
    public void UpdateZoom_DeactivatesBelowThreshold()
    {
        var analysis = CreateAnalysis(1, 3);
        _nav.SetAnalysis(analysis, new HashSet<int> { TextClass });

        // First activate
        _nav.UpdateZoom(4.0, 0, 0, WindowWidth, WindowHeight);
        Assert.True(_nav.Active);

        // Then zoom below threshold
        _nav.UpdateZoom(2.0, 0, 0, WindowWidth, WindowHeight);

        Assert.False(_nav.Active);
    }

    [Fact]
    public void ScaleVerticalBias_ScalesProportionally()
    {
        ActivateWithAnalysis(1, 3);

        _nav.VerticalBias = 100.0;
        double previousZoom = 4.0;
        double newZoom = 8.0;

        _nav.ScaleVerticalBias(previousZoom, newZoom);

        // Bias should scale by newZoom/previousZoom = 8/4 = 2x
        Assert.Equal(200.0, _nav.VerticalBias, precision: 5);
    }
}
