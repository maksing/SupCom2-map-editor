using System;
using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// The Lobby Spawns window (Scenario &gt; Preview &gt; "Edit Lobby Spawns..."):
/// the map's minimap with one draggable marker per ARMY_n, showing where each
/// army's start marker is drawn on the minimap in the game lobby.
///
/// Edits only SC2MapDocument.Scenario.LobbyStartPositions, which export writes
/// into _scenario.lua's StartPositions. The armies' real spawn positions
/// (_save.lua, SC2TerrainData.ObjMarkers) are never read-write here — the
/// panel only reads them, to show them and to work out which armies mirror
/// each other.
///
/// A plain GUILayout overlay like SC2InfoBdfEditorPanel (a real EditorWindow
/// can't run in a Build): works identically in the Editor Game view and a
/// standalone player.
/// </summary>
public sealed class SC2LobbySpawnPanel
{
    public Action RepaintHost;
    public Action<string> ReportStatus;
    public bool IsOpen;

    private readonly Func<SC2TerrainShell> _getTerrainShell;
    private readonly Func<SC2MapSession> _getSession;
    private readonly Func<SC2ScenarioShell> _getScenarioShell;

    private static readonly SymmetryMode[] Modes =
    {
        SymmetryMode.None, SymmetryMode.X, SymmetryMode.Y, SymmetryMode.XY,
        SymmetryMode.DiagonalTLBR, SymmetryMode.DiagonalTRBL, SymmetryMode.DiagonalBoth, SymmetryMode.Radial,
    };
    private static readonly string[] ModeLabels = { "None", "X", "Y", "XY", "Diagonal (TL-BR)", "Diagonal (TR-BL)", "Diagonal (both)", "Radial" };
    private static readonly Comparison<string> ByArmyNumber = SC2LobbySpawnMath.CompareArmyNames;

    private const float HitRadius = 14f;
    /// <summary>Diameter of an army's lobby marker, in pixels of the
    /// 1024x1024 minimap image — scaled with the drawn minimap, whatever the
    /// map's world size is.</summary>
    private const float EditCircleDiameter = 150f;
    private const float MinimapImageSize = 1024f;

    private Texture2D _minimap;
    private bool _minimapRequested;
    private string _minimapStatus = "";

    private SymmetryMode _mode = SymmetryMode.None;
    private int _radialCount = 4;
    private bool _moveSymmetric = true;

    private string _selected;
    private string _dragArmy;
    private Vector2 _grabOffset;
    private Dictionary<string, Vector3> _preview; // lobby positions while a drag / numeric edit is in flight
    private List<KeyValuePair<string, int>> _partners = new();
    private string _numericPendingArmy;
    private Vector3 _numericPendingPosition;
    private bool _showGameSpawns = true;
    private int _undoable, _redoable;
    private string _status = "";
    private Vector2 _listScroll;

    private Texture2D _disc, _ring, _thinRing;
    private GUIStyle _labelStyle, _noteStyle, _numberStyle;

    public SC2LobbySpawnPanel(Func<SC2TerrainShell> getTerrainShell, Func<SC2MapSession> getSession, Func<SC2ScenarioShell> getScenarioShell)
    {
        _getTerrainShell = getTerrainShell ?? throw new ArgumentNullException(nameof(getTerrainShell));
        _getSession = getSession ?? throw new ArgumentNullException(nameof(getSession));
        _getScenarioShell = getScenarioShell ?? throw new ArgumentNullException(nameof(getScenarioShell));
    }

    public void Open()
    {
        var data = _getTerrainShell()?.Data;
        if (data == null || !data.IsInitialized || _getSession()?.Document == null)
        {
            ReportStatus?.Invoke("Lobby Spawns: create or load a terrain first.");
            return;
        }
        IsOpen = true;
        _selected = null; _dragArmy = null; _preview = null; _numericPendingArmy = null;
        _undoable = _redoable = 0;
        _status = "";
        _mode = data.Symmetry;
        _radialCount = Mathf.Clamp(data.RadialCount, 2, 8);
        _moveSymmetric = true;
        RequestMinimap(data);
    }

    private void Close()
    {
        IsOpen = false;
        _dragArmy = null; _preview = null; _numericPendingArmy = null;
        if (_minimap != null) { UnityEngine.Object.Destroy(_minimap); _minimap = null; }
        _minimapRequested = false;
    }

