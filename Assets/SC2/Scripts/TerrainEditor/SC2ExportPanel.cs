using UnityEditor;
using UnityEngine;

/// <summary>See SC2GeometryToolState's own doc comment — same reasoning,
/// folded into SC2TerrainShell.CaptureAuxState/ApplyAuxState so the export
/// folder/debug toggle are undo-able like everything else in this
/// shell.</summary>
public struct SC2ExportPanelState
{
    public string ExportDir;
    public bool DebugSaveCostsImages;
    public bool ExportAsArchives;
}

public sealed class SC2ExportPanel
{
    public SC2TerrainData Data;

    private readonly SC2TerrainShell _win;

    // Persisted across Editor restarts (PlayerPrefs — the same pattern every
    // other cross-session setting in this project uses, since EditorPrefs
    // has no Build equivalent). Without this, _exportDir was only ever an
    // in-memory field plus an undo-stack entry (SC2TerrainShell.CaptureAuxState/
    // ApplyAuxState) — real for Ctrl+Z within one session, but reset to the
    // hardcoded default the moment the Editor process restarted, discarding
    // whatever folder the user had actually picked.
    private const string ExportDirPrefKey = "SC2.ExportDir";
#if UNITY_EDITOR
    private const string DefaultExportDir = "Assets/Exports";
#else
    // "Assets/" only exists inside a Unity project. In a standalone Build & Run
    // player there is no Assets folder, so default to a location that is always
    // writable next to (or under) the built game instead.
    private static readonly string DefaultExportDir = System.IO.Path.Combine(Application.persistentDataPath, "Exports");
#endif
    private string _exportDir = PlayerPrefs.GetString(ExportDirPrefKey, DefaultExportDir);

    private void SetExportDir(string dir)
    {
        _exportDir = dir;
        PlayerPrefs.SetString(ExportDirPrefKey, _exportDir ?? "");
        PlayerPrefs.Save();
    }

    // EditorUtility.DisplayDialog (which SC2TerrainExporter otherwise uses
    // for every success/error message) is a silent no-op in a standalone
    // build, so an export that failed used to look identical to nothing
    // happening at all there — no file, no error, no feedback whatsoever.
    // SC2TerrainExporter.ExportCorrect now also returns the outcome as a
    // string; shown here as a persistent note so it's visible in both the
    // Editor and a build.
    private string _lastExportResult;

    // Debug-only: dumps PNGs of exactly what gets written into costs.bdf
    // (navmesh + islandmap per block, terrain types) into a CostsDebug/
    // subfolder next to the exported .bdf files — see SC2CostsDebugImageWriter.
    private bool _debugSaveCostsImages;

    // Export .scd by type: when off (default), each real
    // vanilla archive "type" (maps.scd/lua.scd/uncompiled_lua.scd/
    // textures.scd — see SC2TerrainExporter.CreateExportStagingDir's doc
    // comment) is written as a loose folder of that same name. When on,
    // each type is packed into a real .scd zip archive of that name
    // instead, matching how the real game actually ships them.
    private bool _exportAsArchives;

    private GUIStyle _exportDirAreaStyle;
    private GUIStyle ExportDirAreaStyle => _exportDirAreaStyle ??= new GUIStyle(EditorStyles.textArea)
    {
        wordWrap = true,
        fixedHeight = 0f,
    };

    public SC2ExportPanel(SC2TerrainShell win, SC2TerrainData data)
    {
        _win = win;
        Data = data;
    }

    public SC2ExportPanelState CaptureState() => new SC2ExportPanelState
    {
        ExportDir = _exportDir,
        DebugSaveCostsImages = _debugSaveCostsImages,
        ExportAsArchives = _exportAsArchives,
    };

    public void ApplyState(SC2ExportPanelState state)
    {
        SetExportDir(state.ExportDir);
        _debugSaveCostsImages = state.DebugSaveCostsImages;
        _exportAsArchives = state.ExportAsArchives;
    }

