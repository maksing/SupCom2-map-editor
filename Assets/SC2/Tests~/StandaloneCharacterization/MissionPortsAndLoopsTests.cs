using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>Nodes with several named inputs and a number of outputs that depends on a parameter, and the loop nodes built on them.</summary>
public class MissionPortsAndLoopsTests
{
    private static (SC2MissionDocument doc, SC2MissionStage stage) NewStage()
    {
        var doc = new SC2MissionDocument();
        doc.Variables.Add(new SC2MissionVariable { Name = "count", Type = SC2MissionVariable.Number, Initial = "0" });
        var stage = new SC2MissionStage { Id = "stage1" };
        doc.Stages.Add(stage);
        return (doc, stage);
    }

    private static string Lua(SC2MissionDocument doc)
    {
        var result = SC2MissionCompiler.Compile(doc);
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error + "\n" + result.Lua);
        return result.Lua;
    }

    private static SC2MissionNode Add(SC2MissionStage stage, string type, params (string key, string value)[] set)
    {
        var node = stage.AddNode(type, 0, 0);
        foreach (var (key, value) in set) node.SetParam(key, value);
        return node;
    }

    // ------------------------------------------------------------------ inputs

    [Fact]
    public void AWireRemembersTheInputItEndsAt_AndSurvivesSaving()
    {
        var document = new SC2MapDocument();
        var (doc, stage) = NewStage();
        var join = Add(stage, "join", ("count", "3"));
        stage.Connect(0, "out", join.Id, "2");
        document.Scenario.Mission = doc;

        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(document)).Scenario.Mission;

        Assert.Equal("2", restored.Stages[0].Edges.Single().ToPort);
    }

    [Fact]
    public void ANodeWithoutAnInputNameUsesItsFirstInput()
    {
        var (doc, stage) = NewStage();
        var loop = Add(stage, "loop_repeat");
        var plain = Add(stage, "set_variable", ("variable", "count"));

        Assert.Equal("start", stage.Connect(0, "out", loop.Id).ToPort);
        Assert.Equal("in", stage.Connect(0, "out", plain.Id).ToPort);
    }

    [Fact]
    public void EveryInputGetsAFunction_AndAWireCallsTheOneItEndsAt()
    {
        var (doc, stage) = NewStage();
        var join = Add(stage, "join", ("count", "3"));
        stage.Connect(0, "out", join.Id, "1");
        stage.Connect(0, "out", join.Id, "3");

        var lua = Lua(doc);

        Assert.Contains("function SCED_stage1_N1_in_1()", lua);
        Assert.Contains("function SCED_stage1_N1_in_2()", lua);
        Assert.Contains("function SCED_stage1_N1_in_3()", lua);
        Assert.DoesNotContain("SCED_stage1_N1_in_4", lua);
        Assert.Contains("function SCED_stage1_Start()\n\tScenarioInfo.SCED.stage = \"stage1\"\n\tSCED_stage1_N1_in_1()\n\tSCED_stage1_N1_in_3()\n", lua);
        // the join fires once every input has been reached, not after any three arrivals
        Assert.Contains("if not join.seen[\"3\"] then", lua);
        Assert.Contains("if join.n == 3 then", lua);
    }

    [Fact]
    public void AWireToAnInputTheNodeDoesNotHaveIsAnError()
    {
        var (doc, stage) = NewStage();
        var join = Add(stage, "join", ("count", "2"));
        stage.Connect(0, "out", join.Id, "5");

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("has no input '5'"));
    }

    // ------------------------------------------------------------------ outputs that depend on a parameter

    [Fact]
    public void ASequenceHasAsManyOutputsAsItsCount_AndRunsThemInOrder()
    {
        var (doc, stage) = NewStage();
        var sequence = Add(stage, "sequence", ("count", "4"));
        stage.Connect(0, "out", sequence.Id);

        var lua = Lua(doc);

        Assert.Equal(new[] { "1", "2", "3", "4" }, SC2MissionNodeCatalog.PortsOf(SC2MissionNodeCatalog.Find("sequence"), sequence));
        Assert.Contains("function SCED_stage1_N1()\n\tSCED_stage1_N1_1()\n\tSCED_stage1_N1_2()\n\tSCED_stage1_N1_3()\n\tSCED_stage1_N1_4()\nend", lua);
    }

    [Fact]
    public void ASwitchHasAnOutputForEachValueAndOneForTheRest()
    {
        var (doc, stage) = NewStage();
        var switchNode = Add(stage, "switch_variable", ("variable", "count"), ("cases", "1,5,9"));
        stage.Connect(0, "out", switchNode.Id);

        var lua = Lua(doc);

        Assert.Equal(new[] { "1", "2", "3", "other" }, SC2MissionNodeCatalog.PortsOf(SC2MissionNodeCatalog.Find("switch_variable"), switchNode));
        Assert.Contains("if value == \"1\" then\n\t\tSCED_stage1_N1_1()", lua);
        Assert.Contains("elseif value == \"9\" then\n\t\tSCED_stage1_N1_3()", lua);
        Assert.Contains("else\n\t\tSCED_stage1_N1_other()", lua);
    }

    [Fact]
    public void AWireFromAnOutputThatNoLongerExistsIsAnError()
    {
        var (doc, stage) = NewStage();
        var sequence = Add(stage, "sequence", ("count", "4"));
        var after = Add(stage, "set_variable", ("variable", "count"));
        stage.Connect(0, "out", sequence.Id);
        stage.Connect(sequence.Id, "4", after.Id);
        sequence.SetParam("count", "2");

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("has no output '4'"));
    }

    [Fact]
    public void AGateLetsTheFlowThroughOnlyWhileItIsOpen()
    {
        var (doc, stage) = NewStage();
        var gate = Add(stage, "gate", ("open", "false"));
        stage.Connect(0, "out", gate.Id, "pass");
        stage.Connect(0, "out", gate.Id, "open");

        var lua = Lua(doc);

        Assert.Equal(new[] { "pass", "open", "close" }, SC2MissionNodeCatalog.InputsOf(SC2MissionNodeCatalog.Find("gate"), gate));
        Assert.Contains("function SCED_stage1_N1_in_open()\n\tScenarioInfo.SCED.gates = ScenarioInfo.SCED.gates or {}\n\tScenarioInfo.SCED.gates[\"stage1/N1\"] = true\nend", lua);
        Assert.Contains("if open then\n\t\tSCED_stage1_N1_out()\n\telse\n\t\tSCED_stage1_N1_blocked()", lua);
        // opening and closing change the state only; they do not let anything through
        Assert.DoesNotContain("in_open()\n\tScenarioInfo.SCED.gates = ScenarioInfo.SCED.gates or {}\n\tScenarioInfo.SCED.gates[\"stage1/N1\"] = true\n\tSCED_stage1_N1_out()", lua);
    }

    [Fact]
    public void ACounterCountsAndResets()
    {
        var (doc, stage) = NewStage();
        var counter = Add(stage, "counter", ("target", "3"), ("variable", "count"));
        stage.Connect(0, "out", counter.Id, "count");
        stage.Connect(0, "out", counter.Id, "reset");

        var lua = Lua(doc);

        Assert.Contains("if value == 3 then", lua);
        Assert.Contains("function SCED_stage1_N1_in_reset()\n\tScenarioInfo.SCED.counters = ScenarioInfo.SCED.counters or {}\n\tScenarioInfo.SCED.counters[\"stage1/N1\"] = 0", lua);
    }

    // ------------------------------------------------------------------ loops

    private static (SC2MissionDocument doc, SC2MissionStage stage, SC2MissionNode loop, SC2MissionNode body) RepeatWithBody(string auto, bool wireNext)
    {
        var (doc, stage) = NewStage();
        var loop = Add(stage, "loop_repeat", ("count", "4"), ("auto", auto), ("variable", "count"));
        var body = Add(stage, "set_variable", ("variable", "count"));
        stage.Connect(0, "out", loop.Id);
        stage.Connect(loop.Id, "body", body.Id);
        if (wireNext) stage.Connect(body.Id, "out", loop.Id, "next");
        return (doc, stage, loop, body);
    }

    [Fact]
    public void ARepeatLoopRunsItsBodyRoundByRound_AndTheBodyWiresBackToNext()
    {
        var (doc, stage, loop, body) = RepeatWithBody("false", wireNext: true);

        var lua = Lua(doc);

        Assert.Contains("function SCED_LoopRound(state)", lua);
        Assert.Contains("function SCED_stage1_N1_in_start()", lua);
        Assert.Contains("function SCED_stage1_N1_in_next()", lua);
        Assert.Contains("function SCED_stage1_N1_in_break()", lua);
        Assert.Contains("count = 4", lua);
        Assert.Contains("ScenarioInfo.SCED.vars[\"count\"] = s.i", lua);
        // the end of the body leads to the 'next' input, the loop's own start is called from the stage start
        Assert.Contains("function SCED_stage1_N2_out()\n\tSCED_stage1_N1_in_next()\nend", lua);
        Assert.Contains("function SCED_stage1_Start()\n\tScenarioInfo.SCED.stage = \"stage1\"\n\tSCED_stage1_N1_in_start()\n", lua);
        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains("next"));
        // the loop goes through itself, which is fine: it does not call itself in the same breath
        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains("call each other in a loop"));
    }

    [Fact]
    public void ALoopThatWaitsForNextButHasNoWireToItIsReported()
    {
        var (doc, stage, loop, body) = RepeatWithBody("false", wireNext: false);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.NodeId == loop.Id && i.Message.Contains("Nothing is wired to 'next'"));
    }

    [Fact]
    public void ALoopThatMovesItselfIgnoresNext_AndSaysSo()
    {
        var (doc, stage, loop, body) = RepeatWithBody("true", wireNext: true);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.NodeId == loop.Id && i.Message.Contains("are not used"));
    }

    [Fact]
    public void ALoopWithNothingInItsBodyIsReported()
    {
        var (doc, stage) = NewStage();
        var loop = Add(stage, "loop_repeat", ("auto", "true"));
        stage.Connect(0, "out", loop.Id);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.NodeId == loop.Id && i.Message.Contains("Nothing is wired to 'body'"));
    }

    [Fact]
    public void ACycleWithNoLoopNodeAndNoWaitIsStillAnError()
    {
        var (doc, stage) = NewStage();
        var a = Add(stage, "set_variable", ("variable", "count"));
        var b = Add(stage, "set_variable", ("variable", "count"));
        stage.Connect(0, "out", a.Id);
        stage.Connect(a.Id, "out", b.Id);
        stage.Connect(b.Id, "out", a.Id);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("call each other in a loop"));
    }

    [Fact]
    public void AWhileLoopComparesAVariableEveryRound_AndNeverSpinsFasterThanTheEngine()
    {
        var (doc, stage) = NewStage();
        var loop = Add(stage, "loop_while", ("variable", "count"), ("compare", "<"), ("value", "5"), ("auto", "true"), ("pause", "0"));
        var body = Add(stage, "set_variable", ("variable", "count"), ("op", "add"), ("value", "1"));
        stage.Connect(0, "out", loop.Id);
        stage.Connect(loop.Id, "body", body.Id);

        var lua = Lua(doc);

        Assert.Contains("cond = function() return ScenarioInfo.SCED.vars[\"count\"] < 5 end", lua);
        Assert.Contains("pause = math.max(0, 0.1)", lua);
        Assert.Contains("limit = 1000", lua);
    }

    [Fact]
    public void AForeverLoopCanBePausedResumedAndStopped()
    {
        var (doc, stage) = NewStage();
        var loop = Add(stage, "loop_forever", ("seconds", "2"));
        var tick = Add(stage, "set_variable", ("variable", "count"), ("op", "add"), ("value", "1"));
        stage.Connect(0, "out", loop.Id, "start");
        stage.Connect(0, "out", loop.Id, "stop");
        stage.Connect(loop.Id, "tick", tick.Id);

        var lua = Lua(doc);

        Assert.Equal(new[] { "start", "stop", "pause", "resume" }, SC2MissionNodeCatalog.InputsOf(SC2MissionNodeCatalog.Find("loop_forever"), loop));
        Assert.Contains("function SCED_stage1_N1_in_pause()\n\tlocal state = ScenarioInfo.SCED.loops[\"stage1/N1\"]\n\tif state then state.paused = true end", lua);
        Assert.Contains("function SCED_stage1_N1_in_stop()\n\tSCED_LoopStop(ScenarioInfo.SCED.loops[\"stage1/N1\"])", lua);
        Assert.Contains("pause = math.max(2, 0.1), auto = true, delayFirst = not false", lua);
    }

    [Fact]
    public void AForEachLoopGivesItsBodyTheCurrentUnit()
    {
        var (doc, stage) = NewStage();
        var each = Add(stage, "loop_foreach", ("army", "ARMY_2"), ("group", "OUTPOST"), ("auto", "false"));
        var kill = Add(stage, "loop_unit_kill", ("loop", each.Id.ToString()));
        stage.Connect(0, "out", each.Id);
        stage.Connect(each.Id, "each", kill.Id);
        stage.Connect(kill.Id, "out", each.Id, "next");

        var lua = Lua(doc);

        Assert.Contains("local group = SCED_Group(\"ARMY_2\", \"OUTPOST\")", lua);
        Assert.Contains("ScenarioInfo.SCED.loopUnit[\"stage1/N1\"] = s.items[s.i]", lua);
        Assert.Contains("local unit = ScenarioInfo.SCED.loopUnit[\"stage1/N1\"]\n\tif unit and not unit:IsDead() then\n\t\tunit:Kill()", lua);
        Assert.DoesNotContain(SC2MissionValidator.Validate(doc).Where(i => i.Severity == SC2MissionSeverity.Error), i => true);
    }

    [Fact]
    public void ALoopUnitNodeMustNameAForEachLoopOfItsStage()
    {
        var (doc, stage) = NewStage();
        var other = Add(stage, "set_variable", ("variable", "count"));
        var kill = Add(stage, "loop_unit_kill", ("loop", other.Id.ToString()));
        stage.Connect(0, "out", kill.Id);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.NodeId == kill.Id && i.Message.Contains("For each unit"));
    }

    [Fact]
    public void TheThreeWavesSampleHasNoErrorsAndRunsThreeRounds()
    {
        var doc = SC2MissionSamples.ThreeWaves();

        var lua = Lua(doc);

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error);
        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains("next"));
        Assert.Contains("count = 3, pause = 2, auto = false", lua);
        Assert.Contains("function SCED_stage1_N2_done()\n\tSCED_stage1_N1_in_next()\nend", lua);
    }

    [Fact]
    public void TheLoopEngineIsOnlyInTheScriptsThatUseLoops()
    {
        var (doc, stage) = NewStage();
        var plain = Add(stage, "set_variable", ("variable", "count"));
        stage.Connect(0, "out", plain.Id);

        Assert.DoesNotContain("SCED_LoopRound", Lua(doc));
    }
}