    private void RequestMinimap(SC2TerrainData data)
    {
        _minimapRequested = true;
        _minimapStatus = "Rendering minimap...";
        SC2MapPreviewCapture.RequestMinimapCapture(data, tex =>
        {
            _minimapRequested = false;
            if (!IsOpen) { if (tex != null) UnityEngine.Object.Destroy(tex); return; }
            if (_minimap != null) UnityEngine.Object.Destroy(_minimap);
            _minimap = tex;
            _minimapStatus = tex != null ? "" : "Minimap render failed — no camera found.";
            RepaintHost?.Invoke();
        });
        RepaintHost?.Invoke();
    }

    // ---------------------------------------------------------------- data

    private static List<string> ArmyNames(SC2TerrainData data)
    {
        var names = new List<string>();
        foreach (var marker in data.ObjMarkers)
            if (SC2ObjectsShell.IsArmyStartPosition(marker.Name) && !names.Contains(marker.Name)) names.Add(marker.Name);
        names.Sort(ByArmyNumber);
        return names;
    }

    private static Dictionary<string, Vector3> GamePositions(SC2TerrainData data)
    {
        var positions = new Dictionary<string, Vector3>();
        foreach (var marker in data.ObjMarkers)
            if (SC2ObjectsShell.IsArmyStartPosition(marker.Name) && !positions.ContainsKey(marker.Name)) positions[marker.Name] = marker.Position;
        return positions;
    }

    /// <summary>The lobby positions that currently apply. An entry whose army's
    /// real spawn has been moved (Objects tab) since it was set is rolled back
    /// — left out, so the lobby follows the real spawn again.</summary>
    private static Dictionary<string, Vector3> DocumentPositions(SC2MapDocument document, Dictionary<string, Vector3> game) =>
        SC2LobbyStartPosition.CurrentOverrides(document.Scenario.LobbyStartPositions, game);

    /// <summary>The lobby position shown for an army: the in-flight edit if
    /// any, else its document entry, else its in-game position.</summary>
    private Vector3 Effective(string army, Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game)
    {
        if (_preview != null && _preview.TryGetValue(army, out var preview)) return preview;
        if (stored.TryGetValue(army, out var lobby)) return lobby;
        return game.TryGetValue(army, out var spawn) ? spawn : Vector3.zero;
    }

    private static void WorldSize(SC2TerrainData data, out float width, out float height, out float side, out float originX, out float originZ)
    {
        width = Mathf.Max(1f, data.GridWidth * data.CellSize);
        height = Mathf.Max(1f, data.GridHeight * data.CellSize);
        // Same framing SC2MapPreviewCapture.CaptureMinimap uses: a square
        // window as wide as the longer map side, centred on the map.
        side = Mathf.Max(width, height);
        originX = width * 0.5f - side * 0.5f;
        originZ = height * 0.5f - side * 0.5f;
    }

    // The minimap reads like the exported minimap.win.dds (and the lobby):
    // x grows to the right, z grows DOWN the picture.
    private static Vector2 WorldToScreen(Vector3 world, Rect map, float side, float originX, float originZ) =>
        new(map.x + (world.x - originX) / side * map.width, map.y + (world.z - originZ) / side * map.height);

    // ---------------------------------------------------------------- draw

