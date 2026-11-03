using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>Variables, bases, co-op settings, the extra objectives, dialogue and cutscene nodes, and the test start stage.</summary>
public class MissionGraphFeatureTests
{
    private static string Lua(SC2MissionDocument doc, SC2MissionMapInfo map = null)
    {
        var result = SC2MissionCompiler.Compile(doc, map);
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error + "\n" + result.Lua);
        return result.Lua;
    }

    [Fact]
    public void HoldTheLine_HasNoErrorsAndCompiles()
    {
        var doc = SC2MissionSamples.HoldTheLine();

        var result = SC2MissionCompiler.Compile(doc);

        Assert.False(result.HasErrors, string.Join("\n", result.Issues));
        var lua = Lua(doc);
        Assert.Contains("AddBaseManager(\"EnemyMain\", \"ENEMY_BASE\", 50", lua);
        Assert.Contains("base:StartNonZeroBase(2)", lua);
        Assert.Contains("opai:Disable()", lua);
        Assert.Contains("SimObjectives.Timer(", lua);
        Assert.Contains("NIS.MoveTo(\"CAM_1\"", lua);
        Assert.Contains("NIS.StartNIS_Standard(nil, true, true)", lua);
        Assert.Contains("NIS.EndNIS_Standard(nil, false, 2)", lua);
        Assert.DoesNotContain("ScenarioCinematics", lua);
        Assert.Contains("ScenarioInfo.Options.DoNotShareUnitCap = true", lua);
        Assert.Contains("SCED_HumanCount() > 1", lua);
    }

    [Fact]
    public void HoldTheLine_RegionsAreAllIntact()
    {
        var regions = SC2MissionLuaMarkers.Parse(SC2MissionCompiler.Compile(SC2MissionSamples.HoldTheLine()).Lua);

        Assert.Equal(new[] { "common", "armies", "bases", "intro", "stage1", "stage2", "campaign" }, regions.Select(r => r.Id).ToArray());
        Assert.All(regions, r => Assert.True(r.Intact, r.Id));
    }

    [Fact]
    public void Coop_FailWhenAnyAndAllPickTheirOwnTest_AndNeverStartsNoWatcher()
    {
        var doc = SC2MissionSamples.HoldTheLine();

        doc.Settings.FailWhen = SC2MissionSettings.FailAny;
        Assert.Contains("if dead > 0 then ScenarioGameCleanup.Defeat()", Lua(doc));

        doc.Settings.FailWhen = SC2MissionSettings.FailAll;
        Assert.Contains("if dead > 0 and alive == 0 then", Lua(doc));

        doc.Settings.FailWhen = SC2MissionSettings.FailNever;
        var never = Lua(doc);
        Assert.DoesNotContain("ForkThread(SCED_WatchPlayers)", never);
    }

    [Fact]
    public void Coop_WatchesOnlyTheHumanSlots_AndSharedCapFlipsTheOption()
    {
        var doc = SC2MissionSamples.HoldTheLine();
        doc.Settings.HumanSlots = 3;
        doc.Settings.SharedCap = true;

        var lua = Lua(doc);

        Assert.Contains("{ \"ARMY_1\", \"ARMY_2\", \"ARMY_3\", }", lua);
        Assert.Contains("DoNotShareUnitCap = false", lua);
    }

    [Fact]
    public void WithoutCoop_NoWatcherIsGenerated()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();

        Assert.DoesNotContain("SCED_WatchPlayers", Lua(doc));
    }

    [Fact]
    public void TestStartStage_MakesBeginStartThatStage_AndWarns()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Settings.TestStartStage = "stage2";

        var result = SC2MissionCompiler.Compile(doc);

        Assert.Contains("\tSCED_stage2_Start()\n", result.Lua.Substring(result.Lua.IndexOf("function SCED_Begin", System.StringComparison.Ordinal)));
        Assert.Contains(result.Issues, i => i.Severity == SC2MissionSeverity.Warning && i.Message.Contains("Test start stage"));
        Assert.False(result.HasErrors);

        doc.Settings.TestStartStage = "missing";
        Assert.True(SC2MissionCompiler.Compile(doc).HasErrors);
    }

    [Fact]
    public void Variables_FeedNumbersAndBranches()
    {
        var doc = new SC2MissionDocument();
        doc.Variables.Add(new SC2MissionVariable { Name = "delay", Type = SC2MissionVariable.Number, Initial = "15" });
        doc.Variables.Add(new SC2MissionVariable { Name = "mode", Type = SC2MissionVariable.Text, Initial = "easy" });
        var stage = new SC2MissionStage { Id = "stage1" };
        var timer = stage.AddNode(SC2MissionNodeCatalog.Timer, 0, 0);
        timer.SetParam("seconds", "$delay");
        var branch = stage.AddNode(SC2MissionNodeCatalog.Branch, 0, 0);
        branch.SetParam("variable", "mode");
        branch.SetParam("compare", "==");
        branch.SetParam("value", "easy");
        var win = stage.AddNode(SC2MissionNodeCatalog.Victory, 0, 0);
        var lose = stage.AddNode(SC2MissionNodeCatalog.Defeat, 0, 0);
        stage.Connect(0, "out", timer.Id);
        stage.Connect(timer.Id, "fired", branch.Id);
        stage.Connect(branch.Id, "true", win.Id);
        stage.Connect(branch.Id, "false", lose.Id);
        doc.Stages.Add(stage);

        var result = SC2MissionCompiler.Compile(doc);

        Assert.False(result.HasErrors, string.Join("\n", result.Issues));
        Assert.Contains("ScenarioInfo.SCED.vars[\"delay\"] = 15", Lua(doc));
        Assert.Contains("ScenarioInfo.SCED.vars[\"mode\"] = \"easy\"", Lua(doc));
        Assert.Contains("CreateTimerTrigger(SCED_stage1_N1_fired, (ScenarioInfo.SCED.vars[\"delay\"] or 0))", Lua(doc));
        Assert.Contains("if ScenarioInfo.SCED.vars[\"mode\"] == \"easy\" then", Lua(doc));
    }

    [Fact]
    public void Validator_CatchesVariableMistakes()
    {
        var doc = SC2MissionSamples.HoldTheLine();
        doc.Variables.Add(new SC2MissionVariable { Name = "label", Type = SC2MissionVariable.Text, Initial = "x" });
        doc.Variables.Add(new SC2MissionVariable { Name = "label", Type = SC2MissionVariable.Text, Initial = "y" });
        doc.Variables.Add(new SC2MissionVariable { Name = "bad name", Type = SC2MissionVariable.Number, Initial = "1" });
        doc.Variables.Add(new SC2MissionVariable { Name = "flag", Type = SC2MissionVariable.Bool, Initial = "maybe" });
        var stage = doc.Stages[2];
        var add = stage.AddNode(SC2MissionNodeCatalog.SetVariable, 0, 0);
        add.SetParam("variable", "label");
        add.SetParam("op", "add");
        var ghost = stage.AddNode(SC2MissionNodeCatalog.Timer, 0, 0);
        ghost.SetParam("seconds", "$nowhere");
        var compare = stage.AddNode(SC2MissionNodeCatalog.Branch, 0, 0);
        compare.SetParam("variable", "label");
        compare.SetParam("compare", ">");
        compare.SetParam("value", "y");

        var messages = SC2MissionValidator.Validate(doc).Select(i => i.Message).ToList();

        Assert.Contains(messages, m => m.Contains("Two variables are called 'label'"));
        Assert.Contains(messages, m => m.Contains("'bad name' must be"));
        Assert.Contains(messages, m => m.Contains("start value of variable 'flag'"));
        Assert.Contains(messages, m => m.Contains("Add only works on number variables"));
        Assert.Contains(messages, m => m.Contains("variable 'nowhere' does not exist"));
        Assert.Contains(messages, m => m.Contains("can only be compared with == or ~="));
    }

    [Fact]
    public void Validator_CatchesBaseMistakes()
    {
        var doc = SC2MissionSamples.HoldTheLine();
        var other = doc.Bases[0].Clone();
        other.Radius = 0;
        other.OpAIs[0].Type = "NoSuchAI";
        other.OpAIs[0].MaxPlatoons = 0;
        doc.Bases.Add(other);

        var messages = SC2MissionValidator.Validate(doc).Where(i => i.Severity == SC2MissionSeverity.Error).Select(i => i.Message).ToList();

        Assert.Contains(messages, m => m.Contains("Two bases are called 'EnemyMain'"));
        Assert.Contains(messages, m => m.Contains("radius must be more than 0"));
        Assert.Contains(messages, m => m.Contains("another OpAI already has this name"));
        Assert.Contains(messages, m => m.Contains("unknown OpAI type 'NoSuchAI'"));
        Assert.Contains(messages, m => m.Contains("max platoons must be at least 1"));
    }

    [Fact]
    public void Validator_UsesTheMapToWarnAboutMissingNames()
    {
        var doc = SC2MissionSamples.HoldTheLine();
        var map = new SC2MissionMapInfo
        {
            Armies = new HashSet<string> { "ARMY_1", "ARMY_2", "ARMY_3" },
            Areas = new HashSet<string>(),
            Markers = new HashSet<string> { "ENEMY_BASE" },
            Chains = new HashSet<string>(),
            Groups = new Dictionary<string, HashSet<string>> { ["ARMY_3"] = new HashSet<string> { "OUTPOST" } },
            Units = new Dictionary<string, HashSet<string>>(),
        };

        var warnings = SC2MissionValidator.Validate(doc, map).Where(i => i.Severity == SC2MissionSeverity.Warning).Select(i => i.Message).ToList();

        Assert.Contains(warnings, m => m.Contains("no marker 'CAM_1'"));
        Assert.Contains(warnings, m => m.Contains("no chain 'RAID_1'"));
        Assert.DoesNotContain(warnings, m => m.Contains("no group 'OUTPOST'"));
        Assert.DoesNotContain(warnings, m => m.Contains("no marker 'ENEMY_BASE'"));
    }

    [Fact]
    public void ResolveObjective_PointsAtAnObjectiveNodeOfTheSameStage()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var objective = stage.AddNode(SC2MissionNodeCatalog.ObjectiveTimer, 0, 0);
        var resolve = stage.AddNode(SC2MissionNodeCatalog.ObjectiveResolve, 0, 0);
        resolve.SetParam("node", objective.Id.ToString());
        resolve.SetParam("result", "failed");
        stage.Connect(0, "out", objective.Id);
        stage.Connect(objective.Id, "completed", resolve.Id);
        doc.Stages.Add(stage);

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains("is not an objective"));
        Assert.Contains("objective:ManualResult(false)", Lua(doc));

        resolve.SetParam("node", "99");
        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("is not an objective"));
    }

    [Fact]
    public void Compile_ProducesDeterministicNodeSourceMap()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        var first = SC2MissionCompiler.Compile(doc);
        var second = SC2MissionCompiler.Compile(doc);

        Assert.Equal(first.Lua, second.Lua);
        Assert.Equal(first.SourceMap.Count, second.SourceMap.Count);
        Assert.NotEmpty(first.SourceMap);
        Assert.All(first.SourceMap, entry =>
        {
            Assert.True(entry.LineStart > 0);
            Assert.True(entry.LineEnd >= entry.LineStart);
            Assert.False(string.IsNullOrEmpty(entry.StageId));
        });
        Assert.Contains(first.SourceMap, entry => entry.StageId == "stage1" && entry.NodeId > 0);
    }

    [Fact]
    public void AutoLayout_IsDeterministicAndPreservesGraphSemantics()
    {
        var stage = new SC2MissionStage { Id = "stage1" };
        var timer = stage.AddNode(SC2MissionNodeCatalog.Timer, 999, -99);
        var victory = stage.AddNode(SC2MissionNodeCatalog.Victory, -50, 700);
        stage.Connect(0, "out", timer.Id);
        stage.Connect(timer.Id, "fired", victory.Id);
        var before = stage.Edges.Select(e => (e.FromNode, e.FromPort, e.ToNode, e.ToPort)).ToArray();

        SC2MissionGraphLayout.AutoLayout(stage);

        Assert.Equal(260f, timer.X);
        Assert.Equal(520f, victory.X);
        Assert.Equal(before, stage.Edges.Select(e => (e.FromNode, e.FromPort, e.ToNode, e.ToPort)).ToArray());
        Assert.True(timer.Y >= 0f);
        Assert.True(victory.Y >= 0f);
    }

    [Fact]
    public void MissionSchemaVersion_DefaultsAndClonesAsCurrent()
    {
        var doc = new SC2MissionDocument { SchemaVersion = 0 };
        var clone = doc.Clone();

        Assert.Equal(SC2MissionDocument.CurrentSchemaVersion, clone.SchemaVersion);
        Assert.Equal(SC2MissionDocument.CurrentSchemaVersion, new SC2MissionDocument().SchemaVersion);
    }

    [Fact]
    public void Dialogue_UsesTheQueueOnlyWithAVideo()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var talk = stage.AddNode(SC2MissionNodeCatalog.Dialogue, 0, 0);
        talk.SetParam("speaker", "HQ");
        talk.SetParam("text", "Hold on.");
        stage.Connect(0, "out", talk.Id);
        doc.Stages.Add(stage);

        var text = Lua(doc);
        Assert.Contains("table.insert(Sync.PrintText", text);
        Assert.Contains("text = \"HQ: Hold on.\"", text);
        Assert.DoesNotContain("ScenarioFramework.Dialogue(", text);

        talk.SetParam("video", "intro.sfd");
        talk.SetParam("bank", "SC2/VOC/");
        talk.SetParam("cue", "CUE_1");
        var video = Lua(doc);
        Assert.Contains("ScenarioFramework.Dialogue({ { speaker = \"HQ\"", video);
        Assert.Contains("vid = \"intro.sfd\"", video);
    }

    [Fact]
    public void Serializer_RoundTripsVariablesBasesAndSettings()
    {
        var document = new SC2MapDocument();
        document.Scenario.Mission = SC2MissionSamples.HoldTheLine();
        document.Scenario.Mission.Settings.TestStartStage = "stage1";
        document.Scenario.Mission.Settings.SharedCap = true;

        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(document)).Scenario.Mission;

        Assert.Equal(SC2MissionCompiler.Compile(document.Scenario.Mission).Lua, SC2MissionCompiler.Compile(restored).Lua);
        Assert.Equal("outposts_destroyed", restored.Variables.Single().Name);
        Assert.Equal("EnemyMain", restored.Bases.Single().Id);
        Assert.Equal(new[] { "RAID_1" }, restored.Bases[0].OpAIs[0].Chains.ToArray());
        Assert.False(restored.Bases[0].OpAIs[0].Enabled);
        Assert.True(restored.Settings.Coop);
        Assert.Equal(2, restored.Settings.HumanSlots);
        Assert.True(restored.Settings.SharedCap);
        Assert.Equal("stage1", restored.Settings.TestStartStage);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = SC2MissionSamples.HoldTheLine();
        var copy = original.Clone();

        copy.Bases[0].OpAIs[0].Chains.Add("X");
        copy.Variables[0].Name = "changed";
        copy.Settings.Coop = false;
        copy.Stages[0].Nodes[0].SetParam("anything", "1");

        Assert.Single(original.Bases[0].OpAIs[0].Chains);
        Assert.Equal("outposts_destroyed", original.Variables[0].Name);
        Assert.True(original.Settings.Coop);
        Assert.Equal("", original.Stages[0].Nodes[0].GetParam("anything"));
    }
}
