using Xunit;

namespace SC2CharacterizationTests;

public class BusyGateTests
{
    [Fact]
    public void ANewGateIsIdle()
    {
        var gate = new SC2BusyGate();
        Assert.False(gate.IsActive);
        Assert.Equal(SC2BusyGate.Phase.Idle, gate.State);
    }

    [Fact]
    public void TheJobDoesNotStartBeforeThePlateHasBeenDrawnTwice()
    {
        var gate = new SC2BusyGate();
        Assert.True(gate.Begin("Exporting map", "detail", 10.0));
        Assert.False(gate.ReadyToStart(10.0));
        gate.Presented();
        Assert.False(gate.ReadyToStart(10.01));
        gate.Presented();
        Assert.True(gate.ReadyToStart(10.02));
    }

    [Fact]
    public void TheJobStartsAnywayWhenNothingRepaints()
    {
        var gate = new SC2BusyGate();
        gate.Begin("Baking lighting", "", 5.0);
        Assert.False(gate.ReadyToStart(5.5));
        Assert.True(gate.ReadyToStart(5.0 + SC2BusyGate.MaxShowSeconds));
    }

    [Fact]
    public void ASecondJobIsRefusedWhileOneOwnsThePlate()
    {
        var gate = new SC2BusyGate();
        Assert.True(gate.Begin("Autosaving", "", 0.0));
        Assert.False(gate.Begin("Exporting map", "", 0.1));
        Assert.Equal("Autosaving", gate.Title);
        gate.Presented();
        gate.Presented();
        gate.MarkRunning(1.0);
        Assert.False(gate.Begin("Exporting map", "", 1.1));
    }

    [Fact]
    public void RepaintsWhileRunningDoNotCountTowardsTheNextJob()
    {
        var gate = new SC2BusyGate();
        gate.Begin("A", "", 0.0);
        gate.Presented();
        gate.Presented();
        gate.MarkRunning(0.1);
        gate.Presented();
        gate.Presented();
        gate.End();

        gate.Begin("B", "", 5.0);
        Assert.False(gate.ReadyToStart(5.0));
        gate.Presented();
        Assert.False(gate.ReadyToStart(5.01));
    }

    [Fact]
    public void EndingFreesThePlateAndClearsTheText()
    {
        var gate = new SC2BusyGate();
        gate.Begin("Baking point cloud", "Calculating", 0.0);
        gate.End();
        Assert.False(gate.IsActive);
        Assert.Equal("", gate.Title);
        Assert.Equal("", gate.Detail);
        Assert.True(gate.Begin("Autosaving", "", 1.0));
    }

    [Fact]
    public void RunningSecondsCountFromTheStartOfTheJob()
    {
        var gate = new SC2BusyGate();
        gate.Begin("Exporting map", "", 0.0);
        Assert.Equal(0.0, gate.RunningSeconds(3.0));
        gate.MarkRunning(4.0);
        Assert.Equal(2.5, gate.RunningSeconds(6.5), 6);
    }

    [Fact]
    public void MarkingRunningOnlyWorksFromTheShowingPhase()
    {
        var gate = new SC2BusyGate();
        gate.MarkRunning(1.0);
        Assert.Equal(SC2BusyGate.Phase.Idle, gate.State);
    }
}
