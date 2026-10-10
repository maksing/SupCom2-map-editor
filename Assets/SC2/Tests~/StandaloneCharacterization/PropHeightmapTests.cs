using BdfIO.Formats.Hfield;
using BdfIO.Formats.Terrain;
using UnityEngine;
using Xunit;

// SC2HfieldProjectionService reads one constant of SC2TerrainData (a ScriptableObject this project cannot compile); this stands in for it.
public static class SC2TerrainData { public const ushort DefaultTerrainTypeId = 2; }

public class PropHeightmapTests
{
    // A flat ground model 0..16 at height 10 (two triangles).
    private static TerrainModel Ground()
    {
        var model = new TerrainModel();
        model.Materials.Add(new TerrainMaterial { Name = "ground" });
        model.Layers.Add(new TerrainLayer { CumulativeVertices = 0, CumulativeIndices = 0 });
        foreach (var (x, z) in new[] { (0f, 0f), (16f, 0f), (16f, 16f), (0f, 16f) })
            model.Vertices.Add(new TerrainVertex { PosX = x, PosY = 10f, PosZ = z });
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 1, V2 = 2 });
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 2, V2 = 3 });
        return model;
    }

    // A flat 4 x 4 roof (two triangles) at height 3 over its own origin.
    private static readonly float[] RoofPositions = { -2, 3, -2, 2, 3, -2, 2, 3, 2, -2, 3, 2 };
    private static readonly ushort[] RoofIndices = { 0, 1, 2, 0, 2, 3 };

    private static float HeightAt(HfieldModel hfield, int x, int z) => hfield.Heightmap[z * (int)hfield.MapX + x] / 128f;

    [Fact]
    public void Ground_alone_gives_the_ground_height()
    {
        var hfield = SC2HfieldProjectionService.Project(Ground(), null, 17, 17);
        Assert.Equal(10f, HeightAt(hfield, 8, 8), 2);
    }

    [Fact]
    public void An_included_roof_raises_the_cells_under_it_and_leaves_the_rest()
    {
        // The roof's origin sits on the ground at (8, 8) and its Y is 3 above that, i.e. at 13.
        var roof = SC2HfieldProjectionService.BuildSolid(RoofPositions, RoofIndices, new Vector3(8f, 10f, 8f), Quaternion.identity, 1f);
        var hfield = SC2HfieldProjectionService.Project(Ground(), null, 17, 17, extraSolids: new[] { roof });
        Assert.Equal(13f, HeightAt(hfield, 8, 8), 2);
        Assert.Equal(10f, HeightAt(hfield, 2, 2), 2);
    }

    [Fact]
    public void A_solid_lower_than_the_ground_does_not_dig_into_it()
    {
        var pit = SC2HfieldProjectionService.BuildSolid(RoofPositions, RoofIndices, new Vector3(8f, 0f, 8f), Quaternion.identity, 1f);
        var hfield = SC2HfieldProjectionService.Project(Ground(), null, 17, 17, extraSolids: new[] { pit });
        Assert.Equal(10f, HeightAt(hfield, 8, 8), 2);
    }

    [Fact]
    public void The_solid_is_scaled_and_turned_with_the_prop()
    {
        // Twice the size: the roof now spans 8 x 8 and sits at 3 * 2 above the origin.
        var big = SC2HfieldProjectionService.BuildSolid(RoofPositions, RoofIndices, new Vector3(8f, 10f, 8f), Quaternion.identity, 2f);
        var hfield = SC2HfieldProjectionService.Project(Ground(), null, 17, 17, extraSolids: new[] { big });
        Assert.Equal(16f, HeightAt(hfield, 5, 5), 2); // inside the 8 x 8 footprint, outside the 4 x 4 one
        Assert.Equal(10f, HeightAt(hfield, 2, 2), 2);

        // A quarter turn about Y keeps a square roof where it was: the roof is symmetric, so the cell under its centre stays at 13.
        var turned = SC2HfieldProjectionService.BuildSolid(RoofPositions, RoofIndices, new Vector3(8f, 10f, 8f), new Quaternion(0f, 0.70710678f, 0f, 0.70710678f), 1f);
        var turnedField = SC2HfieldProjectionService.Project(Ground(), null, 17, 17, extraSolids: new[] { turned });
        Assert.Equal(13f, HeightAt(turnedField, 8, 8), 2);
    }
}

public class PropHeightmapMergeTests
{
    private static HfieldModel Flat(int size, float height)
    {
        var heights = new ushort[size * size];
        for (var i = 0; i < heights.Length; i++) heights[i] = (ushort)(height * 128f);
        return new HfieldModel { MapX = (uint)size, MapY = (uint)size, Heightmap = heights };
    }

    private static readonly float[] Roof = { -2, 3, -2, 2, 3, -2, 2, 3, 2, -2, 3, 2 };
    private static readonly ushort[] RoofIndices = { 0, 1, 2, 0, 2, 3 };

    [Fact]
    public void A_solid_is_merged_into_a_finished_heightmap()
    {
        var hfield = Flat(17, 10f);
        var roof = SC2HfieldProjectionService.BuildSolid(Roof, RoofIndices, new Vector3(8f, 10f, 8f), Quaternion.identity, 1f);
        SC2HfieldProjectionService.MergeSolids(hfield, new[] { roof });
        Assert.Equal(13f, hfield.Heightmap[8 * 17 + 8] / 128f, 2);
        Assert.Equal(10f, hfield.Heightmap[2 * 17 + 2] / 128f, 2);
    }

    [Fact]
    public void A_solid_below_the_heightmap_leaves_it_as_it_was()
    {
        var hfield = Flat(17, 10f);
        var pit = SC2HfieldProjectionService.BuildSolid(Roof, RoofIndices, new Vector3(8f, 0f, 8f), Quaternion.identity, 1f);
        SC2HfieldProjectionService.MergeSolids(hfield, new[] { pit });
        Assert.Equal(10f, hfield.Heightmap[8 * 17 + 8] / 128f, 2);
    }
}
