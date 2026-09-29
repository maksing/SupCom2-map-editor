using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>GetSlotRotationDegrees' returned angle must reproduce exactly the linear (rotation)
/// part of GetSlots' own (relX, relZ) formula for that slot — it's a second description of the
/// same rigid rotation (used to turn a custom paint brush's stamp so a Radial/180-degree symmetric
/// copy's content matches its own moved position), so the two must never silently diverge. See
/// SC2SymmetryMath.GetSlotRotationDegrees' own doc comment for why the caller must negate this
/// value before handing it to SC2PaintBrush.shader's _BrushRotation.</summary>
public class SymmetryRotationTests
{
    [Theory]
    [InlineData(SymmetryMode.Radial, 2, 1)]
    [InlineData(SymmetryMode.Radial, 3, 1)]
    [InlineData(SymmetryMode.Radial, 3, 2)]
    [InlineData(SymmetryMode.Radial, 4, 1)]
    [InlineData(SymmetryMode.Radial, 4, 2)]
    [InlineData(SymmetryMode.Radial, 4, 3)]
    [InlineData(SymmetryMode.XY, 4, 3)]
    [InlineData(SymmetryMode.DiagonalBoth, 4, 3)]
    public void GetSlotRotationDegrees_MatchesGetSlots_ForEveryRotatingSlot(SymmetryMode mode, int radialCount, int slot)
    {
        var rel = new Vector3(3f, 0f, 7f); // asymmetric on purpose, so a wrong-sign angle is distinguishable
        var center = new Vector3(100f, 0f, 100f);
        var slots = SC2SymmetryMath.GetSlots(mode, center, radialCount, center + rel);
        var expected = slots[slot] - center;

        var degrees = SC2SymmetryMath.GetSlotRotationDegrees(mode, radialCount, slot);
        var rad = degrees * Mathf.Deg2Rad;
        var s = Mathf.Sin(rad); var c = Mathf.Cos(rad);
        var rotated = new Vector2(rel.x * c - rel.z * s, rel.x * s + rel.z * c);

        Assert.Equal(expected.x, rotated.x, 3);
        Assert.Equal(expected.z, rotated.y, 3);
    }

    [Theory]
    [InlineData(SymmetryMode.X, 1)]
    [InlineData(SymmetryMode.Y, 1)]
    [InlineData(SymmetryMode.XY, 1)]
    [InlineData(SymmetryMode.XY, 2)]
    [InlineData(SymmetryMode.DiagonalTLBR, 1)]
    [InlineData(SymmetryMode.DiagonalBoth, 1)]
    [InlineData(SymmetryMode.None, 0)]
    [InlineData(SymmetryMode.Radial, 0)]
    public void GetSlotRotationDegrees_IsZero_ForReflectingOrSeedSlots(SymmetryMode mode, int slot)
    {
        Assert.Equal(0f, SC2SymmetryMath.GetSlotRotationDegrees(mode, radialCount: 4, slot));
    }

    [Fact]
    public void GetSlotRotationDegrees_IsZero_PastRotationAngleStepsCount()
    {
        // radialCount=2 has exactly one extra slot (180 degrees) — slot 2 doesn't exist for it.
        Assert.Equal(0f, SC2SymmetryMath.GetSlotRotationDegrees(SymmetryMode.Radial, radialCount: 2, slot: 2));
    }
}
