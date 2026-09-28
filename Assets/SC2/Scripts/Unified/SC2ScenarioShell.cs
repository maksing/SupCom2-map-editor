using UnityEditor;
using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>
/// Scenario editor for the PROCEDURAL SC2TerrainData workflow — the Build
/// &amp; Run GameView UI's "Scenario" tab (SC2GameViewEditorUI.DrawMainPanel,
/// case 5), which previously fell straight through to the generic
/// "Panel is ready for migration from the editor window." stub (no
/// SC2ScenarioShell/DrawScenarioInspector existed at all before this pass).
/// Sibling of SC2EnvironmentShell/SC2Collision2Shell — same "lives in
/// Scripts/Unified so it compiles for both the Editor and a real .exe
/// build" reasoning.
///
/// SC2ScenarioDocument is scoped around the IMPORTED-map .scd
/// workflow (KnownFields is populated by executing a real scenario.lua's
/// bytecode through KopiLua — see that class's own doc comment) — almost
/// none of it has a live equivalent on the procedural SC2TerrainData this
/// shell edits. Two exceptions, both live procedural fields: SC2TerrainData.
/// MapName (the technical file/folder identifier — NOT Document-backed,
/// see DrawMapNameField's own note) and SC2TerrainData.DisplayName (the real
/// in-game display name, maps 1:1 onto SC2ScenarioDocument.KnownFields.Name /
/// SC2SetScenarioNameCommand — confirmed via a real vanilla map that these
/// two genuinely differ, e.g. name='!Eclipse map' vs. MapName=
/// 'SC2_D3_Eclipse'; this shell used to conflate them into one MapName field,
/// silently losing an imported map's real display name on every export).
/// Every other KnownFields member (Description/Type/CampaignId/...) has no
/// procedural-workflow source and is deliberately left out rather than
/// inventing new SC2TerrainData fields just to have something to wire — see
/// this class's own "not done" note in the status doc.
/// </summary>
public sealed class SC2ScenarioShell
{
    private readonly SC2TerrainShell _terrainShell;
    private readonly SC2MapSession _session;
    public System.Action RepaintHost;
    /// <summary>Opens the Lobby Spawns window (owned by SC2GameViewEditorUI,
    /// like the other overlay panels).</summary>
    public System.Action OpenLobbySpawnEditor;

    private long _lastActionTimestamp;

    /// <summary>The Lobby Spawns window records its own document commands
    /// (through _session.History) — this makes them the "latest action" for
    /// the shared Ctrl+Z dispatch, exactly as this shell's own edits do.</summary>
    public void NoteExternalAction() => _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>See SC2ObjectsShell's identical property for why.</summary>
    public bool HasUndo => _session != null && _session.History.CanUndo;
    public bool HasRedo => _session != null && _session.History.CanRedo;
    public long LastActionTimestamp => _lastActionTimestamp;

    private SC2TerrainData Data => _terrainShell?.Data;
    public bool IsReady => Data != null && Data.IsInitialized;

    private SC2TerrainData _loadedForData;

    /// <summary>Value the Map Name field held when it last GAINED focus —
    /// null while the field isn't focused. Committing on focus-loss (rather
    /// than per-keystroke) avoids recording one undo entry per character
    /// typed, the same class of problem DrawCompactSlider's
    /// DraggingSliderKeys coalescing already solves for continuous
    /// sliders.</summary>
    private string _mapNameBeforeEdit;
    private bool _mapNameFieldWasFocused;

    /// <summary>Same focus-commit pattern as _mapNameBeforeEdit/
    /// _mapNameFieldWasFocused above, for the separate Display Name field.</summary>
    private string _displayNameBeforeEdit;
    private bool _displayNameFieldWasFocused;

    // "_PC_mapshot.dds" preview thumbnail — rebuilt only when Data.
    // MapShotBgraPixels actually changes (_mapShotThumbnailSource caches the
    // exact array reference last used to build it), not every OnGUI call.
    // Moved here (Preview sub-tab) from SC2ExportPanel, which used to own
    // this section directly above the Export button.
    private Texture2D _mapShotThumbnail;
    private byte[] _mapShotThumbnailSource;
    private string _captureStatus;

