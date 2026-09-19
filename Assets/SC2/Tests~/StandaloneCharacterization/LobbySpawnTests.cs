using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>The lobby-minimap spawn editor's logic: symmetry pairing, the
/// StartPositions block, the document command/serialization, and reading
/// StartPositions from a scenario.lua.</summary>
public class LobbySpawnTests
{
    // Real SC2_MP_001 army positions (_save.lua): a 180-degree rotation about
    // (512, 512) maps ARMY_1<->ARMY_2, ARMY_3<->ARMY_4, ARMY_5<->ARMY_6, ARMY_7<->ARMY_8.
    private static readonly Dictionary<string, Vector3> Mp001 = new()
    {
        ["ARMY_1"] = new(670f, 19.341f, 346f),
        ["ARMY_2"] = new(354f, 19.38f, 678f),
        ["ARMY_3"] = new(868f, 18.489f, 338f),
        ["ARMY_4"] = new(156f, 18.489f, 686f),
        ["ARMY_5"] = new(634f, 19.579f, 102f),
        ["ARMY_6"] = new(390f, 18.732f, 922f),
        ["ARMY_7"] = new(744f, 19.038f, 210f),
        ["ARMY_8"] = new(280f, 19.037f, 814f),
    };
    private static readonly Vector3 Center = new(512f, 0f, 512f);

    // ---- symmetry slots / pairing

    [Fact]
    public void GetSlots_MirrorX_NegatesTheXOffsetOnly()
    {
        var slots = SC2SymmetryMath.GetSlots(SymmetryMode.X, Center, 4, new Vector3(600f, 5f, 300f));
        Assert.Equal(2, slots.Count);
        Assert.Equal(new Vector3(424f, 5f, 300f), slots[1]);
    }

    [Fact]
    public void GetSlots_SlotCountPerMode()
    {
        var p = new Vector3(600f, 0f, 300f);
        Assert.Single(SC2SymmetryMath.GetSlots(SymmetryMode.None, Center, 4, p));
        Assert.Equal(4, SC2SymmetryMath.GetSlots(SymmetryMode.XY, Center, 4, p).Count);
        Assert.Equal(4, SC2SymmetryMath.GetSlots(SymmetryMode.DiagonalBoth, Center, 4, p).Count);
        Assert.Equal(6, SC2SymmetryMath.GetSlots(SymmetryMode.Radial, Center, 6, p).Count);
    }

    [Fact]
    public void GetSlots_RadialTwoIsAHalfTurn()
    {
        var slots = SC2SymmetryMath.GetSlots(SymmetryMode.Radial, Center, 2, new Vector3(868f, 18f, 338f));
        Assert.Equal(156f, slots[1].x, 2);
        Assert.Equal(686f, slots[1].z, 2);
    }

    [Fact]
    public void FindPartners_OnVanillaLayout_PairsArmiesAcrossTheCentre()
    {
        var partners = SC2LobbySpawnMath.FindPartners("ARMY_3", Mp001, SymmetryMode.Radial, Center, 2);
        Assert.Equal(new[] { new KeyValuePair<string, int>("ARMY_4", 1) }, partners);
        foreach (var pair in new[] { ("ARMY_1", "ARMY_2"), ("ARMY_5", "ARMY_6"), ("ARMY_7", "ARMY_8") })
            Assert.Equal(pair.Item2, SC2LobbySpawnMath.FindPartners(pair.Item1, Mp001, SymmetryMode.Radial, Center, 2).Single().Key);
    }

    [Fact]
    public void FindPartners_WithNoSymmetryOrNoMatch_ReturnsNothing()
    {
        Assert.Empty(SC2LobbySpawnMath.FindPartners("ARMY_3", Mp001, SymmetryMode.None, Center, 2));
        // A mirror across X does not line ARMY_3 up with any other army.
        Assert.Empty(SC2LobbySpawnMath.FindPartners("ARMY_3", Mp001, SymmetryMode.X, Center, 2));
        Assert.Empty(SC2LobbySpawnMath.FindPartners("ARMY_99", Mp001, SymmetryMode.Radial, Center, 2));
    }

    [Fact]
    public void FindPartners_ToleranceAbsorbsCellCentreOffsets()
    {
        var shifted = new Dictionary<string, Vector3>(Mp001) { ["ARMY_4"] = new(156.5f, 18.489f, 685.5f) };
        Assert.Single(SC2LobbySpawnMath.FindPartners("ARMY_3", shifted, SymmetryMode.Radial, Center, 2));
        shifted["ARMY_4"] = new Vector3(170f, 18.489f, 700f);
        Assert.Empty(SC2LobbySpawnMath.FindPartners("ARMY_3", shifted, SymmetryMode.Radial, Center, 2));
    }

