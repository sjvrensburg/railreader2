using Xunit;

namespace RailReader.Core.Tests;

public class EdgeHoldStateMachineTests
{
    [Fact]
    public void OnEdgeHit_FromIdle_StartsHolding()
    {
        var sm = new EdgeHoldStateMachine();
        bool result = sm.OnEdgeHit(forward: true);

        Assert.False(result);
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);
    }

    [Fact]
    public void OnEdgeHit_AfterHoldThreshold_ReturnsTrue()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);

        Thread.Sleep(405);
        bool result = sm.OnEdgeHit(forward: true);

        Assert.True(result);
        Assert.Equal(EdgeHoldState.Cooldown, sm.CurrentState);
    }

    [Fact]
    public void OnMoved_ResetsToIdle()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);

        sm.OnMoved();
        Assert.Equal(EdgeHoldState.Idle, sm.CurrentState);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: true); // fires advance

        sm.Reset();

        Assert.Equal(EdgeHoldState.Idle, sm.CurrentState);
        Assert.False(sm.AdvanceJustFired);
        Assert.Null(sm.ConsumePendingAdvance());
    }

    [Fact]
    public void ShouldSuppressInput_TrueDuringCooldown()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: true); // fires advance, enters cooldown

        Assert.True(sm.ShouldSuppressInput);
    }

    [Fact]
    public void ShouldSuppressInput_FalseAfterCooldown()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: true); // fires advance, enters cooldown

        Thread.Sleep(305);
        Assert.False(sm.ShouldSuppressInput);
    }

    [Fact]
    public void DirectionChange_ResetsHold()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(200);

        // Change direction mid-hold — should restart timer
        bool result = sm.OnEdgeHit(forward: false);
        Assert.False(result);
        Assert.Equal(EdgeHoldState.Holding, sm.CurrentState);

        // Original 400ms has not passed since the direction change
        Thread.Sleep(205);
        result = sm.OnEdgeHit(forward: false);
        Assert.False(result);

        // Now wait enough for the restarted timer
        Thread.Sleep(205);
        result = sm.OnEdgeHit(forward: false);
        Assert.True(result);
    }

    [Fact]
    public void AdvanceJustFired_SetAfterAdvance()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: true);

        Assert.True(sm.AdvanceJustFired);
    }

    [Fact]
    public void ClearAdvanceFlag_ClearsFlag()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: true);
        Assert.True(sm.AdvanceJustFired);

        sm.ClearAdvanceFlag();
        Assert.False(sm.AdvanceJustFired);
    }

    [Fact]
    public void ConsumePendingAdvance_ReturnsDirection()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: true);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: true);

        var direction = sm.ConsumePendingAdvance();
        Assert.Equal(Models.ScrollDirection.Forward, direction);

        // Second call should return null (consumed)
        Assert.Null(sm.ConsumePendingAdvance());
    }

    [Fact]
    public void ConsumePendingAdvance_BackwardDirection()
    {
        var sm = new EdgeHoldStateMachine();
        sm.OnEdgeHit(forward: false);
        Thread.Sleep(405);
        sm.OnEdgeHit(forward: false);

        var direction = sm.ConsumePendingAdvance();
        Assert.Equal(Models.ScrollDirection.Backward, direction);
    }
}
