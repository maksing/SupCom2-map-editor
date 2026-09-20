using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class MapDocumentToLegacyBridgeTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2reversebridgeblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    // ---- Terrain ----

    [Fact]
    public void ReconstructTerrain_RoundTripsGridHeightsSymmetryAndFlags()
    {
        var store = NewStore(out var root);
        try
        {
            var heights = new float[] { 10f, 20f, 30f };
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair>(),
                heights, Array.Empty<short>(),
                gridWidth: 4, gridHeight: 5, cellSize: 2.5f,
                symmetry: SymmetryMode.Radial, symmetryCenter: new Vector3(1, 2, 3),
                blobStore: store, isInitialized: true, dataVersion: 7, radialCount: 4,
                importOnlyMode: true, singleBspNodePerLayer: true, vanillaCandidateBspSplit: false,
                playableAreaName: "MainPlayableArea");

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal(heights, result.Heights);
            Assert.Equal(4, result.GridWidth);
            Assert.Equal(5, result.GridHeight);
            Assert.Equal(2.5f, result.CellSize);
            Assert.Equal(SymmetryMode.Radial, result.Symmetry);
            Assert.Equal(new Vector3(1, 2, 3), result.SymmetryCenter);
            Assert.Equal(4, result.RadialCount);
            Assert.True(result.IsInitialized);
            Assert.Equal(7, result.DataVersion);
            // ImportOnlyMode/SingleBspNodePerLayer/VanillaCandidateBspSplit
            // must have a document schema home, not fall through unmapped.
            Assert.True(result.ImportOnlyMode);
            Assert.True(result.SingleBspNodePerLayer);
            Assert.False(result.VanillaCandidateBspSplit);
            Assert.Equal("MainPlayableArea", result.PlayableAreaName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_RoundTripsMaterialsAndResolvesPairLinksBackToIndices()
    {
        var store = NewStore(out var root);
        try
        {
            var grass = new SC2Material { Id = "grass-id", Name = "Grass" };
            var rock = new SC2Material { Id = "rock-id", Name = "Rock" };
            var pair = new SC2MaterialPair { Id = "pair-id", Name = "GrassRock", Mat1Index = 0, Mat2Index = 1, TilingScale = 8f, SimplifyRatio = 0.5f };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { grass, rock }, new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal(2, result.Materials.Count);
            Assert.Equal("grass-id", result.Materials[0].Id);
            Assert.Equal("Grass", result.Materials[0].Name);
            Assert.Equal("rock-id", result.Materials[1].Id);

            Assert.Single(result.Pairs);
            Assert.Equal("pair-id", result.Pairs[0].Id);
            Assert.Equal(0, result.Pairs[0].Mat1Index);
            Assert.Equal(1, result.Pairs[0].Mat2Index);
            Assert.Equal(8f, result.Pairs[0].TilingScale);
            Assert.Equal(0.5f, result.Pairs[0].SimplifyRatio);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_CustomImportedTextureReference_ReassemblesFileTagFromGamePath()
    {
        // Mirrors SC2MaterialsPairsMode.BuildTextureReference's own split for
        // a Custom-imported pick: no real Resources asset exists for it, so
        // ResourcePath is left empty and the real TextureLibrary file path
        // goes into GamePath instead. Regression coverage for a real bug:
        // ToSlotInfo used to read ResourcePath only, silently dropping
        // GamePath, so a material's imported Diffuse/Normal/Mask pick came
        // back with an empty path on the very next load even though the
        // .dds file was still on disk.
        var store = NewStore(out var root);
        try
        {
            var grass = new SC2Material { Id = "grass-id", Name = "Grass" };
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { grass }, new List<SC2MaterialPair>(),
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);
            doc.Materials[0].Diffuse = new SC2TextureReference
            {
                Name = "sand_abc1234567",
                ResourcePath = "",
                GamePath = "C:/Users/user/AppData/LocalLow/DefaultCompany/SupCom2 map editor/TextureLibrary/sand_abc1234567.dds",
                Source = "Custom",
            };

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal("sand_abc1234567", result.MaterialDiffuse[0].Name);
            Assert.Equal(
                "file:C:/Users/user/AppData/LocalLow/DefaultCompany/SupCom2 map editor/TextureLibrary/sand_abc1234567.dds",
                result.MaterialDiffuse[0].ResourcePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_ForceExportFlag_IsCarriedPerSlot_AndDefaultsToFalse()
    {
        var store = NewStore(out var root);
        try
        {
            var grass = new SC2Material { Id = "grass-id", Name = "Grass", NormalForceExport = true };
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { grass }, new List<SC2MaterialPair>(),
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);
            // The forward (legacy -> document) bridge keeps the flag even with no texture assigned.
            Assert.False(doc.Materials[0].Diffuse.ForceExport);
            Assert.True(doc.Materials[0].Normal.ForceExport);
            Assert.False(doc.Materials[0].Mask.ForceExport);

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.False(result.MaterialDiffuse[0].ForceExport);
            Assert.True(result.MaterialNormal[0].ForceExport);
            Assert.False(result.MaterialMask[0].ForceExport);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EnvironmentTextureExport_RoundTripsThroughTheLegacyBridge()
    {
        var tags = new[] { "", "archive:textures/env/sky.win.dds", "", "", "" };
        var force = new[] { false, true, false, false, false };
        var header = new byte[SC2EnvironmentDocument.HeaderSize];

        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(header, "", "/textures/env/sky.dds", "", "", "",
            textureSourceTags: tags, textureForceExport: force);
        Assert.True(doc.TextureExport.GetForce(SC2EnvironmentTextureSlot.SkyMap));
        Assert.Equal("archive:textures/env/sky.win.dds", doc.TextureExport.GetTag(SC2EnvironmentTextureSlot.SkyMap));

        var result = SC2MapDocumentToLegacyBridge.ReconstructEnvironment(doc);
        Assert.True(result.TextureExport.GetForce(SC2EnvironmentTextureSlot.SkyMap));
        Assert.False(result.TextureExport.GetForce(SC2EnvironmentTextureSlot.EnvironmentMap));

        // Old data with no arrays at all must come out as five unchecked, empty slots.
        var legacy = SC2EnvironmentLegacyBridge.BuildFromLegacy(header, "", "", "", "", "");
        for (var i = 0; i < SC2EnvironmentTextureExport.SlotCount; i++)
        {
            Assert.False(legacy.TextureExport.GetForce((SC2EnvironmentTextureSlot)i));
            Assert.Equal("", legacy.TextureExport.GetTag((SC2EnvironmentTextureSlot)i));
        }
    }

    [Fact]
    public void ReconstructTerrain_NonCustomTextureReference_ResourcePathPassesThroughUnchanged()
    {
        // A Vanilla/Map-sourced pick already carries its full "archive:..."
        // (or bare resource) tag directly in ResourcePath (see
        // SC2MaterialsPairsMode.BuildTextureReference's non-custom branch) —
        // must NOT be reinterpreted or altered just because GamePath also
        // happens to be populated (a real catalog entry has both).
        var store = NewStore(out var root);
        try
        {
            var grass = new SC2Material { Id = "grass-id", Name = "Grass" };
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material> { grass }, new List<SC2MaterialPair>(),
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);
            doc.Materials[0].Diffuse = new SC2TextureReference
            {
                Name = "sandstone_001a_d",
                ResourcePath = "archive:textures/Terrain/sandstone_001a_d.win.dds",
                GamePath = "/textures/Terrain/sandstone_001a_d.win.dds",
                Source = "Vanilla",
            };

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal("archive:textures/Terrain/sandstone_001a_d.win.dds", result.MaterialDiffuse[0].ResourcePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_UnresolvableMaterialLinkBecomesUnassignedIndex()
    {
        var store = NewStore(out var root);
        try
        {
            // Pair references a material Id that no longer exists in the
            // document's Materials list (e.g. the material was deleted) —
            // must come back -1 (unassigned), not throw or guess.
            var pair = new SC2MaterialPair { Id = "pair-id", Mat1Index = -1, Mat2Index = -1 };
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);
            doc.MaterialPairs[0].Material1Id = "does-not-exist";

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal(-1, result.Pairs[0].Mat1Index);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_RoundTripsTrianglePairIndexIncludingUnassignedSlots()
    {
        var store = NewStore(out var root);
        try
        {
            var pairA = new SC2MaterialPair { Id = "pair-a" };
            var pairB = new SC2MaterialPair { Id = "pair-b" };
            var trianglePairIndex = new short[] { 0, 1, -1, 0 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pairA, pairB },
                Array.Empty<float>(), trianglePairIndex, 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal(trianglePairIndex, result.TrianglePairIndex);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_TombstonedPairDeletedAfterSlotAssignment_BecomesUnassigned()
    {
        var store = NewStore(out var root);
        try
        {
            var pairA = new SC2MaterialPair { Id = "pair-a" };
            var pairB = new SC2MaterialPair { Id = "pair-b" };
            var trianglePairIndex = new short[] { 0, 1 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pairA, pairB },
                Array.Empty<float>(), trianglePairIndex, 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            // Simulate pair-b having been deleted from the live document
            // (SlotIds keeps the tombstone entry, MaterialPairs no longer
            // lists it) — triangle 1 (slot 1 == "pair-b") must resolve to
            // unassigned, not crash or point at a stale index.
            doc.MaterialPairs.RemoveAt(1);

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal(0, result.TrianglePairIndex[0]);
            Assert.Equal(-1, result.TrianglePairIndex[1]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_RoundTripsRnmMaskBytes()
    {
        var store = NewStore(out var root);
        try
        {
            var maskBytes = new byte[] { 9, 8, 7, 6 };
            var pair = new SC2MaterialPair { Id = "pair-a", RnmMaskName = "mask01", RnmMaskBytes = maskBytes, MaskWidth = 2, MaskHeight = 1 };

            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { pair },
                Array.Empty<float>(), Array.Empty<short>(), 0, 0, 1f, SymmetryMode.None, Vector3.zero, store);

            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(doc, store);

            Assert.Equal(maskBytes, result.Pairs[0].RnmMaskBytes);
            Assert.Equal(2, result.Pairs[0].MaskWidth);
            Assert.Equal(1, result.Pairs[0].MaskHeight);
            Assert.Equal("mask01", result.Pairs[0].RnmMaskName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructTerrain_ThrowsOnNullArguments()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => SC2MapDocumentToLegacyBridge.ReconstructTerrain(null, store));
            Assert.Throws<ArgumentNullException>(() => SC2MapDocumentToLegacyBridge.ReconstructTerrain(new SC2TerrainDocument(), null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- Hfield ----

    [Fact]
    public void ReconstructHfield_RoundTripsTerrainTypes()
    {
        var store = NewStore(out var root);
        try
        {
            var terrainTypes = new ushort[] { 1, 2, 3, 4 };
            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(terrainTypes, gridSize: 2, store);

            var result = SC2MapDocumentToLegacyBridge.ReconstructHfield(doc, store, gridCellCount: 4);

            Assert.Equal(terrainTypes, result.TerrainTypes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ReconstructHfield_MissingOrWrongSizedBlob_ReturnsAllZeroGridOfExpectedSize()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = new SC2HfieldDocument(); // TerrainTypesUInt8 left default/empty

            var result = SC2MapDocumentToLegacyBridge.ReconstructHfield(doc, store, gridCellCount: 9);

            Assert.Equal(9, result.TerrainTypes.Length);
            Assert.All(result.TerrainTypes, v => Assert.Equal((ushort)0, v));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- Costs ----

    [Fact]
    public void ReconstructCosts_RoundTripsBlockNamesAndNavmeshBytes()
    {
        var store = NewStore(out var root);
        try
        {
            var blocks = new List<SC2CostsBlockData>
            {
                new() { Name = "Land_Small", Navmesh = new byte[] { 1, 1, 1 } },
                new() { Name = "Water_Large", Navmesh = new byte[] { 255, 255 } },
            };
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(blocks, store);

            var result = SC2MapDocumentToLegacyBridge.ReconstructCosts(doc, store);

            Assert.Equal(2, result.Blocks.Count);
            Assert.Equal("Land_Small", result.Blocks[0].Name);
            Assert.Equal(new byte[] { 1, 1, 1 }, result.Blocks[0].Navmesh);
            Assert.Equal("Water_Large", result.Blocks[1].Name);
            Assert.Equal(new byte[] { 255, 255 }, result.Blocks[1].Navmesh);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- Collision2 ----

    [Fact]
    public void ReconstructCollision2HeightOffset_RoundTrips()
    {
        var doc = SC2CollisionLegacyBridge.BuildFromLegacy(0.75f);

        var offset = SC2MapDocumentToLegacyBridge.ReconstructCollision2HeightOffset(doc);

        Assert.Equal(0.75f, offset);
    }

    // ---- Environment ----

    [Fact]
    public void ReconstructEnvironment_RoundTripsHeaderAndTexturePaths()
    {
        var header = new byte[SC2EnvironmentDocument.HeaderSize];
        header[0] = 42;
        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(header, "env.dds", "sky.dds", "tex3.dds", "cc.dds", "fow.dds",
            waterPlaneSize: 2048, waterUvScale: 512f, waterTexturePaths: new[] { "water_d.dds" });

        var result = SC2MapDocumentToLegacyBridge.ReconstructEnvironment(doc);

        Assert.Equal(42, result.Header[0]);
        Assert.Equal(SC2EnvironmentDocument.HeaderSize, result.Header.Length);
        Assert.Equal("env.dds", result.EnvironmentMap);
        Assert.Equal("sky.dds", result.SkyMap);
        Assert.Equal("tex3.dds", result.TexturePath3);
        Assert.Equal("cc.dds", result.ColorCorrectionCube);
        Assert.Equal("fow.dds", result.FogOfWarCorrectionCube);
        // WaterPlaneSize/WaterUvScale/WaterTexturePaths must round-trip too.
        Assert.Equal(2048, result.WaterPlaneSize);
        Assert.Equal(512f, result.WaterUvScale);
        Assert.Equal(new[] { "water_d.dds" }, result.WaterTexturePaths);
    }

    // ---- Objects ----

    [Fact]
    public void ReconstructObjects_RoundTripsListsAndLuaOriginalText()
    {
        var props = new List<SC2ObjProp> { new() { BlueprintPath = "Rock1" } };
        var markers = new List<SC2ObjMarker> { new() { Name = "M1" } };
        var doc = SC2ObjectsLegacyBridge.BuildFromLegacy(
            props, markers, new List<SC2ObjGraph>(), new List<SC2ObjArmy>(),
            new List<SC2ObjUnitNode>(), new List<SC2ObjZone>(), new List<SC2ObjChain>(),
            new List<SC2ObjSymmetryGroup>(), nextObjectId: 5,
            saveLuaTextOriginal: "-- save", scenarioLuaTextOriginal: "-- scenario");

        var result = SC2MapDocumentToLegacyBridge.ReconstructObjects(doc);

        Assert.Single(result.Props);
        Assert.Equal("Rock1", result.Props[0].BlueprintPath);
        Assert.Single(result.Markers);
        Assert.Equal("M1", result.Markers[0].Name);
        Assert.Equal(5, result.NextObjectId);
        Assert.Equal("-- save", result.SaveLuaTextOriginal);
        Assert.Equal("-- scenario", result.ScenarioLuaTextOriginal);
    }

    // ---- MapName ----

    [Fact]
    public void ReconstructMapName_PrefersTheMapStem_OverTheScenarioDisplayName()
    {
        var document = new SC2MapDocument();
        document.Identity.MapStem = "MyMap";
        document.Scenario.KnownFields.Name = "A Pretty In-Game Name";

        Assert.Equal("MyMap", SC2MapDocumentToLegacyBridge.ReconstructMapName(document));
    }

    [Fact]
    public void ReconstructMapName_PrefersScenarioKnownFieldsName()
    {
        var document = new SC2MapDocument();
        document.Scenario.KnownFields.Name = "FromScenario";
        document.Identity.DisplayName = "FromIdentity";

        Assert.Equal("FromScenario", SC2MapDocumentToLegacyBridge.ReconstructMapName(document));
    }

    [Fact]
    public void ReconstructMapName_FallsBackToIdentityDisplayName_WhenScenarioNameEmpty()
    {
        var document = new SC2MapDocument();
        document.Identity.DisplayName = "FromIdentity";

        Assert.Equal("FromIdentity", SC2MapDocumentToLegacyBridge.ReconstructMapName(document));
    }

    [Fact]
    public void ReconstructMapName_FallsBackToUntitled_WhenNothingSet()
    {
        var document = new SC2MapDocument();

        Assert.Equal("Untitled", SC2MapDocumentToLegacyBridge.ReconstructMapName(document));
    }
}
