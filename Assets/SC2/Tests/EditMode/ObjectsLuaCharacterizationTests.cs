using System;
using System.IO;
using BdfIO.Core;
using BdfIO.Formats.MapObjs;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Unlike the BdfIO/SmartExport/Lua-text-parsing layers (characterized for
/// real via a standalone dotnet-test project, since those layers have no
/// UnityEngine dependency), this specifically needs a live
/// SC2TerrainData ScriptableObject instance, which only a running Unity
/// Editor/Player process can construct correctly (ScriptableObject.
/// CreateInstance needs the native engine runtime — a bare console app
/// can't do this safely; FormatterServices.GetUninitializedObject is not
/// viable either, since making it compile would require dragging in most
/// of Scripts/TerrainEditor and Scripts/Unified just to satisfy
/// SC2TerrainData's own dependencies).
///
/// These tests are written to the exact real signatures in
/// SC2ObjectsExport.cs, SC2ObjectsModel.cs, and SC2TerrainData.cs. Run
/// them via Window &gt; General &gt; Test Runner &gt; EditMode in the Unity
/// Editor.
/// </summary>
public class ObjectsLuaCharacterizationTests
{
    private SC2TerrainData _data;

    [SetUp]
    public void SetUp()
    {
        _data = ScriptableObject.CreateInstance<SC2TerrainData>();
        _data.InitializeFlatPlane(4, 4, 8f);
        _data.EnsureObjectsLists();
    }

    [TearDown]
    public void TearDown()
    {
        if (_data != null) UnityEngine.Object.DestroyImmediate(_data);
    }

    [Test]
    public void BuildMapObjsModel_ExcludesArmyPositionMarkersFromMarkerList()
    {
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "ARMY_1", Type = "Blank Marker" });
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Mass_01", Type = "Mass" });

        var model = SC2ObjectsExport.BuildMapObjsModel(_data);

        Assert.AreEqual(1, model.Markers.Count, "ARMY_* start-position markers must not appear in mapobjs.bdf's marker list");
        Assert.AreEqual("Mass_01", model.Markers[0].Name);
    }

    [Test]
    public void BuildMapObjsModel_GraphEdgesReferenceCorrectMapobjsIndexes()
    {
        var a = new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Node_A", Type = "Land Path Node" };
        var b = new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Node_B", Type = "Land Path Node" };
        _data.ObjMarkers.Add(a);
        _data.ObjMarkers.Add(b);
        var graph = new SC2ObjGraph { Id = _data.AllocateObjectId(), Name = "TestGraph" };
        SC2ObjectGraphRefs.AddEdge(graph, a.Id, b.Id);
        _data.ObjGraphs.Add(graph);

        var model = SC2ObjectsExport.BuildMapObjsModel(_data);

        Assert.AreEqual(1, model.Graphs.Count);
        Assert.AreEqual(1, model.Graphs[0].Entries.Count);
        var (ia, ib) = model.Graphs[0].Entries[0];
        Assert.AreEqual("Node_A", model.Markers[ia].Name);
        Assert.AreEqual("Node_B", model.Markers[ib].Name);
    }

    [Test]
    public void RegenerateSaveLua_FromScratchProceduralMap_ProducesMinimalValidSkeletonWithMarkers()
    {
        // No ObjSaveLuaTextOriginal captured (a from-scratch map, never
        // imported) -> RegenerateSaveLua must fall back to its minimal
        // skeleton and still patch the marker(s) into it.
        Assert.IsTrue(string.IsNullOrEmpty(_data.ObjSaveLuaTextOriginal));
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Blank Marker 1", Type = "Blank Marker", Prop = "/props/markers/M_Blank_prop.bp" });

        var lua = SC2ObjectsExport.RegenerateSaveLua(_data);

        StringAssert.Contains("Blank Marker 1", lua);
        StringAssert.Contains("Areas = ", lua);
        StringAssert.Contains("Armies = ", lua);
        StringAssert.Contains("Chains = ", lua);
    }

    [Test]
    public void RegenerateSaveLua_PatchesOnlyManagedBlocksOfARealOriginalFile()
    {
        // A minimal, deliberately fake "original" file with a distinctive
        // unmanaged section (ScenarioInfo-like) that must survive untouched.
        _data.ObjSaveLuaTextOriginal =
            "-- untouched preamble marker: KEEP_ME_UNCHANGED\r\n" +
            "Scenario = {\r\n" +
            "    Areas = {\r\n" +
            "    },\r\n" +
            "    MasterChain = {\r\n" +
            "        _MASTERCHAIN_ = {\r\n" +
            "            Markers = {\r\n" +
            "            },\r\n" +
            "        },\r\n" +
            "    },\r\n" +
            "    Armies = {\r\n" +
            "    },\r\n" +
            "    Chains = {\r\n" +
            "    },\r\n" +
            "}\r\n";
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Blank Marker 1", Type = "Blank Marker" });

        var lua = SC2ObjectsExport.RegenerateSaveLua(_data);

        StringAssert.Contains("KEEP_ME_UNCHANGED", lua);
        StringAssert.Contains("Blank Marker 1", lua);
    }

    [Test]
    public void RegenerateScenarioLua_OnlyIncludesArmyPositionMarkers()
    {
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "ARMY_1", Position = new Vector3(10f, 0f, 20f) });
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Mass_01", Type = "Mass" });

        var lua = SC2ObjectsExport.RegenerateScenarioLua(_data);

        StringAssert.Contains("ARMY_1", lua);
        StringAssert.DoesNotContain("Mass_01", lua);
    }

    [Test]
    public void AutoPathNode_ResolvesToLandOrWaterBasedOnHeightVsWaterElevation()
    {
        // InitializeFlatPlane fills Heights with SC2TerrainData.DefaultFlatHeight
        // (100). EnvironmentHeader defaults to a real vanilla map's bytes
        // (SC2EnvironmentDefaults) rather than all-zero, but
        // that reference map's own WaterElevation is 0, so this
        // still reads as 0. 100 is not below 0, so this should resolve to Land.
        var marker = new SC2ObjMarker { Id = _data.AllocateObjectId(), Name = "Auto_1", Type = "Auto Path Node", Position = new Vector3(4f, 0f, 4f) };
        _data.ObjMarkers.Add(marker);

        var effectiveType = SC2ObjectsExport.ResolveEffectiveType(_data, marker);

        Assert.AreEqual("Land Path Node", effectiveType, "Flat plane (height 100) with default water elevation (0) should resolve to Land, not Water");
    }

    [Test]
    public void SaveLuaParser_DuplicateMarkerName_KeepsFirstAndSkipsLaterEntry()
    {
        const string lua =
            "Scenario = { MasterChain = { _MASTERCHAIN_ = { Markers = {\n" +
            "['Mass_01'] = { ['type'] = STRING( 'Mass' ), },\n" +
            "['mass_01'] = { ['type'] = STRING( 'Blank Marker' ), },\n" +
            "}, }, }, }";

        var (markers, _, _) = SC2SaveLuaParser.Parse(lua);

        Assert.AreEqual(1, markers.Count);
        Assert.IsTrue(markers.ContainsKey("Mass_01"));
        Assert.AreEqual("STRING( 'Mass' )", markers["Mass_01"][0].RawValue);
    }

    [Test]
    public void ObjectsImport_SaveMarkersWin_MapobjsAddsOnlyPropsGraphsAndReferencedMarkers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sc2_object_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var terrainPath = Path.Combine(directory, "Merge.terrain.win.bdf");
            File.WriteAllBytes(terrainPath, Array.Empty<byte>());

            var mapobjs = new MapObjsModel();
            mapobjs.Props.Add(new MapProp
            {
                BlueprintPath = "/env/test/props/tree_prop.bp",
                Position = new[] { 4f, 5f, 6f }
            });
            mapobjs.Markers.Add(new MapMarker
            {
                Name = "Shared_Node",
                MarkerType = "Land Path Node",
                Position = new[] { 100f, 100f, 100f }
            });
            mapobjs.Markers.Add(new MapMarker
            {
                Name = "Bdf_Path_Only",
                MarkerType = "Land Path Node",
                Position = new[] { 40f, 50f, 60f }
            });
            mapobjs.Markers.Add(new MapMarker
            {
                Name = "Unused_Bdf_Marker",
                MarkerType = "Blank Marker",
                Position = new[] { 70f, 80f, 90f }
            });
            mapobjs.Markers.Add(new MapMarker
            {
                Name = "Self_Edge_Bdf_Marker",
                MarkerType = "Land Path Node",
                Position = new[] { 75f, 85f, 95f }
            });
            var graph = new MapGraph { Name = "LandGraph" };
            graph.Entries.Add((0, 1));
            graph.Entries.Add((3, 3));
            mapobjs.Graphs.Add(graph);
            var (mapData, mapOffsets) = MapObjsBdf.Serialize(mapobjs, BdfPlatform.Pc);
            File.WriteAllBytes(Path.Combine(directory, "Merge.mapobjs.win.bdf"), new BdfContainer
            {
                Platform = BdfPlatform.Pc,
                Data = mapData,
                OffsetTable = mapOffsets,
                NumFiles = 1
            }.Write(MapObjsBdf.Padding));

            File.WriteAllText(Path.Combine(directory, "Merge_save.lua"),
                "Scenario = { MasterChain = { _MASTERCHAIN_ = { Markers = {\n" +
                "['Shared_Node'] = { ['type'] = STRING( 'Land Path Node' ), ['position'] = VECTOR3( 10, 20, 30 ), },\n" +
                "['Lua_Only'] = { ['type'] = STRING( 'Blank Marker' ), ['position'] = VECTOR3( 1, 2, 3 ), },\n" +
                "}, }, }, Areas = {}, Armies = {}, Chains = {}, Props = {}, }");
            File.WriteAllText(Path.Combine(directory, "Merge_scenario.lua"),
                "ScenarioInfo = { StartPositions = { ['Shared_Node'] = { 999, 999, 999 }, ['Scenario_Only'] = { 8, 8, 8 }, }, }");

            var result = SC2ObjectsImport.ImportFromTerrainBdfPath(_data, terrainPath);

            Assert.IsTrue(result.Success, result.Summary);
            Assert.AreEqual(2, result.SaveLuaMarkerCount);
            Assert.AreEqual(1, result.MapObjsGraphMarkerCount);
            Assert.AreEqual(2, result.IgnoredMapObjsMarkerCount);
            Assert.AreEqual(1, _data.ObjProps.Count);
            Assert.AreEqual(1, _data.ObjGraphs.Count);
            Assert.AreEqual(3, _data.ObjMarkers.Count);
            Assert.AreEqual(1, _data.ObjMarkers.FindAll(marker => marker.Name == "Shared_Node").Count);
            Assert.AreEqual(new Vector3(10f, 20f, 30f), _data.ObjMarkers.Find(marker => marker.Name == "Shared_Node").Position,
                "save.lua position must win over both mapobjs.bdf and _scenario.lua");
            Assert.IsNotNull(_data.ObjMarkers.Find(marker => marker.Name == "Bdf_Path_Only"));
            Assert.IsNull(_data.ObjMarkers.Find(marker => marker.Name == "Unused_Bdf_Marker"));
            Assert.IsNull(_data.ObjMarkers.Find(marker => marker.Name == "Self_Edge_Bdf_Marker"));
            Assert.IsNull(_data.ObjMarkers.Find(marker => marker.Name == "Scenario_Only"));
            Assert.AreEqual(1, _data.ObjGraphs[0].MarkerIdsA.Count);
            Assert.AreEqual(_data.ObjMarkers.Find(marker => marker.Name == "Shared_Node").Id,
                _data.ObjGraphs[0].MarkerIdsA[0],
                "the graph must reference the retained save.lua marker, not a duplicate mapobjs row");
            Assert.AreNotEqual(_data.ObjGraphs[0].MarkerIdsA[0], _data.ObjGraphs[0].MarkerIdsB[0]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void DistinctPositions_CollapsedSymmetrySlots_ReturnsOnePosition()
    {
        var positions = new[]
        {
            new Vector3(512f, 100f, 512f),
            new Vector3(512f, 100f, 512f),
            new Vector3(512.0001f, 100f, 512f)
        };

        var distinct = SC2MarkerDeduplication.DistinctPositions(positions);

        Assert.AreEqual(1, distinct.Count);
    }

    [Test]
    public void EnsureObjectIds_StaleCounter_DoesNotReuseExistingMarkerId()
    {
        _data.ObjMarkers.Add(new SC2ObjMarker { Id = 40, Name = "Existing" });
        _data.NextObjectId = 1;

        _data.EnsureObjectIds();
        var allocated = _data.AllocateObjectId();

        Assert.AreEqual(41, allocated);
    }

    [Test]
    public void RemoveDuplicates_FirstWinsAndRemapsMarkerReferences()
    {
        _data.ObjMarkers.Clear();
        _data.ObjArmies.Clear();
        _data.ObjGraphs.Clear();
        _data.ObjChains.Clear();
        _data.ObjSymmetryGroups.Clear();
        var retained = new SC2ObjMarker { Id = 10, Name = "Mass_01", Type = "Mass", Position = new Vector3(2f, 100f, 2f) };
        var duplicate = new SC2ObjMarker { Id = 11, Name = "mass_01", Type = "Mass", Position = new Vector3(2f, 100f, 2f) };
        var other = new SC2ObjMarker { Id = 12, Name = "Land_01", Type = "Land Path Node", Position = new Vector3(8f, 100f, 8f) };
        _data.ObjMarkers.Add(retained);
        _data.ObjMarkers.Add(duplicate);
        _data.ObjMarkers.Add(other);
        _data.ObjGraphs.Add(new SC2ObjGraph
        {
            UsesMarkerIds = true,
            MarkerIdsA = { duplicate.Id },
            MarkerIdsB = { other.Id }
        });
        _data.ObjChains.Add(new SC2ObjChain { Name = "Chain", MarkerNames = { duplicate.Name, other.Name } });
        _data.ObjSymmetryGroups.Add(new SC2ObjSymmetryGroup
        {
            Kind = "Marker",
            Members =
            {
                new SC2ObjSymmetryMember { ObjectId = retained.Id },
                new SC2ObjSymmetryMember { ObjectId = duplicate.Id },
                new SC2ObjSymmetryMember { ObjectId = other.Id }
            }
        });

        var skipped = SC2MarkerDeduplication.RemoveDuplicates(_data);

        Assert.AreEqual(1, skipped);
        CollectionAssert.AreEqual(new[] { retained, other }, _data.ObjMarkers);
        Assert.AreEqual(retained.Id, _data.ObjGraphs[0].MarkerIdsA[0]);
        Assert.AreEqual(retained.Name, _data.ObjChains[0].MarkerNames[0]);
        Assert.AreEqual(2, _data.ObjSymmetryGroups[0].Members.Count);
    }
}
