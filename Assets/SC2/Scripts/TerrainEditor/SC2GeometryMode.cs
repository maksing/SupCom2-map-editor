using UnityEngine;
using UnityEditor;
using System.IO;
using SC2.MapEditor.SmartExport;

/// <summary>Brush tool config outside SC2TerrainData (active tool + its
/// sliders) — captured/restored via SC2GeometryMode.CaptureState/ApplyState
/// so SC2TerrainShell can fold it into SC2TerrainUndoStack.CaptureExtra/
/// ApplyExtra alongside the terrain data snapshot itself.</summary>
public struct SC2GeometryToolState
{
    public int Tool;
    public float Radius, Strength, Hardness, FlattenH, SetHeight;
}

public class SC2GeometryMode
{
    public SC2TerrainData Data;
    private SC2TerrainShell _win;

    // Mode 5 = SetHeight (draws exact height), rest match SculptBrush modes
    private enum Tool { Raise=0, Lower=1, Smooth=2, Flatten=3, Noise=4, SetHeight=5 }
    private Tool  _tool      = Tool.Raise;
    private float _radius    = 20f;
    private float _strength  = 0.5f;
    private float _hardness  = 0.5f; // 0=soft cosine, 1=hard step
    private float _flattenH  = SC2TerrainData.DefaultFlatHeight;
    private float _setHeight = SC2TerrainData.DefaultFlatHeight;  // target height for SetHeight tool

    private Vector3 _brushPos;
    private bool    _brushVis, _isPainting;
    private double  _lastApply;
    private const double kMinInterval = 0.016;

    // F interactive resize, Blender-style — see
    // SC2BrushRadiusGesture's own doc comment.
    private readonly SC2BrushRadiusGesture _radiusGesture = new();

    private float     _importScale = 200f;

    // Optional custom stamp brush for sculpting — mirrors
    // SC2PaintMode's own _customBrushCache/_customBrushCachedPath pair
    // (resolved-texture cache, re-resolved only when Data.
    // CustomGeometryBrushTexturePath actually changes underneath it) plus a
    // separate readable-pixel cache SculptBrush/SculptSetHeight need for
    // their CPU-side sampling (SC2TerrainRuntime.SampleBrushMask) — GPU
    // Texture2D content isn't otherwise accessible per-vertex without a
    // GetPixels32() readback, which is cached here and only rebuilt when the
    // underlying Texture2D instance changes.
    private Texture   _customGeoBrushCache;
    private string    _customGeoBrushCachedPath = "\0";
    private Color32[] _geoBrushPixels;
    private int       _geoBrushPixelsW, _geoBrushPixelsH;
    private Texture   _geoBrushPixelsSourceTex;
    // Rolled once per stroke (mouse-down) in HandleInput when Data.
    // RandomizeGeometryBrushRotation is on, held fixed for the whole drag —
    // same reasoning as SC2PaintMode._rnmBrushRotationRad.
    private float     _geoBrushRotationRad;

    private float _heightScaleFactor = 1f;
    private float _heightRaiseOffset;

    public SC2GeometryMode(SC2TerrainShell win, SC2TerrainData data)
    { _win = win; Data = data; }

    /// <summary>See SC2GeometryToolState's own doc comment.</summary>
    public SC2GeometryToolState CaptureState() => new SC2GeometryToolState
    {
        Tool = (int)_tool,
        Radius = _radius,
        Strength = _strength,
        Hardness = _hardness,
        FlattenH = _flattenH,
        SetHeight = _setHeight,
    };

    public void ApplyState(SC2GeometryToolState state)
    {
        _tool = (Tool)state.Tool;
        _radius = state.Radius;
        _strength = state.Strength;
        _hardness = state.Hardness;
        _flattenH = state.FlattenH;
        _setHeight = state.SetHeight;
    }

    // ── Panel ─────────────────────────────────────────────────────────────────

