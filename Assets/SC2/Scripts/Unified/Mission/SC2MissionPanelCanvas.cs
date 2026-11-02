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
            minX = Mathf.Min(minX, n.X * K);
            minY = Mathf.Min(minY, n.Y * K);
            maxX = Mathf.Max(maxX, n.X * K + S(220f));
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

        var libraryWidth = S(240f);
        var propsWidth = S(310f);
        var gap = S(8f);
        var library = new Rect(body.x, body.y, libraryWidth, body.height);
        var props = new Rect(body.xMax - propsWidth, body.y, propsWidth, body.height);
        var canvas = new Rect(library.xMax + gap, body.y, body.width - libraryWidth - propsWidth - gap * 2f, body.height);

        DrawLibrary(library, stage, canvas);
        DrawCanvas(canvas, stage);
        DrawProperties(props, stage);
    }

    // ------------------------------------------------------------------ library

    private void DrawLibrary(Rect r, SC2MissionStage stage, Rect canvas)
    {
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
        foreach (var category in SC2MissionNodeCatalog.Categories())
        {
            // with a search the categories that have no match are left out
            var any = false;
            foreach (var def in SC2MissionNodeCatalog.All)
                if (def.Category == category && MatchesSearch(def, query)) { any = true; break; }
            if (!any) continue;

            var color = CategoryColor(category);
            var header = GUILayoutUtility.GetRect(0, S(18f), GUILayout.ExpandWidth(true));
            FillRect(header, new Color(color.r, color.g, color.b, 0.75f));
            GUI.Label(header, "  " + category, _nodeTitleStyle);
            foreach (var def in SC2MissionNodeCatalog.All)
            {
                if (def.Category != category || !MatchesSearch(def, query)) continue;
                var height = Mathf.Max(S(22f), _libraryButtonStyle.CalcHeight(new GUIContent(def.Title), titleWidth + _libraryButtonStyle.padding.horizontal) + S(2f));
                if (GUILayout.Button(def.Title, _libraryButtonStyle, GUILayout.Height(height)))
                    AddNodeFromLibrary(stage, def.Type, canvas);
            }
            GUILayout.Space(S(6f));
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private static bool MatchesSearch(SC2MissionNodeDef def, string query) =>
        query.Length == 0 || def.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || def.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
        || def.Type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private void AddNodeFromLibrary(SC2MissionStage stage, string type, Rect canvas)
    {
        Commit();
        var pan = PanOf(stage);
        var z = ZoomOf(stage);
        var count = stage.Nodes.Count;
        var node = stage.AddNode(type, (canvas.width * 0.5f / z - pan.x - S(110f)) / K + (count % 6) * 18f, (canvas.height * 0.3f / z - pan.y) / K + (count % 6) * 18f);
        _selNode = node.Id;
        _selEdge = -1;
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
        var width = Mathf.Clamp(S(56f) + def.Title.Length * S(6.8f), S(220f), S(420f));
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
        var pan = PanOf(stage);
        var zoom = ZoomOf(stage);
        var boxes = LayoutAll(stage, pan);
        var id = GUIUtility.GetControlID(FocusType.Passive);
        var e = Event.current;

        // The events are read in the clip's own coordinates (its top-left is 0,0). The graph is laid out in unzoomed pixels and drawn
        // multiplied by the zoom, so the mouse is divided by the zoom before it is compared with the layout.
        GUI.BeginClip(r);
        var local = new Rect(0, 0, r.width, r.height);

        if (e.type == EventType.Repaint) DrawCanvasContents(local, zoom, stage, pan, boxes);
        HandleCanvasInput(id, local, zoom, stage, pan, boxes);
        GUI.EndClip();

        if (stage.Nodes.Count == 0 && e.type == EventType.Repaint)
            GUI.Label(new Rect(r.x + S(20f), r.y + r.height - S(46f), r.width - S(40f), S(40f)),
                "Click a node in the library to add it. Drag from a port on the right of a node to the left edge of another to connect them. Drag the empty canvas to pan, scroll to zoom.", _noteStyle);

        if (GUI.Button(new Rect(r.xMax - S(96f), r.y + S(6f), S(90f), S(24f)), "Fit all")) FitView(stage, r.size);
        if (GUI.Button(new Rect(r.xMax - S(96f) - S(34f), r.y + S(6f), S(30f), S(24f)), "+")) ZoomAt(stage, r.size * 0.5f, 1.25f);
        if (GUI.Button(new Rect(r.xMax - S(96f) - S(68f), r.y + S(6f), S(30f), S(24f)), "-")) ZoomAt(stage, r.size * 0.5f, 0.8f);
        GUI.Label(new Rect(r.xMax - S(96f) - S(128f), r.y + S(6f), S(56f), S(24f)), Mathf.RoundToInt(zoom * 100f) + "%", _noteStyle);
    }

    private void DrawCanvasContents(Rect local, float z, SC2MissionStage stage, Vector2 pan, Dictionary<int, NodeBox> boxes)
    {
        FillRect(local, new Color(0.10f, 0.11f, 0.13f, 1f));

        // fonts follow the zoom
        var titleSize = _nodeTitleStyle.fontSize;
        var textSize = _nodeTextStyle.fontSize;
        var portSize = _portLabelStyle.fontSize;
        _nodeTitleStyle.fontSize = Mathf.Max(5, Mathf.RoundToInt(titleSize * z));
        _nodeTextStyle.fontSize = Mathf.Max(5, Mathf.RoundToInt(textSize * z));
        _portLabelStyle.fontSize = Mathf.Max(5, Mathf.RoundToInt(portSize * z));

        // grid
        var grid = S(40f) * z;
        var gridColor = new Color(1f, 1f, 1f, 0.045f);
        for (var x = Mathf.Repeat(pan.x * z, grid); x < local.width; x += grid) FillRect(new Rect(x, 0, 1f, local.height), gridColor);
        for (var y = Mathf.Repeat(pan.y * z, grid); y < local.height; y += grid) FillRect(new Rect(0, y, local.width, 1f), gridColor);

        // wires
        for (var i = 0; i < stage.Edges.Count; i++)
        {
            var edge = stage.Edges[i];
            if (!TryPortPosition(boxes, pan, edge.FromNode, edge.FromPort, out var from)) continue;
            if (!boxes.TryGetValue(edge.ToNode, out var to)) continue;
            var selected = i == _selEdge;
            var color = selected ? new Color(1f, 0.9f, 0.3f, 1f) : new Color(0.7f, 0.78f, 0.9f, 0.85f);
            var points = Bezier(from, InPortOf(to, edge.ToPort));
            for (var p = 0; p + 1 < points.Count; p++) DrawLine(points[p] * z, points[p + 1] * z, color, Mathf.Max(1.2f, (selected ? S(3.5f) : S(2.2f)) * z));
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
            var selected = node.Id == _selNode;
            var color = CategoryColor(def.Category);
            var rect = Zr(box.Rect, z);
            var header = box.HeaderHeight * z;
            FillRect(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f), selected ? new Color(1f, 0.9f, 0.3f, 1f) : new Color(0f, 0f, 0f, 0.8f));
            FillRect(rect, new Color(0.16f, 0.17f, 0.20f, 1f));
            FillRect(new Rect(rect.x, rect.y, rect.width, header), color);
            GUI.Label(new Rect(rect.x + S(14f) * z, rect.y, rect.width - S(40f) * z, header), def.Title, _nodeTitleStyle);
            GUI.Label(new Rect(rect.xMax - S(34f) * z, rect.y, S(30f) * z, header), "#" + node.Id, _portLabelStyle);
            for (var i = 0; i < box.Lines.Count; i++)
                GUI.Label(new Rect(rect.x + S(8f) * z, rect.y + header + (box.InputRows + i) * S(18f) * z, rect.width - S(16f) * z, S(18f) * z), box.Lines[i], _nodeTextStyle);
            foreach (var input in box.InPorts)
            {
                if (box.InputRows > 0)
                    GUI.Label(new Rect(rect.x + S(12f) * z, (input.Value.y - S(9f)) * z, rect.width * 0.5f, S(18f) * z), input.Key, _nodeTextStyle);
                DrawPort(input.Value * z, false, z);
            }
            foreach (var port in box.OutPorts)
            {
                GUI.Label(new Rect(rect.x + S(8f) * z, (port.Value.y - S(9f)) * z, rect.width - S(24f) * z, S(18f) * z), port.Key, _portLabelStyle);
                DrawPort(port.Value * z, true, z);
            }
        }

        // the wire being dragged out of a port
        if (_drag == DragMode.Connect && TryPortPosition(boxes, pan, _connectNode, _connectPort, out var origin))
        {
            var points = Bezier(origin, _mouse);
            for (var p = 0; p + 1 < points.Count; p++) DrawLine(points[p] * z, points[p + 1] * z, new Color(1f, 1f, 1f, 0.6f), Mathf.Max(1.2f, S(2f) * z));
        }

        _nodeTitleStyle.fontSize = titleSize;
        _nodeTextStyle.fontSize = textSize;
        _portLabelStyle.fontSize = portSize;
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
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (!local.Contains(e.mousePosition)) break;
                // the zoom and fit buttons float over the canvas corner and take their own clicks
                if (new Rect(local.width - S(96f) - S(132f), S(6f), S(132f) + S(90f), S(24f)).Contains(e.mousePosition)) break;
                _mouse = graphMouse;
                OnCanvasMouseDown(id, e, graphMouse, stage, pan, boxes);
                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl != id) break;
                _mouse = graphMouse;
                if (_drag == DragMode.Pan)
                {
                    _pan[stage.Id] = pan + e.delta / zoom;
                }
                else if (_drag == DragMode.Node)
                {
                    var node = stage.FindNode(_dragNode);
                    if (node != null)
                    {
                        node.X = (graphMouse.x - pan.x - _dragGrab.x) / K;
                        node.Y = (graphMouse.y - pan.y - _dragGrab.y) / K;
                        _dragMoved = true;
                        _workVersion++;
                    }
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
                if (_drag == DragMode.Node && _dragMoved) { _dirty = true; if (string.IsNullOrEmpty(_pendingName)) _pendingName = "Move node"; Commit(); }
                else if (_drag == DragMode.Connect) FinishConnect(stage, boxes, graphMouse);
                _drag = DragMode.None;
                _dragMoved = false;
                e.Use();
                break;
        }
    }

    private void OnCanvasMouseDown(int id, Event e, Vector2 graphMouse, SC2MissionStage stage, Vector2 pan, Dictionary<int, NodeBox> boxes)
    {
        // A text field in the properties may still hold an edit; end it before something else gets selected.
        Commit();
        var p = graphMouse;

        if (e.button == 2) { BeginDrag(id, DragMode.Pan); e.Use(); return; }

        // output ports: the start node's and every node's
        if (e.button == 0)
        {
            var portRadius = S(9f);
            if (Vector2.Distance(p, StartPort(pan)) <= portRadius) { BeginConnect(id, SC2MissionStage.StartNodeId, SC2MissionStage.StartPort); e.Use(); return; }
            foreach (var node in Enumerable.Reverse(stage.Nodes))
            {
                if (!boxes.TryGetValue(node.Id, out var box)) continue;
                foreach (var port in box.OutPorts)
                    if (Vector2.Distance(p, port.Value) <= portRadius) { BeginConnect(id, node.Id, port.Key); e.Use(); return; }
            }
        }

        // nodes, topmost first
        foreach (var node in Enumerable.Reverse(stage.Nodes))
        {
            if (!boxes.TryGetValue(node.Id, out var box) || !box.Rect.Contains(p)) continue;
            _selNode = node.Id;
            _selEdge = -1;
            if (e.button == 0)
            {
                _dragNode = node.Id;
                _dragGrab = new Vector2(p.x - pan.x - node.X * K, p.y - pan.y - node.Y * K);
                _dragMoved = false;
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
            _selEdge = hitEdge;
            _selNode = -1;
            if (e.button == 1) DeleteSelection();
            e.Use();
            return;
        }

        _selNode = _selEdge = -1;
        if (e.button == 0 || e.button == 1) BeginDrag(id, DragMode.Pan);
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

    private void FinishConnect(SC2MissionStage stage, Dictionary<int, NodeBox> boxes, Vector2 p)
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
            if (node.Id == _connectNode && box.InputRows == 0) return;
            if (stage.Edges.Any(x => x.FromNode == _connectNode && x.FromPort == _connectPort && x.ToNode == node.Id && x.ToPort == target)) return;
            stage.Connect(_connectNode, _connectPort, node.Id, target);
            _selEdge = stage.Edges.Count - 1;
            _selNode = -1;
            Mutate("Connect nodes");
            Commit();
            return;
        }
    }

    private void DeleteSelection()
    {
        var stage = CurrentStage();
        if (stage == null) return;
        Commit();
        if (_selNode > 0 && stage.FindNode(_selNode) != null)
        {
            stage.RemoveNode(_selNode);
            _selNode = -1;
            Mutate("Delete node");
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
