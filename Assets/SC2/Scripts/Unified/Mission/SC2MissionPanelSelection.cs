using System;
using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>Working with several nodes at once (box selection, Shift-click, moving, copy and paste, aligning, comment frames) and the helps of
/// the canvas: wire highlight, the minimap, the status line and the list of shortcuts.</summary>
public sealed partial class SC2MissionPanel
{
    // ------------------------------------------------------------------ state

    /// <summary>The selected nodes when there is more than one; _selNode is then the primary one (the one the properties show). Only valid on
    /// the tab it was made on and while _selNode is still in it, so code that changes the selection by setting _selNode does not need to know about it.</summary>
    private readonly HashSet<int> _multi = new();
    private string _multiTab = "";

    /// <summary>What Ctrl+C took: kept across closing the panel and shared by all stages.</summary>
    private static SC2MissionClip _clip;

    private static bool _snap = true;
    private static bool _showMinimap = true;
    private bool _helpOpen;

    // a box being dragged, in the graph's own units minus the pan, so it stays where it is when the view scrolls
    private Vector2 _boxStartRel;
    private Vector2 _boxEndRel;
    private bool _boxAdds;

    // nodes being moved
    private Vector2 _dragOriginRel;
    private readonly Dictionary<int, Vector2> _dragStarts = new();

    // a comment frame being resized
    private int _resizeFrame;
    private Vector2 _resizeStartSize;
    private Vector2 _resizeStartRel;

    private int _panButton;
    private Vector2 _panStartLocal;

    private Vector2 _hoverLocal;
    private int _hoverEdge = -1;
    private int _hoverNode = -1;

    private readonly Dictionary<int, Vector2Int> _issueCounts = new();

    private static readonly string[] FrameColors = { "gray", "blue", "green", "orange", "red", "purple" };

    // ------------------------------------------------------------------ selection

    private bool HasMulti => _multi.Count > 1 && _multiTab == _tab && _multi.Contains(_selNode);

    private bool IsSelected(int id) => id == _selNode || (HasMulti && _multi.Contains(id));

    private List<int> SelectedIds()
    {
        if (HasMulti) return _multi.ToList();
        return _selNode > 0 ? new List<int> { _selNode } : new List<int>();
    }

    private List<SC2MissionNode> SelectedNodes(SC2MissionStage stage) =>
        SelectedIds().Select(stage.FindNode).Where(n => n != null).ToList();

    private void SelectOnly(int id)
    {
        _multi.Clear();
        _selNode = id;
        _selEdge = -1;
    }

    private void SetSelection(IEnumerable<int> ids, int primary)
    {
        var list = ids.ToList();
        _multi.Clear();
        foreach (var id in list) _multi.Add(id);
        _multiTab = _tab;
        _selNode = list.Count == 0 ? -1 : list.Contains(primary) ? primary : list[list.Count - 1];
        _selEdge = -1;
    }

    private void ToggleSelected(int id)
    {
        var current = SelectedIds();
        if (!current.Remove(id)) current.Add(id);
        SetSelection(current, id);
    }

    private void SelectAllNodes(SC2MissionStage stage)
    {
        var ids = stage.Nodes.Select(n => n.Id).ToList();
        SetSelection(ids, ids.Count > 0 ? ids[0] : -1);
    }

    private static bool IsFrame(SC2MissionNode node) => SC2MissionNodeCatalog.Find(node.Type)?.IsComment ?? false;

    // ------------------------------------------------------------------ comment frames

    private static float ParseUnits(string text, float fallback) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f ? v : fallback;

    private static Vector2 FrameSize(SC2MissionNode node) => new(ParseUnits(node.GetParam("width", "360"), 360f), ParseUnits(node.GetParam("height", "220"), 220f));

