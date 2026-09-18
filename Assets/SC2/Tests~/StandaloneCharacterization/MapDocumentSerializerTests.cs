using System;
using System.Collections.Generic;
using BdfIO.Core;
using SC2.MapEditor.Document;
using UnityEngine;

namespace SC2CharacterizationTests;

public class MapDocumentSerializerTests
{
    private static SC2MapDocument BuildFullDocument()
    {
        var doc = new SC2MapDocument();
        doc.Identity.MapId = "map-123";
        doc.Identity.DisplayName = "Test Map";
        doc.Identity.MapStem = "SC2_TEST_001";
        doc.Identity.Author = "arshadmanan098";
        doc.Identity.Description = "A map used only in tests.";
        doc.Identity.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        doc.Identity.ModifiedUtc = new DateTime(2026, 7, 15, 12, 30, 0, DateTimeKind.Utc);

        doc.Source.Kind = SC2MapSourceKind.ScdArchiveFile;
        doc.Source.ContainerKind = SC2SourceContainerKind.ZipArchive;
        doc.Source.OriginalPath = @"C:\maps\SC2_TEST_001.scd";
        doc.Source.OriginalFileName = "SC2_TEST_001.scd";
        doc.Source.OriginalFingerprint = "deadbeef";
        doc.Source.ImportedUtc = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        doc.Source.OriginalStillAvailable = true;

        doc.EntryPoint.ScenarioEntryPath = "SC2_TEST_001_scenario.lua";
        doc.EntryPoint.ScenarioEntrySha256 = "abc123";
        doc.EntryPoint.MapPath = "SC2_TEST_001.terrain.win.bdf";
        doc.EntryPoint.SavePath = "SC2_TEST_001_save.lua";
        doc.EntryPoint.ScriptPath = "SC2_TEST_001_script.lua";
        doc.EntryPoint.BdfDirectory = @"C:\maps\SC2_TEST_001";
        doc.EntryPoint.BdfStem = "SC2_TEST_001";
        doc.EntryPoint.Platform = BdfPlatform.Pc;
        doc.EntryPoint.ResolutionMethod = "heuristic (first *.terrain.win.bdf found)";
        doc.EntryPoint.ResolutionWarnings.Add("multiple candidates found");

        doc.Archive.SourceContainerFingerprint = "fingerprint-xyz";
        doc.Archive.SourceContainerKind = SC2SourceContainerKind.ZipArchive;
        doc.Archive.SelectedScenarioEntryPath = "SC2_TEST_001_scenario.lua";
        doc.Archive.Entries.Add(new SC2ArchiveEntry
        {
            OriginalPath = "SC2_TEST_001_scenario.lua",
            NormalizedPath = "SC2_TEST_001_scenario.lua",
            UncompressedLength = 1024,
            Sha256 = "abc123",
            Role = SC2ArchiveEntryRole.ScenarioLua,
            OriginalContent = new SC2BlobReference("abc123hash", 1024),
        });
        doc.Archive.Entries.Add(new SC2ArchiveEntry
        {
            OriginalPath = "readme.txt",
            NormalizedPath = "readme.txt",
            UncompressedLength = 42,
            Sha256 = "def456",
            Role = SC2ArchiveEntryRole.Unknown,
            // No OriginalContent set — exercises the "blob ref omitted" path.
        });

        // ---- The 8 domain sub-documents ----

        doc.Environment.OriginalHeader[0] = 0xAB;
        doc.Environment.OriginalHeader[507] = 0xCD;
        doc.Environment.WorkingHeader[1] = 0xEF;
        doc.Environment.EnvironmentMap = "/textures/env/paradise.dds";
        doc.Environment.SkyMap = "/textures/env/sky.dds";
        doc.Environment.TexturePath3 = "/textures/env/extra.dds";
        doc.Environment.ColorCorrectionCube = "/textures/lut/default.dds";
        doc.Environment.FogOfWarCorrectionCube = "/textures/lut/fow.dds";
        doc.Environment.WaterPlaneSize = 2048;
        doc.Environment.WaterUvScale = 512f;
        doc.Environment.WaterTexturePaths = new[] { "/textures/water/diffuse.dds", "/textures/water/normal.dds" };
        doc.Environment.UnknownFields.Add(new SC2UnknownInfoField { ByteOffset = 12, Note = "suspected float" });

        doc.Collision2.SourceMode = SC2CollisionSourceMode.GeneratedFromHfield;
        doc.Collision2.Mesh = new SC2MeshBlobReference
        {
            VerticesFloat32 = new SC2BlobReference("verts-hash", 300),
            TrianglesUInt32 = new SC2BlobReference("tris-hash", 120),
            VertexCount = 25,
            TriangleCount = 10,
        };
        doc.Collision2.Generation.MaxPolygons = 2000;
        doc.Collision2.Generation.VerticalOffset = 0.35f;
        doc.Collision2.SourceHfieldRevision = 7;
        doc.Collision2.State = SC2DerivedState.Current;

        doc.Costs.Profile = SC2CostsProfile.PlayableWater;
        doc.Costs.SourceHfieldRevision = 3;
        doc.Costs.State = SC2DerivedState.Stale;
        doc.Costs.Layers.Add(new SC2CostsLayerDocument
        {
            Id = "Land_Small",
            FieldName = "Land_Small",
            DisplayName = "Land (Small)",
            FinalGrid = new SC2BlobReference("costs-hash", 4096),
            Islands = new SC2IslandValidationResult { Checked = true, HasUnreachableIslands = true, IslandCount = 4 },
        });

        doc.Hfield.MapX = 1025;
        doc.Hfield.MapY = 1025;
        doc.Hfield.PlayableRect = new Rect(10, 20, 1000, 1000);
        doc.Hfield.TerrainTypesUInt8 = new SC2BlobReference("terraintypes-hash", 1048576);
        doc.Hfield.Projection.Width = 1025;
        doc.Hfield.Projection.Height = 1025;
        doc.Hfield.Projection.WorldBounds = new Rect(0, 0, 1024, 1024);
        doc.Hfield.Projection.TopHitPolicy = SC2TopHitPolicy.HighestSurface;
        doc.Hfield.Projection.GapFillPolicy = SC2GapFillPolicy.None;
        doc.Hfield.Projection.IncludedLayerIds.Add("layer-1");
        doc.Hfield.Projection.ExcludedMaterialPairIds.Add("pair-water");
        doc.Hfield.SourceTerrainRevision = 5;
        doc.Hfield.State = SC2DerivedState.Current;

        doc.Objects.NextObjectId = 42;
        doc.Objects.SaveLuaTextOriginal = "-- original save lua text";
        doc.Objects.ScenarioLuaTextOriginal = "-- original scenario lua text";
        doc.Objects.Props.Add(new SC2ObjProp
        {
            Id = 1, BlueprintPath = "/props/tree.bp", Position = new Vector3(1, 2, 3),
            Rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), Scale = 1.5f, Flags = 7,
            AnchorToTerrain = false, HeightOffset = 2.5f,
        });
        doc.Objects.Markers.Add(new SC2ObjMarker
        {
            Id = 2, Name = "Mass_01", Type = "Mass", Position = new Vector3(4, 5, 6),
            OrientationEuler = new Vector3(0, 90, 0), OrientationIsQuaternion = false,
            ColorHex = "ff00ff00", Prop = "/props/mass.bp",
            ExtraFields = { new SC2LuaField { Key = "amount", RawValue = "NUMBER( 100 )" } },
        });
        doc.Objects.Graphs.Add(new SC2ObjGraph
        {
            Id = 3, Name = "PathGraph", EntriesA = { 1 }, EntriesB = { 2 },
            MarkerIdsA = { 2 }, MarkerIdsB = { 5 }, UsesMarkerIds = true, GraphKind = "Air",
        });
        var army = new SC2ObjArmy
        {
            Id = 4, Name = "ARMY_1", Personality = "Human", Plans = "", Color = 2f,
            Faction = 1, Mass = 100f, Energy = 50f,
        };
        army.Alliances.Add(new SC2ObjAlliance { OtherArmy = "ARMY_2", Stance = "ENEMY" });
        doc.Objects.Armies.Add(army);
        doc.Objects.Units.Add(new SC2ObjUnitNode
        {
            Id = 5, ArmyName = "ARMY_1", Name = "unit_0", ParentIndex = -1, IsGroup = true,
            Type = "uca0104", Position = new Vector3(7, 8, 9), OrientationEuler = new Vector3(0, 45, 0),
            AnchorToTerrain = false, HeightOffset = 1f,
        });
        doc.Objects.Zones.Add(new SC2ObjZone { Id = 6, Name = "Zone_1", X1 = -10, Z1 = -10, X2 = 10, Z2 = 10, Role = "Playable" });
        doc.Objects.Chains.Add(new SC2ObjChain { Id = 7, Name = "Chain_1", MarkerNames = { "Mass_01", "Mass_02" } });
        var symmetryGroup = new SC2ObjSymmetryGroup { Id = 8, Kind = "Marker" };
        symmetryGroup.Members.Add(new SC2ObjSymmetryMember { ObjectId = 2, SlotIndex = 0 });
        symmetryGroup.Members.Add(new SC2ObjSymmetryMember { ObjectId = 9, SlotIndex = 1 });
        doc.Objects.SymmetryGroups.Add(symmetryGroup);

