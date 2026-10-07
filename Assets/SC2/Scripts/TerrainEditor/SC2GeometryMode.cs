using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
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
    public bool StrengthScalesWithRadius;
    public bool FlattenLockPlane;
}

/// <summary>
/// Sculpting tool — shared, unmodified, between the core and the
/// background terrain. Per explicit user request, tool/radius/strength/
/// hardness/etc. are ONE selection regardless of which surface the brush
/// is currently pointing at, not two independent copies — so this class
/// reads/writes whichever surface SC2TerrainShell.ActiveRuntime/
/// ActiveTerrainData currently resolves to (decided by
/// SC2TerrainShell.DetermineActiveBrushTarget from where the cursor
/// actually is), instead of a fixed Data/Runtime pair. ActiveWorldOffset is
/// zero for the core (which always sits at world origin) and the
/// background's own real offset otherwise, added/subtracted uniformly so
/// every call site below works unchanged for either surface.
///
/// The surface under the cursor decides what the panel shows and where a
/// stroke starts, but the stroke itself is applied to EVERY surface the brush
/// reaches (CollectTargets): along the core/background seam one stroke shapes
/// both, and the background's rim is re-synced to the core's perimeter after
/// each dab so they stay level. The undo checkpoints of a stroke that reaches
/// both are one joint step (SC2TerrainShell.SeamStrokeLabel).
///
/// Two things deliberately still always read the CORE's own data
/// regardless of which surface is active:
///  - Symmetry (Data.Symmetry/SymmetryCenter/GetSymmetricPositions) — there
///    is only one symmetry setting for the whole map, authored on the
///    Terrain tab's own Symmetry buttons.
///  - The brush's own SHAPE settings (UseCustomGeometryBrush/
///    CustomGeometryBrushTexturePath/RandomizeGeometryBrushRotation*) — like
///    Tool/Radius/etc. above, these are meant to be ONE shared brush
///    preference, but they're persisted as fields ON SC2TerrainData rather
///    than on this class, so they explicitly read/write the core's own
///    _win.Data instead of the dynamic Data property to avoid silently
///    forking into two independent copies (core's own vs background's own
///    nested SC2TerrainData instance) the moment the active surface changes.
/// </summary>
public class SC2GeometryMode
{
    // Which core map this instance was built for — used only by
    // SC2TerrainShell.EnsureSubEditors to detect a map switch and rebuild
    // this instance. NOT what sculpt operations act on; see Data below.
    public SC2TerrainData OwnerData { get; }
    private SC2TerrainShell _win;

    /// <summary>The surface actually being read/written right now — see
    /// this class' own header comment.</summary>
    public SC2TerrainData Data => _win.ActiveTerrainData;

    // Mode 5 = SetHeight (draws exact height), rest match SculptBrush modes
    private enum Tool { Raise=0, Lower=1, Smooth=2, Flatten=3, Noise=4, SetHeight=5 }
    private Tool  _tool      = Tool.Raise;
    private float _radius    = 20f;
    private float _strength  = 0.5f;
    private float _hardness  = 0.5f; // 0=soft cosine, 1=hard step
    private float _flattenH  = SC2TerrainData.DefaultFlatHeight;
    private float _setHeight = SC2TerrainData.DefaultFlatHeight;  // target height for SetHeight tool

    // Off by default (unchanged behavior): Raise/Lower/Smooth apply the same
    // per-cell height delta regardless of Radius (SculptBrush's own dt is a
    // function of Strength alone), which can make a wide brush feel "weaker"
    // per stroke than a small one at the same Strength since a single drag
    // re-stamps a small brush's own footprint far more densely. When on,
    // effective strength scales linearly with Radius relative to
    // StrengthRadiusReference (the slider's own default radius, so a fresh
    // brush at its default Radius/Strength behaves identically either way) —
    // Raise/Lower/Smooth only, per direct instruction; Flatten/SetHeight
    // target an absolute height rather than adding a delta, and Noise's own
    // amplitude wasn't asked for.
    private bool _strengthScalesWithRadius;
    private const float StrengthRadiusReference = 20f;

    // Flatten works like Blender's: it samples a plane from the terrain under the brush on every dab.
    // With _flattenLockPlane it samples once at the start of a stroke instead (Blender's "Original
    // Plane"); _flattenPlanes holds that plane per symmetry copy for the stroke in progress.
    private bool _flattenLockPlane = true;
    private SC2FlattenPlane[] _flattenPlanes = System.Array.Empty<SC2FlattenPlane>();
    // Flatten dabs are placed by distance along the stroke (Blender's spacing), not per mouse event;
    // this is where the last one was placed. Null at the start of a stroke.
    private Vector3? _lastFlattenDab;
    private const int MaxFlattenDabsPerEvent = 48;

    private Vector3 _brushPos;
    private bool    _brushVis, _isPainting;
    private double  _lastApply;
    private const double kMinInterval = 0.016;
    // "Place as stamp": the custom brush lays the stamp down once, sized and turned by a press-drag-release (see SC2StampPlacement).
    // A stamp raises, lowers or roughens by Strength (its alpha) x _stampHeight world units at its brightest point.
    private readonly SC2StampPlacement _stamp = new SC2StampPlacement();
    private float _stampHeight = 8f;
    // SculptBrush adds falloff x strength x this per dab (dt = strength * 0.016 * 60), and Smooth moves a vertex half of that toward
    // its neighbours' average.
    private const float SculptHeightPerStrength = 0.96f;

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
    { _win = win; OwnerData = data; }

    /// <summary>See SC2GeometryToolState's own doc comment.</summary>
    public SC2GeometryToolState CaptureState() => new SC2GeometryToolState
    {
        Tool = (int)_tool,
        Radius = _radius,
        Strength = _strength,
        Hardness = _hardness,
        FlattenH = _flattenH,
        SetHeight = _setHeight,
        StrengthScalesWithRadius = _strengthScalesWithRadius,
        FlattenLockPlane = _flattenLockPlane,
    };

