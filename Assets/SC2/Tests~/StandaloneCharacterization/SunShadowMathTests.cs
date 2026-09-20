using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>Where a cube's shadow falls for a given map sun direction (the direction to the sun, Y up).</summary>
public class SunShadowMathTests
{
    [Fact]
    public void FortyFiveDegreeSun_CastsAShadowAsLongAsTheObjectIsTall_OppositeTheSun()
    {
        // sun to the +X side, 45 degrees up
        var r = SC2SunShadowMath.Compute(new Vector3(1f, 1f, 0f));
        Assert.True(r.HasShadow);
        Assert.Equal(45f, r.ElevationDeg, 2);
        Assert.Equal(90f, r.AzimuthDeg, 2);
        Assert.Equal(1f, r.LengthPerUnit, 3);
        Assert.Equal(-1f, r.OffsetPerUnit.x, 3);
        Assert.Equal(0f, r.OffsetPerUnit.y, 3);
    }

    [Fact]
    public void LowerSun_CastsALongerShadow()
    {
        // tan(30 degrees) = 0.5774 -> the shadow is 1.732 times the height
        var r = SC2SunShadowMath.Compute(new Vector3(0f, 0.5f, 0.866025f));
        Assert.Equal(30f, r.ElevationDeg, 2);
        Assert.Equal(180f, r.AzimuthDeg, 2);   // the sun is on the +Z side, which is the bottom of the map
        Assert.Equal(1.732f, r.LengthPerUnit, 2);
        Assert.Equal(-1.732f, r.OffsetPerUnit.y, 2);
    }

    [Fact]
    public void AzimuthRunsClockwiseFromMapUp_WhichIsMinusZ()
    {
        Assert.Equal(0f, SC2SunShadowMath.Compute(new Vector3(0f, 1f, -1f)).AzimuthDeg, 2);
        Assert.Equal(90f, SC2SunShadowMath.Compute(new Vector3(1f, 1f, 0f)).AzimuthDeg, 2);
        Assert.Equal(180f, SC2SunShadowMath.Compute(new Vector3(0f, 1f, 1f)).AzimuthDeg, 2);
        Assert.Equal(270f, SC2SunShadowMath.Compute(new Vector3(-1f, 1f, 0f)).AzimuthDeg, 2);
    }

    [Fact]
    public void SunAtOrBelowTheHorizon_HasNoShadow()
    {
        Assert.False(SC2SunShadowMath.Compute(new Vector3(1f, 0f, 0f)).HasShadow);
        var below = SC2SunShadowMath.Compute(new Vector3(1f, -1f, 0f));
        Assert.False(below.HasShadow);
        Assert.Equal(-45f, below.ElevationDeg, 2);
        Assert.False(SC2SunShadowMath.Compute(Vector3.zero).HasShadow);
    }

    [Fact]
    public void SunStraightOverhead_ShadowStaysUnderTheObject()
    {
        var r = SC2SunShadowMath.Compute(new Vector3(0f, 1f, 0f));
        Assert.True(r.HasShadow);
        Assert.Equal(0f, r.LengthPerUnit, 4);
    }

    [Fact]
    public void SweptSquare_CoversTheCubeAndTheStripTheShadowSweeps()
    {
        var offset = new Vector2(-3f, 0f);   // unit square, shadow reaching 3 units to -X
        Assert.True(SC2SunShadowMath.InSweptSquare(new Vector2(0f, 0f), 1f, offset));
        Assert.True(SC2SunShadowMath.InSweptSquare(new Vector2(-2f, 0.4f), 1f, offset));
        Assert.True(SC2SunShadowMath.InSweptSquare(new Vector2(-3.4f, -0.4f), 1f, offset));
        Assert.False(SC2SunShadowMath.InSweptSquare(new Vector2(-4f, 0f), 1f, offset));
        Assert.False(SC2SunShadowMath.InSweptSquare(new Vector2(-2f, 0.7f), 1f, offset));
        Assert.False(SC2SunShadowMath.InSweptSquare(new Vector2(1f, 0f), 1f, offset));
    }

    [Fact]
    public void SweptSquare_FollowsADiagonalShadow()
    {
        var offset = new Vector2(2f, 2f);
        Assert.True(SC2SunShadowMath.InSweptSquare(new Vector2(1f, 1f), 1f, offset));
        Assert.True(SC2SunShadowMath.InSweptSquare(new Vector2(1.9f, 1.2f), 1f, offset));  // along the diagonal band
        Assert.False(SC2SunShadowMath.InSweptSquare(new Vector2(2f, -0.2f), 1f, offset));  // off the band
    }
}