    [Fact]
    public void MoveWithPartners_MirrorsTheNewPositionAndKeepsPartnerHeight()
    {
        var partners = SC2LobbySpawnMath.FindPartners("ARMY_3", Mp001, SymmetryMode.Radial, Center, 2);
        var lobby = new Dictionary<string, Vector3>(Mp001) { ["ARMY_4"] = new Vector3(156f, 99f, 686f) };

        var moved = SC2LobbySpawnMath.MoveWithPartners(new Vector3(800f, 18f, 300f), partners, lobby, SymmetryMode.Radial, Center, 2);

        Assert.Single(moved);
        Assert.Equal(224f, moved["ARMY_4"].x, 2);
        Assert.Equal(724f, moved["ARMY_4"].z, 2);
        Assert.Equal(99f, moved["ARMY_4"].y, 3);
    }

    [Fact]
    public void FourWayRotation_MovesAllThreePartners()
    {
        var square = new Dictionary<string, Vector3>
        {
            ["ARMY_1"] = new(700f, 0f, 512f), ["ARMY_2"] = new(512f, 0f, 700f),
            ["ARMY_3"] = new(324f, 0f, 512f), ["ARMY_4"] = new(512f, 0f, 324f),
        };
        var partners = SC2LobbySpawnMath.FindPartners("ARMY_1", square, SymmetryMode.Radial, Center, 4);
        Assert.Equal(3, partners.Count);
        var moved = SC2LobbySpawnMath.MoveWithPartners(new Vector3(800f, 0f, 512f), partners, square, SymmetryMode.Radial, Center, 4);
        Assert.Equal(3, moved.Count);
        Assert.Equal(224f, moved["ARMY_3"].x, 2);
    }

    [Fact]
    public void CompareArmyNames_OrdersByNumber()
    {
        var names = new List<string> { "ARMY_10", "ARMY_2", "ARMY_EXTRA", "ARMY_1" };
        names.Sort(SC2LobbySpawnMath.CompareArmyNames);
        Assert.Equal(new[] { "ARMY_1", "ARMY_2", "ARMY_10", "ARMY_EXTRA" }, names);
    }

    // ---- scenario.lua StartPositions block

    [Fact]
    public void BuildStartPositionsBody_UsesLobbyOverrides_AndFallsBackToGamePositions()
    {
        var armies = new List<KeyValuePair<string, Vector3>>
        {
            new("ARMY_1", new Vector3(10f, 2f, 20f)),
            new("ARMY_2", new Vector3(30f, 4f, 40f)),
        };
        var overrides = new Dictionary<string, Vector3>
        {
            ["ARMY_2"] = new Vector3(31.5f, 4f, 41.25f),
            ["ARMY_GONE"] = new Vector3(1f, 1f, 1f), // army no longer on the map
        };

        var body = SC2LobbySpawnMath.BuildStartPositionsBody(armies, overrides);

        Assert.Equal("\r\n        ['ARMY_1'] = { 10, 2, 20 },\r\n        ['ARMY_2'] = { 31.5, 4, 41.25 },\r\n    ", body);
        Assert.DoesNotContain("ARMY_GONE", body);
    }

    [Fact]
    public void BuildStartPositionsBody_WithNoOverrides_WritesGamePositionsLikeBefore()
    {
        var armies = new List<KeyValuePair<string, Vector3>> { new("ARMY_1", new Vector3(867.5f, 18.489f, 338.5f)) };
        Assert.Equal("\r\n        ['ARMY_1'] = { 867.5, 18.489, 338.5 },\r\n    ",
            SC2LobbySpawnMath.BuildStartPositionsBody(armies, null));
    }

    // ---- rollback when the real spawn moves

    private static SC2LobbyStartPosition Entry(string army, float x, float z, float? baseX = null, float? baseZ = null)
    {
        var e = new SC2LobbyStartPosition { ArmyName = army, X = x, Y = 1f, Z = z };
        if (baseX.HasValue) e.SetBase(new Vector3(baseX.Value, 0f, baseZ.Value));
        return e;
    }

    [Fact]
    public void IsCurrent_HoldsWhileTheRealSpawnStaysPut_AndFailsOnceItMoves()
    {
        var e = Entry("ARMY_1", 100f, 100f, 670f, 346f);
        Assert.True(e.IsCurrent(new Vector3(670f, 19f, 346f)));
        Assert.True(e.IsCurrent(new Vector3(670.005f, 19f, 346f)));
        Assert.False(e.IsCurrent(new Vector3(671f, 19f, 346f)));
        Assert.False(e.IsCurrent(new Vector3(670f, 19f, 350f)));
        // Height is not part of the comparison.
        Assert.True(e.IsCurrent(new Vector3(670f, 999f, 346f)));
    }

