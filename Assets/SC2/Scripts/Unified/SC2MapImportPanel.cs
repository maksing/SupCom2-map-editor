using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using SC2.MapEditor.Document;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Unified "Import Map" panel — replaces the old "Import .scd" (legacy scene)
/// and "Import .scd (Document)" (raw-only, never parsed content) menu items
/// with the ONE real flow: pick a .scd file or an already-extracted folder,
/// optionally scan sibling archives (a real game install splits one map's
/// files across maps.scd/lua.scd/textures.scd — see
/// Scripts/MapDocument/Domain/SC2SourceContainer.cs's
/// SC2SiblingArchiveDiscovery), pick which of the (possibly dozens of) real
/// maps found to import, then run every already-correct single-domain
/// importer against it via SC2MapImportOrchestrator.
///
/// Plain GUILayout, no EditorGUILayout/EditorStyles — same build-compatible
/// pattern as SC2ObjectsShell/SC2CostsShell, so this renders identically in
/// the Editor Game View and in a standalone Build &amp; Run player; only the
/// file/folder picker dialog differs (EditorUtility vs SC2RuntimeFileDialog).
/// </summary>
public sealed class SC2MapImportPanel
{
    public Action RepaintHost;
    public Action<string> ReportStatus;

    /// <summary>Host wiring for what a successful import should DO to live
    /// scene/UI state — this panel only orchestrates data, it never reaches
    /// into _importedTerrains/_collision2Shell/etc itself, matching how
    /// OpenTerrainBdfImport/OpenDecalsBdfImport already keep that wiring in
    /// SC2GameViewEditorUI, not inside the importer classes themselves.</summary>
    public Action<SC2MapImportOrchestrator.Result> OnImportComplete;

    public bool IsOpen;

    private readonly SC2MapSession _session;
    private readonly Func<SC2TerrainData> _getData;

    private string _primaryPath = "";
    private List<SC2SiblingArchiveDiscovery.SiblingArchive> _siblings = new();
    private readonly Dictionary<string, bool> _siblingChecked = new(StringComparer.OrdinalIgnoreCase);

    private ISC2SourceContainerReader _reader;
    private List<SC2ScenarioCandidate> _candidates;
    private int _selectedCandidate = -1;

    private SC2MapImportOrchestrator.Result _lastResult;
    private string _errorMessage;
    private bool _busy;
    private Vector2 _scroll;

    // Units/props placed by the import only get real 3D models once the
    // game-asset catalog (blueprints -> SCM -> DDS) is indexed - previously
    // that only happened lazily whenever the Objects tab was next drawn, so
    // a freshly imported map could sit with invisible props/units until the
    // user happened to open that tab. Reuses SC2UnitPropBrowser's own
    // Task.Run+poll indexing (and its static SharedIndexes cache, so this is
    // a no-op if the Objects tab already indexed the same root this
    // session) rather than inventing a second indexing pathway.
    private readonly SC2UnitPropBrowser _assetIndexer = new SC2UnitPropBrowser();
    private bool _importPendingIndex;

    public SC2MapImportPanel(SC2MapSession session, Func<SC2TerrainData> getData)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _getData = getData ?? throw new ArgumentNullException(nameof(getData));
    }

    public void Open()
    {
        IsOpen = true;
        ResetPick();
    }

    private void ResetPick()
    {
        CloseReader();
        _primaryPath = "";
        _siblings = new List<SC2SiblingArchiveDiscovery.SiblingArchive>();
        _siblingChecked.Clear();
        _candidates = null;
        _selectedCandidate = -1;
        _lastResult = null;
        _errorMessage = null;
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    private void PickFile()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Select .scd map", "", "scd");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Select .scd map", "SC2 map (*.scd)", "scd");
#endif
        if (!string.IsNullOrEmpty(path)) SetPrimaryPath(path);
    }

    private void PickFolder()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFolderPanel("Select .scd folder", "", "");
#else
        var path = SC2RuntimeFileDialog.OpenFolder("Select .scd folder");
