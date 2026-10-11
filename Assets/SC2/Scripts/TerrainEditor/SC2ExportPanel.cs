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
    private GUISkin _exportDirAreaSkin;
    private GUIStyle ExportDirAreaStyle
    {
        get
        {
            // A copy of the skin's text area, so it is taken again when the palette changes and the skin is rebuilt.
            if (_exportDirAreaStyle == null || _exportDirAreaSkin != GUI.skin)
            {
                _exportDirAreaSkin = GUI.skin;
                _exportDirAreaStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true, fixedHeight = 0f };
            }
            return _exportDirAreaStyle;
        }
    }

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

    private bool _simplificationExpanded = true;
    private bool _backgroundSimplificationExpanded = true;
    private bool _advancedExpanded;

    private void DrawPanelContent(float w)
    {
        SC2TerrainShell.DrawUnifiedSectionLabel("Export");
        if (_win.IsPairCpuDataStale)
            SC2TerrainShell.DrawUnifiedNote("The terrain is still being read back from the graphics card. Export waits for it before building the map.");

        // Map Name is edited on Scenario → Info (SC2ScenarioShell) — shown here read-only so the export summary is still
        // self-contained without a second editable copy of the same SC2TerrainData.MapName.
        SC2TerrainShell.DrawUnifiedNote($"Map: {Data.MapName}. The name is set on Scenario > Info.");
        EditorGUILayout.Space(SC2UiScale.Pxf(6f));

        DrawOutputSection(w);
        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        DrawExportButtons(w);
        EditorGUILayout.Space(SC2UiScale.Pxf(10f));

        _simplificationExpanded = SC2TerrainShell.DrawFoldoutHeader("Simplification (per pair)", _simplificationExpanded);
        if (_simplificationExpanded)
            DrawPairSimplification();

        // The generated background is simplified per pair too: 1.0 is its export budget (the most it can be), lower values go
        // lighter. The same slider as on the Background, Pairs and Simplified tabs.
        var background = _win.ProceduralBackgroundData;
        if (background != null && background.Pairs.Count > 0)
        {
            EditorGUILayout.Space(SC2UiScale.Pxf(6f));
            _backgroundSimplificationExpanded = SC2TerrainShell.DrawFoldoutHeader("Background simplification (per pair)", _backgroundSimplificationExpanded);
            if (_backgroundSimplificationExpanded)
                DrawBackgroundSimplification(background);
        }

        var importedTerrains = _win.GetImportedTerrainsForExport?.Invoke();
        if (importedTerrains != null && importedTerrains.Count > 0)
        {
            EditorGUILayout.Space(SC2UiScale.Pxf(10f));
            SC2TerrainShell.DrawUnifiedSectionLabel($"Imported objects ({importedTerrains.Count})");
            SC2TerrainShell.BeginUnifiedPanelBox();
            foreach (var imported in importedTerrains)
            {
                var nameStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };
                GUILayout.Label(imported.Name, nameStyle, GUILayout.ExpandWidth(true));
            }
            SC2TerrainShell.EndUnifiedPanelBox();
            SC2TerrainShell.DrawUnifiedNote("Merged into the same terrain.bdf at their current position, rotation and scale.");
        }

        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        _advancedExpanded = SC2TerrainShell.DrawFoldoutHeader("Advanced", _advancedExpanded);
        if (_advancedExpanded)
            DrawAdvanced(w);

        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        SC2TerrainShell.DrawUnifiedNote("The minimap is rendered fresh into minimap.win.dds on every export. To review its framing or capture the map-select screenshot, see Scenario > Preview.");
    }

    private void DrawOutputSection(float w)
    {
        SC2TerrainShell.DrawUnifiedSectionLabel("Output folder");
        // A wrapping, full-width TextArea instead of a single-line TextField: a long path is shown whole, wrapping mid-path when
        // it has to (Unity's text layout falls back to a character break for one "word" wider than the line, which is exactly
        // the common case for an unbroken path).
        GUI.SetNextControlName("SC2_ExportOutputFolder");
        var nextExportDir = EditorGUILayout.TextArea(_exportDir ?? "", ExportDirAreaStyle, GUILayout.Width(w), GUILayout.MaxWidth(w), GUILayout.MinHeight(SC2UiScale.Pxf(20f)));
        if (nextExportDir != _exportDir)
        {
            _win.RecordUndoCheckpoint("Change Export Folder");
            SetExportDir(nextExportDir);
        }
        GUILayout.Space(SC2UiScale.Pxf(6f));
        if (GUILayout.Button("Choose folder...", GUILayout.Height(SC2UiScale.Pxf(26f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
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
        EditorGUILayout.Space(SC2UiScale.Pxf(6f));

        var nextExportAsArchives = EditorGUILayout.ToggleLeft(" Export as .scd archive", _exportAsArchives, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (nextExportAsArchives != _exportAsArchives)
        {
            _win.RecordUndoCheckpoint("Toggle Export As Archives");
            _exportAsArchives = nextExportAsArchives;
        }
        SC2TerrainShell.DrawUnifiedNote("Zips the map folder into Archives/<Map Name>.scd instead of writing a loose folder. The two never collide, even for the same map name.");
    }

    private void DrawExportButtons(float w)
    {
        var btnStyle = new GUIStyle(GUI.skin.button) { fontSize = SC2Type.Nav, fontStyle = FontStyle.Bold };
        var exportPending = SC2MapPreviewCapture.IsCapturePending;
        var guiEnabledBeforeExport = GUI.enabled;
        GUI.enabled = guiEnabledBeforeExport && !exportPending;
        if (SC2EnvFieldGui.PrimaryButton(exportPending ? "Rendering minimap..." : "Export", btnStyle, GUILayout.Height(SC2UiScale.Pxf(38f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
            RunExport();
        GUI.enabled = guiEnabledBeforeExport;

        if (!string.IsNullOrEmpty(_lastExportResult))
            SC2TerrainShell.DrawUnifiedNote(_lastExportResult);
    }

    /// <summary>One card per pair: swatch, name and the triangles before and after simplification, then its Simplify slider.</summary>
    private void DrawPairSimplification()
    {
        var counts = _win.LastSimplifiedCounts;
        if (GUILayout.Button(counts == null ? "Count triangles" : "Update triangle counts", GUILayout.Height(SC2UiScale.Pxf(26f))))
            _win.RequestSimplifiedCounts();
        SC2TerrainShell.DrawUnifiedNote(counts == null
            ? "Each card shows the pair's triangles before simplification. Count triangles runs the simplification and shows what is left."
            : counts.Stale
                ? "* Counted before the latest change. Update triangle counts to refresh them."
                : "Triangles before and after simplification, as of the last count.");

        for (var i = 0; i < Data.Pairs.Count; i++)
        {
            var pair = Data.Pairs[i];
            var source = PairTriangleCount(Data, i);
            var right = counts != null && i < counts.CoreBuilt.Length
                ? SC2TerrainShell.TrianglesBeforeAfter(counts.CoreSource[i], counts.CoreBuilt[i], counts.Stale)
                : SC2TerrainShell.CompactCount(source) + " tris";
            SC2TerrainShell.DrawPairCardHeader(Data.PairColor(i), pair.Name, right);
            var next = SC2TerrainShell.DrawCompactSlider("Simplify", pair.SimplifyRatio, 0f, 1f, $"export-{i}",
                onValueChangeStart: () => _win.RecordUndoCheckpoint("Simplify Ratio"));
            if (!Mathf.Approximately(next, pair.SimplifyRatio))
            {
                pair.SimplifyRatio = next;
                _win.MarkDirtyAndRepaint();
            }
            SC2TerrainShell.EndUnifiedPanelBox();
        }
    }

    private void DrawBackgroundSimplification(SC2TerrainData background)
    {
        SC2TerrainShell.DrawUnifiedNote($"1.0 = {SC2BackgroundTerrain.ExportTriangleBudget:N0} triangles for the whole background, shared between its pairs by size. Lower is lighter, it cannot go above. Exports as about {SC2BackgroundTerrain.EstimateExportTriangles(background):N0}.");
        var counts = _win.LastSimplifiedCounts;
        for (var i = 0; i < background.Pairs.Count; i++)
        {
            var pair = background.Pairs[i];
            var right = counts != null && i < counts.BackgroundBuilt.Length
                ? SC2TerrainShell.TrianglesBeforeAfter(counts.BackgroundSource[i], counts.BackgroundBuilt[i], counts.Stale)
                : SC2TerrainShell.CompactCount(SC2BackgroundTerrain.PairSourceTriangles(background, i)) + " tris";
            SC2TerrainShell.DrawPairCardHeader(background.PairColor(i), pair.Name, right);
            _win.DrawBackgroundDetailSlider(background, pair, $"export-bg-{i}");
            SC2TerrainShell.EndUnifiedPanelBox();
        }
    }

    /// <summary>Rarely needed: the costs debug dump.</summary>
    private void DrawAdvanced(float w)
    {
        var nextDebugSaveCostsImages = EditorGUILayout.ToggleLeft(" Save costs PNGs on export", _debugSaveCostsImages, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (nextDebugSaveCostsImages != _debugSaveCostsImages)
        {
            _win.RecordUndoCheckpoint("Toggle Debug Costs Images");
            _debugSaveCostsImages = nextDebugSaveCostsImages;
        }
        SC2TerrainShell.DrawUnifiedNote("Debug: writes the navmesh and island map of every block and the terrain types as PNGs into a CostsDebug folder next to the .bdf files.");
    }

    /// <summary>The "Export" button's action, also run by the Global.Export hotkey: saves the terrain state,
    /// renders the minimap and writes the map to the chosen output folder. Does nothing while a minimap
    /// capture from an earlier export is still pending or another long job owns the busy plate.
    /// <paramref name="onFinished"/> receives the result text (the same one the panel shows), so a caller
    /// without the panel on screen can report it.</summary>
    public bool RunExport(System.Action<string> onFinished = null)
    {
        if (SC2MapPreviewCapture.IsCapturePending || SC2BusyOverlay.IsActive) return false;
        // The export runs on a worker thread behind the busy plate (see SC2BusyOverlay): the first function below runs on the main
        // thread, the one it returns on the worker, and the one that returns on the main thread again.
        return SC2BusyOverlay.RunInBackground("Exporting map", ExportDetail, () =>
        {
            _win.SaveTerrainState(writeAssets: false);
            System.IO.Directory.CreateDirectory(_exportDir);
            _lastExportResult = "Exporting...";
            var data = Data;
            var exportDir = _exportDir;
            var debugSaveCostsImages = _debugSaveCostsImages;
            var session = _win.Session;
            var exportAsArchives = _exportAsArchives;
            var importedTerrains = _win.GetImportedTerrainsForExport?.Invoke();
            return () =>
            {
                var minimapDds = RenderMinimapDds(data);
                var result = SC2TerrainExporter.ExportCorrect(data, exportDir, importedTerrains, debugSaveCostsImages, session, exportAsArchives, minimapDds);
                return () =>
                {
                    _lastExportResult = result;
                    onFinished?.Invoke(result);
                };
            };
        }, ex =>
        {
            _lastExportResult = "Export failed: " + SC2TerrainExporter.DescribeFailure(ex);
            onFinished?.Invoke(_lastExportResult);
        });
    }

    /// <summary>The minimap, rendered fresh for the export (see SC2MapPreviewCapture's own doc comment for why it cannot be a
    /// synchronous call from a button), encoded as DDS. Runs on the export's worker thread: the render and the pixel read happen on
    /// the main thread, a few frames later; the encoding stays on the worker. Null when no minimap could be rendered.</summary>
    private byte[] RenderMinimapDds(SC2TerrainData data)
    {
        SC2BusyOverlay.Report("Rendering the minimap");
        byte[] rgb = null;
        SC2BusyOverlay.OnMainAsync(done => SC2MapPreviewCapture.RequestMinimapCapture(data, tex =>
        {
            try
            {
                if (tex == null) return;
                rgb = SC2MapPreviewCapture.ToRgbBytes(tex);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally { done(); }
        }, _win), MinimapWaitMilliseconds);
        if (rgb == null) return null;
        SC2BusyOverlay.Report("Encoding the minimap");
        return SC2DdsEncoder.BuildDxt1SingleMip(SC2MapPreviewCapture.MinimapSize, SC2MapPreviewCapture.MinimapSize, rgb);
    }

    private const int MinimapWaitMilliseconds = 30_000;

    private const string ExportDetail = "Rendering the minimap and writing the map files. This can take a while.";

    private static int PairTriangleCount(SC2TerrainData data, int pairIdx)
    {
        if (data.PairTriCounts != null && pairIdx < data.PairTriCounts.Length)
            return data.PairTriCounts[pairIdx];
        if (data.TrianglePairIndex == null || data.TrianglePairIndex.Length == 0) return 0;
        return CountPairTris(data.TrianglePairIndex, pairIdx);
    }

    private static int CountPairTris(short[] idx, int pairIdx)
    {
        var n = 0;
        foreach (var v in idx) if (v == pairIdx) n++;
        return n;
    }
}
