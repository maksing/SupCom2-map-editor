using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The nodes that are a few lines of Lua around calls of the game's own framework, found by reading how every campaign mission of the
    /// game (SC2_CA_*, the D1 demos, the tutorials) and the framework files they import use that framework. Each has a template that the
    /// compiler fills in from the node's parameters, so adding a node is a table entry here and nothing else:
    ///   {name}            the parameter, written as a Lua value of its kind (a string, a number, true/false, a categories expression)
    ///   {raw:name}        the parameter's text as it is (a choice value, a categories expression)
    ///   {opt:name}        the parameter as a string, or nil when it is empty
    ///   {map:name}        the Lua fragment that belongs to the chosen value of a choice parameter (SC2MissionParamDef.Lua)
    ///   {brain:name}      GetArmyBrain("ARMY_n") of an army parameter
    ///   {armyindex:name}  the engine's index of an army parameter
    ///   {pos:name}        the position of a marker parameter
    ///   {list:name}       a list of strings from a comma-separated parameter
    ///   {brains:name}     a list of army brains from a comma-separated list of armies
    ///   {positions:name}  a list of positions from a comma-separated list of markers
    ///   {groupkey}        "army/group" of the node's army and group parameters
    ///   {node}            "stage/N3": a name that is unique to the node
    ///   {cb:port}         the function the engine calls for an output of the node
    /// A node that does not wait and has an "out" output calls it after its template; one that waits calls its outputs from its callbacks.
    /// </summary>
    public static class SC2MissionExtraNodes
    {
        // ------------------------------------------------------------------ parameter shortcuts

        private static SC2MissionParamDef P(string key, string label, SC2MissionParamKind kind, string def = "", string[] choices = null, string[] lua = null, bool signed = false) =>
            new() { Key = key, Label = label, Kind = kind, Default = def, Choices = choices, Lua = lua, Signed = signed };

        private static SC2MissionParamDef Army(string key = "army", string label = "Army", string def = "") => P(key, label, SC2MissionParamKind.Army, def);
        private static SC2MissionParamDef Group(string key = "group", string label = "Group") => P(key, label, SC2MissionParamKind.Group);
        private static SC2MissionParamDef Unit(string key = "unit", string label = "Unit") => P(key, label, SC2MissionParamKind.Unit);
        private static SC2MissionParamDef Marker(string key = "marker", string label = "Marker") => P(key, label, SC2MissionParamKind.Marker);
        private static SC2MissionParamDef Chain(string key = "chain", string label = "Chain") => P(key, label, SC2MissionParamKind.Chain);
        private static SC2MissionParamDef Area(string key = "area", string label = "Area") => P(key, label, SC2MissionParamKind.Area);
        private static SC2MissionParamDef Num(string key, string label, string def, bool signed = false) => P(key, label, SC2MissionParamKind.Number, def, signed: signed);
        private static SC2MissionParamDef Flag(string key, string label, bool def) => P(key, label, SC2MissionParamKind.Bool, def ? "true" : "false");
        private static SC2MissionParamDef Text(string key, string label, string def = "") => P(key, label, SC2MissionParamKind.Text, def);
        private static SC2MissionParamDef Cats(string key = "categories", string label = "Unit categories", string def = "categories.ALLUNITS") => P(key, label, SC2MissionParamKind.Categories, def);
        private static SC2MissionParamDef Pick(string key, string label, string def, string[] choices, string[] lua = null) => P(key, label, SC2MissionParamKind.Choice, def, choices, lua);
        private static SC2MissionParamDef Var(string key = "variable", string label = "Store in variable") => P(key, label, SC2MissionParamKind.Variable);
        private static SC2MissionParamDef BaseRef(string key = "base") => P(key, key == "base" ? "Base" : "Expansion base", SC2MissionParamKind.Base);
        private static SC2MissionParamDef Opt(SC2MissionParamDef p) { p.Optional = true; return p; }

        // Kept here rather than read from the catalog: two classes whose static fields read each other are initialised in an order that
        // differs between runtimes, and a field read too early is null.
        /// <summary>The node that asks player 1 for the difficulty; a mission that has one builds its bases after the answer.</summary>
        public const string AskDifficultyType = "ask_difficulty";

        private static readonly string[] Out = { "out" };
        private static readonly string[] AreaCompare = { ">=", "<=", ">", "<", "==" };
        private static readonly string[] ObjectiveResults = { "completed", "failed" };
        private static readonly string[] ObjectiveKinds = { "primary", "secondary" };

        // ------------------------------------------------------------------ node constructors

        private static SC2MissionNodeDef Event(string type, string title, string port, string template, params SC2MissionParamDef[] ps) =>
            new() { Type = type, Title = title, Category = "Events", Defers = true, Ports = new[] { port }, Params = ps, Template = template };

        private static SC2MissionNodeDef Act(string type, string title, string category, string template, params SC2MissionParamDef[] ps) =>
            new() { Type = type, Title = title, Category = category, Ports = Out, Params = ps, Template = template };

        /// <summary>A step of a cutscene: it can wait (a camera move, a dialogue), so it runs in a thread of its own and calls its "done" output
        /// when it has finished. Wiring a step to two others runs both at once, the way the campaign scripts fork a camera move beside a dialogue.</summary>
        // Kept here rather than read from the catalog: static fields of two classes that read each other are initialised in an order that
        // differs between runtimes.
        private static readonly string[] CompareOperators = { "==", "~=", ">", ">=", "<", "<=" };

        private static readonly string[] LoopInputs = { "start", "next", "break" };

        private const string LoopBreakLua = "SCED_LoopStop(ScenarioInfo.SCED.loops[{node}])";

        /// <summary>What the 'next' and 'break' inputs of the loops that count rounds do.</summary>
        private static readonly Dictionary<string, string> LoopControls = new()
        {
            ["next"] = "local state = ScenarioInfo.SCED.loops[{node}]\nif state and state.active and not state.auto then SCED_LoopRound(state) end",
            ["break"] = LoopBreakLua,
        };

        /// <summary>The Lua of a loop's 'start' input: a new state, and the first round. fields are the loop-specific fields of the state, pre runs
        /// before it, enter runs at the start of every round, done runs when the rounds are over. The variable of the round number is the
        /// parameter named by variableParam (null: no counter).</summary>
        private static string LoopStart(string fields, string pre, string enter, string done, string variableParam = "variable")
        {
            var variable = variableParam == null ? "" : variableParam == "variable" ? "{opt:variable}" : "{opt:" + variableParam + "}";
            var counter = variable.Length == 0 ? "" : "\tif " + variable + " then ScenarioInfo.SCED.vars[" + variable + "] = s.i end\n";
            return pre +
                "local previous = ScenarioInfo.SCED.loops[{node}]\n" +
                "if previous then previous.active = false end\n" +
                "local state = { active = true, i = 0, paused = false, token = (previous and previous.token or 0) + 1, " + fields + " }\n" +
                "state.enter = function(s)\n" + counter + "\t" + enter + "\nend\n" +
                "state.done = function() " + done + " end\n" +
                "state.stopped = function() {cb:stopped}() end\n" +
                "ScenarioInfo.SCED.loops[{node}] = state\n" +
                "SCED_LoopRound(state)";
        }

        private static SC2MissionParamDef LoopParam() => Num("loop", "Loop node number (the # of the 'For each unit' node)", "");

        /// <summary>A node that works on the unit the 'For each unit in a group' loop is at.</summary>
        private static SC2MissionNodeDef LoopAct(string type, string title, string statement, params SC2MissionParamDef[] ps)
        {
            var all = new List<SC2MissionParamDef> { LoopParam() };
            all.AddRange(ps);
            return new SC2MissionNodeDef
            {
                Type = type, Title = title, Category = "Loops", Ports = Out, Params = all.ToArray(),
                Template = "local unit = {loopunit}\nif unit and not unit:IsDead() then\n\t" + statement + "\nend",
            };
        }

        private static SC2MissionNodeDef Nis(string type, string title, string body, params SC2MissionParamDef[] ps) =>
            new()
            {
                Type = type, Title = title, Category = "Cutscene (NIS)", Defers = true, Ports = new[] { "done" }, Params = ps,
                Template = "ForkThread(function()\n\t" + body.Replace("\n", "\n\t") + "\n\t{cb:done}()\nend)",
            };

        private static SC2MissionNodeDef NisNow(string type, string title, string body, params SC2MissionParamDef[] ps) =>
            Act(type, title, "Cutscene (NIS)", body, ps);

        private static SC2MissionNodeDef Objective(string type, string title, string defaultTitle, string function, string action, string target, bool needsGroup, params SC2MissionParamDef[] ps)
        {
            var all = new List<SC2MissionParamDef>
            {
                Pick("kind", "Kind", "primary", ObjectiveKinds),
                Text("title", "Title", defaultTitle),
                Text("description", "Description"),
            };
            all.AddRange(ps);
            return new SC2MissionNodeDef
            {
                Type = type, Title = title, Category = "Objectives", Defers = true, Ports = ObjectiveResults, Params = all.ToArray(),
                Objective = new SC2MissionObjectiveSpec { Function = function, Action = action, Target = target, NeedsGroup = needsGroup },
            };
        }

        // lookups shared by the templates
        private const string GroupLookup = "local group = SCED_Group({army}, {group})\nif group then\n";
        private const string PlatoonLookup = "local platoon = SCED_Platoon({army}, {group})\nif platoon then\n";
        private const string UnitLookup = "local unit = SCED_FindUnit({army}, {unit})\nif unit then\n";
        private const string UnitMissing = "else\n\tWARN('SCED: unit ' .. {army} .. '/' .. {unit} .. ' does not exist')\nend";
        private const string TargetLookup = "local target = SCED_FindUnit({targetarmy}, {targetunit})\n";

        private static readonly string[] ObjectiveIcons = { "kill", "capture", "build", "protect", "timer", "move", "reclaim", "repair", "locate", "group" };
        private static readonly string[] UnitStateChoices = { "protect", "unprotect", "restrict-death", "allow-death", "kill", "destroy" };

        public static readonly SC2MissionNodeDef[] Nodes =
        {
            // ================================================================ events
            Event("unit_damaged", "Unit damaged", "hit",
                UnitLookup + "\tScenarioFramework.CreateUnitDamagedTrigger({cb:hit}, unit, {damage}, nil, {once})\n" + UnitMissing,
                Army(), Unit(), Num("damage", "Damage at least", "0"), Flag("once", "Only once", true)),
            Event("unit_health", "Unit health", "reached",
                UnitLookup + "\tScenarioFramework.CreateUnitHealthRatioTrigger({cb:reached}, unit, {ratio}, {below}, {once})\n" + UnitMissing,
                Army(), Unit(), Num("ratio", "Health ratio (0 to 1)", "0.5"), Flag("below", "Fires when it falls below (off: rises above)", true), Flag("once", "Only once", true)),
            Event("unit_at_marker", "Unit reached marker", "arrived",
                UnitLookup + "\tScenarioFramework.CreateUnitToMarkerDistanceTrigger({cb:arrived}, unit, {marker}, {distance})\n" + UnitMissing,
                Army(), Unit(), Marker(), Num("distance", "Within (distance)", "20")),
            Event("unit_near_type", "Unit near enemy units", "near",
                UnitLookup + "\tScenarioFramework.CreateUnitNearTypeTrigger({cb:near}, unit, {brain:other}, {categories}, {distance})\n" + UnitMissing,
                Army(), Unit(), Army("other", "Army whose units count"), Cats(), Num("distance", "Within (distance)", "30")),
            Event("unit_killed_unit", "Unit made a kill", "kills",
                UnitLookup + "\tScenarioFramework.CreateUnitKilledUnitTrigger({cb:kills}, unit)\n" + UnitMissing,
                Army(), Unit()),
            Event("unit_built", "Unit built something", "built",
                UnitLookup + "\tScenarioFramework.CreateUnitBuiltTrigger({cb:built}, unit, {categories})\n" + UnitMissing,
                Army(), Unit("unit", "Builder"), Cats("categories", "Built unit categories")),
            Event("unit_captured", "Unit captured", "captured",
                UnitLookup + "\tScenarioFramework.CreateUnitCapturedTrigger({cb:captured}, function() end, unit)\n" + UnitMissing,
                Army(), Unit()),
            Event("unit_reclaimed", "Unit reclaimed", "reclaimed",
                UnitLookup + "\tScenarioFramework.CreateUnitReclaimedTrigger({cb:reclaimed}, unit)\n" + UnitMissing,
                Army(), Unit()),
            Event("unit_selected", "Unit selected by the player", "selected",
                UnitLookup + "\tScenarioFramework.CreateUnitSelectedTrigger({cb:selected}, unit)\n" + UnitMissing,
                Army(), Unit()),
            Event("unit_veteran", "Unit gained a veterancy level", "veteran",
                UnitLookup + "\tScenarioFramework.CreateUnitVeterancyTrigger({cb:veteran}, unit)\n" + UnitMissing,
                Army(), Unit()),
            Event("unit_dying", "Unit is about to die (controlled death)", "dying",
                UnitLookup + "\tScenarioGameEvents.SetupUnitForControlledDeath(unit, {cb:dying})\n" + UnitMissing,
                Army(), Unit()),
            Event("army_veteran", "Army unit gained a veterancy level", "veteran",
                "ScenarioFramework.CreateArmyUnitCategoryVeterancyTrigger({cb:veteran}, {brain:army}, {categories}, {level})",
                Army(), Cats(), Num("level", "Level", "1")),
            Event("army_stat", "Army statistic", "reached",
                "ScenarioFramework.CreateArmyStatTrigger({cb:reached}, {brain:army}, 'SCED_' .. {node}, {{StatType = {stat}, CompareType = {compare}, Value = {value}, Category = {categories}}})",
                Army(),
                Pick("stat", "Statistic", "Units_Active", new[]
                {
                    "Units_Active", "Units_Killed", "Units_History", "Units_BeingBuilt", "Enemies_Killed",
                    "Economy_Stored_Mass", "Economy_Stored_Energy", "Economy_Income_Mass", "Economy_Income_Energy",
                    "Economy_TotalProduced_Mass", "Economy_TotalProduced_Energy", "Economy_TotalConsumed_Mass", "Economy_TotalConsumed_Energy",
                    "Economy_Research_Learned_Count", "Economy_Research_Units_Unlocked",
                }),
                Pick("compare", "Compare", "GreaterThanOrEqual", new[] { "GreaterThan", "GreaterThanOrEqual", "LessThan", "LessThanOrEqual" }),
                Num("value", "Value", "1"),
                Cats("categories", "Unit categories (for Units statistics)")),
            Event("intel_gained", "Intel gained", "seen",
                "ScenarioFramework.CreateArmyIntelTrigger({cb:seen}, {brain:army}, {intel}, false, true, {categories}, {once}, {brain:target})",
                Army("army", "Army that looks"), P("target", "Army that is seen", SC2MissionParamKind.Army),
                Pick("intel", "Intel", "LOSNow", new[] { "LOSNow", "Radar", "Sonar", "Omni" }), Cats(), Flag("once", "Only once", true)),
            Event("area_entered_by", "Area entered by any of several armies", "entered",
                "ScenarioFramework.CreateMultipleBrainAreaTrigger({cb:entered}, {area}, {categories}, {once}, false, {brains:armies}, {count})",
                Area(), P("armies", "Armies (comma separated)", SC2MissionParamKind.ArmyList, "ARMY_1"), Cats(), Num("count", "Units needed", "1"), Flag("once", "Only once", true)),
            Event("platoon_killed", "Platoon killed", "killed",
                PlatoonLookup + "\tScenarioFramework.CreatePlatoonDeathTrigger({cb:killed}, platoon)\nend",
                Army(), Group()),
            Event("research_done", "Research completed", "done",
                "ScenarioFramework.CreateOnResearchTrigger({cb:done}, {brain:army}, {research}, 'complete')",
                Army("army", "Army", "ARMY_1"), Text("research", "Research name")),
            Event("repeat_timer", "Every N seconds", "tick",
                "ForkThread(function()\n\tlocal runs = 0\n\twhile not ScenarioInfo.OpEnded do\n\t\tWaitSeconds({seconds})\n\t\truns = runs + 1\n\t\t{cb:tick}()\n\t\tif {times} > 0 and runs >= {times} then return end\n\tend\nend)",
                Num("seconds", "Every (seconds)", "60"), Num("times", "Stop after (0 = never)", "0")),

            // ================================================================ objectives
            Objective(SC2MissionNodeCatalog.ObjectiveKillOrCapture, "Objective: kill or capture group", "Take out the enemy",
                "SimObjectives.KillOrCapture", null, "{ Units = group, MarkUnits = true }", true, Army(), Group()),
            Objective(SC2MissionNodeCatalog.ObjectiveBasic, "Objective: manual (Resolve objective ends it)", "Complete the task",
                "SimObjectives.Basic", "SimObjectives.GetActionIcon({icon})", "{}", false, Pick("icon", "Icon", "locate", ObjectiveIcons)),
            Objective("objective_army_stat", "Objective: army statistic", "Build up",
                "SimObjectives.ArmyStatCompare", "{icon}", "{ Army = {armyindex:army}, StatName = {stat}, CompareOp = {compare}, Value = {value}, Category = {categories}, ShowProgress = true }", false,
                Army("army", "Army", "ARMY_1"), Pick("icon", "Icon", "build", ObjectiveIcons),
                Pick("stat", "Statistic", "Units_Active", new[] { "Units_Active", "Units_Killed", "Units_History", "Enemies_Killed" }),
                Pick("compare", "Compare", ">=", AreaCompare), Num("value", "Value", "1"), Cats()),
            Objective("objective_research", "Objective: research", "Learn the technology",
                "SimObjectives.Research", null, "{ ShowProgress = true, AiBrain = {brain:army}, ResearchList = {list:research}, NumRequired = ({count} > 0 and {count} or nil) }", false,
                Army("army", "Army", "ARMY_1"), P("research", "Research names (comma separated)", SC2MissionParamKind.NameList), Num("count", "How many (0 = all)", "0")),
            Objective("objective_camera", "Objective: look at markers", "Look around",
                "SimObjectives.Camera", null, "{positions:markers}", false,
                P("markers", "Markers (comma separated)", SC2MissionParamKind.MarkerList)),

            // ================================================================ units
            Act("create_unit", "Create unit", "Units",
                "local ok, unit = pcall(ScenarioUtils.CreateArmyUnit, {army}, {unit})\nif not ok or not unit then\n\tWARN('SCED: unit ' .. {army} .. '/' .. {unit} .. ' was not created: ', unit)\nend",
                Army(), Unit()),
            Act("spawn_platoon", "Spawn group as platoon", "Units",
                "local ok, platoon = pcall(ScenarioUtils.CreateArmyGroupAsPlatoon, {army}, {group}, {formation})\nif ok and platoon then\n\tScenarioInfo.SCED.platoons[{groupkey}] = platoon\n\tLOG('SCED: platoon ' .. {groupkey} .. ' created')\nelse\n\tWARN('SCED: the platoon ' .. {groupkey} .. ' was not created: ', platoon)\nend",
                Army(), Group(), Pick("formation", "Formation", "AttackFormation", new[] { "AttackFormation", "GrowthFormation", "NoFormation" })),
            Act("platoon_order", "Platoon order along a chain", "Units",
                PlatoonLookup + "\tScenarioFramework.{map:order}(platoon, {chain})\nend",
                Army(), Group(), Pick("order", "Order", "patrol", new[] { "patrol", "move", "attack" }, new[] { "PlatoonPatrolChain", "PlatoonMoveChain", "PlatoonAttackChain" }), Chain()),
            Act("group_random_patrol", "Group patrols a chain in random order", "Units",
                GroupLookup + "\tScenarioFramework.GroupPatrolRoute(group, ScenarioPlatoonAI.GetRandomPatrolRoute({chain}))\nend",
                Army(), Group(), Chain()),
            Act("group_move_marker", "Group goes to a marker", "Units",
                GroupLookup + "\tif {clear} then IssueClearCommands(group) end\n\t{map:command}(group, {pos:marker})\nend",
                Army(), Group(), Marker(), Pick("command", "Order", "move", new[] { "move", "attack-move", "patrol" }, new[] { "IssueMove", "IssueAggressiveMove", "IssuePatrol" }),
                Flag("clear", "Cancel its earlier orders first", true)),
            Act("group_attack_unit", "Group attacks a unit", "Units",
                GroupLookup + "\t" + TargetLookup + "\tif target then IssueAttack(group, target) end\nend",
                Army(), Group(), Army("targetarmy", "Target's army"), Unit("targetunit", "Target unit")),
            Act("group_guard_unit", "Group guards a unit", "Units",
                GroupLookup + "\t" + TargetLookup + "\tif target then IssueGuard(group, target) end\nend",
                Army(), Group(), Army("targetarmy", "Guarded unit's army"), Unit("targetunit", "Guarded unit")),
            Act("group_stop", "Stop a group", "Units",
                GroupLookup + "\t{map:mode}(group)\nend",
                Army(), Group(), Pick("mode", "How", "clear", new[] { "clear", "stop" }, new[] { "IssueClearCommands", "IssueStop" })),
            Act("unit_move_marker", "Unit goes to a marker", "Units",
                UnitLookup + "\tif {clear} then IssueClearCommands({ unit }) end\n\t{map:command}({ unit }, {pos:marker})\n" + UnitMissing,
                Army(), Unit(), Marker(), Pick("command", "Order", "move", new[] { "move", "attack-move", "patrol" }, new[] { "IssueMove", "IssueAggressiveMove", "IssuePatrol" }),
                Flag("clear", "Cancel its earlier orders first", true)),
            Act("group_state", "Protect or kill group", "Units",
                GroupLookup + "\tScenarioFramework.{map:action}(group)\nend",
                Army(), Group(), Pick("action", "Action", "protect", UnitStateChoices,
                    new[] { "ProtectGroup", "UnProtectGroup", "RestrictGroupDeath", "AllowGroupDeath", "ForceGroupDeath", "DestroyGroup" })),
            Act("unit_state", "Protect or kill unit", "Units",
                UnitLookup + "\tScenarioFramework.{map:action}(unit)\n" + UnitMissing,
                Army(), Unit(), Pick("action", "Action", "protect", UnitStateChoices,
                    new[] { "ProtectUnit", "UnProtectUnit", "RestrictUnitDeath", "AllowUnitDeath", "ForceUnitDeath", "DestroyUnit" })),
            Act("group_veterancy", "Set group veterancy", "Units",
                GroupLookup + "\tScenarioFramework.SetGroupVeterancyLevel(group, {level})\nend",
                Army(), Group(), Num("level", "Level (0 to 5)", "1")),
            Act("unit_veterancy", "Set unit veterancy", "Units",
                UnitLookup + "\tScenarioFramework.SetUnitVeterancyLevel(unit, {level})\n" + UnitMissing,
                Army(), Unit(), Num("level", "Level (0 to 5)", "1")),
            Act("group_give", "Give group to army", "Units",
                GroupLookup + "\tlocal given = ScenarioFramework.GiveGroupToArmy(group, {armyindex:to})\n\tif given then ScenarioInfo.SCED.groups[{groupkey}] = given end\nend",
                Army(), Group(), P("to", "New owner", SC2MissionParamKind.Army)),
            Act("unit_give", "Give unit to army", "Units",
                UnitLookup + "\tScenarioFramework.GiveUnitToArmy(unit, {armyindex:to})\n" + UnitMissing,
                Army(), Unit(), P("to", "New owner", SC2MissionParamKind.Army)),
            Act("unit_name", "Name a unit", "Units",
                UnitLookup + "\tunit:SetCustomName({name})\n" + UnitMissing,
                Army(), Unit(), Text("name", "Name")),
            Act("unit_flags", "Unit: what can happen to it", "Units",
                UnitLookup + "\tunit:SetCanBeKilled({killable})\n\tunit:SetDoNotTarget(not {targetable})\n\tunit:SetCapturable({capturable})\n\tunit:SetReclaimable({reclaimable})\n\tunit:SetUnSelectable(not {selectable})\n\tunit:SetCanTakeDamage({damage})\n" + UnitMissing,
                Army(), Unit(), Flag("killable", "Can be killed", true), Flag("targetable", "Enemies can target it", true), Flag("capturable", "Can be captured", true),
                Flag("reclaimable", "Can be reclaimed", true), Flag("selectable", "The player can select it", true), Flag("damage", "Takes damage", true)),
            Act("group_flags", "Group: what can happen to it", "Units",
                GroupLookup + "\tfor index, unit in group do\n\t\tunit:SetCanBeKilled({killable})\n\t\tunit:SetDoNotTarget(not {targetable})\n\t\tunit:SetCapturable({capturable})\n\t\tunit:SetReclaimable({reclaimable})\n\t\tunit:SetUnSelectable(not {selectable})\n\t\tunit:SetCanTakeDamage({damage})\n\tend\nend",
                Army(), Group(), Flag("killable", "Can be killed", true), Flag("targetable", "Enemies can target it", true), Flag("capturable", "Can be captured", true),
                Flag("reclaimable", "Can be reclaimed", true), Flag("selectable", "The player can select it", true), Flag("damage", "Takes damage", true)),
            Act("unit_max_health", "Set a unit's health", "Units",
                UnitLookup + "\tunit:SetMaxHealth({health})\n\tif {heal} then unit:AdjustHealth(unit, {health}) end\n" + UnitMissing,
                Army(), Unit(), Num("health", "Maximum health", "5000"), Flag("heal", "Heal it to full", true)),
            Act("unit_tuning", "Set a unit's speed and build rate", "Units",
                UnitLookup + "\tif {speed} > 0 then unit:SetSpeedMult({speed}) end\n\tif {buildrate} > 0 then unit:SetBuildRate({buildrate}) end\n" + UnitMissing,
                Army(), Unit(), Num("speed", "Speed multiplier (0 = unchanged)", "1"), Num("buildrate", "Build rate (0 = unchanged)", "0")),
            Act("unit_weapon", "Switch a weapon on or off", "Units",
                UnitLookup + "\tunit:SetWeaponEnabledByLabel({label}, {enabled})\n" + UnitMissing,
                Army(), Unit(), Text("label", "Weapon label"), Flag("enabled", "Enabled", true)),
            Act("unit_nuke_ammo", "Load nuke missiles", "Units",
                UnitLookup + "\tunit:GiveNukeSiloAmmo({count})\n" + UnitMissing,
                Army(), Unit(), Num("count", "Missiles", "1")),
            Act("unit_command_cap", "Take an order away from a unit", "Units",
                UnitLookup + "\tunit:RemoveCommandCap({cap})\n" + UnitMissing,
                Army(), Unit(), Pick("cap", "Order", "RULEUCC_Reclaim", new[] { "RULEUCC_Reclaim", "RULEUCC_Capture", "RULEUCC_Attack", "RULEUCC_Move", "RULEUCC_Patrol", "RULEUCC_Guard", "RULEUCC_Stop" })),
            Act("transport_deploy", "Fly a group in by transport", "Units",
                "TransportUtils.SpawnTransportDeployGroup({ armyName = {army}, units = {group}, transport = {transport}, approachChain = {opt:approach}, unloadDest = {unload}, returnDest = {opt:leave}, platoonMoveDest = {opt:move}, bSkipTransportCleanup = {keep} })",
                Army(), Group("group", "Group to carry"), Text("transport", "Transport unit"), Opt(P("approach", "Approach chain (optional)", SC2MissionParamKind.Chain)),
                Marker("unload", "Drop-off marker"), Opt(Marker("leave", "Fly-away marker (optional)")), Opt(Marker("move", "Platoon moves to (optional)")), Flag("keep", "Keep the transport after it leaves", false)),
            Act("create_effect", "Visual effect at a marker", "Units",
                "NIS.CreateNISOnlyFX({effect}, NISSupport.GetPos({marker}))",
                Text("effect", "Effect blueprint"), Marker()),

            // ================================================================ world
            Act("damage_area", "Damage units around a marker", "World",
                "ScenarioGameEvents.DamageUnitsAroundPosition({pos:marker}, {radius}, {sparecdr}, {damageplayer}, {damageother}, ArmyBrains)",
                Marker(), Num("radius", "Radius", "30"), Flag("sparecdr", "Spare commanders", true),
                Num("damageplayer", "Damage to the player (army 1)", "500"), Num("damageother", "Damage to the others", "500")),
            Act("kill_area", "Kill units around a marker", "World",
                "ScenarioGameEvents.KillUnitsAroundPosition({pos:marker}, {radius}, {sparecdr}, ArmyBrains)",
                Marker(), Num("radius", "Radius", "30"), Flag("sparecdr", "Spare commanders", true)),
            Act("nuke_effect", "Nuke blast at a marker", "World",
                "ScenarioGameEvents.NukePosition({marker}, {range}, {decal})",
                Marker(), Num("range", "Random range", "10"), Flag("decal", "Leave a crater", true)),
            Act("set_playable_area", "Set playable area", "World",
                "ScenarioFramework.SetPlayableArea({area}, false)",
                Area()),
            Act("reveal_area", "Reveal an area", "World",
                "ScenarioFramework.CreateVisibleAreaLocation({radius}, {marker}, {seconds}, {brain:army})",
                Army("army", "Army that sees", "ARMY_1"), Marker(), Num("radius", "Radius", "60"), Num("seconds", "For (seconds)", "30")),
            Act("create_intel", "Give intel at a marker", "World",
                "ScenarioFramework.CreateIntelAtLocation({radius}, {marker}, {armyindex:army}, {intel}, ({seconds} > 0 and {seconds} or -1))",
                Army("army", "Army that gets it", "ARMY_1"), Marker(), Num("radius", "Radius", "100"),
                Pick("intel", "Intel", "Radar", new[] { "Vision", "Radar", "Sonar", "Omni" }), Num("seconds", "For (seconds, 0 = forever)", "0")),
            Act("clear_intel", "Take away the intel at a marker", "World",
                "ScenarioFramework.ClearIntel({pos:marker}, {radius})",
                Marker(), Num("radius", "Radius", "100")),
            Act("set_alliance", "Set relation of two armies", "World",
                "SetAlliance({armyindex:army}, {armyindex:other}, {relation})\nSetAlliance({armyindex:other}, {armyindex:army}, {relation})",
                Army(), P("other", "Other army", SC2MissionParamKind.Army), Pick("relation", "Relation", "Ally", new[] { "Ally", "Enemy", "Neutral" })),
            Act("block_build_grid", "Block building along a chain", "World",
                "ScenarioFramework.BlockOGrids({chain}, {army}, {large})",
                Army("army", "Army"), Chain(), Flag("large", "Large blockers", false)),
            Act("weather", "Create weather", "World",
                "import('/lua/sim/weather.lua').CreateWeather({ MapStyle = {style}, WeatherTypes = { { Type = {kind}, Chance = 1.0 }, }, Direction = { 0, 0, 0 }, })",
                Text("style", "Map style", "Tundra"), Text("kind", "Weather effect", "WhitePatchyClouds03")),

            // ================================================================ economy and research
            Act("give_resources", "Give resources", "Economy",
                "local brain = {brain:army}\nbrain:GiveResource('MASS', {mass})\nbrain:GiveResource('ENERGY', {energy})",
                Army("army", "Army", "ARMY_1"), Num("mass", "Mass", "1000"), Num("energy", "Energy", "1000")),
            Act("take_resources", "Take resources away", "Economy",
                "local brain = {brain:army}\nbrain:TakeResource('MASS', {mass})\nbrain:TakeResource('ENERGY', {energy})",
                Army("army", "Army", "ARMY_1"), Num("mass", "Mass", "1000"), Num("energy", "Energy", "1000")),
            Act("set_unit_cap", "Set unit cap", "Economy",
                "SetArmyUnitCap({armyindex:army}, {cap})",
                Army("army", "Army", "ARMY_1"), Num("cap", "Unit cap", "1000")),
            Act("build_restriction", "Restrict or allow units", "Economy",
                "ScenarioFramework.{map:mode}({armyindex:army}, {categories})",
                Army("army", "Army", "ARMY_1"), Cats("categories", "Unit categories", "categories.EXPERIMENTAL"),
                Pick("mode", "Mode", "allow", new[] { "restrict", "allow" }, new[] { "AddRestriction", "RemoveRestriction" })),
            Act("award_research", "Award research points", "Economy",
                "ScenarioGameEvents.AwardBonusResearchPoints({points}, {armyindex:army}, {delay}, {tech})",
                Army("army", "Army", "ARMY_1"), Num("points", "Points", "5"), Num("delay", "After (seconds)", "0"), Flag("tech", "Show it as a tech award", false)),
            Act("complete_research", "Complete research", "Economy",
                "{brain:army}:CompleteResearch({list:research})",
                Army("army", "Army", "ARMY_1"), P("research", "Research names (comma separated)", SC2MissionParamKind.NameList)),
            Act("research_restrict", "Lock or unlock research", "Economy",
                "{brain:army}:ResearchRestrict({list:research}, {lock})",
                Army("army", "Army", "ARMY_1"), P("research", "Research names (comma separated)", SC2MissionParamKind.NameList), Flag("lock", "Lock (off: unlock)", true)),
            Act("count_in_area", "Count an army's units in an area", "Economy",
                "ScenarioInfo.SCED.vars[{variable}] = ScenarioFramework.NumCatUnitsInArea({categories}, {area}, {brain:army})",
                Army("army", "Army", "ARMY_1"), Area(), Cats(), Var()),
            Act("count_army_units", "Count an army's units", "Economy",
                "ScenarioInfo.SCED.vars[{variable}] = {brain:army}:GetCurrentUnits({categories})",
                Army("army", "Army", "ARMY_1"), Cats(), Var()),
            Act("random_number", "Random number", "Economy",
                "ScenarioInfo.SCED.vars[{variable}] = Random({min}, {max})",
                Num("min", "From", "1"), Num("max", "To", "10"), Var()),

            // ================================================================ messages and sound
            Act("show_text", "Show text", "Actions",
                "Sync.PrintText = Sync.PrintText or {}\ntable.insert(Sync.PrintText, { text = {text}, size = 20, color = 'ffffffff', duration = {seconds}, location = {location} })",
                Text("text", "Text"), Num("seconds", "For (seconds)", "5"), Pick("location", "Where", "center", new[] { "center", "leftbottom" })),
            Act("log_message", "Write to the game log", "Actions",
                "LOG('SCED: ' .. {text})",
                Text("text", "Text")),
            Act("flush_dialogue", "Stop the dialogue queue", "Actions",
                "ScenarioFramework.FlushDialogueQueue()"),
            Act("play_music", "Play a music event", "Actions",
                "NIS.PlayMusicEventByHandle({handle})",
                Text("handle", "Music event handle")),
            Act("play_sound", "Play a sound", "Actions",
                "NIS.Sound({bank}, {cue})",
                Text("bank", "Sound bank"), Text("cue", "Cue")),
            Act("defeat_when_unit_dies", "Defeat when this unit dies", "Flow",
                UnitLookup + "\tScenarioFramework.CreateUnitDeathTrigger(ScenarioGameCleanup.PlayerDeath, unit)\n" + UnitMissing,
                Army(), Unit()),

            // ================================================================ cutscene (the game's NIS system)
            NisNow("nis_begin_standard", "Cutscene: begin", "NIS.StartNIS_Standard(({fov} > 0 and {fov} or nil), {bars}, {lock})",
                Num("fov", "Start field of view (0 = default)", "0"), Flag("bars", "Black bars", true), Flag("lock", "Lock the player's input", true)),
            NisNow("nis_begin_opening", "Cutscene: begin the opening", "NIS.StartNIS_Opening({camera}, {zoom}, {opt:position}, ({fov} > 0 and {fov} or nil))",
                Marker("camera", "Camera marker"), Num("zoom", "Zoom", "40"), Opt(Marker("position", "Start position marker (optional)")), Num("fov", "Field of view (0 = default)", "0")),
            NisNow("nis_begin_midop", "Cutscene: begin in the middle of the mission", "NIS.StartNIS_MidOp({area}, ({zoom} > 0 and {zoom} or nil), ({fov} > 0 and {fov} or nil))",
                Area("area", "Area to show"), Num("zoom", "Minimum zoom (0 = default)", "0"), Num("fov", "Field of view (0 = default)", "0")),
            NisNow("nis_begin_victory", "Cutscene: begin the victory scene", "NIS.StartNIS_Victory(({fov} > 0 and {fov} or nil), {bars}, {lock})",
                Num("fov", "Start field of view (0 = default)", "0"), Flag("bars", "Black bars", true), Flag("lock", "Lock the player's input", true)),
            Nis("nis_end_standard", "Cutscene: end", "NIS.EndNIS_Standard(({zoom} > 0 and {zoom} or nil), {flush}, {seconds})",
                Num("zoom", "Zoom to restore (0 = as it was)", "0"), Flag("flush", "Forget the intel it gave", false), Num("seconds", "Restore over (seconds)", "2")),
            Nis("nis_end_opening", "Cutscene: end the opening", "NIS.EndNIS_Opening(({zoom} > 0 and {zoom} or nil), {flush}, {seconds})",
                Num("zoom", "Zoom to restore (0 = as it was)", "100"), Flag("flush", "Forget the intel it gave", false), Num("seconds", "Restore over (seconds)", "2")),
            Nis("nis_end_midop", "Cutscene: end the mid-mission scene", "NIS.EndNIS_MidOp({flush}, {seconds})",
                Flag("flush", "Forget the intel it gave", false), Num("seconds", "Restore over (seconds)", "2")),
            Nis("nis_end_norestore", "Cutscene: end without restoring the camera", "NIS.EndNIS_NoRestore({flush}, {seconds})",
                Flag("flush", "Forget the intel it gave", false), Num("seconds", "Over (seconds)", "0")),
            new SC2MissionNodeDef
            {
                Type = "nis_end_victory", Title = "Cutscene: end and win", Category = "Cutscene (NIS)", Ports = new string[0], Params = new SC2MissionParamDef[0],
                Template = "ForkThread(function()\n\tNIS.EndNIS_Victory(nil, false)\nend)",
            },
            Nis("nis_zoom_to", "Cutscene: camera to a marker", "{map:wait}(NISSupport.GetPos({marker}), NISSupport.GetOrient({marker}), {zoom}, {seconds})",
                Marker("marker", "Camera marker"), Num("zoom", "Distance (negative = closer)", "0", signed: true), Num("seconds", "Seconds", "5"),
                Pick("wait", "Wait for the move", "wait", new[] { "wait", "go on at once" }, new[] { "NIS.ZoomTo", "NIS.ZoomToNoWait" })),
            Nis("nis_move_to", "Cutscene: camera moves to a marker", "{map:wait}({marker}, {seconds})",
                Marker("marker", "Camera marker"), Num("seconds", "Seconds", "5"),
                Pick("wait", "Wait for the move", "wait", new[] { "wait", "go on at once" }, new[] { "NIS.MoveTo", "NIS.MoveToNoWait" })),
            NisNow("nis_snap_to", "Cutscene: camera jumps to a marker", "NIS.SnapTo({marker})",
                Marker("marker", "Camera marker")),
            NisNow("nis_set_zoom", "Cutscene: change the zoom", "NIS.SetZoom({distance}, {seconds})",
                Num("distance", "Distance", "60"), Num("seconds", "Over (seconds)", "3")),
            NisNow("nis_set_fov", "Cutscene: change the field of view", "NIS.SetFOV({fov}, {seconds})",
                Num("fov", "Field of view", "55"), Num("seconds", "Over (seconds)", "0")),
            NisNow("nis_spin", "Cutscene: spin the camera", "NIS.Spin({rate}, {seconds})",
                Num("rate", "Degrees per second", "10", signed: true), Num("seconds", "For (seconds)", "5")),
            NisNow("nis_track_unit", "Cutscene: camera follows a unit", UnitLookup + "\tNIS.EntityTrackRelative({ ent = unit, degreesHeading = {heading}, degreesPitch = {pitch}, nTrackDistance = {distance}, nTrackToDuration = {seconds}, nOffsetX = {ox}, nOffsetY = {oy}, nOffsetZ = {oz} })\n" + UnitMissing,
                Army(), Unit(), Num("heading", "Heading (180 = the unit's front)", "135", signed: true), Num("pitch", "Pitch (90 = from above)", "35", signed: true),
                Num("distance", "Distance", "60"), Num("seconds", "Time to get there", "2"),
                Num("ox", "Offset X", "0", signed: true), Num("oy", "Offset Y", "0", signed: true), Num("oz", "Offset Z", "0", signed: true)),
            NisNow("nis_track_group", "Cutscene: camera follows a group", GroupLookup + "\tNIS.Track(group, {zoom}, {seconds})\nend",
                Army(), Group(), Num("zoom", "Zoom", "60"), Num("seconds", "Time to get there", "2")),
            Nis("nis_fade", "Cutscene: fade the screen", "{map:mode}({seconds})",
                Pick("mode", "Fade", "out", new[] { "out", "in", "out to white", "in from white" }, new[] { "NIS.FadeOut", "NIS.FadeIn", "NIS.FadeOutWhite", "NIS.FadeInWhite" }),
                Num("seconds", "Seconds", "2")),
            Nis("nis_dialog", "Cutscene: dialogue", "if {opt:video} then\n\t{map:wait}({ { speaker = {speaker}, text = {text}, vid = {opt:video}, bank = {opt:bank}, cue = {opt:cue}, forced_duration = {seconds} }, })\nelse\n\tSync.PrintText = Sync.PrintText or {}\n\ttable.insert(Sync.PrintText, { text = {speaker} .. ': ' .. {text}, size = 16, color = 'ffffffff', duration = {seconds}, location = 'leftbottom' })\n\tWaitSeconds({seconds})\nend",
                Text("speaker", "Speaker"), Text("text", "Text"), Num("seconds", "Shown for (seconds)", "5"),
                Text("video", "Video file (optional)"), Text("bank", "Voice bank (optional)"), Text("cue", "Voice cue (optional)"),
                Pick("wait", "Wait for the line", "wait", new[] { "wait", "go on at once" }, new[] { "NIS.Dialog", "NIS.DialogNoWait" })),
            NisNow("nis_title", "Cutscene: show the mission title", "NIS.DisplayTitle({text})",
                Text("text", "Title")),
            NisNow("nis_location", "Cutscene: show place and date", "NIS.DisplayLocationDate({place}, {date})",
                Text("place", "Place"), Text("date", "Date")),
            NisNow("nis_viz_marker", "Cutscene: reveal around a marker", "NIS.CreateVizMarker({marker}, {radius}, ({seconds} > 0 and {seconds} or nil))",
                Marker(), Num("radius", "Radius", "100"), Num("seconds", "For (seconds, 0 = until the end)", "0")),
            NisNow("nis_arrow", "Cutscene: arrow at a marker", "NIS.CreateArrow({marker}, ({height} > 0 and {height} or nil))",
                Marker(), Num("height", "Height (0 = default)", "0")),
            NisNow("nis_unit_visible", "Cutscene: show or hide a unit", UnitLookup + "\t{map:mode}(unit)\n" + UnitMissing,
                Army(), Unit(), Pick("mode", "Mode", "hide", new[] { "show", "hide" }, new[] { "NIS.ShowUnit", "NIS.HideUnit" })),
            NisNow("nis_group_visible", "Cutscene: show or hide a group", GroupLookup + "\t{map:mode}(group)\nend",
                Army(), Group(), Pick("mode", "Mode", "hide", new[] { "show", "hide" }, new[] { "NIS.ShowGroup", "NIS.HideGroup" })),
            NisNow("nis_warp_unit", "Cutscene: move a unit to a marker at once", UnitLookup + "\tNIS.WarpUnit(unit, NISSupport.GetPos({marker}))\n" + UnitMissing,
                Army(), Unit(), Marker()),
            NisNow("nis_warp_group", "Cutscene: move a group to a marker at once", GroupLookup + "\tNIS.WarpGroup(group, NISSupport.GetPos({marker}))\nend",
                Army(), Group(), Marker()),
            NisNow("nis_create_unit", "Cutscene: a unit that exists only in it", "NIS.CreateNISOnlyUnit({army}, {unit})",
                Army(), Unit()),
            NisNow("nis_create_group", "Cutscene: a group that exists only in it", "ScenarioInfo.SCED.groups[{groupkey}] = NIS.CreateNISOnlyGroup({army}, {group})",
                Army(), Group()),
            NisNow("nis_unhunker", "Cutscene: a commander climbs out", UnitLookup + "\tNIS.UnHunker(unit)\n" + UnitMissing,
                Army(), Unit()),
            NisNow("nis_no_combat", "Cutscene: stop the fighting", "NIS.DisableCombat()"),
            Nis("nis_movie", "Cutscene: play a movie", "NIS.PlayMovie({movie})\nwhile UIAreMoviesFinished() == false do\n\tWaitSeconds(0.1)\nend",
                Text("movie", "Movie path", "/movies/FMV_CYB_OPENING")),

            // ================================================================ bases
            Act("base_expansion", "Make a base an expansion of another", "Bases",
                "local parent = ScenarioInfo.SCED.bases[{base}]\nif parent then parent:AddExpansionBase({expansion}) end",
                BaseRef(), BaseRef("expansion")),
            Act("base_active", "Switch a base on or off", "Bases",
                "local base = ScenarioInfo.SCED.bases[{base}]\nif base then base:BaseActive({active}) end",
                BaseRef(), Flag("active", "Active", true)),
            Act("base_engineers", "Set a base's engineer count", "Bases",
                "local base = ScenarioInfo.SCED.bases[{base}]\nif base then base:SetEngineerCount({count}) end",
                BaseRef(), Num("count", "Engineers", "4")),
            Act("base_build_all", "Let a base build every structure", "Bases",
                "local base = ScenarioInfo.SCED.bases[{base}]\nif base then base:SetBuildAllStructures({enabled}) end",
                BaseRef(), Flag("enabled", "Build everything", true)),
            Act("opai_target", "Choose the army an OpAI attacks", "Bases",
                "local base = ScenarioInfo.SCED.bases[{base}]\nlocal opai = base and base:GetOpAI({opai})\nif opai then opai:SetTargetArmyIndex({armyindex:target}) end",
                BaseRef(), P("opai", "OpAI", SC2MissionParamKind.OpAI), Army("target", "Army to attack", "ARMY_1")),
            Act("opai_tune", "Change an OpAI's wave size and pace", "Bases",
                "local base = ScenarioInfo.SCED.bases[{base}]\nlocal opai = base and base:GetOpAI({opai})\nif opai then\n\tif {child} > 0 then opai:SetChildCount({child}) end\n\tif {max} > 0 then opai:SetMaxActivePlatoons({max}) end\n\topai:SetAttackDelay({delay})\nend",
                BaseRef(), P("opai", "OpAI", SC2MissionParamKind.OpAI), Num("child", "Child count (0 = unchanged)", "0"), Num("max", "Max platoons (0 = unchanged)", "0"), Num("delay", "Delay (seconds)", "0")),

            // ---------------------------------------------------------------- more units, found in the campaign scripts
            Act("unit_warp", "Warp a unit to a marker", "Units",
                UnitLookup + "\tWarp(unit, {pos:marker})\n" + UnitMissing,
                Army(), Unit(), Marker()),
            Act("unit_shield", "Switch a unit's shield on or off", "Units",
                UnitLookup + "\tif {on} then unit:EnableShield() else unit:DisableShield() end\n" + UnitMissing,
                Army(), Unit(), Flag("on", "Shield on", true)),
            Act("unit_launch_nuke", "Launch a nuke from a unit", "Units",
                UnitLookup + "\tIssueNuke({ unit }, {pos:marker})\n" + UnitMissing,
                Army(), Unit("unit", "Launcher"), Marker("marker", "Target marker")),
            Event("group_partly_killed", "Some of a group killed", "killed",
                GroupLookup + "\tScenarioFramework.CreateSubGroupDeathTrigger({cb:killed}, group, {count})\nend",
                Army(), Group(), Num("count", "Units that must die", "1")),
            Act("anti_nuke", "Start the AI's anti-nuke defence", "Units",
                "import('/lua/sim/ScenarioFramework/ScenarioGameUtilsAntiNuke.lua').StartAntiNukeManagers()"),
            Act("drone_launcher", "Activate a drone launcher", "Units",
                UnitLookup + "\timport('/lua/sim/ScenarioFramework/ScenarioGameUtilsArtifact.lua').ActivateDroneLauncher(unit, {army}, {drones}, {chain}, {respawn}, {delay})\n" + UnitMissing,
                Army(), Unit("unit", "Launcher"), Num("drones", "Most drones at once", "4"), Chain("chain", "Drones patrol this chain"), Num("respawn", "Respawn delay (seconds)", "10"), Num("delay", "First launch after (seconds)", "5")),
            Act("krakken_ai", "Start a Krakken's behaviour", "Units",
                UnitLookup + "\timport('/lua/sim/ScenarioFramework/ScenarioGameUtilsKrakken.lua').LaunchKrakkenAI(unit, {chain}, {pos:home}, {homearea}, {restart})\n" + UnitMissing,
                Army(), Unit("unit", "Krakken"), Chain("chain", "Patrol chain"), Marker("home", "Home marker"), Area("homearea", "Home area"), Num("restart", "Restart delay (seconds)", "30")),

            // ---------------------------------------------------------------- more economy queries
            Act("count_economy", "Read an army's stored resources", "Economy",
                "ScenarioInfo.SCED.vars[{variable}] = {brain:army}:GetEconomyStored({resource})",
                Army("army", "Army", "ARMY_1"), Pick("resource", "Resource", "MASS", new[] { "MASS", "ENERGY" }), Var()),
            Act("check_research", "Has an army learned a research?", "Economy",
                "ScenarioInfo.SCED.vars[{variable}] = {brain:army}:HasResearched({research}) and true or false",
                Army("army", "Army", "ARMY_1"), Text("research", "Research name"), Var("variable", "Store the answer (a true/false variable)")),

            // ---------------------------------------------------------------- more cutscene
            Nis("nis_transport_arrival", "Cutscene: a transport brings a group", "NIS.TransportArrival({ armyName = {army}, units = {group}, transport = {transport}, approachChain = {opt:approach}, unloadDest = {unload}, returnDest = {opt:leave}, platoonMoveDest = {opt:move}, bSkipTransportCleanup = {keep}, bUnSelectAfterNIS = {lock} }, false)",
                Army(), Group("group", "Group to carry"), Text("transport", "Transport unit"), Opt(P("approach", "Approach chain (optional)", SC2MissionParamKind.Chain)),
                Marker("unload", "Drop-off marker"), Opt(Marker("leave", "Fly-away marker (optional)")), Opt(Marker("move", "Platoon moves to (optional)")),
                Flag("keep", "Keep the transport after it leaves", false), Flag("lock", "The player cannot use the transport after the scene", true)),
            NisNow("nis_spin_around", "Cutscene: camera circles a unit", UnitLookup + "\tNIS.EntitySpinRelative({ ent = unit, degreesHeading = {heading}, degreesPitch = {pitch}, nSpinRate = {rate}, nSpinDistance = {distance}, nSpinDuration = {seconds} })\n" + UnitMissing,
                Army(), Unit(), Num("heading", "Start heading", "135", signed: true), Num("pitch", "Pitch", "30", signed: true), Num("rate", "Rotations per second", "0.03"),
                Num("distance", "Distance", "50"), Num("seconds", "For (seconds)", "10")),
            NisNow("nis_nose_cam", "Cutscene: camera on a unit's nose", UnitLookup + "\tNIS.NoseCam(unit, {pitch}, {zoom}, {seconds}, {transition})\n" + UnitMissing,
                Army(), Unit(), Num("pitch", "Pitch adjustment", "0", signed: true), Num("zoom", "Zoom", "10"), Num("seconds", "For (seconds)", "5"), Num("transition", "Time to get there", "1")),
            NisNow("nis_clear_tracking", "Cutscene: stop following", "NIS.ClearTracking({zoom}, {seconds})",
                Num("zoom", "Zoom", "60"), Num("seconds", "Over (seconds)", "2")),
            Nis("nis_group_move_chain", "Cutscene: group follows a chain", GroupLookup + "\tNIS.GroupMoveChain(group, {chain})\nend",
                Army(), Group(), Chain()),
            NisNow("nis_hide_warp", "Cutscene: hide a unit and move it", UnitLookup + "\tNIS.HideAndWarpUnit(unit, NISSupport.GetPos({marker}))\n" + UnitMissing,
                Army(), Unit(), Marker()),
            NisNow("nis_ease", "Cutscene: smooth camera starts and stops", "{map:mode}()",
                Pick("mode", "Camera moves", "smooth", new[] { "smooth", "linear" }, new[] { "NIS.EnableEaseInOut", "NIS.DisableEaseInOut" })),

            // ================================================================ flow
            new SC2MissionNodeDef
            {
                Type = "once", Title = "Only the first time", Category = "Flow", Ports = Out, Params = new SC2MissionParamDef[0],
                Template = "ScenarioInfo.SCED.once = ScenarioInfo.SCED.once or {}\nif not ScenarioInfo.SCED.once[{node}] then\n\tScenarioInfo.SCED.once[{node}] = true\n\t{cb:out}()\nend",
            },
            new SC2MissionNodeDef
            {
                Type = "join", Title = "Wait for all inputs", Category = "Flow", Ports = Out, CountedInputsParam = "count",
                Params = new[] { Num("count", "Inputs (2 to 12)", "2"), Flag("again", "Can fire again", false) },
                // fires when every input has been reached at least once
                Template = "ScenarioInfo.SCED.joins = ScenarioInfo.SCED.joins or {}\nlocal join = ScenarioInfo.SCED.joins[{node}]\nif not join then\n\tjoin = { seen = {}, n = 0 }\n\tScenarioInfo.SCED.joins[{node}] = join\nend\nif not join.seen[{input}] then\n\tjoin.seen[{input}] = true\n\tjoin.n = join.n + 1\n\tif join.n == {count} then\n\t\tif {again} then ScenarioInfo.SCED.joins[{node}] = nil end\n\t\t{cb:out}()\n\tend\nend",
            },
            new SC2MissionNodeDef
            {
                Type = "sequence", Title = "Sequence (every output, one after the other)", Category = "Flow", Ports = new[] { "1", "2", "3" }, CountedPortsParam = "count",
                Params = new[] { Num("count", "Outputs (2 to 12)", "3") },
            },
            new SC2MissionNodeDef
            {
                Type = "sequence_timed", Title = "Sequence with pauses", Category = "Flow", Defers = true, Ports = new[] { "1", "2", "3" }, CountedPortsParam = "count",
                Params = new[] { Num("count", "Outputs (2 to 12)", "3"), Num("seconds", "Pause between outputs (seconds)", "1") },
            },
            new SC2MissionNodeDef
            {
                Type = "switch_variable", Title = "Switch on a variable", Category = "Flow", Ports = new[] { "1", "other" }, CountedPortsParam = "cases", ExtraPorts = new[] { "other" },
                Params = new[] { Var(), P("cases", "Values (comma separated, one output each)", SC2MissionParamKind.NameList, "1,2,3") },
            },
            new SC2MissionNodeDef
            {
                Type = "gate", Title = "Gate (lets the flow through while it is open)", Category = "Flow", Ports = new[] { "out", "blocked" },
                Inputs = new[] { "pass", "open", "close" }, Params = new[] { Flag("open", "Starts open", true) },
                Template = "ScenarioInfo.SCED.gates = ScenarioInfo.SCED.gates or {}\nlocal open = ScenarioInfo.SCED.gates[{node}]\nif open == nil then open = {open} end\nif open then\n\t{cb:out}()\nelse\n\t{cb:blocked}()\nend",
                InputTemplates = new Dictionary<string, string>
                {
                    ["open"] = "ScenarioInfo.SCED.gates = ScenarioInfo.SCED.gates or {}\nScenarioInfo.SCED.gates[{node}] = true",
                    ["close"] = "ScenarioInfo.SCED.gates = ScenarioInfo.SCED.gates or {}\nScenarioInfo.SCED.gates[{node}] = false",
                },
            },
            new SC2MissionNodeDef
            {
                Type = "counter", Title = "Counter", Category = "Flow", Ports = new[] { "each", "reached" },
                Inputs = new[] { "count", "reset" }, Params = new[] { Num("target", "Reaches at", "3"), Opt(Var("variable", "Store the count in variable")) },
                Template = "ScenarioInfo.SCED.counters = ScenarioInfo.SCED.counters or {}\nlocal value = (ScenarioInfo.SCED.counters[{node}] or 0) + 1\nScenarioInfo.SCED.counters[{node}] = value\nif {opt:variable} then ScenarioInfo.SCED.vars[{opt:variable}] = value end\n{cb:each}()\nif value == {target} then\n\t{cb:reached}()\nend",
                InputTemplates = new Dictionary<string, string>
                {
                    ["reset"] = "ScenarioInfo.SCED.counters = ScenarioInfo.SCED.counters or {}\nScenarioInfo.SCED.counters[{node}] = 0\nif {opt:variable} then ScenarioInfo.SCED.vars[{opt:variable}] = 0 end",
                },
            },

            // ================================================================ layout
            new SC2MissionNodeDef
            {
                Type = "comment", Title = "Comment frame", Category = "Layout", IsComment = true, Inputs = new string[0], Ports = new string[0],
                Params = new[]
                {
                    Text("text", "Label", "Group"), Num("width", "Width", "360"), Num("height", "Height", "220"),
                    Pick("color", "Colour", "gray", new[] { "gray", "blue", "green", "orange", "red", "purple" }),
                },
            },

            // ================================================================ loops
            new SC2MissionNodeDef
            {
                Type = "loop_repeat", Title = "Repeat N times", Category = "Loops", Defers = true,
                Inputs = LoopInputs, Ports = new[] { "body", "done", "stopped" },
                Params = new[] { Num("count", "Rounds", "3"), Flag("auto", "Next round by itself", false), Num("pause", "Pause between rounds (seconds)", "0"), Opt(Var("variable", "Store the round number (1, 2...) in variable")) },
                Template = LoopStart("count = {count}, pause = {pause}, auto = {auto}", "", "{cb:body}()", "{cb:done}()"),
                InputTemplates = LoopControls,
            },
            new SC2MissionNodeDef
            {
                Type = "loop_while", Title = "Repeat while a variable matches", Category = "Loops", Defers = true,
                Inputs = LoopInputs, Ports = new[] { "body", "done", "stopped" },
                Params = new[]
                {
                    Var(), Pick("compare", "Compare", "==", CompareOperators), Text("value", "Value", "1"),
                    Flag("auto", "Next round by itself", false), Num("pause", "Pause between rounds (seconds)", "0.5"), Num("limit", "Most rounds (a safety stop)", "1000"),
                    Opt(Var("round", "Store the round number (1, 2...) in variable")),
                },
                Template = LoopStart("cond = function() return ScenarioInfo.SCED.vars[{variable}] {raw:compare} {varvalue:value} end, limit = {limit}, pause = math.max({pause}, 0.1), auto = {auto}", "", "{cb:body}()", "{cb:done}()", "round"),
                InputTemplates = LoopControls,
            },
            new SC2MissionNodeDef
            {
                Type = "loop_forever", Title = "Repeat every N seconds until stopped", Category = "Loops", Defers = true,
                Inputs = new[] { "start", "stop", "pause", "resume" }, Ports = new[] { "tick", "stopped" },
                Params = new[] { Num("seconds", "Seconds between ticks (at least 0.1)", "5"), Flag("first", "First tick right away", false), Opt(Var("variable", "Store the tick number (1, 2...) in variable")) },
                Template = LoopStart("pause = math.max({seconds}, 0.1), auto = true, delayFirst = not {first}", "", "{cb:tick}()", ""),
                InputTemplates = new Dictionary<string, string>
                {
                    ["stop"] = LoopBreakLua,
                    ["pause"] = "local state = ScenarioInfo.SCED.loops[{node}]\nif state then state.paused = true end",
                    ["resume"] = "local state = ScenarioInfo.SCED.loops[{node}]\nif state then state.paused = false end",
                },
            },
            new SC2MissionNodeDef
            {
                Type = "loop_foreach", Title = "For each unit in a group", Category = "Loops", Defers = true,
                Inputs = LoopInputs, Ports = new[] { "each", "done", "stopped" },
                Params = new[]
                {
                    Army(), Group(), Flag("auto", "Next round by itself", false), Num("pause", "Pause between units (seconds)", "0"),
                    Opt(Var("variable", "Store the number of the unit (1, 2...) in variable")),
                },
                Template = LoopStart("items = items, pause = {pause}, auto = {auto}",
                    "local group = SCED_Group({army}, {group})\nif not group then\n\t{cb:done}()\n\treturn\nend\nlocal items = {}\nfor index, unit in group do table.insert(items, unit) end\n",
                    "ScenarioInfo.SCED.loopUnit[{node}] = s.items[s.i]\n\t{cb:each}()", "{cb:done}()"),
                InputTemplates = LoopControls,
            },
            LoopAct("loop_unit_kill", "Loop unit: kill it", "unit:Kill()"),
            LoopAct("loop_unit_remove", "Loop unit: remove it", "unit:Destroy()"),
            LoopAct("loop_unit_stop", "Loop unit: stop its orders", "IssueStop({ unit })"),
            LoopAct("loop_unit_move", "Loop unit: move to a marker", "IssueMove({ unit }, {pos:marker})", Marker()),
            LoopAct("loop_unit_attack_move", "Loop unit: attack-move to a marker", "IssueAggressiveMove({ unit }, {pos:marker})", Marker()),
            LoopAct("loop_unit_patrol", "Loop unit: patrol a chain", "for index, position in ScenarioUtils.ChainToPositions({chain}) do IssuePatrol({ unit }, position) end", Chain()),
            LoopAct("loop_unit_give", "Loop unit: give it to an army", "ScenarioFramework.GiveUnitToArmy(unit, {armyindex:to})", P("to", "New owner", SC2MissionParamKind.Army)),
            LoopAct("loop_unit_veterancy", "Loop unit: set its veterancy", "ScenarioFramework.SetUnitVeterancyLevel(unit, {level})", Num("level", "Level (0 to 5)", "1")),
            LoopAct("loop_unit_health", "Loop unit: set its health (percent)", "unit:SetHealth(unit, unit:GetMaxHealth() * {percent} / 100)", Num("percent", "Percent of full health", "100")),
            new SC2MissionNodeDef
            {
                Type = "loop_unit_is", Title = "Loop unit: is it of a kind?", Category = "Loops", Ports = new[] { "yes", "no" },
                Params = new[] { LoopParam(), Cats() },
                Template = "local unit = {loopunit}\nif unit and not unit:IsDead() and EntityCategoryContains({categories}, unit) then\n\t{cb:yes}()\nelse\n\t{cb:no}()\nend",
            },
            new SC2MissionNodeDef
            {
                Type = AskDifficultyType, Title = "Ask the player for the difficulty", Category = "Flow", Defers = true, Ports = new[] { "chosen" },
                Params = new[]
                {
                    Text("text", "Question", "Choose the difficulty"),
                    Text("easy", "First button", "Easy"), Text("normal", "Second button", "Normal"), Text("hard", "Third button", "Hard"),
                    Pick("position", "Where the menu is", "center", new[] { "center", "left", "right" }),
                    Flag("onlyplayer", "Only player 1 can answer", true),
                    Pick("default", "Used when nobody answers", "normal", new[] { "easy", "normal", "hard" }, new[] { "1", "2", "3" }),
                    Num("timeout", "Wait for an answer (seconds, 0 = for ever)", "0"),
                    Opt(Var("variable", "Store the answer (1, 2 or 3) in variable")),
                },
                // The menu is the game's own sim dialogue: it is drawn by the interface, and a click comes back as a callback. The bases of the mission
                // are created only now, because the campaign AI reads ScenarioInfo.Options.Difficulty when it builds them.
                Template = "local dialogue = import('/lua/sim/SimDialogue.lua').Create({text}, { {easy}, {normal}, {hard} }, {position})\n" +
                    "local answered = false\n" +
                    "local function choose(level)\n" +
                    "\tif answered then return end\n" +
                    "\tanswered = true\n" +
                    "\tScenarioInfo.Options = ScenarioInfo.Options or {}\n" +
                    "\tScenarioInfo.Options.Difficulty = level\n" +
                    "\tif {opt:variable} then ScenarioInfo.SCED.vars[{opt:variable}] = level end\n" +
                    "\tLOG('SCED: the difficulty is ', level)\n" +
                    "\tdialogue:Destroy()\n" +
                    "\tSCED_BuildBases()\n" +
                    "\t{cb:chosen}()\n" +
                    "end\n" +
                    "dialogue.OnButtonPressed = function(self, info)\n" +
                    "\tlocal player = ScenarioInfo.ARMY_PLAYER\n" +
                    "\tif {onlyplayer} and player and info.presser ~= player then return end\n" +
                    "\tchoose(info.buttonID)\n" +
                    "end\n" +
                    "if {timeout} > 0 then\n" +
                    "\tForkThread(function()\n" +
                    "\t\tWaitSeconds({timeout})\n" +
                    "\t\tchoose({map:default})\n" +
                    "\tend)\n" +
                    "end",
            },
            new SC2MissionNodeDef
            {
                Type = "cutscenes_allowed", Title = "Branch: cutscenes allowed?", Category = "Flow", Ports = new[] { "play", "skip" }, Params = new SC2MissionParamDef[0],
                Template = "if ScenarioInfo.Options and ScenarioInfo.Options.NoNIS then\n\t{cb:skip}()\nelse\n\t{cb:play}()\nend",
            },
            new SC2MissionNodeDef
            {
                Type = "random_branch", Title = "Random branch", Category = "Flow", Ports = new[] { "yes", "no" }, Params = new[] { Num("percent", "Chance of yes (percent)", "50") },
                Template = "if Random(1, 100) <= {percent} then\n\t{cb:yes}()\nelse\n\t{cb:no}()\nend",
            },
        };
    }
}