    public void DrawPanel()
    {
        var toolLabels = new[] { "Raise", "Lower", "Smooth", "Flatten", "Set H", "Noise" };
        var toolValues = new[] { Tool.Raise, Tool.Lower, Tool.Smooth, Tool.Flatten, Tool.SetHeight, Tool.Noise };
        var selected = System.Array.IndexOf(toolValues, _tool);
        selected = Mathf.Max(0, SC2TerrainShell.DrawUnifiedSegmentGrid(selected, toolLabels, 3));
        var newTool = toolValues[selected];
        if (newTool != _tool)
        {
            _win.RecordUndoCheckpoint("Change Brush Tool");
            _tool = newTool;
        }

        EditorGUILayout.Space(4);
        _radius   = SC2TerrainShell.DrawCompactSlider("Radius",   _radius,   1f, 1000f,
            onValueChangeStart: () => _win.RecordUndoCheckpoint("Change Brush Radius"));
        _strength = SC2TerrainShell.DrawCompactSlider("Strength", _strength, 0.01f, 1f,
            onValueChangeStart: () => _win.RecordUndoCheckpoint("Change Brush Strength"));
        _hardness = SC2TerrainShell.DrawCompactSlider("Hardness", _hardness, 0f, 1f,
            onValueChangeStart: () => _win.RecordUndoCheckpoint("Change Brush Hardness"));

        // Irregular stamp brushes (World
        // Machine/Gaea-style grayscale masks) for sculpting, mirroring
        // Paint's existing Blend-brush stamp feature (SC2PaintMode.DrawRnm/
        // DrawCustomBrushPicker) — see SC2TerrainRuntime.SculptBrush/
        // SculptSetHeight's brushMask params for where this actually applies.
        EditorGUILayout.Space(4);
        var brushShapeLabels = new[] { "Round", "Custom" };
        var nextCustomBrush = SC2TerrainShell.DrawUnifiedSegmentGrid(Data.UseCustomGeometryBrush ? 1 : 0, brushShapeLabels, 2) == 1;
        if (nextCustomBrush != Data.UseCustomGeometryBrush)
        {
            _win.RecordUndoCheckpoint("Change Geometry Brush Shape");
            Data.UseCustomGeometryBrush = nextCustomBrush;
            EditorUtility.SetDirty(Data);
        }
        if (Data.UseCustomGeometryBrush)
        {
            DrawCustomGeometryBrushPicker();
            var nextRandomize = EditorGUILayout.Toggle("Randomize Rotation", Data.RandomizeGeometryBrushRotation);
            if (nextRandomize != Data.RandomizeGeometryBrushRotation)
            {
                _win.RecordUndoCheckpoint("Change Geometry Brush Randomize Rotation");
                Data.RandomizeGeometryBrushRotation = nextRandomize;
                EditorUtility.SetDirty(Data);
            }
            if (Data.RandomizeGeometryBrushRotation)
            {
                var nextContinuous = EditorGUILayout.Toggle("Randomize While Dragging", Data.RandomizeGeometryBrushRotationContinuous);
                if (nextContinuous != Data.RandomizeGeometryBrushRotationContinuous)
                {
                    _win.RecordUndoCheckpoint("Change Geometry Brush Randomize Continuous");
                    Data.RandomizeGeometryBrushRotationContinuous = nextContinuous;
                    EditorUtility.SetDirty(Data);
                }
            }
        }

        if (_tool == Tool.Flatten || _tool == Tool.SetHeight)
        {
            var label = _tool == Tool.SetHeight ? "Height" : "Flatten H";
            if (_tool == Tool.SetHeight)
                _setHeight = SC2TerrainShell.DrawCompactSlider(label, _setHeight, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight,
                    onValueChangeStart: () => _win.RecordUndoCheckpoint("Change Set Height"));
            else
                _flattenH = SC2TerrainShell.DrawCompactSlider(label, _flattenH, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight,
                    onValueChangeStart: () => _win.RecordUndoCheckpoint("Change Flatten Height"));
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Pick", GUILayout.Width(SC2UiScale.Pxf(40f))))
            {
                _win.RecordUndoCheckpoint("Pick Height");
                if (_tool == Tool.SetHeight) _setHeight = SC2TerrainData.ClampHeight(_brushPos.y);
                else _flattenH = SC2TerrainData.ClampHeight(_brushPos.y);
            }
            EditorGUILayout.EndHorizontal();

        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Height Adjustments", EditorStyles.boldLabel);
        // Whole-map operations, distinct from the brush tools above (which
        // only ever touch the area under the cursor) - each rewrites every
        // vertex in Data.Heights in one shot via SC2TerrainRuntime.
        // UploadHeightsCPU (the same bulk-write primitive ImportHeightmap
        // already uses), then syncs Data.Heights back from the runtime the
        // same way every other mutation here does.
        _heightScaleFactor = SC2TerrainShell.DrawCompactSlider("Scale Factor", _heightScaleFactor, 0.1f, 5f);
        EditorGUI.BeginDisabledGroup(_win.Runtime == null);
        if (GUILayout.Button("Apply Height Scale"))
            ApplyHeightTransform(h => h * _heightScaleFactor, "Scale Terrain Height");
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(2);
        _heightRaiseOffset = SC2TerrainShell.DrawCompactSlider("Raise/Lower Amount", _heightRaiseOffset, -SC2TerrainData.MaxHeight, SC2TerrainData.MaxHeight);
        EditorGUI.BeginDisabledGroup(_win.Runtime == null);
        if (GUILayout.Button("Apply Raise/Lower"))
            ApplyHeightTransform(h => h + _heightRaiseOffset, "Raise/Lower Terrain");
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Import Heightmap", EditorStyles.boldLabel);
        // Wider range than SC2TerrainData.MaxHeight on purpose: the final
        // per-vertex height is still clamped to [MinHeight, MaxHeight] by
        // ImportHeightmap (via SC2TerrainData.ClampHeight), so this slider
        // isn't a hard height cap — it's a contrast/steepness multiplier
        // applied to the source image's 0..1 grayscale BEFORE that clamp.
        // A user asked for this after finding the old MaxHeight-capped range
        // (512) too narrow to push mid-gray pixels close to full height.
        _importScale = SC2TerrainShell.DrawCompactSlider("Height Scale", _importScale, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight * 8f);
        // Free choice of file to import,
        // not restricted to an already-imported Texture2D project asset —
        // one shared button/dialog for Editor and Build & Run alike, since
        // SC2HeightmapExporter.ImportHeights reads the PNG bytes directly
        // (its own general grayscale decoder, 8- or 16-bit, full precision)
        // and never touches Unity's Texture2D/AssetDatabase import pipeline
        // at all — the thing that was silently downgrading precision when
        // this used to go through a real Texture2D asset.
        EditorGUI.BeginDisabledGroup(_win.Runtime == null);
        if (GUILayout.Button("Import Heightmap from file..."))
            ImportHeightmapFromFile();
        EditorGUI.EndDisabledGroup();
        if (!string.IsNullOrEmpty(_lastImportError))
            SC2TerrainShell.DrawUnifiedNote(_lastImportError);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Export Heightmap", EditorStyles.boldLabel);
        // Same "read-back" direction as Import's "Height Scale" above: a
        // 16-bit grayscale PNG, one pixel per height GRID VERTEX, intensity
        // = height normalized across [MinHeight, MaxHeight] — see
        // SC2HeightmapExporter's own doc comment for the exact round-trip
        // contract with Import. Works identically in the Editor and a
        // Build & Run player (plain File.WriteAllBytes, only the save-
        // location picker differs) so this is one shared button — free
        // choice of destination folder, same as Import's free choice of
        // source file.
        EditorGUI.BeginDisabledGroup(Data == null || !Data.IsInitialized);
        if (GUILayout.Button("Export Heightmap..."))
            ExportHeightmap();
        EditorGUI.EndDisabledGroup();
        if (!string.IsNullOrEmpty(_lastExportError))
            SC2TerrainShell.DrawUnifiedNote(_lastExportError);
    }

    private string _lastExportError;
    private string _lastImportError;

    private void ExportHeightmap()
    {
        // Exporting Data.Heights (the live
        // procedural sculpt mesh's OWN resolution, GridWidth+1) doesn't
        // match "the map" — the real canonical map-height grid is
        // CostsGridSize+1, the same resolution hfield.bdf/Costs/Collision2
        // (and Costs painting/Objects placement's own live height queries)
        // already use. Sourcing from SC2LiveHeightGrid's projection instead
        // of Data.Heights directly also correctly includes any merged
        // imported terrain.bdf geometry, which Data.Heights alone never
        // captured at all.
        if (!_win.TryGetLiveHeightGridSnapshot(out var heightmap128, out var mapX, out var mapY))
        {
            _lastExportError = "Export failed: no terrain height data available.";
            return;
        }
        var heights = new float[heightmap128.Length];
        for (var i = 0; i < heights.Length; i++)
            heights[i] = heightmap128[i] / 128f;

        var png = SC2HeightmapExporter.BuildPng(heights, mapX - 1, mapY - 1);
        if (png == null)
        {
            _lastExportError = "Export failed: height data doesn't match the terrain's own grid size.";
            return;
        }

#if UNITY_EDITOR
        var path = EditorUtility.SaveFilePanel("Export Heightmap", "", "heightmap", "png");
#else
        var path = SC2RuntimeFileDialog.SaveFile("Export Heightmap", "PNG Image (*.png)", "png");
#endif
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            File.WriteAllBytes(path, png);
            _lastExportError = null;
        }
        catch (System.Exception ex)
        {
            _lastExportError = "Export failed: " + ex.Message;
        }
    }

