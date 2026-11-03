using System;
using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

public sealed partial class SC2MissionPanel
{
    private struct NodeBox
    {
        public Rect Rect;
        public float HeaderHeight;
        /// <summary>The first input; a node with one input has no other.</summary>
        public Vector2 InPort;
        public List<KeyValuePair<string, Vector2>> InPorts;
        /// <summary>Rows taken by the named inputs under the header; 0 for a node with a single input, whose port sits on the header.</summary>
        public int InputRows;
        public List<KeyValuePair<string, Vector2>> OutPorts;
        public List<string> Lines;
    }

    /// <summary>Graph positions are stored in units that the UI scale turns into pixels, so a graph looks the same at every scale.</summary>
    private static float K => SC2UiScale.Pxf(1f);

    private static Rect StartRect => new(-170f * K, 0f, 100f * K, 44f * K);

    private const float MinZoom = 0.3f;
    private const float MaxZoom = 1.6f;
    private readonly Dictionary<string, float> _zoom = new();
    private readonly HashSet<string> _fitted = new();

    private Vector2 _canvasSize;
    private Vector2 _lastLocalMouse;
    private float _edgePanAt = -1f;

    // the search that opens when a wire is let go over empty canvas, or on a double click of the canvas
    private bool _quickOpen;
    private bool _quickFocus;
    private bool _quickScrollToSel;
    private int _quickFromNode = -1;
    private string _quickFromPort = "";
    private string _quickSearch = "";
    private string _quickMatchesFor;
    private List<SC2MissionNodeDef> _quickMatches = new();
    private int _quickSel;
    private Vector2 _quickScroll;
    private Vector2 _quickLocal;
    private Vector2 _quickGraphMouse;

    private float ZoomOf(SC2MissionStage stage) => _zoom.TryGetValue(stage.Id, out var z) ? z : 1f;

    /// <summary>Zooms and pans so every node of the stage (and the Start block) is visible in a canvas of the given size.</summary>
    private void FitView(SC2MissionStage stage, Vector2 canvasSize, bool keepReadable = false)
    {
        var minX = StartRect.x;
        var minY = 0f;
        var maxX = StartRect.xMax;
        var maxY = StartRect.yMax;
        foreach (var n in stage.Nodes)
        {
            var def = SC2MissionNodeCatalog.Find(n.Type);
            var inputCount = SC2MissionNodeCatalog.InputsOf(def, n).Length;
            var height = S(24f + 18f * 3f + 18f * (SC2MissionNodeCatalog.PortsOf(def, n).Length + (inputCount > 1 ? inputCount : 0)) + 6f);
            var width = S(220f);
            if (def != null && def.IsComment) { var size = FrameSize(n); width = size.x * K; height = size.y * K; }
            minX = Mathf.Min(minX, n.X * K);
            minY = Mathf.Min(minY, n.Y * K);
            maxX = Mathf.Max(maxX, n.X * K + width);
            maxY = Mathf.Max(maxY, n.Y * K + height);
        }
        var margin = S(28f);
        // Opening a stage never shrinks the text below what can be read; the Fit all button shows everything.
        var z = Mathf.Clamp(Mathf.Min((canvasSize.x - margin * 2f) / Mathf.Max(1f, maxX - minX), (canvasSize.y - margin * 2f) / Mathf.Max(1f, maxY - minY)), keepReadable ? 0.6f : MinZoom, 1f);
        _zoom[stage.Id] = z;
        _pan[stage.Id] = new Vector2(margin / z - minX, margin / z - minY);
        _fitted.Add(stage.Id);
    }

    private void ZoomAt(SC2MissionStage stage, Vector2 screenPoint, float factor)
    {
        var z = ZoomOf(stage);
        var next = Mathf.Clamp(z * factor, MinZoom, MaxZoom);
        if (Mathf.Approximately(next, z)) return;
        var pan = PanOf(stage);
        // keep the graph point under screenPoint where it is
        _pan[stage.Id] = pan + screenPoint * (1f / next - 1f / z);
        _zoom[stage.Id] = next;
    }

    // ------------------------------------------------------------------ stage body: library | canvas | properties

    private void DrawStageBody(Rect body)
    {
        var stage = CurrentStage();
        if (stage == null)
        {
            GUI.Label(new Rect(body.x, body.y, body.width, S(40f)), "Add a stage with + Stage to start building the mission.", _noteStyle);
            return;
        }

        var gap = S(8f);
        var minCanvas = S(320f);
        var propsWidth = Mathf.Min(S(_propsW), Mathf.Max(S(200f), body.width - minCanvas - S(160f) - gap * 2f));
        var libraryWidth = Mathf.Min(S(_libraryW), Mathf.Max(S(140f), body.width - propsWidth - minCanvas - gap * 2f));
        var library = new Rect(body.x, body.y, libraryWidth, body.height);
        var props = new Rect(body.xMax - propsWidth, body.y, propsWidth, body.height);
        var canvas = new Rect(library.xMax + gap, body.y, body.width - libraryWidth - propsWidth - gap * 2f, body.height);

        DrawLibrary(library, stage, canvas);
        DrawCanvas(canvas, stage);
        DrawProperties(props, stage);

        // the borders between the three panels can be dragged
        Splitter(1, new Rect(library.xMax, body.y, gap, body.height), true, ref _libraryW, 150f, 480f, 1f);
        Splitter(2, new Rect(canvas.xMax, body.y, gap, body.height), true, ref _propsW, 220f, 620f, -1f);
        DrawLibraryGhost(stage);
    }

    private static float _libraryW = 240f;
    private static float _propsW = 310f;
    private static float _bottomH = 170f;
    private int _splitterActive;
    private Vector2 _splitterLast;

