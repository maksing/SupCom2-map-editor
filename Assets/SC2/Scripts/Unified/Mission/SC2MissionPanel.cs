using System;
using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// The mission editor window (Scenario &gt; Mission &gt; "Open Mission Editor..."): the node graph of the mission, one tab per stage, with the
/// node library on the left, the canvas in the middle, the properties of the selected node on the right and the generated Lua, the
/// validation and the mission log below. The Bases, Variables and Settings tabs replace the canvas with forms.
///
/// A plain GUILayout/IMGUI overlay like SC2LobbySpawnPanel (a real EditorWindow cannot run in a Build). It edits a working copy of
/// SC2MapDocument.Scenario.Mission and writes it back through SC2ReplaceMissionCommand, one command per finished edit (a node drop,
/// a wire, a field left), so the document's undo history sees whole edits only.
/// </summary>
public sealed partial class SC2MissionPanel
{
    public Action RepaintHost;
    public Action<string> ReportStatus;
    public bool IsOpen;

    private readonly Func<SC2TerrainShell> _getTerrainShell;
    private readonly Func<SC2MapSession> _getSession;
    private readonly Func<SC2ScenarioShell> _getScenarioShell;

    private const string TabBases = "@bases";
    private const string TabVariables = "@variables";
    private const string TabSettings = "@settings";

    private SC2MissionDocument _work = new();
    private long _syncedRevision = -1;
    private bool _dirty;
    private string _pendingName = "";
    private int _workVersion;
    private int _undoable, _redoable;
    private string _lastFocused = "";

    private string _tab = TabSettings;
    private readonly Dictionary<string, Vector2> _pan = new();
    private int _selNode = -1;
    private int _selEdge = -1;

    private enum DragMode { None, Pan, Node, Connect }
    private DragMode _drag;
    private int _dragNode;
    private Vector2 _dragGrab;
    private bool _dragMoved;
    private int _connectNode;
    private string _connectPort = "";
    private Vector2 _mouse;

    private SC2MissionMapInfo _mapInfo = new();
    private SC2MissionCompileResult _compiled = new();
    private string _syntaxError = "";
    private int _compiledVersion = -1;

    private bool _bottomOpen = true;
    private int _bottomTab;
    private Vector2 _bottomScroll;
    private Vector2 _libraryScroll;
    private string _librarySearch = "";
    private Vector2 _propsScroll;
    private Vector2 _formScroll;

    private Texture2D _disc;
    private GUIStyle _wrapTitleStyle, _wrapToggleStyle, _titleStyle, _nodeTitleStyle, _nodeTextStyle, _portLabelStyle, _monoStyle, _noteStyle, _fieldLabelStyle, _libraryButtonStyle, _headerNoteStyle;
    private float _stylesScale = -1f;

    private static float S(float px) => SC2UiScale.Pxf(px);

    public SC2MissionPanel(Func<SC2TerrainShell> getTerrainShell, Func<SC2MapSession> getSession, Func<SC2ScenarioShell> getScenarioShell)
    {
        _getTerrainShell = getTerrainShell ?? throw new ArgumentNullException(nameof(getTerrainShell));
        _getSession = getSession ?? throw new ArgumentNullException(nameof(getSession));
        _getScenarioShell = getScenarioShell ?? throw new ArgumentNullException(nameof(getScenarioShell));
    }

    // ------------------------------------------------------------------ open / close / sync

    public void Open()
    {
        var data = _getTerrainShell()?.Data;
        var session = _getSession();
        if (data == null || !data.IsInitialized || session?.Document == null)
        {
            ReportStatus?.Invoke("Mission Editor: create or load a terrain first.");
            return;
        }
        IsOpen = true;
        _work = session.Document.Scenario.Mission.Clone();
        _syncedRevision = session.Document.Revision;
        _dirty = false;
        _workVersion++;
        _undoable = _redoable = 0;
        _mapInfo = SC2MissionMapInfoBuilder.From(data);
        _selNode = _selEdge = -1;
        _drag = DragMode.None;
        _tab = _work.Stages.Count > 0 ? _work.Stages[0].Id : TabSettings;
        SC2MissionLog.Add("Mission editor opened.");
    }

    private void Close()
    {
        Commit();
        IsOpen = false;
        _drag = DragMode.None;
        SC2MissionLog.Add("Mission editor closed.");
    }

    /// <summary>Takes the document's mission when something other than this panel changed it (an undo, a load).</summary>
    private void SyncFromDocument(SC2MapDocument document)
    {
        if (_dirty || _syncedRevision == document.Revision) return;
        _work = document.Scenario.Mission.Clone();
        _syncedRevision = document.Revision;
        _workVersion++;
        EnsureTab();
        if (SelectedNode() == null) _selNode = -1;
    }