    private void ImportHeightmapFromFile()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Import Heightmap", "", "png");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Import Heightmap", "PNG Image (*.png)", "png");
#endif
        if (string.IsNullOrEmpty(path)) return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (System.Exception ex)
        {
            _lastImportError = "Could not read the selected file: " + ex.Message;
            return;
        }

        var newHeights = SC2HeightmapExporter.ImportHeights(bytes, Data.GridWidth, Data.GridHeight, _importScale, out var error);
        if (newHeights == null)
        {
            _lastImportError = "Import failed: " + error;
            return;
        }

        _win.RecordUndoCheckpoint("Import Heightmap");
        _win.Runtime.UploadHeightsCPU(newHeights);
        Data.Heights = _win.Runtime.GetHeightsCopy();
        EditorUtility.SetDirty(Data);
        SC2TerrainShell.BumpTerrainGeometryVersion();
        _win.InvalidateLiveHeightGrid();
        _lastImportError = null;
    }

    /// <summary>Rewrites every height in the terrain via <paramref name="transform"/>
    /// (clamped to [MinHeight, MaxHeight] the same way every other height
    /// mutation here already is) - used by both "Apply Height Scale" and
    /// "Apply Raise/Lower". One undo checkpoint per click, not per-vertex.</summary>
    private void ApplyHeightTransform(System.Func<float, float> transform, string undoLabel)
    {
        if (_win.Runtime == null) return;
        _win.RecordUndoCheckpoint(undoLabel);
        var heights = Data.Heights;
        var newHeights = new float[heights.Length];
        for (var i = 0; i < heights.Length; i++)
            newHeights[i] = SC2TerrainData.ClampHeight(transform(heights[i]));
        _win.Runtime.UploadHeightsCPU(newHeights);
        Data.Heights = _win.Runtime.GetHeightsCopy();
        EditorUtility.SetDirty(Data);
        SC2TerrainShell.BumpTerrainGeometryVersion();
        _win.InvalidateLiveHeightGrid();
    }

    // ── SceneView ─────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    public void OnSceneGUI(SceneView sv)
    {
        HandleScroll(Event.current);
        RaycastTerrain();
        DrawGizmos();
        HandleInput(Event.current);
    }
