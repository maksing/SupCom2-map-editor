using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>The mission graph: its Lua compiler, validator, hash markers, undo command and the way it is saved with the map.</summary>
public class MissionGraphTests
{
    [Fact]
    public void Sample_CompilesToLuaThatTheCompilerAccepts()
    {
        var result = SC2MissionCompiler.Compile(SC2MissionSamples.DestroyTheOutpost());

        Assert.False(result.HasErrors, string.Join("\n", result.Issues));
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error);
        Assert.Contains("SCED_stage1_Start", result.Lua);
        Assert.Contains("SimObjectives.Kill(\"primary\"", result.Lua);
        Assert.Contains("ScenarioGameCleanup.Victory()", result.Lua);
    }

    [Fact]
    public void EveryNodeTypeCompilesToValidLua()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1", Name = "Stage 1" };
        doc.Stages.Add(stage);
        var previous = SC2MissionStage.StartNodeId;
        var previousPort = SC2MissionStage.StartPort;
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            var node = stage.AddNode(def.Type, 0, 0);
            node.SetParam("army", "ARMY_2");
            node.SetParam("group", "G1");
            node.SetParam("unit", "UNIT_1");
            node.SetParam("area", "AREA_1");
            node.SetParam("stage", "stage1");
            node.SetParam("code", "LOG('raw')");
            stage.Connect(previous, previousPort, node.Id);
            if (def.Ports.Length > 0) { previous = node.Id; previousPort = def.Ports[0]; }
            else { previous = SC2MissionStage.StartNodeId; previousPort = SC2MissionStage.StartPort; }
        }

        var result = SC2MissionCompiler.Compile(doc);

        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error);
    }

    [Fact]
    public void TextWithQuotesAndNewlinesCannotBreakOutOfTheLuaString()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        var objective = doc.Stages[0].Nodes.First(n => n.Type == SC2MissionNodeCatalog.ObjectiveKill);
        objective.SetParam("title", "Say \"hi\"\nthen \\ leave");
        objective.SetParam("description", "it's] ]] -- done");

        var result = SC2MissionCompiler.Compile(doc);

        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error);
    }

    [Fact]
    public void RawLuaWithASyntaxErrorIsReportedByTheSyntaxCheck()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        stage.AddNode(SC2MissionNodeCatalog.RawLua, 0, 0).SetParam("code", "if then");
        stage.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, 1);
        doc.Stages.Add(stage);

        var result = SC2MissionCompiler.Compile(doc);

        Assert.False(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Validator_AcceptsTheSample()
    {
        var issues = SC2MissionValidator.Validate(SC2MissionSamples.DestroyTheOutpost());

        Assert.Empty(issues);
    }

    [Fact]
    public void Validator_ReportsMissingParametersAndUnknownStages()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Stages[0].Nodes.First(n => n.Type == SC2MissionNodeCatalog.SpawnGroup).SetParam("group", "");
        doc.Stages[0].Nodes.First(n => n.Type == SC2MissionNodeCatalog.AdvanceStage).SetParam("stage", "nowhere");

        var issues = SC2MissionValidator.Validate(doc);

        Assert.Contains(issues, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("Group is not set"));
        Assert.Contains(issues, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("'nowhere'"));
        Assert.True(SC2MissionCompiler.Compile(doc).HasErrors);
    }

    [Fact]
    public void Validator_RejectsACategoriesExpressionThatCouldInjectLua()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var node = stage.AddNode(SC2MissionNodeCatalog.AreaEntered, 0, 0);
        node.SetParam("area", "AREA_1");
        node.SetParam("categories", "categories.ALLUNITS) os.exit() --");
        stage.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, node.Id);
        doc.Stages.Add(stage);

        var issues = SC2MissionValidator.Validate(doc);

        Assert.Contains(issues, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("categories expression"));
    }

    [Fact]
    public void Validator_FindsALoopOfNodesThatNeverWait_ButAllowsOneThroughATimer()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var a = stage.AddNode(SC2MissionNodeCatalog.RawLua, 0, 0);
        var b = stage.AddNode(SC2MissionNodeCatalog.RawLua, 0, 0);
        stage.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, a.Id);
        stage.Connect(a.Id, "out", b.Id);
        stage.Connect(b.Id, "out", a.Id);
        doc.Stages.Add(stage);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("loop"));

        var timer = stage.AddNode(SC2MissionNodeCatalog.Timer, 0, 0);
        stage.Edges.RemoveAll(e => e.FromNode == b.Id);
        stage.Connect(b.Id, "out", timer.Id);
        stage.Connect(timer.Id, "fired", a.Id);

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains("loop"));
    }

    [Fact]
    public void Validator_WarnsAboutUnreachableNodesAndAMissionThatCannotEnd()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var wired = stage.AddNode(SC2MissionNodeCatalog.RawLua, 0, 0);
        var stray = stage.AddNode(SC2MissionNodeCatalog.RawLua, 0, 0);
        stage.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, wired.Id);
        doc.Stages.Add(stage);

        var issues = SC2MissionValidator.Validate(doc);

        Assert.Contains(issues, i => i.NodeId == stray.Id && i.Severity == SC2MissionSeverity.Warning);
        Assert.DoesNotContain(issues, i => i.NodeId == wired.Id);
        Assert.Contains(issues, i => i.Message.Contains("Victory or Defeat"));
        Assert.False(SC2MissionValidator.HasErrors(issues));
    }

    [Fact]
    public void Markers_DetectARegionEditedByHand()
    {
        var lua = SC2MissionCompiler.Compile(SC2MissionSamples.DestroyTheOutpost()).Lua;
        Assert.All(SC2MissionLuaMarkers.Parse(lua), r => Assert.True(r.Intact, r.Id));

        var edited = lua.Replace("ScenarioGameCleanup.Victory()", "ScenarioGameCleanup.Victory() LOG('hand')");
        var regions = SC2MissionLuaMarkers.Parse(edited);

        Assert.Equal(new[] { "common", "armies", "stage1", "stage2", "campaign" }, regions.Select(r => r.Id).ToArray());
        Assert.False(regions.Single(r => r.Id == "stage2").Intact);
        Assert.True(regions.Single(r => r.Id == "stage1").Intact);
    }

    [Fact]
    public void Markers_IgnoreTheLineEndingStyle()
    {
        var lua = SC2MissionCompiler.Compile(SC2MissionSamples.DestroyTheOutpost()).Lua;

        var regions = SC2MissionLuaMarkers.Parse(lua.Replace("\n", "\r\n"));

        Assert.All(regions, r => Assert.True(r.Intact, r.Id));
    }

    [Fact]
    public void Serializer_RoundTripsTheMissionGraph()
    {
        var doc = new SC2MapDocument();
        doc.Scenario.Mission = SC2MissionSamples.DestroyTheOutpost();
        doc.Scenario.Mission.Enabled = true;

        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        Assert.True(restored.Scenario.Mission.Enabled);
        Assert.Equal(SC2MissionCompiler.Compile(doc.Scenario.Mission).Lua, SC2MissionCompiler.Compile(restored.Scenario.Mission).Lua);
        Assert.Equal(doc.Scenario.Mission.Stages[0].NextNodeId, restored.Scenario.Mission.Stages[0].NextNodeId);
    }

    [Fact]
    public void Serializer_ReadsASaveWithoutAMissionAsAnEmptyDisabledGraph()
    {
        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(new SC2MapDocument()));

        Assert.False(restored.Scenario.Mission.Enabled);
        Assert.True(restored.Scenario.Mission.IsEmpty);
    }

    [Fact]
    public void ReplaceCommand_SwapsTheGraphAndUndoesIt()
    {
        var document = new SC2MapDocument();
        var command = new SC2ReplaceMissionCommand("Add sample", SC2MissionSamples.DestroyTheOutpost());

        var change = command.Do(document);
        Assert.Equal(2, document.Scenario.Mission.Stages.Count);
        Assert.Equal(SC2DomainId.Scenario, change.ChangedDomains.Single());

        command.Undo(document);
        Assert.True(document.Scenario.Mission.IsEmpty);
    }
}
