using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>GetSlotMirror's (flip, swapAxes) must reproduce exactly the linear part of GetSlots'
/// own (relX, relZ) formula for that slot — it's a second description of the same reflection
/// (used to mirror a custom paint brush's stamp for a reflected symmetric copy), so the two must
/// never silently diverge.</summary>
public class SymmetryMirrorTests
{
    private static readonly SymmetryMode[] ReflectingModes =
    {
        SymmetryMode.X, SymmetryMode.Y, SymmetryMode.XY,
        SymmetryMode.DiagonalTLBR, SymmetryMode.DiagonalTRBL, SymmetryMode.DiagonalBoth,
    };

    [Theory]
    [InlineData(SymmetryMode.X, 1)]
    [InlineData(SymmetryMode.Y, 1)]
    [InlineData(SymmetryMode.XY, 1)]
    [InlineData(SymmetryMode.XY, 2)]
    [InlineData(SymmetryMode.DiagonalTLBR, 1)]
    [InlineData(SymmetryMode.DiagonalTRBL, 1)]
    [InlineData(SymmetryMode.DiagonalBoth, 1)]
    [InlineData(SymmetryMode.DiagonalBoth, 2)]
    public void GetSlotMirror_MatchesGetSlots_ForEveryReflectingSlot(SymmetryMode mode, int slot)
    {
        var rel = new Vector3(3f, 0f, 7f); // asymmetric on purpose, so x/z swaps are distinguishable
        var center = new Vector3(100f, 0f, 100f);
        var slots = SC2SymmetryMath.GetSlots(mode, center, radialCount: 4, center + rel);
        var expected = slots[slot] - center;

        var reflects = SC2SymmetryMath.GetSlotMirror(mode, slot, out var flip, out var swapAxes);

        Assert.True(reflects);
        var mirrored = swapAxes ? new Vector2(rel.z, rel.x) : new Vector2(rel.x, rel.z);
        mirrored *= flip;
        Assert.Equal(expected.x, mirrored.x, 3);
        Assert.Equal(expected.z, mirrored.y, 3);
    }

    [Theory]
    [InlineData(SymmetryMode.X, 0)]
    [InlineData(SymmetryMode.XY, 3)]
    [InlineData(SymmetryMode.DiagonalBoth, 3)]
    [InlineData(SymmetryMode.Radial, 1)]
    [InlineData(SymmetryMode.None, 0)]
    public void GetSlotMirror_IsIdentity_ForSlotsThatOnlyRotate(SymmetryMode mode, int slot)
    {
        var reflects = SC2SymmetryMath.GetSlotMirror(mode, slot, out var flip, out var swapAxes);

        Assert.False(reflects);
        Assert.Equal(Vector2.one, flip);
        Assert.False(swapAxes);
    }

    [Fact]
    public void GetSlotMirror_AgreesWithSlotReflects_AcrossEveryModeAndSlot()
    {
        foreach (var mode in ReflectingModes)
            for (var slot = 0; slot <= 3; slot++)
                Assert.Equal(SC2SymmetryMath.SlotReflects(mode, slot), SC2SymmetryMath.GetSlotMirror(mode, slot, out _, out _));
    }

    [Theory]
    [InlineData(SymmetryMode.X, 1)]
    [InlineData(SymmetryMode.Y, 1)]
    [InlineData(SymmetryMode.DiagonalTLBR, 1)]
    [InlineData(SymmetryMode.DiagonalTRBL, 1)]
    public void GetSlotMirror_IsItsOwnInverse_ForASingleMirrorAxis(SymmetryMode mode, int slot)
    {
        // A single mirror applied twice returns the original point — confirms the shader's
        // "mirror before rotate" step can't accidentally double-flip anything for these modes.
        SC2SymmetryMath.GetSlotMirror(mode, slot, out var flip, out var swapAxes);
        var rel = new Vector2(3f, 7f);
        var once = swapAxes ? new Vector2(rel.y, rel.x) : rel;
        once *= flip;
        var twice = swapAxes ? new Vector2(once.y, once.x) : once;
        twice *= flip;
        Assert.Equal(rel, twice);
    }
}