    public void DrawPanel()
    {
        // Hard outer container: ensures every child (pair boxes, sliders,
        // text fields) is constrained to the available panel width even in
        // build & run where EditorGUIUtility.currentViewWidth can return
        // Screen.width instead of the panel column width.
        var w = SC2TerrainShell.GetPanelContentWidth();
        EditorGUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));
        DrawPanelContent(w);
        EditorGUILayout.EndVertical();
    }

    private void DrawPanelContent(float w)
    {
        EditorGUILayout.LabelField("Export terrain.bdf", EditorStyles.boldLabel, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (_win.IsPairCpuDataStale)
            SC2TerrainShell.DrawUnifiedNote("CPU pair data is pending GPU readback. Export will force sync before building geometry.");

        // Map Name is edited on Scenario → Info now (SC2ScenarioShell) — shown
        // here read-only so the export summary is still self-contained
        // without a second editable copy of the same SC2TerrainData.MapName.
        EditorGUILayout.LabelField($"Map Name: {Data.MapName}  (set on Scenario → Info)", GUILayout.Width(w), GUILayout.MaxWidth(w));
        // EditorGUILayout.ObjectField's label-less overload is now backed by
        // SC2RuntimeObjectPicker in a build too (see UnityEditorRuntimeShim),
        // so this no longer needs a separate Editor-only vs. build-only
        // branch — the old #else fallback just showed a permanently
        // read-only "None"/name label with no way to pick a different
        // SC2MapRawData asset in Build & Run.
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        GUILayout.Label("RawData", GUILayout.Width(SC2UiScale.Pxf(54f)));
        var nextRawData = (SC2MapRawData)EditorGUILayout.ObjectField(Data.RawData, typeof(SC2MapRawData), false, GUILayout.Width(Mathf.Max(1f, w - 58f)), GUILayout.MaxWidth(Mathf.Max(1f, w - 58f)));
        if (nextRawData != Data.RawData)
        {
            _win.RecordUndoCheckpoint("Change RawData");
            Data.RawData = nextRawData;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        GUILayout.Label("Output", GUILayout.Width(SC2UiScale.Pxf(54f)));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Choose Folder...", GUILayout.Width(SC2UiScale.Pxf(120f))))
        {
#if UNITY_EDITOR
            var d = EditorUtility.OpenFolderPanel("Export Folder", "Assets", "");
            if (!string.IsNullOrEmpty(d))
            {
                _win.RecordUndoCheckpoint("Change Export Folder");
                SetExportDir("Assets" + d[Application.dataPath.Length..]);
            }
#else
            // EditorUtility.OpenFolderPanel is an Editor-only dialog and does
            // nothing in a standalone player — SC2RuntimeFileDialog.OpenFolder
            // is the native Windows equivalent. Unlike the Editor branch
            // above, the result is used as-is (an absolute filesystem path):
            // standalone exports write directly to disk via File.IO, not
            // through AssetDatabase, so there's no "Assets/"-relative path to
            // convert to.
            var d = SC2RuntimeFileDialog.OpenFolder("Export Folder");
            if (!string.IsNullOrEmpty(d))
            {
                _win.RecordUndoCheckpoint("Change Export Folder");
                SetExportDir(d);
            }
#endif
        }
        EditorGUILayout.EndHorizontal();

        // A single-line TextField clipped long paths with no way to read the
        // rest short of clicking in and scrolling character-by-character — a
        // wrapping, full-width TextArea instead shows the entire path,
        // wrapping mid-path when necessary (Unity's own text layout already
        // falls back to a character break for one "word" wider than the
        // line, which is exactly the common case for an unbroken path).
        GUI.SetNextControlName("SC2_ExportOutputFolder");
        var nextExportDir = EditorGUILayout.TextArea(_exportDir ?? "", ExportDirAreaStyle, GUILayout.Width(w), GUILayout.MaxWidth(w), GUILayout.MinHeight(SC2UiScale.Pxf(20f)));
        if (nextExportDir != _exportDir)
        {
            _win.RecordUndoCheckpoint("Change Export Folder");
            SetExportDir(nextExportDir);
        }

        SC2TerrainShell.DrawUnifiedNote(
            "Output: one folder per map, named \"<Map Name>.scd\" (matching a real\n" +
            "game install's own per-map .scd, e.g. gamedata/eclipse.scd):\n" +
            "  <mapName>.scd/maps/<mapName>/<mapName>.terrain.win.bdf\n" +
            "  <mapName>.scd/maps/<mapName>/<mapName>.hfield.win.bdf (projected from\n" +
            "  the same simplified+merged mesh written to terrain.bdf above)\n" +
            "  <mapName>.scd/maps/<mapName>/<mapName>.costs.win.bdf (from Costs tab\n" +
            "  painting, or an all-passable default if nothing was painted)\n" +
            "  <mapName>.scd/maps/<mapName>/lighting/<PairName>_0/1/2.win.dds\n" +
            "  <mapName>.scd/maps/<mapName>/<mapName>_save/_scenario/_script.lua\n" +
            "  <mapName>.scd/uncompiled/maps/<mapName>/... (same 3 files, plain text)\n" +
            "  <mapName>.scd/Textures/<Name>.dds\n\n" +
            "Textures must be real DDS files (DXT1/DXT5).\n\n" +
            "With \"Export as .scd archive\" on, the zip file goes into an Archives/\n" +
            "subfolder instead (Archives/<mapName>.scd) — kept separate from the loose\n" +
            "folder path above so a folder export and an archive export of the same\n" +
            "map never collide on the same name.");

        var nextExportAsArchives = EditorGUILayout.ToggleLeft(
            "Export as .scd archive (zip the per-map folder into Archives/<Map Name>.scd instead of writing a loose folder)",
            _exportAsArchives, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (nextExportAsArchives != _exportAsArchives)
        {
            _win.RecordUndoCheckpoint("Toggle Export As Archives");
            _exportAsArchives = nextExportAsArchives;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Simplification (per pair):", EditorStyles.boldLabel, GUILayout.Width(w), GUILayout.MaxWidth(w));
        for (var i = 0; i < Data.Pairs.Count; i++)
        {
            var p = Data.Pairs[i];
            SC2TerrainShell.BeginUnifiedPanelBox();
            var col = Data.PairColor(i);
            EditorGUILayout.BeginHorizontal(GUILayout.Width(Mathf.Max(1f, w - 20f)), GUILayout.MaxWidth(Mathf.Max(1f, w - 20f)));
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
            EditorGUI.DrawRect(swatch, col);
            EditorGUILayout.LabelField(PairTriStr(Data, i), GUILayout.Width(SC2UiScale.Pxf(38f)));
            var nameStyle = new GUIStyle(EditorStyles.miniBoldLabel) { clipping = TextClipping.Clip };
            EditorGUILayout.LabelField(p.Name, nameStyle, GUILayout.Width(Mathf.Max(32f, w - 90f)), GUILayout.MaxWidth(Mathf.Max(32f, w - 90f)));
            EditorGUILayout.EndHorizontal();
            var next = SC2TerrainShell.DrawCompactSlider("Simplify", p.SimplifyRatio, 0f, 1f, $"export-{i}",
                onValueChangeStart: () => _win.RecordUndoCheckpoint("Simplify Ratio"));
            if (!Mathf.Approximately(next, p.SimplifyRatio))
            {
                p.SimplifyRatio = next;
                _win.MarkDirtyAndRepaint();
            }
            SC2TerrainShell.EndUnifiedPanelBox();
        }

        var importedTerrains = _win.GetImportedTerrainsForExport?.Invoke();
        if (importedTerrains != null && importedTerrains.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Imported objects ({importedTerrains.Count}):", EditorStyles.boldLabel, GUILayout.Width(w), GUILayout.MaxWidth(w));
            SC2TerrainShell.BeginUnifiedPanelBox();
            foreach (var imported in importedTerrains)
            {
                var nameStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };
                EditorGUILayout.LabelField(imported.Name, nameStyle, GUILayout.Width(w - 16f), GUILayout.MaxWidth(w - 16f));
            }
            SC2TerrainShell.EndUnifiedPanelBox();
            SC2TerrainShell.DrawUnifiedNote("Merged into the same terrain.bdf, at their current position/rotation/scale.");
        }

        EditorGUILayout.Space(8);
        var nextDebugSaveCostsImages = EditorGUILayout.ToggleLeft(
            "Debug: save costs PNGs on export (navmesh + islandmap per block, terrain types)",
            _debugSaveCostsImages, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (nextDebugSaveCostsImages != _debugSaveCostsImages)
        {
            _win.RecordUndoCheckpoint("Toggle Debug Costs Images");
            _debugSaveCostsImages = nextDebugSaveCostsImages;
        }

        EditorGUILayout.Space(8);
        SC2TerrainShell.DrawUnifiedNote("Minimap renders fresh into minimap.win.dds below on every Export — nothing to do here. To review the minimap framing or capture the map-select preview screenshot by hand, see Scenario → Preview.");

        EditorGUILayout.Space(4);
        var btnStyle = new GUIStyle(GUI.skin.button){ fontSize=14, fontStyle=FontStyle.Bold };
        var exportPending = SC2MapPreviewCapture.IsCapturePending;
        var guiEnabledBeforeExport = GUI.enabled;
        GUI.enabled = guiEnabledBeforeExport && !exportPending;
        if (GUILayout.Button(exportPending ? "Rendering minimap..." : "Export", btnStyle, GUILayout.Height(SC2UiScale.Pxf(38f))))
            RunExport();
        GUI.enabled = guiEnabledBeforeExport;

        // An additive path sitting beside Export above,
        // never replacing it — routes through SC2SmartExportPlanner/
        // SC2SmartExportWriter instead of DoExport's direct File.WriteAllBytes
        // sequence, sharing the exact same byte-generation code (see
        // SC2TerrainExporter.BuildExportArtifacts) so both buttons' output is
        // byte-identical on the same data. Everything is still marked dirty
        // here (real per-domain dirty tracking) so this button
        // regenerates every file every time too, same as Export above.
        var smartBtnStyle = new GUIStyle(GUI.skin.button){ fontSize=11 };
        if (GUILayout.Button("Smart Export (new)", smartBtnStyle, GUILayout.Height(SC2UiScale.Pxf(22f))))
        {
            _win.SaveTerrainState(writeAssets: false);
            System.IO.Directory.CreateDirectory(_exportDir);
            _lastExportResult = SC2TerrainExporter.ExportCorrectSmart(Data, _exportDir, importedTerrains, _win.Session, _exportAsArchives);
        }

        if (!string.IsNullOrEmpty(_lastExportResult))
        {
            EditorGUILayout.Space(4);
            SC2TerrainShell.DrawUnifiedNote(_lastExportResult);
        }
    }

    /// <summary>The "Export" button's action, also run by the Global.Export hotkey: saves the terrain state,
    /// renders the minimap and writes the map to the chosen output folder. Does nothing while a minimap
    /// capture from an earlier export is still pending. <paramref name="onFinished"/> receives the result
    /// text (the same one the panel shows), so a caller without the panel on screen can report it.</summary>
    public bool RunExport(System.Action<string> onFinished = null)
    {
        if (SC2MapPreviewCapture.IsCapturePending) return false;
        _win.SaveTerrainState(writeAssets: false);
        System.IO.Directory.CreateDirectory(_exportDir);
        _lastExportResult = "Rendering minimap...";
        var data = Data;
        var exportDir = _exportDir;
        var debugSaveCostsImages = _debugSaveCostsImages;
        var session = _win.Session;
        var exportAsArchives = _exportAsArchives;
        var importedTerrains = _win.GetImportedTerrainsForExport?.Invoke();
        // Minimap must be rendered fresh (see SC2MapPreviewCapture's own
        // doc comment for why this can't be a synchronous call right
        // here) before the real, otherwise-unchanged ExportCorrect call
        // below — everything captured into this closure is a plain
        // local, unaffected by DrawPanelContent running again next frame.
        SC2MapPreviewCapture.RequestMinimapCapture(data, tex =>
        {
            byte[] minimapDds = null;
            if (tex != null)
            {
                minimapDds = SC2DdsEncoder.BuildDxt1SingleMip(SC2MapPreviewCapture.MinimapSize, SC2MapPreviewCapture.MinimapSize, SC2MapPreviewCapture.ToRgbBytes(tex));
                UnityEngine.Object.DestroyImmediate(tex);
            }
            _lastExportResult = SC2TerrainExporter.ExportCorrect(data, exportDir, importedTerrains, debugSaveCostsImages, session, exportAsArchives, minimapDds);
            onFinished?.Invoke(_lastExportResult);
        }, _win);
        return true;
    }

    private static string PairTriStr(SC2TerrainData data, int pairIdx)
    {
        if (data.PairTriCounts != null && pairIdx < data.PairTriCounts.Length)
            return $"{data.PairTriCounts[pairIdx]}t";
        if (data.TrianglePairIndex == null || data.TrianglePairIndex.Length == 0) return "?t";
        return $"{CountPairTris(data.TrianglePairIndex, pairIdx)}t";
    }

    private static int CountPairTris(short[] idx, int pairIdx)
    {
        var n = 0;
        foreach (var v in idx) if (v == pairIdx) n++;
        return n;
    }
}
