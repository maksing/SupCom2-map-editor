using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SC2.MapEditor.Document
{
    public enum SC2MissionSeverity { Warning, Error }

    public sealed class SC2MissionIssue
    {
        public SC2MissionSeverity Severity;
        public string StageId = "";
        /// <summary>The node the issue is about, 0 when it concerns the stage or the whole mission.</summary>
        public int NodeId;
        public string Message = "";

        public override string ToString() =>
            (Severity == SC2MissionSeverity.Error ? "Error" : "Warning") + (StageId.Length > 0 ? " [" + StageId + (NodeId > 0 ? " #" + NodeId : "") + "]" : "") + ": " + Message;
    }

    /// <summary>What the map itself contains, so the validator can tell a name that is not there. Null lists are not checked.</summary>
    public sealed class SC2MissionMapInfo
    {
        public HashSet<string> Armies;
        public HashSet<string> Areas;
        /// <summary>The scenario's armies in the order the engine counts them: an army's index is its place here, from 1.</summary>
        public List<string> ArmyOrder;
        /// <summary>The Area that limits the playable region of the map, empty when none is chosen.</summary>
        public string PlayableArea = "";
        public HashSet<string> Markers;
        public HashSet<string> Chains;
        /// <summary>Unit group names by army.</summary>
        public Dictionary<string, HashSet<string>> Groups;
        /// <summary>Single unit names by army.</summary>
        public Dictionary<string, HashSet<string>> Units;
        /// <summary>How many units each group holds (nested groups included), by army and group name. Null when the counts are not known.</summary>
        public Dictionary<string, Dictionary<string, int>> GroupUnitCounts;
        /// <summary>How many units each army has on the map. Null when the counts are not known.</summary>
        public Dictionary<string, int> ArmyUnitCounts;
    }

    /// <summary>Checks a mission graph for what would break the generated Lua or the mission itself. Errors stop the generated script from
    /// being exported; warnings only describe a graph that is probably unfinished or points at something the map does not have.</summary>
    public static class SC2MissionValidator
    {
        private static readonly Regex StageIdPattern = new("^[a-z][a-z0-9_]*$");
        private static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$");
        private static readonly Regex CategoriesPattern = new(@"^categories\.[A-Za-z0-9_\.\s\+\-\*\(\)]+$");

        public static List<SC2MissionIssue> Validate(SC2MissionDocument doc, SC2MissionMapInfo map = null)
        {
            var issues = new List<SC2MissionIssue>();
            if (doc == null) return issues;

            if (doc.IsEmpty)
            {
                issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "The mission is empty."));
                return issues;
            }
            if (doc.Stages.Count == 0)
                issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "The mission has no stages."));

            ValidateSettings(doc, issues);
            ValidateArmies(doc, map, issues);
            ValidateVariables(doc, issues);
            ValidateBases(doc, map, issues);

            var seenStages = new HashSet<string>();
            var spawned = SpawnedGroups(doc);
            var advanced = AdvancedStages(doc);
            var hasEnding = false;
            for (var i = 0; i < doc.Stages.Count; i++)
            {
                var stage = doc.Stages[i];
                if (!StageIdPattern.IsMatch(stage.Id ?? ""))
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, 0, "Stage id '" + stage.Id + "' must be lower-case letters, digits and underscores, starting with a letter."));
                else if (!seenStages.Add(stage.Id))
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, 0, "Two stages are called '" + stage.Id + "'."));

                var started = SC2MissionCompiler.StartStage(doc);
                if (stage != started && !advanced.Contains(stage.Id))
                    issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, 0, "No Advance to stage node leads to this stage, so it never starts."));

                ValidateStage(doc, stage, spawned, map, issues, ref hasEnding);
            }

            if (doc.Stages.Count > 0 && !hasEnding)
                issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "No Victory or Defeat node: the mission can never end."));
            CheckCutsceneEnds(doc, issues);
            return issues;
        }

        private static readonly string[] CutsceneBeginTypes = { SC2MissionNodeCatalog.CutsceneStart, "nis_begin_standard", "nis_begin_opening", "nis_begin_midop", "nis_begin_victory" };
        private static readonly string[] CutsceneEndTypes = { SC2MissionNodeCatalog.CutsceneEnd, "nis_end_standard", "nis_end_opening", "nis_end_midop", "nis_end_norestore", "nis_end_victory" };

        /// <summary>A cutscene that is begun and cannot be ended leaves the player with locked input, black bars and a camera that does not come
        /// back. Each begin node needs a cutscene end that its wires can reach, in its own stage or - through Advance to stage - in a later one;
        /// an end that sits elsewhere in the mission does not help it.</summary>
        private static void CheckCutsceneEnds(SC2MissionDocument doc, List<SC2MissionIssue> issues)
        {
            foreach (var stage in doc.Stages)
                foreach (var node in stage.Nodes)
                    if (System.Array.IndexOf(CutsceneBeginTypes, node.Type) >= 0 && !CutsceneEndReachable(doc, stage, node))
                        issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "This cutscene is begun here, but no cutscene end node can be reached from it (through wires, or through Advance to stage): the player stays locked out of the game. Add a cutscene end after it."));
        }

        private static bool CutsceneEndReachable(SC2MissionDocument doc, SC2MissionStage firstStage, SC2MissionNode begin)
        {
            var seen = new HashSet<string> { firstStage.Id + "/" + begin.Id };
            var queue = new Queue<(SC2MissionStage stage, int node)>();
            queue.Enqueue((firstStage, begin.Id));
            while (queue.Count > 0)
            {
                var (stage, id) = queue.Dequeue();
                foreach (var edge in stage.Edges)
                {
                    if (edge.FromNode != id) continue;
                    var target = stage.FindNode(edge.ToNode);
                    if (target == null) continue;
                    if (System.Array.IndexOf(CutsceneEndTypes, target.Type) >= 0) return true;
                    if (seen.Add(stage.Id + "/" + target.Id)) queue.Enqueue((stage, target.Id));
                }
                var current = id == SC2MissionStage.StartNodeId ? null : stage.FindNode(id);
                if (current != null && current.Type == SC2MissionNodeCatalog.AdvanceStage)
                {
                    var next = doc.FindStage(current.GetParam("stage"));
                    if (next != null && seen.Add(next.Id + "/" + SC2MissionStage.StartNodeId)) queue.Enqueue((next, SC2MissionStage.StartNodeId));
                }
            }
            return false;
        }

        public static bool HasErrors(IEnumerable<SC2MissionIssue> issues)
        {
            foreach (var i in issues)
                if (i.Severity == SC2MissionSeverity.Error) return true;
            return false;
        }

        // ------------------------------------------------------------------ mission-wide

        private static void ValidateSettings(SC2MissionDocument doc, List<SC2MissionIssue> issues)
        {
            var s = doc.Settings;
            if (s.HumanSlots < 1 || s.HumanSlots > 4)
                issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Human slots must be 1 to 4."));
            if (s.FailWhen != SC2MissionSettings.FailAny && s.FailWhen != SC2MissionSettings.FailAll && s.FailWhen != SC2MissionSettings.FailNever)
                issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Fail when must be any, all or never."));
            if (!string.IsNullOrEmpty(s.TestStartStage))
            {
                if (doc.FindStage(s.TestStartStage) == null)
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "The test start stage '" + s.TestStartStage + "' does not exist."));
                else if (doc.Stages.Count > 0 && doc.Stages[0].Id != s.TestStartStage)
                    issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "Test start stage is set to '" + s.TestStartStage + "': the exported mission skips the stages before it. Clear it before release."));
            }
        }

        private static void ValidateArmies(SC2MissionDocument doc, SC2MissionMapInfo map, List<SC2MissionIssue> issues)
        {
            var seenAliases = new HashSet<string>();
            foreach (var e in SC2MissionArmies.Effective(doc, map))
            {
                var where = "Army " + e.Army.Name;
                if (!SC2MissionArmies.AliasPattern.IsMatch(e.Alias ?? ""))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the script name '" + e.Alias + "' must be letters, digits and underscores, starting with a letter."));
                else if (!seenAliases.Add(e.Alias))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the script name '" + e.Alias + "' is used by another army."));
                if (e.Army.Role != SC2MissionArmy.RolePlayer && e.Army.Role != SC2MissionArmy.RoleEnemy && e.Army.Role != SC2MissionArmy.RoleGeneric)
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": unknown role '" + e.Army.Role + "'."));
                if (e.Role != SC2MissionArmy.RolePlayer)
                    foreach (var color in e.Army.Colors)
                        if (!SC2MissionArmies.ColorPattern.IsMatch(color ?? ""))
                        {
                            issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": colours are eight hex digits (AARRGGBB), not '" + color + "'."));
                            break;
                        }
                if (e.Army.UnitCap < 1) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the unit cap must be at least 1."));
                if (e.Army.MassBoost < 0 || e.Army.EnergyBoost < 0 || e.Army.Mass < 0 || e.Army.Energy < 0 || e.Army.Research < 0)
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": resources and research cannot be negative."));
            }
            ValidateArmyUnits(doc, map, issues);
            if (map?.ArmyOrder != null && map.ArmyOrder.Count > 0)
                foreach (var a in doc.Armies)
                    if (!map.ArmyOrder.Contains(a.Name))
                        issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "Army " + a.Name + " has mission settings but is not on the map."));
        }

        /// <summary>An army with no units at all (no INITIAL group, nothing a Spawn group node creates) is not an error but is nearly always a
        /// mistake, and the OpAI of a base is silent against it: its builders only start when the target army has units.</summary>
        private static void ValidateArmyUnits(SC2MissionDocument doc, SC2MissionMapInfo map, List<SC2MissionIssue> issues)
        {
            if (map?.ArmyUnitCounts == null) return;
            var spawned = new HashSet<string>();
            foreach (var stage in doc.Stages)
                foreach (var node in stage.Nodes)
                    if (node.Type == SC2MissionNodeCatalog.SpawnGroup) spawned.Add(node.GetParam("army"));
            foreach (var b in doc.Bases)
                if (b.BuildGroups.Count > 0) spawned.Add(b.Army);

            foreach (var e in SC2MissionArmies.Effective(doc, map))
            {
                var name = e.Army.Name;
                map.ArmyUnitCounts.TryGetValue(name, out var count);
                if (count > 0 || spawned.Contains(name)) continue;
                if (e.Role == SC2MissionArmy.RolePlayer)
                    issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "Army " + name + " has no units on the map and no Spawn group node creates any: the player starts without a commander (its INITIAL group is empty)."));
                else
                    issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "Army " + name + " has no units on the map and nothing creates any."));
            }

            // The OpAI builders compare the army they attack (the first army) with a number of units: an army without units never qualifies.
            var target = map.ArmyOrder != null && map.ArmyOrder.Count > 0 ? map.ArmyOrder[0] : "ARMY_1";
            map.ArmyUnitCounts.TryGetValue(target, out var targetCount);
            if (targetCount == 0 && !spawned.Contains(target))
                foreach (var b in doc.Bases)
                    if (b.OpAIs.Count > 0)
                    {
                        issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, "Base '" + b.Id + "' has OpAI, but its target " + target +
                            " has no units: the attack waves are only built against an army that has units, so nothing will be built."));
                        break;
                    }
        }

        private static void ValidateVariables(SC2MissionDocument doc, List<SC2MissionIssue> issues)
        {
            var seen = new HashSet<string>();
            foreach (var v in doc.Variables)
            {
                if (!NamePattern.IsMatch(v.Name ?? ""))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Variable name '" + v.Name + "' must be letters, digits and underscores, not starting with a digit."));
                else if (!seen.Add(v.Name))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Two variables are called '" + v.Name + "'."));
                if (v.Type != SC2MissionVariable.Number && v.Type != SC2MissionVariable.Text && v.Type != SC2MissionVariable.Bool)
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Variable '" + v.Name + "' has the unknown type '" + v.Type + "'."));
                else if (!ValueFits(v.Type, v.Initial))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "The start value of variable '" + v.Name + "' is not a valid " + v.Type + "."));
            }
        }

        private static void ValidateBases(SC2MissionDocument doc, SC2MissionMapInfo map, List<SC2MissionIssue> issues)
        {
            var seenBases = new HashSet<string>();
            var seenOpAIs = new HashSet<string>();
            foreach (var b in doc.Bases)
            {
                var where = "Base '" + b.Id + "'";
                if (!NamePattern.IsMatch(b.Id ?? ""))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Base id '" + b.Id + "' must be letters, digits and underscores, not starting with a digit."));
                else if (!seenBases.Add(b.Id))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, "Two bases are called '" + b.Id + "'."));
                if (string.IsNullOrWhiteSpace(b.Army)) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the army is not set."));
                else if (map?.Armies != null && !map.Armies.Contains(b.Army)) issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, where + ": the map has no army '" + b.Army + "'."));
                if (string.IsNullOrWhiteSpace(b.Marker)) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the base marker is not set."));
                else if (map?.Markers != null && !map.Markers.Contains(b.Marker)) issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, where + ": the map has no marker '" + b.Marker + "'."));
                // A base that is built up from groups (a main base) has engineers walking a chain named after it. A base that starts empty
                // is an expansion: its engineers come from the main base and it has no chain of its own (so in Termesis).
                if (b.StartMode == SC2MissionBase.StartFilled && map?.Chains != null && !string.IsNullOrWhiteSpace(b.Id)
                    && !map.Chains.Contains(SC2MissionNodeCatalog.EngineerChainName(b.Id)))
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the map has no chain '" + SC2MissionNodeCatalog.EngineerChainName(b.Id) +
                        "'. Every main base (start as filled) needs its engineer chain: add a chain with that name on the Objects tab (Chains)."));
                if (b.Radius <= 0) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the radius must be more than 0."));
                if (b.Engineers < 0) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the engineer count cannot be negative."));
                if (b.StartMode != SC2MissionBase.StartFilled && b.StartMode != SC2MissionBase.StartEmpty)
                    issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": unknown start mode '" + b.StartMode + "'."));
                foreach (var g in b.BuildGroups)
                {
                    if (string.IsNullOrWhiteSpace(g.Group)) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": a build group has no name."));
                    else if (map?.Groups != null && !GroupExists(map, b.Army, g.Group)) issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, where + ": " + b.Army + " has no group '" + g.Group + "'."));
                    else if (GroupIsEmpty(map, b.Army, g.Group)) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, where + ": the group '" + g.Group + "' of " + b.Army + " has no units, so the base would have nothing to build from."));
                }
                foreach (var o in b.OpAIs)
                {
                    var opaiWhere = where + ", OpAI '" + o.Name + "'";
                    if (!NamePattern.IsMatch(o.Name ?? "")) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": the name must be letters, digits and underscores."));
                    else if (!seenOpAIs.Add(o.Name)) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": another OpAI already has this name."));
                    if (System.Array.IndexOf(SC2MissionNodeCatalog.OpAITypes, o.Type) < 0) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": unknown OpAI type '" + o.Type + "'."));
                    if (System.Array.IndexOf(SC2MissionNodeCatalog.OpAIThreads, o.Thread) < 0) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": unknown patrol behaviour '" + o.Thread + "'."));
                    if (o.Chains.Count == 0) issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, opaiWhere + ": no patrol chain, its platoons will stay at the base."));
                    foreach (var c in o.Chains)
                        if (string.IsNullOrWhiteSpace(c)) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": a chain has no name."));
                        else if (map?.Chains != null && !map.Chains.Contains(c)) issues.Add(Issue(SC2MissionSeverity.Warning, "", 0, opaiWhere + ": the map has no chain '" + c + "'."));
                    if (o.MaxPlatoons < 1) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": max platoons must be at least 1."));
                    if (o.ChildCount < 0 || o.AttackDelay < 0) issues.Add(Issue(SC2MissionSeverity.Error, "", 0, opaiWhere + ": child count and attack delay cannot be negative."));
                }
            }
        }

        // ------------------------------------------------------------------ stages

        private static void ValidateStage(SC2MissionDocument doc, SC2MissionStage stage, HashSet<string> spawned, SC2MissionMapInfo map, List<SC2MissionIssue> issues, ref bool hasEnding)
        {
            var ids = new HashSet<int>();
            foreach (var node in stage.Nodes)
            {
                if (node.Id <= SC2MissionStage.StartNodeId || !ids.Add(node.Id))
                {
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Node id " + node.Id + " is not unique."));
                    continue;
                }
                var def = SC2MissionNodeCatalog.Find(node.Type);
                if (def == null)
                {
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Unknown node type '" + node.Type + "'."));
                    continue;
                }
                if (node.Type == SC2MissionNodeCatalog.Victory || node.Type == SC2MissionNodeCatalog.Defeat) hasEnding = true;
                ValidateParams(doc, stage, node, def, spawned, map, issues);
            }

            foreach (var edge in stage.Edges)
            {
                if (edge.FromNode == SC2MissionStage.StartNodeId)
                {
                    if (edge.FromPort != SC2MissionStage.StartPort)
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, 0, "The start of the stage has no output '" + edge.FromPort + "'."));
                }
                else
                {
                    var from = stage.FindNode(edge.FromNode);
                    var fromDef = from == null ? null : SC2MissionNodeCatalog.Find(from.Type);
                    if (from == null)
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, 0, "A wire starts at node " + edge.FromNode + ", which does not exist."));
                    else if (fromDef != null && System.Array.IndexOf(SC2MissionNodeCatalog.PortsOf(fromDef, from), edge.FromPort) < 0)
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, from.Id, "'" + fromDef.Title + "' has no output '" + edge.FromPort + "'."));
                }
                var to = stage.FindNode(edge.ToNode);
                if (to == null)
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, 0, "A wire ends at node " + edge.ToNode + ", which does not exist."));
                else
                {
                    var toDef = SC2MissionNodeCatalog.Find(to.Type);
                    if (toDef != null && System.Array.IndexOf(SC2MissionNodeCatalog.InputsOf(toDef, to), edge.ToPort ?? SC2MissionNodeCatalog.DefaultInput) < 0)
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, to.Id, "'" + toDef.Title + "' has no input '" + edge.ToPort + "'."));
                }
            }

            var reachable = Reachable(stage);
            var startUsed = false;
            foreach (var e in stage.Edges)
                if (e.FromNode == SC2MissionStage.StartNodeId) startUsed = true;
            if (!startUsed && stage.Nodes.Count > 0)
                issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, 0, "Nothing is connected to the start of the stage, so none of its nodes will run."));
            foreach (var node in stage.Nodes)
                if (startUsed && !reachable.Contains(node.Id) && !(SC2MissionNodeCatalog.Find(node.Type)?.IsComment ?? false))
                    issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "This node is not connected to the start of the stage and will never run."));

            CheckLoops(stage, issues);
            DetectImmediateCycle(stage, issues);
        }

        private static readonly string[] LoopEngineTypes = { "loop_repeat", "loop_while", "loop_foreach" };

        /// <summary>A loop that is moved on by its 'next' input needs a wire that leads back to it; a loop that moves itself must not have one. The
        /// nodes that work on the unit of a for-each loop must name a for-each loop of the same stage.</summary>
        private static void CheckLoops(SC2MissionStage stage, List<SC2MissionIssue> issues)
        {
            foreach (var node in stage.Nodes)
            {
                var def = SC2MissionNodeCatalog.Find(node.Type);
                if (def == null || def.Category != "Loops") continue;

                if (System.Array.IndexOf(LoopEngineTypes, node.Type) >= 0)
                {
                    var auto = node.GetParam("auto", "false") == "true";
                    var nextWires = stage.Edges.FindAll(e => e.ToNode == node.Id && e.ToPort == "next").Count;
                    if (!auto && nextWires == 0)
                        issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "Nothing is wired to 'next', so only the first round runs. Wire the end of the body to 'next', or switch on 'Next round by itself'."));
                    else if (auto && nextWires > 0)
                        issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "'Next round by itself' is on, so the wires to 'next' are not used."));
                }

                var bodyPort = def.Ports.Length > 0 ? def.Ports[0] : "";
                if ((System.Array.IndexOf(LoopEngineTypes, node.Type) >= 0 || node.Type == "loop_forever") && bodyPort.Length > 0 && !stage.Edges.Exists(e => e.FromNode == node.Id && e.FromPort == bodyPort))
                    issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "Nothing is wired to '" + bodyPort + "': the loop runs but does nothing."));

                var loopParam = SC2MissionNodeCatalog.FindParam(def, "loop");
                if (loopParam != null)
                {
                    var target = int.TryParse(node.GetParam("loop"), out var id) ? stage.FindNode(id) : null;
                    if (target == null || target.Type != "loop_foreach")
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "'Loop node number' must be the number of a 'For each unit in a group' node of this stage."));
                }
            }
        }

        private static void ValidateParams(SC2MissionDocument doc, SC2MissionStage stage, SC2MissionNode node, SC2MissionNodeDef def, HashSet<string> spawned, SC2MissionMapInfo map, List<SC2MissionIssue> issues)
        {
            foreach (var p in def.Params)
            {
                var value = node.GetParam(p.Key, p.Default);
                switch (p.Kind)
                {
                    case SC2MissionParamKind.Number:
                        ValidateNumber(doc, stage, node, p, value, issues);
                        break;
                    case SC2MissionParamKind.NameList:
                    case SC2MissionParamKind.MarkerList:
                    case SC2MissionParamKind.ArmyList:
                        ValidateList(stage, node, p, value, map, issues);
                        break;
                    case SC2MissionParamKind.Army:
                        if (Required(stage, node, p, value, issues) && map?.Armies != null && !map.Armies.Contains(value))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "The map has no army '" + value + "'."));
                        break;
                    case SC2MissionParamKind.Area:
                        if (Required(stage, node, p, value, issues) && map?.Areas != null && !map.Areas.Contains(value))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "The map has no area '" + value + "'."));
                        break;
                    case SC2MissionParamKind.Marker:
                        if (Required(stage, node, p, value, issues) && map?.Markers != null && !map.Markers.Contains(value))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "The map has no marker '" + value + "'."));
                        break;
                    case SC2MissionParamKind.Chain:
                        if (Required(stage, node, p, value, issues) && map?.Chains != null && !map.Chains.Contains(value))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "The map has no chain '" + value + "'."));
                        break;
                    case SC2MissionParamKind.Group:
                        if (Required(stage, node, p, value, issues) && map?.Groups != null && !GroupExists(map, node.GetParam("army"), value))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, node.GetParam("army") + " has no group '" + value + "'."));
                        else if (!string.IsNullOrWhiteSpace(value) && GroupIsEmpty(map, node.GetParam("army"), value))
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "The group '" + value + "' of " + node.GetParam("army") + " has no units: the game raises an error when it creates an empty group."));
                        break;
                    case SC2MissionParamKind.Unit:
                        if (Required(stage, node, p, value, issues) && map?.Units != null
                            && !(map.Units.TryGetValue(node.GetParam("army"), out var units) && units.Contains(value)))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, node.GetParam("army") + " has no unit '" + value + "'."));
                        break;
                    case SC2MissionParamKind.Categories:
                        if (!CategoriesPattern.IsMatch(value ?? "") || value.Contains("--"))
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, p.Label + " must be a categories expression such as categories.ALLUNITS."));
                        break;
                    case SC2MissionParamKind.Stage:
                        if (doc.FindStage(value) == null)
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Stage '" + value + "' does not exist."));
                        break;
                    case SC2MissionParamKind.Variable:
                        if (p.Optional && string.IsNullOrEmpty(value)) break;
                        if (doc.FindVariable(value) == null)
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Variable '" + value + "' does not exist (Variables tab)."));
                        break;
                    case SC2MissionParamKind.Base:
                        if (doc.FindBase(value) == null)
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Base '" + value + "' does not exist (Bases tab)."));
                        break;
                    case SC2MissionParamKind.OpAI:
                        var ownerBase = doc.FindBase(node.GetParam("base"));
                        if (ownerBase != null && ownerBase.OpAIs.Find(o => o.Name == value) == null)
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Base '" + ownerBase.Id + "' has no OpAI '" + value + "'."));
                        break;
                    case SC2MissionParamKind.Choice:
                        if (System.Array.IndexOf(p.Choices, value) < 0)
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, p.Label + " must be one of: " + string.Join(", ", p.Choices) + "."));
                        break;
                    case SC2MissionParamKind.Code:
                        if ((value ?? "").Contains("SCED:"))
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Raw Lua may not contain the text 'SCED:' (it fences the generated regions)."));
                        break;
                    case SC2MissionParamKind.Text:
                        if (string.IsNullOrWhiteSpace(value) && (p.Key == "title" || p.Key == "text"))
                            issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, p.Label + " is empty."));
                        break;
                }
            }

            switch (node.Type)
            {
                case SC2MissionNodeCatalog.GroupKilled:
                case SC2MissionNodeCatalog.ObjectiveKill:
                case SC2MissionNodeCatalog.ObjectiveProtect:
                case SC2MissionNodeCatalog.ObjectiveCapture:
                case SC2MissionNodeCatalog.GiveOrder:
                {
                    var army = node.GetParam("army");
                    var group = node.GetParam("group");
                    if (army.Length > 0 && group.Length > 0 && !spawned.Contains(army + "/" + group))
                        issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "No Spawn group node creates " + army + "/" + group + "; this only works if the group already exists when the node runs."));
                    break;
                }
                case SC2MissionNodeCatalog.ObjectiveResolve:
                {
                    int.TryParse(node.GetParam("node"), NumberStyles.None, CultureInfo.InvariantCulture, out var target);
                    var targetNode = stage.FindNode(target);
                    if (targetNode == null || !SC2MissionNodeCatalog.IsObjective(targetNode.Type))
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Node #" + node.GetParam("node") + " of this stage is not an objective."));
                    break;
                }
                case SC2MissionNodeCatalog.SetVariable:
                {
                    var variable = doc.FindVariable(node.GetParam("variable"));
                    if (variable != null)
                    {
                        if (node.GetParam("op") == "add" && variable.Type != SC2MissionVariable.Number)
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Add only works on number variables."));
                        if (!ValueFits(variable.Type, node.GetParam("value")))
                            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "The value is not a valid " + variable.Type + "."));
                    }
                    break;
                }
                case SC2MissionNodeCatalog.Branch:
                {
                    var variable = doc.FindVariable(node.GetParam("variable"));
                    if (variable != null && !ValueFits(variable.Type, node.GetParam("value")))
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "The value is not a valid " + variable.Type + "."));
                    if (variable != null && variable.Type != SC2MissionVariable.Number && node.GetParam("compare") != "==" && node.GetParam("compare") != "~=")
                        issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, "Text and bool variables can only be compared with == or ~=."));
                    break;
                }
                case SC2MissionNodeCatalog.CoopBranch:
                    if (!doc.Settings.Coop)
                        issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "Co-op is switched off in the mission settings; this node always takes the solo output."));
                    break;
            }
        }

        private static bool Required(SC2MissionStage stage, SC2MissionNode node, SC2MissionParamDef p, string value, List<SC2MissionIssue> issues)
        {
            if (!string.IsNullOrWhiteSpace(value)) return true;
            if (p.Optional) return false;
            issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, p.Label + " is not set."));
            return false;
        }

        private static void ValidateList(SC2MissionStage stage, SC2MissionNode node, SC2MissionParamDef p, string value, SC2MissionMapInfo map, List<SC2MissionIssue> issues)
        {
            var items = new List<string>();
            foreach (var part in (value ?? "").Split(','))
                if (part.Trim().Length > 0) items.Add(part.Trim());
            if (items.Count == 0)
            {
                if (!p.Optional) issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, p.Label + " is empty."));
                return;
            }
            foreach (var item in items)
            {
                if (item.Contains("'") || item.Contains("\"") || item.Contains("\\"))
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, p.Label + ": '" + item + "' has a quote or backslash in it."));
                else if (p.Kind == SC2MissionParamKind.MarkerList && map?.Markers != null && !map.Markers.Contains(item))
                    issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "The map has no marker '" + item + "'."));
                else if (p.Kind == SC2MissionParamKind.ArmyList && map?.Armies != null && !map.Armies.Contains(item))
                    issues.Add(Issue(SC2MissionSeverity.Warning, stage.Id, node.Id, "The map has no army '" + item + "'."));
            }
        }

        private static void ValidateNumber(SC2MissionDocument doc, SC2MissionStage stage, SC2MissionNode node, SC2MissionParamDef p, string value, List<SC2MissionIssue> issues)
        {
            var label = p.Label;
            if (value != null && value.StartsWith("$"))
            {
                var variable = doc.FindVariable(value.Substring(1));
                if (variable == null) issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, label + ": variable '" + value.Substring(1) + "' does not exist."));
                else if (variable.Type != SC2MissionVariable.Number) issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, label + ": variable '" + variable.Name + "' is not a number."));
                return;
            }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || (number < 0 && !p.Signed))
                issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, node.Id, label + (p.Signed ? " must be a number." : " must be a number of zero or more.")));
        }

        private static bool ValueFits(string type, string text)
        {
            if (text != null && text.StartsWith("$")) return true;
            switch (type)
            {
                case SC2MissionVariable.Number: return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                case SC2MissionVariable.Bool: return text == "true" || text == "false";
                default: return true;
            }
        }

        /// <summary>True when the map says the group exists and holds no units.</summary>
        private static bool GroupIsEmpty(SC2MissionMapInfo map, string army, string group) =>
            map?.GroupUnitCounts != null && map.Groups != null && GroupExists(map, army, group)
            && !(map.GroupUnitCounts.TryGetValue(army ?? "", out var counts) && counts.TryGetValue(group, out var n) && n > 0);

        private static bool GroupExists(SC2MissionMapInfo map, string army, string group) =>
            map.Groups.TryGetValue(army ?? "", out var groups) && groups.Contains(group);

        private static HashSet<string> SpawnedGroups(SC2MissionDocument doc)
        {
            var set = new HashSet<string>();
            foreach (var stage in doc.Stages)
                foreach (var node in stage.Nodes)
                    if (node.Type == SC2MissionNodeCatalog.SpawnGroup)
                        set.Add(node.GetParam("army") + "/" + node.GetParam("group"));
            return set;
        }

        private static HashSet<string> AdvancedStages(SC2MissionDocument doc)
        {
            var set = new HashSet<string>();
            foreach (var stage in doc.Stages)
                foreach (var node in stage.Nodes)
                    if (node.Type == SC2MissionNodeCatalog.AdvanceStage)
                        set.Add(node.GetParam("stage"));
            return set;
        }

        private static HashSet<int> Reachable(SC2MissionStage stage)
        {
            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(SC2MissionStage.StartNodeId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var e in stage.Edges)
                    if (e.FromNode == current && seen.Add(e.ToNode)) queue.Enqueue(e.ToNode);
            }
            return seen;
        }

        /// <summary>A loop made only of nodes that run straight through (no timer, trigger or objective in it) would call itself without
        /// end in the game. A loop that passes an event node is fine: the event re-arms itself later.</summary>
        private static void DetectImmediateCycle(SC2MissionStage stage, List<SC2MissionIssue> issues)
        {
            var immediate = new HashSet<int>();
            foreach (var node in stage.Nodes)
            {
                var def = SC2MissionNodeCatalog.Find(node.Type);
                if (def != null && !def.Defers) immediate.Add(node.Id);
            }

            var state = new Dictionary<int, int>();
            foreach (var id in immediate)
                if (!state.ContainsKey(id) && Visit(stage, id, immediate, state))
                {
                    issues.Add(Issue(SC2MissionSeverity.Error, stage.Id, id, "These nodes call each other in a loop with no Timer, trigger or objective in it; the game would never leave the loop."));
                    return;
                }
        }

        private static bool Visit(SC2MissionStage stage, int id, HashSet<int> immediate, Dictionary<int, int> state)
        {
            state[id] = 1;
            foreach (var e in stage.Edges)
            {
                if (e.FromNode != id || !immediate.Contains(e.ToNode)) continue;
                state.TryGetValue(e.ToNode, out var s);
                if (s == 1) return true;
                if (s == 0 && Visit(stage, e.ToNode, immediate, state)) return true;
            }
            state[id] = 2;
            return false;
        }

        private static SC2MissionIssue Issue(SC2MissionSeverity severity, string stageId, int nodeId, string message) =>
            new() { Severity = severity, StageId = stageId ?? "", NodeId = nodeId, Message = message };
    }
}
