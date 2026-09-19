using System.Collections.Generic;
using System.IO;
using System.Linq;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>RNM lighting baked onto layers of imported terrain.bdf objects by
/// their own UV2: the rasteriser (SC2RnmMaskBaker), the imported-mask
/// document/command/serialization and the helpers around them.</summary>
public class ImportedRnmBakeTests
{
    private const int Size = 32;

    /// <summary>A flat, upward-facing quad covering UV2 u in [u0, u1], v in
    /// [0, 1], appended to <paramref name="model"/> as its own layer.</summary>
    private static void AddQuadLayer(TerrainModel model, float u0, float u1)
    {
        var layer = new TerrainLayer
        {
            CumulativeVertices = model.Vertices.Count,
            CumulativeIndices = model.Triangles.Count * 3,
        };
        model.Layers.Add(layer);
        model.Materials.Add(new TerrainMaterial { Name = "layer" + model.Layers.Count });

        TerrainVertex V(float u, float v) => new()
        {
            PosX = u * 64f, PosY = 0f, PosZ = v * 64f,
            Uv1U = u * 4f, Uv1V = v * 4f, // real UV1 gradient -> a usable tangent
            Uv2U = u, Uv2V = v,
            NormalX = 0f, NormalY = 1f, NormalZ = 0f,
        };
        model.Vertices.Add(V(u0, 0f));
        model.Vertices.Add(V(u1, 0f));
        model.Vertices.Add(V(u1, 1f));
        model.Vertices.Add(V(u0, 1f));
        // Triangle indices are local to the layer's own vertex range.
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 1, V2 = 2 });
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 2, V2 = 3 });
    }

    private static SC2RnmMaskBaker.Settings OverheadSun() => new()
    {
        ToSun = Vector3.up, // straight overhead: nothing can occlude it, no heightmap needed
        Hfield = null,
        ShadowSoftRadius = 0,
        ShadowSoftRadiusUnderwater = 0,
        SunColor = Color.white,
        ShadowColor = Color.black,
        SunIntensity = 1f,
    };

    private static SC2RnmMaskBaker.MaskResult Bake(TerrainModel model, IReadOnlyList<int> layers,
        System.Func<int, System.Func<float, float, byte>> blend = null) =>
        SC2RnmMaskBaker.BakeMaskGroup(model, SC2RnmMaskBaker.ComputeVertexTangents(model), layers, Size, Size, OverheadSun(), blend);

    private static TerrainModel TwoIslands()
    {
        var model = new TerrainModel { Version = 6 };
        AddQuadLayer(model, 0f, 0.5f);   // layer 0: left half of the mask
        AddQuadLayer(model, 0.5f, 1f);   // layer 1: right half
        return model;
    }

    // ---- the rasteriser

    [Fact]
    public void BakeMaskGroup_LightsExactlyTheTexelsTheLayersUv2Covers()
    {
        var model = TwoIslands();

        var left = Bake(model, new[] { 0 });

        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var covered = left.Covered[y * Size + x];
            if (x < Size / 2 - 1) Assert.True(covered, $"texel ({x},{y}) is inside layer 0's UV2 island");
            if (x > Size / 2 + 1) Assert.False(covered, $"texel ({x},{y}) is outside layer 0's UV2 island");
        }
        // Baked light: overhead sun on an upward normal, all three slices lit.
        var i = (Size / 4) * Size + Size / 4;
        Assert.True(left.Rgb0[i * 3] > 40);
        Assert.InRange(left.Rgb1[i * 3], left.Rgb0[i * 3] - 1, left.Rgb0[i * 3] + 1);
        Assert.InRange(left.Rgb2[i * 3], left.Rgb0[i * 3] - 1, left.Rgb0[i * 3] + 1);
        Assert.Equal(255, left.ShadowAlpha[i]); // unshadowed
    }

    [Fact]
    public void BakeMaskGroup_TwoLayersOnOneMask_FillItBetweenThem_WithoutClobbering()
    {
        var model = TwoIslands();

        var both = Bake(model, new[] { 0, 1 });
        var onlyLeft = Bake(model, new[] { 0 });
        var onlyRight = Bake(model, new[] { 1 });

        // Every texel each layer covers on its own is covered in the shared
        // bake, with the same light — sharing a mask changes nothing.
        for (var t = 0; t < Size * Size; t++)
        {
            Assert.Equal(onlyLeft.Covered[t] || onlyRight.Covered[t], both.Covered[t]);
            if (onlyLeft.Covered[t]) Assert.Equal(onlyLeft.Rgb0[t * 3], both.Rgb0[t * 3]);
            if (onlyRight.Covered[t]) Assert.Equal(onlyRight.Rgb0[t * 3], both.Rgb0[t * 3]);
        }
        // Different layers, different islands: the two covered sets barely touch.
        var overlap = Enumerable.Range(0, Size * Size).Count(t => onlyLeft.Covered[t] && onlyRight.Covered[t]);
        Assert.True(overlap <= Size * 2, "islands share at most the seam column");
    }

    [Fact]
    public void BakeMaskGroup_WithNoLayers_WritesNothing()
    {
        var result = Bake(TwoIslands(), new int[0]);
        Assert.All(result.Covered, c => Assert.False(c));
        Assert.All(result.Blend, b => Assert.Equal(0, b));
    }

    [Fact]
    public void BakeMaskGroup_CarriesTheBlendAlphaOfEachLayersSampler()
    {
        var model = TwoIslands();

        var result = Bake(model, new[] { 0, 1 }, li => li == 0 ? (u, v) => (byte)200 : null);

        var leftTexel = (Size / 4) * Size + Size / 4;
        var rightTexel = (Size / 4) * Size + 3 * Size / 4;
        Assert.Equal(200, result.Blend[leftTexel]);   // layer 0 had a sampler
        Assert.Equal(0, result.Blend[rightTexel]);    // layer 1 had none
    }

    [Fact]
    public void BakeMaskGroup_SamplerReceivesTheTexelsUv2()
    {
        var model = TwoIslands();
        var seen = new List<(float u, float v)>();

        Bake(model, new[] { 0 }, _ => (u, v) => { seen.Add((u, v)); return 1; });

        Assert.NotEmpty(seen);
        Assert.All(seen, s => { Assert.InRange(s.u, 0f, 0.55f); Assert.InRange(s.v, 0f, 1f); });
    }

    // ---- masks: names, files, bake state

    [Fact]
    public void NextName_PicksTheFirstFreeNumber()
    {
        var sets = new List<SC2RnmMaskSetDocument> { new() { Name = "imported01" }, new() { Name = "IMPORTED02" } };
        Assert.Equal("imported03", SC2ImportedRnmMasks.NextName(sets));
        Assert.Equal("imported01", SC2ImportedRnmMasks.NextName(new List<SC2RnmMaskSetDocument>()));
    }

    [Theory]
    [InlineData("imported01", "imp_imported01")]
    [InlineData("my mask!", "imp_my_mask_")]
    [InlineData("Ünï", "imp__n_")]
    [InlineData("", "imp_imported")]
    public void ExportStem_IsPrefixedAndFileSafe(string name, string expected) =>
        Assert.Equal(expected, SC2ImportedRnmMasks.ExportStem(name));

    [Fact]
    public void HasBake_NeedsAllFourArraysAtTheMasksResolution()
    {
        var n = 16 * 16;
        var set = new SC2RnmMaskSetDocument
        {
            Name = "m", Width = 16, Height = 16,
            BakedRgb0 = new SC2BlobReference("a", n * 3), BakedRgb1 = new SC2BlobReference("b", n * 3),
            BakedRgb2 = new SC2BlobReference("c", n * 3), BakedShadowAlpha = new SC2BlobReference("d", n),
        };
        Assert.True(SC2ImportedRnmMasks.HasBake(set));

        set.BakedShadowAlpha = default;
        Assert.False(SC2ImportedRnmMasks.HasBake(set));

        set.BakedShadowAlpha = new SC2BlobReference("d", n);
        set.Width = 32; // resolution changed after the bake
        Assert.False(SC2ImportedRnmMasks.HasBake(set));
        Assert.False(SC2ImportedRnmMasks.HasBake(null));
    }

    [Fact]
    public void BlobRoundTrip_StoresAndReadsTheBakedBytes()
    {
        var root = Directory.CreateTempSubdirectory("sc2impmask_").FullName;
        try
        {
            var store = new SC2DirectoryBlobStore(root);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var reference = SC2ImportedRnmMasks.Put(store, bytes);
            Assert.Equal(bytes, SC2ImportedRnmMasks.Read(store, reference));
            Assert.Null(SC2ImportedRnmMasks.Read(store, SC2BlobReference.Empty));
            Assert.True(SC2ImportedRnmMasks.Put(store, new byte[0]).IsEmpty);
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- command

    [Fact]
    public void Command_DoUndoRedo_ReplacesTheImportedMasks_WithoutTouchingTheCoreOnes()
    {
        var document = new SC2MapDocument();
        document.Terrain.Rnm.MaskSets.Add(new SC2RnmMaskSetDocument { Id = "mask01", Name = "mask01", Width = 64, Height = 64 });
        var history = new SC2CommandHistory();
        var next = new List<SC2RnmMaskSetDocument> { new() { Id = "imported01", Name = "imported01", Width = 1024, Height = 1024 } };

        history.Execute(document, new SC2SetImportedRnmMasksCommand(next));
        Assert.Equal(new[] { "imported01" }, document.Terrain.ImportedRnm.MaskSets.Select(s => s.Name));
        Assert.Equal(new[] { "mask01" }, document.Terrain.Rnm.MaskSets.Select(s => s.Name));

        next[0].Name = "changed-after"; // caller's list must not leak in
        Assert.Equal("imported01", document.Terrain.ImportedRnm.MaskSets[0].Name);

        history.Undo(document);
        Assert.Empty(document.Terrain.ImportedRnm.MaskSets);
        history.Redo(document);
        Assert.Single(document.Terrain.ImportedRnm.MaskSets);
    }

    [Fact]
    public void Command_DirtiesTerrainMaterials()
    {
        var change = new SC2SetImportedRnmMasksCommand(new List<SC2RnmMaskSetDocument>()).Do(new SC2MapDocument());
        Assert.Equal(SC2DirtyFlags.TerrainMaterials, change.DirectDirtyFlags);
        Assert.Equal(new[] { SC2DomainId.Terrain }, change.ChangedDomains);
    }

    // ---- serialization

    [Fact]
    public void Serializer_RoundTripsImportedMasks_AndPerLayerTargets()
    {
        var doc = new SC2MapDocument();
        doc.Terrain.ImportedRnm.MaskSets.Add(new SC2RnmMaskSetDocument
        {
            Id = "imported01", Name = "imported01", Width = 1024, Height = 1024,
            MaskBytes = new SC2BlobReference("mask-hash", 4096),
            BakedRgb0 = new SC2BlobReference("r0", 100), BakedRgb1 = new SC2BlobReference("r1", 100),
            BakedRgb2 = new SC2BlobReference("r2", 100), BakedShadowAlpha = new SC2BlobReference("sa", 33),
        });
        var entry = new SC2ImportedTerrainEntryDocument { Id = "e1", DisplayName = "Crystal" };
        entry.MaterialOverrides.Add(new SC2ImportedTerrainMaterialOverrideDocument { RnmBakeMaskName = "imported01" });
        entry.MaterialOverrides.Add(new SC2ImportedTerrainMaterialOverrideDocument()); // layer 1: not baked
        doc.Terrain.ImportedEntries.Add(entry);

        var read = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        var set = Assert.Single(read.Terrain.ImportedRnm.MaskSets);
        Assert.Equal("imported01", set.Name);
        Assert.Equal(1024, set.Width);
        Assert.Equal("r1", set.BakedRgb1.ContentHash);
        Assert.Equal(33, set.BakedShadowAlpha.Length);
        Assert.Equal("mask-hash", set.MaskBytes.ContentHash);
        var overrides = read.Terrain.ImportedEntries.Single().MaterialOverrides;
        Assert.Equal("imported01", overrides[0].RnmBakeMaskName);
        Assert.Equal("", overrides[1].RnmBakeMaskName);
        Assert.Empty(read.Terrain.Rnm.MaskSets); // never mixed with the core masks
    }

    [Fact]
    public void Serializer_ASaveWithoutTheKeysLoadsWithNoImportedMasks()
    {
        var read = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(new SC2MapDocument()));
        Assert.Empty(read.Terrain.ImportedRnm.MaskSets);
    }

    [Fact]
    public void MaterialOverride_CloneKeepsTheTarget()
    {
        var clone = new SC2ImportedTerrainMaterialOverrideDocument { RnmBakeMaskName = "imported02" }.Clone();
        Assert.Equal("imported02", clone.RnmBakeMaskName);
    }
}