    public void DrawOverlay(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;
        var terrainShell = _getTerrainShell();
        var data = terrainShell?.Data;
        var session = _getSession();
        if (data == null || session?.Document == null) { Close(); return; }
        EnsureResources();

        var panelWidth = Mathf.Min(1040f, screenWidth - 40f);
        var panelHeight = Mathf.Min(760f, screenHeight - 60f);
        var rect = new Rect((screenWidth - panelWidth) * 0.5f, (screenHeight - panelHeight) * 0.5f, panelWidth, panelHeight);

        var prevDepth = GUI.depth;
        GUI.depth = -2500;
        var prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(new Rect(0, 0, screenWidth, screenHeight), Texture2D.whiteTexture);
        GUI.color = prevColor;
        GUI.Box(rect, GUIContent.none);

        var armies = ArmyNames(data);
        var game = GamePositions(data);
        var stored = DocumentPositions(session.Document, game);
        WorldSize(data, out var worldW, out var worldH, out var side, out var originX, out var originZ);

        const float pad = 12f;
        var sidebarWidth = Mathf.Min(320f, rect.width * 0.34f);
        var mapArea = new Rect(rect.x + pad, rect.y + 44f, rect.width - sidebarWidth - pad * 3f, rect.height - 44f - pad);
        var mapSize = Mathf.Max(64f, Mathf.Min(mapArea.width, mapArea.height));
        var mapRect = new Rect(mapArea.x, mapArea.y, mapSize, mapSize);

        GUI.Label(new Rect(rect.x + pad, rect.y + 10f, rect.width - 80f, 26f), "Lobby Spawns — where armies appear on the lobby minimap");
        if (GUI.Button(new Rect(rect.xMax - pad - SC2UiScale.Pxf(26f), rect.y + 10f, SC2UiScale.Pxf(26f), SC2UiScale.Pxf(22f)), "X"))
        {
            CommitPendingNumeric(session, armies, stored, game);
            Close();
            GUI.depth = prevDepth;
            return;
        }

        DrawMinimap(mapRect);
        DrawMarkers(mapRect, armies, stored, game, side, originX, originZ);
        HandleMapInput(mapRect, data, session, armies, stored, game, worldW, worldH, side, originX, originZ);

        var sidebar = new Rect(rect.xMax - sidebarWidth - pad, rect.y + 44f, sidebarWidth, rect.height - 44f - pad);
        DrawSidebar(sidebar, data, session, armies, stored, game, worldW, worldH);

        GUI.depth = prevDepth;
    }