    // Render-on-demand only — never persisted. The minimap that actually
    // ships still renders fresh on every real Export click (see
    // SC2ExportPanel's Export button handler); this is purely so the user
    // can check the framing without running a full export.
    private Texture2D _minimapPreviewTexture;
    // The imported minimap bytes _minimapPreviewTexture was decoded from (null = a rendered preview or none).
    private byte[] _minimapThumbSource;
    private string _minimapPreviewStatus;

    public SC2ScenarioShell(SC2TerrainShell terrainShell, SC2MapSession session)
    {
        _terrainShell = terrainShell;
        _session = session;
    }

    public void DrawEmbeddedGUI(float panelWidth, int subTab)
    {
        // See SC2TerrainShell.SyncEffectiveViewWidth's own doc comment — keeps
        // GetUnifiedContentWidth/DrawUnifiedSegmentGrid sizing against THIS
        // panel's own current width instead of whatever another tab last cached.
        SC2TerrainShell.SyncEffectiveViewWidth(panelWidth);
        var w = Mathf.Max(1f, panelWidth);
        GUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));

        if (!IsReady)
        {
            SC2TerrainShell.DrawUnifiedNote("Create/load a terrain first (Terrain tab) — Scenario edits the procedural map's own name, used by Save/Export.");
            GUILayout.EndVertical();
            return;
        }

        if (_loadedForData != Data) LoadFromNewData();

        DrawUndoRedoRow(w);
        GUILayout.Space(4);

        if (subTab == 1) DrawScriptsTab(w);
        else if (subTab == 2) DrawPreviewTab(w);
        else DrawInfoTab(w);

        GUILayout.EndVertical();
    }

    private void DrawInfoTab(float w)
    {
        DrawDisplayNameField();
        SC2TerrainShell.DrawUnifiedNote("Display Name: shown in the game's own map browser (_scenario.lua ScenarioInfo.name) — genuinely separate from the technical Map Name below, confirmed against real vanilla maps (e.g. name='!Eclipse map' vs. MapName='SC2_D3_Eclipse').");
        GUILayout.Space(4);
        DrawMapNameField();
        SC2TerrainShell.DrawUnifiedNote("Map Name: the technical identifier used for every file/folder name on export — terrain.bdf, maps/<MapName>/, the .scd archive/folder name, and the map/save/script path fields inside _scenario.lua. Every other Scenario field (Description/Type/CampaignId/...) belongs to the imported-.scd workflow and has no equivalent here yet — see the status doc for why.");
    }

    /// <summary>_script.lua's one real per-map setting — confirmed against a
    /// real vanilla _script.lua (user-supplied) that every other line is
    /// fixed engine boilerplate; only ScenarioFramework.SetPlayableArea('X')'s
    /// argument varies per map. Lists every Area (SC2ObjZone) so the user can
    /// pick which one is "the" playable area — SC2TerrainData.PlayableAreaName
    /// stores the choice; SC2ObjectsExport.RegenerateScriptLua reads it at
    /// export time.</summary>
    private void DrawScriptsTab(float w)
    {
        SC2EnvFieldGui.SectionLabel("Playable Area");
        SC2TerrainShell.DrawUnifiedNote("Passed to ScenarioFramework.SetPlayableArea(...) in the exported _script.lua — the game clips the actual playable region to this Area's rectangle. Pick which Area (Objects tab) that is.");

        var zones = Data.ObjZones;
        if (zones == null || zones.Count == 0)
        {
            SC2TerrainShell.DrawUnifiedNote("No Areas exist yet — add one on the Objects tab (Areas) first.");
            return;
        }

        foreach (var zone in zones)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
            var isSelected = Data.PlayableAreaName == zone.Name;
            var nowSelected = GUILayout.Toggle(isSelected, "", GUILayout.Width(SC2UiScale.Pxf(20f)));
            if (nowSelected && !isSelected)
            {
                _terrainShell.RecordUndoCheckpoint("Set Playable Area");
                Data.PlayableAreaName = zone.Name;
            }
            GUILayout.Label(zone.Name, GUILayout.Width(w * 0.5f));
            GUILayout.Label(zone.Role, GUILayout.Width(w * 0.3f));
            GUILayout.EndHorizontal();
        }

        if (!string.IsNullOrEmpty(Data.PlayableAreaName) && !zones.Exists(z => z.Name == Data.PlayableAreaName))
            SC2TerrainShell.DrawUnifiedNote($"⚠ Selected Area '{Data.PlayableAreaName}' no longer exists (renamed/deleted) — pick another.");
    }

    /// <summary>Minimap + mapshot review — moved here from the Export tab so
    /// checking/capturing them isn't tied to the Export button itself. The
    /// minimap shown here is render-on-demand for review only (never
    /// persisted); the real one written to disk still renders fresh from
    /// whatever the map currently looks like on every actual Export click
    /// (see SC2ExportPanel's Export button handler). The mapshot below is
    /// framed by hand and stored on the map, same as before.</summary>
    private void DrawPreviewTab(float w)
    {
        SC2EnvFieldGui.SectionLabel("Minimap");
        SC2TerrainShell.DrawUnifiedNote("Orthographic top-down render (RNM + props) — written fresh into minimap.win.dds on every Export, unless a custom image is imported below. Preview one here any time to check the framing.");

        // An imported minimap is shown as soon as the tab opens (also after the map was reloaded or switched).
        var custom = Data.CustomMinimapPng is { Length: > 0 } ? Data.CustomMinimapPng : null;
        if (custom != null && !ReferenceEquals(_minimapThumbSource, custom))
        {
            if (_minimapPreviewTexture != null) UnityEngine.Object.DestroyImmediate(_minimapPreviewTexture);
            _minimapPreviewTexture = SC2MapPreviewCapture.DecodeCustomMinimap(Data);
            _minimapThumbSource = custom; // also when decoding failed, so it is not retried every frame
        }
        else if (custom == null && _minimapThumbSource != null)
        {
            if (_minimapPreviewTexture != null) UnityEngine.Object.DestroyImmediate(_minimapPreviewTexture);
            _minimapPreviewTexture = null;
            _minimapThumbSource = null;
        }

        if (_minimapPreviewTexture != null)
        {
            var minimapRect = GUILayoutUtility.GetRect(w, w);
            GUI.DrawTexture(minimapRect, _minimapPreviewTexture, ScaleMode.ScaleToFit);
        }

        var minimapPending = SC2MapPreviewCapture.IsCapturePending;
        var guiEnabledBeforeMinimap = GUI.enabled;
        GUI.enabled = guiEnabledBeforeMinimap && !minimapPending;
        if (GUILayout.Button(minimapPending ? "Rendering..." : "Preview Minimap", GUILayout.Width(w), GUILayout.MaxWidth(w)))
        {
            _minimapPreviewStatus = "Rendering...";
            var data = Data;
            var terrainShell = _terrainShell;
            SC2MapPreviewCapture.RequestMinimapCapture(data, tex =>
            {
                if (_minimapPreviewTexture != null) UnityEngine.Object.DestroyImmediate(_minimapPreviewTexture);
                _minimapPreviewTexture = tex;
                _minimapThumbSource = data.CustomMinimapPng is { Length: > 0 } ? data.CustomMinimapPng : null;
                terrainShell.MarkDirtyAndRepaint();
                _minimapPreviewStatus = tex != null ? "Preview rendered." : "Render failed — no camera found.";
            }, terrainShell);
        }
        GUI.enabled = guiEnabledBeforeMinimap;
        if (!string.IsNullOrEmpty(_minimapPreviewStatus))
            SC2TerrainShell.DrawUnifiedNote(_minimapPreviewStatus);

        if (GUILayout.Button("Import Custom Minimap Image...", GUILayout.Width(w), GUILayout.MaxWidth(w)))
            ImportCustomMinimap();
        if (Data.CustomMinimapPng != null && Data.CustomMinimapPng.Length > 0)
        {
            SC2TerrainShell.DrawUnifiedNote("A custom minimap image is in use: export, the previews and the Lobby Spawns editor use it instead of rendering the map.");
            if (GUILayout.Button("Remove Custom Minimap (render the map again)", GUILayout.Width(w), GUILayout.MaxWidth(w)))
            {
                Data.CustomMinimapPng = null;
                EditorUtility.SetDirty(Data);
                if (_minimapPreviewTexture != null) { UnityEngine.Object.DestroyImmediate(_minimapPreviewTexture); _minimapPreviewTexture = null; }
                _minimapPreviewStatus = "Custom minimap removed — the minimap renders from the map again.";
                _terrainShell.MarkDirtyAndRepaint();
            }
        }

        GUILayout.Space(12);

        SC2EnvFieldGui.SectionLabel("Map Preview");
        SC2TerrainShell.DrawUnifiedNote("Shown in a real map-select list — framed by hand: navigate the viewport to the angle you want, then Capture. Stored on the map and reused at every export until re-captured.");

        RefreshMapShotThumbnail();
        if (_mapShotThumbnail != null)
        {
            var previewRect = GUILayoutUtility.GetRect(w, w * SC2MapPreviewCapture.MapShotHeight / (float)SC2MapPreviewCapture.MapShotWidth);
            GUI.DrawTexture(previewRect, _mapShotThumbnail, ScaleMode.ScaleToFit);
        }
        else
        {
            SC2TerrainShell.DrawUnifiedNote("No preview captured or imported yet — export writes a black picture with the preview frame.");
        }

        var capturePending = SC2MapPreviewCapture.IsCapturePending;
        var guiEnabledBeforeCapture = GUI.enabled;
        GUI.enabled = guiEnabledBeforeCapture && !capturePending;
        if (GUILayout.Button(capturePending ? "Capturing..." : "Capture Preview from Current View", GUILayout.Width(w), GUILayout.MaxWidth(w)))
        {
            _captureStatus = "Capturing...";
            var data = Data;
            var terrainShell = _terrainShell;
            SC2MapPreviewCapture.RequestPreviewCapture(tex =>
            {
                if (tex == null) { _captureStatus = "Capture failed — no camera found."; return; }
                data.MapShotBgraPixels = SC2MapPreviewCapture.ToBgraBytes(tex);
                UnityEngine.Object.DestroyImmediate(tex);
                EditorUtility.SetDirty(data);
                terrainShell.MarkDirtyAndRepaint();
                _captureStatus = "Preview captured.";
            });
        }
        GUI.enabled = guiEnabledBeforeCapture;
        if (GUILayout.Button("Import Custom Preview Image...", GUILayout.Width(w), GUILayout.MaxWidth(w)))
            ImportCustomMapShot();
        if (Data.MapShotBgraPixels != null && GUILayout.Button("Clear Preview (export black + frame)", GUILayout.Width(w), GUILayout.MaxWidth(w)))
        {
            Data.MapShotBgraPixels = null;
            EditorUtility.SetDirty(Data);
            _terrainShell.MarkDirtyAndRepaint();
            _captureStatus = "Preview cleared.";
        }
        if (!string.IsNullOrEmpty(_captureStatus))
            SC2TerrainShell.DrawUnifiedNote(_captureStatus);

        GUILayout.Space(12);

        SC2EnvFieldGui.SectionLabel("Lobby Spawns");
        SC2TerrainShell.DrawUnifiedNote("Where each army's start marker is drawn on the minimap in the game lobby (_scenario.lua). Only the lobby picture changes — the armies' real spawn positions (_save.lua) stay exactly where they are.");
        if (GUILayout.Button("Edit Lobby Spawns...", GUILayout.Width(w), GUILayout.MaxWidth(w)))
            OpenLobbySpawnEditor?.Invoke();
    }

    private void ImportCustomMinimap()
    {
        var path = SC2PreviewImageImport.PickImageFile("Import Minimap Image");
        if (string.IsNullOrEmpty(path)) return;
        if (!SC2PreviewImageImport.TryImportMinimapPng(path, out var png, out var error))
        {
            _minimapPreviewStatus = "Import failed: " + error;
            return;
        }
        Data.CustomMinimapPng = png;
        EditorUtility.SetDirty(Data);
        if (_minimapPreviewTexture != null) UnityEngine.Object.DestroyImmediate(_minimapPreviewTexture);
        _minimapPreviewTexture = SC2MapPreviewCapture.DecodeCustomMinimap(Data);
        _minimapThumbSource = Data.CustomMinimapPng;
        _minimapPreviewStatus = "Custom minimap imported (fitted to 1024x1024, cropped to fill).";
        _terrainShell.MarkDirtyAndRepaint();
    }

    private void ImportCustomMapShot()
    {
        var path = SC2PreviewImageImport.PickImageFile("Import Preview Image");
        if (string.IsNullOrEmpty(path)) return;
        if (!SC2PreviewImageImport.TryImportMapShot(path, out var bgra, out var error))
        {
            _captureStatus = "Import failed: " + error;
            return;
        }
        Data.MapShotBgraPixels = bgra;
        EditorUtility.SetDirty(Data);
        _terrainShell.MarkDirtyAndRepaint();
        _captureStatus = "Custom preview imported (fitted to 448x206, cropped to fill).";
    }

    /// <summary>Rebuilds the on-screen thumbnail only when Data.
    /// MapShotBgraPixels actually changed since the last call — decoding a
    /// fresh Texture2D from 369KB of raw pixels every single OnGUI repaint
    /// would be wasteful for a value that only changes on an explicit
    /// Capture click.</summary>
    private void RefreshMapShotThumbnail()
    {
        var pixels = Data.MapShotBgraPixels;
        if (ReferenceEquals(pixels, _mapShotThumbnailSource)) return;
        _mapShotThumbnailSource = pixels;
        if (_mapShotThumbnail != null) { UnityEngine.Object.DestroyImmediate(_mapShotThumbnail); _mapShotThumbnail = null; }
        if (pixels == null || pixels.Length != SC2MapPreviewCapture.MapShotWidth * SC2MapPreviewCapture.MapShotHeight * 4) return;

        // RGBA32 rather than BGRA32 for the swizzled copy below — BGRA32
        // isn't guaranteed supported as a regular CPU-uploaded texture
        // format on every platform/graphics API (it's mainly a native
        // interop format), while RGBA32 always is.
        var rgba = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            rgba[i] = pixels[i + 2];     // R
            rgba[i + 1] = pixels[i + 1]; // G
            rgba[i + 2] = pixels[i];     // B
            rgba[i + 3] = pixels[i + 3]; // A
        }
        // Same vanilla frame the export stamps, so the preview matches it.
        SC2MapShotFrameMask.ApplyToBgra(rgba, SC2MapPreviewCapture.MapShotWidth, SC2MapPreviewCapture.MapShotHeight, bottomRowFirst: true);
        var tex = new Texture2D(SC2MapPreviewCapture.MapShotWidth, SC2MapPreviewCapture.MapShotHeight, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.LoadRawTextureData(rgba);
        tex.Apply(false, false);
        _mapShotThumbnail = tex;
    }

    private void DrawUndoRedoRow(float w)
    {
        if (_session == null) return;
        GUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        // Restores whatever GUI.enabled was BEFORE this row (not hardcode
        // true) — see SC2ObjectsShell.DrawUndoRedoRow's own comment for why.
        var guiEnabledBeforeUndoRow = GUI.enabled;
        GUI.enabled = guiEnabledBeforeUndoRow && _session.History.CanUndo;
        if (SC2EnvFieldGui.Button("↩ Undo")) Undo();
        GUI.enabled = guiEnabledBeforeUndoRow && _session.History.CanRedo;
        if (SC2EnvFieldGui.Button("↪ Redo")) Redo();
        GUI.enabled = guiEnabledBeforeUndoRow;
        GUILayout.EndHorizontal();
    }

    /// <summary>Mirrors SC2Collision2Shell.LoadFromNewData — syncs the
    /// document to whatever the freshly-loaded terrain already has, WITHOUT
    /// clearing Session.History itself (SC2TerrainCombinedUndo.Reset(),
    /// called from SC2TerrainShell.SetData(), already does that once at the
    /// actual load/create moment).
    ///
    /// This used to PUSH Data.MapName into
    /// KnownFields.Name unconditionally — for an imported map, that silently
    /// overwrote whatever real display name SC2ScenarioLegacyBridge had just
    /// parsed out of the actual scenario.lua with the unrelated technical
    /// map name, every single time the terrain loaded. Now it PULLS a real
    /// parsed name from the document into the live field instead, only
    /// falling back to MapName for a genuinely from-scratch map with no real
    /// name yet.</summary>
    private void LoadFromNewData()
    {
        if (_session?.Document != null)
        {
            var knownName = _session.Document.Scenario.KnownFields.Name;
            // A name already on the loaded data (restored from a save) wins over the session document's.
            Data.DisplayName = !string.IsNullOrEmpty(Data.DisplayName) ? Data.DisplayName
                : !string.IsNullOrEmpty(knownName) ? knownName
                : Data.MapName;
            _session.Document.Scenario.KnownFields.Name = Data.DisplayName;
        }
        _mapNameBeforeEdit = null;
        _mapNameFieldWasFocused = false;
        _displayNameBeforeEdit = null;
        _displayNameFieldWasFocused = false;
        _loadedForData = Data;
    }

    /// <summary>The technical file/folder identifier (see this field's own
    /// note on SC2TerrainData.MapName) — no longer touches the Document at
    /// all (it used to drive KnownFields.Name directly, which is now
    /// DisplayName's job below), so committing a change just needs the
    /// ordinary whole-terrain undo checkpoint, not a Document command.</summary>
    private void DrawMapNameField()
    {
        const string key = "scenario.mapname";
        var controlName = "SC2EnvText_" + key;
        var newValue = SC2EnvFieldGui.TextField("Map Name", Data.MapName, key);
        var focusedNow = GUI.GetNameOfFocusedControl() == controlName;

        if (focusedNow && !_mapNameFieldWasFocused)
            _mapNameBeforeEdit = Data.MapName;

        if (!focusedNow && _mapNameFieldWasFocused && _mapNameBeforeEdit != null && _mapNameBeforeEdit != newValue)
        {
            _terrainShell.RecordUndoCheckpoint("Change Map Name");
            _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        Data.MapName = newValue;
        _mapNameFieldWasFocused = focusedNow;
    }

    /// <summary>The real in-game display name (see SC2TerrainData.DisplayName's
    /// own note) — mirrors DrawMapNameField's old behavior exactly, just
    /// targeting DisplayName/KnownFields.Name instead of MapName.</summary>
    private void DrawDisplayNameField()
    {
        const string key = "scenario.displayname";
        var controlName = "SC2EnvText_" + key;
        var newValue = SC2EnvFieldGui.TextField("Display Name", Data.DisplayName, key);
        var focusedNow = GUI.GetNameOfFocusedControl() == controlName;

        if (focusedNow && !_displayNameFieldWasFocused)
            _displayNameBeforeEdit = Data.DisplayName;

        Data.DisplayName = newValue;

        if (!focusedNow && _displayNameFieldWasFocused && _displayNameBeforeEdit != null)
            CommitDisplayNameChange(_displayNameBeforeEdit);

        _displayNameFieldWasFocused = focusedNow;
    }

    private void CommitDisplayNameChange(string previousValue)
    {
        if (_session?.Document == null) return;
        if (previousValue == Data.DisplayName) return;
        _session.History.Execute(_session.Document, new SC2SetScenarioNameCommand(Data.DisplayName));
        _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Public so the shared undo dispatch can call it — see SC2EnvironmentShell.Undo's
    /// identical doc comment for why.</summary>
    public void Undo()
    {
        if (_session == null || !_session.History.CanUndo) return;
        _session.History.Undo(_session.Document);
        SyncFromDocument();
    }

    public void Redo()
    {
        if (_session == null || !_session.History.CanRedo) return;
        _session.History.Redo(_session.Document);
        SyncFromDocument();
    }

    /// <summary>Pulls DisplayName back from the document after an Undo/Redo,
    /// which mutated the document directly rather than through the text
    /// field — same reasoning as SC2EnvironmentShell.SyncFromDocument.
    /// MapName isn't Document-backed at all now (see DrawMapNameField's own
    /// note), so there's nothing to pull for it here.</summary>
    private void SyncFromDocument()
    {
        if (_session?.Document == null) return;
        Data.DisplayName = _session.Document.Scenario.KnownFields.Name;
        _displayNameBeforeEdit = null;
        _displayNameFieldWasFocused = false;
        RepaintHost?.Invoke();
    }
}
