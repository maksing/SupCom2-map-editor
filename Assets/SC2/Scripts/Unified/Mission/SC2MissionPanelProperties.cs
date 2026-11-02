using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEditor;
using UnityEngine;

public sealed partial class SC2MissionPanel
{
    private readonly Dictionary<string, string> _numberBuffers = new();

    /// <summary>A dropdown or switch changed this frame: the edit is written once the caller has stored the new value in the model.</summary>
    private bool _commitAfterFrame;

    /// <summary>Counts the dropdowns drawn so far in this pass; reset at the start of every pass.</summary>
    private int _popupSeq;
    private bool _confirmDeleteStage;

    private static readonly string[] CategoryPresets =
    {
        "categories.ALLUNITS", "categories.COMMAND", "categories.MOBILE", "categories.LAND", "categories.AIR", "categories.NAVAL",
        "categories.STRUCTURE", "categories.ENGINEER", "categories.FACTORY", "categories.EXPERIMENTAL",
    };

    // ------------------------------------------------------------------ field helpers (shared by the properties and the forms)

    /// <summary>The width the text of a field has; a label taller than one line gets the room of every line it wraps to.</summary>
    private float _labelWidth = 2000f;

    /// <summary>A label whose height is worked out from the column width, because a wrapped label must not be cut after its first line.</summary>
    private void WrappedLabel(string text, GUIStyle style, float minHeight)
    {
        var content = new GUIContent(text);
        GUILayout.Label(content, style, GUILayout.Height(Mathf.Max(minHeight, style.CalcHeight(content, _labelWidth))));
    }

    private void FieldLabel(string text) => WrappedLabel(text, _fieldLabelStyle, S(18f));

    private bool TextEdit(string label, ref string value, string key, string undo, bool multiline = false)
    {
        if (!string.IsNullOrEmpty(label)) FieldLabel(label);
        GUI.SetNextControlName("m_" + key);
        var next = multiline
            ? GUILayout.TextArea(value ?? "", GUILayout.MinHeight(S(110f)))
            : GUILayout.TextField(value ?? "", GUILayout.Height(S(22f)));
        if (next == (value ?? "")) return false;
        value = next;
        Mutate(undo);
        return true;
    }

