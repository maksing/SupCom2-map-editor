using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>The mission panel harness (see Docs/Process/MissionPanelHarness.md). Draws the mission panel in an EditorWindow exactly as the host does,
/// feeds it synthetic IMGUI events, saves a picture of many states and checks what the panel does.
///
/// Entry point: SC2MissionClickCheck.Run. What it does is chosen by environment variables:
///   (none)                  every scenario: a picture, then a click on every button, switch and text field it drew; then the flows
///   MISSION_SHOTS_ONLY=1    only the pictures
///   MISSION_SHOT_NAME=x     with MISSION_SHOTS_ONLY: only the scenario named x
///   MISSION_ONLY_PREFIX=p   clicks only in the scenarios whose name starts with p (library_scroll)
///   MISSION_UX_ONLY=1       only the canvas flows (wire to search, double click, Home, F, scroll at the edge)
///   MISSION_OUT=dir         where the pictures and click_report.txt go
/// Result lines are logged with the tag "[Click]": FLOW OK / FLOW FAIL, TOTAL, UNCHANGED, ERROR. The exit code is 1 when there was an error.</summary>
public static class SC2MissionClickCheck
{
    public class ClickWin : EditorWindow
    {
        public static SC2MissionPanel Panel;
        public static string CaptureTo;
        void OnGUI()
        {
            if (Event.current.type == EventType.Repaint && CaptureTo != null)
            {
                var w = (int)position.width; var h = (int)position.height;
                var rt = RenderTexture.GetTemporary(w, h, 24);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0.25f, 0.25f, 0.28f, 1f));
                DrawLikeHost(w, h);
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                System.IO.File.WriteAllBytes(CaptureTo, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                CaptureTo = null;
                return;
            }
            DrawLikeHost((int)position.width, (int)position.height);
        }

