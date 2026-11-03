using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    public enum SC2MissionParamKind
    {
        Text,
        Number,
        Bool,
        /// <summary>The name of an army of the map (ARMY_1...).</summary>
        Army,
        /// <summary>The name of an Area of the map.</summary>
        Area,
        /// <summary>The name of a unit group of an army.</summary>
        Group,
        /// <summary>The name of a single unit of an army.</summary>
        Unit,
        /// <summary>The name of a marker of the map (camera targets, base centres).</summary>
        Marker,
        /// <summary>The name of a chain (patrol route) of the map.</summary>
        Chain,
        /// <summary>A Lua category expression such as categories.ALLUNITS.</summary>
        Categories,
        /// <summary>The id of a stage of the graph.</summary>
        Stage,
        /// <summary>The name of a mission variable.</summary>
        Variable,
        /// <summary>The id of a base of the Bases tab.</summary>
        Base,
        /// <summary>The name of an OpAI of the chosen base.</summary>
        OpAI,
        /// <summary>Free Lua statements.</summary>
        Code,
        Choice,
        /// <summary>Names separated by commas (a list of research names, of unit blueprints).</summary>
        NameList,
        /// <summary>Marker names separated by commas.</summary>
        MarkerList,
        /// <summary>Army names separated by commas (ARMY_1, ARMY_2).</summary>
        ArmyList
    }

    public sealed class SC2MissionParamDef
    {
        public string Key;
        public string Label;
        public SC2MissionParamKind Kind;
        public string Default;
        public string[] Choices;
        /// <summary>A number that may be negative (a camera distance, an angle).</summary>
        public bool Signed;
        /// <summary>The parameter may be left empty (the call gets nil).</summary>
        public bool Optional;
        /// <summary>For a choice: the Lua fragment of each choice, in the order of Choices (used by {map:name} in a node template).</summary>
        public string[] Lua;
    }

    /// <summary>How an objective node is written: the framework function that creates it, and the pieces it is given. Each piece is a template
    /// (see SC2MissionExtraNodes); the title, description and kind come from the node's Kind, Title and Description parameters.</summary>
    public sealed class SC2MissionObjectiveSpec
    {
        public string Function;
        /// <summary>An extra argument between the description and the target (the action icon), or null.</summary>
        public string Action;
        public string Target;
        /// <summary>The target is the units of the node's army and group: the group has to have been spawned.</summary>
        public bool NeedsGroup;
    }

    public sealed class SC2MissionNodeDef
    {
        /// <summary>Set for the objective nodes written from a spec rather than by hand.</summary>
        public SC2MissionObjectiveSpec Objective;

        public string Type;
        public string Title;
        public string Category;

        /// <summary>The node only arms something (a timer, a trigger, an objective, a camera move) and returns at once; its outputs run
        /// later, from the game. A node that does not defer runs its outputs before it returns.</summary>
        public bool Defers;

        /// <summary>A frame with a label drawn behind other nodes: it has no inputs or outputs and writes no Lua.</summary>
        public bool IsComment;

        /// <summary>The execution outputs, in the order they are shown. Empty for a node that ends the chain.</summary>
        public string[] Ports;

        /// <summary>The execution inputs, in the order they are shown; null means the one input, "in". Each input runs its own Lua: the
        /// template of InputTemplates for its name, or Template.</summary>
        public string[] Inputs;

        /// <summary>When set, the outputs are "1", "2"... as many as this parameter says (the count of a list parameter, or a number), followed by ExtraPorts.</summary>
        public string CountedPortsParam;
        public string[] ExtraPorts;

        /// <summary>When set, the inputs are "1", "2"... as many as this number parameter says.</summary>
        public string CountedInputsParam;

        /// <summary>The Lua of the inputs that do not run the main Template, by input name.</summary>
        public System.Collections.Generic.Dictionary<string, string> InputTemplates;

        public SC2MissionParamDef[] Params;

        /// <summary>The Lua of a node that is a few lines around a call of the game's framework; see SC2MissionExtraNodes for the placeholders.
        /// Null for the nodes the compiler writes by hand.</summary>
        public string Template;
    }

    /// <summary>Every node type the graph knows. The compiler, the validator and the editor all read this table, so a new node starts here.</summary>
    public static class SC2MissionNodeCatalog
    {
        /// <summary>The name of the one input most nodes have.</summary>
        public const string DefaultInput = "in";

        /// <summary>How many outputs or inputs a node can have when their number is a parameter.</summary>
        public const int MaxCounted = 12;

        private static readonly string[] DefaultInputs = { DefaultInput };

        /// <summary>The inputs of a node, which for some node types depend on its parameters. node may be null: the defaults are used.</summary>
        public static string[] InputsOf(SC2MissionNodeDef def, SC2MissionNode node)
        {
            if (def == null) return DefaultInputs;
            if (def.CountedInputsParam != null) return Numbered(CountOf(def, node, def.CountedInputsParam), null);
            return def.Inputs ?? DefaultInputs;
        }

        /// <summary>The outputs of a node, which for some node types depend on its parameters. node may be null: the defaults are used.</summary>
        public static string[] PortsOf(SC2MissionNodeDef def, SC2MissionNode node)
        {
            if (def == null) return new string[0];
            if (def.CountedPortsParam != null) return Numbered(CountOf(def, node, def.CountedPortsParam), def.ExtraPorts);
            return def.Ports ?? new string[0];
        }

        private static string[] Numbered(int count, string[] extra)
        {
            var list = new System.Collections.Generic.List<string>();
            for (var i = 1; i <= count; i++) list.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (extra != null) list.AddRange(extra);
            return list.ToArray();
        }

        private static int CountOf(SC2MissionNodeDef def, SC2MissionNode node, string key)
        {
            var param = FindParam(def, key);
            var value = node?.GetParam(key, param?.Default ?? "") ?? param?.Default ?? "";
            if (param != null && (param.Kind == SC2MissionParamKind.NameList || param.Kind == SC2MissionParamKind.MarkerList || param.Kind == SC2MissionParamKind.ArmyList))
            {
                var items = 0;
                foreach (var part in value.Split(',')) if (part.Trim().Length > 0) items++;
                return System.Math.Max(1, System.Math.Min(MaxCounted, items));
            }
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? System.Math.Max(1, System.Math.Min(MaxCounted, n)) : 2;
        }

        public const string Timer = "timer";
        public const string AreaEntered = "area_entered";
        public const string UnitKilled = "unit_killed";
        public const string GroupKilled = "group_killed";
        public const string SpawnGroup = "spawn_group";
        public const string GiveOrder = "give_order";
        public const string ObjectiveKill = "objective_kill";
        public const string ObjectiveProtect = "objective_protect";
        public const string ObjectiveCapture = "objective_capture";
        public const string ObjectiveArea = "objective_area";
        public const string ObjectiveTimer = "objective_timer";
        public const string ObjectiveResolve = "objective_resolve";
        public const string ObjectiveKillOrCapture = "objective_kill_or_capture";
        public const string ObjectiveBasic = "objective_basic";
        public const string Dialogue = "dialogue";
        public const string CutsceneStart = "cutscene_start";
        public const string CameraMove = "camera_move";
        public const string CutsceneEnd = "cutscene_end";
        public const string SetVariable = "set_variable";
        public const string Branch = "branch";
        public const string CoopBranch = "coop_branch";
        public const string BaseStart = "base_start";
        public const string OpAISet = "opai_set";
        public const string AdvanceStage = "advance_stage";
        public const string Victory = "victory";
        public const string Defeat = "defeat";
        public const string RawLua = "raw_lua";

        /// <summary>The OpAI types the game ships (lua/AI/campaign/OpAI), for the Bases tab.</summary>
        public static readonly string[] OpAITypes =
        {
            "AirAttackCybran", "AirAttackIlluminate", "AirAttackUEF",
            "AirFightersCybran", "AirFightersIlluminate", "AirFightersUEF",
            "AirResponsePatrolAir", "AirResponsePatrolLand", "AirResponseTargetAir", "AirResponseTargetCybPower",
            "AirResponseTargetLand", "AirResponseTargetMassFab",
            "LandAttackCybran", "LandAttackIlluminate", "LandAttackUEF",
            "NavalAttackCybran", "NavalAttackUEF",
            "SingleCybranExperimentals", "SingleIlluminateExperimentals", "SingleUEFExperimentals",
            "TransportsCybran", "TransportsIlluminate", "TransportsUEF",
        };

        /// <summary>The chain the game looks for to send a base's engineers patrolling: BaseManager takes it by this name when it is created.</summary>
        public static string EngineerChainName(string baseId) => baseId + "_EngineerChain";

        public static readonly string[] OpAIThreads = { SC2MissionOpAI.RandomRoute, SC2MissionOpAI.RandomizedPoints };
        public static readonly string[] CompareOps = { "==", "~=", ">", ">=", "<", "<=" };
        /// <summary>The comparisons the game's area objective understands (it has no "not equal").</summary>
        public static readonly string[] AreaCompareOps = { ">=", "<=", ">", "<", "==" };

        private static SC2MissionParamDef P(string key, string label, SC2MissionParamKind kind, string def = "", string[] choices = null) =>
            new() { Key = key, Label = label, Kind = kind, Default = def, Choices = choices };

        /// <summary>The kinds of parameter whose text is a Lua expression or a list that the compiler writes out as it is understood.</summary>
        public static bool IsList(SC2MissionParamKind kind) =>
            kind == SC2MissionParamKind.NameList || kind == SC2MissionParamKind.MarkerList || kind == SC2MissionParamKind.ArmyList;

        private static readonly string[] None = new string[0];
        private static readonly SC2MissionParamDef[] NoParams = new SC2MissionParamDef[0];
        private static readonly string[] ObjectiveKinds = { "primary", "secondary" };

        private static SC2MissionParamDef[] ObjectiveHeader(string title) => new[]
        {
            P("kind", "Kind", SC2MissionParamKind.Choice, "primary", ObjectiveKinds),
            P("title", "Title", SC2MissionParamKind.Text, title),
            P("description", "Description", SC2MissionParamKind.Text),
        };

        private static SC2MissionParamDef[] Concat(SC2MissionParamDef[] a, params SC2MissionParamDef[] b)
        {
            var all = new List<SC2MissionParamDef>(a);
            all.AddRange(b);
            return all.ToArray();
        }

        private static readonly SC2MissionNodeDef[] Core =
        {
            new() { Type = Timer, Title = "Timer", Category = "Events", Defers = true, Ports = new[] { "fired" },
                Params = new[] { P("seconds", "Seconds", SC2MissionParamKind.Number, "30") } },
            new() { Type = AreaEntered, Title = "Area entered", Category = "Events", Defers = true, Ports = new[] { "entered" },
                Params = new[]
                {
                    P("area", "Area", SC2MissionParamKind.Area),
                    P("army", "Army whose units count", SC2MissionParamKind.Army, "ARMY_1"),
                    P("categories", "Categories", SC2MissionParamKind.Categories, "categories.ALLUNITS"),
                    P("count", "Units needed", SC2MissionParamKind.Number, "1"),
                    P("once", "Only once", SC2MissionParamKind.Bool, "true"),
                } },
            new() { Type = UnitKilled, Title = "Unit killed", Category = "Events", Defers = true, Ports = new[] { "killed" },
                Params = new[] { P("army", "Army", SC2MissionParamKind.Army), P("unit", "Unit", SC2MissionParamKind.Unit) } },
            new() { Type = GroupKilled, Title = "Group killed", Category = "Events", Defers = true, Ports = new[] { "killed" },
                Params = new[] { P("army", "Army", SC2MissionParamKind.Army), P("group", "Group", SC2MissionParamKind.Group) } },

            new() { Type = ObjectiveKill, Title = "Objective: kill group", Category = "Objectives", Defers = true, Ports = new[] { "completed", "failed" },
                Params = Concat(ObjectiveHeader("Destroy the enemy"), P("army", "Army", SC2MissionParamKind.Army), P("group", "Group", SC2MissionParamKind.Group)) },
            new() { Type = ObjectiveProtect, Title = "Objective: protect group", Category = "Objectives", Defers = true, Ports = new[] { "completed", "failed" },
                Params = Concat(ObjectiveHeader("Protect the base"), P("army", "Army", SC2MissionParamKind.Army), P("group", "Group", SC2MissionParamKind.Group),
                    P("seconds", "Hold for (seconds)", SC2MissionParamKind.Number, "300")) },
            new() { Type = ObjectiveCapture, Title = "Objective: capture group", Category = "Objectives", Defers = true, Ports = new[] { "completed", "failed" },
                Params = Concat(ObjectiveHeader("Capture the structures"), P("army", "Army", SC2MissionParamKind.Army), P("group", "Group", SC2MissionParamKind.Group)) },
            new() { Type = ObjectiveArea, Title = "Objective: units in area", Category = "Objectives", Defers = true, Ports = new[] { "completed", "failed" },
                Params = Concat(ObjectiveHeader("Reach the area"), P("area", "Area", SC2MissionParamKind.Area),
                    P("army", "Army whose units count", SC2MissionParamKind.Army, "ARMY_1"),
                    P("categories", "Categories", SC2MissionParamKind.Categories, "categories.ALLUNITS"),
                    P("compare", "Compare", SC2MissionParamKind.Choice, ">=", AreaCompareOps),
                    P("count", "Count", SC2MissionParamKind.Number, "1")) },
            new() { Type = ObjectiveTimer, Title = "Objective: timer", Category = "Objectives", Defers = true, Ports = new[] { "completed", "failed" },
                Params = Concat(ObjectiveHeader("Survive"), P("seconds", "Seconds", SC2MissionParamKind.Number, "300"),
                    P("result", "When it runs out", SC2MissionParamKind.Choice, "complete", new[] { "complete", "failed" })) },
            new() { Type = ObjectiveResolve, Title = "Resolve objective", Category = "Objectives", Ports = new[] { "out" },
                Params = new[]
                {
                    P("node", "Objective node (#)", SC2MissionParamKind.Number, "1"),
                    P("result", "Result", SC2MissionParamKind.Choice, "complete", new[] { "complete", "failed" }),
                } },

            new() { Type = SpawnGroup, Title = "Spawn group", Category = "Actions", Ports = new[] { "out" },
                Params = new[] { P("army", "Army", SC2MissionParamKind.Army), P("group", "Group", SC2MissionParamKind.Group) } },
            new() { Type = GiveOrder, Title = "Give order", Category = "Actions", Ports = new[] { "out" },
                Params = new[]
                {
                    P("army", "Army", SC2MissionParamKind.Army), P("group", "Group", SC2MissionParamKind.Group),
                    P("order", "Order", SC2MissionParamKind.Choice, "patrol", new[] { "patrol", "move", "attack-move" }),
                    P("chain", "Chain", SC2MissionParamKind.Chain),
                } },
            new() { Type = Dialogue, Title = "Dialogue", Category = "Actions", Defers = true, Ports = new[] { "done" },
                Params = new[]
                {
                    P("speaker", "Speaker", SC2MissionParamKind.Text),
                    P("text", "Text", SC2MissionParamKind.Text),
                    P("seconds", "Shown for (seconds)", SC2MissionParamKind.Number, "5"),
                    P("video", "Video file (optional)", SC2MissionParamKind.Text),
                    P("bank", "Voice bank (optional)", SC2MissionParamKind.Text),
                    P("cue", "Voice cue (optional)", SC2MissionParamKind.Text),
                } },

            new() { Type = CutsceneStart, Title = "Simple cutscene: begin", Category = "Cutscene (simple)", Ports = new[] { "out" }, Params = NoParams },
            new() { Type = CameraMove, Title = "Simple cutscene: camera move", Category = "Cutscene (simple)", Defers = true, Ports = new[] { "done" },
                Params = new[] { P("marker", "Marker", SC2MissionParamKind.Marker), P("seconds", "Seconds", SC2MissionParamKind.Number, "3") } },
            new() { Type = CutsceneEnd, Title = "Simple cutscene: end", Category = "Cutscene (simple)", Ports = new[] { "out" }, Params = NoParams },

            new() { Type = SetVariable, Title = "Set variable", Category = "Flow", Ports = new[] { "out" },
                Params = new[]
                {
                    P("variable", "Variable", SC2MissionParamKind.Variable),
                    P("op", "Operation", SC2MissionParamKind.Choice, "set", new[] { "set", "add" }),
                    P("value", "Value", SC2MissionParamKind.Text, "1"),
                } },
            new() { Type = Branch, Title = "Branch on variable", Category = "Flow", Ports = new[] { "true", "false" },
                Params = new[]
                {
                    P("variable", "Variable", SC2MissionParamKind.Variable),
                    P("compare", "Compare", SC2MissionParamKind.Choice, "==", CompareOps),
                    P("value", "Value", SC2MissionParamKind.Text, "1"),
                } },
            new() { Type = AdvanceStage, Title = "Advance to stage", Category = "Flow", Ports = None,
                Params = new[] { P("stage", "Stage", SC2MissionParamKind.Stage) } },
            new() { Type = Victory, Title = "Victory", Category = "Flow", Ports = None, Params = NoParams },
            new() { Type = Defeat, Title = "Defeat", Category = "Flow", Ports = None, Params = NoParams },

            new() { Type = CoopBranch, Title = "Branch: solo or co-op", Category = "Coop", Ports = new[] { "solo", "coop" }, Params = NoParams },

            new() { Type = BaseStart, Title = "Start base", Category = "Bases", Ports = new[] { "out" },
                Params = new[] { P("base", "Base", SC2MissionParamKind.Base) } },
            new() { Type = OpAISet, Title = "Enable / disable OpAI", Category = "Bases", Ports = new[] { "out" },
                Params = new[]
                {
                    P("base", "Base", SC2MissionParamKind.Base),
                    P("opai", "OpAI", SC2MissionParamKind.OpAI),
                    P("state", "State", SC2MissionParamKind.Choice, "enable", new[] { "enable", "disable" }),
                } },

            new() { Type = RawLua, Title = "Raw Lua", Category = "Raw Lua", Ports = new[] { "out" },
                Params = new[] { P("code", "Lua", SC2MissionParamKind.Code) } },
        };

        /// <summary>Every node, in the order the library shows them: the hand-written ones, then the template ones.</summary>
        private static SC2MissionNodeDef[] _all;

        public static SC2MissionNodeDef[] All => _all ??= Combine(Core, SC2MissionExtraNodes.Nodes);

        private static SC2MissionNodeDef[] Combine(SC2MissionNodeDef[] first, SC2MissionNodeDef[] second)
        {
            var all = new List<SC2MissionNodeDef>(first);
            all.AddRange(second);
            return all.ToArray();
        }

        public static SC2MissionNodeDef Find(string type)
        {
            foreach (var d in All)
                if (d.Type == type) return d;
            return null;
        }

        public static SC2MissionParamDef FindParam(SC2MissionNodeDef def, string key)
        {
            if (def == null) return null;
            foreach (var p in def.Params)
                if (p.Key == key) return p;
            return null;
        }

        /// <summary>The order the library lists the categories in; a category that is not here comes last.</summary>
        private static readonly string[] CategoryOrder =
        {
            "Events", "Objectives", "Units", "Actions", "World", "Economy", "Cutscene (NIS)", "Cutscene (simple)", "Bases", "Flow", "Loops", "Coop", "Layout", "Raw Lua",
        };

        public static IEnumerable<string> Categories()
        {
            var present = new HashSet<string>();
            foreach (var d in All) present.Add(d.Category);
            foreach (var c in CategoryOrder)
                if (present.Remove(c)) yield return c;
            foreach (var d in All)
                if (present.Remove(d.Category)) yield return d.Category;
        }

        /// <summary>True for the objective nodes, whose objects Resolve objective can reach.</summary>
        public static bool IsObjective(string type) =>
            type == ObjectiveKill || type == ObjectiveProtect || type == ObjectiveCapture || type == ObjectiveArea || type == ObjectiveTimer
            || type == ObjectiveKillOrCapture || type == ObjectiveBasic || Find(type)?.Objective != null;
    }
}
