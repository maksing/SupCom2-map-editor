using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>A node that works on a group may name several: Spawn group creates each of them, the other nodes act on the units of all of them.</summary>
public class MissionMultiGroupTests
{
    private static SC2MissionDocument OneNode(string type, params (string key, string value)[] set)
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var node = stage.AddNode(type, 0, 0);
        foreach (var (key, value) in set) node.SetParam(key, value);
        stage.Connect(0, "out", node.Id);
        doc.Stages.Add(stage);
        return doc;
    }

    private static string Lua(SC2MissionDocument doc)
    {
        var result = SC2MissionCompiler.Compile(doc, new SC2MissionMapInfo { ArmyOrder = new List<string> { "ARMY_1", "ARMY_2" }, PlayableArea = "AREA_1" });
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error + "\n" + result.Lua);
        return result.Lua;
    }

    private static SC2MissionMapInfo MapWithGroups() => new()
    {
        ArmyOrder = new List<string> { "ARMY_1", "ARMY_2" },
        Armies = new HashSet<string> { "ARMY_1", "ARMY_2" },
        Groups = new Dictionary<string, HashSet<string>> { ["ARMY_2"] = new HashSet<string> { "WAVE", "FORT" } },
        GroupUnitCounts = new Dictionary<string, Dictionary<string, int>> { ["ARMY_2"] = new Dictionary<string, int> { ["WAVE"] = 3, ["FORT"] = 2 } },
    };

    [Fact]
    public void SpawnGroupCreatesEachNamedGroupUnderItsOwnKey()
    {
        var lua = Lua(OneNode("spawn_group", ("army", "ARMY_2"), ("group", "WAVE, FORT")));

        Assert.Contains("pcall(ScenarioUtils.CreateArmyGroup, \"ARMY_2\", \"WAVE\")", lua);
        Assert.Contains("pcall(ScenarioUtils.CreateArmyGroup, \"ARMY_2\", \"FORT\")", lua);
        Assert.Contains("ScenarioInfo.SCED.groups[\"ARMY_2/WAVE\"] = group", lua);
        Assert.Contains("ScenarioInfo.SCED.groups[\"ARMY_2/FORT\"] = group", lua);
    }

    [Fact]
    public void OneGroupIsWrittenAsItAlwaysWas()
    {
        var lua = Lua(OneNode("spawn_group", ("army", "ARMY_2"), ("group", "WAVE")));

        Assert.Contains("local ok, group = pcall(ScenarioUtils.CreateArmyGroup, \"ARMY_2\", \"WAVE\")", lua);
        Assert.DoesNotContain("do\n\t\tlocal ok, group", lua);
    }

    [Fact]
    public void ANodeThatWatchesSeveralGroupsGetsTheUnitsOfAllOfThem()
    {
        var lua = Lua(OneNode("objective_kill", ("army", "ARMY_2"), ("group", "WAVE, FORT")));

        Assert.Contains("local group = SCED_Group(\"ARMY_2\", \"WAVE,FORT\")", lua);
        // the helper joins the groups
        Assert.Contains("string.gfind(name, '[^,]+')", lua);
        Assert.Contains("table.insert(merged, unit)", lua);
    }

    [Fact]
    public void ANodeWithOneGroupStillReadsTheStoredGroupDirectly()
    {
        var lua = Lua(OneNode("objective_kill", ("army", "ARMY_2"), ("group", "WAVE")));

        Assert.Contains("local group = ScenarioInfo.SCED.groups[\"ARMY_2/WAVE\"]", lua);
    }

    [Theory]
    [InlineData("group_killed")]
    [InlineData("objective_protect")]
    [InlineData("objective_capture")]
    [InlineData("objective_kill_or_capture")]
    [InlineData("give_order")]
    [InlineData("group_move_marker")]
    [InlineData("group_stop")]
    [InlineData("group_state")]
    [InlineData("group_veterancy")]
    [InlineData("group_flags")]
    [InlineData("group_partly_killed")]
    [InlineData("group_random_patrol")]
    [InlineData("group_attack_unit")]
    [InlineData("group_guard_unit")]
    [InlineData("nis_track_group")]
    [InlineData("nis_group_visible")]
    [InlineData("nis_warp_group")]
    [InlineData("nis_group_move_chain")]
    [InlineData("loop_foreach")]
    public void NodesThatActOnAGroupsUnitsTakeSeveralGroups(string type)
    {
        var def = SC2MissionNodeCatalog.Find(type);
        Assert.Equal(SC2MissionParamKind.GroupList, def.Params.Single(p => p.Key == "group").Kind);

        // and each one compiles with two groups named
        var doc = OneNode(type, ("army", "ARMY_2"), ("group", "WAVE, FORT"), ("marker", "M"), ("chain", "C"), ("targetarmy", "ARMY_1"), ("targetunit", "U"));
        var lua = Lua(doc);
        Assert.Contains("WAVE", lua);
        Assert.Contains("FORT", lua);
    }

    [Theory]
    [InlineData("spawn_platoon")]
    [InlineData("platoon_order")]
    [InlineData("platoon_killed")]
    [InlineData("group_give")]
    [InlineData("transport_deploy")]
    [InlineData("nis_transport_arrival")]
    [InlineData("nis_create_group")]
    public void NodesThatKeepAGroupUnderOneKeyOrPassItsNameOnStayWithOneGroup(string type)
    {
        // a platoon, the group handed to another army and a group given to the transport helper are each stored or looked up by one name
        var def = SC2MissionNodeCatalog.Find(type);
        Assert.Equal(SC2MissionParamKind.Group, def.Params.Single(p => p.Key == "group").Kind);
    }

    [Fact]
    public void TheValidatorChecksEveryNamedGroup()
    {
        var doc = OneNode("objective_kill", ("army", "ARMY_2"), ("group", "WAVE, NOPE"));

        var issues = SC2MissionValidator.Validate(doc, MapWithGroups());

        Assert.Contains(issues, i => i.Message.Contains("ARMY_2 has no group 'NOPE'"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("no group 'WAVE'"));
    }

    [Fact]
    public void ASpawnNodeCountsForEveryGroupItNames()
    {
        var doc = OneNode("spawn_group", ("army", "ARMY_2"), ("group", "WAVE, FORT"));
        var stage = doc.Stages[0];
        var watch = stage.AddNode("objective_kill", 200, 0);
        watch.SetParam("army", "ARMY_2");
        watch.SetParam("group", "FORT");
        stage.Connect(stage.Nodes[0].Id, "out", watch.Id);

        var issues = SC2MissionValidator.Validate(doc, MapWithGroups());

        Assert.DoesNotContain(issues, i => i.Message.Contains("No Spawn group node creates"));
    }

    [Fact]
    public void AGroupNobodySpawnsIsStillReported()
    {
        var doc = OneNode("spawn_group", ("army", "ARMY_2"), ("group", "WAVE"));
        var stage = doc.Stages[0];
        var watch = stage.AddNode("objective_kill", 200, 0);
        watch.SetParam("army", "ARMY_2");
        watch.SetParam("group", "WAVE, FORT");
        stage.Connect(stage.Nodes[0].Id, "out", watch.Id);

        var issues = SC2MissionValidator.Validate(doc, MapWithGroups());

        Assert.Contains(issues, i => i.Message.Contains("No Spawn group node creates ARMY_2/FORT"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("No Spawn group node creates ARMY_2/WAVE"));
    }

    [Fact]
    public void AnEmptyGroupListIsAnError()
    {
        var doc = OneNode("spawn_group", ("army", "ARMY_2"), ("group", " , "));

        var issues = SC2MissionValidator.Validate(doc, MapWithGroups());

        Assert.Contains(issues, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("Groups is not set"));
    }

    [Fact]
    public void SplitListTrimsAndDropsEmptyNames()
    {
        Assert.Equal(new[] { "A", "B" }, SC2MissionNodeCatalog.SplitList(" A ,, B ,"));
        Assert.Empty(SC2MissionNodeCatalog.SplitList(null));
        Assert.Empty(SC2MissionNodeCatalog.SplitList(" , "));
    }
}
