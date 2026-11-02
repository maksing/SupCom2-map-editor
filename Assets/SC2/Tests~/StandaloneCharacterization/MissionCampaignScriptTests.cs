using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>The generated script is a whole campaign-style _script.lua: the army block, the tuning tables, the campaign AI load and the
/// campaign entry points, as the mission that runs from the normal map list has them.</summary>
public class MissionCampaignScriptTests
{
    private static SC2MissionMapInfo Map(params string[] armies) => new() { ArmyOrder = armies.ToList(), PlayableArea = "AREA_1" };

    private static string Script(SC2MissionDocument doc, SC2MissionMapInfo map = null)
    {
        var result = SC2MissionCompiler.Compile(doc, map);
        Assert.True(SC2LuaSyntaxCheck.TryCompile(result.Lua, out var error), error + "\n" + result.Lua);
        return result.Lua;
    }

    [Fact]
    public void EveryMissionOpensWithTheArmyBlock_EvenWithoutBases()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), Map("ARMY_1", "ARMY_2"));

        Assert.Contains("ScenarioInfo.ARMY_PLAYER = 1\n", lua);
        Assert.Contains("ScenarioInfo.ARMY_ENEM01 = 2\n", lua);
        Assert.Contains("ScenarioInfo.AssignedObjectives = {}\n", lua);
        Assert.Contains("color_ENEM01 = {'FF00A2BE','FFFFFFFF','FF00A2BE',}\n", lua);
        Assert.Contains("TUNING_PLAYER = {unitCap = 1500,mass = 2500,energy = 6500,research = 10,}\n", lua);
        Assert.Contains("TUNING_ENEM01 = {color = color_ENEM01,unitCap = 1000,massBoost = 9999999,energyBoost = 9999999,}\n", lua);
        Assert.Contains("local ARMY_PLAYER = ScenarioInfo.ARMY_PLAYER\n", lua);
        Assert.Contains("local ARMY_ENEM01 = ScenarioInfo.ARMY_ENEM01\n", lua);
        Assert.Contains("LOG('*AI DEBUG: Initializing campaign')\n", lua);
        Assert.Contains("doscript '/lua/ai/campaign/system/CAIInitializeSystems.lua'\n", lua);
    }

    [Fact]
    public void TheArmyBlockComesBeforeTheEntryPointsThatUseIt()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), Map("ARMY_1", "ARMY_2"));

        var block = lua.IndexOf("ScenarioInfo.ARMY_PLAYER = 1", System.StringComparison.Ordinal);
        var doscript = lua.IndexOf("doscript '/lua/ai/campaign/system/CAIInitializeSystems.lua'", System.StringComparison.Ordinal);
        var populate = lua.IndexOf("function OnPopulate()", System.StringComparison.Ordinal);
        Assert.True(block >= 0 && block < doscript && doscript < populate);
    }

    [Fact]
    public void EntryPointsFollowTheCampaignFlow()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), Map("ARMY_1", "ARMY_2"));

        Assert.Contains("ScenarioGameSetup.CAMPAIGN_OnPopulate(camData)", lua);
        Assert.Contains("ScenarioGameSetup.SetupPlayerArmy(\tARMY_PLAYER, TUNING_PLAYER)", lua);
        Assert.Contains("ScenarioGameSetup.SetupNonPlayerArmy(\tARMY_ENEM01, TUNING_ENEM01)", lua);
        Assert.Contains("ScenarioGameSetup.CAMPAIGN_OnStart()", lua);
        Assert.Contains("funcSetup\t\t= SCED_Setup", lua);
        Assert.Contains("funcMain\t\t= SCED_Begin", lua);
        Assert.Contains("areaStart\t\t= 'AREA_1'", lua);
        Assert.Contains("SetPlayableArea('AREA_1')", lua);
        Assert.Contains("ScenarioGameSetup.CAMPAIGN_OnInitCamera(data)", lua);
    }

    [Fact]
    public void NoPlayableAreaMeansNoAreaStartLine()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), new SC2MissionMapInfo { ArmyOrder = new List<string> { "ARMY_1", "ARMY_2" } });

        Assert.DoesNotContain("areaStart", lua);
        Assert.DoesNotContain("SetPlayableArea", lua);
    }

    [Fact]
    public void ArmyIndicesFollowTheOrderOfTheMapsArmies()
    {
        var doc = SC2MissionSamples.HoldTheLine();

        var lua = Script(doc, Map("ARMY_1", "ARMY_2", "ARMY_3", "ARMY_4"));

        // two human slots, then two enemies with their own palette
        Assert.Contains("ScenarioInfo.ARMY_PLAYER = 1\nScenarioInfo.ARMY_PLAYER2 = 2\nScenarioInfo.ARMY_ENEM01 = 3\nScenarioInfo.ARMY_ENEM02 = 4\n", lua);
        Assert.Contains("SetupPlayerArmy(\tARMY_PLAYER2, TUNING_PLAYER2)", lua);
        Assert.Contains("color_ENEM02 = {'FF950000','FFFF7E00','FF950000',}", lua);
    }

    [Fact]
    public void AGenericArmyOnlyGetsAColourAndACap()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_3", Alias = "CIVL01", Role = SC2MissionArmy.RoleGeneric });

        var lua = Script(doc, Map("ARMY_1", "ARMY_2", "ARMY_3"));

        Assert.Contains("ScenarioInfo.ARMY_CIVL01 = 3\n", lua);
        Assert.Contains("ScenarioGameSetup.SetupGenericArmy(\tARMY_CIVL01, color_CIVL01)", lua);
        Assert.DoesNotContain("TUNING_CIVL01", lua);
    }

    [Fact]
    public void ChangedTuningGoesIntoTheTables()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_1", Alias = "PLAYER", Role = SC2MissionArmy.RolePlayer, UnitCap = 800, Mass = 100, Energy = 200, Research = 3 });
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_2", Alias = "BOSS", Role = SC2MissionArmy.RoleEnemy, UnitCap = 2000, MassBoost = 5, EnergyBoost = 6, Colors = new[] { "FF111111", "FF222222", "FF333333" } });

        var lua = Script(doc, Map("ARMY_1", "ARMY_2"));

        Assert.Contains("TUNING_PLAYER = {unitCap = 800,mass = 100,energy = 200,research = 3,}", lua);
        Assert.Contains("color_BOSS = {'FF111111','FF222222','FF333333',}", lua);
        Assert.Contains("TUNING_BOSS = {color = color_BOSS,unitCap = 2000,massBoost = 5,energyBoost = 6,}", lua);
        Assert.Contains("ScenarioInfo.ARMY_BOSS = 2\n", lua);
    }

    [Fact]
    public void EnemyArmiesHaveTheirResearchUnlocked_LikeTermesis()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), Map("ARMY_1", "ARMY_2", "ARMY_3"));

        Assert.Contains("\tif ARMY_ENEM01 then UnlockAllResearch(ARMY_ENEM01) end\n\tif ARMY_ENEM02 then UnlockAllResearch(ARMY_ENEM02) end\n", lua);
        Assert.DoesNotContain("UnlockAllResearch(ARMY_PLAYER)", lua);
        // after the army tuning, inside OnPopulate
        Assert.True(lua.IndexOf("SetupNonPlayerArmy(\tARMY_ENEM02", System.StringComparison.Ordinal) < lua.IndexOf("UnlockAllResearch(ARMY_ENEM01)", System.StringComparison.Ordinal));
    }

    [Fact]
    public void AnArmyOfAnEmptyLobbySlotIsSkippedInsteadOfCrashingTheGame()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Settings.Coop = true;
        doc.Settings.HumanSlots = 2;
        doc.Bases.Add(new SC2MissionBase { Id = "Outpost", Army = "ARMY_3", Marker = "M" });
        var lua = Script(doc, Map("ARMY_1", "ARMY_2", "ARMY_3"));

        // the fixed indices stay for a full lobby, and are looked up by army name when the game is set up
        Assert.Contains("ScenarioInfo.ARMY_PLAYER2 = 2\n", lua);
        Assert.Contains("local function SCED_ResolveArmies()", lua);
        Assert.Contains("ScenarioInfo.ARMY_PLAYER2 = present['ARMY_2']", lua);
        Assert.Contains("function OnPopulate()\n\tSCED_ResolveArmies()\n", lua);
        // an army that is not there is not set up
        Assert.Contains("\tif ARMY_PLAYER2 then\n\t\tScenarioGameSetup.SetupPlayerArmy(\tARMY_PLAYER2, TUNING_PLAYER2)\n\tend\n", lua);
        Assert.Contains("\tif ARMY_ENEM01 then\n\t\tScenarioGameSetup.SetupNonPlayerArmy(\tARMY_ENEM01, TUNING_ENEM01)\n\tend\n", lua);
        // the co-op watcher and the bases check the army first
        Assert.Contains("local brain = SCED_HasArmy(name) and GetArmyBrain(name)", lua);
        Assert.Contains("if not SCED_HasArmy(\"ARMY_3\") then", lua);
    }

    [Fact]
    public void ResearchUnlockCanBeSwitchedOffPerArmy_AndSurvivesSaving()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_2", Alias = "ENEM01", Role = SC2MissionArmy.RoleEnemy, UnlockResearch = false });

        Assert.DoesNotContain("UnlockAllResearch", Script(doc, Map("ARMY_1", "ARMY_2")));

        var document = new SC2MapDocument();
        document.Scenario.Mission = doc;
        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(document)).Scenario.Mission;
        Assert.False(restored.Armies.Single().UnlockResearch);
        Assert.True(new SC2MissionArmy().UnlockResearch);
    }

    [Fact]
    public void ObjectivesAreAddedToTheAssignedObjectiveList()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), Map("ARMY_1", "ARMY_2"));

        Assert.Contains("table.insert(ScenarioInfo.AssignedObjectives, objective)", lua);
    }

    [Fact]
    public void Validator_CatchesArmyMistakes()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_2", Alias = "PLAYER", Colors = new[] { "FF00A2BE", "zz", "FF00A2BE" }, UnitCap = 0 });
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_9", Alias = "GHOST" });

        var messages = SC2MissionValidator.Validate(doc, Map("ARMY_1", "ARMY_2")).Select(i => i.Message).ToList();

        Assert.Contains(messages, m => m.Contains("script name 'PLAYER' is used by another army"));
        Assert.Contains(messages, m => m.Contains("colours are eight hex digits"));
        Assert.Contains(messages, m => m.Contains("unit cap must be at least 1"));
        Assert.Contains(messages, m => m.Contains("ARMY_9 has mission settings but is not on the map"));
    }

    [Fact]
    public void Serializer_RoundTripsTheArmies()
    {
        var document = new SC2MapDocument();
        document.Scenario.Mission = SC2MissionSamples.DestroyTheOutpost();
        document.Scenario.Mission.Armies.Add(new SC2MissionArmy
        {
            Name = "ARMY_2", Alias = "BOSS", Role = SC2MissionArmy.RoleEnemy, UnitCap = 2500, MassBoost = 123456789012, EnergyBoost = 7,
            Colors = new[] { "FF010203", "FF040506", "FF070809" }, Mass = 11, Energy = 22, Research = 33,
        });

        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(document)).Scenario.Mission;

        var army = restored.Armies.Single();
        Assert.Equal("BOSS", army.Alias);
        Assert.Equal(2500, army.UnitCap);
        Assert.Equal(123456789012, army.MassBoost);
        Assert.Equal(new[] { "FF010203", "FF040506", "FF070809" }, army.Colors);
        Assert.Equal(33, army.Research);
    }

    [Fact]
    public void Clone_CopiesTheArmies()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost();
        doc.Armies.Add(new SC2MissionArmy { Name = "ARMY_2", Alias = "BOSS" });

        var copy = doc.Clone();
        copy.Armies[0].Alias = "CHANGED";
        copy.Armies[0].Colors[0] = "FF000000";

        Assert.Equal("BOSS", doc.Armies[0].Alias);
        Assert.Equal("FF00A2BE", doc.Armies[0].Colors[0]);
    }

    private static SC2MissionMapInfo TwoArmyMap() => new()
    {
        ArmyOrder = new List<string> { "ARMY_1", "ARMY_2" },
        PlayableArea = "AREA_1",
        Armies = new HashSet<string> { "ARMY_1", "ARMY_2" },
        Areas = new HashSet<string> { "AREA_1" },
        Markers = new HashSet<string> { "ARMY_1", "ARMY_2", "BASE_SPOT", "CAMERA_SPOT" },
        Chains = new HashSet<string> { "PATROL_A", "EnemyMain_EngineerChain" },
        Groups = new Dictionary<string, HashSet<string>> { ["ARMY_2"] = new HashSet<string> { "WAVE", "FORT" } },
        Units = new Dictionary<string, HashSet<string>>(),
    };

    [Fact]
    public void TheSamplesFitTheArmiesGroupsMarkersAndChainsOfTheMap()
    {
        var map = TwoArmyMap();

        foreach (var sample in new[] { SC2MissionSamples.DestroyTheOutpost(map), SC2MissionSamples.HoldTheLine(map) })
        {
            var issues = SC2MissionValidator.Validate(sample, map);
            Assert.DoesNotContain(issues, i => i.Message.Contains("the map has no") || i.Message.Contains("has no group") || i.Message.Contains("has no army"));
            Assert.False(SC2MissionValidator.HasErrors(issues), string.Join("\n", issues));
        }

        var outpost = SC2MissionSamples.DestroyTheOutpost(map);
        Assert.Equal("ARMY_2", outpost.Stages[0].Nodes[0].GetParam("army"));
        Assert.Equal("FORT", outpost.Stages[0].Nodes[0].GetParam("group"));
    }

    private static SC2MissionMapInfo MapWithUnitCounts()
    {
        var map = TwoArmyMap();
        map.Chains.Add("Base1_EngineerChain");
        map.GroupUnitCounts = new Dictionary<string, Dictionary<string, int>> { ["ARMY_2"] = new Dictionary<string, int> { ["WAVE"] = 3, ["FORT"] = 0 } };
        map.ArmyUnitCounts = new Dictionary<string, int> { ["ARMY_2"] = 3 };
        return map;
    }

    [Fact]
    public void AnEmptyGroupIsAnErrorWhereANodeUsesIt()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost("ARMY_1", "ARMY_2", "FORT");

        var issues = SC2MissionValidator.Validate(doc, MapWithUnitCounts());

        Assert.Contains(issues, i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("'FORT' of ARMY_2 has no units"));
        Assert.True(SC2MissionCompiler.Compile(doc, MapWithUnitCounts()).HasErrors);

        var fine = SC2MissionValidator.Validate(SC2MissionSamples.DestroyTheOutpost("ARMY_1", "ARMY_2", "WAVE"), MapWithUnitCounts());
        Assert.DoesNotContain(fine, i => i.Message.Contains("has no units: the game"));
    }

    [Fact]
    public void AnEmptyBuildGroupOfABaseIsAnError()
    {
        var doc = new SC2MissionDocument();
        var b = new SC2MissionBase { Id = "Base1", Army = "ARMY_2", Marker = "BASE_SPOT" };
        b.BuildGroups.Add(new SC2MissionBuildGroup { Group = "FORT" });
        doc.Bases.Add(b);

        Assert.Contains(SC2MissionValidator.Validate(doc, MapWithUnitCounts()), i => i.Severity == SC2MissionSeverity.Error && i.Message.Contains("'FORT' of ARMY_2 has no units"));
    }

    [Fact]
    public void AnArmyWithoutUnitsIsReported_AndTheOpAIThatCannotRun()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost("ARMY_1", "ARMY_2", "WAVE");
        var b = new SC2MissionBase { Id = "Base1", Army = "ARMY_2", Marker = "BASE_SPOT", StartMode = SC2MissionBase.StartEmpty };
        b.OpAIs.Add(new SC2MissionOpAI { Name = "Wave1", Type = "LandAttackCybran" });
        doc.Bases.Add(b);

        var messages = SC2MissionValidator.Validate(doc, MapWithUnitCounts()).Select(i => i.Message).ToList();

        Assert.Contains(messages, m => m.Contains("Army ARMY_1 has no units on the map") && m.Contains("without a commander"));
        Assert.Contains(messages, m => m.Contains("has OpAI, but its target ARMY_1 has no units"));
        Assert.DoesNotContain(messages, m => m.Contains("Army ARMY_2 has no units"));
    }

    [Fact]
    public void ASpawnGroupForTheArmyCountsAsGivingItUnits()
    {
        var doc = SC2MissionSamples.DestroyTheOutpost("ARMY_1", "ARMY_2", "WAVE");
        var spawn = doc.Stages[0].AddNode(SC2MissionNodeCatalog.SpawnGroup, 0, 0);
        spawn.SetParam("army", "ARMY_1");
        spawn.SetParam("group", "WAVE");

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc, MapWithUnitCounts()), i => i.Message.Contains("Army ARMY_1 has no units"));
    }

    [Fact]
    public void ThePlayerWithoutUnitsIsNotReportedWhenTheCountsAreUnknown()
    {
        Assert.DoesNotContain(SC2MissionValidator.Validate(SC2MissionSamples.DestroyTheOutpost(), TwoArmyMap()), i => i.Message.Contains("has no units on the map"));
    }

    [Fact]
    public void TheScriptReportsArmiesWithoutUnitsInTheGameLog()
    {
        var doc = SC2MissionSamples.HoldTheLine();

        var lua = Script(doc, Map("ARMY_1", "ARMY_2", "ARMY_3"));

        Assert.Contains("local SCED_ArmyNames = { 'PLAYER', 'PLAYER2', 'ENEM01', }", lua);
        Assert.Contains("WARN('SCED: army ', index, ' (', name, ') has no units", lua);
        Assert.Contains("the OpAI of the bases is built only against army 1, which has no units", lua);
        Assert.Contains("\tSCED_ReportArmies()\n\tForkThread(function() WaitSeconds(10) SCED_ReportArmies() end)\n", lua);
    }

    [Fact]
    public void SpawnGroupLogsAnEmptyOrMissingGroupInsteadOfBreaking()
    {
        var lua = Script(SC2MissionSamples.DestroyTheOutpost(), Map("ARMY_1", "ARMY_2"));

        Assert.Contains("local ok, group = pcall(ScenarioUtils.CreateArmyGroup, \"ARMY_2\", \"OUTPOST\")", lua);
        Assert.Contains("if ok and group and table.getn(group) > 0 then", lua);
        Assert.Contains("LOG('SCED: spawned ', table.getn(group), ' units of ARMY_2/OUTPOST')", lua);
        Assert.Contains("WARN('SCED: the group ARMY_2/OUTPOST created no units: ', ok and 'it is empty' or group)", lua);
    }

    [Fact]
    public void EveryBaseNeedsItsEngineerChainOnTheMap()
    {
        var doc = SC2MissionSamples.HoldTheLine();
        var map = TwoArmyMap();
        map.Chains.Remove("EnemyMain_EngineerChain");

        var missing = SC2MissionValidator.Validate(doc, map).Where(i => i.Message.Contains("EnemyMain_EngineerChain")).ToList();

        Assert.Single(missing);
        Assert.Equal(SC2MissionSeverity.Error, missing[0].Severity);
        Assert.True(SC2MissionCompiler.Compile(doc, map).HasErrors);

        map.Chains.Add("EnemyMain_EngineerChain");
        Assert.DoesNotContain(SC2MissionValidator.Validate(doc, map), i => i.Message.Contains("EngineerChain"));
    }

    [Fact]
    public void AnExpansionBaseThatStartsEmptyNeedsNoEngineerChain_LikeTermesis()
    {
        // Termesis: every main base (StartNonZeroBase) has <name>_EngineerChain on the map, every expansion (StartEmptyBase) has none.
        var doc = new SC2MissionDocument();
        foreach (var name in new[] { "ArmyBase_UEF01_Main_Base", "ArmyBase_CYB01_Main_Base" })
            doc.Bases.Add(new SC2MissionBase { Id = name, Army = "ARMY_2", Marker = "M", StartMode = SC2MissionBase.StartFilled });
        foreach (var name in new[] { "ArmyBase_UEF01_Expansion_Base_01", "P2_CYB01_EXP1_Base_100" })
            doc.Bases.Add(new SC2MissionBase { Id = name, Army = "ARMY_2", Marker = "M", StartMode = SC2MissionBase.StartEmpty });
        var map = TwoArmyMap();
        map.Chains = new HashSet<string> { "ArmyBase_UEF01_Main_Base_EngineerChain", "ArmyBase_CYB01_Main_Base_EngineerChain" };

        Assert.DoesNotContain(SC2MissionValidator.Validate(doc, map), i => i.Message.Contains("EngineerChain"));

        map.Chains.Remove("ArmyBase_CYB01_Main_Base_EngineerChain");
        var missing = SC2MissionValidator.Validate(doc, map).Where(i => i.Message.Contains("EngineerChain")).ToList();
        Assert.Single(missing);
        Assert.Contains("ArmyBase_CYB01_Main_Base_EngineerChain", missing[0].Message);
    }

    [Fact]
    public void ReproducesTheArmyBlockOfTermesis()
    {
        // The six armies of the Termesis mission, in the order its _scenario.lua lists them, with the names its script gives them.
        var doc = SC2MissionSamples.DestroyTheOutpost();
        var names = new[] { "ARMY_PLAYER", "ARMY_UEF01", "ARMY_CYB01", "ARMY_ILLUM01", "ARMY_UEF02", "ARMY_ALIEN01" };
        var aliases = new[] { "PLAYER", "UEF01", "CYB01", "ILLUM01", "UEF02", "ALIEN01" };
        for (var i = 0; i < names.Length; i++)
            doc.Armies.Add(new SC2MissionArmy { Name = names[i], Alias = aliases[i], Role = i == 0 ? SC2MissionArmy.RolePlayer : SC2MissionArmy.RoleEnemy, UnitCap = i == 0 ? 1500 : 1000 });

        var lua = Script(doc, Map(names));

        Assert.Contains("ScenarioInfo.ARMY_PLAYER = 1\nScenarioInfo.ARMY_UEF01 = 2\nScenarioInfo.ARMY_CYB01 = 3\nScenarioInfo.ARMY_ILLUM01 = 4\nScenarioInfo.ARMY_UEF02 = 5\nScenarioInfo.ARMY_ALIEN01 = 6\n\nScenarioInfo.AssignedObjectives = {}\n", lua);
        Assert.Contains("local ARMY_PLAYER = ScenarioInfo.ARMY_PLAYER\nlocal ARMY_UEF01 = ScenarioInfo.ARMY_UEF01\nlocal ARMY_CYB01 = ScenarioInfo.ARMY_CYB01\nlocal ARMY_ILLUM01 = ScenarioInfo.ARMY_ILLUM01\nlocal ARMY_UEF02 = ScenarioInfo.ARMY_UEF02\nlocal ARMY_ALIEN01 = ScenarioInfo.ARMY_ALIEN01\n", lua);
        Assert.Contains("TUNING_PLAYER = {unitCap = 1500,mass = 2500,energy = 6500,research = 10,}", lua);
        Assert.Contains("TUNING_UEF01 = {color = color_UEF01,unitCap = 1000,massBoost = 9999999,energyBoost = 9999999,}", lua);
        Assert.Contains("ScenarioGameSetup.SetupNonPlayerArmy(\tARMY_ALIEN01, TUNING_ALIEN01)", lua);
    }

    [Fact]
    public void TheEngineerChainCheckIsSkippedWhenTheMapIsNotKnown()
    {
        Assert.DoesNotContain(SC2MissionValidator.Validate(SC2MissionSamples.HoldTheLine()), i => i.Message.Contains("EngineerChain"));
    }

    [Fact]
    public void TheCoopSampleIsSoloOnAMapWithTwoArmies_AndCoopWithThree()
    {
        var two = SC2MissionSamples.HoldTheLine(TwoArmyMap());
        Assert.False(two.Settings.Coop);
        Assert.Equal(1, two.Settings.HumanSlots);
        Assert.Equal("ARMY_2", two.Bases[0].Army);
        Assert.Equal("BASE_SPOT", two.Bases[0].Marker);
        Assert.Equal("PATROL_A", two.Bases[0].OpAIs[0].Chains[0]);

        var map = TwoArmyMap();
        map.ArmyOrder.Add("ARMY_3");
        var three = SC2MissionSamples.HoldTheLine(map);
        Assert.True(three.Settings.Coop);
        Assert.Equal(2, three.Settings.HumanSlots);
        Assert.Equal("ARMY_3", three.Bases[0].Army);
    }
}
