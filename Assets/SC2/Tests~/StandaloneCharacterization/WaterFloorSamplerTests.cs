using BdfIO.Formats.Terrain;
using Xunit;

namespace SC2CharacterizationTests;

public class WaterFloorSamplerTests
{
    private const float Water = 5f;

    /// <summary>A flat quad (two triangles) covering [x0, x1] x [z0, z1] at height y, appended to the model as its own layer.</summary>
    private static void AddQuad(TerrainModel model, string name, float x0, float z0, float x1, float z1, float y)
    {
        model.Layers.Add(new TerrainLayer { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 });
        model.Materials.Add(new TerrainMaterial { Name = name });
        TerrainVertex V(float x, float z) => new() { PosX = x, PosY = y, PosZ = z };
        model.Vertices.Add(V(x0, z0)); model.Vertices.Add(V(x1, z0)); model.Vertices.Add(V(x1, z1)); model.Vertices.Add(V(x0, z1));
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 1, V2 = 2 });
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 2, V2 = 3 });
    }

    private static bool IsWater(TerrainMaterial m) => m != null && m.Name == "Water";
    private static float Fallback(float x, float z) => 999f;

    private static SC2WaterFloorSampler Build(TerrainModel model) => SC2WaterFloorSampler.Build(model, 21, 21, Water, IsWater);

    [Fact]
    public void ABridgeDeckOverWaterDoesNotHideTheWaterFloorUnderIt()
    {
        var model = new TerrainModel();
        AddQuad(model, "Ground", 0, 0, 20, 20, 0f);      // the river bed, below the water at 5
        AddQuad(model, "Bridge", 4, 0, 16, 20, 12f);     // a deck high above the water
        var sampler = Build(model);
        Assert.Equal(0f, sampler.Sample(10f, 10f, Fallback), 3); // under the deck: the bed, so it is water
        Assert.Equal(0f, sampler.Sample(2f, 10f, Fallback), 3);
    }

    [Fact]
    public void APlainTerrainReadsExactlyItsOwnHeight()
    {
        var model = new TerrainModel();
        AddQuad(model, "Ground", 0, 0, 20, 20, 9f);       // land, above the water: its own height, no floor to prefer
        Assert.Equal(9f, Build(model).Sample(7.5f, 3.25f, Fallback), 3);
    }

    [Fact]
    public void ADeckOverLandIsStillLand()
    {
        var model = new TerrainModel();
        AddQuad(model, "Ground", 0, 0, 20, 20, 9f);
        AddQuad(model, "Bridge", 4, 0, 16, 20, 12f);
        // Both surfaces are above the water, so the column is land; the highest one is what counts.
        Assert.True(Build(model).Sample(10f, 10f, Fallback) > Water);
    }

    [Fact]
    public void TheWaterPlaneItselfIsNotASurface()
    {
        var model = new TerrainModel();
        AddQuad(model, "Ground", 0, 0, 20, 20, 9f);
        AddQuad(model, "Water", 0, 0, 20, 20, 5f);
        Assert.Equal(9f, Build(model).Sample(10f, 10f, Fallback), 3);
    }

    [Fact]
    public void AColumnNoTriangleCoversAsksTheFallback()
    {
        var model = new TerrainModel();
        AddQuad(model, "Ground", 0, 0, 10, 20, 9f);
        var sampler = Build(model);
        Assert.Equal(999f, sampler.Sample(18f, 10f, Fallback));
        Assert.Equal(9f, sampler.Sample(2f, 10f, Fallback), 3);
    }
}
