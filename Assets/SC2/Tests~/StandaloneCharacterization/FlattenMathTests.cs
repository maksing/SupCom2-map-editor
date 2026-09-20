using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>Blender-style Flatten on a heightfield: the plane is sampled from the terrain under the brush,
/// so a slope flattens into a slope and noise is pulled onto the average plane.</summary>
public class FlattenMathTests
{
    private const int Size = 32;
    private const float Cell = 1f;

    private static float[] Field(System.Func<int, int, float> height)
    {
        var heights = new float[(Size + 1) * (Size + 1)];
        for (var z = 0; z <= Size; z++)
            for (var x = 0; x <= Size; x++)
                heights[z * (Size + 1) + x] = height(x, z);
        return heights;
    }

    // hard, full-strength disc brush
    private static float Disc(float radius, float dx, float dz) => dx * dx + dz * dz <= radius * radius ? 1f : 0f;

    [Fact]
    public void AreaPlane_OfATiltedSurface_HasThatSlope()
    {
        var heights = Field((x, z) => 10f + 0.5f * x);           // rises 0.5 per unit along +X
        var centre = new Vector3(16f, 0f, 16f);
        var plane = SC2FlattenMath.ComputeAreaPlane(heights, Size, Size, Cell, centre, 6f, (dx, dz) => Disc(6f, dx, dz));
        Assert.True(plane.Valid);
        Assert.Equal(10f + 0.5f * 16f, plane.HeightAt(16f, 16f), 2);
        Assert.Equal(10f + 0.5f * 20f, plane.HeightAt(20f, 16f), 2);   // same tilt, not a level plane
        Assert.Equal(plane.HeightAt(20f, 5f), plane.HeightAt(20f, 25f), 3); // no tilt along Z
    }

    [Fact]
    public void Flatten_OnATiltedSurface_LeavesItAsIs()
    {
        var heights = Field((x, z) => 10f + 0.5f * x);
        var before = (float[])heights.Clone();
        var centre = new Vector3(16f, 0f, 16f);
        System.Func<float, float, float> w = (dx, dz) => Disc(6f, dx, dz);
        var plane = SC2FlattenMath.ComputeAreaPlane(heights, Size, Size, Cell, centre, 6f, w);
        SC2FlattenMath.ApplyToHeights(heights, Size, Size, Cell, centre, 6f, 1f, plane, w, 0f, 512f);
        for (var i = 0; i < heights.Length; i++) Assert.Equal(before[i], heights[i], 2);
    }

    [Fact]
    public void Flatten_PullsBumpsAndHollowsOntoTheAveragePlane()
    {
        // level ground at 20 with a bump and a hollow; the plane is level at about the mean
        var heights = Field((x, z) => 20f + (x == 15 && z == 16 ? 4f : 0f) - (x == 17 && z == 16 ? 4f : 0f));
        var centre = new Vector3(16f, 0f, 16f);
        System.Func<float, float, float> w = (dx, dz) => Disc(6f, dx, dz);
        var plane = SC2FlattenMath.ComputeAreaPlane(heights, Size, Size, Cell, centre, 6f, w);
        SC2FlattenMath.ApplyToHeights(heights, Size, Size, Cell, centre, 6f, 1f, plane, w, 0f, 512f);
        Assert.InRange(heights[16 * (Size + 1) + 15], 19f, 21f);   // the bump came down (was 24)
        Assert.InRange(heights[16 * (Size + 1) + 17], 19f, 21f);   // the hollow came up (was 16)
        Assert.Equal(20f, heights[10 * (Size + 1) + 20], 3);       // outside the brush: untouched
    }

    [Fact]
    public void Flatten_StrengthAndFalloffScaleHowFarVerticesMove()
    {
        var heights = Field((x, z) => 30f);
        heights[16 * (Size + 1) + 16] = 40f;
        var centre = new Vector3(16f, 0f, 16f);
        System.Func<float, float, float> w = (dx, dz) => Disc(6f, dx, dz);
        var plane = SC2FlattenMath.ComputeAreaPlane(heights, Size, Size, Cell, centre, 6f, w);
        var half = (float[])heights.Clone();
        SC2FlattenMath.ApplyToHeights(half, Size, Size, Cell, centre, 6f, 0.5f, plane, w, 0f, 512f);
        var full = (float[])heights.Clone();
        SC2FlattenMath.ApplyToHeights(full, Size, Size, Cell, centre, 6f, 1f, plane, w, 0f, 512f);
        var i = 16 * (Size + 1) + 16;
        var planeH = plane.HeightAt(16f, 16f);
        Assert.Equal(planeH, full[i], 3);
        Assert.Equal((40f + planeH) * 0.5f, half[i], 3);
    }

