using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    public sealed class SC2MissionParam
    {
        public string Key = "";
        public string Value = "";
    }

    /// <summary>One block of the mission graph: a type from SC2MissionNodeCatalog, the literal values of its parameters and the place
    /// it has on the canvas.</summary>
    public sealed class SC2MissionNode
    {
        public int Id;
        public string Type = "";
        public float X;
        public float Y;
        public List<SC2MissionParam> Params = new();

        public string GetParam(string key, string fallback = "")
        {
            foreach (var p in Params)
                if (p.Key == key) return p.Value ?? fallback;
            return fallback;
        }

        public void SetParam(string key, string value)
        {
            foreach (var p in Params)
                if (p.Key == key) { p.Value = value ?? ""; return; }
            Params.Add(new SC2MissionParam { Key = key, Value = value ?? "" });
        }

        public SC2MissionNode Clone()
        {
            var copy = new SC2MissionNode { Id = Id, Type = Type, X = X, Y = Y };
            foreach (var p in Params) copy.Params.Add(new SC2MissionParam { Key = p.Key, Value = p.Value });
            return copy;
        }
    }

    /// <summary>An execution wire: when node FromNode finishes through its output FromPort, node ToNode runs. FromNode 0 is the start
    /// of the stage.</summary>
    public sealed class SC2MissionEdge
    {
        public int FromNode;
        public string FromPort = "";
        public int ToNode;
        /// <summary>The input of ToNode the wire ends at; most nodes have the one input, "in".</summary>
        public string ToPort = SC2MissionNodeCatalog.DefaultInput;

        public SC2MissionEdge Clone() => new() { FromNode = FromNode, FromPort = FromPort, ToNode = ToNode, ToPort = ToPort };
    }

    /// <summary>A tab of the graph (Intro, Stage 1, Outro...): its own set of nodes, started by a call to its Start function.</summary>
    public sealed class SC2MissionStage
    {
        /// <summary>The virtual node every stage starts from; edges leaving it are what runs when the stage begins.</summary>
        public const int StartNodeId = 0;
        public const string StartPort = "out";

        /// <summary>Lower-case letters, digits and underscores; part of the generated Lua function names.</summary>
        public string Id = "";
        public string Name = "";
        public List<SC2MissionNode> Nodes = new();
        public List<SC2MissionEdge> Edges = new();
        public int NextNodeId = 1;

        public SC2MissionNode FindNode(int id)
        {
            foreach (var n in Nodes)
                if (n.Id == id) return n;
            return null;
        }

        public SC2MissionNode AddNode(string type, float x, float y)
        {
            var node = new SC2MissionNode { Id = NextNodeId++, Type = type, X = x, Y = y };
            var def = SC2MissionNodeCatalog.Find(type);
            if (def != null)
                foreach (var p in def.Params) node.SetParam(p.Key, p.Default);
            // the nodes that work on the unit of a for-each loop start out pointing at the latest one of the stage
            if (def != null && SC2MissionNodeCatalog.FindParam(def, "loop") != null)
            {
                for (var i = Nodes.Count - 1; i >= 0; i--)
                    if (Nodes[i].Type == "loop_foreach") { node.SetParam("loop", Nodes[i].Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); break; }
            }
            Nodes.Add(node);
            return node;
        }

        /// <summary>Wires an output to an input; without toPort the wire ends at the first input of the node.</summary>
        public SC2MissionEdge Connect(int fromNode, string fromPort, int toNode, string toPort = null)
        {
            if (toPort == null)
            {
                var target = FindNode(toNode);
                var inputs = SC2MissionNodeCatalog.InputsOf(target == null ? null : SC2MissionNodeCatalog.Find(target.Type), target);
                toPort = inputs.Length > 0 ? inputs[0] : SC2MissionNodeCatalog.DefaultInput;
            }
            var edge = new SC2MissionEdge { FromNode = fromNode, FromPort = fromPort, ToNode = toNode, ToPort = toPort };
            Edges.Add(edge);
            return edge;
        }

        /// <summary>Removes a node with every wire that touches it.</summary>
        public void RemoveNode(int id)
        {
            Nodes.RemoveAll(n => n.Id == id);
            Edges.RemoveAll(e => e.FromNode == id || e.ToNode == id);
        }

        public SC2MissionStage Clone()
        {
            var copy = new SC2MissionStage { Id = Id, Name = Name, NextNodeId = NextNodeId };
            foreach (var n in Nodes) copy.Nodes.Add(n.Clone());
            foreach (var e in Edges) copy.Edges.Add(e.Clone());
            return copy;
        }
    }

    public sealed class SC2MissionVariable
    {
        public const string Number = "number";
        public const string Text = "text";
        public const string Bool = "bool";

        public string Name = "";
        public string Type = Number;
        public string Initial = "0";

        public SC2MissionVariable Clone() => new() { Name = Name, Type = Type, Initial = Initial };
    }

    /// <summary>Mission-wide switches: co-op, how the player slots fail the mission, and where a test run starts.</summary>
    public sealed class SC2MissionSettings
    {
        public const string FailAny = "any";
        public const string FailAll = "all";
        public const string FailNever = "never";

        public bool Coop;
        /// <summary>How many of the first armies (ARMY_1...) are played by humans, 1 to 4.</summary>
        public int HumanSlots = 1;
        public bool SharedCap;
        public string FailWhen = FailAny;
        /// <summary>Id of the stage the generated script starts from instead of the first one; empty for a normal run. For testing a stage
        /// without playing the ones before it.</summary>
        public string TestStartStage = "";

        public SC2MissionSettings Clone() => (SC2MissionSettings)MemberwiseClone();

        /// <summary>True while nothing has been changed from the defaults.</summary>
        public bool IsDefault => !Coop && HumanSlots == 1 && !SharedCap && FailWhen == FailAny && string.IsNullOrEmpty(TestStartStage);
    }

    /// <summary>
    /// How one army of the map is set up when the mission starts: the name the script knows it by, its colours and its starting resources
    /// and unit cap (the game's own SetupPlayerArmy / SetupNonPlayerArmy / SetupGenericArmy tables). Its place in the map's army list is the
    /// army index the engine uses, so it is not stored here.
    /// </summary>
    public sealed class SC2MissionArmy
    {
        public const string RolePlayer = "player";
        public const string RoleEnemy = "enemy";
        public const string RoleGeneric = "generic";

        /// <summary>The army as the scenario names it (ARMY_1...).</summary>
        public string Name = "";
        /// <summary>The short name the script gives it: ScenarioInfo.ARMY_&lt;Alias&gt; holds its index.</summary>
        public string Alias = "";
        public string Role = RoleEnemy;
        /// <summary>Three ARGB colours (AARRGGBB) the game gives an army: primary, secondary, tertiary. Unused by a player army.</summary>
        public string[] Colors = { "FF00A2BE", "FFFFFFFF", "FF00A2BE" };
        public int UnitCap = 1000;
        /// <summary>Resources an enemy or generic army is given at the start (the AI lives on them).</summary>
        public long MassBoost = 9999999;
        public long EnergyBoost = 9999999;
        /// <summary>Starting resources and research of a player army.</summary>
        public int Mass = 2500;
        public int Energy = 6500;
        public int Research = 10;
        /// <summary>Unlock every unit of the army's research tree when the mission loads. Combat units are locked behind research until then,
        /// so an AI army that is not unlocked builds its engineers and nothing that fights (the Termesis mission unlocks all of its AI armies).</summary>
        public bool UnlockResearch = true;

        public SC2MissionArmy Clone()
        {
            var copy = (SC2MissionArmy)MemberwiseClone();
            copy.Colors = (string[])Colors.Clone();
            return copy;
        }
    }

    public sealed class SC2MissionBuildGroup
    {
        public string Group = "";
        public int Priority = 100;

        public SC2MissionBuildGroup Clone() => new() { Group = Group, Priority = Priority };
    }

    /// <summary>An attack/defence wave generator of a base: one of the game's OpAI types plus the chains it patrols.</summary>
    public sealed class SC2MissionOpAI
    {
        public const string RandomRoute = "PatrolRandomRoute";
        public const string RandomizedPoints = "PatrolRandomizedPoints";

        public string Name = "";
        public string Type = "LandAttackUEF";
        public string Thread = RandomRoute;
        public List<string> Chains = new();
        public int ChildCount;
        public int MaxPlatoons = 1;
        public int AttackDelay;
        public bool Enabled = true;

        public SC2MissionOpAI Clone()
        {
            var copy = (SC2MissionOpAI)MemberwiseClone();
            copy.Chains = new List<string>(Chains);
            return copy;
        }
    }

    /// <summary>A campaign-AI base (BaseManager) of one army: where it stands, which unit groups it builds up from and which OpAI it runs.</summary>
    public sealed class SC2MissionBase
    {
        public const string StartFilled = "filled";
        public const string StartEmpty = "empty";

        public string Id = "";
        public string Army = "";
        public string Marker = "";
        public int Radius = 50;
        public List<SC2MissionBuildGroup> BuildGroups = new();
        public int Engineers = 2;
        public string StartMode = StartFilled;
        /// <summary>Start the base when the mission begins; otherwise a Start base node does.</summary>
        public bool StartAtBegin = true;
        public List<SC2MissionOpAI> OpAIs = new();

        public SC2MissionBase Clone()
        {
            var copy = (SC2MissionBase)MemberwiseClone();
            copy.BuildGroups = new List<SC2MissionBuildGroup>();
            foreach (var g in BuildGroups) copy.BuildGroups.Add(g.Clone());
            copy.OpAIs = new List<SC2MissionOpAI>();
            foreach (var o in OpAIs) copy.OpAIs.Add(o.Clone());
            return copy;
        }
    }

    /// <summary>
    /// The mission graph of a map: the source of the generated _script.lua when Enabled. Lives in the Scenario domain and is saved
    /// with the map; nothing here knows about Lua, see SC2MissionCompiler for that.
    /// </summary>
    public sealed class SC2MissionDocument
    {
        /// <summary>Export writes the Lua generated from the graph instead of the plain script. Off keeps the graph as a draft.</summary>
        public bool Enabled;
        public string Title = "";
        public List<SC2MissionStage> Stages = new();
        public List<SC2MissionVariable> Variables = new();
        public List<SC2MissionBase> Bases = new();
        /// <summary>Per-army tuning the user changed; an army of the map that is not here uses SC2MissionArmies' defaults.</summary>
        public List<SC2MissionArmy> Armies = new();
        public SC2MissionSettings Settings = new();

        /// <summary>Nothing the user entered: no stages, bases, variables or army tuning, default settings and no title. A graph that only has
        /// army or co-op settings is not empty - its export is the campaign script with the army block, without a flow.</summary>
        public bool IsEmpty => Stages.Count == 0 && Bases.Count == 0 && Variables.Count == 0 && Armies.Count == 0 && Settings.IsDefault && string.IsNullOrEmpty(Title);

        public SC2MissionStage FindStage(string id)
        {
            foreach (var s in Stages)
                if (s.Id == id) return s;
            return null;
        }

        public SC2MissionVariable FindVariable(string name)
        {
            foreach (var v in Variables)
                if (v.Name == name) return v;
            return null;
        }

        public SC2MissionBase FindBase(string id)
        {
            foreach (var b in Bases)
                if (b.Id == id) return b;
            return null;
        }

        public SC2MissionDocument Clone()
        {
            var copy = new SC2MissionDocument();
            copy.CopyFrom(this);
            return copy;
        }

        public void CopyFrom(SC2MissionDocument other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            Enabled = other.Enabled;
            Title = other.Title;
            Stages = new List<SC2MissionStage>();
            foreach (var s in other.Stages) Stages.Add(s.Clone());
            Variables = new List<SC2MissionVariable>();
            foreach (var v in other.Variables) Variables.Add(v.Clone());
            Bases = new List<SC2MissionBase>();
            foreach (var b in other.Bases) Bases.Add(b.Clone());
            Armies = new List<SC2MissionArmy>();
            foreach (var army in other.Armies) Armies.Add(army.Clone());
            Settings = other.Settings.Clone();
        }
    }
}