    private void EnsureResources()
    {
        if (_disc == null) _disc = BuildDisc(32, 0f);
        if (_ring == null) _ring = BuildDisc(32, 0.72f);
        if (_thinRing == null) _thinRing = BuildDisc(256, 0.97f);
        if (_labelStyle == null)
        {
            _labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = Mathf.RoundToInt(SC2UiScale.Pxf(12f)) };
            _labelStyle.normal.textColor = Color.white;
            _numberStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
            _noteStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = Mathf.RoundToInt(SC2UiScale.Pxf(11f)) };
        }
    }

    /// <summary>A filled disc (hollow = 0) or a ring (hollow = inner radius
    /// fraction), anti-aliased at the edge.</summary>
    private static Texture2D BuildDisc(int size, float hollow)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var pixels = new Color[size * size];
        var c = (size - 1) * 0.5f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (size * 0.5f);
            var outer = Mathf.Clamp01((1f - d) * size * 0.5f);
            var inner = hollow > 0f ? Mathf.Clamp01((d - hollow) * size * 0.5f) : 1f;
            pixels[y * size + x] = new Color(1f, 1f, 1f, outer * inner);
        }
        tex.SetPixels(pixels);
        tex.Apply(false, true);
        return tex;
    }

    private void DrawMinimap(Rect mapRect)
    {
        var prev = GUI.color;
        GUI.color = new Color(0.07f, 0.08f, 0.1f, 1f);
        GUI.DrawTexture(mapRect, Texture2D.whiteTexture);
        GUI.color = prev;
        // The captured texture drawn as-is looks like the exported
        // minimap.win.dds (see SC2MapPreviewCapture.ToRgbBytes' row flip).
        if (_minimap != null) GUI.DrawTexture(mapRect, _minimap, ScaleMode.StretchToFill);
        else GUI.Label(mapRect, _minimapRequested ? "Rendering minimap..." : _minimapStatus, _labelStyle);
    }

    /// <summary>Diameter (screen pixels) of an army's lobby marker: 150 px of
    /// the 1024x1024 minimap image, scaled to the drawn minimap.</summary>
    private static float MarkerDiameter(Rect mapRect) => EditCircleDiameter / MinimapImageSize * mapRect.width;

    private void DrawMarkers(Rect mapRect, List<string> armies, Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game,
        float side, float originX, float originZ)
    {
        if (Event.current.type != EventType.Repaint) return;
        var prev = GUI.color;
        var diameter = MarkerDiameter(mapRect);

        // Lobby markers: a translucent disc of EditCircleDiameter minimap
        // pixels in the army's colour with a thin outline (white and doubled
        // for the army being edited) and the army number in the middle.
        foreach (var army in armies)
        {
            var p = WorldToScreen(Effective(army, stored, game), mapRect, side, originX, originZ);
            var isSelected = army == _selected;
            var rect = new Rect(p.x - diameter * 0.5f, p.y - diameter * 0.5f, diameter, diameter);
            var color = ArmyColor(army, GameMarker(army));
            GUI.color = new Color(color.r, color.g, color.b, isSelected ? 0.6f : 0.45f);
            GUI.DrawTexture(rect, _disc);
            GUI.color = isSelected ? Color.white : new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(rect, _thinRing);
            if (isSelected)
                GUI.DrawTexture(new Rect(rect.x - 3f, rect.y - 3f, rect.width + 6f, rect.height + 6f), _thinRing);
            GUI.color = prev;
            DrawArmyNumber(new Rect(rect.x, rect.y, rect.width, rect.height), ArmyLabel(army), diameter);
        }

        // The real in-game spawn (_save.lua) of every army, drawn on top of
        // the lobby markers: a bright yellow ring with a dark outline and
        // centre dot, joined by a line to its lobby marker when the two
        // differ.
        if (_showGameSpawns)
            foreach (var army in armies)
            {
                var lobby = WorldToScreen(Effective(army, stored, game), mapRect, side, originX, originZ);
                var real = WorldToScreen(game[army], mapRect, side, originX, originZ);
                if (Vector2.Distance(lobby, real) > 1f)
                    DrawLine(lobby, real, new Color(1f, 0.92f, 0.2f, 0.9f), 2f);
                GUI.color = new Color(0f, 0f, 0f, 0.9f);
                GUI.DrawTexture(new Rect(real.x - 11f, real.y - 11f, 22f, 22f), _disc);
                GUI.color = new Color(1f, 0.92f, 0.2f, 1f);
                GUI.DrawTexture(new Rect(real.x - 9f, real.y - 9f, 18f, 18f), _disc);
                GUI.color = new Color(0f, 0f, 0f, 0.9f);
                GUI.DrawTexture(new Rect(real.x - 6f, real.y - 6f, 12f, 12f), _disc);
                GUI.color = new Color(1f, 0.92f, 0.2f, 1f);
                GUI.DrawTexture(new Rect(real.x - 3f, real.y - 3f, 6f, 6f), _disc);
            }
        GUI.color = prev;
    }

    /// <summary>The army number in the middle of its marker: large (scaled with
    /// the marker) white text with a dark outline, readable on any terrain.</summary>
    private void DrawArmyNumber(Rect rect, string text, float diameter)
    {
        _numberStyle.fontSize = Mathf.Max(14, Mathf.RoundToInt(diameter * 0.5f));
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.95f);
        var o = Mathf.Max(1.5f, diameter * 0.03f);
        foreach (var d in new[] { new Vector2(-o, 0f), new Vector2(o, 0f), new Vector2(0f, -o), new Vector2(0f, o),
                                   new Vector2(-o, -o), new Vector2(o, -o), new Vector2(-o, o), new Vector2(o, o) })
            GUI.Label(new Rect(rect.x + d.x, rect.y + d.y, rect.width, rect.height), text, _numberStyle);
        GUI.color = Color.white;
        GUI.Label(rect, text, _numberStyle);
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

    private SC2ObjMarker GameMarker(string army) =>
        _getTerrainShell()?.Data?.ObjMarkers.FirstOrDefault(m => m.Name == army);

    private static Color ArmyColor(string army, SC2ObjMarker marker)
    {
        try
        {
            if (marker != null && !string.IsNullOrEmpty(marker.ColorHex) && marker.ColorHex.Length >= 8)
            {
                var c = SC2MarkerTemplates.HexToColor(marker.ColorHex);
                c.a = 1f;
                if (c.maxColorComponent > 0.15f) return c;
            }
        }
        catch (Exception) { /* fall through to the palette */ }
        var index = Mathf.Abs(army.GetHashCode()) % 8;
        return Color.HSVToRGB(index / 8f, 0.65f, 0.95f);
    }

    private static string ArmyLabel(string army) => army.StartsWith("ARMY_", StringComparison.Ordinal) ? army.Substring(5) : army;

    // --------------------------------------------------------------- input

    private void HandleMapInput(Rect mapRect, SC2TerrainData data, SC2MapSession session, List<string> armies,
        Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game,
        float worldW, float worldH, float side, float originX, float originZ)
    {
        var e = Event.current;
        var id = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button != 0 || !mapRect.Contains(e.mousePosition)) break;
                CommitPendingNumeric(session, armies, stored, game);
                var hit = PickArmy(e.mousePosition, mapRect, armies, stored, game, side, originX, originZ);
                if (hit == null) break;
                _selected = hit;
                _dragArmy = hit;
                _grabOffset = WorldToScreen(Effective(hit, stored, game), mapRect, side, originX, originZ) - e.mousePosition;
                _partners = _moveSymmetric && _mode != SymmetryMode.None
                    ? SC2LobbySpawnMath.FindPartners(hit, game, _mode, data.SymmetryCenter, _radialCount)
                    : new List<KeyValuePair<string, int>>();
                _preview = new Dictionary<string, Vector3>();
                GUIUtility.hotControl = id;
                e.Use();
                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl != id || _dragArmy == null) break;
                var target = e.mousePosition + _grabOffset;
                var world = new Vector3(
                    originX + (target.x - mapRect.x) / mapRect.width * side,
                    Effective(_dragArmy, stored, game).y,
                    originZ + (target.y - mapRect.y) / mapRect.height * side);
                ApplyMove(_dragArmy, world, stored, game, data, worldW, worldH);
                e.Use();
                RepaintHost?.Invoke();
                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl != id) break;
                GUIUtility.hotControl = 0;
                e.Use();
                if (_dragArmy != null && _preview != null && _preview.Count > 0)
                    Commit(session, armies, game, _preview);
                _dragArmy = null;
                _preview = null;
                break;
        }
    }

    private string PickArmy(Vector2 mouse, Rect mapRect, List<string> armies, Dictionary<string, Vector3> stored,
        Dictionary<string, Vector3> game, float side, float originX, float originZ)
    {
        var reach = Mathf.Max(HitRadius, MarkerDiameter(mapRect) * 0.5f);
        string best = null; var bestDistance = reach;
        foreach (var army in armies)
        {
            var d = Vector2.Distance(mouse, WorldToScreen(Effective(army, stored, game), mapRect, side, originX, originZ));
            if (d <= bestDistance) { best = army; bestDistance = d; }
        }
        return best;
    }

    /// <summary>Fills the in-flight preview: the moved army at
    /// <paramref name="world"/> (clamped to the map) and, with symmetry on,
    /// its partners at their mirrored slots.</summary>
    private void ApplyMove(string army, Vector3 world, Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game,
        SC2TerrainData data, float worldW, float worldH)
    {
        Vector3 Clamp(Vector3 v) => new(Mathf.Clamp(v.x, 0f, worldW), v.y, Mathf.Clamp(v.z, 0f, worldH));

        var current = new Dictionary<string, Vector3>();
        foreach (var name in game.Keys) current[name] = Effective(name, stored, game);
        _preview = new Dictionary<string, Vector3> { [army] = Clamp(world) };
        if (_partners.Count == 0) return;
        foreach (var moved in SC2LobbySpawnMath.MoveWithPartners(world, _partners, current, _mode, data.SymmetryCenter, _radialCount))
            _preview[moved.Key] = Clamp(moved.Value);
    }

    // -------------------------------------------------------------- commit

    /// <summary>The document's entries that still apply: army on the map and
    /// real spawn not moved since the entry was set (a moved one is dropped —
    /// the rollback becomes part of the edit). Entries without a recorded base
    /// adopt the current real position.</summary>
    private static List<SC2LobbyStartPosition> KeptEntries(SC2MapSession session, List<string> armies, Dictionary<string, Vector3> game)
    {
        var kept = new List<SC2LobbyStartPosition>();
        foreach (var entry in session.Document.Scenario.LobbyStartPositions)
        {
            if (entry == null || !armies.Contains(entry.ArmyName) || !entry.IsCurrent(game[entry.ArmyName])) continue;
            var copy = entry.Clone();
            if (!copy.HasBase) copy.SetBase(game[copy.ArmyName]);
            kept.Add(copy);
        }
        return kept;
    }

    /// <summary>Writes the given lobby positions into the document as ONE
    /// undo step (entries for armies that no longer exist, or whose real spawn
    /// has moved since, are dropped).</summary>
    private void Commit(SC2MapSession session, List<string> armies, Dictionary<string, Vector3> game, Dictionary<string, Vector3> changes)
    {
        var existing = SC2LobbyStartPosition.CloneList(session.Document.Scenario.LobbyStartPositions);
        var list = KeptEntries(session, armies, game);
        foreach (var change in changes)
        {
            if (!game.ContainsKey(change.Key)) continue;
            var entry = list.Find(p => p.ArmyName == change.Key);
            if (entry == null) { entry = new SC2LobbyStartPosition { ArmyName = change.Key }; list.Add(entry); }
            entry.X = change.Value.x; entry.Y = change.Value.y; entry.Z = change.Value.z;
            entry.SetBase(game[change.Key]);
        }
        list.Sort((a, b) => SC2LobbySpawnMath.CompareArmyNames(a.ArmyName, b.ArmyName));
        Execute(session, existing, list);
    }

    private void RemoveEntries(SC2MapSession session, List<string> armies, Dictionary<string, Vector3> game, IEnumerable<string> names)
    {
        var existing = SC2LobbyStartPosition.CloneList(session.Document.Scenario.LobbyStartPositions);
        var set = new HashSet<string>(names);
        var list = KeptEntries(session, armies, game).Where(p => !set.Contains(p.ArmyName)).ToList();
        Execute(session, existing, list);
    }

    private void Execute(SC2MapSession session, List<SC2LobbyStartPosition> before, List<SC2LobbyStartPosition> after)
    {
        if (SameList(before, after)) return;
        session.History.Execute(session.Document, new SC2SetLobbyStartPositionsCommand(after));
        _undoable++; _redoable = 0;
        _getScenarioShell()?.NoteExternalAction();
        RepaintHost?.Invoke();
    }

    private static bool SameList(List<SC2LobbyStartPosition> a, List<SC2LobbyStartPosition> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].ArmyName != b[i].ArmyName || a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Z != b[i].Z ||
                a[i].HasBase != b[i].HasBase || a[i].BaseX != b[i].BaseX || a[i].BaseZ != b[i].BaseZ) return false;
        return true;
    }

    private void CommitPendingNumeric(SC2MapSession session, List<string> armies, Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game)
    {
        if (_numericPendingArmy == null || _preview == null) { _numericPendingArmy = null; return; }
        var preview = _preview;
        _numericPendingArmy = null;
        _preview = null;
        if (preview.Count > 0) Commit(session, armies, game, preview);
    }

    // ------------------------------------------------------------- sidebar

    private void DrawSidebar(Rect area, SC2TerrainData data, SC2MapSession session, List<string> armies,
        Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game, float worldW, float worldH)
    {
        GUILayout.BeginArea(area);
        GUILayout.Label("Only the lobby minimap changes (_scenario.lua). The armies' real spawns (_save.lua) are not touched.", _noteStyle);
        GUILayout.Space(6);

        SC2EnvFieldGui.SectionLabel("Armies");
        if (armies.Count == 0)
        {
            GUILayout.Label("No ARMY_n markers on this map yet — add them on the Objects tab.", _noteStyle);
        }
        else
        {
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(Mathf.Min(SC2UiScale.Pxf(26f) * armies.Count + 6f, area.height * 0.32f)));
            foreach (var army in armies)
            {
                GUILayout.BeginHorizontal();
                var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), SC2UiScale.Pxf(14f), GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(SC2UiScale.Pxf(14f)));
                var prevColor = GUI.color;
                GUI.color = ArmyColor(army, GameMarker(army));
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                GUI.color = prevColor;
                var moved = stored.ContainsKey(army);
                var label = army + (moved ? "  •" : "");
                var isSelected = army == _selected;
                if (GUILayout.Toggle(isSelected, label, GUI.skin.button) && !isSelected)
                {
                    CommitPendingNumeric(session, armies, stored, game);
                    _selected = army;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Label("• = has its own lobby position", _noteStyle);
        }

        if (_selected != null && game.ContainsKey(_selected))
            DrawSelectedArmy(data, session, armies, stored, game, worldW, worldH);

        GUILayout.Space(8);
        DrawSymmetry(data);

        GUILayout.Space(8);
        _showGameSpawns = GUILayout.Toggle(_showGameSpawns, "Show real spawn positions (yellow)");

        GUILayout.FlexibleSpace();
        if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, _noteStyle);

        GUILayout.BeginHorizontal();
        GUI.enabled = _undoable > 0;
        if (GUILayout.Button("Undo")) DoUndo();
        GUI.enabled = _redoable > 0;
        if (GUILayout.Button("Redo")) DoRedo();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUI.enabled = stored.Count > 0;
        if (GUILayout.Button("Reset all to real spawns"))
        {
            CommitPendingNumeric(session, armies, stored, game);
            RemoveEntries(session, armies, game, stored.Keys.ToList());
        }
        GUI.enabled = !_minimapRequested;
        if (GUILayout.Button("Refresh minimap")) RequestMinimap(data);
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void DrawSelectedArmy(SC2TerrainData data, SC2MapSession session, List<string> armies,
        Dictionary<string, Vector3> stored, Dictionary<string, Vector3> game, float worldW, float worldH)
    {
        GUILayout.Space(6);
        SC2EnvFieldGui.SectionLabel(_selected + " — lobby position");
        var eff = Effective(_selected, stored, game);
        var nx = SC2EnvFieldGui.FloatField("X", eff.x, "lobby.x");
        var nz = SC2EnvFieldGui.FloatField("Z", eff.z, "lobby.z");
        if (_dragArmy == null && (!Mathf.Approximately(nx, eff.x) || !Mathf.Approximately(nz, eff.z)))
        {
            // Typing edits the preview per keystroke; ONE undo entry is
            // committed when the field loses focus / another army is picked.
            if (_numericPendingArmy != _selected)
            {
                CommitPendingNumeric(session, armies, stored, game);
                _numericPendingArmy = _selected;
                _partners = _moveSymmetric && _mode != SymmetryMode.None
                    ? SC2LobbySpawnMath.FindPartners(_selected, game, _mode, data.SymmetryCenter, _radialCount)
                    : new List<KeyValuePair<string, int>>();
            }
            ApplyMove(_selected, new Vector3(nx, eff.y, nz), stored, game, data, worldW, worldH);
        }
        var focused = GUI.GetNameOfFocusedControl();
        if (_numericPendingArmy != null && focused != "SC2EnvNum_lobby.x" && focused != "SC2EnvNum_lobby.z")
            CommitPendingNumeric(session, armies, stored, game);

        var real = game[_selected];
        GUILayout.Label($"Real spawn: X {real.x:0.#}  Z {real.z:0.#}", _noteStyle);
        GUI.enabled = stored.ContainsKey(_selected);
        if (GUILayout.Button("Reset to real spawn"))
        {
            CommitPendingNumeric(session, armies, stored, game);
            RemoveEntries(session, armies, game, new[] { _selected });
        }
        GUI.enabled = true;
    }

    private void DrawSymmetry(SC2TerrainData data)
    {
        SC2EnvFieldGui.SectionLabel("Symmetry");
        _moveSymmetric = GUILayout.Toggle(_moveSymmetric, "Move mirrored armies together");
        var index = Mathf.Max(0, Array.IndexOf(Modes, _mode));
        var next = GUILayout.SelectionGrid(index, ModeLabels, 2);
        if (next != index) _mode = Modes[next];
        if (_mode == SymmetryMode.Radial)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Count", GUILayout.Width(SC2UiScale.Pxf(50f)));
            _radialCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(_radialCount, 2, 8));
            GUILayout.Label(_radialCount.ToString(), GUILayout.Width(SC2UiScale.Pxf(20f)));
            GUILayout.EndHorizontal();
        }

        if (_moveSymmetric && _mode != SymmetryMode.None && _selected != null)
        {
            var partners = SC2LobbySpawnMath.FindPartners(_selected, GamePositions(data), _mode, data.SymmetryCenter, _radialCount);
            GUILayout.Label(partners.Count == 0
                ? _selected + " has no mirrored army for this symmetry — it moves alone."
                : _selected + " moves with " + string.Join(", ", partners.Select(p => p.Key)) + ".", _noteStyle);
        }
        GUILayout.Label("Mirrored armies are found from their real spawn positions, around the map's symmetry centre.", _noteStyle);
    }

    private void DoUndo()
    {
        var shell = _getScenarioShell();
        if (shell == null || _undoable <= 0) return;
        shell.Undo();
        _undoable--; _redoable++;
        _numericPendingArmy = null; _preview = null;
        RepaintHost?.Invoke();
    }

    private void DoRedo()
    {
        var shell = _getScenarioShell();
        if (shell == null || _redoable <= 0) return;
        shell.Redo();
        _redoable--; _undoable++;
        _numericPendingArmy = null; _preview = null;
        RepaintHost?.Invoke();
    }
}
