using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>The node library as a whole: every template is consistent with its parameters and compiles, and the nodes that carry a
/// campaign-framework call write it the way the game's own scripts do.</summary>
public class MissionNodeLibraryTests
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z0-9_]+)(?::([A-Za-z0-9_,]+))?\}");

    private static SC2MissionDocument OneNode(string type, params (string key, string value)[] set)
    {
        var doc = new SC2MissionDocument();
        doc.Bases.Add(new SC2MissionBase { Id = "Base1", Army = "ARMY_2", Marker = "M" });
        doc.Variables.Add(new SC2MissionVariable { Name = "count", Type = SC2MissionVariable.Number, Initial = "0" });
        var stage = new SC2MissionStage { Id = "stage1" };
        var node = stage.AddNode(type, 0, 0);
        foreach (var (key, value) in set) node.SetParam(key, value);
        stage.Connect(0, "out", node.Id);
        doc.Stages.Add(stage);
        return doc;
    }

    private static string Lua(SC2MissionDocument doc)
    {
        var result = SC2MissionCompiler.Compile(doc);
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error + "\n" + result.Lua);
        return result.Lua;
    }

    [Fact]
    public void TheLibraryHasWellOverAHundredNodesWithUniqueTypes()
    {
        Assert.True(SC2MissionNodeCatalog.All.Length >= 100, "nodes: " + SC2MissionNodeCatalog.All.Length);
        Assert.Equal(SC2MissionNodeCatalog.All.Length, SC2MissionNodeCatalog.All.Select(d => d.Type).Distinct().Count());
        Assert.All(SC2MissionNodeCatalog.All, d => Assert.False(string.IsNullOrWhiteSpace(d.Title + d.Category), d.Type));
    }

    [Fact]
    public void EveryChoiceParameterOffersChoices_AndItsDefaultIsOneOfThem()
    {
        foreach (var def in SC2MissionNodeCatalog.All)
            foreach (var p in def.Params.Where(p => p.Kind == SC2MissionParamKind.Choice))
            {
                Assert.True(p.Choices != null && p.Choices.Length > 0, def.Type + "." + p.Key + " has no choices");
                Assert.Contains(p.Default, p.Choices);
            }
    }

    [Fact]
    public void EveryPlaceholderOfEveryTemplateNamesARealParameterOrACallback()
    {
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            var templates = new List<string>();
            if (def.Template != null) templates.Add(def.Template);
            if (def.InputTemplates != null) templates.AddRange(def.InputTemplates.Values);
            if (def.Objective != null)
            {
                templates.Add(def.Objective.Target);
                if (def.Objective.Action != null) templates.Add(def.Objective.Action);
            }
            foreach (var template in templates)
                foreach (Match m in Placeholder.Matches(template))
                {
                    var name = m.Groups[1].Value;
                    var arg = m.Groups[2].Success ? m.Groups[2].Value : null;
                    if (arg == null && (name == "node" || name == "groupkey" || name == "input" || name == "loopunit")) continue;
                    if (name == "cb") { Assert.Contains(arg, SC2MissionNodeCatalog.PortsOf(def, null)); continue; }
                    var key = arg ?? name;
                    Assert.True(SC2MissionNodeCatalog.FindParam(def, key) != null, def.Type + ": " + m.Value + " has no parameter '" + key + "'");
                }
        }
    }

    [Fact]
    public void EveryChoiceThatIsMappedHasOneFragmentPerChoice()
    {
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            foreach (var p in def.Params.Where(p => p.Lua != null))
            {
                Assert.NotNull(p.Choices);
                Assert.Equal(p.Choices.Length, p.Lua.Length);
            }
            foreach (var template in new[] { def.Template ?? "" })
                foreach (Match m in Placeholder.Matches(template).Where(m => m.Groups[1].Value == "map"))
                    Assert.NotNull(SC2MissionNodeCatalog.FindParam(def, m.Groups[2].Value).Lua);
        }
    }

    [Fact]
    public void EveryNodeCompilesAndLeavesNoPlaceholderBehind()
    {
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            var lua = Lua(OneNode(def.Type));
            var left = Placeholder.Matches(lua).Cast<Match>().Where(m => !lua.Contains("{ " + m.Value)).Select(m => m.Value).ToList();
            Assert.True(left.Count == 0, def.Type + " left " + string.Join(" ", left));
            Assert.DoesNotContain("nil nil", lua);
        }
    }

    [Fact]
    public void ANodeThatWaitsCallsItsOutputsFromACallback_AndOneThatDoesNotCallsItItself()
    {
        foreach (var def in SC2MissionNodeCatalog.All.Where(d => d.Template != null))
        {
            if (def.Defers)
                foreach (var port in def.Ports)
                    Assert.Contains("{cb:" + port + "}", def.Template);
            else if (def.Ports.Contains("out"))
            {
                var lua = Lua(OneNode(def.Type));
                Assert.Contains("SCED_stage1_N1_out()", lua);
            }
        }
    }

    [Fact]
    public void EveryNodeHasWiresThatTheValidatorUnderstands()
    {
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            var doc = OneNode(def.Type);
            var issues = SC2MissionValidator.Validate(doc);
            Assert.DoesNotContain(issues, i => i.Message.StartsWith("Unknown node type") || i.Message.Contains("has no output"));
        }
    }

    [Fact]
    public void ACutsceneCameraMoveWritesTheCallTheCampaignScriptsUse()
    {
        var lua = Lua(OneNode("nis_zoom_to", ("marker", "CAM_1"), ("zoom", "-30"), ("seconds", "7")));

        Assert.Contains("NIS.ZoomTo(NISSupport.GetPos(\"CAM_1\"), NISSupport.GetOrient(\"CAM_1\"), -30, 7)", lua);
        Assert.Contains("ForkThread(function()", lua);
        Assert.Contains("SCED_stage1_N1_done()", lua);
    }

    [Fact]
    public void TheCameraMoveCanGoOnAtOnce()
    {
        Assert.Contains("NIS.ZoomToNoWait(", Lua(OneNode("nis_zoom_to", ("marker", "A"), ("wait", "go on at once"))));
    }

    [Fact]
    public void ANegativeNumberIsAllowedOnlyWhereTheNodeSaysSo()
    {
        var signed = SC2MissionValidator.Validate(OneNode("nis_zoom_to", ("marker", "A"), ("zoom", "-30")));
        Assert.DoesNotContain(signed, i => i.Message.Contains("Distance"));

        var unsigned = SC2MissionValidator.Validate(OneNode("nis_zoom_to", ("marker", "A"), ("seconds", "-1")));
        Assert.Contains(unsigned, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("zero or more"));
    }

    [Fact]
    public void OptionalParametersBecomeNilWhenEmpty()
    {
        var lua = Lua(OneNode("transport_deploy", ("army", "ARMY_2"), ("group", "TROOPS"), ("transport", "uct0001"), ("unload", "DROP")));

        Assert.Contains("approachChain = nil", lua);
        Assert.Contains("unloadDest = \"DROP\"", lua);
        Assert.Contains("returnDest = nil", lua);
        Assert.DoesNotContain(SC2MissionValidator.Validate(OneNode("transport_deploy", ("army", "ARMY_2"), ("group", "T"), ("transport", "t"), ("unload", "D"))),
            i => i.Message.Contains("Approach chain") || i.Message.Contains("Fly-away"));
    }

    [Fact]
    public void ListsAreWrittenAsLuaTables_AndAnEmptyListIsAnError()
    {
        var doc = OneNode("complete_research", ("army", "ARMY_1"), ("research", "CLA_JUMPJETS, CLA_SHIELDS"));
        Assert.Contains("GetArmyBrain(\"ARMY_1\"):CompleteResearch({ \"CLA_JUMPJETS\", \"CLA_SHIELDS\" })", Lua(doc));

        var empty = OneNode("complete_research", ("army", "ARMY_1"), ("research", " , "));
        Assert.Contains(SC2MissionValidator.Validate(empty), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("is empty"));
    }

    [Fact]
    public void ObjectiveNodesAreWrittenFromTheirSpec_AndFeedTheAssignedObjectives()
    {
        var lua = Lua(OneNode("objective_research", ("army", "ARMY_1"), ("research", "A,B"), ("count", "1")));

        Assert.Contains("SimObjectives.Research(\"primary\", 'incomplete'", lua);
        Assert.Contains("ResearchList = { \"A\", \"B\" }", lua);
        Assert.Contains("table.insert(ScenarioInfo.AssignedObjectives, objective)", lua);
        Assert.Contains("SCED_stage1_N1_completed()", lua);
        Assert.True(SC2MissionNodeCatalog.IsObjective("objective_research"));

        var killOrCapture = Lua(OneNode("objective_kill_or_capture", ("army", "ARMY_2"), ("group", "G")));
        Assert.Contains("SimObjectives.KillOrCapture(", killOrCapture);
        Assert.Contains("SCED_Group", killOrCapture.Substring(0, killOrCapture.IndexOf("SimObjectives.KillOrCapture(", System.StringComparison.Ordinal)) + "SCED_Group");
    }

    [Fact]
    public void TheCameraObjectiveTakesTheMarkerPositions()
    {
        var lua = Lua(OneNode("objective_camera", ("markers", "PAN_1, PAN_2")));

        Assert.Contains("SimObjectives.Camera(\"primary\", 'incomplete'", lua);
        Assert.Contains("{ ScenarioUtils.MarkerToPosition(\"PAN_1\"), ScenarioUtils.MarkerToPosition(\"PAN_2\") }", lua);
    }

    [Fact]
    public void ChoicesMapToTheFrameworkCallsOfTheGame()
    {
        Assert.Contains("ScenarioFramework.ForceGroupDeath(group)", Lua(OneNode("group_state", ("army", "A"), ("group", "G"), ("action", "kill"))));
        Assert.Contains("ScenarioFramework.UnProtectUnit(unit)", Lua(OneNode("unit_state", ("army", "A"), ("unit", "U"), ("action", "unprotect"))));
        Assert.Contains("IssueAggressiveMove(group, ScenarioUtils.MarkerToPosition(\"M\"))", Lua(OneNode("group_move_marker", ("army", "A"), ("group", "G"), ("marker", "M"), ("command", "attack-move"))));
        Assert.Contains("ScenarioFramework.PlatoonAttackChain(platoon, \"C\")", Lua(OneNode("platoon_order", ("army", "A"), ("group", "G"), ("order", "attack"), ("chain", "C"))));
        Assert.Contains("NIS.FadeInWhite(2)", Lua(OneNode("nis_fade", ("mode", "in from white"))));
    }

    [Fact]
    public void StatisticTriggersUseTheFrameworkTableFormat()
    {
        var lua = Lua(OneNode("army_stat", ("army", "ARMY_1"), ("stat", "Units_Killed"), ("compare", "GreaterThanOrEqual"), ("value", "5"), ("categories", "categories.NAVAL")));

        Assert.Contains("ScenarioFramework.CreateArmyStatTrigger(SCED_stage1_N1_reached, GetArmyBrain(\"ARMY_1\"), 'SCED_' .. \"stage1/N1\", {{StatType = \"Units_Killed\", CompareType = \"GreaterThanOrEqual\", Value = 5, Category = categories.NAVAL}})", lua);
    }

    [Fact]
    public void BaseAndOpAINodesNeedTheirBaseToExist()
    {
        var doc = OneNode("opai_target", ("base", "Missing"), ("opai", "X"), ("target", "ARMY_1"));

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("Base 'Missing' does not exist"));
    }

    [Fact]
    public void TheSimpleCutsceneUsesTheGamesNisSystemSoTheEndGivesTheGameBack()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var begin = stage.AddNode(SC2MissionNodeCatalog.CutsceneStart, 0, 0);
        var move = stage.AddNode(SC2MissionNodeCatalog.CameraMove, 0, 0);
        move.SetParam("marker", "CAM");
        var end = stage.AddNode(SC2MissionNodeCatalog.CutsceneEnd, 0, 0);
        stage.Connect(0, "out", begin.Id);
        stage.Connect(begin.Id, "out", move.Id);
        stage.Connect(move.Id, "done", end.Id);
        doc.Stages.Add(stage);

        var lua = Lua(doc);

        Assert.Contains("NIS.StartNIS_Standard(nil, true, true)", lua);
        Assert.Contains("NIS.MoveTo(\"CAM\", 3)", lua);
        Assert.Contains("NIS.EndNIS_Standard(nil, false, 2)", lua);
        Assert.DoesNotContain("ScenarioCinematics", lua);
        Assert.DoesNotContain(SC2MissionValidator.Validate(doc), i => i.Message.Contains("no cutscene end node can be reached"));
    }

    [Fact]
    public void ACutsceneThatIsBegunAndNeverEndedIsReported()
    {
        var doc = new SC2MissionDocument();
        var stage = new SC2MissionStage { Id = "stage1" };
        var begin = stage.AddNode("nis_begin_standard", 0, 0);
        stage.Connect(0, "out", begin.Id);
        doc.Stages.Add(stage);

        Assert.Contains(SC2MissionValidator.Validate(doc), i => i.NodeId == begin.Id && i.Message.Contains("no cutscene end node can be reached"));
    }

    [Fact]
    public void AskingForTheDifficultyUsesTheSimDialogueAndBuildsTheBasesAfterTheAnswer()
    {
        var doc = OneNode("ask_difficulty", ("variable", "count"), ("timeout", "30"), ("default", "hard"));
        doc.Bases.Add(new SC2MissionBase { Id = "Outpost", Army = "ARMY_2", Marker = "M" });

        var lua = Lua(doc);

        Assert.Contains("import('/lua/sim/SimDialogue.lua').Create(\"Choose the difficulty\", { \"Easy\", \"Normal\", \"Hard\" }, \"center\")", lua);
        Assert.Contains("ScenarioInfo.Options.Difficulty = level", lua);
        Assert.Contains("ScenarioInfo.SCED.vars[\"count\"] = level", lua);
        Assert.Contains("info.presser ~= player", lua);
        Assert.Contains("choose(3)", lua);
        Assert.Contains("WaitSeconds(30)", lua);
        // the bases wait for the answer instead of being built on the loading screen
        Assert.Contains("function SCED_Setup()\n\t-- the bases are built when the player has chosen the difficulty\nend", lua);
        Assert.Matches(@"SCED_BuildBases\(\)\n\t+SCED_\w+_chosen\(\)\n", lua);
    }

    [Fact]
    public void WithoutTheDifficultyQuestionTheBasesAreBuiltOnTheLoadingScreen()
    {
        var doc = OneNode("timer", ("seconds", "5"));
        doc.Bases.Add(new SC2MissionBase { Id = "Outpost", Army = "ARMY_2", Marker = "M" });

        Assert.Contains("function SCED_Setup()\n\tSCED_BuildBases()\nend", Lua(doc));
    }

    [Fact]
    public void TheScriptImportsWhatTheTemplatesUse()
    {
        var lua = Lua(OneNode("nis_title", ("text", "Hello")));

        foreach (var import in new[] { "local NIS = import(", "local NISSupport = import(", "local ScenarioGameEvents = import(", "local TransportUtils = import(", "local ScenarioPlatoonAI = import(" })
            Assert.Contains(import, lua);
    }
}