    private void EnsureTab()
    {
        if (_tab == TabBases || _tab == TabVariables || _tab == TabSettings) return;
        if (_work.FindStage(_tab) != null) return;
        _tab = _work.Stages.Count > 0 ? _work.Stages[0].Id : TabSettings;
        _selNode = _selEdge = -1;
    }

    /// <summary>Marks the working copy as changed; the edit is written to the document by Commit.</summary>
    private void Mutate(string name)
    {
        _workVersion++;
        _dirty = true;
        if (string.IsNullOrEmpty(_pendingName)) _pendingName = name;
    }

    private void Commit()
    {
        if (!_dirty) return;
        var session = _getSession();
        var shell = _getScenarioShell();
        if (session?.Document == null || shell == null) { _dirty = false; return; }
        var name = string.IsNullOrEmpty(_pendingName) ? "Edit Mission" : _pendingName;
        shell.ApplyMission(name, _work);
        _syncedRevision = session.Document.Revision;
        _dirty = false;
        _pendingName = "";
        // the shared history keeps the latest MaxSteps commands only, so the counter cannot run past that
        _undoable = System.Math.Min(_undoable + 1, SC2ShellUndoCoordinator.MaxSteps);
        _redoable = 0;
        SC2MissionLog.Add(name);
    }

    /// <summary>What the panel can undo: its own edits, as long as the shared history still has them.</summary>
    private bool CanUndoHere => _undoable > 0 && (_getScenarioShell()?.HasUndo ?? false);

    private bool CanRedoHere => _redoable > 0 && (_getScenarioShell()?.HasRedo ?? false);

    private void Undo()
    {
        Commit();
        if (!CanUndoHere)
        {
            // the edits are older than the history keeps: there is nothing more to undo
            if (!(_getScenarioShell()?.HasUndo ?? false)) _undoable = 0;
            return;
        }
        _getScenarioShell().Undo();
        _undoable--;
        _redoable++;
        SC2MissionLog.Add("Undo");
    }

    private void Redo()
    {
        Commit();
        if (!CanRedoHere)
        {
            if (!(_getScenarioShell()?.HasRedo ?? false)) _redoable = 0;
            return;
        }
        _getScenarioShell().Redo();
        _redoable--;
        _undoable++;
        SC2MissionLog.Add("Redo");
    }

    private SC2MissionStage CurrentStage() => _work.FindStage(_tab);

    private SC2MissionNode SelectedNode() => CurrentStage()?.FindNode(_selNode);

    private void RefreshCompile()
    {
        if (_compiledVersion == _workVersion || _drag != DragMode.None) return;
        _compiled = SC2MissionCompiler.Compile(_work, _mapInfo);
        _syntaxError = SC2LuaSyntaxCheck.TryCompile(_compiled.Lua, out var error) ? "" : error;
        _compiledVersion = _workVersion;
    }

    // ------------------------------------------------------------------ frame

    public void DrawOverlay(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;
        var session = _getSession();
        if (_getTerrainShell()?.Data == null || session?.Document == null) { IsOpen = false; return; }
        EnsureResources();
        _popupSeq = 0;
        SyncFromDocument(session.Document);
        RefreshCompile();

        var panelWidth = Mathf.Min(1560f, screenWidth - 30f);
        var panelHeight = Mathf.Min(940f, screenHeight - 40f);
        var rect = new Rect((screenWidth - panelWidth) * 0.5f, (screenHeight - panelHeight) * 0.5f, panelWidth, panelHeight);

        var prevDepth = GUI.depth;
        GUI.depth = -2600;
        var prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(0, 0, screenWidth, screenHeight), Texture2D.whiteTexture);
        GUI.color = prevColor;
        GUI.Box(rect, GUIContent.none);

        var pad = S(10f);
        var headerHeight = S(34f);
        var tabsHeight = S(32f);
        var bottomHeight = _bottomOpen ? S(230f) : S(30f);

        DrawHeader(new Rect(rect.x + pad, rect.y + S(6f), rect.width - pad * 2f, headerHeight - S(6f)));
        DrawTabs(new Rect(rect.x + pad, rect.y + headerHeight, rect.width - pad * 2f, tabsHeight - S(4f)));

        var bodyTop = rect.y + headerHeight + tabsHeight;
        var bodyHeight = rect.height - headerHeight - tabsHeight - bottomHeight - pad;
        var body = new Rect(rect.x + pad, bodyTop, rect.width - pad * 2f, bodyHeight);

        if (_tab == TabBases) DrawBasesForm(body);
        else if (_tab == TabVariables) DrawVariablesForm(body);
        else if (_tab == TabSettings) DrawSettingsForm(body);
        else DrawStageBody(body);