        // The host draws the mission editor, then the runtime dropdown lists; while a list is open the editor sees an Ignore event.
        static void DrawLikeHost(int w, int h)
        {
            var e = Event.current;
            var type = e.type;
            var masked = false;
            if (HarnessDropdown.IsAnyOpen && type != EventType.Repaint && type != EventType.Layout && type != EventType.Used && type != EventType.Ignore)
            {
                e.type = EventType.Ignore;
                masked = true;
            }
            Panel?.DrawOverlay(w, h);
            if (masked && e.type == EventType.Ignore) e.type = type;
            HarnessDropdown.DrawOverlays();
        }
    }

    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    static SC2MissionPanel panel;
    static SC2MapSession session;
    static readonly HashSet<string> LibraryTitles = new HashSet<string>(SC2MissionNodeCatalog.All.Select(d => d.Title));
    static SC2ScenarioShell scenario;
    static SC2TerrainShell shell;
    static ClickWin win;
    static int errors;
    static readonly List<string> errorTexts = new();
    static readonly StringBuilder report = new();

    /// <summary>Where the pictures and click_report.txt are written: the MISSION_OUT environment variable, else a folder in the temp directory.</summary>
    static readonly string OutDir = (Environment.GetEnvironmentVariable("MISSION_OUT") is { Length: > 0 } outDir ? outDir : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mission_harness")).Replace('\\', '/');

    static void Log(string text) { Debug.Log("[Click] " + text); report.AppendLine(text); }
    static void Set(string field, object value) => typeof(SC2MissionPanel).GetField(field, Flags).SetValue(panel, value);
    static T Get<T>(string field) => (T)typeof(SC2MissionPanel).GetField(field, Flags).GetValue(panel);

    static SC2MissionDocument SeedMission()
    {
        var doc = SC2MissionSamples.HoldTheLine();
        doc.Variables.Add(new SC2MissionVariable { Name = "alarm", Type = SC2MissionVariable.Bool, Initial = "false" });
        var all = new SC2MissionStage { Id = "all", Name = "All nodes" };
        var i = 0;
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            var node = all.AddNode(def.Type, (i % 5) * 250, (i / 5) * 170);
            node.SetParam("army", "ARMY_2"); node.SetParam("group", "OUTPOST"); node.SetParam("area", "AREA_1");
            node.SetParam("marker", "CAM_1"); node.SetParam("chain", "RAID_1"); node.SetParam("stage", "stage1");
            node.SetParam("base", "EnemyMain"); node.SetParam("opai", "EnemyRaid"); node.SetParam("variable", "outposts_destroyed");
            node.SetParam("code", "LOG('hello')\nLOG('second line')");
            i++;
        }
        all.Connect(0, "out", all.Nodes[0].Id);
        all.Connect(all.Nodes[0].Id, "fired", all.Nodes[1].Id);
        doc.Stages.Add(all);
        var waves = SC2MissionSamples.ThreeWaves();
        waves.Stages[0].Id = "waves";
        waves.Stages[0].Name = "Waves";
        doc.Stages.Add(waves.Stages[0]);
        doc.Variables.Add(waves.Variables[0]);
        return doc;
    }

    // The panel sizes are remembered in the editor's preferences; a run starts from the defaults and leaves nothing behind.
    static void ResetLayout()
    {
        foreach (var key in new[] { "SC2Mission.libraryWidth", "SC2Mission.propsWidth", "SC2Mission.bottomHeight" }) PlayerPrefs.DeleteKey(key);
        typeof(SC2MissionPanel).GetField("_libraryW", StaticFlags).SetValue(null, 240f);
        typeof(SC2MissionPanel).GetField("_propsW", StaticFlags).SetValue(null, 310f);
        typeof(SC2MissionPanel).GetField("_bottomH", StaticFlags).SetValue(null, 170f);
    }

    static void NewPanel()
    {
        ResetLayout();
        session = new SC2MapSession(new SC2MapDocument());
        scenario = new SC2ScenarioShell(shell, session);
        scenario.ApplyMission("Seed", SeedMission());
        session.History.Clear();
        panel = new SC2MissionPanel(() => shell, () => session, () => scenario);
        ClickWin.Panel = panel;
        panel.Open();
        GUIUtility.hotControl = 0;
        GUIUtility.keyboardControl = 0;
    }

    static void Pump()
    {
        MissionProbe.Controls.Clear();
        win.SendEvent(new Event { type = EventType.MouseDown, button = 1, mousePosition = new Vector2(3, 3) });
        win.SendEvent(new Event { type = EventType.MouseUp, button = 1, mousePosition = new Vector2(3, 3) });
        MissionProbe.Controls.Clear();
        win.SendEvent(new Event { type = EventType.Repaint });
    }

    static Vector2 ToEvent(Vector2 screen) => screen - MissionProbe.GuiOrigin + new Vector2(0f, 26f);

    static string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(SC2MapDocumentSerializer.Serialize(session.Document).GetHashCode());
        foreach (var f in new[] { "_tab", "_selNode", "_selEdge", "_bottomTab", "_bottomOpen", "_confirmDeleteStage", "_confirmDeleteBase", "_baseSel", "_dirty", "_undoable" })
            sb.Append('|').Append(typeof(SC2MissionPanel).GetField(f, Flags).GetValue(panel));
        sb.Append('|').Append(panel.IsOpen);
        sb.Append('|').Append(GUIUtility.systemCopyBuffer.Length);
        var pans = (System.Collections.IDictionary)typeof(SC2MissionPanel).GetField("_pan", Flags).GetValue(panel);
        foreach (System.Collections.DictionaryEntry e in pans) sb.Append('|').Append(e.Key).Append(e.Value);
        sb.Append('|').Append(GUIUtility.keyboardControl);
        return sb.ToString();
    }

    class Scenario
    {
        public string Name;
        public Action Apply;
    }

    static List<Scenario> Scenarios()
    {
        var list = new List<Scenario>();
        void Add(string name, Action act) => list.Add(new Scenario { Name = name, Apply = act });
        Add("intro", () => { Set("_tab", "intro"); });
        Add("stage1", () => { Set("_tab", "stage1"); });
        Add("waves", () => { Set("_tab", "waves"); });
        Add("all_overview", () => { Set("_tab", "all"); });
        Add("waves_multi", () => { Set("_tab", "waves"); PanelMethod("SetSelection").Invoke(panel, new object[] { new List<int> { 1, 2, 3 }, 2 }); });
        Add("waves_help", () => { Set("_tab", "waves"); Set("_helpOpen", true); });
        Add("stage1_frame", () =>
        {
            Set("_tab", "stage1");
            var frame = Get<SC2MissionDocument>("_work").FindStage("stage1").AddNode("comment", -30f, -30f);
            frame.SetParam("text", "Setup"); frame.SetParam("width", "560"); frame.SetParam("height", "300"); frame.SetParam("color", "blue");
        });
        Add("spawn_groups", () =>
        {
            Set("_tab", "all");
            var spawn = Get<SC2MissionDocument>("_work").FindStage("all").Nodes.First(n => n.Type == "spawn_group");
            spawn.SetParam("group", "OUTPOST, GROUP_B");
            Set("_selNode", spawn.Id);
        });
        Add("quick_add", () => { Set("_tab", "waves"); PanelMethod("OpenQuickAdd").Invoke(panel, new object[] { new Vector2(330f, 260f), new Vector2(330f, 260f), 0, "out" }); });
        Add("waves_loop", () => { Set("_tab", "waves"); Set("_selNode", 1); });
        Add("stage1_edge", () => { Set("_tab", "stage1"); Set("_selEdge", 0); });
        Add("stage2_validation", () => { Set("_tab", "stage2"); Set("_bottomTab", 1); });
        Add("stage2_log", () => { Set("_tab", "stage2"); Set("_bottomTab", 2); });
        Add("stage2_bottomclosed", () => { Set("_tab", "stage2"); Set("_bottomOpen", false); });
        Add("bases", () => { Set("_tab", "@bases"); });
        Add("variables", () => { Set("_tab", "@variables"); });
        Add("settings", () => { Set("_tab", "@settings"); });
        var n = 0;
        foreach (var def in SC2MissionNodeCatalog.All)
        {
            var id = ++n;
            Add("node_" + def.Type, () => { Set("_tab", "all"); Set("_selNode", id); });
            if (def.Params.Length >= 3)
                Add("node_" + def.Type + "_scrolled", () => { Set("_tab", "all"); Set("_selNode", id); Set("_propsScroll", new Vector2(0, 10000)); });
        }
        foreach (var y in new[] { 250, 500, 750, 1000, 1250, 1500, 1750, 2000, 2250, 2500, 2750, 3000, 3300, 3600, 3900, 4200, 4500, 4800, 5100, 5400, 5700, 6000, 6500, 7000, 8000, 10000 })
        {
            var yy = y;
            Add("library_scroll_" + y, () => { Set("_tab", "stage1"); Set("_libraryScroll", new Vector2(0, yy)); });
            Add("bases_scroll_" + y, () => { Set("_tab", "@bases"); Set("_formScroll", new Vector2(0, yy)); });
        }
        Add("settings_scroll", () => { Set("_tab", "@settings"); Set("_formScroll", new Vector2(0, 10000)); });
        Add("validation_scroll", () => { Set("_tab", "stage2"); Set("_bottomTab", 1); Set("_bottomScroll", new Vector2(0, 10000)); });
        Add("stage_props_confirm", () => { Set("_tab", "stage2"); Set("_confirmDeleteStage", true); });
        return list;
    }

    static MissionProbe.Control? Find(string kind, string label)
    {
        foreach (var c in MissionProbe.Controls)
            if (c.Kind == kind && c.Label == label) return c;
        return null;
    }

    static bool ClickLabel(string kind, string label)
    {
        Pump();
        var c = Find(kind, label);
        if (c == null) { Log("   !! flow: no " + kind + " '" + label + "' on screen"); return false; }
        var ev = ToEvent(c.Value.ScreenRect.center);
        win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = ev });
        win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = ev });
        win.SendEvent(new Event { type = EventType.Repaint });
        return true;
    }

    static void FlowCheck(string name, bool ok)
    {
        Log((ok ? "FLOW OK   " : "FLOW FAIL ") + name);
        if (!ok) { errors++; errorTexts.Add("flow failed: " + name); }
    }

    static SC2MissionStage DocStage(string id) => session.Document.Scenario.Mission.FindStage(id);


    // ---- canvas usability flows: search on a loose wire, double click, Home, F, pan at the edge
    static MethodInfo PanelMethod(string name) => typeof(SC2MissionPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    static Vector2 CanvasOriginScreen()
    {
        Pump();
        var home = Find("Button", "Home (fit all)");
        var size = Get<Vector2>("_canvasSize");
        var f = SC2UiScale.Pxf(1f);
        return new Vector2(home.Value.ScreenRect.xMax + 6f * f - size.x, home.Value.ScreenRect.y - 6f * f);
    }

    static Vector2 _origin;

    static void MouseAt(EventType type, Vector2 canvasLocal, int clickCount = 1)
    {
        var screen = _origin + canvasLocal;
        win.SendEvent(new Event { type = type, button = 0, mousePosition = ToEvent(screen), clickCount = clickCount });
    }

    static void Key(KeyCode code, char ch = '\0')
    {
        win.SendEvent(new Event { type = EventType.KeyDown, keyCode = code, character = ch });
        win.SendEvent(new Event { type = EventType.KeyUp, keyCode = code });
        win.SendEvent(new Event { type = EventType.Repaint });
    }

    static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            win.SendEvent(new Event { type = EventType.KeyDown, character = ch });
            win.SendEvent(new Event { type = EventType.Repaint });
        }
    }

    static Vector2 PanNow(string stage) => (Vector2)((System.Collections.IDictionary)typeof(SC2MissionPanel).GetField("_pan", Flags).GetValue(panel))[stage];
    static float ZoomNow(string stage) => (float)((System.Collections.IDictionary)typeof(SC2MissionPanel).GetField("_zoom", Flags).GetValue(panel))[stage];

    static Vector2 StartPortLocal(string stageId)
    {
        var start = (Vector2)PanelMethod("StartPort").Invoke(null, new object[] { PanNow(stageId) });
        return start * ZoomNow(stageId);
    }

    static void MouseScreen(EventType type, Vector2 screen, EventModifiers mods = EventModifiers.None) =>
        win.SendEvent(new Event { type = type, button = 0, mousePosition = ToEvent(screen), clickCount = 1, modifiers = mods });

    static void MouseAtMod(EventType type, Vector2 canvasLocal, EventModifiers mods) => MouseScreen(type, _origin + canvasLocal, mods);

    static void KeyMod(KeyCode code, EventModifiers mods)
    {
        win.SendEvent(new Event { type = EventType.KeyDown, keyCode = code, modifiers = mods });
        win.SendEvent(new Event { type = EventType.KeyUp, keyCode = code, modifiers = mods });
        win.SendEvent(new Event { type = EventType.Repaint });
    }

    static Vector2 NodeCentre(string stageId, int nodeId)
    {
        var boxes = (System.Collections.IDictionary)PanelMethod("LayoutAll").Invoke(panel, new object[] { DocStage(stageId), PanNow(stageId) });
        var box = boxes[nodeId];
        return ((Rect)box.GetType().GetField("Rect").GetValue(box)).center * ZoomNow(stageId);
    }

    static int MultiCount() => Get<HashSet<int>>("_multi").Count;

    // editing flows: box select, Shift click, group move, copy and paste, delete, undo, shortcut list, splitters, library drag
    static void RunEditFlows()
    {
        var f = SC2UiScale.Pxf(1f);
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        var size = Get<Vector2>("_canvasSize");
        var stage = DocStage("waves");
        var total = stage.Nodes.Count;

        // a box over the middle of the canvas selects the nodes it touches
        var inset = 70f * f;
        var a = new Vector2(inset, inset);
        var b = new Vector2(size.x - inset, size.y - inset - 30f * f);
        MouseAt(EventType.MouseDown, a);
        MouseAt(EventType.MouseDrag, (a + b) * 0.5f);
        MouseAt(EventType.MouseDrag, b);
        win.SendEvent(new Event { type = EventType.Repaint });
        FlowCheck("a box drag shows the rubber band (drag mode " + Get<object>("_drag") + ")", Get<object>("_drag").ToString() == "Box");
        MouseAt(EventType.MouseUp, b);
        win.SendEvent(new Event { type = EventType.Repaint });
        FlowCheck("a box selects the nodes inside it (" + MultiCount() + " of " + total + ")", MultiCount() >= 2);

        // Shift click adds a node to the selection and removes it again
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        var first = NodeCentre("waves", 1) + new Vector2(0f, -4f * f);
        var second = NodeCentre("waves", 2) + new Vector2(0f, -4f * f);
        MouseAt(EventType.MouseDown, first); MouseAt(EventType.MouseUp, first);
        FlowCheck("a click selects one node (#" + Get<int>("_selNode") + ", group " + MultiCount() + ")", Get<int>("_selNode") == 1 && MultiCount() <= 1);
        MouseAtMod(EventType.MouseDown, second, EventModifiers.Shift); MouseAtMod(EventType.MouseUp, second, EventModifiers.Shift);
        FlowCheck("Shift click adds a second node (" + MultiCount() + ")", MultiCount() == 2);
        MouseAtMod(EventType.MouseDown, second, EventModifiers.Shift); MouseAtMod(EventType.MouseUp, second, EventModifiers.Shift);
        FlowCheck("Shift click on a selected node removes it (" + MultiCount() + ", primary #" + Get<int>("_selNode") + ")", MultiCount() <= 1 && Get<int>("_selNode") == 1);
        MouseAtMod(EventType.MouseDown, second, EventModifiers.Control); MouseAtMod(EventType.MouseUp, second, EventModifiers.Control);
        FlowCheck("Ctrl click adds too (" + MultiCount() + ")", MultiCount() == 2);

        // a selected group moves together
        stage = DocStage("waves");
        var n1 = stage.FindNode(1); var n2 = stage.FindNode(2);
        var x1 = n1.X; var x2 = n2.X; var y1 = n1.Y; var y2 = n2.Y;
        typeof(SC2MissionPanel).GetField("_snap", StaticFlags).SetValue(null, false);
        var grab = NodeCentre("waves", 1) + new Vector2(0f, -4f * f);
        MouseAt(EventType.MouseDown, grab);
        MouseAt(EventType.MouseDrag, grab + new Vector2(30f * f, 20f * f));
        MouseAt(EventType.MouseDrag, grab + new Vector2(60f * f, 40f * f));
        MouseAt(EventType.MouseUp, grab + new Vector2(60f * f, 40f * f));
        win.SendEvent(new Event { type = EventType.Repaint });
        stage = DocStage("waves"); n1 = stage.FindNode(1); n2 = stage.FindNode(2);
        var d1 = new Vector2(n1.X - x1, n1.Y - y1); var d2 = new Vector2(n2.X - x2, n2.Y - y2);
        FlowCheck("dragging one node of a selection moves all of them by the same amount (" + d1 + " / " + d2 + ")", d1.sqrMagnitude > 1f && (d1 - d2).sqrMagnitude < 0.01f);
        typeof(SC2MissionPanel).GetField("_snap", StaticFlags).SetValue(null, true);
        FlowCheck("and the move is one undo step (can undo=" + Get<int>("_undoable") + ")", Get<int>("_undoable") == 1);

        // copy, paste and duplicate through the keyboard
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        stage = DocStage("waves"); total = stage.Nodes.Count; var wires = stage.Edges.Count;
        KeyMod(KeyCode.A, EventModifiers.Control);
        FlowCheck("Ctrl+A selects every node (" + MultiCount() + " of " + total + ")", MultiCount() == total);
        KeyMod(KeyCode.C, EventModifiers.Control);
        KeyMod(KeyCode.V, EventModifiers.Control);
        stage = DocStage("waves");
        FlowCheck("Ctrl+C and Ctrl+V paste a copy of every node (" + total + " -> " + stage.Nodes.Count + ")", stage.Nodes.Count == total * 2);
        FlowCheck("the pasted nodes keep the wires between them (" + wires + " -> " + stage.Edges.Count + ")", stage.Edges.Count > wires);
        FlowCheck("the pasted nodes are the selection (" + MultiCount() + ")", MultiCount() == total);
        KeyMod(KeyCode.D, EventModifiers.Control);
        FlowCheck("Ctrl+D duplicates the selection (" + DocStage("waves").Nodes.Count + ")", DocStage("waves").Nodes.Count == total * 3);
        KeyMod(KeyCode.Delete, EventModifiers.None);
        FlowCheck("Delete removes every selected node (" + DocStage("waves").Nodes.Count + ")", DocStage("waves").Nodes.Count == total * 2);
        KeyMod(KeyCode.Z, EventModifiers.Control);
        FlowCheck("Ctrl+Z brings them back (" + DocStage("waves").Nodes.Count + ")", DocStage("waves").Nodes.Count == total * 3);

        // Escape steps back: the shortcut list, then the selection, then the window
        KeyMod(KeyCode.A, EventModifiers.Control);
        KeyMod(KeyCode.F1, EventModifiers.None);
        FlowCheck("F1 opens the list of shortcuts", Get<bool>("_helpOpen"));
        KeyMod(KeyCode.Escape, EventModifiers.None);
        FlowCheck("Escape closes the list first", !Get<bool>("_helpOpen") && panel.IsOpen && MultiCount() > 0);
        KeyMod(KeyCode.Escape, EventModifiers.None);
        FlowCheck("the next Escape clears the selection (" + MultiCount() + ", node " + Get<int>("_selNode") + ")", panel.IsOpen && MultiCount() == 0 && Get<int>("_selNode") < 0);
        KeyMod(KeyCode.Escape, EventModifiers.None);
        FlowCheck("and the third closes the window", !panel.IsOpen);

        // the border between the library and the canvas moves
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        var libBefore = (float)typeof(SC2MissionPanel).GetField("_libraryW", StaticFlags).GetValue(null);
        var strip = _origin + new Vector2(-4f * f, 200f * f);
        MouseScreen(EventType.MouseDown, strip);
        MouseScreen(EventType.MouseDrag, strip + new Vector2(40f * f, 0f));
        MouseScreen(EventType.MouseDrag, strip + new Vector2(80f * f, 0f));
        MouseScreen(EventType.MouseUp, strip + new Vector2(80f * f, 0f));
        win.SendEvent(new Event { type = EventType.Repaint });
        var libAfter = (float)typeof(SC2MissionPanel).GetField("_libraryW", StaticFlags).GetValue(null);
        FlowCheck("dragging the border widens the library (" + libBefore + " -> " + libAfter + ")", libAfter > libBefore + 20f);
        ResetLayout();

        // a node button dragged from the library onto the canvas adds the node where it is dropped
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        stage = DocStage("waves"); total = stage.Nodes.Count;
        var row = Find("Button", "Victory");
        if (row == null) FlowCheck("the library lists Victory", false);
        else
        {
            var from = row.Value.ScreenRect.center;
            var drop = _origin + new Vector2(size.x * 0.5f, size.y * 0.65f);
            MouseScreen(EventType.MouseDown, from);
            Log("   library press: pressed=" + Get<string>("_libPressed") + " at " + from);
            MouseScreen(EventType.MouseDrag, from + new Vector2(40f * f, 0f));
            MouseScreen(EventType.MouseDrag, drop);
            Log("   library drag: dragged=" + Get<bool>("_libDragged") + " screen=" + Get<Vector2>("_libDragScreen") + " canvasScreen=" + Get<Rect>("_canvasScreen") + " drop=" + drop);
            win.SendEvent(new Event { type = EventType.Repaint });
            MouseScreen(EventType.MouseUp, drop);
            win.SendEvent(new Event { type = EventType.Repaint });
            stage = DocStage("waves");
            var made = stage.Nodes.Count == total + 1 ? stage.Nodes[stage.Nodes.Count - 1] : null;
            FlowCheck("dropping a library button on the canvas adds that node (" + total + " -> " + stage.Nodes.Count + ")", made != null && made.Type == "victory");
        }

        // "+ Add a group" makes an empty slot and assigns nothing until a group is picked in it
        NewPanel(); Set("_tab", "all");
        var spawnNode = Get<SC2MissionDocument>("_work").FindStage("all").Nodes.First(n => n.Type == "spawn_group");
        Set("_selNode", spawnNode.Id);
        Pump(); Pump();
        var groupBefore = spawnNode.GetParam("group");
        var added = ClickLabel("Button", "+ Add a group");
        Pump();
        var pendingSlots = (System.Collections.IDictionary)typeof(SC2MissionPanel).GetField("_pendingListSlots", Flags).GetValue(panel);
        var slotCount = 0;
        foreach (System.Collections.DictionaryEntry slot in pendingSlots) slotCount += (int)slot.Value;
        FlowCheck("+ Add a group was found and clicked", added);
        FlowCheck("it adds an empty slot and no group (" + groupBefore + " -> " + spawnNode.GetParam("group") + ", slots " + slotCount + ")",
            spawnNode.GetParam("group") == groupBefore && slotCount == 1);
        FlowCheck("the empty slot offers the choice text", Find("Popup", "(choose a group)") != null || MissionProbe.Controls.Any(c => c.Kind == "Popup"));

        var recent = (System.Collections.IList)typeof(SC2MissionPanel).GetField("_recent", StaticFlags).GetValue(null);
        FlowCheck("the added node is the first of the recent nodes (" + (recent.Count > 0 ? recent[0] : "none") + ")", recent.Count > 0 && (string)recent[0] == "victory");
    }

    static void RunUxFlows()
    {
        var f = SC2UiScale.Pxf(1f);
        // a wire let go over empty canvas opens the search; typing and Return add the node and connect it
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        var stage = DocStage("waves");
        var nodes = stage.Nodes.Count; var edges = stage.Edges.Count;
        var size = Get<Vector2>("_canvasSize");
        var startLocal = StartPortLocal("waves");
        MouseAt(EventType.MouseDown, startLocal);
        MouseAt(EventType.MouseDrag, startLocal + new Vector2(60f * f, 160f * f));
        MouseAt(EventType.MouseDrag, new Vector2(size.x * 0.4f, size.y * 0.8f));
        MouseAt(EventType.MouseUp, new Vector2(size.x * 0.4f, size.y * 0.8f));
        win.SendEvent(new Event { type = EventType.Repaint });
        win.SendEvent(new Event { type = EventType.Repaint });
        FlowCheck("a wire let go over empty canvas opens the node search", Get<bool>("_quickOpen"));
        TypeText("victo");
        FlowCheck("typing fills the search (" + Get<string>("_quickSearch") + ")", Get<string>("_quickSearch") == "victo");
        Key(KeyCode.Return);
        stage = DocStage("waves");
        FlowCheck("Return adds the first match and closes the search (" + nodes + " -> " + stage.Nodes.Count + ")", stage.Nodes.Count == nodes + 1 && !Get<bool>("_quickOpen"));
        var added = stage.Nodes[stage.Nodes.Count - 1];
        FlowCheck("the new node is a Victory node wired from Start (" + added.Type + ", edges " + edges + " -> " + stage.Edges.Count + ")",
            added.Type == "victory" && stage.Edges.Count == edges + 1 && stage.Edges.Exists(e => e.FromNode == 0 && e.ToNode == added.Id && e.ToPort == "in"));

        // Escape closes only the search; the panel stays open
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        MouseAt(EventType.MouseDown, new Vector2(size.x * 0.5f, size.y * 0.75f), 2);
        MouseAt(EventType.MouseUp, new Vector2(size.x * 0.5f, size.y * 0.75f), 2);
        win.SendEvent(new Event { type = EventType.Repaint });
        FlowCheck("a double click on empty canvas opens the node search", Get<bool>("_quickOpen"));
        Key(KeyCode.Escape);
        FlowCheck("Escape closes the search and not the panel", !Get<bool>("_quickOpen") && panel.IsOpen);

        // Home shows everything again after the view was moved far away
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        var panels = (System.Collections.IDictionary)typeof(SC2MissionPanel).GetField("_pan", Flags).GetValue(panel);
        panels["waves"] = new Vector2(-9000f, -7000f);
        Pump();
        var far = PanNow("waves");
        Key(KeyCode.Home);
        Pump();
        var home = PanNow("waves");
        var boxes = (System.Collections.IDictionary)PanelMethod("LayoutAll").Invoke(panel, new object[] { DocStage("waves"), PanNow("waves") });
        var zoom = ZoomNow("waves");
        var allInside = true;
        foreach (System.Collections.DictionaryEntry de in boxes)
        {
            var rect = (Rect)de.Value.GetType().GetField("Rect").GetValue(de.Value);
            var min = rect.min * zoom; var max = rect.max * zoom;
            if (min.x < -1f || min.y < -1f || max.x > size.x + 1f || max.y > size.y + 1f) allInside = false;
        }
        FlowCheck("Home brings every node back into view (pan " + far + " -> " + home + ")", far != home && allInside);

        // F centres the selected node
        Set("_selNode", 1);
        Key(KeyCode.F);
        Pump();
        boxes = (System.Collections.IDictionary)PanelMethod("LayoutAll").Invoke(panel, new object[] { DocStage("waves"), PanNow("waves") });
        var box = boxes[1];
        var centre = ((Rect)box.GetType().GetField("Rect").GetValue(box)).center * ZoomNow("waves");
        FlowCheck("F centres the selected node (" + centre + " in " + size + ")", Mathf.Abs(centre.x - size.x * 0.5f) < 3f && Mathf.Abs(centre.y - size.y * 0.5f) < 3f);

        // dragging a node to the edge moves the view
        NewPanel(); Set("_tab", "waves"); Pump(); Pump(); _origin = CanvasOriginScreen();
        boxes = (System.Collections.IDictionary)PanelMethod("LayoutAll").Invoke(panel, new object[] { DocStage("waves"), PanNow("waves") });
        var first = boxes[1];
        var headerPoint = ((Rect)first.GetType().GetField("Rect").GetValue(first)).center * ZoomNow("waves");
        var panBefore = PanNow("waves");
        var xBefore = DocStage("waves").FindNode(1).X;
        MouseAt(EventType.MouseDown, headerPoint);
        MouseAt(EventType.MouseDrag, new Vector2(size.x - 6f, headerPoint.y));
        for (var i = 0; i < 12; i++) { System.Threading.Thread.Sleep(40); win.SendEvent(new Event { type = EventType.Repaint }); }
        MouseAt(EventType.MouseUp, new Vector2(size.x - 6f, headerPoint.y));
        var panAfter = PanNow("waves");
        var xAfter = DocStage("waves").FindNode(1).X;
        FlowCheck("a node held at the right edge scrolls the view (pan x " + panBefore.x + " -> " + panAfter.x + ")", panAfter.x < panBefore.x - 20f);
        FlowCheck("and the node goes along (x " + xBefore + " -> " + xAfter + ")", xAfter > xBefore + 20f);

        RunEditFlows();
        ResetLayout();
    }

    static void RunFlows()
    {
        // library button -> node in the document -> Undo button -> Redo button
        NewPanel(); Set("_tab", "stage2"); Pump();
        var before = DocStage("stage2").Nodes.Count;
        ClickLabel("Button", "Timer");
        FlowCheck("library button adds a node (" + before + " -> " + DocStage("stage2").Nodes.Count + ")", DocStage("stage2").Nodes.Count == before + 1);
        ClickLabel("Button", "Undo");
        FlowCheck("Undo button removes it (" + DocStage("stage2").Nodes.Count + ")", DocStage("stage2").Nodes.Count == before);
        ClickLabel("Button", "Redo");
        FlowCheck("Redo button restores it (" + DocStage("stage2").Nodes.Count + ")", DocStage("stage2").Nodes.Count == before + 1);

        // header switch
        NewPanel(); Set("_tab", "stage2"); Pump();
        var enabledBefore = session.Document.Scenario.Mission.Enabled;
        ClickLabel("Toggle", "Use graph for _script.lua");
        FlowCheck("header switch flips Use graph (" + enabledBefore + " -> " + session.Document.Scenario.Mission.Enabled + ")", session.Document.Scenario.Mission.Enabled != enabledBefore);

        // stage management
        NewPanel(); Set("_tab", "stage2"); Pump();
        var stages = session.Document.Scenario.Mission.Stages.Count;
        ClickLabel("Button", "+ Stage");
        FlowCheck("+ Stage adds a stage (" + stages + " -> " + session.Document.Scenario.Mission.Stages.Count + ")", session.Document.Scenario.Mission.Stages.Count == stages + 1);
        ClickLabel("Toggle", "Intro");
        FlowCheck("stage tab switches the tab to intro (" + Get<string>("_tab") + ")", Get<string>("_tab") == "intro");
        ClickLabel("Toggle", "Test from this stage");
        FlowCheck("Test from this stage sets the test stage (" + session.Document.Scenario.Mission.Settings.TestStartStage + ")", session.Document.Scenario.Mission.Settings.TestStartStage == "intro");
        ClickLabel("Button", "Delete stage"); ClickLabel("Button", "Delete");
        FlowCheck("Delete stage (two clicks) removes intro (" + (DocStage("intro") == null) + ")", DocStage("intro") == null);

        // Fit all restores the view, zoom buttons zoom
        NewPanel(); Set("_tab", "stage1"); Pump();
        var pans = (System.Collections.Generic.Dictionary<string, Vector2>)typeof(SC2MissionPanel).GetField("_pan", Flags).GetValue(panel);
        var zooms = (System.Collections.Generic.Dictionary<string, float>)typeof(SC2MissionPanel).GetField("_zoom", Flags).GetValue(panel);
        ClickLabel("Button", "Home (fit all)");
        var fitted = pans["stage1"];
        var fittedZoom = zooms["stage1"];
        pans["stage1"] = fitted + new Vector2(300, 300);
        zooms["stage1"] = 1.2f;
        ClickLabel("Button", "Home (fit all)");
        FlowCheck("Fit all restores pan and zoom (" + pans["stage1"] + " " + zooms["stage1"] + " vs " + fitted + " " + fittedZoom + ")",
            Vector2.Distance(pans["stage1"], fitted) < 0.5f && Mathf.Abs(zooms["stage1"] - fittedZoom) < 0.001f);
        var z0 = zooms["stage1"];
        ClickLabel("Button", "+");
        FlowCheck("zoom + button zooms in (" + z0 + " -> " + zooms["stage1"] + ")", zooms["stage1"] > z0);
        // compared with the zoom after "+": the fitted zoom itself may already be the smallest the canvas allows
        var zPlus = zooms["stage1"];
        ClickLabel("Button", "-");
        FlowCheck("zoom - button zooms out (" + zPlus + " -> " + zooms["stage1"] + ")", zooms["stage1"] < zPlus);

        // a validation line jumps to its node
        NewPanel(); Set("_tab", "stage2"); Set("_bottomTab", 1); Pump();
        var issue = MissionProbe.Controls.FirstOrDefault(c => c.Kind == "Button" && c.Label.Contains("all #"));
        if (issue.Label != null)
        {
            var ev = ToEvent(issue.ScreenRect.center);
            win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = ev });
            win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = ev });
            FlowCheck("validation line jumps to its stage and node (" + Get<string>("_tab") + " #" + Get<int>("_selNode") + ")", Get<string>("_tab") == "all" && Get<int>("_selNode") > 0);
        }
        else Log("   !! flow: no validation line with a node on screen");

        // typing in a field is committed when the field loses focus
        NewPanel(); Set("_tab", "all"); Set("_selNode", 1); Pump();
        var timerBefore = DocStage("all").FindNode(1).GetParam("seconds");
        var field = MissionProbe.Controls.LastOrDefault(c => c.Kind == "Text");
        if (field.Kind == "Text")
        {
            var ev = ToEvent(field.ScreenRect.center);
            win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = ev });
            win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = ev });
            win.SendEvent(new Event { type = EventType.Repaint });
            win.SendEvent(new Event { type = EventType.KeyDown, character = '7' });
            win.SendEvent(new Event { type = EventType.Repaint });
            var typed = Get<SC2MissionDocument>("_work").FindStage("all").FindNode(1).GetParam("seconds");
            Pump();
            var empty = ToEvent(new Vector2(MissionProbe.GuiOrigin.x + 700, MissionProbe.GuiOrigin.y + 420));
            win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = empty });
            win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = empty });
            win.SendEvent(new Event { type = EventType.Repaint });
            win.SendEvent(new Event { type = EventType.Repaint });
            var stored = DocStage("all").FindNode(1).GetParam("seconds");
            FlowCheck("typed text reaches the document after the field loses focus ('" + timerBefore + "' -> typed '" + typed + "' -> stored '" + stored + "')", stored != timerBefore);
        }
        // a dropdown opens, shows its list on top, and a click on a list item that lies over another field changes the value
        NewPanel(); Set("_tab", "@bases"); Pump();
        var armyPopup = MissionProbe.Controls.FirstOrDefault(c => c.Kind == "Popup" && c.Label == "ARMY_2");
        if (armyPopup.Kind == "Popup")
        {
            var evOpen = ToEvent(armyPopup.ScreenRect.center);
            win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = evOpen });
            win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = evOpen });
            win.SendEvent(new Event { type = EventType.Repaint });
            FlowCheck("dropdown button opens the list (" + HarnessDropdown.IsAnyOpen + ")", HarnessDropdown.IsAnyOpen);
            ClickWin.CaptureTo = OutDir + "/dropdown_open.png";
            win.SendEvent(new Event { type = EventType.Repaint });
            // the list is drawn under the anchor: first row ARMY_1, second ARMY_2
            var anchor = new Rect(armyPopup.ScreenRect.position - MissionProbe.GuiOrigin, armyPopup.ScreenRect.size);
            var row = SC2UiScale.Pxf(20f);
            var item0 = new Vector2(anchor.x + anchor.width * 0.5f, anchor.yMax + 2f + 3f + row * 0.5f);
            var evPick = ToEvent(MissionProbe.GuiOrigin + item0);
            win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = evPick });
            win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = evPick });
            win.SendEvent(new Event { type = EventType.Repaint });
            win.SendEvent(new Event { type = EventType.Repaint });
            win.SendEvent(new Event { type = EventType.Repaint });
            var army = session.Document.Scenario.Mission.Bases[0].Army;
            FlowCheck("picking the first list item sets the base army (" + army + ", list open=" + HarnessDropdown.IsAnyOpen + ")", army == "ARMY_1" && !HarnessDropdown.IsAnyOpen);
        }
        else Log("   !! flow: no ARMY_2 dropdown on screen");
    }

    public static void Run()
    {
        Application.logMessageReceived += (condition, stack, type) =>
        {
            if ((type == LogType.Exception || type == LogType.Error) && !condition.StartsWith("[Click]")) { errors++; if (errorTexts.Count < 10) errorTexts.Add(condition); }
        };

        var data = ScriptableObject.CreateInstance<SC2TerrainData>();
        data.MapName = "ClickCheck";
        data.InitializeFlatPlane(128, 128, 8f);
        data.ObjArmies.Add(new SC2ObjArmy { Id = 1, Name = "ARMY_1" });
        data.ObjArmies.Add(new SC2ObjArmy { Id = 2, Name = "ARMY_2" });
        data.ObjMarkers.Add(new SC2ObjMarker { Id = 1, Name = "ENEMY_BASE" });
        data.ObjMarkers.Add(new SC2ObjMarker { Id = 2, Name = "CAM_1" });
        data.ObjZones.Add(new SC2ObjZone { Id = 1, Name = "AREA_1" });
        data.ObjChains.Add(new SC2ObjChain { Id = 1, Name = "RAID_1" });
        data.ObjUnits.Add(new SC2ObjUnitNode { Id = 1, ArmyName = "ARMY_2", Name = "OUTPOST", IsGroup = true });
        data.ObjUnits.Add(new SC2ObjUnitNode { Id = 2, ArmyName = "ARMY_2", Name = "GROUP_B", IsGroup = true });
        shell = ScriptableObject.CreateInstance<SC2TerrainShell>();
        shell.SetData(data, "clickcheck");

        win = EditorWindow.GetWindow<ClickWin>();
        win.position = new Rect(0, 0, 1500, 900);
        System.IO.Directory.CreateDirectory(OutDir);

        var summary = new List<string>();
        var totalClicks = 0;
        if (System.Environment.GetEnvironmentVariable("MISSION_UX_ONLY") == "1")
        {
            RunUxFlows();
            Log("TOTAL clicks=0 unchanged=0 imguiErrors=" + errors);
            foreach (var t in errorTexts) Log("ERROR " + t);
            EditorApplication.Exit(errors > 0 ? 1 : 0);
            return;
        }
        foreach (var scn in Scenarios())
        {
            var onlyName = System.Environment.GetEnvironmentVariable("MISSION_SHOT_NAME");
            if (!string.IsNullOrEmpty(onlyName) && scn.Name != onlyName) continue;
            NewPanel(); scn.Apply(); Pump();
            ClickWin.CaptureTo = OutDir + "/" + scn.Name + ".png";
            win.SendEvent(new Event { type = EventType.MouseDown, button = 1, mousePosition = new Vector2(3, 3) });
            win.SendEvent(new Event { type = EventType.Repaint });
            Pump();
            var controls = MissionProbe.Controls.ToList();
            Log("== " + scn.Name + ": " + controls.Count + " controls (" + controls.Count(c => c.Kind == "Button") + " buttons, " + controls.Count(c => c.Kind == "Toggle") + " switches, " +
                controls.Count(c => c.Kind == "Text") + " text, " + controls.Count(c => c.Kind == "Popup") + " popups)");

            if (System.Environment.GetEnvironmentVariable("MISSION_SHOTS_ONLY") == "1") continue;
            var only = System.Environment.GetEnvironmentVariable("MISSION_ONLY_PREFIX");
            if (!string.IsNullOrEmpty(only) && !scn.Name.StartsWith(only)) continue;
            for (var j = 0; j < controls.Count; j++)
            {
                var c = controls[j];
                var center = c.ScreenRect.center;
                var outside = c.ScreenRect.width < 4 || c.ScreenRect.height < 4;
                // the library buttons are many: they are clicked in the library scenarios only
                if (c.Kind == "Button" && LibraryTitles.Contains(c.Label) && !scn.Name.StartsWith("library_scroll") && scn.Name != "stage1") continue;
                if (c.Kind == "Popup")
                {
                    Log("   popup '" + c.Label + "' rect=" + c.ScreenRect + (outside ? "  TOO SMALL" : ""));
                    continue;
                }
                NewPanel(); scn.Apply(); Pump();
                if (MissionProbe.Controls.Count != controls.Count || MissionProbe.Controls[j].Label != c.Label)
                {
                    Log("   !! control list changed between passes at " + j + " '" + c.Label + "'");
                    continue;
                }
                GUIUtility.systemCopyBuffer = "";
                var before = Fingerprint();
                var ev = ToEvent(center);
                win.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = ev });
                win.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = ev });
                win.SendEvent(new Event { type = EventType.Repaint });
                var after = Fingerprint();
                totalClicks++;
                var changed = before != after;
                var line = "   " + (changed ? "OK     " : "NOCHANGE") + " " + c.Kind + " '" + c.Label + "' enabled=" + c.Enabled + " size=" + (int)c.ScreenRect.width + "x" + (int)c.ScreenRect.height;
                Log(line);
                if (!changed) summary.Add(scn.Name + ": " + c.Kind + " '" + c.Label + "' enabled=" + c.Enabled);
            }
        }
        if (System.Environment.GetEnvironmentVariable("MISSION_SHOTS_ONLY") != "1") RunFlows();
        Log("TOTAL clicks=" + totalClicks + " unchanged=" + summary.Count + " imguiErrors=" + errors);
        foreach (var s in summary) Log("UNCHANGED " + s);
        foreach (var t in errorTexts) Log("ERROR " + t);
        ResetLayout();
        System.IO.File.WriteAllText(OutDir + "/click_report.txt", report.ToString());
        EditorApplication.Exit(errors > 0 ? 1 : 0);
    }
}
