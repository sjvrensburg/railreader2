using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class HorizontalScrollStateTests
{
    [Fact]
    public void Start_SetsActiveAndDirection()
    {
        var state = new HorizontalScrollState();
        state.Start(ScrollDirection.Forward, 0, 100, 500);
        Assert.True(state.Active);
        Assert.Equal(ScrollDirection.Forward, state.Direction);
    }

    [Fact]
    public void Stop_ClearsState()
    {
        var state = new HorizontalScrollState();
        state.Start(ScrollDirection.Forward, 0, 100, 500);
        state.Stop();
        Assert.False(state.Active);
    }

    [Fact]
    public void Stop_WhenNotStarted_IsNoOp()
    {
        var state = new HorizontalScrollState();
        state.Stop();
        Assert.False(state.Active);
    }

    [Fact]
    public void DisplacementIntegral_AtZero_ReturnsZero()
    {
        double result = HorizontalScrollState.DisplacementIntegral(100, 500, 1.0, 0);
        Assert.Equal(0, result);
    }

    [Fact]
    public void DisplacementIntegral_AtRampEnd_MatchesFormula()
    {
        double speedStart = 100;
        double speedMax = 500;
        double ramp = 2.0;
        double expected = speedStart * ramp + (speedMax - speedStart) * ramp / 3.0;

        double result = HorizontalScrollState.DisplacementIntegral(speedStart, speedMax, ramp, ramp);
        Assert.Equal(expected, result, precision: 10);
    }

    [Fact]
    public void DisplacementIntegral_AfterRamp_IsLinear()
    {
        double speedStart = 100;
        double speedMax = 500;
        double ramp = 2.0;
        double atRamp = HorizontalScrollState.DisplacementIntegral(speedStart, speedMax, ramp, ramp);
        double atRampPlus1 = HorizontalScrollState.DisplacementIntegral(speedStart, speedMax, ramp, ramp + 1);

        Assert.Equal(atRamp + speedMax * 1, atRampPlus1, precision: 10);
    }

    [Fact]
    public void DisplacementIntegral_ConstantSpeed_IsLinear()
    {
        double speed = 300;
        double t = 2.5;
        double result = HorizontalScrollState.DisplacementIntegral(speed, speed, 1.0, t);
        Assert.Equal(speed * t, result, precision: 10);
    }
}