        DrawBottom(new Rect(rect.x + pad, rect.yMax - bottomHeight - pad * 0.5f, rect.width - pad * 2f, bottomHeight));

        HandleKeys();
        TrackFocus();
        if (_commitAfterFrame && Event.current.type != EventType.Layout)
        {
            _commitAfterFrame = false;
            Commit();
        }

        GUI.depth = prevDepth;
    }

    private void HandleKeys()
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown) return;
        if (e.keyCode == KeyCode.Escape && !TextFieldFocused()) { Close(); e.Use(); return; }
        if (TextFieldFocused()) return;
        if (e.control && e.keyCode == KeyCode.Z && !e.shift) { Undo(); e.Use(); }
        else if (e.control && (e.keyCode == KeyCode.Y || (e.keyCode == KeyCode.Z && e.shift))) { Redo(); e.Use(); }
        else if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && CurrentStage() != null) { DeleteSelection(); e.Use(); }
    }

    private static bool TextFieldFocused() => GUI.GetNameOfFocusedControl().StartsWith("m_", StringComparison.Ordinal);

    /// <summary>A text field that lost focus ends its edit: the typed value goes into the undo history as one step.</summary>
    private void TrackFocus()
    {
        if (Event.current.type != EventType.Layout) return;
        var focused = GUI.GetNameOfFocusedControl();
        if (_dirty && _lastFocused.StartsWith("m_", StringComparison.Ordinal) && focused != _lastFocused) Commit();
        _lastFocused = focused;
    }

    // ------------------------------------------------------------------ header and tabs

    private void DrawHeader(Rect r)
    {
        GUI.Label(new Rect(r.x, r.y, r.width * 0.5f, r.height), "Mission Editor  —  " + (string.IsNullOrEmpty(_work.Title) ? "untitled" : _work.Title), _titleStyle);

        var x = r.xMax;
        var buttonHeight = r.height;
        x -= S(28f);
        if (GUI.Button(new Rect(x, r.y, S(26f), buttonHeight), "X")) { Close(); return; }

        x -= S(8f) + S(70f);
        GUI.enabled = CanRedoHere;
        if (GUI.Button(new Rect(x, r.y, S(70f), buttonHeight), "Redo")) Redo();
        x -= S(6f) + S(70f);
        GUI.enabled = CanUndoHere;
        if (GUI.Button(new Rect(x, r.y, S(70f), buttonHeight), "Undo")) Undo();
        GUI.enabled = true;

        var errors = _compiled.Issues.Count(i => i.Severity == SC2MissionSeverity.Error) + (_syntaxError.Length > 0 ? 1 : 0);
        var warnings = _compiled.Issues.Count(i => i.Severity == SC2MissionSeverity.Warning);
        x -= S(10f) + S(210f);
        var summary = errors > 0 ? errors + " error(s), " + warnings + " warning(s)" : warnings > 0 ? warnings + " warning(s)" : "no problems";
        GUI.Label(new Rect(x, r.y, S(210f), buttonHeight), summary, _headerNoteStyle);

        x -= S(10f) + S(190f);
        var enabled = GUI.Toggle(new Rect(x, r.y, S(190f), buttonHeight), _work.Enabled, "Use graph for _script.lua");
        if (enabled != _work.Enabled)
        {
            _work.Enabled = enabled;
            Mutate(enabled ? "Enable Mission Graph" : "Disable Mission Graph");
            Commit();
        }
    }

    private void DrawTabs(Rect r)
    {
        var x = r.x;
        foreach (var stage in _work.Stages.ToList())
        {
            var label = string.IsNullOrEmpty(stage.Name) ? stage.Id : stage.Name;
            var width = Mathf.Max(S(70f), label.Length * S(8f) + S(24f));
            if (_work.Settings.TestStartStage == stage.Id) label += "  (test)";
            if (GUI.Toggle(new Rect(x, r.y, width, r.height), _tab == stage.Id, label, GUI.skin.button) && _tab != stage.Id)
                SwitchTab(stage.Id);
            x += width + S(3f);
        }

        x += S(8f);
        if (GUI.Button(new Rect(x, r.y, S(76f), r.height), "+ Stage")) AddStage("stage");
        x += S(79f);
        if (_work.FindStage("intro") == null && GUI.Button(new Rect(x, r.y, S(70f), r.height), "+ Intro")) { AddStage("intro"); x += S(73f); }
        else if (_work.FindStage("intro") == null) x += S(73f);
        if (_work.FindStage("outro") == null && GUI.Button(new Rect(x, r.y, S(70f), r.height), "+ Outro")) AddStage("outro");

        var rightX = r.xMax;
        foreach (var (id, label) in new[] { (TabSettings, "Settings"), (TabVariables, "Variables"), (TabBases, "Bases") })
        {
            var width = S(92f);
            rightX -= width + S(3f);
            if (GUI.Toggle(new Rect(rightX, r.y, width, r.height), _tab == id, label, GUI.skin.button) && _tab != id) SwitchTab(id);
        }
    }

    private void SwitchTab(string tab)
    {
        Commit();
        _tab = tab;
        _selNode = _selEdge = -1;
        _drag = DragMode.None;
    }

    private void AddStage(string kind)
    {
        Commit();
        string id, name;
        if (kind == "stage")
        {
            var n = 1;
            while (_work.FindStage("stage" + n) != null) n++;
            id = "stage" + n;
            name = "Stage " + n;
        }
        else
        {
            id = kind;
            name = kind == "intro" ? "Intro" : "Outro";
        }
        var stage = new SC2MissionStage { Id = id, Name = name };
        if (kind == "intro") _work.Stages.Insert(0, stage);
        else _work.Stages.Add(stage);
        Mutate("Add " + name);
        Commit();
        SwitchTab(id);
    }

    // ------------------------------------------------------------------ resources

    private void EnsureResources()
    {
        if (_disc == null) _disc = BuildDisc(32);
        if (_titleStyle != null && Mathf.Approximately(_stylesScale, SC2UiScale.Scale)) return;
        _stylesScale = SC2UiScale.Scale;
        _titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(14f)), alignment = TextAnchor.MiddleLeft };
        _nodeTitleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(S(11f)), alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _nodeTitleStyle.normal.textColor = Color.white;
        _nodeTextStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(S(10f)), alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _nodeTextStyle.normal.textColor = new Color(0.82f, 0.85f, 0.9f, 1f);
        _portLabelStyle = new GUIStyle(_nodeTextStyle) { alignment = TextAnchor.MiddleRight };
        _monoStyle = new GUIStyle(GUI.skin.label) { wordWrap = false, richText = false, alignment = TextAnchor.UpperLeft, fontSize = Mathf.RoundToInt(S(11f)) };
        _noteStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = Mathf.RoundToInt(S(11f)) };
        _fieldLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(S(11f)), alignment = TextAnchor.MiddleLeft, wordWrap = true };
        _wrapTitleStyle = new GUIStyle(_titleStyle) { wordWrap = true };
        _wrapToggleStyle = new GUIStyle(GUI.skin.toggle) { wordWrap = true };
        // Library titles are long, so a button grows taller and wraps its title instead of cutting it.
        _libraryButtonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true, alignment = TextAnchor.MiddleLeft, fontSize = Mathf.RoundToInt(S(11f)) };
        _libraryButtonStyle.padding = new RectOffset(Mathf.RoundToInt(S(8f)), Mathf.RoundToInt(S(4f)), Mathf.RoundToInt(S(3f)), Mathf.RoundToInt(S(3f)));
        _headerNoteStyle = new GUIStyle(_noteStyle) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
    }

    private static Texture2D BuildDisc(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var pixels = new Color[size * size];
        var c = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (size * 0.5f);
            pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01((1f - d) * size * 0.5f));
        }
        tex.SetPixels(pixels);
        tex.Apply(false, true);
        return tex;
    }

    private static Color CategoryColor(string category)
    {
        switch (category)
        {
            case "Events": return new Color(0.80f, 0.55f, 0.15f);
            case "Objectives": return new Color(0.25f, 0.62f, 0.35f);
            case "Actions": return new Color(0.25f, 0.50f, 0.80f);
            case "Cutscene (simple)": return new Color(0.62f, 0.38f, 0.58f);
            case "Cutscene (NIS)": return new Color(0.78f, 0.40f, 0.68f);
            case "Units": return new Color(0.30f, 0.55f, 0.75f);
            case "World": return new Color(0.35f, 0.60f, 0.45f);
            case "Economy": return new Color(0.72f, 0.62f, 0.25f);
            case "Flow": return new Color(0.50f, 0.50f, 0.62f);
            case "Loops": return new Color(0.62f, 0.52f, 0.30f);
            case "Coop": return new Color(0.20f, 0.62f, 0.62f);
            case "Bases": return new Color(0.78f, 0.40f, 0.25f);
            default: return new Color(0.38f, 0.38f, 0.42f);
        }
    }

    private static void FillRect(Rect r, Color c)
    {
        var prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
    }

    private static void DrawLine(Vector2 a, Vector2 b, Color color, float width)
    {
        var delta = b - a;
        var length = delta.magnitude;
        if (length < 0.5f) return;
        var matrix = GUI.matrix;
        var prev = GUI.color;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), Texture2D.whiteTexture);
        GUI.matrix = matrix;
        GUI.color = prev;
    }
}