        doc.Terrain.AuthoringMode = SC2TerrainAuthoringMode.ProceduralHeightmap;
        doc.Terrain.Grid = new SC2TerrainGrid
        {
            Width = 64, Height = 64, CellSize = 2f,
            HeightsFloat32 = new SC2BlobReference("heights-hash", 16384),
            MinimumHeight = 80f, MaximumHeight = 120f,
        };
        doc.Terrain.Layers.Add(new SC2TerrainLayerDocument
        {
            LayerId = "layer-1", Name = "Ground", Role = SC2TerrainLayerRole.GameplaySurface,
            IncludeInVisualTerrain = true, IncludeInHfield = true, IncludeInCollisionSource = true,
            MaterialPairIds = { "pair-1" },
        });
        doc.Terrain.Materials.Add(new SC2MaterialDocument
        {
            Id = "mat-1", Name = "Grass",
            Diffuse = new SC2TextureReference { Id = "tex-1", Name = "grass_d", ResourcePath = "res/grass_d", GamePath = "/textures/grass_d.dds", Source = "Vanilla", ContentHash = "" },
            Normal = new SC2TextureReference { Name = "grass_n" },
            Mask = new SC2TextureReference { Name = "grass_m" },
            DebugColor = new Color(0.2f, 0.8f, 0.1f, 1f),
        });
        doc.Terrain.MaterialPairs.Add(new SC2MaterialPairDocument
        {
            Id = "pair-1", Name = "Grass/Rock", Material1Id = "mat-1", Material2Id = "mat-2",
            DebugColor = Color.red, TilingScale = 8f, SimplifyRatio = 0.5f,
            ShaderFlags = { new SC2ShaderFlag { Key = "RNM_LIGHTING", Value = true } },
            HfieldContribution = SC2HfieldContribution.Exclude, RnmSetId = "rnm-1",
        });
        doc.Terrain.PairAssignments = new SC2TrianglePairMap
        {
            SlotIndexPerTriangleInt16 = new SC2BlobReference("slots-hash", 2048),
            SlotIds = { "pair-1", "" },
        };
        doc.Terrain.Blend = new SC2BlendDocument { Width = 2048, Height = 2048, BlendTextureRgba32 = new SC2BlobReference("blend-hash", 16777216) };
        doc.Terrain.Rnm.MaskSets.Add(new SC2RnmMaskSetDocument { Id = "rnm-1", Name = "mask01", Width = 512, Height = 512, MaskBytes = new SC2BlobReference("rnm-hash", 786432) });
        doc.Terrain.Simplification.Enabled = true;
        doc.Terrain.Normals.RecalculateOnEdit = false;
        doc.Terrain.Symmetry.Mode = SymmetryMode.X;
        doc.Terrain.Symmetry.Center = new Vector3(512, 0, 512);
        doc.Terrain.IsInitialized = true;
        doc.Terrain.DataVersion = 2;
        doc.Terrain.ImportOnlyMode = true;
        doc.Terrain.SingleBspNodePerLayer = true;
        doc.Terrain.VanillaCandidateBspSplit = false;
        doc.Terrain.PlayableAreaName = "MainPlayableArea";
        var importedOverride = new SC2ImportedTerrainMaterialOverrideDocument();
        importedOverride.TextureResourcePaths["_Diffuse1"] = "SC2Textures/rock_d";
        importedOverride.Flags["_EnableRNM"] = 1f;
        importedOverride.Vectors["_TextureScroll"] = new[] { 0.5f, -0.25f, 0f, 0f };
        importedOverride.BlendState = "TerrainAlphaBlendState";
        doc.Terrain.ImportedEntries.Add(new SC2ImportedTerrainEntryDocument
        {
            Id = "imported-1", DisplayName = "Imported Rock",
            OriginalBdf = new SC2BlobReference("imported-terrain-hash", 32768),
            Position = new[] { 10f, 20f, 30f }, EulerAngles = new[] { 0f, 90f, 0f }, Scale = new[] { 2f, 2f, 2f },
            RecomputeNormals = false,
            MaterialOverrides = { importedOverride },
        });