    public void ApplyState(SC2GeometryToolState state)
    {
        _tool = (Tool)state.Tool;
        _radius = state.Radius;
        _strength = state.Strength;
        _hardness = state.Hardness;
        _flattenH = state.FlattenH;
        _setHeight = state.SetHeight;
        _strengthScalesWithRadius = state.StrengthScalesWithRadius;
        _flattenLockPlane = state.FlattenLockPlane;
    }

    // ── Panel ─────────────────────────────────────────────────────────────────

    public void DrawPanel()
    {
        // Each group of controls gets its own heading and a gap above it, so the grids and sliders do not run together.
        SC2TerrainShell.DrawUnifiedSectionLabel("Brush tool");
        var toolLabels = new[] { "Raise", "Lower", "Smooth", "Flatten", "Set H", "Noise" };
        var toolValues = new[] { Tool.Raise, Tool.Lower, Tool.Smooth, Tool.Flatten, Tool.SetHeight, Tool.Noise };
        var selected = System.Array.IndexOf(toolValues, _tool);
        selected = Mathf.Max(0, SC2TerrainShell.DrawUnifiedSegmentGrid(selected, toolLabels, 3));
        var newTool = toolValues[selected];
        if (newTool != _tool)
        {
            // RecordUndoCheckpointFor, not the bare core-only overload —
            // this is a pure tool-CONFIG change (part of the shared Extra
            // channel both stacks capture), not a terrain-data edit, but it
            // still needs to land on whichever stack (core or background)
            // Ctrl+Z will actually pop next, which SC2TerrainShell decides
            // from whatever was most recently edited — see
            // SC2TerrainShell._lastEditedTargetIsBackground's own doc
            // comment. The bare overload would always record onto the
            // core's stack regardless, silently losing this checkpoint from
            // Undo history whenever the background was the more recent edit.
            _win.RecordUndoCheckpointFor(Data, "Change Brush Tool");
            _tool = newTool;
        }

        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Brush settings");
        _radius   = SC2TerrainShell.DrawCompactSlider("Radius",   _radius,   1f, 1000f,
            onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change Brush Radius"));
        _strength = SC2TerrainShell.DrawCompactSlider("Strength", _strength, 0.01f, 1f,
            onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change Brush Strength"));
        var newScalesWithRadius = EditorGUILayout.ToggleLeft(" Scale strength with radius (Raise/Lower/Smooth)", _strengthScalesWithRadius);
        if (newScalesWithRadius != _strengthScalesWithRadius)
        {
            _win.RecordUndoCheckpointFor(Data, "Toggle Strength Scales With Radius");
            _strengthScalesWithRadius = newScalesWithRadius;
        }
        _hardness = SC2TerrainShell.DrawCompactSlider("Hardness", _hardness, 0f, 1f,
            onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change Brush Hardness"));
        SC2TerrainShell.DrawBrushHint();

        // Irregular stamp brushes (World
        // Machine/Gaea-style grayscale masks) for sculpting, mirroring
        // Paint's existing Blend-brush stamp feature (SC2PaintMode.DrawRnm/
        // DrawCustomBrushPicker) — see SC2TerrainRuntime.SculptBrush/
        // SculptSetHeight's brushMask params for where this actually applies.
        // Always the CORE's own _win.Data (not the dynamic Data property) —
        // see this class' own header comment for why.
        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Brush shape");
        var brushShapeLabels = new[] { "Round", "Custom" };
        var nextCustomBrush = SC2TerrainShell.DrawUnifiedSegmentGrid(_win.Data.UseCustomGeometryBrush ? 1 : 0, brushShapeLabels, 2) == 1;
        if (nextCustomBrush != _win.Data.UseCustomGeometryBrush)
        {
            _win.RecordUndoCheckpoint("Change Geometry Brush Shape");
            _win.Data.UseCustomGeometryBrush = nextCustomBrush;
            EditorUtility.SetDirty(_win.Data);
        }
        if (_win.Data.UseCustomGeometryBrush)
        {
            DrawCustomGeometryBrushPicker();
            _stamp.DrawOptions("geometry");
            if (_stamp.Enabled && (_tool == Tool.Raise || _tool == Tool.Lower || _tool == Tool.Noise))
                _stampHeight = SC2TerrainShell.DrawCompactSlider("Stamp height", _stampHeight, 0.5f, 200f, "geometry-stamp-height");
            var nextRandomize = EditorGUILayout.Toggle("Randomize Rotation", _win.Data.RandomizeGeometryBrushRotation);
            if (nextRandomize != _win.Data.RandomizeGeometryBrushRotation)
            {
                _win.RecordUndoCheckpoint("Change Geometry Brush Randomize Rotation");
                _win.Data.RandomizeGeometryBrushRotation = nextRandomize;
                EditorUtility.SetDirty(_win.Data);
            }
            if (_win.Data.RandomizeGeometryBrushRotation)
            {
                var nextContinuous = EditorGUILayout.Toggle("Randomize While Dragging", _win.Data.RandomizeGeometryBrushRotationContinuous);
                if (nextContinuous != _win.Data.RandomizeGeometryBrushRotationContinuous)
                {
                    _win.RecordUndoCheckpoint("Change Geometry Brush Randomize Continuous");
                    _win.Data.RandomizeGeometryBrushRotationContinuous = nextContinuous;
                    EditorUtility.SetDirty(_win.Data);
                }
            }
        }

        if (_tool == Tool.Flatten)
        {
            var newLock = EditorGUILayout.ToggleLeft(" Original plane (sample once per stroke)", _flattenLockPlane);
            if (newLock != _flattenLockPlane)
            {
                _win.RecordUndoCheckpointFor(Data, "Toggle Flatten Original Plane");
                _flattenLockPlane = newLock;
            }
            SC2TerrainShell.DrawUnifiedNote("Flatten pulls the terrain under the brush toward the plane of that terrain — the falloff-weighted average position and slope inside the radius — up or down, so a slope flattens into a slope, not a level floor. With 'Original plane' (default) the plane is taken where the stroke starts and kept while you drag, so the ground does not flow after the brush; without it the plane is re-sampled under the brush on every dab. The plane is sampled from the middle half of the brush. For an exact height use Set H.");
        }
        else if (_tool == Tool.SetHeight)
        {
            _setHeight = SC2TerrainShell.DrawCompactSlider("Height", _setHeight, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight,
                onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change Set Height"));
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Pick", GUILayout.Width(SC2UiScale.Pxf(40f))))
            {
                _win.RecordUndoCheckpointFor(Data, "Pick Height");
                _setHeight = SC2TerrainData.ClampHeight(_brushPos.y);
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Height Adjustments");
        // Whole-map operations, distinct from the brush tools above (which
        // only ever touch the area under the cursor) - each rewrites every
        // vertex in Data.Heights in one shot via SC2TerrainRuntime.
        // UploadHeightsCPU (the same bulk-write primitive ImportHeightmap
        // already uses), then syncs Data.Heights back from the runtime the
        // same way every other mutation here does. Applies to whichever
        // surface is currently active, same as the brush tools above.
        _heightScaleFactor = SC2TerrainShell.DrawCompactSlider("Scale Factor", _heightScaleFactor, 0.1f, 5f);
        EditorGUI.BeginDisabledGroup(_win.ActiveRuntime == null);
        if (GUILayout.Button("Apply Height Scale"))
            ApplyHeightTransform(h => h * _heightScaleFactor, "Scale Terrain Height");
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(SC2UiScale.Pxf(6f));
        _heightRaiseOffset = SC2TerrainShell.DrawCompactSlider("Raise/Lower Amount", _heightRaiseOffset, -SC2TerrainData.MaxHeight, SC2TerrainData.MaxHeight);
        EditorGUI.BeginDisabledGroup(_win.ActiveRuntime == null);
        if (GUILayout.Button("Apply Raise/Lower"))
            ApplyHeightTransform(h => h + _heightRaiseOffset, "Raise/Lower Terrain");
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Import Heightmap");
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
        EditorGUI.BeginDisabledGroup(_win.ActiveRuntime == null);
        if (GUILayout.Button("Import Heightmap from file..."))
            ImportHeightmapFromFile();
        EditorGUI.EndDisabledGroup();
        if (!string.IsNullOrEmpty(_lastImportError))
            SC2TerrainShell.DrawUnifiedNote(_lastImportError);

        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Export Heightmap");
        // Same "read-back" direction as Import's "Height Scale" above: a
        // 16-bit grayscale PNG, one pixel per height GRID VERTEX, intensity
        // = height normalized across [MinHeight, MaxHeight] — see
        // SC2HeightmapExporter's own doc comment for the exact round-trip
        // contract with Import. Works identically in the Editor and a
        // Build & Run player (plain File.WriteAllBytes, only the save-
        // location picker differs) so this is one shared button — free
        // choice of destination folder, same as Import's free choice of
        // source file. Always the CORE's own map — there is no equivalent
        // "canonical costs-grid heightmap" for the background (see
        // ExportHeightmap's own use of TryGetLiveHeightGridSnapshot, which
        // is core-only), unlike every other control on this panel.
        EditorGUI.BeginDisabledGroup(_win.Data == null || !_win.Data.IsInitialized);
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
        // captured at all. Core-only — see DrawPanel's own comment on this
        // button.
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

        _win.RecordUndoCheckpointFor(Data, "Import Heightmap");
        _win.ActiveRuntime.UploadHeightsCPU(newHeights);
        Data.Heights = _win.ActiveRuntime.GetHeightsCopy();
        EditorUtility.SetDirty(Data);
        SC2TerrainShell.BumpTerrainGeometryVersion();
        _win.InvalidateLiveHeightGrid();
        NotifyWholeMapHeightChanged();
        _lastImportError = null;
    }

    /// <summary>Same SC2TerrainShell.HeightBrushApplied notification live
    /// brush strokes fire (see its own doc comment), for a whole-map height
    /// rewrite instead of one brush stamp — a heightmap import or an Apply
    /// Height Scale/Raise/Lower can move every vertex on the map at once,
    /// so every anchored Prop/Marker/Unit/AlignToSurface decal needs
    /// reprojecting, not just a local circle. Reuses the same (center,
    /// radius) event shape with a radius spanning the map's whole diagonal
    /// rather than adding a second event type. World-space center — adds
    /// ActiveWorldOffset when the active surface is the background, same as
    /// every brush-stamp notification below.</summary>
    private void NotifyWholeMapHeightChanged()
    {
        var worldWidth = Data.GridWidth * Data.CellSize;
        var worldHeight = Data.GridHeight * Data.CellSize;
        var offset = _win.ActiveWorldOffset;
        var center = offset + new Vector3(worldWidth * 0.5f, 0f, worldHeight * 0.5f);
        var radius = 0.5f * Mathf.Sqrt(worldWidth * worldWidth + worldHeight * worldHeight) + 1f;
        _win.HeightBrushApplied?.Invoke(center, radius);
    }

    /// <summary>Rewrites every height in the terrain via <paramref name="transform"/>
    /// (clamped to [MinHeight, MaxHeight] the same way every other height
    /// mutation here already is) - used by both "Apply Height Scale" and
    /// "Apply Raise/Lower". One undo checkpoint per click, not per-vertex.</summary>
    private void ApplyHeightTransform(System.Func<float, float> transform, string undoLabel)
    {
        if (_win.ActiveRuntime == null) return;
        _win.RecordUndoCheckpointFor(Data, undoLabel);
        var heights = Data.Heights;
        var newHeights = new float[heights.Length];
        for (var i = 0; i < heights.Length; i++)
            newHeights[i] = SC2TerrainData.ClampHeight(transform(heights[i]));
        _win.ActiveRuntime.UploadHeightsCPU(newHeights);
        Data.Heights = _win.ActiveRuntime.GetHeightsCopy();
        EditorUtility.SetDirty(Data);
        SC2TerrainShell.BumpTerrainGeometryVersion();
        _win.InvalidateLiveHeightGrid();
        NotifyWholeMapHeightChanged();
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

        if (!viewportRect.Contains(mousePosition) && !_isPainting && !_stamp.Active)
            return;

        HandleScroll(e);
        RaycastTerrain(camera, mousePosition, viewportRect);
        // Checked BEFORE DrawGameViewGizmo/HandleInput (not after) so an
        // already-active resize draws its own preview circle and keeps
        // responding to mouse movement/Escape/click-to-confirm instead of
        // this tool's own brush cursor/sculpt input firing underneath it.
        if (_radiusGesture.HandleInput(camera, viewportRect, mousePosition, _brushVis, _brushPos, true, ref _radius, 1f, 1000f,
            sampleHeight: SampleActiveHeight))
            return;
        DrawGameViewGizmo(camera, viewportRect);
        HandleInput(e);
    }

    /// <summary>Live height at a WORLD (x,z), whichever surface is currently
    /// active — core via SC2ObjectsExport.SampleTerrainHeight(_win.Data,...)
    /// (unchanged), background via SC2TerrainShell.SampleLiveBackgroundHeight
    /// (already offset-aware). Used everywhere this class used to hardcode
    /// the core-only sampler.</summary>
    private float SampleActiveHeight(float x, float z) =>
        _win.ActiveTerrainData == _win.Data
            ? SC2ObjectsExport.SampleTerrainHeight(_win.Data, x, z)
            : _win.SampleLiveBackgroundHeight(x, z);

    void HandleScroll(Event e)
    {
        if (!e.isScrollWheel) return;
        // Radius has its own dedicated F-hold resize gesture (SC2BrushRadiusGesture)
        // — no scroll modifier for it any more, that was a straight duplicate.
        // Some mice/trackpads report an occasional much-larger-than-usual
        // delta for a single tick, which made Strength/Hardness jump hard
        // instead of stepping smoothly — clamped to a single tick's worth.
        // Windows also reports Shift+wheel as a horizontal-scroll message,
        // delta.y coming back 0 with the tick magnitude in delta.x instead;
        // the fallback below covers that for any modifier, not just Shift.
        var delta = Mathf.Clamp(e.delta.y != 0f ? e.delta.y : e.delta.x, -3f, 3f);
        if (SC2Hotkeys.ModifierHeld("Brush.StrengthScrollModifier", e))
        { _strength = Mathf.Clamp01(_strength - delta * .01f); e.Use(); _win.Repaint(); }
        else if (SC2Hotkeys.ModifierHeld("Brush.HardnessScrollModifier", e))
        { _hardness = Mathf.Clamp01(_hardness + delta * .015f); e.Use(); _win.Repaint(); }
    }

    void RaycastTerrain()
    {
        _brushVis = false;
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        var offset = _win.ActiveWorldOffset;
        var localRay = new Ray(ray.origin - offset, ray.direction);
        if (SC2TerrainShell.RaycastTerrainHeight(_win.ActiveRuntime, localRay, out var hit))
        {
            _brushPos = hit + offset;
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
        // RaycastTerrainHeight works in ActiveRuntime's own LOCAL grid space
        // (starts at (0,0)) — ActiveWorldOffset is zero for the core (a
        // no-op here) and the background's own real world offset otherwise,
        // so this same translate/untranslate works for both surfaces
        // unconditionally.
        var offset = _win.ActiveWorldOffset;
        var localRay = new Ray(ray.origin - offset, ray.direction);
        if (SC2TerrainShell.RaycastTerrainHeight(_win.ActiveRuntime, localRay, out var hit))
        {
            _brushPos = hit + offset;
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

        // Symmetry always follows the CORE's own setting (_win.Data), not
        // the dynamic Data — see this class' own header comment.
        if (_win.Data.Symmetry != SymmetryMode.None)
        {
            Handles.color = new Color(.3f, .8f, 1f, .6f);
            foreach (var s in _win.Data.GetSymmetricPositions(_brushPos))
                if (s != _brushPos) Handles.DrawWireDisc(s, Vector3.up, _radius);
        }
    }

    void DrawGameViewGizmo(Camera camera, Rect viewportRect)
    {
        if (_stamp.Active)
        {
            // A stamp being placed: the preview sits at its anchor, not under the mouse.
            SC2StampPreview.Draw(camera, viewportRect, _stamp.Gesture.Anchor, _stamp.Gesture.Radius, _stamp.Gesture.AngleRad, _customGeoBrushCache,
                _strength, new Color(1f, 1f, 0.1f, 0.95f), SampleActiveHeight, _stamp.Readout(_strength),
                _tool == Tool.Raise ? _stampHeight : _tool == Tool.Lower ? -_stampHeight : 0f);
            return;
        }
        if (!_brushVis) return;

        SC2TerrainShell.DrawGameViewBrush(
            camera,
            viewportRect,
            _brushPos,
            _radius,
            new Color(1f, 1f, 0.1f, 0.95f),
            new Color(1f, 0.65f, 0f, 0.75f),
            _radius * _hardness,
            sampleHeight: SampleActiveHeight);

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

        // Symmetry always follows the CORE's own setting (_win.Data), not
        // the dynamic Data — see this class' own header comment.
        if (_win.Data.Symmetry != SymmetryMode.None)
            SC2TerrainShell.DrawSymmetryPointMarkers(camera, viewportRect, _brushPos, _win.Data.GetSymmetricPositions(_brushPos),
                new Color(0.3f, 0.8f, 1f, 0.9f),
                sampleHeight: SampleActiveHeight);
    }

    /// <summary>Editable path + thumbnail for Data.CustomGeometryBrushTexturePath
    /// — straight copy of SC2PaintMode.DrawCustomBrushPicker's pattern (same
    /// shared widget, same resolve-cache shape), just targeting the
    /// Geometry-specific field. The stamp's own alpha channel is what
    /// SC2TerrainRuntime.SampleBrushMask actually reads. Always the CORE's
    /// own _win.Data — see this class' own header comment.</summary>
    private void DrawCustomGeometryBrushPicker()
    {
        var currentPath = _win.Data.CustomGeometryBrushTexturePath ?? "";
        if (currentPath != _customGeoBrushCachedPath)
        {
            _customGeoBrushCache = SC2BrushLibrary.Resolve(currentPath);
            _customGeoBrushCachedPath = currentPath;
        }
        var pathDraft = _customGeoBrushCachedPath;
        // The built-in brushes: one click sets the path and the texture.
        var preset = SC2BrushLibrary.DrawPresetGrid(pathDraft, SC2TerrainShell.GetPanelContentWidth());
        if (preset != null) { pathDraft = preset; _customGeoBrushCache = SC2BrushLibrary.Load(preset); }
        var picked = SC2TerrainShell.DrawTexturePickerWithPath("Brush Image", _customGeoBrushCache, "GeometryBrush:CustomBrush", ref pathDraft, out var pickedViaBrowser);
        if (picked != _customGeoBrushCache)
        {
            _customGeoBrushCache = picked;
            if (pickedViaBrowser)
                pathDraft = picked != null ? (SC2TerrainMaterialFactory.TryGetResourcePath(picked) ?? "") : "";
        }
        if (pathDraft != (_win.Data.CustomGeometryBrushTexturePath ?? ""))
        {
            _win.RecordUndoCheckpoint("Change Geometry Brush Texture");
            _win.Data.CustomGeometryBrushTexturePath = pathDraft;
            _customGeoBrushCachedPath = pathDraft;
            EditorUtility.SetDirty(_win.Data);
        }
    }

    /// <summary>Rebuilds _geoBrushPixels (a CPU-readable Color32[] snapshot)
    /// only when the resolved custom-brush Texture2D instance actually
    /// changed since the last stroke — SculptBrush/SculptSetHeight's
    /// per-vertex CPU sampling (SC2TerrainRuntime.SampleBrushMask) needs
    /// real pixel data, not just a GPU texture reference, and GetPixels32()
    /// is too expensive to call every frame of a drag. Reads the CORE's own
    /// _win.Data (brush shape is a shared setting) — see this class' own
    /// header comment.</summary>
    private void EnsureGeoBrushPixelCache()
    {
        if (!_win.Data.UseCustomGeometryBrush || _customGeoBrushCache is not Texture2D tex2d)
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
        catch (UnityException)
        {
            // Not CPU-readable (a texture from the game archive, say): read it back through the GPU, so the sculpt tools still use the
            // stamp instead of silently dropping to the round brush.
            var rt = RenderTexture.GetTemporary(tex2d.width, tex2d.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(tex2d, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(tex2d.width, tex2d.height, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, tex2d.width, tex2d.height), 0, 0);
                copy.Apply(false);
                _geoBrushPixels = copy.GetPixels32();
                _geoBrushPixelsW = tex2d.width;
                _geoBrushPixelsH = tex2d.height;
                _geoBrushPixelsSourceTex = tex2d;
            }
            catch (UnityException ex)
            {
                Debug.LogWarning($"[SC2GeometryMode] Custom brush texture '{tex2d.name}' could not be read — falling back to the round brush for this stroke: {ex.Message}");
                _geoBrushPixels = null;
                _geoBrushPixelsSourceTex = null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) Object.Destroy(copy);
            }
        }
    }

    // ── Seam editing ──────────────────────────────────────────────────────────
    // A stroke is applied to EVERY surface the brush reaches, not only the one under the cursor, so sculpting along the seam
    // shapes the core and the background together. The background's rim is then re-synced to the core's perimeter after every dab
    // (SC2TerrainShell.HeightBrushApplied), so the two stay exactly level along it.

    private struct SculptTarget
    {
        public SC2TerrainRuntime Runtime;
        public SC2TerrainData Data;
        public Vector3 Offset;
        public bool Background;
    }

    private readonly List<SculptTarget> _targets = new List<SculptTarget>();
    private bool _strokeTouchedCore;
    private bool _strokeTouchedBackground;

    private static SC2FlattenPlane PlaneToLocal(SC2FlattenPlane plane, SculptTarget target)
    {
        plane.Center -= new Vector3(target.Offset.x, 0f, target.Offset.z);
        return plane;
    }

    private static SC2FlattenPlane PlaneToWorld(SC2FlattenPlane plane, SculptTarget target)
    {
        plane.Center += new Vector3(target.Offset.x, 0f, target.Offset.z);
        return plane;
    }

    /// <summary>Fills <see cref="_targets"/> with the surfaces a brush of the current radius at <paramref name="pos"/> (world
    /// space) reaches: the core when the brush touches its rectangle, the background when it touches any of it outside the core
    /// (inside the carved hole there is nothing to sculpt).</summary>
    private void CollectTargets(Vector3 pos, float radius)
    {
        _targets.Clear();
        var core = _win.Data;
        if (core == null) return;
        var margin = core.CellSize * 2f;
        var coreW = core.GridWidth * core.CellSize;
        var coreD = core.GridHeight * core.CellSize;

        if (_win.Runtime != null && SC2SeamReach.ReachesCore(pos.x, pos.z, coreW, coreD, radius, margin))
            _targets.Add(new SculptTarget { Runtime = _win.Runtime, Data = core, Offset = Vector3.zero, Background = false });

        var bg = core.BackgroundTerrainEnabled ? core.BackgroundTerrainData : null;
        if (_win.BackgroundRuntime == null || bg == null) return;
        var offset = SC2BackgroundTerrain.GetWorldOffset(core);
        if (SC2SeamReach.ReachesBackground(pos.x, pos.z, coreW, coreD, offset.x, offset.z,
                bg.GridWidth * bg.CellSize, bg.GridHeight * bg.CellSize, radius, margin))
            _targets.Add(new SculptTarget { Runtime = _win.BackgroundRuntime, Data = bg, Offset = offset, Background = true });
    }

    /// <summary>Records the undo checkpoint of a surface the first time the current stroke touches it. When the second surface
    /// joins, both checkpoints are labelled as one joint step so Undo/Redo walk both back together.</summary>
    private void EnsureStrokeCheckpoint(bool background)
    {
        if (background ? _strokeTouchedBackground : _strokeTouchedCore) return;
        var otherTouched = background ? _strokeTouchedCore : _strokeTouchedBackground;
        var data = background ? _win.Data?.BackgroundTerrainData : _win.Data;
        if (data == null) return;
        _win.RecordUndoCheckpointFor(data, otherTouched ? SC2TerrainShell.SeamStrokeLabel : "Sculpt");
        if (otherTouched) _win.RelabelLastCheckpoint(!background, SC2TerrainShell.SeamStrokeLabel);
        if (background) _strokeTouchedBackground = true; else _strokeTouchedCore = true;
    }

    /// <summary>Places a stamp the user sized and turned with a press-drag-release: replays the ordinary stroke (a first dab, then the
    /// stroke's end) at the anchor with that radius and angle, so symmetry, the seam, flatten planes and undo work as for any other dab.</summary>
    void PlaceStamp(Event release, Vector3 anchor, float radius, float angleRad)
    {
        var savedPos = _brushPos; var savedRadius = _radius;
        _brushPos = anchor; _radius = radius;
        try
        {
            _stamp.Replay(angleRad, () =>
            {
                HandleInput(SC2StampPlacement.SyntheticPress(release));
                HandleInput(release);
            });
        }
        finally { _brushPos = savedPos; _radius = savedRadius; }
    }

    void HandleInput(Event e)
    {
        if (!_brushVis || _win.ActiveRuntime == null) return;

        // Alt+left-click over the terrain
        // samples the height under the cursor into whichever target-height
        // field the active tool actually uses — the same action as the
        // "Pick" button in DrawPanel above, just reachable without leaving
        // the viewport. Only meaningful for SetHeight (the only tool with a
        // target height; Flatten samples its plane from the terrain).
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
        if (e.type == EventType.MouseDown && e.button == 0 && e.alt && _tool == Tool.SetHeight)
        {
            _win.RecordUndoCheckpointFor(Data, "Pick Height");
            _setHeight = SC2TerrainData.ClampHeight(_brushPos.y);
            e.Use();
            _win.Repaint();
            return;
        }

        // A custom brush in "Place as stamp" mode: press anchors, drag sizes and turns it, release places it once (by replaying an
        // ordinary first dab below with the chosen size and angle).
        if (_win.Data.UseCustomGeometryBrush && _stamp.Enabled && _customGeoBrushCache != null && !_stamp.Applying
            && _stamp.HandleEvent(e, _brushPos, _radius, Data.CellSize, _win.Data.RandomizeGeometryBrushRotation, (anchor, radius, angle) => PlaceStamp(e, anchor, radius, angle)))
        {
            _win.Repaint();
            return;
        }

        var down = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
        var drag = e.type == EventType.MouseDrag  && e.button == 0 && !e.alt;
        if (e.type == EventType.MouseUp)
        {
            // Real bug: only the MarkDirty/InvalidateLiveHeightGrid calls
            // below were ever gated on _isPainting — the rest of this block
            // (the full Data.Heights array copy, EditorUtility.SetDirty,
            // a version bump, and a conditional background-runtime
            // refresh) ran on EVERY MouseUp regardless of button or
            // whether a stroke was even active. SC2GameViewEditorUI's own
            // camera-orbit/pan/fly MouseUp handler never calls e.Use(), so
            // the SAME MouseUp that ends a middle/right-mouse camera drag
            // falls through to here too — releasing the camera after any
            // orbit/pan/fly was paying for a full heightmap copy + SetDirty
            // + version bump every single time, on the Terrain tab only
            // (confirmed real: user-reported "editor freezes for a moment
            // whenever a camera drag is released, Terrain tab only").
            // Gating the WHOLE block on _isPainting (true only from a real
            // left-click-no-alt MouseDown, never a camera drag) is exactly
            // what the block's own "not an idle mouse-up" comment already
            // claimed — it just didn't cover the whole block before.
            if (!_isPainting) return;

            // Ad-hoc timing instrumentation (SC2Log) — a quick, no-rebuild-
            // needed alternative to the Unity Profiler for diagnosing which
            // step of a completed stroke's MouseUp costs what. See
            // Logs/SC2TerrainEditor.log (SC2 > Open Log File).
            var swMouseUp = System.Diagnostics.Stopwatch.StartNew();

            // Real Smart Export dirty-
            // tracking for terrain geometry — sculpting has always mutated
            // Data.Heights directly with zero signal to the session
            // (SC2MapSession.PendingDirtyFlags), the one gap that forced
            // Costs/Hfield/Collision2 to stay permanently "dirty" via the
            // TerrainGeometry cascade in SC2ExportDependencyGraph regardless
            // of their own tracking.
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
            var swAfterDirty = swMouseUp.ElapsedMilliseconds;
            _isPainting = false;

            var swHeights = System.Diagnostics.Stopwatch.StartNew();
            // Every surface the stroke touched settles its runtime heights back into its data (the active surface always does:
            // a stroke that never reached anything leaves them unchanged).
            var syncCore = _strokeTouchedCore || Data == _win.Data;
            var syncBackground = _strokeTouchedBackground || Data != _win.Data;
            if (syncCore && _win.Runtime != null) _win.Data.Heights = _win.Runtime.GetHeightsCopy();
            if (syncBackground && _win.BackgroundRuntime != null && _win.Data?.BackgroundTerrainData != null)
                _win.Data.BackgroundTerrainData.Heights = _win.BackgroundRuntime.GetHeightsCopy();
            var swHeightsMs = swHeights.ElapsedMilliseconds;

            var swSetDirty = System.Diagnostics.Stopwatch.StartNew();
            if (syncCore) EditorUtility.SetDirty(_win.Data);
            if (syncBackground && _win.Data?.BackgroundTerrainData != null) EditorUtility.SetDirty(_win.Data.BackgroundTerrainData);
            var swSetDirtyMs = swSetDirty.ElapsedMilliseconds;

            SC2TerrainShell.BumpTerrainGeometryVersion();

            // Re-syncs the background's rim with the core after a completed stroke that touched the background - a no-op when
            // the stroke stayed on the core (RefreshBackgroundRuntime itself checks whether the background even exists/needs
            // rebuilding). Logs its own internal breakdown separately (see its own doc comment).
            if (syncBackground) _win.RefreshBackgroundRuntime();

            SC2Log.Log($"[SC2GeometryMode] MouseUp: MarkDirty+InvalidateLiveHeightGrid={swAfterDirty}ms, GetHeightsCopy={swHeightsMs}ms, EditorUtility.SetDirty={swSetDirtyMs}ms, total={swMouseUp.ElapsedMilliseconds}ms");
            return;
        }
        if (down)
        {
            _isPainting = true;
            _strokeTouchedCore = false;
            _strokeTouchedBackground = false;
            // Roll a fresh stamp rotation for this stroke only,
            // and (re)build the readable-pixel cache the mask sampler needs
            // — see _geoBrushRotationRad/EnsureGeoBrushPixelCache's own doc
            // comments.
            _geoBrushRotationRad = _stamp.Applying ? _stamp.AppliedRotation
                : (_win.Data.UseCustomGeometryBrush && _win.Data.RandomizeGeometryBrushRotation) ? Random.Range(0f, Mathf.PI * 2f) : 0f;
            EnsureGeoBrushPixelCache();
            _flattenPlanes = System.Array.Empty<SC2FlattenPlane>(); // "Original plane" is re-sampled for every stroke
            _lastFlattenDab = null;
            // Heights[] is only re-synced from the runtime on MouseUp (see below),
            // so it already reflects the state before this stroke — one undo
            // checkpoint per stroke, not per drag frame.
            // The surface under the cursor is checkpointed right away; the other one when the stroke first reaches it (see
            // EnsureStrokeCheckpoint).
            EnsureStrokeCheckpoint(Data != _win.Data);
        }
        if (!_isPainting || (!down && !drag)) return;
        var now = EditorApplication.timeSinceStartup;
        // (Flatten places its dabs by distance instead, so the mouse speed and event rate do not matter.)
        if (!down && _tool != Tool.Flatten && now - _lastApply < kMinInterval) return;
        _lastApply = now;

        // A SEPARATE toggle from RandomizeGeometryBrushRotation — that one
        // still rolls once per stroke (mouse-down) and holds it fixed for
        // the drag; this one re-rolls on every single application (down AND
        // every drag frame that passes the throttle above).
        if (!_stamp.Applying && _win.Data.UseCustomGeometryBrush && _win.Data.RandomizeGeometryBrushRotation && _win.Data.RandomizeGeometryBrushRotationContinuous)
            _geoBrushRotationRad = Random.Range(0f, Mathf.PI * 2f);

        // A little wider than the nominal brush radius when notifying
        // Objects/Decals below — the brush's own falloff/hardness shaping
        // and (for Smooth) its neighbor-sampling both reach very slightly
        // past _radius, and it's far cheaper to reproject a couple of
        // harmless extra objects at the edge than to miss one that's now
        // sitting at a stale height.
        var notifyRadius = _radius + Mathf.Max(2f, Data.CellSize * 2f);

        var brushMask = _win.Data.UseCustomGeometryBrush ? _geoBrushPixels : null;

        if (_tool == Tool.SetHeight)
        {
            // SetHeight: directly set vertices within radius to _setHeight,
            // with hardness-controlled falloff. Symmetry always follows the
            // CORE's own setting (_win.Data) — see this class' own header
            // comment — and ActiveRuntime works in LOCAL space, so each
            // world-space symmetric position is translated by
            // ActiveWorldOffset before applying and back before notifying.
            var setHeightPositions = _win.Data.GetSymmetricPositions(_brushPos);
            for (var slot = 0; slot < setHeightPositions.Count; slot++)
            {
                var pos = setHeightPositions[slot];
                GetSymmetricStampTransform(slot, out var mirror, out var swapAxes, out var rotationRad);
                CollectTargets(pos, _radius);
                foreach (var target in _targets)
                {
                    EnsureStrokeCheckpoint(target.Background);
                    target.Runtime.SculptSetHeight(pos - target.Offset, _radius, _strength, _hardness, _setHeight,
                        brushMask, _geoBrushPixelsW, _geoBrushPixelsH, rotationRad, mirror, swapAxes);
                }
                _win.HeightBrushApplied?.Invoke(pos, notifyRadius);
            }
        }
        else if (_tool == Tool.Flatten)
        {
            // Dabs every 10% of the brush diameter along the stroke; the first one where it starts.
            List<Vector3> dabs;
            if (down || !_lastFlattenDab.HasValue)
            {
                dabs = new List<Vector3> { _brushPos };
                _lastFlattenDab = _brushPos;
            }
            else
            {
                var spacing = Mathf.Max(_radius * SC2FlattenMath.SpacingRadiusFraction, Data.CellSize * 0.5f);
                dabs = SC2FlattenMath.SpacedDabs(_lastFlattenDab.Value, _brushPos, spacing, MaxFlattenDabsPerEvent, out var jumped);
                if (dabs.Count > 0) _lastFlattenDab = jumped ? _brushPos : dabs[dabs.Count - 1];
            }

            if (dabs.Count > 0)
            {
                var touchedCore = false;
                var touchedBackground = false;
                foreach (var dab in dabs)
                {
                    var positions = _win.Data.GetSymmetricPositions(dab);
                    if (_flattenPlanes.Length != positions.Count) _flattenPlanes = new SC2FlattenPlane[positions.Count];
                    for (var i = 0; i < positions.Count; i++)
                    {
                        GetSymmetricStampTransform(i, out var mirror, out var swapAxes, out var rotationRad);
                        CollectTargets(positions[i], _radius);
                        // Every surface the dab reaches is flattened to ONE plane (kept in world space, so it stays valid as the
                        // stroke moves from one surface to the other): the locked per-stroke plane when there is one, otherwise
                        // the plane the first surface samples for this dab.
                        var dabPlane = default(SC2FlattenPlane);
                        foreach (var target in _targets)
                        {
                            EnsureStrokeCheckpoint(target.Background);
                            if (target.Background) touchedBackground = true; else touchedCore = true;
                            SC2FlattenPlane local;
                            bool useGiven;
                            if (_flattenLockPlane && _flattenPlanes[i].Valid) { local = PlaneToLocal(_flattenPlanes[i], target); useGiven = true; }
                            else if (dabPlane.Valid) { local = PlaneToLocal(dabPlane, target); useGiven = true; }
                            else { local = default; useGiven = _flattenLockPlane; }
                            var used = default(SC2FlattenPlane);
                            target.Runtime.SculptFlatten(positions[i] - target.Offset, _radius, _strength, _hardness,
                                ref local, useGiven,
                                brushMask, _geoBrushPixelsW, _geoBrushPixelsH, rotationRad, rebuildChunks: false,
                                brushMirror: mirror, brushSwapAxes: swapAxes, onPlane: plane => used = plane);
                            if (!dabPlane.Valid && used.Valid) dabPlane = PlaneToWorld(used, target);
                            if (_flattenLockPlane && !_flattenPlanes[i].Valid && dabPlane.Valid) _flattenPlanes[i] = dabPlane;
                        }
                    }
                }
                // One mesh rebuild and one Objects/Decals notification for the whole swept area.
                var middle = (dabs[0] + dabs[dabs.Count - 1]) * 0.5f;
                var reach = notifyRadius + Vector3.Distance(dabs[0], dabs[dabs.Count - 1]) * 0.5f;
                foreach (var pos in _win.Data.GetSymmetricPositions(middle))
                {
                    if (touchedCore && _win.Runtime != null) _win.Runtime.RebuildChunksAround(pos, reach);
                    if (touchedBackground && _win.BackgroundRuntime != null)
                        _win.BackgroundRuntime.RebuildChunksAround(pos - SC2BackgroundTerrain.GetWorldOffset(_win.Data), reach);
                    _win.HeightBrushApplied?.Invoke(pos, reach);
                }
            }
        }
        else
        {
            var mode = (int)_tool;
            var appliesRadiusCompensation = _strengthScalesWithRadius &&
                (_tool == Tool.Raise || _tool == Tool.Lower || _tool == Tool.Smooth);
            var effectiveStrength = appliesRadiusCompensation
                ? _strength * (_radius / StrengthRadiusReference)
                : _strength;
            if (_stamp.Applying)
            {
                // One dab places the whole stamp: Strength is its alpha, and it moves the ground by _stampHeight at its brightest point.
                if (_tool == Tool.Raise || _tool == Tool.Lower || _tool == Tool.Noise)
                    effectiveStrength = _strength * _stampHeight / SculptHeightPerStrength;
                else if (_tool == Tool.Smooth)
                    effectiveStrength = _strength * 2f / SculptHeightPerStrength;
            }
            var sculptPositions = _win.Data.GetSymmetricPositions(_brushPos);
            for (var slot = 0; slot < sculptPositions.Count; slot++)
            {
                var pos = sculptPositions[slot];
                GetSymmetricStampTransform(slot, out var mirror, out var swapAxes, out var rotationRad);
                // Noise (mode 4) needs its own, UN-combined symmetry rotation (not mixed with the
                // free per-stroke _geoBrushRotationRad rotationRad already carries) plus the
                // symmetry center, to map each vertex back to its slot-0 counterpart before
                // sampling — see SculptBrush's own comment on its Noise case.
                var slotRotationDeg = mode == 4
                    ? SC2SymmetryMath.GetSlotRotationDegrees(_win.Data.Symmetry, _win.Data.RadialCount, slot)
                    : 0f;
                CollectTargets(pos, _radius);
                if (mode == 2 && _targets.Count == 2)
                {
                    // Smooth over the seam is computed over the core's and the background's vertices together, so the vertices on
                    // the seam average the same neighbours from both sides and stay level (see SculptSmoothAcrossSeam).
                    foreach (var target in _targets) EnsureStrokeCheckpoint(target.Background);
                    SC2BackgroundTerrain.GetCoreOriginInBackground(_win.Data, out var originX, out var originZ);
                    SC2TerrainRuntime.SculptSmoothAcrossSeam(_win.Runtime, _win.BackgroundRuntime, originX, originZ, pos,
                        _radius, effectiveStrength, _hardness, brushMask, _geoBrushPixelsW, _geoBrushPixelsH, rotationRad, mirror, swapAxes);
                }
                else
                {
                    foreach (var target in _targets)
                    {
                        EnsureStrokeCheckpoint(target.Background);
                        target.Runtime.SculptBrush(pos - target.Offset, _radius, effectiveStrength, _hardness, mode,
                            brushMask, _geoBrushPixelsW, _geoBrushPixelsH, rotationRad, mirror, swapAxes,
                            _win.Data.SymmetryCenter - target.Offset, slotRotationDeg, target.Offset);
                    }
                }
                _win.HeightBrushApplied?.Invoke(pos, notifyRadius);
            }
        }

        e.Use();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
        _win.Repaint();
    }

    /// <summary>Same per-copy stamp mirror/rotation SC2PaintMode.HandleRnm applies for a custom RNM
    /// brush, generalized to the sculpt tools' own custom brush mask — without it a symmetric copy
    /// on a REFLECTING slot (X/Y/diagonal) sculpts an identical, unmirrored bump instead of one
    /// facing the other way, and a ROTATION-only copy (Radial, or the 180-degree slot of
    /// XY/DiagonalBoth) sculpts unrotated instead of turning to match its own moved position. See
    /// SC2SymmetryMath.GetSlotMirror/GetSlotRotationDegrees for the underlying math.</summary>
    private void GetSymmetricStampTransform(int slot, out Vector2 mirror, out bool swapAxes, out float rotationRad)
    {
        SC2SymmetryMath.GetSlotMirror(_win.Data.Symmetry, slot, out mirror, out swapAxes);
        rotationRad = _geoBrushRotationRad - SC2SymmetryMath.GetSlotRotationDegrees(_win.Data.Symmetry, _win.Data.RadialCount, slot) * Mathf.Deg2Rad;
    }

    // ── Gradient overlay ──────────────────────────────────────────────────────

    public bool HandleKeyboardShortcut(UnityEngine.Event e) => false;
}
