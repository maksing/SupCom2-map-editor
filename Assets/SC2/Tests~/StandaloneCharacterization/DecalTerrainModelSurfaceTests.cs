using System.Collections.Generic;
using System.Linq;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class DecalTerrainModelSurfaceTests
{
    private static TerrainVertex V(float x, float y, float z) => new()
    {
        PosX = x, PosY = y, PosZ = z, NormalX = 0f, NormalY = 1f, NormalZ = 0f
    };

    // Layer 0: a 10x10 ground square wound DOWNWARD (opposite of what the decal
    // builder expects); layer 1: a water square 1 unit above it.
    private static TerrainModel Model()
    {
        var model = new TerrainModel();
        model.Materials.Add(new TerrainMaterial { Name = "grass" });
        model.Materials.Add(new TerrainMaterial { Name = "water_plane" });
        model.Layers.Add(new TerrainLayer { CumulativeVertices = 0, CumulativeIndices = 0 });
        model.Layers.Add(new TerrainLayer { CumulativeVertices = 4, CumulativeIndices = 6 });
        foreach (var y in new[] { 0f, 1f })
        {
            model.Vertices.Add(V(0, y, 0)); model.Vertices.Add(V(10, y, 0));
            model.Vertices.Add(V(10, y, 10)); model.Vertices.Add(V(0, y, 10));
            model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 1, V2 = 2 });
            model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 2, V2 = 3 });
        }
        return model;
    }

    private static readonly SC2DecalTerrainModelSurface.Box Everything = new(-100, -100, -100, 100, 100, 100);

    private static bool IsWater(TerrainMaterial m) => m != null && m.Name.Contains("water");

    private static float Upwardness(SC2DecalSurfaceTriangle t) =>
        (t.BZ - t.AZ) * (t.CX - t.AX) - (t.BX - t.AX) * (t.CZ - t.AZ);

    [Fact]
    public void WaterLayers_AreLeftOut()
    {
        var all = SC2DecalTerrainModelSurface.Collect(Model(), null, new[] { Everything });
        var dry = SC2DecalTerrainModelSurface.Collect(Model(), IsWater, new[] { Everything });
        Assert.Equal(4, all.Count);
        Assert.Equal(2, dry.Count);
        Assert.All(dry, t => Assert.Equal(0f, t.AY));
    }

    [Fact]
    public void Triangles_AreOrientedByTheirNormals_NotByTheModelsOwnWinding()
    {
        var dry = SC2DecalTerrainModelSurface.Collect(Model(), IsWater, new[] { Everything });
        Assert.All(dry, t => Assert.True(Upwardness(t) > 0f, "decal builder needs Cross(B-A, C-A) to face the surface normal"));
        Assert.All(dry, t => Assert.True(t.HasNormals));
    }

    [Fact]
    public void TrianglesOutsideEveryPlacementBox_AreDropped()
    {
        var far = new SC2DecalTerrainModelSurface.Box(500, -10, 500, 510, 10, 510);
        Assert.Empty(SC2DecalTerrainModelSurface.Collect(Model(), IsWater, new[] { far }));
        var near = new SC2DecalTerrainModelSurface.Box(1, -1, 1, 2, 1, 2);
        Assert.NotEmpty(SC2DecalTerrainModelSurface.Collect(Model(), IsWater, new[] { far, near }));
    }

    [Fact]
    public void ExportGround_ProducesDecalMeshThatFacesUp()
    {
        var document = new SC2DecalsDocument { AtlasWidth = 256, AtlasHeight = 256 };
        document.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", X = 0, Y = 0, Width = 64, Height = 64 });
        document.Placements.Add(new SC2DecalPlacement
        {
            Id = "p", AtlasEntryId = "entry", Position = new[] { 5f, 0f, 5f },
            SurfaceNormal = new[] { 0f, 1f, 0f }, Width = 4f, Length = 4f, ProjectionDepth = 4f, HeightOffset = 0f
        });

        var ground = SC2DecalTerrainModelSurface.Collect(Model(), IsWater, new[] { Everything });
        var decal = SC2DecalGeometryBuilder.Build(document, ground);

        Assert.NotEmpty(decal.Indices);
        var facingUp = 0;
        for (var i = 0; i < decal.Indices.Count; i += 3)
        {
            var a = decal.Vertices[decal.Indices[i]]; var b = decal.Vertices[decal.Indices[i + 1]]; var c = decal.Vertices[decal.Indices[i + 2]];
            var up = (b.PositionZ - a.PositionZ) * (c.PositionX - a.PositionX) - (b.PositionX - a.PositionX) * (c.PositionZ - a.PositionZ);
            // A clip can leave a zero-area sliver; only a truly downward triangle is wrong.
            Assert.True(up > -1e-4f, $"decal triangle {i / 3} winds away from the camera ({up})");
            if (up > 1e-4f) facingUp++;
        }
        Assert.True(facingUp > 0);
        Assert.All(decal.Vertices, v => Assert.Equal(0f, v.PositionY, 3));
    }
}