        doc.Scenario.OriginalScenarioLua = new SC2BlobReference("scenario-lua-hash", 8192);
        doc.Scenario.OriginalSaveLua = new SC2BlobReference("save-lua-hash", 4096);
        doc.Scenario.OriginalScriptLua = new SC2BlobReference("script-lua-hash", 2048);
        doc.Scenario.KnownFields.Name = "Test Map";
        doc.Scenario.KnownFields.Description = "A description.";
        doc.Scenario.KnownFields.Type = "skirmish";
        doc.Scenario.KnownFields.CampaignId = "";
        doc.Scenario.KnownFields.Starts = true;
        doc.Scenario.KnownFields.Preview = "preview.dds";
        doc.Scenario.KnownFields.ReverbPreset = "Default";
        doc.Scenario.KnownFields.SizeX = 1024;
        doc.Scenario.KnownFields.SizeY = 1024;
        doc.Scenario.KnownFields.MapPath = "/maps/test/test.scmap";
        doc.Scenario.KnownFields.SavePath = "/maps/test/test_save.lua";
        doc.Scenario.KnownFields.ScriptPath = "/maps/test/test_script.lua";
        doc.Scenario.KnownFields.UiDeactivate = true;
        doc.Scenario.KnownFields.NoRushRadius = 64f;
        doc.Scenario.KnownFields.ExtraFields.Add(new SC2LuaField { Key = "norushoffsetX_ARMY_1", RawValue = "NUMBER( 0 )" });
        doc.Scenario.ScenarioMode = SC2LuaPatchMode.OriginalPassthrough;
        doc.Scenario.SaveMode = SC2LuaPatchMode.PatchKnownBlocks;
        doc.Scenario.ScriptMode = SC2LuaPatchMode.OriginalPassthrough;
        doc.Scenario.State = SC2DerivedState.Current;