    [Fact]
    public void Flatten_ClampsToTheHeightRange()
    {
        var heights = Field((x, z) => 5f);
        var centre = new Vector3(16f, 0f, 16f);
        // a plane far above the range (locked plane from elsewhere)
        var plane = new SC2FlattenPlane { Valid = true, Center = new Vector3(16f, 900f, 16f), Normal = Vector3.up };
        SC2FlattenMath.ApplyToHeights(heights, Size, Size, Cell, centre, 6f, 1f, plane, (dx, dz) => Disc(6f, dx, dz), 0f, 512f);
        Assert.Equal(512f, heights[16 * (Size + 1) + 16], 3);
    }

    [Fact]
    public void AreaWeight_IsUnweighted_AndOnlyTheMiddleHalfOfTheBrush()
    {
        Assert.Equal(1f, SC2FlattenMath.AreaWeight(10f, 0f, 0f));
        Assert.Equal(1f, SC2FlattenMath.AreaWeight(10f, 4.9f, 0f));   // still inside radius / 2, no falloff
        Assert.Equal(0f, SC2FlattenMath.AreaWeight(10f, 5.1f, 0f));
        Assert.Equal(0f, SC2FlattenMath.AreaWeight(10f, 8f, 0f));
    }

    [Fact]
    public void PlaneFromTheMiddle_IgnoresTheOuterRing()
    {
        // level at 10 in the middle half of the brush, a tall ring further out
        var heights = Field((x, z) =>
        {
            var d = Mathf.Sqrt((x - 16f) * (x - 16f) + (z - 16f) * (z - 16f));
            return d > 6f && d <= 12f ? 30f : 10f;
        });
        var centre = new Vector3(16f, 0f, 16f);
        var plane = SC2FlattenMath.ComputeAreaPlane(heights, Size, Size, Cell, centre, 6f, (dx, dz) => SC2FlattenMath.AreaWeight(12f, dx, dz));
        Assert.Equal(10f, plane.HeightAt(16f, 16f), 2);
    }

    [Fact]
    public void SmoothFalloff_IsOneInTheCoreThenASmoothstepToZeroAtTheEdge()
    {
        Assert.Equal(1f, SC2FlattenMath.SmoothFalloff(0f, 0.5f));
        Assert.Equal(1f, SC2FlattenMath.SmoothFalloff(0.5f, 0.5f));
        Assert.Equal(0.5f, SC2FlattenMath.SmoothFalloff(0.75f, 0.5f), 3);   // middle of the band
        Assert.Equal(0f, SC2FlattenMath.SmoothFalloff(1f, 0.5f));
        Assert.Equal(0f, SC2FlattenMath.SmoothFalloff(1.4f, 0.5f));
        // falls monotonically and flatly toward both ends of the band (smoothstep)
        Assert.True(SC2FlattenMath.SmoothFalloff(0.6f, 0.5f) > SC2FlattenMath.SmoothFalloff(0.7f, 0.5f));
        Assert.True(SC2FlattenMath.SmoothFalloff(0.51f, 0.5f) > 0.99f);
        Assert.Equal(1f, SC2FlattenMath.SmoothFalloff(0.9f, 1f));           // fully hard brush
    }

    [Fact]
    public void SpacedDabs_AreEvenlySpacedWhateverTheEventRate()
    {
        var from = new Vector3(0f, 0f, 0f);
        // one long jump and many tiny steps end up with the same dabs
        var jump = SC2FlattenMath.SpacedDabs(from, new Vector3(10f, 0f, 0f), 2f, 48, out var capped);
        Assert.False(capped);
        Assert.Equal(5, jump.Count);
        Assert.Equal(2f, jump[0].x, 3);
        Assert.Equal(10f, jump[4].x, 3);
        Assert.Empty(SC2FlattenMath.SpacedDabs(from, new Vector3(1.9f, 0f, 0f), 2f, 48, out _)); // not far enough yet
    }

    [Fact]
    public void SpacedDabs_AreCappedOnAHugeJump()
    {
        var dabs = SC2FlattenMath.SpacedDabs(Vector3.zero, new Vector3(1000f, 0f, 0f), 1f, 48, out var capped);
        Assert.True(capped);
        Assert.Equal(48, dabs.Count);
    }

    [Fact]
    public void NoWeightOrInvalidPlane_ChangesNothing()
    {
        var heights = Field((x, z) => 8f);
        var centre = new Vector3(16f, 0f, 16f);
        var none = SC2FlattenMath.ComputeAreaPlane(heights, Size, Size, Cell, centre, 6f, (dx, dz) => 0f);
        Assert.False(none.Valid);
        Assert.False(SC2FlattenMath.ApplyToHeights(heights, Size, Size, Cell, centre, 6f, 1f, none, (dx, dz) => 1f, 0f, 512f));
    }
}
