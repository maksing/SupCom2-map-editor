using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class TerrainDocumentBridgeTests
{
    private static SC2DirectoryBlobStore NewBlobStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2terrainblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    private static float[] ReadFloats(ISC2BlobStore store, SC2BlobReference reference)
    {
        using var stream = store.OpenRead(reference.ContentHash);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static short[] ReadShorts(ISC2BlobStore store, SC2BlobReference reference)
    {
        using var stream = store.OpenRead(reference.ContentHash);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var shorts = new short[bytes.Length / sizeof(short)];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }

    [Fact]
    public void BuildFromLegacy_ProjectsGridDimensionsAndHeightRangeAndActualBytes()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var heights = new float[] { 100f, 120f, 80f, 100f };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair>(),
                heights, Array.Empty<short>(),
                gridWidth: 1, gridHeight: 1, cellSize: 2f,
                symmetry: SymmetryMode.XY, symmetryCenter: new Vector3(1, 2, 3),
                blobStore: store);

            Assert.Equal(1, doc.Grid.Width);
            Assert.Equal(1, doc.Grid.Height);
            Assert.Equal(2f, doc.Grid.CellSize);
            Assert.Equal(80f, doc.Grid.MinimumHeight);
            Assert.Equal(120f, doc.Grid.MaximumHeight);
            Assert.Equal(heights, ReadFloats(store, doc.Grid.HeightsFloat32));

            Assert.Equal(SymmetryMode.XY, doc.Symmetry.Mode);
            Assert.Equal(new Vector3(1, 2, 3), doc.Symmetry.Center);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_DefaultsIsInitializedAndDataVersion()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair>(),
                new float[] { 0f }, Array.Empty<short>(),
                gridWidth: 1, gridHeight: 1, cellSize: 1f,
                symmetry: SymmetryMode.None, symmetryCenter: Vector3.zero,
                blobStore: store);

            Assert.False(doc.IsInitialized);
            Assert.Equal(0, doc.DataVersion);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_PopulatesIsInitializedAndDataVersionWhenProvided()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair>(),
                new float[] { 0f }, Array.Empty<short>(),
                gridWidth: 1, gridHeight: 1, cellSize: 1f,
                symmetry: SymmetryMode.None, symmetryCenter: Vector3.zero,
                blobStore: store, isInitialized: true, dataVersion: 2);

            Assert.True(doc.IsInitialized);
            Assert.Equal(2, doc.DataVersion);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_AssignsStableIdsAndTheySurviveARepeatCall()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var material = new SC2Material { Name = "Grass" };
            var pair = new SC2MaterialPair { Name = "GrassRock" };
            Assert.True(string.IsNullOrEmpty(material.Id));
            Assert.True(string.IsNullOrEmpty(pair.Id));

            var doc1 = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { material }, new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            var assignedMaterialId = doc1.Materials[0].Id;
            var assignedPairId = doc1.MaterialPairs[0].Id;
            Assert.False(string.IsNullOrEmpty(assignedMaterialId));
            Assert.False(string.IsNullOrEmpty(assignedPairId));

            // Same in-memory objects, called again: the bridge must have
            // written the Ids back onto material/pair, not just the
            // snapshot, or a second call would mint different Ids and the
            // "stable" identity claim would be false.
            var doc2 = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { material }, new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Equal(assignedMaterialId, doc2.Materials[0].Id);
            Assert.Equal(assignedPairId, doc2.MaterialPairs[0].Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_PreservesExistingIds()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var material = new SC2Material { Id = "existing-material-id", Name = "Grass" };
            var pair = new SC2MaterialPair { Id = "existing-pair-id", Name = "GrassRock" };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { material }, new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Equal("existing-material-id", doc.Materials[0].Id);
            Assert.Equal("existing-pair-id", doc.MaterialPairs[0].Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ResolvesMaterialPairLinksByStableId()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var grass = new SC2Material { Id = "grass-id", Name = "Grass" };
            var rock = new SC2Material { Id = "rock-id", Name = "Rock" };
            var pair = new SC2MaterialPair { Id = "pair-id", Name = "GrassRock", Mat1Index = 0, Mat2Index = 1 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { grass, rock }, new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Equal("grass-id", doc.MaterialPairs[0].Material1Id);
            Assert.Equal("rock-id", doc.MaterialPairs[0].Material2Id);
            Assert.NotNull(doc.FindMaterial("grass-id"));
            Assert.NotNull(doc.FindPair("pair-id"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_OutOfRangeOrUnassignedMaterialIndexResolvesToEmptyString()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var pair = new SC2MaterialPair { Mat1Index = -1, Mat2Index = 99 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Equal("", doc.MaterialPairs[0].Material1Id);
            Assert.Equal("", doc.MaterialPairs[0].Material2Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_TrianglePairMap_UsesTombstoneSafeSlotsMatchingPairOrder()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var pairA = new SC2MaterialPair { Id = "pair-a" };
            var pairB = new SC2MaterialPair { Id = "pair-b" };
            // triangle 0 -> pair A, triangle 1 -> pair B, triangle 2 -> unassigned, triangle 3 -> out of range
            var trianglePairIndex = new short[] { 0, 1, -1, 5 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pairA, pairB },
                Array.Empty<float>(), trianglePairIndex, 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Equal(new List<string> { "pair-a", "pair-b" }, doc.PairAssignments.SlotIds);

            var decoded = ReadShorts(store, doc.PairAssignments.SlotIndexPerTriangleInt16);
            Assert.Equal(new short[] { 0, 1, -1, -1 }, decoded); // slot 5 (out of range) safely clamped to -1
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_DedupsSharedRnmMaskNameAcrossPairsIntoOneMaskSet()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var sharedBytes = new byte[] { 1, 2, 3, 4 };
            var pairA = new SC2MaterialPair { Id = "pair-a", RnmMaskName = "sharedMask", RnmMaskBytes = sharedBytes, MaskWidth = 4, MaskHeight = 1 };
            var pairB = new SC2MaterialPair { Id = "pair-b", RnmMaskName = "sharedMask", RnmMaskBytes = sharedBytes, MaskWidth = 4, MaskHeight = 1 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pairA, pairB },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Single(doc.Rnm.MaskSets);
            Assert.Equal("sharedMask", doc.MaterialPairs[0].RnmSetId);
            Assert.Equal("sharedMask", doc.MaterialPairs[1].RnmSetId);

            var maskSet = doc.Rnm.Find("sharedMask");
            Assert.NotNull(maskSet);
            using var stream = store.OpenRead(maskSet.MaskBytes.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(sharedBytes, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_PairWithNoRnmMaskBytesHasNoRnmSetId()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var pair = new SC2MaterialPair { Id = "pair-a", RnmMaskBytes = Array.Empty<byte>() };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.Equal("", doc.MaterialPairs[0].RnmSetId);
            Assert.Empty(doc.Rnm.MaskSets);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_UnassignedTextureSlotsProjectToUnassignedReference()
    {
        var store = NewBlobStore(out var root);
        try
        {
            var material = new SC2Material { Id = "m1", Name = "Grass" }; // Diffuse/Normal/Mask left null

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { material }, new List<SC2MaterialPair>(),
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            Assert.False(doc.Materials[0].Diffuse.IsAssigned);
            Assert.False(doc.Materials[0].Normal.IsAssigned);
            Assert.False(doc.Materials[0].Mask.IsAssigned);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ProjectsImportOnlyModeAndBspOptions()
    {
        // Regression test: ImportOnlyMode/SingleBspNodePerLayer/
        // VanillaCandidateBspSplit must have a document schema home, or they
        // would silently reset to their SC2TerrainData field defaults on
        // every Save/Load cycle through the document package.
        var store = NewBlobStore(out var root);
        try
        {
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair>(),
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store,
                importOnlyMode: true, singleBspNodePerLayer: true, vanillaCandidateBspSplit: false,
                playableAreaName: "MainPlayableArea");

            Assert.True(doc.ImportOnlyMode);
            Assert.True(doc.SingleBspNodePerLayer);
            Assert.False(doc.VanillaCandidateBspSplit);
            Assert.Equal("MainPlayableArea", doc.PlayableAreaName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ThrowsOnNullArguments()
    {
        var store = NewBlobStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => SC2TerrainLegacyBridge.BuildFromLegacy(
                null, new List<SC2MaterialPair>(), Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store));
            Assert.Throws<ArgumentNullException>(() => SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair>(), Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