#endif

    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        var e = Event.current;
        if (e == null || camera == null)
            return;

        if (!viewportRect.Contains(mousePosition) && !_isPainting)
            return;

        HandleScroll(e);
        RaycastTerrain(camera, mousePosition, viewportRect);
        // Checked BEFORE DrawGameViewGizmo/HandleInput (not after) so an
        // already-active resize draws its own preview circle and keeps
        // responding to mouse movement/Escape/click-to-confirm instead of
        // this tool's own brush cursor/sculpt input firing underneath it.
        if (_radiusGesture.HandleInput(camera, viewportRect, mousePosition, _brushVis, _brushPos, true, ref _radius, 1f, 1000f))
            return;
        DrawGameViewGizmo(camera, viewportRect);
        HandleInput(e);
    }

    void HandleScroll(Event e)
    {
        if (!e.isScrollWheel) return;
        if (e.shift && !e.alt && !e.control)
        { _radius   = Mathf.Clamp(_radius - e.delta.y * 2f, 1f, 1000f); e.Use(); _win.Repaint(); }
        else if (e.control && !e.alt)
        { _strength = Mathf.Clamp01(_strength - e.delta.y * .02f); e.Use(); _win.Repaint(); }
        else if (e.alt && !e.control)
        { _hardness = Mathf.Clamp01(_hardness + e.delta.y * .05f); e.Use(); _win.Repaint(); }
    }

    void RaycastTerrain()
    {
        _brushVis = false;
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        if (SC2TerrainShell.RaycastTerrainHeight(_win.Runtime, ray, out var hit))
        {
            _brushPos = hit;
            _brushVis = true;
            return;
        }

        if (ray.direction.y < -1e-5f)
        { _brushPos = ray.GetPoint((_brushPos.y - ray.origin.y) / ray.direction.y); _brushVis = true; }
        if (!_brushVis && ray.direction.y < -1e-10f)
        { _brushPos = ray.GetPoint(-ray.origin.y / ray.direction.y); _brushVis = true; }
    }

    void RaycastTerrain(Camera camera, Vector2 guiMousePosition, Rect viewportRect)
    {
        _brushVis = false;
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, guiMousePosition);
        if (SC2TerrainShell.RaycastTerrainHeight(_win.Runtime, ray, out var hit))
        {
            _brushPos = hit;
            _brushVis = true;
            return;
        }

        if (ray.direction.y < -1e-5f)
        {
            _brushPos = ray.GetPoint((_brushPos.y - ray.origin.y) / ray.direction.y);
            _brushVis = true;
        }
        if (!_brushVis && ray.direction.y < -1e-10f)
        {
            _brushPos = ray.GetPoint(-ray.origin.y / ray.direction.y);
            _brushVis = true;
        }
    }

    void DrawGizmos()
    {
        if (!_brushVis) return;

        // Outer ring — full radius
        Handles.color = new Color(1f, 1f, .2f, .9f);
        Handles.DrawWireDisc(_brushPos, Vector3.up, _radius);

        // Inner ring — hardness (where falloff starts)
        // hardness=1 → inner=outer (hard), hardness=0 → inner=0 (soft)
        var innerR = _radius * _hardness;
        if (innerR > 0.5f)
        {
            Handles.color = new Color(1f, .8f, .0f, .6f);
            Handles.DrawWireDisc(_brushPos, Vector3.up, innerR);
        }

        // Fill
        Handles.color = new Color(1f, 1f, .2f, .04f);
        Handles.DrawSolidDisc(_brushPos, Vector3.up, _radius);

        // SetHeight: show target height line
        if (_tool == Tool.SetHeight)
        {
            Handles.color = new Color(0f, 1f, 1f, .8f);
            Handles.DrawWireDisc(new Vector3(_brushPos.x, _setHeight, _brushPos.z),
                                 Vector3.up, _radius * 1.05f);
        }

        if (Data.Symmetry != SymmetryMode.None)
        {
            Handles.color = new Color(.3f, .8f, 1f, .6f);
            foreach (var s in Data.GetSymmetricPositions(_brushPos))
                if (s != _brushPos) Handles.DrawWireDisc(s, Vector3.up, _radius);
        }
    }

    void DrawGameViewGizmo(Camera camera, Rect viewportRect)
    {
        if (!_brushVis) return;

        SC2TerrainShell.DrawGameViewBrush(
            camera,
            viewportRect,
            _brushPos,
            _radius,
            new Color(1f, 1f, 0.1f, 0.95f),
            new Color(1f, 0.65f, 0f, 0.75f),
            _radius * _hardness);

        if (_tool == Tool.SetHeight)
        {
            SC2TerrainShell.DrawGameViewBrush(
                camera,
                viewportRect,
                new Vector3(_brushPos.x, _setHeight, _brushPos.z),
                _radius * 1.05f,
                new Color(0f, 1f, 1f, 0.9f),
                Color.clear);
        }
    }

    /// <summary>Editable path + thumbnail for Data.CustomGeometryBrushTexturePath
    /// — straight copy of SC2PaintMode.DrawCustomBrushPicker's pattern (same
    /// shared widget, same resolve-cache shape), just targeting the
    /// Geometry-specific field. The stamp's own alpha channel is what
    /// SC2TerrainRuntime.SampleBrushMask actually reads.</summary>
    private void DrawCustomGeometryBrushPicker()
    {
        var currentPath = Data.CustomGeometryBrushTexturePath ?? "";
        if (currentPath != _customGeoBrushCachedPath)
        {
            _customGeoBrushCache = SC2TerrainMaterialFactory.LoadTexturePublicAny(currentPath);
            _customGeoBrushCachedPath = currentPath;
        }
        var pathDraft = _customGeoBrushCachedPath;
        var picked = SC2TerrainShell.DrawTexturePickerWithPath("Brush Image", _customGeoBrushCache, "GeometryBrush:CustomBrush", ref pathDraft, out var pickedViaBrowser);
        if (picked != _customGeoBrushCache)
        {
            _customGeoBrushCache = picked;
            if (pickedViaBrowser)
                pathDraft = picked != null ? (SC2TerrainMaterialFactory.TryGetResourcePath(picked) ?? "") : "";
        }
        if (pathDraft != (Data.CustomGeometryBrushTexturePath ?? ""))
        {
            _win.RecordUndoCheckpoint("Change Geometry Brush Texture");
            Data.CustomGeometryBrushTexturePath = pathDraft;
            _customGeoBrushCachedPath = pathDraft;
            EditorUtility.SetDirty(Data);
        }
    }

    /// <summary>Rebuilds _geoBrushPixels (a CPU-readable Color32[] snapshot)
    /// only when the resolved custom-brush Texture2D instance actually
    /// changed since the last stroke — SculptBrush/SculptSetHeight's
    /// per-vertex CPU sampling (SC2TerrainRuntime.SampleBrushMask) needs
    /// real pixel data, not just a GPU texture reference, and GetPixels32()
    /// is too expensive to call every frame of a drag.</summary>
    private void EnsureGeoBrushPixelCache()
    {
        if (!Data.UseCustomGeometryBrush || _customGeoBrushCache is not Texture2D tex2d)
        {
            _geoBrushPixels = null;
            _geoBrushPixelsSourceTex = null;
            return;
        }
        if (_geoBrushPixelsSourceTex == tex2d && _geoBrushPixels != null) return;
        // Not every resolvable Texture2D is guaranteed CPU-readable (e.g. a
        // vanilla catalog texture picked via the browser rather than one
        // imported through SC2CustomTextureImporter, which does keep its
        // result readable) — GetPixels32() throws on a non-readable one;
        // fall back to no mask (round-brush behavior) rather than crash the
        // whole sculpt stroke.
        try
        {
            _geoBrushPixels = tex2d.GetPixels32();
            _geoBrushPixelsW = tex2d.width;
            _geoBrushPixelsH = tex2d.height;
            _geoBrushPixelsSourceTex = tex2d;
        }
        catch (UnityException ex)
        {
            Debug.LogWarning($"[SC2GeometryMode] Custom brush texture '{tex2d.name}' isn't CPU-readable — falling back to the round brush for this stroke: {ex.Message}");
            _geoBrushPixels = null;
            _geoBrushPixelsSourceTex = null;
        }
    }

    void HandleInput(Event e)
    {
        if (!_brushVis || _win.Runtime == null) return;

        // Alt+left-click over the terrain
        // samples the height under the cursor into whichever target-height
        // field the active tool actually uses — the same action as the
        // "Pick" button in DrawPanel above, just reachable without leaving
        // the viewport. Only meaningful for Flatten/SetHeight (the only two
        // tools with a target height at all).
        //
        // A follow-up fix: this originally used a
        // plain middle-mouse-click, which broke camera orbit any time the
        // active tool was Flatten/SetHeight — SC2GameViewEditorUI's own
        // navigation handling (HandleGameViewNavigation) sets _orbiting on
        // the SAME MouseDown event that this branch was consuming first (and
        // e.Use()-ing), so orbit's own MouseDown handler never got the
        // event, silently disabling orbit-drag for exactly those two tools.
        // Reassigned to Alt+left-click instead, which needs no e.Use()
        // exclusion against orbit/pan (both are plain/Shift+middle-drag,
        // untouched by Alt) and already has a matching carve-out in the
        // normal paint down/drag below (!e.alt), so Alt+click can never
        // double as a paint stroke either.
        if (e.type == EventType.MouseDown && e.button == 0 && e.alt && (_tool == Tool.Flatten || _tool == Tool.SetHeight))
        {
            _win.RecordUndoCheckpoint("Pick Height");
            if (_tool == Tool.SetHeight) _setHeight = SC2TerrainData.ClampHeight(_brushPos.y);
            else _flattenH = SC2TerrainData.ClampHeight(_brushPos.y);
            e.Use();
            _win.Repaint();
            return;
        }

        var down = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
        var drag = e.type == EventType.MouseDrag  && e.button == 0 && !e.alt;
        if (e.type == EventType.MouseUp)
        {
            // Real Smart Export dirty-
            // tracking for terrain geometry — sculpting has always mutated
            // Data.Heights directly with zero signal to the session
            // (SC2MapSession.PendingDirtyFlags), the one gap that forced
            // Costs/Hfield/Collision2 to stay permanently "dirty" via the
            // TerrainGeometry cascade in SC2ExportDependencyGraph regardless
            // of their own tracking. Only fires for a real completed
            // stroke, not an idle mouse-up.
            if (_isPainting)
            {
                _win.Session?.MarkDirty(SC2DirtyFlags.TerrainGeometry);
                // Sculpting a hill up above
                // the water plane still showed the water's (old, pre-sculpt)
                // elevation in the mouse-height HUD/Costs painting/Objects
                // placement — SC2LiveHeightGrid caches its projected
                // heightmap and only rebuilds when told to (MarkDirty), but
                // nothing here ever told it to, unlike every other real
                // geometry-change source (import add/remove/undo/redo,
                // ImportOnlyMode toggle, Costs grid size change — see
                // SC2TerrainShell.InvalidateLiveHeightGrid's call sites).
                // Sculpting a completed stroke is exactly as real a change
                // as any of those.
                _win.InvalidateLiveHeightGrid();
            }
            _isPainting = false;
            Data.Heights = _win.Runtime.GetHeightsCopy();
            EditorUtility.SetDirty(Data);
            SC2TerrainShell.BumpTerrainGeometryVersion();
            return;
        }
        if (down)
        {
            _isPainting = true;
            // Roll a fresh stamp rotation for this stroke only,
            // and (re)build the readable-pixel cache the mask sampler needs
            // — see _geoBrushRotationRad/EnsureGeoBrushPixelCache's own doc
            // comments.
            _geoBrushRotationRad = (Data.UseCustomGeometryBrush && Data.RandomizeGeometryBrushRotation)
                ? Random.Range(0f, Mathf.PI * 2f) : 0f;
            EnsureGeoBrushPixelCache();
            // Heights[] is only re-synced from the runtime on MouseUp (see below),
            // so it already reflects the state before this stroke — one undo
            // checkpoint per stroke, not per drag frame.
            _win.RecordUndoCheckpoint("Sculpt");
        }
        if (!_isPainting || (!down && !drag)) return;
        var now = EditorApplication.timeSinceStartup;
        if (!down && now - _lastApply < kMinInterval) return;
        _lastApply = now;

        // A SEPARATE toggle from RandomizeGeometryBrushRotation — that one
        // still rolls once per stroke (mouse-down) and holds it fixed for
        // the drag; this one re-rolls on every single application (down AND
        // every drag frame that passes the throttle above).
        if (Data.UseCustomGeometryBrush && Data.RandomizeGeometryBrushRotation && Data.RandomizeGeometryBrushRotationContinuous)
            _geoBrushRotationRad = Random.Range(0f, Mathf.PI * 2f);

        var brushMask = Data.UseCustomGeometryBrush ? _geoBrushPixels : null;
        if (_tool == Tool.SetHeight)
        {
            // SetHeight: directly set vertices within radius to _setHeight,
            // with hardness-controlled falloff
            foreach (var pos in Data.GetSymmetricPositions(_brushPos))
                _win.Runtime.SculptSetHeight(pos, _radius, _strength, _hardness, _setHeight,
                    brushMask, _geoBrushPixelsW, _geoBrushPixelsH, _geoBrushRotationRad);
        }
        else
        {
            var mode = (int)_tool;
            var target = _tool == Tool.Flatten ? _flattenH : 0f;
            foreach (var pos in Data.GetSymmetricPositions(_brushPos))
                _win.Runtime.SculptBrush(pos, _radius, _strength, _hardness, mode, target,
                    brushMask, _geoBrushPixelsW, _geoBrushPixelsH, _geoBrushRotationRad);
        }

        e.Use();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
        _win.Repaint();
    }

    // ── Gradient overlay ──────────────────────────────────────────────────────

    public bool HandleKeyboardShortcut(UnityEngine.Event e) => false;
}
