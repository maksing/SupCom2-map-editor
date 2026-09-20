namespace SC2CharacterizationTests;

/// <summary>Which symmetry copies turn the opposite way when their original is rotated: mirrored
/// copies do, rotated copies (180 degrees, radial) do not.</summary>
public class SymmetryTurnTests
{
    [Theory]
    [InlineData(SymmetryMode.X, 1, true)]
    [InlineData(SymmetryMode.Y, 1, true)]
    [InlineData(SymmetryMode.DiagonalTLBR, 1, true)]
    [InlineData(SymmetryMode.DiagonalTRBL, 1, true)]
    [InlineData(SymmetryMode.XY, 1, true)]
    [InlineData(SymmetryMode.XY, 2, true)]
    [InlineData(SymmetryMode.XY, 3, false)]
    [InlineData(SymmetryMode.DiagonalBoth, 3, false)]
    [InlineData(SymmetryMode.Radial, 1, false)]
    [InlineData(SymmetryMode.Radial, 3, false)]
    [InlineData(SymmetryMode.X, 0, false)]
    public void SlotReflects_PerMode(SymmetryMode mode, int slot, bool expected) =>
        Assert.Equal(expected, SC2SymmetryMath.SlotReflects(mode, slot));

    [Fact]
    public void TurnsOpposite_ComparesTheTwoSlots()
    {
        Assert.True(SC2SymmetryMath.TurnsOpposite(SymmetryMode.X, 0, 1));
        Assert.True(SC2SymmetryMath.TurnsOpposite(SymmetryMode.X, 1, 0));
        // XY: mirrorX and mirrorZ are both reflections, so they turn the same way as each other
        Assert.False(SC2SymmetryMath.TurnsOpposite(SymmetryMode.XY, 1, 2));
        // and the 180-degree copy turns the opposite way to either mirror
        Assert.True(SC2SymmetryMath.TurnsOpposite(SymmetryMode.XY, 1, 3));
        Assert.False(SC2SymmetryMath.TurnsOpposite(SymmetryMode.Radial, 1, 2));
    }
}
