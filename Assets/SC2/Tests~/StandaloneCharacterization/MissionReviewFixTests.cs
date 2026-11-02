using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>What the review of the mission editor found: the cutscene check follows wires, and a graph that only has settings is not empty.</summary>
public class MissionReviewFixTests
{
    private const string NoEnd = "no cutscene end node can be reached";

    private static SC2MissionNode Add(SC2MissionStage stage, string type, params (string key, string value)[] set)
    {
        var node = stage.AddNode(type, 0, 0);
        foreach (var (key, value) in set) node.SetParam(key, value);
        return node;
    }

    // ------------------------------------------------------------------ cutscene ends are found along the wires

    [Fact]
    public void AnEndInAnotherStageCountsWhenAdvanceToStageLeadsThere()
    {
        var doc = new SC2MissionDocument();
        var first = new SC2MissionStage { Id = "intro" };
        var second = new SC2MissionStage { Id = "stage1" };
        var begin = Add(first, SC2MissionNodeCatalog.CutsceneStart);
        var advance = Add(first, SC2MissionNodeCatalog.AdvanceStage, ("stage", "stage1"));
        var end = Add(second, SC2MissionNodeCatalog.CutsceneEnd);
        first.Connect(0, "out", begin.Id);
        first.Connect(begin.Id, "out", advance.Id);
        second.Connect(0, "out", end.Id);
        doc.Stages.Add(first);
        doc.Stages.Add(second);

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains(NoEnd));
    }

    [Fact]
    public void AnEndThatTheCutsceneCannotReachDoesNotSilenceTheWarning()
    {
        var doc = new SC2MissionDocument();
        var first = new SC2MissionStage { Id = "intro" };
        var other = new SC2MissionStage { Id = "stage1" };
        var begin = Add(first, SC2MissionNodeCatalog.CutsceneStart);
        first.Connect(0, "out", begin.Id);
        // an end exists in the mission, but nothing leads from the cutscene to it
        var end = Add(other, SC2MissionNodeCatalog.CutsceneEnd);
        other.Connect(0, "out", end.Id);
        var stray = Add(first, SC2MissionNodeCatalog.CutsceneEnd);
        doc.Stages.Add(first);
        doc.Stages.Add(other);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.NodeId == begin.Id && i.StageId == "intro" && i.Message.Contains(NoEnd));
        Assert.NotEqual(0, stray.Id);
    }

    [Fact]
    public void AnEndBehindABranchOrALoopOfWiresIsFound()
    {
        var doc = new SC2MissionDocument();
        doc.Variables.Add(new SC2MissionVariable { Name = "n", Type = SC2MissionVariable.Number, Initial = "0" });
        var stage = new SC2MissionStage { Id = "stage1" };
        var begin = Add(stage, SC2MissionNodeCatalog.CutsceneStart);
        var branch = Add(stage, SC2MissionNodeCatalog.Branch, ("variable", "n"));
        var again = Add(stage, SC2MissionNodeCatalog.SetVariable, ("variable", "n"));
        var timer = Add(stage, SC2MissionNodeCatalog.Timer, ("seconds", "1"));
        var end = Add(stage, SC2MissionNodeCatalog.CutsceneEnd);
        stage.Connect(0, "out", begin.Id);
        stage.Connect(begin.Id, "out", branch.Id);
        stage.Connect(branch.Id, "false", again.Id);
        stage.Connect(again.Id, "out", timer.Id);
        stage.Connect(timer.Id, "fired", branch.Id);
        stage.Connect(branch.Id, "true", end.Id);
        doc.Stages.Add(stage);

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains(NoEnd));
    }

    // ------------------------------------------------------------------ a graph with only settings is a mission

    [Fact]
    public void AGraphWithNothingEnteredIsEmpty()
    {
        Assert.True(new SC2MissionDocument().IsEmpty);
        Assert.True(new SC2MissionDocument { Enabled = true }.IsEmpty);
    }

    [Fact]
    public void ArmyTuningSettingsOrATitleAloneMakeTheGraphNotEmpty()
    {
        Assert.False(new SC2MissionDocument { Armies = { new SC2MissionArmy { Name = "ARMY_1", Alias = "PLAYER" } } }.IsEmpty);
        Assert.False(new SC2MissionDocument { Settings = { Coop = true, HumanSlots = 2 } }.IsEmpty);
        Assert.False(new SC2MissionDocument { Settings = { TestStartStage = "stage1" } }.IsEmpty);
        Assert.False(new SC2MissionDocument { Title = "Test" }.IsEmpty);
    }

    [Fact]
    public void ASettingsOnlyGraphIsCheckedAndCompilesToTheCampaignScript()
    {
        var doc = new SC2MissionDocument { Enabled = true, Settings = { Coop = true, HumanSlots = 9 } };

        var issues = SC2MissionValidator.Validate(doc);

        Assert.DoesNotContain(issues, i => i.Message == "The mission is empty.");
        Assert.Contains(issues, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("Human slots"));

        doc.Settings.HumanSlots = 2;
        var result = SC2MissionCompiler.Compile(doc);
        Assert.False(result.HasErrors);
        Assert.Contains("doscript '/lua/ai/campaign/system/CAIInitializeSystems.lua'", result.Lua);
        Assert.Contains("function OnPopulate()", result.Lua);
    }
}