    [Fact]
    public void IsCurrent_WithoutARecordedBase_IsAlwaysTrue()
    {
        Assert.True(Entry("ARMY_1", 1f, 1f).IsCurrent(new Vector3(5f, 0f, 5f)));
    }

    [Fact]
    public void CurrentOverrides_DropsMovedArmies_MissingArmies_AndKeepsTheRest()
    {
        var real = new Dictionary<string, Vector3>(Mp001) { ["ARMY_2"] = new Vector3(400f, 19f, 700f) }; // ARMY_2 moved in Objects
        var list = new List<SC2LobbyStartPosition>
        {
            Entry("ARMY_1", 1f, 2f, 670f, 346f),
            Entry("ARMY_2", 3f, 4f, 354f, 678f),   // stale: real spawn moved from (354,678)
            Entry("ARMY_GONE", 5f, 6f, 0f, 0f),     // no such army any more
            Entry("ARMY_3", 7f, 8f),                // no base recorded: still applies
        };

        var current = SC2LobbyStartPosition.CurrentOverrides(list, real);

        Assert.Equal(new[] { "ARMY_1", "ARMY_3" }, current.Keys.OrderBy(k => k));
        Assert.Equal(1f, current["ARMY_1"].x);
    }

    [Fact]
    public void RollingBackTheRealMove_RevivesTheLobbyPosition()
    {
        var list = new List<SC2LobbyStartPosition> { Entry("ARMY_1", 1f, 2f, 670f, 346f) };
        var real = new Dictionary<string, Vector3>(Mp001) { ["ARMY_1"] = new Vector3(600f, 0f, 300f) };
        Assert.Empty(SC2LobbyStartPosition.CurrentOverrides(list, real));
        real["ARMY_1"] = Mp001["ARMY_1"]; // e.g. the Objects move was undone
        Assert.Single(SC2LobbyStartPosition.CurrentOverrides(list, real));
    }

    [Fact]
    public void AdoptBases_FillsOnlyEntriesWithoutOne()
    {
        var list = new List<SC2LobbyStartPosition>
        {
            Entry("ARMY_1", 1f, 1f),
            Entry("ARMY_2", 2f, 2f, 5f, 5f),
            Entry("ARMY_GONE", 3f, 3f),
        };

        SC2LobbyStartPosition.AdoptBases(list, Mp001);

        Assert.True(list[0].HasBase);
        Assert.Equal(670f, list[0].BaseX);
        Assert.Equal(346f, list[0].BaseZ);
        Assert.Equal(5f, list[1].BaseX); // already had one
        Assert.False(list[2].HasBase);   // no marker to adopt from
    }

    [Fact]
    public void ImportedVanillaLobbyPositions_StayValidAfterAdoptingTheirMarkers()
    {
        // Vanilla scenario positions sit half a cell off the save.lua markers:
        // adopting the marker position as the base keeps them applying.
        var list = new List<SC2LobbyStartPosition> { Entry("ARMY_3", 867.5f, 338.5f) };
        SC2LobbyStartPosition.AdoptBases(list, Mp001);
        Assert.Single(SC2LobbyStartPosition.CurrentOverrides(list, Mp001));
        var moved = new Dictionary<string, Vector3>(Mp001) { ["ARMY_3"] = new Vector3(900f, 18f, 338f) };
        Assert.Empty(SC2LobbyStartPosition.CurrentOverrides(list, moved));
    }

    // ---- command

    [Fact]
    public void Command_DoUndoRedo_ReplacesTheListAndMarksScenarioLuaDirty()
    {
        var document = new SC2MapDocument();
        document.Scenario.LobbyStartPositions.Add(new SC2LobbyStartPosition { ArmyName = "ARMY_1", X = 1, Y = 2, Z = 3 });
        var history = new SC2CommandHistory();
        var next = new List<SC2LobbyStartPosition>
        {
            new() { ArmyName = "ARMY_1", X = 10, Y = 2, Z = 30 },
            new() { ArmyName = "ARMY_2", X = 5, Y = 6, Z = 7 },
        };

        history.Execute(document, new SC2SetLobbyStartPositionsCommand(next));
        Assert.Equal(2, document.Scenario.LobbyStartPositions.Count);
        Assert.Equal(10f, document.Scenario.LobbyStartPositions[0].X);

        // Mutating the caller's list afterwards must not leak into the document.
        next[0].X = 999;
        Assert.Equal(10f, document.Scenario.LobbyStartPositions[0].X);

        history.Undo(document);
        Assert.Single(document.Scenario.LobbyStartPositions);
        Assert.Equal(1f, document.Scenario.LobbyStartPositions[0].X);

        history.Redo(document);
        Assert.Equal(2, document.Scenario.LobbyStartPositions.Count);
    }