    /// <summary>A strip between two panels that moves the border when it is dragged. value is a size in unscaled units; direction says which way
    /// a drag to the right (or down) changes it.</summary>
    private void Splitter(int key, Rect strip, bool horizontal, ref float value, float min, float max, float direction)
    {
        var e = Event.current;
        var hot = _splitterActive == key;
        if (e.type == EventType.Repaint)
        {
            var hover = strip.Contains(e.mousePosition);
            if (hot || hover) FillRect(new Rect(strip.x + (horizontal ? strip.width * 0.35f : 0f), strip.y + (horizontal ? 0f : strip.height * 0.35f),
                horizontal ? strip.width * 0.3f : strip.width, horizontal ? strip.height : strip.height * 0.3f), new Color(0.55f, 0.75f, 1f, hot ? 0.9f : 0.5f));
        }
        switch (e.type)
        {
            case EventType.MouseDown:
                if (e.button == 0 && strip.Contains(e.mousePosition)) { _splitterActive = key; _splitterLast = e.mousePosition; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (!hot) break;
                // the pointer positions are compared (the delta of a synthesized event is zero)
                var moved = e.mousePosition - _splitterLast;
                _splitterLast = e.mousePosition;
                value = Mathf.Clamp(value + direction * (horizontal ? moved.x : moved.y) / S(1f), min, max);
                e.Use();
                RepaintHost?.Invoke();
                break;
            case EventType.MouseUp:
                if (!hot) break;
                _splitterActive = 0;
                e.Use();
                SaveLayout();
                break;
        }
    }

    private const string PrefLibrary = "SC2Mission.libraryWidth";
    private const string PrefProps = "SC2Mission.propsWidth";
    private const string PrefBottom = "SC2Mission.bottomHeight";

    private static void LoadLayout()
    {
        try
        {
            _libraryW = Mathf.Clamp(PlayerPrefs.GetFloat(PrefLibrary, _libraryW), 150f, 480f);
            _propsW = Mathf.Clamp(PlayerPrefs.GetFloat(PrefProps, _propsW), 220f, 620f);
            _bottomH = Mathf.Clamp(PlayerPrefs.GetFloat(PrefBottom, _bottomH), 110f, 600f);
        }
        catch (Exception) { /* the sizes are a convenience; the defaults do */ }
    }

    private static void SaveLayout()
    {
        try
        {
            PlayerPrefs.SetFloat(PrefLibrary, _libraryW);
            PlayerPrefs.SetFloat(PrefProps, _propsW);
            PlayerPrefs.SetFloat(PrefBottom, _bottomH);
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ library

    // the nodes added last, shown first; and the categories the user folded away
    private static readonly List<string> _recent = new();
    private static readonly HashSet<string> _foldedCategories = new();

    private string _libPressed;
    private bool _libDragged;
    private Vector2 _libPressScreen;
    private Vector2 _libDragScreen;
    private string _libTip = "";
    private Rect _canvasScreen;

    private static void RememberRecent(string type)
    {
        _recent.Remove(type);
        _recent.Insert(0, type);
        if (_recent.Count > 6) _recent.RemoveRange(6, _recent.Count - 6);
    }

    private void DrawLibrary(Rect r, SC2MissionStage stage, Rect canvas)
    {
        var e = Event.current;
        // a button that is held uses the drag and release events, so what they were is taken before the rows are drawn
        var incoming = e.type;
        var incomingAt = e.mousePosition;
        GUI.Box(r, GUIContent.none);
        GUILayout.BeginArea(new Rect(r.x + S(6f), r.y + S(6f), r.width - S(12f), r.height - S(12f)));
        GUILayout.Label("Nodes (" + SC2MissionNodeCatalog.All.Length + ")", _titleStyle);
        GUI.SetNextControlName("m_library_search");
        _librarySearch = GUILayout.TextField(_librarySearch ?? "", GUILayout.Height(S(22f)));
        var query = (_librarySearch ?? "").Trim();
        _libraryScroll = GUILayout.BeginScrollView(_libraryScroll);
        // The button height is worked out from the column width, because a wrapped title must get the room of every line it needs;
        // the width is kept a little short of the real one so that a title that just fits is never measured as one line too few.
        var titleWidth = r.width - S(12f) - S(26f) - _libraryButtonStyle.padding.horizontal - S(8f);
        _libTip = "";

        if (query.Length == 0 && _recent.Count > 0)
        {
            var recentHeader = GUILayoutUtility.GetRect(0, S(18f), GUILayout.ExpandWidth(true));
            FillRect(recentHeader, new Color(0.45f, 0.47f, 0.55f, 0.85f));
            GUI.Label(recentHeader, "  Recent", _nodeTitleStyle);
            foreach (var type in _recent.ToList())
            {
                var recent = SC2MissionNodeCatalog.Find(type);
                if (recent != null) LibraryRow(recent, stage, canvas, titleWidth);
            }
            GUILayout.Space(S(6f));
        }

        foreach (var category in SC2MissionNodeCatalog.Categories())
        {
            // with a search the categories that have no match are left out, and none is folded
            var count = 0;
            foreach (var def in SC2MissionNodeCatalog.All)
                if (def.Category == category && MatchesSearch(def, query)) count++;
            if (count == 0) continue;

            var folded = query.Length == 0 && _foldedCategories.Contains(category);
            var color = CategoryColor(category);
            var header = GUILayoutUtility.GetRect(0, S(20f), GUILayout.ExpandWidth(true));
            FillRect(header, new Color(color.r, color.g, color.b, 0.75f));
            GUI.Label(header, (folded ? "  >  " : "  v  ") + category + "  (" + count + ")", _nodeTitleStyle);
            if (GUI.Button(header, "", GUIStyle.none) && query.Length == 0)
            {
                if (!_foldedCategories.Remove(category)) _foldedCategories.Add(category);
            }
            if (!folded)
                foreach (var def in SC2MissionNodeCatalog.All)
                    if (def.Category == category && MatchesSearch(def, query)) LibraryRow(def, stage, canvas, titleWidth);
            GUILayout.Space(S(6f));
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();

        // dragging a node out of the library: the release over the canvas puts the node there
        if (_libPressed != null)
        {
            if (incoming == EventType.MouseDrag)
            {
                _libDragScreen = GUIUtility.GUIToScreenPoint(incomingAt);
                if ((_libDragScreen - _libPressScreen).magnitude > S(6f)) _libDragged = true;
                RepaintHost?.Invoke();
            }
            else if (incoming == EventType.MouseUp)
            {
                _libDragScreen = GUIUtility.GUIToScreenPoint(incomingAt);
                if (_libDragged && _canvasScreen.Contains(_libDragScreen)) AddNodeAt(stage, _libPressed, _libDragScreen - _canvasScreen.position);
                _libPressed = null;
                _libDragged = false;
            }
        }
    }

    private void LibraryRow(SC2MissionNodeDef def, SC2MissionStage stage, Rect canvas, float titleWidth)
    {
        var e = Event.current;
        var height = Mathf.Max(S(22f), _libraryButtonStyle.CalcHeight(new GUIContent(def.Title), titleWidth + _libraryButtonStyle.padding.horizontal) + S(2f));
        var content = new GUIContent(def.Title, SC2MissionNodeDescriptions.Of(def));
        // the row is reserved first, so that its rectangle is known in every event (the press that starts a drag is found by it)
        var rect = GUILayoutUtility.GetRect(content, _libraryButtonStyle, GUILayout.Height(height));
        if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            _libPressed = def.Type;
            _libDragged = false;
            _libPressScreen = GUIUtility.GUIToScreenPoint(e.mousePosition);
        }
        var clicked = GUI.Button(rect, content, _libraryButtonStyle);
        if (e.type == EventType.Repaint && rect.Contains(e.mousePosition) && !string.IsNullOrEmpty(GUI.tooltip)) _libTip = GUI.tooltip;
        if (clicked && !_libDragged) AddNodeFromLibrary(stage, def.Type, canvas);
    }

    /// <summary>The tooltip of the node under the pointer in the library, or the name of the node being dragged out of it; drawn over everything.</summary>
    private void DrawLibraryGhost(SC2MissionStage stage)
    {
        var e = Event.current;
        if (e.type != EventType.Repaint) return;
        if (_libPressed != null && _libDragged)
        {
            var def = SC2MissionNodeCatalog.Find(_libPressed);
            var at = GUIUtility.ScreenToGUIPoint(_libDragScreen);
            var rect = new Rect(at.x + S(10f), at.y + S(6f), S(220f), S(24f));
            FillRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), new Color(1f, 1f, 1f, 0.8f));
            FillRect(rect, def != null ? CategoryColor(def.Category) : new Color(0.3f, 0.3f, 0.3f));
            GUI.Label(rect, "  " + (def?.Title ?? _libPressed), _nodeTitleStyle);
            return;
        }
        if (!string.IsNullOrEmpty(_libTip))
        {
            var content = new GUIContent(_libTip);
            var width = S(280f);
            var height = _noteStyle.CalcHeight(content, width - S(12f)) + S(8f);
            var rect = new Rect(e.mousePosition.x + S(16f), e.mousePosition.y + S(18f), width, height);
            FillRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), new Color(0f, 0f, 0f, 0.9f));
            FillRect(rect, new Color(0.18f, 0.19f, 0.23f, 1f));
            GUI.Label(new Rect(rect.x + S(6f), rect.y + S(4f), width - S(12f), height), content, _noteStyle);
        }
    }

    private static bool MatchesSearch(SC2MissionNodeDef def, string query) =>
        query.Length == 0 || def.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || def.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
        || def.Type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Adds a node with its input port at a point of the canvas (the point in the canvas's own pixels).</summary>
    private void AddNodeAt(SC2MissionStage stage, string type, Vector2 canvasPoint)
    {
        Commit();
        var pan = PanOf(stage);
        var rel = canvasPoint / ZoomOf(stage) - pan;
        var node = stage.AddNode(type, SnapUnits(rel.x / K), SnapUnits((rel.y - S(12f)) / K));
        RememberRecent(type);
        SelectOnly(node.Id);
        Mutate("Add " + SC2MissionNodeCatalog.Find(type)?.Title);
        Commit();
    }

    private void AddNodeFromLibrary(SC2MissionStage stage, string type, Rect canvas)
    {
        RememberRecent(type);
        Commit();
        var pan = PanOf(stage);
        var z = ZoomOf(stage);
        var count = stage.Nodes.Count;
        var node = stage.AddNode(type, (canvas.width * 0.5f / z - pan.x - S(110f)) / K + (count % 6) * 18f, (canvas.height * 0.3f / z - pan.y) / K + (count % 6) * 18f);
        SelectOnly(node.Id);
        Mutate("Add " + SC2MissionNodeCatalog.Find(type)?.Title);
        Commit();
    }

    // ------------------------------------------------------------------ canvas

    private Vector2 PanOf(SC2MissionStage stage)
    {
        if (!_pan.TryGetValue(stage.Id, out var pan)) { pan = FramePan(stage); _pan[stage.Id] = pan; }
        return pan;
    }

    /// <summary>The pan that brings the top-left corner of the stage's nodes (and the Start block) into view.</summary>
    private static Vector2 FramePan(SC2MissionStage stage)
    {
        var minX = StartRect.x;
        var minY = 0f;
        foreach (var n in stage.Nodes)
        {
            minX = Mathf.Min(minX, n.X * K);
            minY = Mathf.Min(minY, n.Y * K);
        }
        return new Vector2(S(24f) - minX, S(48f) - minY);
    }

    private NodeBox Layout(SC2MissionNode node, SC2MissionNodeDef def, Vector2 pan)
    {
        // A node grows to fit its title, the title being what tells nodes of one category apart.
        if (def.IsComment)
        {
            var size = FrameSize(node);
            var frame = new Rect(pan.x + node.X * K, pan.y + node.Y * K, size.x * K, size.y * K);
            return new NodeBox
            {
                Rect = frame, HeaderHeight = S(24f), InPort = frame.position, Lines = new List<string>(),
                InPorts = new List<KeyValuePair<string, Vector2>>(), OutPorts = new List<KeyValuePair<string, Vector2>>(),
            };
        }
        var width = Mathf.Clamp(S(76f) + def.Title.Length * S(6.8f), S(220f), S(440f));
        var maxChars = Mathf.Max(34, Mathf.FloorToInt((width - S(16f)) / S(5.6f)));
        var lines = new List<string>();
        foreach (var p in def.Params)
        {
            var value = node.GetParam(p.Key, p.Default);
            if (string.IsNullOrEmpty(value)) continue;
            var firstLine = value.Replace("\r", "").Split('\n')[0];
            var text = p.Label + ": " + firstLine;
            if (text.Length > maxChars) text = text.Substring(0, maxChars - 1) + "…";
            lines.Add(text);
            if (lines.Count == 3) break;
        }

        var header = S(24f);
        var row = S(18f);
        var ports = SC2MissionNodeCatalog.PortsOf(def, node);
        var inputs = SC2MissionNodeCatalog.InputsOf(def, node);
        // a node with several inputs lists them in rows under the header; one input sits on the header itself
        var inputRows = inputs.Length > 1 ? inputs.Length : 0;
        var height = header + inputRows * row + lines.Count * row + ports.Length * row + S(6f);
        var rect = new Rect(pan.x + node.X * K, pan.y + node.Y * K, width, height);
        var box = new NodeBox
        {
            Rect = rect,
            HeaderHeight = header,
            InputRows = inputRows,
            InPort = new Vector2(rect.x, rect.y + header * 0.5f),
            InPorts = new List<KeyValuePair<string, Vector2>>(),
            OutPorts = new List<KeyValuePair<string, Vector2>>(),
            Lines = lines,
        };
        for (var i = 0; i < inputs.Length; i++)
            box.InPorts.Add(new KeyValuePair<string, Vector2>(inputs[i], inputRows == 0 ? box.InPort : new Vector2(rect.x, rect.y + header + i * row + row * 0.5f)));
        if (box.InPorts.Count > 0) box.InPort = box.InPorts[0].Value;
        for (var i = 0; i < ports.Length; i++)
            box.OutPorts.Add(new KeyValuePair<string, Vector2>(ports[i], new Vector2(rect.xMax, rect.y + header + (inputRows + lines.Count) * row + i * row + row * 0.5f)));
        return box;
    }

    private Dictionary<int, NodeBox> LayoutAll(SC2MissionStage stage, Vector2 pan)
    {
        var boxes = new Dictionary<int, NodeBox>();
        foreach (var node in stage.Nodes)
        {
            var def = SC2MissionNodeCatalog.Find(node.Type);
            if (def == null) continue;
            boxes[node.Id] = Layout(node, def, pan);
        }
        return boxes;
    }

    private static Vector2 StartPort(Vector2 pan) => new(pan.x + StartRect.xMax, pan.y + StartRect.y + StartRect.height * 0.5f);

    private bool TryPortPosition(Dictionary<int, NodeBox> boxes, Vector2 pan, int nodeId, string port, out Vector2 position)
    {
        position = default;
        if (nodeId == SC2MissionStage.StartNodeId) { position = StartPort(pan); return true; }
        if (!boxes.TryGetValue(nodeId, out var box)) return false;
        foreach (var p in box.OutPorts)
            if (p.Key == port) { position = p.Value; return true; }
        return false;
    }

    /// <summary>Where the wire to the named input of a node ends; the first input when the name is not found.</summary>
    private static Vector2 InPortOf(NodeBox box, string input)
    {
        foreach (var p in box.InPorts)
            if (p.Key == (input ?? SC2MissionNodeCatalog.DefaultInput)) return p.Value;
        return box.InPort;
    }

    private static List<Vector2> Bezier(Vector2 from, Vector2 to)
    {
        var dx = Mathf.Max(S(50f), Mathf.Abs(to.x - from.x) * 0.5f);
        var c1 = from + new Vector2(dx, 0f);
        var c2 = to - new Vector2(dx, 0f);
        // a wire that goes backwards (a loop's body to its next) swings below the nodes instead of running through them
        if (to.x < from.x + S(30f))
        {
            var swing = S(70f) + Mathf.Abs(to.y - from.y) * 0.2f + Mathf.Min(S(160f), (from.x - to.x) * 0.15f);
            dx = S(90f) + Mathf.Min(S(240f), Mathf.Abs(to.x - from.x) * 0.3f);
            c1 = from + new Vector2(dx, swing);
            c2 = to + new Vector2(-dx, swing);
        }
        var points = new List<Vector2>(25);
        for (var i = 0; i <= 24; i++)
        {
            var t = i / 24f;
            var u = 1f - t;
            points.Add(u * u * u * from + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * to);
        }
        return points;
    }

    private static float DistanceToPolyline(Vector2 p, List<Vector2> points)
    {
        var best = float.MaxValue;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var ab = points[i + 1] - a;
            var t = ab.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
        }
        return best;
    }

    private void DrawCanvas(Rect r, SC2MissionStage stage)
    {
        GUI.Box(r, GUIContent.none);
        if (!_fitted.Contains(stage.Id)) FitView(stage, r.size, keepReadable: true);
        _canvasSize = r.size;
        var e = Event.current;
        if (e.type == EventType.Repaint)
        {
            _canvasScreen = new Rect(GUIUtility.GUIToScreenPoint(r.position), r.size);
            ApplyEdgePan(stage, r.size);
            UpdateIssueCounts(stage);
        }
        var pan = PanOf(stage);
        var zoom = ZoomOf(stage);
        var boxes = LayoutAll(stage, pan);
        var id = GUIUtility.GetControlID(FocusType.Passive);

        // The events are read in the clip's own coordinates (its top-left is 0,0). The graph is laid out in unzoomed pixels and drawn
        // multiplied by the zoom, so the mouse is divided by the zoom before it is compared with the layout.
        GUI.BeginClip(r);
        var local = new Rect(0, 0, r.width, r.height);
        if (e.type == EventType.Repaint)
        {
            _hoverLocal = e.mousePosition;
            UpdateHover(stage, pan, zoom, boxes);
            DrawCanvasContents(local, zoom, stage, pan, boxes);
        }
        HandleCanvasInput(id, local, zoom, stage, pan, boxes);
        GUI.EndClip();

        if (stage.Nodes.Count == 0 && e.type == EventType.Repaint)
            GUI.Label(new Rect(r.x + S(20f), r.yMax - S(90f), r.width - S(40f), S(60f)),
                "Click a node in the library to add it, or drag it onto the canvas. Double-click or right-click here to search for a node. Drag from a port to another node to connect them, or let go over empty space to search for the next node. Home shows everything; F1 lists the shortcuts.", _noteStyle);

        DrawToolbar(r, stage, zoom);

        if (e.type == EventType.Repaint)
        {
            var bar = new Rect(r.x, r.yMax - S(22f), r.width, S(22f));
            FillRect(bar, new Color(0f, 0f, 0f, 0.5f));
            GUI.Label(new Rect(bar.x + S(8f), bar.y, bar.width - S(16f), bar.height), StatusText(stage), _nodeTextStyle);
        }

        DrawQuickAdd(r, stage);
        DrawHelp(r);
    }

    private static float FitButtonWidth => S(120f);

    // widths of the buttons in the corner of the canvas, right to left, and the gap between them
    private static readonly float[] ToolbarWidths = { 120f, 30f, 30f, 56f, 102f, 62f, 52f, 28f };

    private static float ToolbarWidth
    {
        get
        {
            var total = S(6f);
            foreach (var w in ToolbarWidths) total += S(w) + S(4f);
            return total;
        }
    }

    private void DrawToolbar(Rect r, SC2MissionStage stage, float zoom)
    {
        var x = r.xMax - S(6f);
        Rect Next(float width)
        {
            x -= S(width);
            var rect = new Rect(x, r.y + S(6f), S(width), S(24f));
            x -= S(4f);
            return rect;
        }

        if (GUI.Button(Next(ToolbarWidths[0]), "Home (fit all)")) FitView(stage, r.size);
        if (GUI.Button(Next(ToolbarWidths[1]), "+")) ZoomAt(stage, r.size * 0.5f, 1.25f);
        if (GUI.Button(Next(ToolbarWidths[2]), "-")) ZoomAt(stage, r.size * 0.5f, 0.8f);
        GUI.Label(Next(ToolbarWidths[3]), Mathf.RoundToInt(zoom * 100f) + "%", _noteStyle);
        if (GUI.Button(Next(ToolbarWidths[4]), "Auto-layout"))
        {
            Commit();
            SC2MissionGraphLayout.AutoLayout(stage);
            Mutate("Auto-layout mission graph");
            Commit();
            FitView(stage, r.size);
        }
        _snap = GUI.Toggle(Next(ToolbarWidths[5]), _snap, "Snap", GUI.skin.button);
        _showMinimap = GUI.Toggle(Next(ToolbarWidths[6]), _showMinimap, "Map", GUI.skin.button);
        if (GUI.Button(Next(ToolbarWidths[7]), "?")) _helpOpen = !_helpOpen;
    }

    private void DrawFrame(NodeBox box, SC2MissionNode node, float z, bool selected)
    {
        var rect = Zr(box.Rect, z);
        var c = FrameColor(node.GetParam("color", "gray"));
        FillRect(rect, new Color(c.r, c.g, c.b, 0.10f));
        FrameRectOutline(rect, new Color(c.r, c.g, c.b, selected ? 1f : 0.55f));
        var header = Zr(new Rect(box.Rect.x, box.Rect.y, box.Rect.width, box.HeaderHeight), z);
        FillRect(header, new Color(c.r, c.g, c.b, selected ? 0.45f : 0.28f));
        GUI.Label(new Rect(header.x + S(8f) * z, header.y, header.width - S(16f) * z, header.height), node.GetParam("text", "Group"), _nodeTitleStyle);
        var h = Zr(FrameHandle(box.Rect), z);
        FillRect(new Rect(h.x + h.width * 0.4f, h.y + h.height * 0.4f, h.width * 0.6f, h.height * 0.6f), new Color(c.r, c.g, c.b, 0.85f));
    }

    private void DrawCanvasContents(Rect local, float z, SC2MissionStage stage, Vector2 pan, Dictionary<int, NodeBox> boxes)
    {
        FillRect(local, new Color(0.10f, 0.11f, 0.13f, 1f));

        // fonts follow the zoom, but never below what can be read; the details of a node are left out when it is too small to read them
        var titleSize = _nodeTitleStyle.fontSize;
        var textSize = _nodeTextStyle.fontSize;
        var portSize = _portLabelStyle.fontSize;
        _nodeTitleStyle.fontSize = Mathf.Max(9, Mathf.RoundToInt(titleSize * z));
        _nodeTextStyle.fontSize = Mathf.Max(7, Mathf.RoundToInt(textSize * z));
        _portLabelStyle.fontSize = Mathf.Max(7, Mathf.RoundToInt(portSize * z));
        var detail = z >= 0.5f;

        // grid
        var grid = S(40f) * z;
        var gridColor = new Color(1f, 1f, 1f, 0.045f);
        for (var x = Mathf.Repeat(pan.x * z, grid); x < local.width; x += grid) FillRect(new Rect(x, 0, 1f, local.height), gridColor);
        for (var y = Mathf.Repeat(pan.y * z, grid); y < local.height; y += grid) FillRect(new Rect(0, y, local.width, 1f), gridColor);

        // frames lie behind everything
        foreach (var node in stage.Nodes)
            if (IsFrame(node) && boxes.TryGetValue(node.Id, out var frameBox)) DrawFrame(frameBox, node, z, IsSelected(node.Id));

        // wires: those of a selected or hovered node are brighter and thicker
        for (var i = 0; i < stage.Edges.Count; i++)
        {
            var edge = stage.Edges[i];
            if (!TryPortPosition(boxes, pan, edge.FromNode, edge.FromPort, out var from)) continue;
            if (!boxes.TryGetValue(edge.ToNode, out var to)) continue;
            var selected = i == _selEdge;
            var related = IsSelected(edge.FromNode) || IsSelected(edge.ToNode) || i == _hoverEdge || edge.FromNode == _hoverNode || edge.ToNode == _hoverNode;
            var color = selected ? new Color(1f, 0.9f, 0.3f, 1f) : related ? new Color(0.98f, 0.88f, 0.55f, 0.98f) : new Color(0.7f, 0.78f, 0.9f, 0.7f);
            var width = selected ? 3.5f : related ? 3f : 2.2f;
            var points = Bezier(from, InPortOf(to, edge.ToPort));
            for (var p = 0; p + 1 < points.Count; p++) DrawLine(points[p] * z, points[p + 1] * z, color, Mathf.Max(1.2f, S(width) * z));
        }

        // start node
        var startRect = Zr(new Rect(pan.x + StartRect.x, pan.y + StartRect.y, StartRect.width, StartRect.height), z);
        FillRect(new Rect(startRect.x - 1f, startRect.y - 1f, startRect.width + 2f, startRect.height + 2f), new Color(0f, 0f, 0f, 0.8f));
        FillRect(startRect, new Color(0.17f, 0.30f, 0.20f, 1f));
        GUI.Label(startRect, " Start", _nodeTitleStyle);
        DrawPort(StartPort(pan) * z, true, z);

        // nodes
        foreach (var node in stage.Nodes)
        {
            if (!boxes.TryGetValue(node.Id, out var box)) continue;
            var def = SC2MissionNodeCatalog.Find(node.Type);
            if (def.IsComment) continue;
            var selected = IsSelected(node.Id);
            _issueCounts.TryGetValue(node.Id, out var issues);
            var color = CategoryColor(def.Category);
            var rect = Zr(box.Rect, z);
            var header = box.HeaderHeight * z;
            var border = selected ? new Color(1f, 0.9f, 0.3f, 1f)
                : issues.x > 0 ? new Color(0.95f, 0.30f, 0.30f, 1f)
                : issues.y > 0 ? new Color(0.95f, 0.70f, 0.20f, 0.95f)
                : node.Id == _hoverNode ? new Color(1f, 1f, 1f, 0.6f)
                : new Color(0f, 0f, 0f, 0.8f);
            FillRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), border);
            FillRect(rect, new Color(0.16f, 0.17f, 0.20f, 1f));
            FillRect(new Rect(rect.x, rect.y, rect.width, header), color);
            GUI.Label(new Rect(rect.x + S(14f) * z, rect.y, rect.width - S(66f) * z, header), def.Title, _nodeTitleStyle);
            GUI.Label(new Rect(rect.xMax - S(34f) * z, rect.y, S(30f) * z, header), "#" + node.Id, _portLabelStyle);
            if (issues.x + issues.y > 0)
                DrawBadge(new Vector2(rect.xMax - S(44f) * z, rect.y + header * 0.5f), issues.x > 0 ? new Color(0.95f, 0.25f, 0.25f) : new Color(0.98f, 0.75f, 0.2f), z);
            if (detail)
                for (var i = 0; i < box.Lines.Count; i++)
                    GUI.Label(new Rect(rect.x + S(8f) * z, rect.y + header + (box.InputRows + i) * S(18f) * z, rect.width - S(16f) * z, S(18f) * z), box.Lines[i], _nodeTextStyle);
            foreach (var input in box.InPorts)
            {
                if (detail && box.InputRows > 0)
                    GUI.Label(new Rect(rect.x + S(12f) * z, (input.Value.y - S(9f)) * z, rect.width * 0.5f, S(18f) * z), input.Key, _nodeTextStyle);
                DrawPort(input.Value * z, false, z);
            }
            foreach (var port in box.OutPorts)
            {
                if (detail) GUI.Label(new Rect(rect.x + S(8f) * z, (port.Value.y - S(9f)) * z, rect.width - S(24f) * z, S(18f) * z), port.Key, _portLabelStyle);
                DrawPort(port.Value * z, true, z);
            }
        }

        // the wire being dragged out of a port
        if (_drag == DragMode.Connect && TryPortPosition(boxes, pan, _connectNode, _connectPort, out var origin))
        {
            var points = Bezier(origin, _mouse);
            for (var p = 0; p + 1 < points.Count; p++) DrawLine(points[p] * z, points[p + 1] * z, new Color(1f, 1f, 1f, 0.6f), Mathf.Max(1.2f, S(2f) * z));
        }

        // the box being dragged
        if (_drag == DragMode.Box)
        {
            var a = (_boxStartRel + pan) * z;
            var b = (_boxEndRel + pan) * z;
            var box = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            FillRect(box, new Color(0.4f, 0.65f, 1f, 0.14f));
            FrameRectOutline(box, new Color(0.5f, 0.75f, 1f, 0.9f));
        }

        DrawMinimap(local, stage, pan, z);

        _nodeTitleStyle.fontSize = titleSize;
        _nodeTextStyle.fontSize = textSize;
        _portLabelStyle.fontSize = portSize;
    }

    private void DrawBadge(Vector2 center, Color color, float z)
    {
        var size = Mathf.Max(7f, S(12f) * z);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.9f);
        GUI.DrawTexture(new Rect(center.x - size * 0.5f - 1f, center.y - size * 0.5f - 1f, size + 2f, size + 2f), _disc);
        GUI.color = color;
        GUI.DrawTexture(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size), _disc);
        GUI.color = prev;
    }

    private static Rect Zr(Rect r, float z) => new(r.x * z, r.y * z, r.width * z, r.height * z);

    private void DrawPort(Vector2 center, bool output, float z)
    {
        var size = Mathf.Max(5f, S(12f) * z);
        var rect = new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size);
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.9f);
        GUI.DrawTexture(new Rect(rect.x - 1.5f, rect.y - 1.5f, size + 3f, size + 3f), _disc);
        GUI.color = output ? new Color(0.95f, 0.8f, 0.3f, 1f) : new Color(0.55f, 0.85f, 1f, 1f);
        GUI.DrawTexture(rect, _disc);
        GUI.color = prev;
    }

    // ------------------------------------------------------------------ canvas input

    private void HandleCanvasInput(int id, Rect local, float zoom, SC2MissionStage stage, Vector2 pan, Dictionary<int, NodeBox> boxes)
    {
        var e = Event.current;
        var graphMouse = e.mousePosition / zoom;
        var mouseEvent = e.type == EventType.MouseDown || e.type == EventType.MouseUp || e.type == EventType.MouseDrag || e.type == EventType.ScrollWheel;
        // the search box and the list of shortcuts are drawn over the canvas and take the mouse while they are open
        if ((_quickOpen || _helpOpen) && mouseEvent) return;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (!local.Contains(e.mousePosition)) break;
                // the buttons in the corner and the status line take their own clicks
                if (new Rect(local.width - ToolbarWidth, S(6f), ToolbarWidth, S(24f)).Contains(e.mousePosition)) break;
                if (e.mousePosition.y > local.height - S(22f)) break;
                _mouse = graphMouse;
                _lastLocalMouse = e.mousePosition;
                if (e.button == 0 && _showMinimap && stage.Nodes.Count > 0 && MinimapRect(local).Contains(e.mousePosition))
                {
                    BeginDrag(id, DragMode.Minimap);
                    MinimapPan(local, stage, e.mousePosition);
                    e.Use();
                    break;
                }
                OnCanvasMouseDown(id, e, graphMouse, stage, pan, boxes);
                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl != id) break;
                _mouse = graphMouse;
                _lastLocalMouse = e.mousePosition;
                switch (_drag)
                {
                    case DragMode.Pan: _pan[stage.Id] = pan + e.delta / zoom; break;
                    case DragMode.Node: MoveDragged(stage, graphMouse, pan); break;
                    case DragMode.Box: _boxEndRel = graphMouse - pan; break;
                    case DragMode.Minimap: MinimapPan(local, stage, e.mousePosition); break;
                    case DragMode.ResizeFrame: ResizeFrameTo(stage, graphMouse, pan); break;
                }
                e.Use();
                RepaintHost?.Invoke();
                break;

            case EventType.ScrollWheel:
                if (!local.Contains(e.mousePosition)) break;
                ZoomAt(stage, e.mousePosition, e.delta.y > 0f ? 0.88f : 1.14f);
                e.Use();
                RepaintHost?.Invoke();
                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl != id) break;
                GUIUtility.hotControl = 0;
                switch (_drag)
                {
                    case DragMode.Node:
                        if (_dragMoved)
                        {
                            _dirty = true;
                            if (string.IsNullOrEmpty(_pendingName)) _pendingName = _dragStarts.Count > 1 ? "Move " + _dragStarts.Count + " nodes" : "Move node";
                            Commit();
                        }
                        break;
                    case DragMode.ResizeFrame:
                        if (_dragMoved) { _dirty = true; if (string.IsNullOrEmpty(_pendingName)) _pendingName = "Resize frame"; Commit(); }
                        break;
                    case DragMode.Connect:
                        // let go over nothing: ask which node comes next, as the node editors of Blender and Unreal do
                        if (!FinishConnect(stage, boxes, graphMouse) && !boxes.Values.Any(b => b.Rect.Contains(graphMouse)))
                            OpenQuickAdd(graphMouse, e.mousePosition, _connectNode, _connectPort);
                        break;
                    case DragMode.Box:
                        FinishBox(stage, boxes, pan);
                        break;
                    case DragMode.Pan:
                        // a right click that did not drag asks for a node, as in the node editors of Unreal and Blender
                        if (_panButton == 1 && Vector2.Distance(e.mousePosition, _panStartLocal) < S(4f))
                            OpenQuickAdd(graphMouse, e.mousePosition, -1, "");
                        break;
                }
                _drag = DragMode.None;
                _dragMoved = false;
                _dragStarts.Clear();
                e.Use();
                break;
        }
    }

    private void ResizeFrameTo(SC2MissionStage stage, Vector2 graphMouse, Vector2 pan)
    {
        var node = stage.FindNode(_resizeFrame);
        if (node == null) return;
        var delta = ((graphMouse - pan) - _resizeStartRel) / K;
        var w = Mathf.Max(120f, _resizeStartSize.x + delta.x);
        var h = Mathf.Max(80f, _resizeStartSize.y + delta.y);
        if (_snap) { w = SC2MissionNodeLayoutTools.Snap(w); h = SC2MissionNodeLayoutTools.Snap(h); }
        node.SetParam("width", Mathf.RoundToInt(w).ToString());
        node.SetParam("height", Mathf.RoundToInt(h).ToString());
        _dragMoved = true;
        _workVersion++;
    }

    private void OnCanvasMouseDown(int id, Event e, Vector2 graphMouse, SC2MissionStage stage, Vector2 pan, Dictionary<int, NodeBox> boxes)
    {
        // A text field in the properties may still hold an edit; end it before something else gets selected.
        Commit();
        var p = graphMouse;
        var additive = e.shift || e.control || e.command;

        if (e.button == 2)
        {
            _panButton = 2;
            _panStartLocal = e.mousePosition;
            BeginDrag(id, DragMode.Pan);
            e.Use();
            return;
        }

        if (e.button == 0)
        {
            // output ports: the start node's and every node's
            var portRadius = S(9f);
            if (Vector2.Distance(p, StartPort(pan)) <= portRadius) { BeginConnect(id, SC2MissionStage.StartNodeId, SC2MissionStage.StartPort); e.Use(); return; }
            foreach (var node in Enumerable.Reverse(stage.Nodes))
            {
                if (!boxes.TryGetValue(node.Id, out var box)) continue;
                foreach (var port in box.OutPorts)
                    if (Vector2.Distance(p, port.Value) <= portRadius) { BeginConnect(id, node.Id, port.Key); e.Use(); return; }
            }

            // the corner handle of a frame
            foreach (var node in Enumerable.Reverse(stage.Nodes))
            {
                if (!IsFrame(node) || !boxes.TryGetValue(node.Id, out var fb) || !FrameHandle(fb.Rect).Contains(p)) continue;
                SelectOnly(node.Id);
                _resizeFrame = node.Id;
                _resizeStartSize = FrameSize(node);
                _resizeStartRel = p - pan;
                _dragMoved = false;
                BeginDrag(id, DragMode.ResizeFrame);
                e.Use();
                return;
            }
        }

        // nodes, topmost first (a frame is only taken by its header, below)
        foreach (var node in Enumerable.Reverse(stage.Nodes))
        {
            if (IsFrame(node) || !boxes.TryGetValue(node.Id, out var box) || !box.Rect.Contains(p)) continue;
            if (e.button == 0 && additive) { ToggleSelected(node.Id); e.Use(); return; }
            // clicking a node of a selection keeps the selection (so that it can be moved together); any other node becomes the selection
            if (!IsSelected(node.Id)) SelectOnly(node.Id);
            else _selNode = node.Id;
            _selEdge = -1;
            if (e.button == 0)
            {
                BeginNodeDrag(node.Id, stage, p, pan, boxes);
                BeginDrag(id, DragMode.Node);
            }
            e.Use();
            return;
        }

        foreach (var node in Enumerable.Reverse(stage.Nodes))
        {
            if (!IsFrame(node) || !boxes.TryGetValue(node.Id, out var fb)) continue;
            var header = new Rect(fb.Rect.x, fb.Rect.y, fb.Rect.width, fb.HeaderHeight);
            if (!header.Contains(p)) continue;
            if (e.button == 0 && additive) { ToggleSelected(node.Id); e.Use(); return; }
            if (!IsSelected(node.Id)) SelectOnly(node.Id);
            else _selNode = node.Id;
            _selEdge = -1;
            if (e.button == 0)
            {
                BeginNodeDrag(node.Id, stage, p, pan, boxes);
                BeginDrag(id, DragMode.Node);
            }
            e.Use();
            return;
        }

        // wires
        var hitEdge = -1;
        var hitDistance = S(7f);
        for (var i = 0; i < stage.Edges.Count; i++)
        {
            var edge = stage.Edges[i];
            if (!TryPortPosition(boxes, pan, edge.FromNode, edge.FromPort, out var from) || !boxes.TryGetValue(edge.ToNode, out var to)) continue;
            var distance = DistanceToPolyline(p, Bezier(from, InPortOf(to, edge.ToPort)));
            if (distance < hitDistance) { hitDistance = distance; hitEdge = i; }
        }
        if (hitEdge >= 0)
        {
            SelectOnly(-1);
            _selEdge = hitEdge;
            if (e.button == 1) DeleteSelection();
            e.Use();
            return;
        }

        // empty canvas: a box select with the left button (a double click asks for a node), a pan or a node search with the right one
        if (e.button == 0)
        {
            if (e.clickCount == 2)
            {
                OpenQuickAdd(graphMouse, e.mousePosition, -1, "");
                e.Use();
                return;
            }
            if (!additive) SelectOnly(-1);
            _boxAdds = additive;
            _boxStartRel = _boxEndRel = p - pan;
            BeginDrag(id, DragMode.Box);
            e.Use();
            return;
        }
        SelectOnly(-1);
        _panButton = e.button;
        _panStartLocal = e.mousePosition;
        BeginDrag(id, DragMode.Pan);
        e.Use();
    }

    private void BeginDrag(int id, DragMode mode)
    {
        GUIUtility.hotControl = id;
        _drag = mode;
    }

    private void BeginConnect(int id, int node, string port)
    {
        _connectNode = node;
        _connectPort = port;
        BeginDrag(id, DragMode.Connect);
    }

    /// <summary>Ends the wire being dragged. True when it was let go over an input (a wire made, or refused), false when it was let go elsewhere.</summary>
    private bool FinishConnect(SC2MissionStage stage, Dictionary<int, NodeBox> boxes, Vector2 p)
    {
        var radius = S(11f);
        foreach (var node in stage.Nodes)
        {
            if (!boxes.TryGetValue(node.Id, out var box)) continue;
            // the input whose port is under the pointer; a node with a single input also takes a drop on its header
            string target = null;
            var best = radius;
            foreach (var input in box.InPorts)
            {
                var d = Vector2.Distance(p, input.Value);
                if (d <= best) { best = d; target = input.Key; }
            }
            if (target == null && box.InputRows == 0 && new Rect(box.Rect.x - radius, box.Rect.y, radius * 2f, box.HeaderHeight).Contains(p) && box.InPorts.Count > 0) target = box.InPorts[0].Key;
            if (target == null) continue;
            // a wire from a node to one of its own inputs is how a loop moves on, so it is allowed when the node has more than one input
            if (node.Id == _connectNode && box.InputRows == 0) return true;
            if (stage.Edges.Any(x => x.FromNode == _connectNode && x.FromPort == _connectPort && x.ToNode == node.Id && x.ToPort == target)) return true;
            stage.Connect(_connectNode, _connectPort, node.Id, target);
            SelectOnly(-1);
            _selEdge = stage.Edges.Count - 1;
            Mutate("Connect nodes");
            Commit();
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ search to add a node (a wire let go over empty canvas, a double click)

    private void OpenQuickAdd(Vector2 graphMouse, Vector2 local, int fromNode, string fromPort)
    {
        _quickOpen = true;
        _quickFocus = true;
        _quickFromNode = fromNode;
        _quickFromPort = fromPort ?? "";
        _quickSearch = "";
        _quickMatchesFor = null;
        _quickSel = 0;
        _quickScroll = Vector2.zero;
        _quickLocal = local;
        _quickGraphMouse = graphMouse;
        RepaintHost?.Invoke();
    }

    private void CloseQuickAdd()
    {
        _quickOpen = false;
        _quickFromNode = -1;
        GUI.FocusControl("");
    }

    /// <summary>The nodes that match what was typed, those whose title starts with it first; every node while nothing is typed.</summary>
    private List<SC2MissionNodeDef> QuickMatches(string query)
    {
        query = (query ?? "").Trim();
        if (_quickMatchesFor == query) return _quickMatches;
        var list = new List<SC2MissionNodeDef>();
        foreach (var category in SC2MissionNodeCatalog.Categories())
            foreach (var def in SC2MissionNodeCatalog.All)
                if (def.Category == category && MatchesSearch(def, query)) list.Add(def);
        if (query.Length > 0)
            list = list.OrderBy(d => d.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : d.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2).ToList();
        _quickMatches = list;
        _quickMatchesFor = query;
        return list;
    }

    private void AddQuickNode(SC2MissionStage stage, string type)
    {
        Commit();
        var pan = PanOf(stage);
        // the node's input port lands where the wire was let go
        var node = stage.AddNode(type, (_quickGraphMouse.x - pan.x) / K, (_quickGraphMouse.y - pan.y - S(12f)) / K);
        if (_quickFromNode >= 0) stage.Connect(_quickFromNode, _quickFromPort, node.Id);
        RememberRecent(type);
        SelectOnly(node.Id);
        Mutate("Add " + SC2MissionNodeCatalog.Find(type)?.Title);
        Commit();
        CloseQuickAdd();
    }

    /// <summary>The search box, drawn over the canvas (r is the whole canvas): a text field and the matching nodes, Up and Down to move,
    /// Return or a click to add, Escape or a click outside to cancel.</summary>
    private void DrawQuickAdd(Rect r, SC2MissionStage stage)
    {
        if (!_quickOpen) return;
        var e = Event.current;
        var width = S(380f);
        var height = S(320f);
        var x = Mathf.Clamp(r.x + _quickLocal.x + S(10f), r.x + S(6f), Mathf.Max(r.x + S(6f), r.xMax - width - S(6f)));
        var y = Mathf.Clamp(r.y + _quickLocal.y - S(14f), r.y + S(6f), Mathf.Max(r.y + S(6f), r.yMax - height - S(6f)));
        var rect = new Rect(x, y, width, height);

        // keys first: the text field would keep Up, Down and Return for itself
        var matches = QuickMatches(_quickSearch);
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { CloseQuickAdd(); e.Use(); return; }
            if (e.keyCode == KeyCode.DownArrow) { _quickSel = Mathf.Min(_quickSel + 1, Mathf.Max(0, matches.Count - 1)); _quickScrollToSel = true; e.Use(); }
            else if (e.keyCode == KeyCode.UpArrow) { _quickSel = Mathf.Max(_quickSel - 1, 0); _quickScrollToSel = true; e.Use(); }
            else if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && matches.Count > 0)
            {
                AddQuickNode(stage, matches[Mathf.Clamp(_quickSel, 0, matches.Count - 1)].Type);
                e.Use();
                return;
            }
        }
        if (e.type == EventType.MouseDown && !rect.Contains(e.mousePosition)) { CloseQuickAdd(); e.Use(); return; }

        // opaque, so the canvas does not show through
        FillRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), new Color(0f, 0f, 0f, 0.9f));
        FillRect(rect, new Color(0.15f, 0.16f, 0.19f, 1f));
        GUI.Label(new Rect(x + S(10f), y + S(6f), width - S(20f), S(22f)), _quickFromNode >= 0 ? "Add the next node" : "Add a node", _titleStyle);
        GUI.SetNextControlName("m_quick_search");
        var typed = GUI.TextField(new Rect(x + S(10f), y + S(32f), width - S(20f), S(24f)), _quickSearch ?? "");
        if (typed != _quickSearch)
        {
            _quickSearch = typed;
            _quickSel = 0;
            _quickScroll = Vector2.zero;
            matches = QuickMatches(typed);
        }
        if (_quickFocus)
        {
            GUI.FocusControl("m_quick_search");
            if (e.type == EventType.Repaint) _quickFocus = false;
        }

        var list = new Rect(x + S(10f), y + S(62f), width - S(20f), height - S(72f));
        var rowHeight = S(22f);
        var content = new Rect(0f, 0f, list.width - S(16f), Mathf.Max(list.height, matches.Count * rowHeight));
        if (_quickScrollToSel)
        {
            var top = _quickSel * rowHeight;
            if (top < _quickScroll.y) _quickScroll.y = top;
            else if (top + rowHeight > _quickScroll.y + list.height) _quickScroll.y = top + rowHeight - list.height;
            _quickScrollToSel = false;
        }
        if (matches.Count == 0) GUI.Label(list, "No node matches.", _noteStyle);

        _quickScroll = GUI.BeginScrollView(list, _quickScroll, content);
        for (var i = 0; i < matches.Count; i++)
        {
            var def = matches[i];
            var row = new Rect(0f, i * rowHeight, content.width, rowHeight - 1f);
            if (i == _quickSel) FillRect(row, new Color(0.25f, 0.45f, 0.75f, 0.65f));
            else if (row.Contains(e.mousePosition)) FillRect(row, new Color(1f, 1f, 1f, 0.10f));
            FillRect(new Rect(row.x + 2f, row.y + 3f, 4f, row.height - 6f), CategoryColor(def.Category));
            if (GUI.Button(row, def.Title, _quickRowStyle))
            {
                GUI.EndScrollView();
                AddQuickNode(stage, def.Type);
                return;
            }
        }
        GUI.EndScrollView();
    }

    // ------------------------------------------------------------------ moving the view

    /// <summary>While a node or a wire is being dragged and the pointer is near an edge of the canvas, the view moves that way, faster the closer
    /// the pointer is to the edge, so a node can be carried further than the canvas shows.</summary>
    private void ApplyEdgePan(SC2MissionStage stage, Vector2 size)
    {
        var now = Time.realtimeSinceStartup;
        if (_drag != DragMode.Node && _drag != DragMode.Connect && _drag != DragMode.Box && _drag != DragMode.ResizeFrame) { _edgePanAt = -1f; return; }
        var dt = _edgePanAt < 0f ? 0f : Mathf.Min(0.1f, now - _edgePanAt);
        _edgePanAt = now;

        var margin = S(44f);
        var push = new Vector2(EdgePush(_lastLocalMouse.x, size.x, margin), EdgePush(_lastLocalMouse.y, size.y, margin));
        if (push == Vector2.zero) return;
        var zoom = ZoomOf(stage);
        var pan = PanOf(stage) - push * (S(520f) * dt / zoom);
        _pan[stage.Id] = pan;
        _mouse = _lastLocalMouse / zoom;
        switch (_drag)
        {
            case DragMode.Node: MoveDragged(stage, _mouse, pan); break;
            case DragMode.Box: _boxEndRel = _mouse - pan; break;
            case DragMode.ResizeFrame: ResizeFrameTo(stage, _mouse, pan); break;
        }
        RepaintHost?.Invoke();
    }

    private static float EdgePush(float value, float size, float margin) =>
        value < margin ? -Mathf.Clamp01((margin - value) / margin) : value > size - margin ? Mathf.Clamp01((value - (size - margin)) / margin) : 0f;

    /// <summary>Home: shows every node of the stage.</summary>
    private void ShowAll()
    {
        var stage = CurrentStage();
        if (stage != null && _canvasSize.x > 1f) FitView(stage, _canvasSize);
    }

    /// <summary>F: centres the view on the selected node (or shows everything when none is selected), keeping the zoom.</summary>
    private void FrameSelection()
    {
        var stage = CurrentStage();
        if (stage == null || _canvasSize.x <= 1f) return;
        var node = SelectedNode();
        var def = node == null ? null : SC2MissionNodeCatalog.Find(node.Type);
        if (node == null || def == null) { ShowAll(); return; }
        var zoom = ZoomOf(stage);
        var centre = Layout(node, def, Vector2.zero).Rect.center;
        _pan[stage.Id] = _canvasSize * 0.5f / zoom - centre;
    }

    private void DeleteSelection()
    {
        var stage = CurrentStage();
        if (stage == null) return;
        Commit();
        var ids = SelectedIds().Where(i => stage.FindNode(i) != null).ToList();
        if (ids.Count > 0)
        {
            foreach (var id in ids) stage.RemoveNode(id);
            SelectOnly(-1);
            Mutate(ids.Count > 1 ? "Delete " + ids.Count + " nodes" : "Delete node");
            Commit();
        }
        else if (_selEdge >= 0 && _selEdge < stage.Edges.Count)
        {
            stage.Edges.RemoveAt(_selEdge);
            _selEdge = -1;
            Mutate("Delete wire");
            Commit();
        }
    }
}