        doc.Decals.OriginalBdf = new SC2BlobReference("decals-hash", 65536);
        doc.Decals.IsEditable = false;
        doc.Decals.HasDecals = true;
        doc.Decals.VertexCount = 400;
        doc.Decals.IndexCount = 600;
        doc.Decals.DiffuseAtlasPath = "/textures/decals/diffuse_atlas.dds";
        doc.Decals.NormalAtlasPath = "/textures/decals/normal_atlas.dds";
        doc.Decals.DiffuseAtlasContent = new SC2BlobReference("decal-diffuse-hash", 4096);
        doc.Decals.NormalAtlasContent = new SC2BlobReference("decal-normal-hash", 4096);
        doc.Decals.AtlasWidth = 1024;
        doc.Decals.AtlasHeight = 1024;
        doc.Decals.AtlasSource = SC2DecalAtlasSource.Existing;
        doc.Decals.SpatialState = SC2DecalsSpatialState.Current;
        var decalEntry = new SC2DecalAtlasEntry { Id = "decal-entry-1", Name = "Crack", X = 16, Y = 32, Width = 128, Height = 64 };
        decalEntry.Tags.Add("Ground");
        doc.Decals.Entries.Add(decalEntry);
        doc.Decals.Placements.Add(new SC2DecalPlacement
        {
            Id = "decal-placement-1", Name = "Crack 1", AtlasEntryId = decalEntry.Id,
            Position = new[] { 10f, 20f, 30f }, SurfaceNormal = new[] { 0f, 1f, 0f },
            Rotation = 45f, Width = 12f, Length = 6f, ProjectionDepth = 24f,
            SymmetryGroupId = "sym-group-1", SymmetrySlotIndex = 2
        });
        doc.Decals.State = SC2DerivedState.Original;