#endif
        if (!string.IsNullOrEmpty(path)) SetPrimaryPath(path);
    }

    private void SetPrimaryPath(string path)
    {
        CloseReader();
        _primaryPath = path;
        _candidates = null;
        _selectedCandidate = -1;
        _lastResult = null;
        _errorMessage = null;

        _siblings = SC2SiblingArchiveDiscovery.FindSiblings(path);
        _siblingChecked.Clear();
        foreach (var sib in _siblings) _siblingChecked[sib.Path] = sib.DefaultChecked;

        ReportStatus?.Invoke($"Picked '{Path.GetFileName(path.TrimEnd('\\', '/'))}' — {_siblings.Count} sibling archive(s) found.");
    }

    private void Scan()
    {
        if (string.IsNullOrEmpty(_primaryPath)) return;
        _busy = true;
        _errorMessage = null;
        try
        {
            CloseReader();
            var checkedSiblings = _siblings.Where(s => _siblingChecked.TryGetValue(s.Path, out var v) && v).Select(s => s.Path);
            _reader = SC2MapImportOrchestrator.OpenComposite(_primaryPath, checkedSiblings);
            _candidates = SC2MapImportOrchestrator.FindCandidates(_reader);
            _selectedCandidate = _candidates.Count == 1 && _candidates[0].CanImport ? 0 : -1;
            ReportStatus?.Invoke($"Scanned: {_candidates.Count} map(s) found across {1 + checkedSiblings.Count()} archive(s).");
        }
        catch (Exception ex)
        {
            _errorMessage = "Scan failed: " + ex.Message;
            ReportStatus?.Invoke(_errorMessage);
        }
        finally
        {
            _busy = false;
            RepaintHost?.Invoke();
        }
    }

    /// <summary>Entry point for the "Import selected map" button. Makes sure
    /// units/props can resolve to real models as soon as the import finishes
    /// (rather than only whenever the Objects tab is next opened) by
    /// indexing the game-asset catalog first, and deferring the actual
    /// import until that finishes if it wasn't already indexed.</summary>
    private void BeginImport()
    {
        _assetIndexer.EnsureIndexed();
        if (_assetIndexer.IsIndexing)
        {
            _importPendingIndex = true;
            _busy = true;
            ReportStatus?.Invoke("Indexing units/props before import...");
        }
        else
        {
            ImportSelected();
        }
    }

    private void ImportSelected()
    {
        if (_reader == null || _candidates == null || _selectedCandidate < 0 || _selectedCandidate >= _candidates.Count)
            return;
        var data = _getData();
        if (data == null || !data.IsInitialized)
        {
            _errorMessage = "No active terrain to import into — create or load a terrain first (Terrain tab).";
            return;
        }

        _busy = true;
        _errorMessage = null;
        try
        {
            var candidate = _candidates[_selectedCandidate];
            _lastResult = SC2MapImportOrchestrator.ImportSelected(data, _session, _reader, candidate);
            ReportStatus?.Invoke(_lastResult.Summary);
            OnImportComplete?.Invoke(_lastResult);
        }
        catch (Exception ex)
        {
            _errorMessage = "Import failed: " + ex.Message;
            ReportStatus?.Invoke(_errorMessage);
        }
        finally
        {
            _busy = false;
            RepaintHost?.Invoke();
        }
    }

    public void DrawOverlay(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;

        _assetIndexer.PollLoadTask();
        if (_importPendingIndex && !_assetIndexer.IsIndexing)
        {
            _importPendingIndex = false;
            _busy = false;
            ImportSelected();
        }

        var panelWidth = Mathf.Min(720f, screenWidth - 40f);
        var panelHeight = Mathf.Min(640f, screenHeight - 80f);
        var rect = new Rect((screenWidth - panelWidth) * 0.5f, (screenHeight - panelHeight) * 0.5f, panelWidth, panelHeight);

        var prevDepth = GUI.depth;
        GUI.depth = -2500;

        var prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(new Rect(0, 0, screenWidth, screenHeight), Texture2D.whiteTexture);
        GUI.color = prevColor;

        GUI.Box(rect, GUIContent.none);
        GUILayout.BeginArea(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f));

        GUILayout.BeginHorizontal();
        GUILayout.Label("Import Map");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("X", GUILayout.Width(SC2UiScale.Pxf(26f)), GUILayout.Height(SC2UiScale.Pxf(22f)))) { CloseReader(); IsOpen = false; }
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        // Restores whatever GUI.enabled was BEFORE this row (not hardcode
        // true) — see SC2ObjectsShell.DrawUndoRedoRow's own comment for why.
        var guiEnabledBeforePickRow = GUI.enabled;
        GUI.enabled = guiEnabledBeforePickRow && !_busy;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Pick .scd file...", GUILayout.Width(SC2UiScale.Pxf(180f)))) PickFile();
        if (GUILayout.Button("Pick .scd folder...", GUILayout.Width(SC2UiScale.Pxf(180f)))) PickFolder();
        GUILayout.EndHorizontal();
        GUI.enabled = guiEnabledBeforePickRow;

        if (!string.IsNullOrEmpty(_primaryPath))
            GUILayout.Label("Primary: " + _primaryPath);
        if (_busy)
            GUILayout.Label("Working...");
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            var prevContentColor = GUI.contentColor;
            GUI.contentColor = new Color(0.9f, 0.4f, 0.4f);
            GUILayout.Label(_errorMessage);
            GUI.contentColor = prevContentColor;
        }

        _scroll = GUILayout.BeginScrollView(_scroll);

        DrawSiblingChecklist();
        DrawScanButtonAndCandidates();
        DrawLastResult();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
        GUI.depth = prevDepth;

        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && !rect.Contains(e.mousePosition))
            e.Use(); // swallow clicks outside the modal instead of closing it
    }

    private void DrawSiblingChecklist()
    {
        if (string.IsNullOrEmpty(_primaryPath) || _siblings.Count == 0) return;

        GUILayout.Space(8);
        GUILayout.Label("Sibling archives found next to the primary pick — a real game install splits one " +
                        "map's files across several of these (BDFs in maps.scd, compiled Lua in lua.scd, " +
                        "shared textures in textures.scd). Check the ones to scan alongside the primary pick:");
        foreach (var sib in _siblings)
        {
            GUILayout.BeginHorizontal();
            var was = _siblingChecked.TryGetValue(sib.Path, out var v) && v;
            var now = GUILayout.Toggle(was, "", GUILayout.Width(SC2UiScale.Pxf(54f)));
            if (now != was) _siblingChecked[sib.Path] = now;
            GUILayout.Label(sib.Stem, GUILayout.Width(SC2UiScale.Pxf(160f)));
            GUILayout.Label(sib.Path, "label");
            GUILayout.EndHorizontal();
        }
    }

    private void DrawScanButtonAndCandidates()
    {
        if (string.IsNullOrEmpty(_primaryPath)) return;

        GUILayout.Space(8);
        // See DrawScanButtonAndCandidates' own picker-row comment above for
        // why this restores the prior value instead of hardcoding true.
        var guiEnabledBeforeScanButton = GUI.enabled;
        GUI.enabled = guiEnabledBeforeScanButton && !_busy;
        if (GUILayout.Button("Scan for maps", GUILayout.Height(SC2UiScale.Pxf(26f))))
            Scan();
        GUI.enabled = guiEnabledBeforeScanButton;

        if (_candidates == null) return;

        GUILayout.Space(6);
        GUILayout.Label($"Maps found ({_candidates.Count}) — pick exactly one, then Import:");
        for (var i = 0; i < _candidates.Count; i++)
        {
            var c = _candidates[i];
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            var wasSelected = _selectedCandidate == i;
            var isSelected = GUILayout.Toggle(wasSelected, "", SC2UnifiedSkin.RadioStyle);
            if (isSelected && !wasSelected) _selectedCandidate = i;
            else if (!isSelected && wasSelected) _selectedCandidate = -1;
            GUILayout.Label($"{c.DisplayName ?? c.BdfStem ?? "(unnamed)"}   [{c.ScenarioEntryPath}]");
            GUILayout.FlexibleSpace();
            GUILayout.Label(c.CanImport ? "OK" : "BLOCKED");
            GUILayout.EndHorizontal();

            GUILayout.Label($"stem={c.BdfStem}   map={c.MapPath}");
            foreach (var msg in c.Messages)
                GUILayout.Label($"   [{msg.Severity}] {msg.Message}");

            GUILayout.EndVertical();
        }

        var canImportSelected = _selectedCandidate >= 0 && _candidates[_selectedCandidate].CanImport;
        // See DrawScanButtonAndCandidates' own picker-row comment above for
        // why this restores the prior value instead of hardcoding true.
        var guiEnabledBeforeImportButton = GUI.enabled;
        GUI.enabled = guiEnabledBeforeImportButton && canImportSelected && !_busy;
        if (GUILayout.Button("Import selected map", GUILayout.Height(SC2UiScale.Pxf(28f))))
            BeginImport();
        GUI.enabled = guiEnabledBeforeImportButton;

        if (_importPendingIndex)
            GUILayout.Label("Indexing units/props before import — models will resolve as soon as this finishes...");
    }

    private void DrawLastResult()
    {
        if (_lastResult == null) return;

        GUILayout.Space(10);
        GUILayout.Label(_lastResult.Summary);
        foreach (var stage in _lastResult.Stages)
        {
            GUILayout.BeginHorizontal();
            var icon = stage.Ok ? "OK" : "--";
            GUILayout.Label(icon, GUILayout.Width(SC2UiScale.Pxf(28f)));
            GUILayout.Label(stage.Name, GUILayout.Width(SC2UiScale.Pxf(280f)));
            GUILayout.Label(stage.Detail ?? "", "label");
            GUILayout.EndHorizontal();
        }
    }
}
