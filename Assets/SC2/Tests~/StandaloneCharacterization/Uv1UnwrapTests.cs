using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>The seam skirt reaches one coarse cell past the core terrain, so the UV1 profile has to
/// continue past the grid edge instead of clamping to it (which collapsed the texture into streaks).</summary>
public class Uv1UnwrapTests
{
    // 4x4 cells, cell size 2; flat terrain -> u and v grow by 2 per cell.
    private static void FlatProfiles(out float[] u, out float[] v)
    {
        var heights = new float[5 * 5];
        SC2TerrainUv1Unwrap.BuildProfiles(heights, 4, 4, 2f, out u, out v);
    }

    [Fact]
    public void SampleExtended_EqualsSample_InsideTheGrid()
    {
        FlatProfiles(out var u, out var v);
        var a = SC2TerrainUv1Unwrap.Sample(u, v, 4, 4, 2f, 3f, 5f);
        var b = SC2TerrainUv1Unwrap.SampleExtended(u, v, 4, 4, 2f, 3f, 5f);
        Assert.Equal(a.x, b.x, 4);
        Assert.Equal(a.y, b.y, 4);
    }

    [Fact]
    public void Sample_ClampsAtTheEdge_ButSampleExtendedKeepsGrowing()
    {
        FlatProfiles(out var u, out var v);
        // grid spans 0..8; sample 3 units past the +X edge and 2 units before the -Z edge
        var clamped = SC2TerrainUv1Unwrap.Sample(u, v, 4, 4, 2f, 11f, 4f);
        var extended = SC2TerrainUv1Unwrap.SampleExtended(u, v, 4, 4, 2f, 11f, 4f);
        Assert.Equal(8f, clamped.x, 4);              // stuck at the edge value
        Assert.Equal(11f, extended.x, 3);            // 1 unit of u per world unit on flat ground
        Assert.Equal(clamped.y, extended.y, 4);      // the other axis is inside and unchanged

        var before = SC2TerrainUv1Unwrap.SampleExtended(u, v, 4, 4, 2f, 4f, -2f);
        Assert.Equal(-2f, before.y, 3);
    }

    [Fact]
    public void SampleExtended_UsesTheLastCellsSlope_OnSteepGround()
    {
        // one steep column at the +X edge: the last cell's u step is larger than the flat 2
        var heights = new float[5 * 5];
        for (var z = 0; z < 5; z++) heights[z * 5 + 4] = 6f;
        SC2TerrainUv1Unwrap.BuildProfiles(heights, 4, 4, 2f, out var u, out var v);
        var lastStep = u[4] - u[3];
        Assert.True(lastStep > 2.5f);
        var atEdge = SC2TerrainUv1Unwrap.SampleExtended(u, v, 4, 4, 2f, 8f, 4f).x;
        var past = SC2TerrainUv1Unwrap.SampleExtended(u, v, 4, 4, 2f, 10f, 4f).x; // one cell past
        Assert.Equal(lastStep, past - atEdge, 3);
    }
}