    [Fact]
    public void Command_OnlyDirtiesScenarioLua_NotObjectsOrSaveLua()
    {
        var document = new SC2MapDocument();
        var change = new SC2SetLobbyStartPositionsCommand(new List<SC2LobbyStartPosition>()).Do(document);
        Assert.Equal(SC2DirtyFlags.ScenarioLua, change.DirectDirtyFlags);
        Assert.Equal(new[] { SC2DomainId.Scenario }, change.ChangedDomains);
        Assert.Empty(change.InvalidatedDomains);
    }

    // ---- serialization

    [Fact]
    public void Serializer_RoundTripsLobbyStartPositions()
    {
        var doc = new SC2MapDocument();
        doc.Scenario.LobbyStartPositions.Add(new SC2LobbyStartPosition { ArmyName = "ARMY_1", X = 867.5f, Y = 18.489f, Z = 338.5f, HasBase = true, BaseX = 868f, BaseZ = 338f });
        doc.Scenario.LobbyStartPositions.Add(new SC2LobbyStartPosition { ArmyName = "ARMY_2", X = 1f, Y = 2f, Z = 3f });

        var read = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        Assert.Equal(2, read.Scenario.LobbyStartPositions.Count);
        Assert.Equal("ARMY_1", read.Scenario.LobbyStartPositions[0].ArmyName);
        Assert.Equal(867.5f, read.Scenario.LobbyStartPositions[0].X);
        Assert.Equal(18.489f, read.Scenario.LobbyStartPositions[0].Y, 3);
        Assert.Equal(338.5f, read.Scenario.LobbyStartPositions[0].Z);
        Assert.Equal(3f, read.Scenario.LobbyStartPositions[1].Z);
        Assert.True(read.Scenario.LobbyStartPositions[0].HasBase);
        Assert.Equal(868f, read.Scenario.LobbyStartPositions[0].BaseX);
        Assert.Equal(338f, read.Scenario.LobbyStartPositions[0].BaseZ);
        Assert.False(read.Scenario.LobbyStartPositions[1].HasBase);
    }

    [Fact]
    public void Serializer_ASaveWithoutTheKeyLoadsAsEmpty()
    {
        var json = SC2MapDocumentSerializer.Serialize(new SC2MapDocument());
        Assert.DoesNotContain("\"army\"", json);
        Assert.Empty(SC2MapDocumentSerializer.Deserialize(json).Scenario.LobbyStartPositions);
    }

    // ---- reading StartPositions from scenario.lua

    private const string ScenarioWithStartPositions = @"
version = 3
ScenarioInfo = {
    name = 'T', type = 'skirmish', size = {1024, 1024},
    StartPositions = {
        ['ARMY_10'] = { 1, 2, 3 },
        ['ARMY_2'] = { 867.5, 18.489, 338.5 },
        ['ARMY_1'] = { 10, 20, 30 },
    },
    Configurations = { ['standard'] = { teams = { { name = 'FFA', armies = {} } }, customprops = {} } },
}
";

    [Fact]
    public void ScenarioBridge_ReadsStartPositions_SortedByArmyNumber_AndNotIntoExtraFields()
    {
        var root = Directory.CreateTempSubdirectory("sc2lobby_").FullName;
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(Encoding.ASCII.GetBytes(ScenarioWithStartPositions), null, null, new SC2DirectoryBlobStore(root));

            Assert.Equal(new[] { "ARMY_1", "ARMY_2", "ARMY_10" }, doc.LobbyStartPositions.Select(p => p.ArmyName));
            Assert.Equal(867.5f, doc.LobbyStartPositions[1].X, 3);
            Assert.Equal(18.489f, doc.LobbyStartPositions[1].Y, 3);
            Assert.Equal(338.5f, doc.LobbyStartPositions[1].Z, 3);
            Assert.DoesNotContain(doc.KnownFields.ExtraFields, f => f.Key == "StartPositions");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ScenarioBridge_ReadsTheRealCompiledVanillaStartPositions()
    {
        if (!TestCorpusLuaCompiled.IsAvailable) return;
        var path = TestCorpusLuaCompiled.ScenarioLuaPath("SC2_MP_001");
        if (!File.Exists(path)) return;

        var root = Directory.CreateTempSubdirectory("sc2lobby_").FullName;
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(File.ReadAllBytes(path), null, null, new SC2DirectoryBlobStore(root));

            Assert.Equal(8, doc.LobbyStartPositions.Count);
            var army3 = doc.LobbyStartPositions.Single(p => p.ArmyName == "ARMY_3");
            Assert.Equal(867.5f, army3.X, 2);
            Assert.Equal(18.489f, army3.Y, 2);
            Assert.Equal(338.5f, army3.Z, 2);
            Assert.Equal("ARMY_1", doc.LobbyStartPositions[0].ArmyName);
        }
        finally { Directory.Delete(root, true); }
    }
}