    private static Color FrameColor(string name)
    {
        switch (name)
        {
            case "blue": return new Color(0.30f, 0.55f, 0.95f);
            case "green": return new Color(0.30f, 0.78f, 0.45f);
            case "orange": return new Color(0.95f, 0.62f, 0.25f);
            case "red": return new Color(0.92f, 0.38f, 0.38f);
            case "purple": return new Color(0.70f, 0.45f, 0.90f);
            default: return new Color(0.62f, 0.64f, 0.70f);
        }
    }

    /// <summary>The handle in the bottom right corner of a frame that resizes it.</summary>
    private static Rect FrameHandle(Rect frame) => new(frame.xMax - S(16f), frame.yMax - S(16f), S(16f), S(16f));

    /// <summary>The nodes that lie inside a frame; moving the frame takes them along.</summary>
    private static IEnumerable<SC2MissionNode> NodesInside(SC2MissionStage stage, Rect frame, Dictionary<int, NodeBox> boxes)
    {
        foreach (var other in stage.Nodes)
        {
            if (IsFrame(other) || !boxes.TryGetValue(other.Id, out var box)) continue;
            if (frame.Contains(box.Rect.min) && frame.Contains(box.Rect.max)) yield return other;
        }
    }

    /// <summary>Wraps the selected nodes in a new frame.</summary>
    private void GroupInFrame(SC2MissionStage stage)
    {
        var nodes = SelectedNodes(stage).Where(n => !IsFrame(n)).ToList();
        if (nodes.Count == 0) return;
        Commit();
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var n in nodes)
        {
            var def = SC2MissionNodeCatalog.Find(n.Type);
            if (def == null) continue;
            var rect = Layout(n, def, Vector2.zero).Rect;
            min = Vector2.Min(min, rect.min);
            max = Vector2.Max(max, rect.max);
        }
        const float pad = 28f;
        var frame = stage.AddNode("comment", (min.x - S(pad)) / K, (min.y - S(pad) - S(26f)) / K);
        frame.SetParam("text", "Group");
        frame.SetParam("width", Mathf.CeilToInt((max.x - min.x + S(pad) * 2f) / K).ToString());
        frame.SetParam("height", Mathf.CeilToInt((max.y - min.y + S(pad) * 2f + S(26f)) / K).ToString());
        SetSelection(new[] { frame.Id }, frame.Id);
        Mutate("Group nodes in a frame");
        Commit();
    }

    // ------------------------------------------------------------------ moving, box selecting

    private void BeginNodeDrag(int id, SC2MissionStage stage, Vector2 graphMouse, Vector2 pan, Dictionary<int, NodeBox> boxes)
    {
        _dragNode = id;
        _dragStarts.Clear();
        foreach (var node in SelectedNodes(stage)) _dragStarts[node.Id] = new Vector2(node.X, node.Y);
        // a frame takes the nodes inside it along
        foreach (var node in SelectedNodes(stage).Where(IsFrame).ToList())
            if (boxes.TryGetValue(node.Id, out var fb))
                foreach (var inside in NodesInside(stage, fb.Rect, boxes))
                    if (!_dragStarts.ContainsKey(inside.Id)) _dragStarts[inside.Id] = new Vector2(inside.X, inside.Y);
        _dragOriginRel = graphMouse - pan;
        _dragMoved = false;
    }

    private void MoveDragged(SC2MissionStage stage, Vector2 graphMouse, Vector2 pan)
    {
        var delta = ((graphMouse - pan) - _dragOriginRel) / K;
        if (_snap && _dragStarts.TryGetValue(_dragNode, out var primary))
            delta = new Vector2(SC2MissionNodeLayoutTools.Snap(primary.x + delta.x), SC2MissionNodeLayoutTools.Snap(primary.y + delta.y)) - primary;
        foreach (var pair in _dragStarts)
        {
            var node = stage.FindNode(pair.Key);
            if (node == null) continue;
            node.X = pair.Value.x + delta.x;
            node.Y = pair.Value.y + delta.y;
        }
        _dragMoved = true;
        _workVersion++;
    }

    private void FinishBox(SC2MissionStage stage, Dictionary<int, NodeBox> boxes, Vector2 pan)
    {
        var a = _boxStartRel + pan;
        var b = _boxEndRel + pan;
        var rect = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        var hit = new List<int>();
        if (_boxAdds) hit.AddRange(SelectedIds());
        // a box that is only a click selects nothing new
        if (rect.width > 3f || rect.height > 3f)
            foreach (var node in stage.Nodes)
            {
                if (!boxes.TryGetValue(node.Id, out var box) || hit.Contains(node.Id)) continue;
                // a frame is picked only when the box holds all of it
                var picked = IsFrame(node) ? rect.Contains(box.Rect.min) && rect.Contains(box.Rect.max) : rect.Overlaps(box.Rect);
                if (picked) hit.Add(node.Id);
            }
        if (hit.Count == 0) { SelectOnly(-1); return; }
        SetSelection(hit, hit[hit.Count - 1]);
    }

    // ------------------------------------------------------------------ copy, paste, delete, align

    private void CopySelection(SC2MissionStage stage)
    {
        var ids = SelectedIds();
        if (ids.Count == 0) return;
        _clip = SC2MissionClipboard.Copy(stage, ids);
        SC2MissionLog.Add("Copied " + _clip.Nodes.Count + " node(s)");
    }

    /// <summary>Where a paste goes: under the pointer when it is over the canvas, otherwise near the middle of the view; in graph units.</summary>
    private Vector2 PasteTarget(SC2MissionStage stage)
    {
        var zoom = ZoomOf(stage);
        var pan = PanOf(stage);
        var local = _hoverLocal.x > 0f && _hoverLocal.y > 0f && _hoverLocal.x < _canvasSize.x && _hoverLocal.y < _canvasSize.y ? _hoverLocal : _canvasSize * 0.5f;
        var rel = local / zoom - pan;
        return new Vector2(SnapUnits(rel.x / K), SnapUnits(rel.y / K));
    }

    private static float SnapUnits(float value) => _snap ? SC2MissionNodeLayoutTools.Snap(value) : value;

    private void PasteClipboard(SC2MissionStage stage)
    {
        if (_clip == null || _clip.IsEmpty) return;
        Commit();
        var target = PasteTarget(stage);
        var created = SC2MissionClipboard.Paste(stage, _clip, target.x, target.y);
        SetSelection(created.Select(n => n.Id), created.Count > 0 ? created[created.Count - 1].Id : -1);
        Mutate("Paste " + created.Count + " node(s)");
        Commit();
    }

    private void DuplicateSelection(SC2MissionStage stage)
    {
        var ids = SelectedIds();
        if (ids.Count == 0) return;
        Commit();
        var clip = SC2MissionClipboard.Copy(stage, ids);
        var nodes = SelectedNodes(stage);
        var created = SC2MissionClipboard.Paste(stage, clip, nodes.Min(n => n.X) + 40f, nodes.Min(n => n.Y) + 40f);
        SetSelection(created.Select(n => n.Id), created.Count > 0 ? created[created.Count - 1].Id : -1);
        Mutate("Duplicate " + created.Count + " node(s)");
        Commit();
    }

    private void ApplyToSelection(SC2MissionStage stage, string undoName, Action<List<SC2MissionNode>> change)
    {
        var nodes = SelectedNodes(stage);
        if (nodes.Count == 0) return;
        Commit();
        change(nodes);
        Mutate(undoName);
        Commit();
    }

    // ------------------------------------------------------------------ hover and the issues on nodes

    private void UpdateIssueCounts(SC2MissionStage stage)
    {
        _issueCounts.Clear();
        foreach (var issue in _compiled.Issues)
        {
            if (issue.StageId != stage.Id || issue.NodeId <= 0) continue;
            _issueCounts.TryGetValue(issue.NodeId, out var counts);
            if (issue.Severity == SC2MissionSeverity.Error) counts.x++; else counts.y++;
            _issueCounts[issue.NodeId] = counts;
        }
    }

    /// <summary>The wire under the pointer and the node under it, for the highlight; only while nothing is being dragged.</summary>
    private void UpdateHover(SC2MissionStage stage, Vector2 pan, float zoom, Dictionary<int, NodeBox> boxes)
    {
        _hoverEdge = -1;
        _hoverNode = -1;
        if (_drag != DragMode.None || _quickOpen) return;
        var graph = _hoverLocal / zoom;
        foreach (var node in Enumerable.Reverse(stage.Nodes))
            if (!IsFrame(node) && boxes.TryGetValue(node.Id, out var box) && box.Rect.Contains(graph)) { _hoverNode = node.Id; return; }
        var best = S(7f);
        for (var i = 0; i < stage.Edges.Count; i++)
        {
            var edge = stage.Edges[i];
            if (!TryPortPosition(boxes, pan, edge.FromNode, edge.FromPort, out var from) || !boxes.TryGetValue(edge.ToNode, out var to)) continue;
            var d = DistanceToPolyline(graph, Bezier(from, InPortOf(to, edge.ToPort)));
            if (d < best) { best = d; _hoverEdge = i; }
        }
    }

    // ------------------------------------------------------------------ minimap

    private Rect MinimapRect(Rect local) => new(S(8f), local.height - S(24f) - S(100f) - S(6f), S(160f), S(100f));

    /// <summary>The area of the graph the minimap shows (unpanned graph pixels): every node, the Start block and what the view shows.</summary>
    private Rect MinimapBounds(SC2MissionStage stage, Vector2 pan, float zoom, Rect local)
    {
        var min = new Vector2(StartRect.x, StartRect.y);
        var max = new Vector2(StartRect.xMax, StartRect.yMax);
        foreach (var node in stage.Nodes)
        {
            var def = SC2MissionNodeCatalog.Find(node.Type);
            if (def == null) continue;
            var rect = Layout(node, def, Vector2.zero).Rect;
            min = Vector2.Min(min, rect.min);
            max = Vector2.Max(max, rect.max);
        }
        var viewMin = -pan;
        var viewMax = -pan + new Vector2(local.width, local.height) / zoom;
        min = Vector2.Min(min, viewMin);
        max = Vector2.Max(max, viewMax);
        var margin = S(20f);
        return Rect.MinMaxRect(min.x - margin, min.y - margin, max.x + margin, max.y + margin);
    }

    private static Vector2 ToMinimap(Vector2 rel, Rect bounds, Rect map)
    {
        var scale = Mathf.Min(map.width / bounds.width, map.height / bounds.height);
        var used = new Vector2(bounds.width * scale, bounds.height * scale);
        var origin = map.position + (map.size - used) * 0.5f;
        return origin + (rel - bounds.position) * scale;
    }

    private static Vector2 FromMinimap(Vector2 point, Rect bounds, Rect map)
    {
        var scale = Mathf.Min(map.width / bounds.width, map.height / bounds.height);
        var used = new Vector2(bounds.width * scale, bounds.height * scale);
        var origin = map.position + (map.size - used) * 0.5f;
        return bounds.position + (point - origin) / scale;
    }

    private void DrawMinimap(Rect local, SC2MissionStage stage, Vector2 pan, float zoom)
    {
        if (!_showMinimap || stage.Nodes.Count == 0) return;
        var map = MinimapRect(local);
        var bounds = MinimapBounds(stage, pan, zoom, local);
        FillRect(new Rect(map.x - 1f, map.y - 1f, map.width + 2f, map.height + 2f), new Color(0f, 0f, 0f, 0.85f));
        FillRect(map, new Color(0.07f, 0.08f, 0.10f, 0.92f));

        var start = ToMinimap(new Vector2(StartRect.x, StartRect.y), bounds, map);
        FillRect(new Rect(start.x, start.y, Mathf.Max(3f, S(8f)), Mathf.Max(3f, S(6f))), new Color(0.3f, 0.6f, 0.35f));
        foreach (var node in stage.Nodes)
        {
            var def = SC2MissionNodeCatalog.Find(node.Type);
            if (def == null) continue;
            var rect = Layout(node, def, Vector2.zero).Rect;
            var a = ToMinimap(rect.min, bounds, map);
            var b = ToMinimap(rect.max, bounds, map);
            var r = new Rect(a.x, a.y, Mathf.Max(2f, b.x - a.x), Mathf.Max(2f, b.y - a.y));
            if (def.IsComment) { FrameRectOutline(r, FrameColor(node.GetParam("color", "gray"))); continue; }
            var color = CategoryColor(def.Category);
            FillRect(r, IsSelected(node.Id) ? new Color(1f, 0.9f, 0.3f) : color);
        }

        // the part of the graph the canvas shows
        var v1 = ToMinimap(-pan, bounds, map);
        var v2 = ToMinimap(-pan + new Vector2(local.width, local.height) / zoom, bounds, map);
        FrameRectOutline(Rect.MinMaxRect(Mathf.Max(map.x, v1.x), Mathf.Max(map.y, v1.y), Mathf.Min(map.xMax, v2.x), Mathf.Min(map.yMax, v2.y)), new Color(1f, 1f, 1f, 0.9f));
    }

    private static void FrameRectOutline(Rect r, Color color)
    {
        FillRect(new Rect(r.x, r.y, r.width, 1f), color);
        FillRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), color);
        FillRect(new Rect(r.x, r.y, 1f, r.height), color);
        FillRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), color);
    }

    private void MinimapPan(Rect local, SC2MissionStage stage, Vector2 point)
    {
        var pan = PanOf(stage);
        var zoom = ZoomOf(stage);
        var rel = FromMinimap(point, MinimapBounds(stage, pan, zoom, local), MinimapRect(local));
        _pan[stage.Id] = new Vector2(local.width, local.height) / (2f * zoom) - rel;
    }

    // ------------------------------------------------------------------ status line and help

    private string StatusText(SC2MissionStage stage)
    {
        switch (_drag)
        {
            case DragMode.Connect: return "Drop on an input to connect. Let go over empty space to search for the next node.";
            case DragMode.Node: return "Moving " + _dragStarts.Count + " node(s). Hold the pointer at the edge to scroll. Snap to grid is " + (_snap ? "on." : "off.");
            case DragMode.Box: return "Let go to select the nodes in the box. Shift or Ctrl adds to the selection.";
            case DragMode.ResizeFrame: return "Resizing the frame.";
        }
        if (HasMulti) return _multi.Count + " nodes selected.  Ctrl+C copy, Ctrl+D duplicate, Delete remove, drag to move them together.";
        var node = SelectedNode();
        if (node != null) return "#" + node.Id + " " + (SC2MissionNodeCatalog.Find(node.Type)?.Title ?? node.Type) + ".  Delete removes it, Ctrl+D duplicates it, F centres it.";
        if (_selEdge >= 0) return "A wire is selected. Delete removes it.";
        return "Drag a box to select. Right-drag pans, wheel zooms. Double-click searches for a node. F1: shortcuts.";
    }

    // a heading has no description; the keys and what they do are in two columns
    private static readonly (string keys, string what)[] HelpLines =
    {
        ("Mouse", ""),
        ("Drag on empty canvas", "select with a box (Shift or Ctrl adds)"),
        ("Click a node", "select it (Shift or Ctrl toggles)"),
        ("Drag a node", "move it and every selected node"),
        ("Right or middle drag", "pan the view"),
        ("Wheel", "zoom at the pointer"),
        ("Drag from an output port", "make a wire; let go on empty space to search"),
        ("Double-click, right-click", "search for a node to add here"),
        ("Right-click on a wire", "delete the wire"),
        ("Keys", ""),
        ("Home", "show every node"),
        ("F", "centre the selected node"),
        ("Ctrl+A", "select all"),
        ("Ctrl+C, Ctrl+V, Ctrl+D", "copy, paste at the pointer, duplicate"),
        ("Delete", "delete the selection"),
        ("Ctrl+Z, Ctrl+Y", "undo, redo"),
        ("Escape", "clear the selection, then close the editor"),
        ("F1", "this list"),
    };

    private void DrawHelp(Rect r)
    {
        if (!_helpOpen) return;
        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.F1)) { _helpOpen = false; e.Use(); return; }
        if (e.type == EventType.MouseDown) { _helpOpen = false; e.Use(); return; }
        // the rows are squeezed to what the canvas can hold, so the list is never cut by the panel below
        var lineHeight = Mathf.Min(S(19f), (r.height - S(76f)) / HelpLines.Length);
        var rect = new Rect(r.x + S(30f), r.y + S(20f), Mathf.Min(S(640f), r.width - S(60f)), lineHeight * HelpLines.Length + S(46f));
        FillRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), new Color(0f, 0f, 0f, 0.9f));
        FillRect(rect, new Color(0.15f, 0.16f, 0.19f, 1f));
        GUI.Label(new Rect(rect.x + S(12f), rect.y + S(6f), rect.width - S(24f), S(22f)), "Shortcuts", _titleStyle);
        var keyWidth = S(210f);
        for (var i = 0; i < HelpLines.Length; i++)
        {
            var (keys, what) = HelpLines[i];
            var y = rect.y + S(32f) + i * lineHeight;
            if (what.Length == 0) { GUI.Label(new Rect(rect.x + S(12f), y, rect.width - S(24f), lineHeight), keys, _fieldLabelStyle); continue; }
            GUI.Label(new Rect(rect.x + S(24f), y, keyWidth, lineHeight), keys, _monoStyle);
            GUI.Label(new Rect(rect.x + S(24f) + keyWidth, y, rect.width - S(36f) - keyWidth, lineHeight), what, _fieldLabelStyle);
        }
    }

    // ------------------------------------------------------------------ the properties of several nodes

    private void DrawMultiProperties(SC2MissionStage stage)
    {
        GUILayout.Label(_multi.Count + " nodes selected", _titleStyle);
        GUILayout.Label("Drag any of them to move them together.", _noteStyle);
        GUILayout.Space(S(6f));

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Duplicate", GUILayout.Height(S(24f)))) DuplicateSelection(stage);
        if (GUILayout.Button("Copy", GUILayout.Height(S(24f)))) CopySelection(stage);
        if (GUILayout.Button("Delete", GUILayout.Height(S(24f)))) DeleteSelection();
        GUILayout.EndHorizontal();

        GUILayout.Space(S(6f));
        GUILayout.Label("Arrange", _fieldLabelStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Align left", GUILayout.Height(S(24f)))) ApplyToSelection(stage, "Align nodes", SC2MissionNodeLayoutTools.AlignLeft);
        if (GUILayout.Button("Align top", GUILayout.Height(S(24f)))) ApplyToSelection(stage, "Align nodes", SC2MissionNodeLayoutTools.AlignTop);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Spread across", GUILayout.Height(S(24f)))) ApplyToSelection(stage, "Distribute nodes", SC2MissionNodeLayoutTools.DistributeHorizontally);
        if (GUILayout.Button("Spread down", GUILayout.Height(S(24f)))) ApplyToSelection(stage, "Distribute nodes", SC2MissionNodeLayoutTools.DistributeVertically);
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Snap to grid", GUILayout.Height(S(24f)))) ApplyToSelection(stage, "Snap nodes", nodes => SC2MissionNodeLayoutTools.SnapToGrid(nodes));
        if (GUILayout.Button("Group in a frame", GUILayout.Height(S(24f)))) GroupInFrame(stage);
    }
}
