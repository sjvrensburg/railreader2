using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AutoScrollStateMachineTests
{
    private sealed class NoOpClamp : ICameraClamp
    {
        public double ClampX(double cameraX, double zoom, double windowWidth) => cameraX;
    }

    private static AutoScrollContext MakeContext(
        bool snapInProgress = false,
        double blockRight = 1000,
        double rawBlockWidthPx = 800,
        int currentLine = 0,
        int blockLineCount = 10,
        double linePauseMs = 200,
        double windowWidth = 600,
        double zoom = 1.0,
        double referenceSpeed = 100,
        double maxSpeed = 5.0)
    {
        return new AutoScrollContext
        {
            SnapInProgress = snapInProgress,
            BlockRight = blockRight,
            RawBlockWidthPx = rawBlockWidthPx,
            CurrentLine = currentLine,
            BlockLineCount = blockLineCount,
            LinePauseMs = linePauseMs,
            WindowWidth = windowWidth,
            Zoom = zoom,
            ReferenceSpeed = referenceSpeed,
            MaxSpeed = maxSpeed,
        };
    }

    [Fact]
    public void Start_FromInactive_Activates()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        Assert.False(sm.IsActive);

        sm.Start(1.0);
        Assert.True(sm.IsActive);
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    [Fact]
    public void Stop_WhenActive_Deactivates()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        Assert.True(sm.IsActive);

        sm.Stop();
        Assert.False(sm.IsActive);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void Stop_WhenInactive_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Stop();
        Assert.False(sm.IsActive);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void Tick_WhenInactive_ReturnsFalse()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        double cameraX = 0;
        var ctx = MakeContext();

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
    }

    [Fact]
    public void Tick_WhenScrolling_MovesCameraLeft()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        // blockRight far away so we don't reach the end
        var ctx = MakeContext(blockRight: 10000);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
        Assert.True(cameraX < 0, "Camera should move left (negative direction)");
    }

    [Fact]
    public void Tick_WhenScrolling_ReachedBlockEnd_ReturnsTrue()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        // Block right is within the visible area so we immediately reach the end.
        // visibleRight = (-cameraX + windowWidth) / zoom
        // With cameraX near 0, visibleRight = 600, blockRight = 100 => reached end.
        // currentLine = 9, blockLineCount = 10 => is block end.
        var ctx = MakeContext(blockRight: 100, currentLine: 9, blockLineCount: 10, linePauseMs: 0);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.True(result);
    }

    [Fact]
    public void Tick_MidBlockLineEnd_EntersPausedState()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        double cameraX = 0;
        // visibleRight >= blockRight immediately, mid-block (not last line), linePauseMs > 0
        var ctx = MakeContext(blockRight: 100, currentLine: 3, blockLineCount: 10, linePauseMs: 200, rawBlockWidthPx: 2000);

        bool result = sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.False(result);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPause_TransitionsToWaitingForSnap()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);

        sm.RequestDeferredPause(500);
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);
    }

    [Fact]
    public void WaitingForSnap_StaysWhileSnapping()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(500);

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: true);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.WaitingForSnap, sm.CurrentState);
    }

    [Fact]
    public void WaitingForSnap_TransitionsToPausedWhenSnapCompletes()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(500);

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: false);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);
    }

    [Fact]
    public void Paused_ResumesScrollingAfterTimeout()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);
        sm.RequestDeferredPause(50); // short pause

        double cameraX = 0;
        var ctx = MakeContext(snapInProgress: false);

        // Snap completes -> enters pause
        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Paused, sm.CurrentState);

        // Wait for the pause to expire
        Thread.Sleep(55);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(AutoScrollState.Scrolling, sm.CurrentState);
    }

    [Fact]
    public void RequestDeferredPause_WhenInactive_IsNoOp()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.RequestDeferredPause(500);
        Assert.Equal(AutoScrollState.Inactive, sm.CurrentState);
    }

    [Fact]
    public void SetBoost_DoublesScrollSpeed()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(1.0);

        // Tick without boost
        double cameraX1 = 0;
        var ctx = MakeContext(blockRight: 10000);
        sm.Tick(ref cameraX1, 1.0, in ctx);

        // Reset and tick with boost
        sm.Stop();
        sm.Start(1.0);
        sm.SetBoost(true);
        double cameraX2 = 0;
        sm.Tick(ref cameraX2, 1.0, in ctx);

        Assert.Equal(cameraX1 * 2, cameraX2, precision: 10);
    }

    [Fact]
    public void NormalizedSpeed_UpdatesDuringScrolling()
    {
        var sm = new AutoScrollStateMachine(new NoOpClamp());
        sm.Start(2.5);
        double cameraX = 0;
        var ctx = MakeContext(blockRight: 10000, maxSpeed: 5.0);

        sm.Tick(ref cameraX, 0.016, in ctx);
        Assert.Equal(0.5, sm.NormalizedSpeed, precision: 10);
    }
}
