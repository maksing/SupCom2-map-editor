using System;
using System.Collections.Generic;
using SC2.MapEditor.Document;
using UnityEngine;

namespace SC2CharacterizationTests;

public class ObjectsDocumentBridgeTests
{
    private static SC2ObjectsDocument BuildSampleDocument(
        out List<SC2ObjProp> props, out List<SC2ObjMarker> markers, out List<SC2ObjGraph> graphs,
        out List<SC2ObjArmy> armies, out List<SC2ObjUnitNode> units, out List<SC2ObjZone> zones,
        out List<SC2ObjChain> chains, out List<SC2ObjSymmetryGroup> symmetryGroups)
    {
        props = new List<SC2ObjProp> { new() { Id = 1, BlueprintPath = "/props/tree.bp", Position = new Vector3(1, 2, 3) } };
        markers = new List<SC2ObjMarker> { new() { Id = 2, Name = "Mass_01", Type = "Mass" } };
        graphs = new List<SC2ObjGraph> { new() { Id = 3, Name = "PathGraph" } };
        armies = new List<SC2ObjArmy> { new() { Id = 4, Name = "ARMY_1" } };
        units = new List<SC2ObjUnitNode> { new() { Id = 5, ArmyName = "ARMY_1", Type = "uca0104" } };
        zones = new List<SC2ObjZone> { new() { Id = 6, Name = "Zone_1", Role = "Playable" } };
        chains = new List<SC2ObjChain> { new() { Id = 7, Name = "Chain_1" } };
        symmetryGroups = new List<SC2ObjSymmetryGroup> { new() { Id = 8, Kind = "Marker" } };

        return SC2ObjectsLegacyBridge.BuildFromLegacy(
            props, markers, graphs, armies, units, zones, chains, symmetryGroups, nextObjectId: 9);
    }

    [Fact]
    public void BuildFromLegacy_ProjectsAllEightListsAndNextObjectId()
    {
        var doc = BuildSampleDocument(out _, out _, out _, out _, out _, out _, out _, out _);

        Assert.Single(doc.Props);
        Assert.Equal("/props/tree.bp", doc.Props[0].BlueprintPath);
        Assert.Single(doc.Markers);
        Assert.Equal("Mass_01", doc.Markers[0].Name);
        Assert.Single(doc.Graphs);
        Assert.Equal("PathGraph", doc.Graphs[0].Name);
        Assert.Single(doc.Armies);
        Assert.Equal("ARMY_1", doc.Armies[0].Name);
        Assert.Single(doc.Units);
        Assert.Equal("uca0104", doc.Units[0].Type);
        Assert.Single(doc.Zones);
        Assert.Equal("Playable", doc.Zones[0].Role);
        Assert.Single(doc.Chains);
        Assert.Equal("Chain_1", doc.Chains[0].Name);
        Assert.Single(doc.SymmetryGroups);
        Assert.Equal("Marker", doc.SymmetryGroups[0].Kind);
        Assert.Equal(9, doc.NextObjectId);
    }

    [Fact]
    public void BuildFromLegacy_ListsAreIndependentCopiesOfTheSourceLists()
    {
        var doc = BuildSampleDocument(out var props, out _, out _, out _, out _, out _, out _, out _);

        doc.Props.Add(new SC2ObjProp { Id = 99 });

        Assert.Equal(2, doc.Props.Count);
        Assert.Single(props); // the original list passed in is untouched
    }

    [Fact]
    public void BuildFromLegacy_ElementsAreSharedReferencesNotDeepClones()
    {
        var doc = BuildSampleDocument(out var props, out _, out _, out _, out _, out _, out _, out _);

        // Mutating the shared element through the document is visible on
        // the original source object too -- this is the documented,
        // deliberate "list-level snapshot, not deep clone" behavior.
        doc.Props[0].BlueprintPath = "/props/changed.bp";

        Assert.Equal("/props/changed.bp", props[0].BlueprintPath);
        Assert.Same(props[0], doc.Props[0]);
    }

    [Fact]
    public void BuildFromLegacy_DefaultsSaveAndScenarioLuaTextToEmpty()
    {
        var doc = BuildSampleDocument(out _, out _, out _, out _, out _, out _, out _, out _);

        Assert.Equal("", doc.SaveLuaTextOriginal);
        Assert.Equal("", doc.ScenarioLuaTextOriginal);
    }

    [Fact]
    public void BuildFromLegacy_PopulatesSaveAndScenarioLuaTextWhenProvided()
    {
        var doc = SC2ObjectsLegacyBridge.BuildFromLegacy(
            new List<SC2ObjProp>(), new List<SC2ObjMarker>(), new List<SC2ObjGraph>(),
            new List<SC2ObjArmy>(), new List<SC2ObjUnitNode>(), new List<SC2ObjZone>(),
            new List<SC2ObjChain>(), new List<SC2ObjSymmetryGroup>(), nextObjectId: 1,
            saveLuaTextOriginal: "-- original save lua",
            scenarioLuaTextOriginal: "-- original scenario lua");

        Assert.Equal("-- original save lua", doc.SaveLuaTextOriginal);
        Assert.Equal("-- original scenario lua", doc.ScenarioLuaTextOriginal);
    }

    [Fact]
    public void BuildFromLegacy_EmptyListsProduceEmptyDocument()
    {
        var doc = SC2ObjectsLegacyBridge.BuildFromLegacy(
            new List<SC2ObjProp>(), new List<SC2ObjMarker>(), new List<SC2ObjGraph>(),
            new List<SC2ObjArmy>(), new List<SC2ObjUnitNode>(), new List<SC2ObjZone>(),
            new List<SC2ObjChain>(), new List<SC2ObjSymmetryGroup>(), nextObjectId: 1);

        Assert.Empty(doc.Props);
        Assert.Empty(doc.Markers);
        Assert.Empty(doc.Graphs);
        Assert.Empty(doc.Armies);
        Assert.Empty(doc.Units);
        Assert.Empty(doc.Zones);
        Assert.Empty(doc.Chains);
        Assert.Empty(doc.SymmetryGroups);
        Assert.Equal(1, doc.NextObjectId);
    }

    [Fact]
    public void BuildFromLegacy_ThrowsOnAnyNullList()
    {
        var empty = new List<SC2ObjProp>();
        Assert.Throws<ArgumentNullException>(() => SC2ObjectsLegacyBridge.BuildFromLegacy(
            null, new List<SC2ObjMarker>(), new List<SC2ObjGraph>(), new List<SC2ObjArmy>(),
            new List<SC2ObjUnitNode>(), new List<SC2ObjZone>(), new List<SC2ObjChain>(),
            new List<SC2ObjSymmetryGroup>(), 1));

        Assert.Throws<ArgumentNullException>(() => SC2ObjectsLegacyBridge.BuildFromLegacy(
            new List<SC2ObjProp>(), null, new List<SC2ObjGraph>(), new List<SC2ObjArmy>(),
            new List<SC2ObjUnitNode>(), new List<SC2ObjZone>(), new List<SC2ObjChain>(),
            new List<SC2ObjSymmetryGroup>(), 1));
    }
}
