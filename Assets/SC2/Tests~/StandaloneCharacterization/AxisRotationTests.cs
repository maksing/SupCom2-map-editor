using UnityEngine;
using Xunit;

namespace SC2CharacterizationTests;

public class AxisRotationTests
{
    // An arbitrary orientation built without the engine's native code.
    private static Quaternion Orientation(float x, float y, float z) =>
        SC2AxisRotation.AngleAxis(y, Vector3.up) * SC2AxisRotation.AngleAxis(x, Vector3.right) * SC2AxisRotation.AngleAxis(z, Vector3.forward);

    private static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

    private static void AssertSame(Vector3 expected, Vector3 actual) => Assert.True((expected - actual).magnitude < 1e-4f, $"expected {expected}, got {actual}");

    [Fact]
    public void PressCyclesGlobalLocalAndOffAndANewAxisStartsGlobal()
    {
        var state = SC2AxisRotation.Press(SC2RotationAxis.None, false, SC2RotationAxis.X);
        Assert.Equal((SC2RotationAxis.X, false), state);
        state = SC2AxisRotation.Press(state.Axis, state.Local, SC2RotationAxis.X);
        Assert.Equal((SC2RotationAxis.X, true), state);
        state = SC2AxisRotation.Press(state.Axis, state.Local, SC2RotationAxis.X);
        Assert.Equal((SC2RotationAxis.None, false), state);

        state = SC2AxisRotation.Press(SC2RotationAxis.X, true, SC2RotationAxis.Z);
        Assert.Equal((SC2RotationAxis.Z, false), state);
    }

    [Fact]
    public void WithoutAnAxisTheTurnIsAboutTheObjectsOwnUpAxisAsBefore()
    {
        var original = Orientation(10f, 20f, 30f);
        var rotated = SC2AxisRotation.Apply(original, SC2RotationAxis.None, false, 40f);
        Assert.True(Quaternion.Angle(original * SC2AxisRotation.AngleAxis(40f, Vector3.up), rotated) < 1e-3f);
    }

    [Theory]
    [InlineData(SC2RotationAxis.X)]
    [InlineData(SC2RotationAxis.Y)]
    [InlineData(SC2RotationAxis.Z)]
    public void AGlobalAxisTurnsAboutTheWorldAxisWhateverTheObjectsOrientation(SC2RotationAxis axis)
    {
        var original = Orientation(25f, 70f, 140f);
        var rotated = SC2AxisRotation.Apply(original, axis, false, 33f);

        // What the turn was: rotated = turn * original.
        var turn = rotated * Conjugate(original);
        var angle = 2f * Mathf.Acos(Mathf.Clamp(turn.w, -1f, 1f)) * Mathf.Rad2Deg;
        var turnAxis = new Vector3(turn.x, turn.y, turn.z).normalized;
        Assert.Equal(33f, angle, 1);
        AssertSame(SC2AxisRotation.Direction(axis), turnAxis);
    }

    [Theory]
    [InlineData(SC2RotationAxis.X)]
    [InlineData(SC2RotationAxis.Y)]
    [InlineData(SC2RotationAxis.Z)]
    public void ALocalAxisTurnsAboutTheObjectsOwnAxis(SC2RotationAxis axis)
    {
        var original = Orientation(25f, 70f, 140f);
        var rotated = SC2AxisRotation.Apply(original, axis, true, 33f);

        // The object's own axis in the world stays where it was.
        var before = original * SC2AxisRotation.Direction(axis);
        var after = rotated * SC2AxisRotation.Direction(axis);
        AssertSame(before, after);
        Assert.True(Quaternion.Angle(original, rotated) > 30f);
    }

    [Fact]
    public void ZeroDegreesLeavesTheRotationAlone()
    {
        var original = Orientation(5f, 6f, 7f);
        foreach (var axis in new[] { SC2RotationAxis.None, SC2RotationAxis.X, SC2RotationAxis.Y, SC2RotationAxis.Z })
        foreach (var local in new[] { false, true })
            Assert.True(Quaternion.Angle(original, SC2AxisRotation.Apply(original, axis, local, 0f)) < 1e-3f);
    }

    [Fact]
    public void ViewSignIsPositiveForAnAxisTowardsTheViewerAndNegativeAwayFromIt()
    {
        // A camera above looking straight down: world up points at it.
        var down = Vector3.down;
        Assert.Equal(1f, SC2AxisRotation.ViewSign(SC2RotationAxis.Y, false, Quaternion.identity, down));
        // An object turned upside down: its local up points away from the viewer.
        Assert.Equal(-1f, SC2AxisRotation.ViewSign(SC2RotationAxis.Y, true, Orientation(180f, 0f, 0f), down));
        Assert.Equal(1f, SC2AxisRotation.ViewSign(SC2RotationAxis.None, false, Quaternion.identity, down));
        // A camera looking along +Z sees +Z pointing away.
        Assert.Equal(-1f, SC2AxisRotation.ViewSign(SC2RotationAxis.Z, false, Quaternion.identity, Vector3.forward));
    }

    [Fact]
    public void TypedAnglesAreParsedWithMinusTogglingTheSign()
    {
        var typed = "";
        Assert.False(SC2AxisRotation.TryParseTyped(typed, out _));
        typed = SC2AxisRotation.AppendTyped(typed, '4');
        typed = SC2AxisRotation.AppendTyped(typed, '5');
        Assert.True(SC2AxisRotation.TryParseTyped(typed, out var degrees));
        Assert.Equal(45f, degrees);

        typed = SC2AxisRotation.AppendTyped(typed, '-');
        Assert.True(SC2AxisRotation.TryParseTyped(typed, out degrees));
        Assert.Equal(-45f, degrees);
        typed = SC2AxisRotation.AppendTyped(typed, '-');
        Assert.Equal("45", typed);

        typed = SC2AxisRotation.AppendTyped(typed, '.');
        typed = SC2AxisRotation.AppendTyped(typed, '5');
        typed = SC2AxisRotation.AppendTyped(typed, '.');   // a second point is ignored
        Assert.True(SC2AxisRotation.TryParseTyped(typed, out degrees));
        Assert.Equal(45.5f, degrees);

        typed = SC2AxisRotation.DropLastTyped(typed);
        typed = SC2AxisRotation.DropLastTyped(typed);
        typed = SC2AxisRotation.DropLastTyped(typed);
        Assert.Equal("4", typed);
        Assert.Equal("", SC2AxisRotation.DropLastTyped(""));
    }

    [Fact]
    public void DescribeNamesTheAxis()
    {
        Assert.Equal("", SC2AxisRotation.Describe(SC2RotationAxis.None, false));
        Assert.Equal("X (global)", SC2AxisRotation.Describe(SC2RotationAxis.X, false));
        Assert.Equal("Z (local)", SC2AxisRotation.Describe(SC2RotationAxis.Z, true));
    }
}