        return doc;
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsEveryField()
    {
        var original = BuildFullDocument();
        var json = SC2MapDocumentSerializer.Serialize(original);
        var restored = SC2MapDocumentSerializer.Deserialize(json);

        Assert.Equal(original.FormatVersion, restored.FormatVersion);

        Assert.Equal(original.Identity.MapId, restored.Identity.MapId);
        Assert.Equal(original.Identity.DisplayName, restored.Identity.DisplayName);
        Assert.Equal(original.Identity.MapStem, restored.Identity.MapStem);
        Assert.Equal(original.Identity.Author, restored.Identity.Author);
        Assert.Equal(original.Identity.Description, restored.Identity.Description);
        Assert.Equal(original.Identity.CreatedUtc, restored.Identity.CreatedUtc);
        Assert.Equal(original.Identity.ModifiedUtc, restored.Identity.ModifiedUtc);

        Assert.Equal(original.Source.Kind, restored.Source.Kind);
        Assert.Equal(original.Source.ContainerKind, restored.Source.ContainerKind);
        Assert.Equal(original.Source.OriginalPath, restored.Source.OriginalPath);
        Assert.Equal(original.Source.OriginalFileName, restored.Source.OriginalFileName);
        Assert.Equal(original.Source.OriginalFingerprint, restored.Source.OriginalFingerprint);
        Assert.Equal(original.Source.ImportedUtc, restored.Source.ImportedUtc);
        Assert.Equal(original.Source.OriginalStillAvailable, restored.Source.OriginalStillAvailable);

        Assert.Equal(original.EntryPoint.ScenarioEntryPath, restored.EntryPoint.ScenarioEntryPath);
        Assert.Equal(original.EntryPoint.ScenarioEntrySha256, restored.EntryPoint.ScenarioEntrySha256);
        Assert.Equal(original.EntryPoint.MapPath, restored.EntryPoint.MapPath);
        Assert.Equal(original.EntryPoint.SavePath, restored.EntryPoint.SavePath);
        Assert.Equal(original.EntryPoint.ScriptPath, restored.EntryPoint.ScriptPath);
        Assert.Equal(original.EntryPoint.BdfDirectory, restored.EntryPoint.BdfDirectory);
        Assert.Equal(original.EntryPoint.BdfStem, restored.EntryPoint.BdfStem);
        Assert.Equal(original.EntryPoint.Platform, restored.EntryPoint.Platform);
        Assert.Equal(original.EntryPoint.ResolutionMethod, restored.EntryPoint.ResolutionMethod);
        Assert.Equal(original.EntryPoint.ResolutionWarnings, restored.EntryPoint.ResolutionWarnings);

        Assert.Equal(original.Archive.SourceContainerFingerprint, restored.Archive.SourceContainerFingerprint);
        Assert.Equal(original.Archive.SourceContainerKind, restored.Archive.SourceContainerKind);
        Assert.Equal(original.Archive.SelectedScenarioEntryPath, restored.Archive.SelectedScenarioEntryPath);
        Assert.Equal(original.Archive.Entries.Count, restored.Archive.Entries.Count);

        var scenarioEntry = restored.Archive.Find("SC2_TEST_001_scenario.lua");
        Assert.NotNull(scenarioEntry);
        Assert.Equal(SC2ArchiveEntryRole.ScenarioLua, scenarioEntry.Role);
        Assert.False(scenarioEntry.OriginalContent.IsEmpty);
        Assert.Equal("abc123hash", scenarioEntry.OriginalContent.ContentHash);
        Assert.Equal(1024, scenarioEntry.OriginalContent.Length);

        var readmeEntry = restored.Archive.Find("readme.txt");
        Assert.NotNull(readmeEntry);
        Assert.True(readmeEntry.OriginalContent.IsEmpty);

        // ---- The 8 domain sub-documents ----

        Assert.Equal(original.Environment.OriginalHeader, restored.Environment.OriginalHeader);
        Assert.Equal(original.Environment.WorkingHeader, restored.Environment.WorkingHeader);
        Assert.Equal(original.Environment.EnvironmentMap, restored.Environment.EnvironmentMap);
        Assert.Equal(original.Environment.SkyMap, restored.Environment.SkyMap);
        Assert.Equal(original.Environment.TexturePath3, restored.Environment.TexturePath3);
        Assert.Equal(original.Environment.ColorCorrectionCube, restored.Environment.ColorCorrectionCube);
        Assert.Equal(original.Environment.FogOfWarCorrectionCube, restored.Environment.FogOfWarCorrectionCube);
        Assert.Equal(original.Environment.WaterPlaneSize, restored.Environment.WaterPlaneSize);
        Assert.Equal(original.Environment.WaterUvScale, restored.Environment.WaterUvScale);
        Assert.Equal(original.Environment.WaterTexturePaths, restored.Environment.WaterTexturePaths);
        Assert.Single(restored.Environment.UnknownFields);
        Assert.Equal(12, restored.Environment.UnknownFields[0].ByteOffset);
        Assert.Equal("suspected float", restored.Environment.UnknownFields[0].Note);

        Assert.Equal(SC2CollisionSourceMode.GeneratedFromHfield, restored.Collision2.SourceMode);
        Assert.NotNull(restored.Collision2.Mesh);
        Assert.Equal("verts-hash", restored.Collision2.Mesh.VerticesFloat32.ContentHash);
        Assert.Equal(300, restored.Collision2.Mesh.VerticesFloat32.Length);
        Assert.Equal("tris-hash", restored.Collision2.Mesh.TrianglesUInt32.ContentHash);
        Assert.Equal(25, restored.Collision2.Mesh.VertexCount);
        Assert.Equal(10, restored.Collision2.Mesh.TriangleCount);
        Assert.Equal(2000, restored.Collision2.Generation.MaxPolygons);
        Assert.Equal(0.35f, restored.Collision2.Generation.VerticalOffset);
        Assert.Equal(7, restored.Collision2.SourceHfieldRevision);
        Assert.Equal(SC2DerivedState.Current, restored.Collision2.State);

        Assert.Equal(SC2CostsProfile.PlayableWater, restored.Costs.Profile);
        Assert.Equal(3, restored.Costs.SourceHfieldRevision);
        Assert.Equal(SC2DerivedState.Stale, restored.Costs.State);
        var landSmall = restored.Costs.Find("Land_Small");
        Assert.NotNull(landSmall);
        Assert.Equal("Land (Small)", landSmall.DisplayName);
        Assert.Equal("costs-hash", landSmall.FinalGrid.ContentHash);
        Assert.True(landSmall.BaseGeneratedGrid.IsEmpty);
        Assert.True(landSmall.Islands.Checked);
        Assert.True(landSmall.Islands.HasUnreachableIslands);
        Assert.Equal(4, landSmall.Islands.IslandCount);

        Assert.Equal(1025, restored.Hfield.MapX);
        Assert.Equal(1025, restored.Hfield.MapY);
        Assert.Equal(new Rect(10, 20, 1000, 1000), restored.Hfield.PlayableRect);
        Assert.Equal("terraintypes-hash", restored.Hfield.TerrainTypesUInt8.ContentHash);
        Assert.True(restored.Hfield.HeightmapUInt16.IsEmpty);
        Assert.Equal(1025, restored.Hfield.Projection.Width);
        Assert.Equal(new Rect(0, 0, 1024, 1024), restored.Hfield.Projection.WorldBounds);
        Assert.Equal(SC2TopHitPolicy.HighestSurface, restored.Hfield.Projection.TopHitPolicy);
        Assert.Equal(new List<string> { "layer-1" }, restored.Hfield.Projection.IncludedLayerIds);
        Assert.Equal(new List<string> { "pair-water" }, restored.Hfield.Projection.ExcludedMaterialPairIds);
        Assert.Equal(5, restored.Hfield.SourceTerrainRevision);
        Assert.Equal(SC2DerivedState.Current, restored.Hfield.State);

        Assert.Equal(42, restored.Objects.NextObjectId);
        Assert.Equal("-- original save lua text", restored.Objects.SaveLuaTextOriginal);
        Assert.Equal("-- original scenario lua text", restored.Objects.ScenarioLuaTextOriginal);
        Assert.Single(restored.Objects.Props);
        Assert.Equal("/props/tree.bp", restored.Objects.Props[0].BlueprintPath);
        Assert.Equal(new Vector3(1, 2, 3), restored.Objects.Props[0].Position);
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), restored.Objects.Props[0].Rotation);
        Assert.Equal(1.5f, restored.Objects.Props[0].Scale);
        Assert.Equal(7u, restored.Objects.Props[0].Flags);
        Assert.False(restored.Objects.Props[0].AnchorToTerrain);
        Assert.Single(restored.Objects.Markers);
        Assert.Equal("Mass_01", restored.Objects.Markers[0].Name);
        Assert.False(restored.Objects.Markers[0].OrientationIsQuaternion);
        Assert.Single(restored.Objects.Markers[0].ExtraFields);
        Assert.Equal("amount", restored.Objects.Markers[0].ExtraFields[0].Key);
        Assert.Single(restored.Objects.Graphs);
        Assert.Equal(new List<int> { 2 }, restored.Objects.Graphs[0].MarkerIdsA);
        Assert.True(restored.Objects.Graphs[0].UsesMarkerIds);
        Assert.Single(restored.Objects.Armies);
        Assert.Single(restored.Objects.Armies[0].Alliances);
        Assert.Equal("ARMY_2", restored.Objects.Armies[0].Alliances[0].OtherArmy);
        Assert.Single(restored.Objects.Units);
        Assert.Equal("uca0104", restored.Objects.Units[0].Type);
        Assert.Single(restored.Objects.Zones);
        Assert.Equal("Playable", restored.Objects.Zones[0].Role);
        Assert.Single(restored.Objects.Chains);
        Assert.Equal(new List<string> { "Mass_01", "Mass_02" }, restored.Objects.Chains[0].MarkerNames);
        Assert.Single(restored.Objects.SymmetryGroups);
        Assert.Equal(2, restored.Objects.SymmetryGroups[0].Members.Count);
        Assert.Equal(9, restored.Objects.SymmetryGroups[0].Members[1].ObjectId);

        Assert.Equal(SC2TerrainAuthoringMode.ProceduralHeightmap, restored.Terrain.AuthoringMode);
        Assert.Equal(64, restored.Terrain.Grid.Width);
        Assert.Equal("heights-hash", restored.Terrain.Grid.HeightsFloat32.ContentHash);
        Assert.Equal(80f, restored.Terrain.Grid.MinimumHeight);
        Assert.Single(restored.Terrain.Layers);
        Assert.Equal(SC2TerrainLayerRole.GameplaySurface, restored.Terrain.Layers[0].Role);
        Assert.Single(restored.Terrain.Materials);
        Assert.Equal("tex-1", restored.Terrain.Materials[0].Diffuse.Id);
        Assert.Equal("/textures/grass_d.dds", restored.Terrain.Materials[0].Diffuse.GamePath);
        Assert.Equal(new Color(0.2f, 0.8f, 0.1f, 1f), restored.Terrain.Materials[0].DebugColor);
        Assert.Single(restored.Terrain.MaterialPairs);
        Assert.Equal("mat-2", restored.Terrain.MaterialPairs[0].Material2Id);
        Assert.Equal(SC2HfieldContribution.Exclude, restored.Terrain.MaterialPairs[0].HfieldContribution);
        Assert.Single(restored.Terrain.MaterialPairs[0].ShaderFlags);
        Assert.Equal("slots-hash", restored.Terrain.PairAssignments.SlotIndexPerTriangleInt16.ContentHash);
        Assert.Equal(new List<string> { "pair-1", "" }, restored.Terrain.PairAssignments.SlotIds);
        Assert.NotNull(restored.Terrain.Blend);
        Assert.Equal(2048, restored.Terrain.Blend.Width);
        Assert.Equal("blend-hash", restored.Terrain.Blend.BlendTextureRgba32.ContentHash);
        Assert.Single(restored.Terrain.Rnm.MaskSets);
        Assert.Equal("mask01", restored.Terrain.Rnm.MaskSets[0].Name);
        Assert.True(restored.Terrain.Simplification.Enabled);
        Assert.False(restored.Terrain.Normals.RecalculateOnEdit);
        Assert.Equal(SymmetryMode.X, restored.Terrain.Symmetry.Mode);
        Assert.Equal(new Vector3(512, 0, 512), restored.Terrain.Symmetry.Center);
        Assert.True(restored.Terrain.IsInitialized);
        Assert.Equal(2, restored.Terrain.DataVersion);
        Assert.True(restored.Terrain.ImportOnlyMode);
        Assert.True(restored.Terrain.SingleBspNodePerLayer);
        Assert.False(restored.Terrain.VanillaCandidateBspSplit);
        Assert.Equal("MainPlayableArea", restored.Terrain.PlayableAreaName);
        Assert.Single(restored.Terrain.ImportedEntries);
        var restoredImported = restored.Terrain.ImportedEntries[0];
        Assert.Equal("imported-1", restoredImported.Id);
        Assert.Equal("Imported Rock", restoredImported.DisplayName);
        Assert.Equal("imported-terrain-hash", restoredImported.OriginalBdf.ContentHash);
        Assert.Equal(new[] { 10f, 20f, 30f }, restoredImported.Position);
        Assert.Equal(new[] { 0f, 90f, 0f }, restoredImported.EulerAngles);
        Assert.Equal(new[] { 2f, 2f, 2f }, restoredImported.Scale);
        Assert.False(restoredImported.RecomputeNormals);
        Assert.Single(restoredImported.MaterialOverrides);
        Assert.Equal("SC2Textures/rock_d", restoredImported.MaterialOverrides[0].TextureResourcePaths["_Diffuse1"]);
        Assert.Equal(1f, restoredImported.MaterialOverrides[0].Flags["_EnableRNM"]);
        Assert.Equal(new[] { 0.5f, -0.25f, 0f, 0f }, restoredImported.MaterialOverrides[0].Vectors["_TextureScroll"]);
        Assert.Equal("TerrainAlphaBlendState", restoredImported.MaterialOverrides[0].BlendState);

        Assert.Equal("scenario-lua-hash", restored.Scenario.OriginalScenarioLua.ContentHash);
        Assert.Equal("save-lua-hash", restored.Scenario.OriginalSaveLua.ContentHash);
        Assert.Equal("Test Map", restored.Scenario.KnownFields.Name);
        Assert.True(restored.Scenario.KnownFields.Starts);
        Assert.Equal(1024, restored.Scenario.KnownFields.SizeX);
        Assert.True(restored.Scenario.KnownFields.UiDeactivate);
        Assert.Equal(64f, restored.Scenario.KnownFields.NoRushRadius);
        Assert.Single(restored.Scenario.KnownFields.ExtraFields);
        Assert.Equal("norushoffsetX_ARMY_1", restored.Scenario.KnownFields.ExtraFields[0].Key);
        Assert.Equal(SC2LuaPatchMode.PatchKnownBlocks, restored.Scenario.SaveMode);
        Assert.Equal(SC2DerivedState.Current, restored.Scenario.State);

        Assert.Equal("decals-hash", restored.Decals.OriginalBdf.ContentHash);
        Assert.False(restored.Decals.IsEditable);
        Assert.True(restored.Decals.HasDecals);
        Assert.Equal(400, restored.Decals.VertexCount);
        Assert.Equal("/textures/decals/diffuse_atlas.dds", restored.Decals.DiffuseAtlasPath);
        Assert.Equal("decal-diffuse-hash", restored.Decals.DiffuseAtlasContent.ContentHash);
        Assert.Equal(1024, restored.Decals.AtlasWidth);
        Assert.Equal(SC2DecalAtlasSource.Existing, restored.Decals.AtlasSource);
        Assert.Equal(SC2DecalsSpatialState.Current, restored.Decals.SpatialState);
        Assert.Single(restored.Decals.Entries);
        Assert.Equal("Ground", restored.Decals.Entries[0].Tags[0]);
        Assert.Single(restored.Decals.Placements);
        Assert.Equal(45f, restored.Decals.Placements[0].Rotation);
        Assert.Equal("sym-group-1", restored.Decals.Placements[0].SymmetryGroupId);
        Assert.Equal(2, restored.Decals.Placements[0].SymmetrySlotIndex);
        Assert.Equal(SC2DerivedState.Original, restored.Decals.State);
    }

    [Fact]
    public void Serialize_ThenDeserialize_NullableBlendAndMeshStayNullWhenNotSet()
    {
        var doc = new SC2MapDocument();
        // Blend and Collision2.Mesh are both intentionally-nullable reference
        // fields (SC2TerrainLegacyBridge/SC2CollisionLegacyBridge leave them
        // null rather than inventing unverified data) — the round-trip must
        // not silently turn a missing value into an empty-but-non-null one.
        Assert.Null(doc.Terrain.Blend);
        Assert.Null(doc.Collision2.Mesh);

        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        Assert.Null(restored.Terrain.Blend);
        Assert.Null(restored.Collision2.Mesh);
    }

    [Fact]
    public void Serialize_ProducesHumanReadableIndentedJson()
    {
        var json = SC2MapDocumentSerializer.Serialize(BuildFullDocument());
        Assert.Contains("\n", json);
        Assert.Contains("\"formatVersion\"", json);
    }

    [Fact]
    public void Deserialize_MissingFieldsFallBackToDefaultsRatherThanThrowing()
    {
        var minimalJson = "{\"formatVersion\": 1}";
        var doc = SC2MapDocumentSerializer.Deserialize(minimalJson);

        Assert.NotNull(doc.Identity);
        Assert.NotNull(doc.Source);
        Assert.NotNull(doc.EntryPoint);
        Assert.NotNull(doc.Archive);
        Assert.Empty(doc.Archive.Entries);
        Assert.Equal("", doc.Identity.DisplayName);
    }

    private sealed class AddDisplayNameDefaultMigration : ISC2DocumentMigration
    {
        public int FromVersion => 0;

        public void Apply(SC2JsonValue root)
        {
            var identity = root.Get("identity");
            if (identity.Kind == SC2JsonKind.Null)
            {
                identity = SC2JsonValue.NewObject();
                root.Set("identity", identity);
            }
            if (identity.Get("displayName").Kind == SC2JsonKind.Null)
                identity.Set("displayName", SC2JsonValue.Of("(migrated from v0)"));
        }
    }

    [Fact]
    public void Deserialize_AppliesRegisteredMigration_AndAdvancesFormatVersion()
    {
        var legacyJson = "{\"formatVersion\": 0}";
        var migrations = new List<ISC2DocumentMigration> { new AddDisplayNameDefaultMigration() };

        var doc = SC2MapDocumentSerializer.Deserialize(legacyJson, migrations);

        Assert.Equal(SC2MapDocument.CurrentFormatVersion, doc.FormatVersion);
        Assert.Equal("(migrated from v0)", doc.Identity.DisplayName);
    }

    [Fact]
    public void Deserialize_OldFormatVersionWithNoMatchingMigration_Throws()
    {
        var legacyJson = "{\"formatVersion\": 0}";
        Assert.Throws<InvalidOperationException>(
            () => SC2MapDocumentSerializer.Deserialize(legacyJson, Array.Empty<ISC2DocumentMigration>()));
    }
}
