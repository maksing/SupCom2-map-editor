using System;
using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

public sealed partial class SC2MissionPanel
{
    private int _baseSel;
    private string _confirmDeleteBase = "";

    private static readonly string[] VariableTypes = { SC2MissionVariable.Number, SC2MissionVariable.Text, SC2MissionVariable.Bool };
    private static readonly string[] FailChoices = { SC2MissionSettings.FailAny, SC2MissionSettings.FailAll, SC2MissionSettings.FailNever };
    private static readonly string[] SlotChoices = { "1", "2", "3", "4" };
    private static readonly string[] StartModes = { SC2MissionBase.StartFilled, SC2MissionBase.StartEmpty };

    private Rect FormArea(Rect body) => new(body.x + S(4f), body.y + S(4f), body.width - S(8f), body.height - S(8f));

    // ------------------------------------------------------------------ variables

    private void DrawVariablesForm(Rect body)
    {
        GUI.Box(body, GUIContent.none);
        GUILayout.BeginArea(FormArea(body));
        GUILayout.Label("Variables", _titleStyle);
        GUILayout.Label("Values the mission remembers while it runs. Nodes set them (Set variable), test them (Branch on variable) and read numbers from them: type $name in a number field.", _noteStyle);
        GUILayout.Space(S(6f));
        _formScroll = GUILayout.BeginScrollView(_formScroll);

        for (var i = 0; i < _work.Variables.Count; i++)
        {
            var v = _work.Variables[i];
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(S(220f)));
            var name = v.Name;
            if (TextEdit("Name", ref name, "var_name_" + i, "Rename variable")) v.Name = name;
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(S(140f)));
            var type = v.Type;
            if (ChoiceEdit("Type", ref type, VariableTypes, false, "Change variable type"))
            {
                v.Type = type;
                v.Initial = type == SC2MissionVariable.Bool ? "false" : type == SC2MissionVariable.Number ? "0" : "";
            }
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(S(220f)));
            if (v.Type == SC2MissionVariable.Bool)
            {
                GUILayout.Label("Start value", _fieldLabelStyle);
                var on = v.Initial == "true";
                if (BoolEdit("starts as true", ref on, "Edit start value")) v.Initial = on ? "true" : "false";
            }
            else
            {
                var initial = v.Initial;
                if (TextEdit("Start value", ref initial, "var_init_" + i, "Edit start value")) v.Initial = initial;
            }
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.Space(S(18f));
            if (GUILayout.Button("Delete", GUILayout.Width(S(80f)), GUILayout.Height(S(22f))))
            {
                Commit();
                _work.Variables.RemoveAt(i);
                Mutate("Delete variable");
                Commit();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(S(4f));
        }

        if (GUILayout.Button("+ Add variable", GUILayout.Width(S(160f)), GUILayout.Height(S(26f))))
        {
            Commit();
            var n = _work.Variables.Count + 1;
            while (_work.FindVariable("variable" + n) != null) n++;
            _work.Variables.Add(new SC2MissionVariable { Name = "variable" + n });
            Mutate("Add variable");
            Commit();
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // ------------------------------------------------------------------ settings

    private void DrawSettingsForm(Rect body)
    {
        GUI.Box(body, GUIContent.none);
        GUILayout.BeginArea(FormArea(body));
        _formScroll = GUILayout.BeginScrollView(_formScroll);
        GUILayout.BeginVertical(GUILayout.MaxWidth(S(620f)));
        var settings = _work.Settings;

        GUILayout.Label("Mission", _titleStyle);
        var title = _work.Title;
        if (TextEdit("Title", ref title, "mission_title", "Rename mission")) _work.Title = title;

        GUILayout.Space(S(10f));
        GUILayout.Label("Co-op", _titleStyle);
        GUILayout.Label("A co-op mission is played by several humans on the first armies. The script can then fail the mission when players lose their commanders and choose whether they share one unit cap.", _noteStyle);
        var coop = settings.Coop;
        if (BoolEdit("Co-op mission", ref coop, "Toggle co-op")) settings.Coop = coop;
        if (settings.Coop)
        {
            var slots = settings.HumanSlots.ToString();
            if (ChoiceEdit("Human slots (ARMY_1 ...)", ref slots, SlotChoices, false, "Set human slots")) settings.HumanSlots = int.Parse(slots);
            var shared = settings.SharedCap;
            if (BoolEdit("Players share one unit cap", ref shared, "Toggle shared cap")) settings.SharedCap = shared;
            var failWhen = settings.FailWhen;
            if (ChoiceEdit("Fail the mission when (a player loses the commander)", ref failWhen, FailChoices, false, "Set fail condition")) settings.FailWhen = failWhen;
            GUILayout.Label("any = one player is enough to fail,  all = every player must lose,  never = the script does not watch.", _noteStyle);
        }

        GUILayout.Space(S(10f));
        GUILayout.Label("Armies", _titleStyle);
        GUILayout.Label("How each army of the map is set up when the mission loads: its name in the script, colours, unit cap and starting resources. The first armies are the human slots; the others are the enemies.", _noteStyle);
        foreach (var entry in SC2MissionArmies.Effective(_work, _mapInfo)) DrawArmyEditor(entry);

        GUILayout.Space(S(10f));
        GUILayout.Label("Testing", _titleStyle);
        var test = settings.TestStartStage;
        if (ChoiceEdit("Start the mission at stage (for testing)", ref test, _work.Stages.Select(s => s.Id).ToList(), true, "Set test start stage")) settings.TestStartStage = test;
        GUILayout.Label("Export, then start the map in the game: the mission begins at that stage and skips the ones before it. Choose (none) for a normal run.", _noteStyle);

        GUILayout.Space(S(10f));
        GUILayout.Label("Samples", _titleStyle);
        GUILayout.Label("Replace the whole mission (undoable).", _noteStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Destroy the outpost", GUILayout.Width(S(190f)), GUILayout.Height(S(26f)))) LoadSample(SC2MissionSamples.DestroyTheOutpost(_mapInfo));
        if (GUILayout.Button("Hold the line (co-op)", GUILayout.Width(S(190f)), GUILayout.Height(S(26f)))) LoadSample(SC2MissionSamples.HoldTheLine(_mapInfo));
        if (GUILayout.Button("Loop: three waves", GUILayout.Width(S(190f)), GUILayout.Height(S(26f)))) LoadSample(SC2MissionSamples.ThreeWaves());
        if (GUILayout.Button("Clear everything", GUILayout.Width(S(190f)), GUILayout.Height(S(26f)))) LoadSample(new SC2MissionDocument());
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private static readonly string[] ArmyRoles = { SC2MissionArmy.RoleEnemy, SC2MissionArmy.RoleGeneric };

    /// <summary>One army of the map: the fields that end up in the army block of the script. The first write stores the army in the document.</summary>
    private void DrawArmyEditor(SC2MissionArmyEntry entry)
    {
        var army = entry.Army;
        var isPlayer = entry.Role == SC2MissionArmy.RolePlayer;
        var key = "army_" + entry.Index + "_";
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(entry.Index + ".  " + army.Name + "   -   " + (isPlayer ? "human slot" : entry.Role), _fieldLabelStyle);

        var alias = entry.Alias;
        if (TextEdit("Name in the script (ScenarioInfo.ARMY_<name>)", ref alias, key + "alias", "Rename army"))
        {
            entry.Alias = alias;
            SC2MissionArmies.Store(_work, entry).Alias = alias;
        }

        if (!isPlayer)
        {
            var role = army.Role == SC2MissionArmy.RoleGeneric ? SC2MissionArmy.RoleGeneric : SC2MissionArmy.RoleEnemy;
            if (ChoiceEdit("Role (enemy: AI with resources, generic: only a colour and a cap)", ref role, ArmyRoles, false, "Set army role"))
                SC2MissionArmies.Store(_work, entry).Role = role;

            GUILayout.Label("Colours (AARRGGBB hex: primary, secondary, tertiary)", _fieldLabelStyle);
            GUILayout.BeginHorizontal();
            for (var c = 0; c < 3; c++)
            {
                var color = army.Colors[c];
                GUILayout.BeginVertical(GUILayout.Width(S(130f)));
                if (TextEdit(null, ref color, key + "color" + c, "Set army colour")) SC2MissionArmies.Store(_work, entry).Colors[c] = color;
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
        }

        var cap = army.UnitCap;
        if (IntEdit("Unit cap", ref cap, key + "cap", "Set unit cap", 1, 100000)) SC2MissionArmies.Store(_work, entry).UnitCap = cap;

        if (isPlayer)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(S(150f)));
            var mass = army.Mass;
            if (IntEdit("Starting mass", ref mass, key + "mass", "Set starting mass", 0, 99999999)) SC2MissionArmies.Store(_work, entry).Mass = mass;
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(S(150f)));
            var energy = army.Energy;
            if (IntEdit("Starting energy", ref energy, key + "energy", "Set starting energy", 0, 99999999)) SC2MissionArmies.Store(_work, entry).Energy = energy;
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(S(150f)));
            var research = army.Research;
            if (IntEdit("Research points", ref research, key + "research", "Set research", 0, 1000)) SC2MissionArmies.Store(_work, entry).Research = research;
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
        else if (entry.Role == SC2MissionArmy.RoleEnemy)
        {
            var unlock = army.UnlockResearch;
            if (BoolEdit("Unlock all research (without it the AI builds engineers but no combat units)", ref unlock, "Toggle unlock research")) SC2MissionArmies.Store(_work, entry).UnlockResearch = unlock;
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(S(190f)));
            var massBoost = (int)System.Math.Min(army.MassBoost, int.MaxValue);
            if (IntEdit("Mass given at the start", ref massBoost, key + "massboost", "Set mass boost", 0, int.MaxValue)) SC2MissionArmies.Store(_work, entry).MassBoost = massBoost;
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(S(190f)));
            var energyBoost = (int)System.Math.Min(army.EnergyBoost, int.MaxValue);
            if (IntEdit("Energy given at the start", ref energyBoost, key + "energyboost", "Set energy boost", 0, int.MaxValue)) SC2MissionArmies.Store(_work, entry).EnergyBoost = energyBoost;
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
        GUILayout.Space(S(4f));
    }

    private void LoadSample(SC2MissionDocument sample)
    {
        Commit();
        sample.Enabled = _work.Enabled;
        _work = sample;
        _selNode = _selEdge = -1;
        Mutate("Load sample mission");
        Commit();
        _tab = _work.Stages.Count > 0 ? _work.Stages[0].Id : TabSettings;
    }

    // ------------------------------------------------------------------ bases

    private void DrawBasesForm(Rect body)
    {
        var listWidth = S(230f);
        var list = new Rect(body.x, body.y, listWidth, body.height);
        var edit = new Rect(list.xMax + S(8f), body.y, body.width - listWidth - S(8f), body.height);

        GUI.Box(list, GUIContent.none);
        GUILayout.BeginArea(new Rect(list.x + S(6f), list.y + S(6f), list.width - S(12f), list.height - S(12f)));
        GUILayout.Label("Bases", _titleStyle);
        for (var i = 0; i < _work.Bases.Count; i++)
        {
            var b = _work.Bases[i];
            if (GUILayout.Toggle(i == _baseSel, string.IsNullOrEmpty(b.Id) ? "(unnamed)" : b.Id + "  (" + b.Army + ")", GUI.skin.button, GUILayout.Height(S(24f)))) _baseSel = i;
        }
        if (GUILayout.Button("+ Add base", GUILayout.Height(S(26f))))
        {
            Commit();
            var n = _work.Bases.Count + 1;
            while (_work.FindBase("Base" + n) != null) n++;
            var army = _mapInfo.Armies.Count > 0 ? SC2MissionMapInfoBuilder.Sorted(_mapInfo.Armies).Last() : "ARMY_2";
            _work.Bases.Add(new SC2MissionBase { Id = "Base" + n, Army = army });
            _baseSel = _work.Bases.Count - 1;
            Mutate("Add base");
            Commit();
        }
        GUILayout.EndArea();

        GUI.Box(edit, GUIContent.none);
        GUILayout.BeginArea(new Rect(edit.x + S(8f), edit.y + S(8f), edit.width - S(16f), edit.height - S(16f)));
        _baseSel = Mathf.Clamp(_baseSel, 0, Math.Max(0, _work.Bases.Count - 1));
        if (_work.Bases.Count == 0)
        {
            GUILayout.Label("A base is a campaign-AI base: the AI builds up the unit groups you list around a marker, keeps engineers working and runs OpAI wave generators that attack along your chains. Add one to start.", _noteStyle);
        }
        else
        {
            _formScroll = GUILayout.BeginScrollView(_formScroll);
            GUILayout.BeginVertical(GUILayout.MaxWidth(S(640f)));
            DrawBaseEditor(_work.Bases[_baseSel]);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }
        GUILayout.EndArea();
    }

    private void DrawBaseEditor(SC2MissionBase b)
    {
        var armies = SC2MissionMapInfoBuilder.Sorted(_mapInfo.Armies);
        var markers = SC2MissionMapInfoBuilder.Sorted(_mapInfo.Markers);
        var groups = _mapInfo.Groups.TryGetValue(b.Army, out var set) ? SC2MissionMapInfoBuilder.Sorted(set) : new string[0];
        var chains = SC2MissionMapInfoBuilder.Sorted(_mapInfo.Chains);

        GUILayout.Label("Base", _titleStyle);
        var id = b.Id;
        if (TextEdit("Id (used by the Start base and OpAI nodes)", ref id, "base_id", "Rename base")) b.Id = id;
        var engineerChain = SC2MissionNodeCatalog.EngineerChainName(b.Id);
        var hasEngineerChain = _mapInfo.Chains.Contains(engineerChain);
        if (b.StartMode == SC2MissionBase.StartFilled)
            GUILayout.Label((hasEngineerChain ? "✔ " : "✖ ") + "Engineer chain '" + engineerChain + "' " + (hasEngineerChain ? "exists on the map." : "is missing: add a chain with this name on the Objects tab (Chains)."), _noteStyle);
        else
            GUILayout.Label("An empty base is an expansion: it needs no engineer chain of its own.", _noteStyle);
        var army = b.Army;
        if (armies.Length > 0 ? ChoiceEdit("Army", ref army, armies, false, "Set base army") : TextEdit("Army", ref army, "base_army", "Set base army")) b.Army = army;
        var marker = b.Marker;
        if (markers.Length > 0 ? ChoiceEdit("Marker (centre of the base)", ref marker, markers, true, "Set base marker") : TextEdit("Marker (centre of the base)", ref marker, "base_marker", "Set base marker")) b.Marker = marker;
        var radius = b.Radius;
        if (IntEdit("Radius", ref radius, "base_radius", "Set base radius", 1)) b.Radius = radius;
        var engineers = b.Engineers;
        if (IntEdit("Engineers", ref engineers, "base_eng", "Set engineers", 0, 50)) b.Engineers = engineers;
        var mode = b.StartMode;
        if (ChoiceEdit("Start as", ref mode, StartModes, false, "Set base start mode")) b.StartMode = mode;
        GUILayout.Label("filled = the unit groups below are built at once and rebuilt when destroyed;  empty = the engineers build everything.", _noteStyle);
        var startAtBegin = b.StartAtBegin;
        if (BoolEdit("Start when the mission begins (otherwise a Start base node does)", ref startAtBegin, "Toggle base autostart")) b.StartAtBegin = startAtBegin;

        GUILayout.Space(S(8f));
        GUILayout.Label("Build groups (priority: higher is rebuilt first)", _titleStyle);
        for (var i = 0; i < b.BuildGroups.Count; i++)
        {
            var g = b.BuildGroups[i];
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(S(300f)));
            var groupName = g.Group;
            if (groups.Length > 0 ? ChoiceEdit(null, ref groupName, groups, true, "Set build group") : TextEdit(null, ref groupName, "bg_" + i, "Set build group")) g.Group = groupName;
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUILayout.Width(S(100f)));
            var priority = g.Priority;
            if (IntEdit(null, ref priority, "bgp_" + i, "Set priority", 0, 1000)) g.Priority = priority;
            GUILayout.EndVertical();
            if (GUILayout.Button("x", GUILayout.Width(S(26f)), GUILayout.Height(S(22f))))
            {
                Commit();
                b.BuildGroups.RemoveAt(i);
                Mutate("Remove build group");
                Commit();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }
        if (GUILayout.Button("+ Add build group", GUILayout.Width(S(180f)), GUILayout.Height(S(24f))))
        {
            Commit();
            b.BuildGroups.Add(new SC2MissionBuildGroup { Group = groups.FirstOrDefault(x => b.BuildGroups.All(y => y.Group != x)) ?? "" });
            Mutate("Add build group");
            Commit();
        }

        GUILayout.Space(S(8f));
        GUILayout.Label("OpAI (attack and defence waves)", _titleStyle);
        GUILayout.Label("An OpAI is built by the base's factories: it needs a factory of the OpAI's faction (UEF, Cybran or Illuminate) in the build groups, and the army's research unlocked (Settings > Armies). The attack types scale with the player's army: the more units the player has, the bigger the waves.", _noteStyle);
        for (var i = 0; i < b.OpAIs.Count; i++)
        {
            if (DrawOpAI(b, b.OpAIs[i], i, chains)) break;
            GUILayout.Space(S(6f));
        }
        if (GUILayout.Button("+ Add OpAI", GUILayout.Width(S(180f)), GUILayout.Height(S(24f))))
        {
            Commit();
            var n = 1;
            while (_work.Bases.Any(x => x.OpAIs.Any(y => y.Name == "OpAI" + n))) n++;
            b.OpAIs.Add(new SC2MissionOpAI { Name = "OpAI" + n, Chains = new List<string>(chains.Take(1)) });
            Mutate("Add OpAI");
            Commit();
        }

        GUILayout.Space(S(14f));
        if (_confirmDeleteBase != b.Id)
        {
            if (GUILayout.Button("Delete base", GUILayout.Width(S(160f)), GUILayout.Height(S(24f)))) _confirmDeleteBase = b.Id;
        }
        else
        {
            GUILayout.Label("Delete base '" + b.Id + "' with its OpAI?", _noteStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Delete", GUILayout.Width(S(90f)), GUILayout.Height(S(24f))))
            {
                Commit();
                _work.Bases.Remove(b);
                _confirmDeleteBase = "";
                Mutate("Delete base");
                Commit();
            }
            if (GUILayout.Button("Cancel", GUILayout.Width(S(90f)), GUILayout.Height(S(24f)))) _confirmDeleteBase = "";
            GUILayout.EndHorizontal();
        }
    }

    /// <summary>Draws one OpAI block; true when it was removed (the caller stops walking the list).</summary>
    private bool DrawOpAI(SC2MissionBase b, SC2MissionOpAI o, int index, string[] chains)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        var name = o.Name;
        if (TextEdit("Name", ref name, "opai_name_" + index, "Rename OpAI")) o.Name = name;
        var type = o.Type;
        if (ChoiceEdit("Type", ref type, SC2MissionNodeCatalog.OpAITypes, false, "Set OpAI type")) o.Type = type;
        var thread = o.Thread;
        if (ChoiceEdit("Patrol behaviour", ref thread, SC2MissionNodeCatalog.OpAIThreads, false, "Set OpAI behaviour")) o.Thread = thread;
        GUILayout.Label(o.Thread == SC2MissionOpAI.RandomizedPoints ? "Patrols the points of the first chain in random order." : "Picks one of the chains at random and walks it.", _noteStyle);

        GUILayout.Label("Chains", _fieldLabelStyle);
        for (var c = 0; c < o.Chains.Count; c++)
        {
            GUILayout.BeginHorizontal();
            var chain = o.Chains[c];
            GUILayout.BeginVertical(GUILayout.Width(S(300f)));
            if (chains.Length > 0 ? ChoiceEdit(null, ref chain, chains, true, "Set OpAI chain") : TextEdit(null, ref chain, "opai_chain_" + index + "_" + c, "Set OpAI chain")) o.Chains[c] = chain;
            GUILayout.EndVertical();
            if (GUILayout.Button("x", GUILayout.Width(S(26f)), GUILayout.Height(S(22f))))
            {
                Commit();
                o.Chains.RemoveAt(c);
                Mutate("Remove OpAI chain");
                Commit();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }
        if (GUILayout.Button("+ Add chain", GUILayout.Width(S(130f)), GUILayout.Height(S(22f))))
        {
            Commit();
            o.Chains.Add(chains.FirstOrDefault(x => !o.Chains.Contains(x)) ?? "");
            Mutate("Add OpAI chain");
            Commit();
        }

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(S(110f)));
        var child = o.ChildCount;
        if (IntEdit("Child count (0 = the type's own)", ref child, "opai_child_" + index, "Set child count", 0, 100)) o.ChildCount = child;
        GUILayout.EndVertical();
        GUILayout.BeginVertical(GUILayout.Width(S(110f)));
        var platoons = o.MaxPlatoons;
        if (IntEdit("Max platoons", ref platoons, "opai_max_" + index, "Set max platoons", 1, 50)) o.MaxPlatoons = platoons;
        GUILayout.EndVertical();
        GUILayout.BeginVertical(GUILayout.Width(S(110f)));
        var delay = o.AttackDelay;
        if (IntEdit("Delay (sec)", ref delay, "opai_delay_" + index, "Set attack delay", 0, 3600)) o.AttackDelay = delay;
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        var enabled = o.Enabled;
        if (BoolEdit("Enabled from the start (otherwise an Enable / disable OpAI node switches it on)", ref enabled, "Toggle OpAI")) o.Enabled = enabled;

        var removed = false;
        if (GUILayout.Button("Remove OpAI", GUILayout.Width(S(130f)), GUILayout.Height(S(22f))))
        {
            Commit();
            b.OpAIs.Remove(o);
            Mutate("Remove OpAI");
            Commit();
            removed = true;
        }
        GUILayout.EndVertical();
        return removed;
    }
}