    private bool IntEdit(string label, ref int value, string key, string undo, int min = 0, int max = 100000)
    {
        if (!string.IsNullOrEmpty(label)) FieldLabel(label);
        var controlName = "m_" + key;
        var focused = GUI.GetNameOfFocusedControl() == controlName;
        var shown = focused && _numberBuffers.TryGetValue(key, out var buffered) ? buffered : value.ToString(CultureInfo.InvariantCulture);
        GUI.SetNextControlName(controlName);
        var next = GUILayout.TextField(shown, GUILayout.Height(S(22f)));
        if (next == shown) return false;
        _numberBuffers[key] = next;
        if (!int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
        parsed = Mathf.Clamp(parsed, min, max);
        if (parsed == value) return false;
        value = parsed;
        Mutate(undo);
        return true;
    }

    private bool BoolEdit(string label, ref bool value, string undo)
    {
        var content = new GUIContent(label);
        var height = Mathf.Max(S(20f), _wrapToggleStyle.CalcHeight(content, _labelWidth));
        var next = GUILayout.Toggle(value, content, _wrapToggleStyle, GUILayout.Height(height));
        if (next == value) return false;
        value = next;
        Mutate(undo);
        _commitAfterFrame = true;
        return true;
    }

    /// <summary>A dropdown over options. A value the list does not have (a name the map lost) stays selectable, marked as missing.</summary>
    private bool ChoiceEdit(string label, ref string value, IList<string> options, bool allowNone, string undo)
    {
        if (!string.IsNullOrEmpty(label)) FieldLabel(label);
        var items = new List<string>();
        if (allowNone) items.Add("(none)");
        items.AddRange(options);
        var index = allowNone ? 0 : -1;
        for (var i = 0; i < options.Count; i++)
            if (options[i] == value) index = (allowNone ? 1 : 0) + i;
        if (!string.IsNullOrEmpty(value) && index < 0 || (allowNone && !string.IsNullOrEmpty(value) && index == 0))
        {
            items.Add(value + "  (not found)");
            index = items.Count - 1;
        }
        if (index < 0) index = 0;
        var picked = PopupAt(index, items.ToArray());
        if (picked == index) return false;
        string next;
        if (allowNone && picked == 0) next = "";
        else if (picked >= (allowNone ? 1 : 0) + options.Count) return false;
        else next = options[picked - (allowNone ? 1 : 0)];
        if (next == value) return false;
        value = next;
        Mutate(undo);
        _commitAfterFrame = true;
        return true;
    }

    /// <summary>A dropdown. A build's dropdown is a floating list that tracks which control is open by a key, and the key has to be the same
    /// in every frame: the position of the dropdown among the dropdowns of the window (and the tab and selection it is shown for).</summary>
    private int PopupAt(int index, string[] items)
    {
#if UNITY_EDITOR
        return EditorGUILayout.Popup(index, items, SC2UiScale.PopupStyle());
#else
        return EditorGUILayout.Popup("mission:" + _tab + ":" + _selNode + ":" + _baseSel + ":" + _popupSeq++, index, items, SC2UiScale.PopupStyle());
#endif
    }

    // ------------------------------------------------------------------ the properties panel

    private void DrawProperties(Rect r, SC2MissionStage stage)
    {
        GUI.Box(r, GUIContent.none);
        GUILayout.BeginArea(new Rect(r.x + S(8f), r.y + S(8f), r.width - S(16f), r.height - S(16f)));
        _propsScroll = GUILayout.BeginScrollView(_propsScroll);
        // the panel width, less the area margins, the scroll bar and the margins of a label
        _labelWidth = r.width - S(16f) - S(40f);
        var node = SelectedNode();
        if (node != null) DrawNodeProperties(stage, node);
        else if (_selEdge >= 0 && _selEdge < stage.Edges.Count) DrawEdgeProperties(stage, stage.Edges[_selEdge]);
        else DrawStageProperties(stage);
        _labelWidth = 2000f;
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawNodeProperties(SC2MissionStage stage, SC2MissionNode node)
    {
        var def = SC2MissionNodeCatalog.Find(node.Type);
        if (def == null) { GUILayout.Label("Unknown node type '" + node.Type + "'.", _noteStyle); return; }

        WrappedLabel(def.Title + "  (#" + node.Id + ")", _wrapTitleStyle, S(24f));
        var inputs = SC2MissionNodeCatalog.InputsOf(def, node);
        var ports = SC2MissionNodeCatalog.PortsOf(def, node);
        WrappedLabel(def.Category + (inputs.Length > 1 ? "  -  inputs: " + string.Join(", ", inputs) : "") +
            (ports.Length > 0 ? "  -  outputs: " + string.Join(", ", ports) : "  -  ends the chain"), _noteStyle, S(18f));
        GUILayout.Space(S(4f));

        foreach (var p in def.Params) DrawParam(stage, node, p);
        PruneWires(stage, node);

        foreach (var issue in _compiled.Issues.Where(i => i.StageId == stage.Id && i.NodeId == node.Id))
            GUILayout.Label((issue.Severity == SC2MissionSeverity.Error ? "✖ " : "⚠ ") + issue.Message, _noteStyle);

        GUILayout.Space(S(8f));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Duplicate", GUILayout.Height(S(24f)))) DuplicateNode(stage, node);
        if (GUILayout.Button("Delete", GUILayout.Height(S(24f)))) DeleteSelection();
        GUILayout.EndHorizontal();
    }

    /// <summary>A parameter that changes how many inputs or outputs a node has takes away the wires that used the ones that are gone.</summary>
    private void PruneWires(SC2MissionStage stage, SC2MissionNode node)
    {
        var def = SC2MissionNodeCatalog.Find(node.Type);
        var ports = SC2MissionNodeCatalog.PortsOf(def, node);
        var inputs = SC2MissionNodeCatalog.InputsOf(def, node);
        var removed = stage.Edges.RemoveAll(e =>
            (e.FromNode == node.Id && Array.IndexOf(ports, e.FromPort) < 0) ||
            (e.ToNode == node.Id && Array.IndexOf(inputs, e.ToPort ?? SC2MissionNodeCatalog.DefaultInput) < 0));
        if (removed == 0) return;
        _selEdge = -1;
        Mutate("Remove the wires of removed ports");
    }

    private void DrawParam(SC2MissionStage stage, SC2MissionNode node, SC2MissionParamDef p)
    {
        var value = node.GetParam(p.Key, p.Default);
        var undo = "Edit " + p.Label;
        var key = "n" + node.Id + "_" + p.Key;
        var changed = false;
        switch (p.Kind)
        {
            case SC2MissionParamKind.Bool:
            {
                var b = value == "true";
                if (BoolEdit(p.Label, ref b, undo)) { value = b ? "true" : "false"; changed = true; }
                break;
            }
            case SC2MissionParamKind.Choice:
                changed = ChoiceEdit(p.Label, ref value, p.Choices, false, undo);
                break;
            case SC2MissionParamKind.Code:
                changed = TextEdit(p.Label, ref value, key, undo, multiline: true);
                break;
            case SC2MissionParamKind.Categories:
            {
                var preset = "";
                if (ChoiceEdit(p.Label, ref preset, CategoryPresets, true, undo) && !string.IsNullOrEmpty(preset)) { value = preset; changed = true; }
                changed |= TextEdit("", ref value, key, undo);
                break;
            }
            case SC2MissionParamKind.Number:
                changed = TextEdit(p.Label + "  (a number, or $variable)", ref value, key, undo);
                break;
            case SC2MissionParamKind.Text:
                changed = TextEdit(p.Label, ref value, key, undo);
                break;
            default:
            {
                var options = OptionsFor(p.Kind, node);
                if (options != null && options.Length > 0) changed = ChoiceEdit(p.Label, ref value, options, true, undo);
                else changed = TextEdit(p.Label, ref value, key, undo);
                break;
            }
        }
        if (changed) node.SetParam(p.Key, value);
    }

    /// <summary>The names a picker offers for a parameter, or null when the map gives none to choose from (then it is typed).</summary>
    private string[] OptionsFor(SC2MissionParamKind kind, SC2MissionNode node)
    {
        switch (kind)
        {
            case SC2MissionParamKind.Army: return SC2MissionMapInfoBuilder.Sorted(_mapInfo.Armies);
            case SC2MissionParamKind.Area: return SC2MissionMapInfoBuilder.Sorted(_mapInfo.Areas);
            case SC2MissionParamKind.Marker: return SC2MissionMapInfoBuilder.Sorted(_mapInfo.Markers);
            case SC2MissionParamKind.Chain: return SC2MissionMapInfoBuilder.Sorted(_mapInfo.Chains);
            case SC2MissionParamKind.Group:
                return _mapInfo.Groups.TryGetValue(node.GetParam("army"), out var groups) ? SC2MissionMapInfoBuilder.Sorted(groups) : new string[0];
            case SC2MissionParamKind.Unit:
                return _mapInfo.Units.TryGetValue(node.GetParam("army"), out var units) ? SC2MissionMapInfoBuilder.Sorted(units) : new string[0];
            case SC2MissionParamKind.Stage: return _work.Stages.Select(s => s.Id).ToArray();
            case SC2MissionParamKind.Variable: return _work.Variables.Select(v => v.Name).ToArray();
            case SC2MissionParamKind.Base: return _work.Bases.Select(b => b.Id).ToArray();
            case SC2MissionParamKind.OpAI:
                return _work.FindBase(node.GetParam("base"))?.OpAIs.Select(o => o.Name).ToArray() ?? new string[0];
            default: return null;
        }
    }

    private void DuplicateNode(SC2MissionStage stage, SC2MissionNode node)
    {
        Commit();
        var copy = node.Clone();
        copy.Id = stage.NextNodeId++;
        copy.X += 30f;
        copy.Y += 30f;
        stage.Nodes.Add(copy);
        _selNode = copy.Id;
        Mutate("Duplicate node");
        Commit();
    }

    private void DrawEdgeProperties(SC2MissionStage stage, SC2MissionEdge edge)
    {
        GUILayout.Label("Wire", _titleStyle);
        var from = edge.FromNode == SC2MissionStage.StartNodeId ? "Start" : "#" + edge.FromNode + " " + (SC2MissionNodeCatalog.Find(stage.FindNode(edge.FromNode)?.Type ?? "")?.Title ?? "?");
        var to = "#" + edge.ToNode + " " + (SC2MissionNodeCatalog.Find(stage.FindNode(edge.ToNode)?.Type ?? "")?.Title ?? "?");
        GUILayout.Label(from + "  [" + edge.FromPort + "]  →  " + to + (edge.ToPort != SC2MissionNodeCatalog.DefaultInput ? "  [" + edge.ToPort + "]" : ""), _noteStyle);
        GUILayout.Space(S(6f));
        if (GUILayout.Button("Delete wire", GUILayout.Height(S(24f)))) DeleteSelection();
    }

    private void DrawStageProperties(SC2MissionStage stage)
    {
        GUILayout.Label("Stage: " + stage.Name, _titleStyle);
        GUILayout.Label("Id: " + stage.Id + "   Nodes: " + stage.Nodes.Count + "   Wires: " + stage.Edges.Count, _noteStyle);
        GUILayout.Space(S(4f));
        var name = stage.Name;
        if (TextEdit("Name", ref name, "stage_name_" + stage.Id, "Rename stage")) stage.Name = name;

        GUILayout.Space(S(6f));
        var testing = _work.Settings.TestStartStage == stage.Id;
        var next = GUILayout.Toggle(testing, "Test from this stage");
        if (next != testing)
        {
            _work.Settings.TestStartStage = next ? stage.Id : "";
            Mutate(next ? "Set test start stage" : "Clear test start stage");
            Commit();
        }
        GUILayout.Label("The next export starts the mission at this stage, skipping the ones before it. Clear it before you release the map.", _noteStyle);

        GUILayout.Space(S(8f));
        var index = _work.Stages.IndexOf(stage);
        GUILayout.BeginHorizontal();
        GUI.enabled = index > 0;
        if (GUILayout.Button("Move left", GUILayout.Height(S(24f)))) MoveStage(index, -1);
        GUI.enabled = index >= 0 && index < _work.Stages.Count - 1;
        if (GUILayout.Button("Move right", GUILayout.Height(S(24f)))) MoveStage(index, 1);
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label("The first stage is the one the mission starts with.", _noteStyle);

        GUILayout.Space(S(8f));
        if (!_confirmDeleteStage)
        {
            if (GUILayout.Button("Delete stage", GUILayout.Height(S(24f)))) _confirmDeleteStage = true;
        }
        else
        {
            GUILayout.Label("Delete '" + stage.Name + "' with all its nodes?", _noteStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Delete", GUILayout.Height(S(24f)))) DeleteStage(stage);
            if (GUILayout.Button("Cancel", GUILayout.Height(S(24f)))) _confirmDeleteStage = false;
            GUILayout.EndHorizontal();
        }
    }

    private void MoveStage(int index, int delta)
    {
        Commit();
        var stage = _work.Stages[index];
        _work.Stages.RemoveAt(index);
        _work.Stages.Insert(index + delta, stage);
        Mutate("Move stage");
        Commit();
    }

    private void DeleteStage(SC2MissionStage stage)
    {
        Commit();
        _confirmDeleteStage = false;
        _work.Stages.Remove(stage);
        if (_work.Settings.TestStartStage == stage.Id) _work.Settings.TestStartStage = "";
        Mutate("Delete stage");
        Commit();
        EnsureTab();
    }

    // ------------------------------------------------------------------ bottom: Lua, validation, log

    private void DrawBottom(Rect r)
    {
        GUI.Box(r, GUIContent.none);
        var headerHeight = S(26f);
        var x = r.x + S(6f);
        if (GUI.Button(new Rect(x, r.y + S(2f), S(26f), headerHeight - S(4f)), _bottomOpen ? "v" : "^")) _bottomOpen = !_bottomOpen;
        x += S(32f);
        var errors = _compiled.Issues.Count + (_syntaxError.Length > 0 ? 1 : 0);
        var tabs = new[] { "Generated Lua", "Validation (" + errors + ")", "Mission Log" };
        for (var i = 0; i < tabs.Length; i++)
        {
            var width = S(140f);
            if (GUI.Toggle(new Rect(x, r.y + S(2f), width, headerHeight - S(4f)), _bottomOpen && _bottomTab == i, tabs[i], GUI.skin.button))
            {
                _bottomTab = i;
                _bottomOpen = true;
            }
            x += width + S(3f);
        }
        if (!_bottomOpen) return;

        var content = new Rect(r.x + S(8f), r.y + headerHeight + S(2f), r.width - S(16f), r.height - headerHeight - S(8f));
        GUILayout.BeginArea(content);
        switch (_bottomTab)
        {
            case 0: DrawLuaTab(); break;
            case 1: DrawValidationTab(); break;
            default: DrawLogTab(); break;
        }
        GUILayout.EndArea();
    }

    private const int LuaShownChars = 9000;

    private void DrawLuaTab()
    {
        var regions = SC2MissionLuaMarkers.Parse(_compiled.Lua);
        var intact = regions.All(rg => rg.Intact);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Generated from the graph: " + regions.Count + " region(s), hand edits: " + (intact ? "none" : "DETECTED") +
            (_syntaxError.Length > 0 ? "   —   does not compile: " + _syntaxError : "   —   compiles"), _noteStyle);
        if (GUILayout.Button("Copy Lua", GUILayout.Width(S(90f)))) GUIUtility.systemCopyBuffer = _compiled.Lua;
        GUILayout.EndHorizontal();
        _bottomScroll = GUILayout.BeginScrollView(_bottomScroll);
        var text = _compiled.Lua.Length > LuaShownChars
            ? _compiled.Lua.Substring(0, LuaShownChars) + "\n... (" + (_compiled.Lua.Length - LuaShownChars) + " more characters, use Copy Lua)"
            : _compiled.Lua;
        GUILayout.Label(text, _monoStyle);
        GUILayout.EndScrollView();
    }

    private void DrawValidationTab()
    {
        _bottomScroll = GUILayout.BeginScrollView(_bottomScroll);
        if (_compiled.Issues.Count == 0 && _syntaxError.Length == 0)
            GUILayout.Label("✔ No problems found; the generated Lua compiles.", _noteStyle);
        if (_syntaxError.Length > 0)
            GUILayout.Label("✖ The generated Lua does not compile: " + _syntaxError, _noteStyle);
        foreach (var issue in _compiled.Issues)
        {
            var text = (issue.Severity == SC2MissionSeverity.Error ? "✖ " : "⚠ ") + (issue.StageId.Length > 0 ? issue.StageId + (issue.NodeId > 0 ? " #" + issue.NodeId : "") + " — " : "") + issue.Message;
            if (!GUILayout.Button(text, GUI.skin.label)) continue;
            if (issue.StageId.Length > 0 && _work.FindStage(issue.StageId) != null)
            {
                SwitchTab(issue.StageId);
                if (issue.NodeId > 0) _selNode = issue.NodeId;
            }
        }
        GUILayout.EndScrollView();
    }

    private void DrawLogTab()
    {
        _bottomScroll = GUILayout.BeginScrollView(_bottomScroll);
        var entries = SC2MissionLog.Entries;
        for (var i = entries.Count - 1; i >= 0; i--) GUILayout.Label(entries[i], _noteStyle);
        GUILayout.EndScrollView();
    }
}
