using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using SC2.MapEditor.Document;

/// <summary>
/// Embedded terrain editor for SC2MapEditorWindow.
/// Hosts the same sub-editors as the standalone TerrainEditorWindow
/// (Geometry, Materials, Pairs, Paint, Simplified, Export) but
/// lives inside the unified shell instead of a separate window.
/// </summary>
public class SC2TerrainShell : ScriptableObject
{
    // ── State ─────────────────────────────────────────────────────────────────

    public enum EditorMode { Geometry, Materials, Pairs, Paint, Simplified, Export, Imported, Water, LightingBake, Background }
    public enum PreviewMode { Textured, TexturedRNM, MatColors, PairColors, Slope, HeightGradient }

    private EditorMode  _mode  = EditorMode.Geometry;
    private PreviewMode _prev  = PreviewMode.Textured;
    private float _heightGradientMin = 0f;
    private float _heightGradientMax = 200f;
    // 0=Standard, 1=Steel, 2=Zebra, 3=Tan, 4=Dark, 5=Normal — see
    // TerrainView.shader's MatCapLit/DrawPreviewToolbar's own picker.
    private int _matCapPreset;

    // Bake Lighting result, shown under the button until the
    // next bake or the panel is closed/reopened. Moved here (from SC2PaintMode)
    // when Bake Lighting got its own "Lighting Bake" tab.
    private string _bakeStatus = "";
    // Same idea as _bakeStatus above, for SC2RnmBakeService.BakeBackground
    // (Background tab's own "Bake Background Lighting" button).
    private string _bgBakeStatus = "";

    private SC2TerrainData    _data;
    private SC2TerrainRuntime _runtime;
    private readonly SC2TerrainUndoStack _undoStack = new();
    private SC2TerrainCombinedUndo _combinedUndo;

    /// <summary>Set by SC2GameViewEditorUI.EnsureTerrainShell() right after
    /// this ScriptableObject is created. Null is a valid, supported state
    /// (e.g. the standalone Terrain Editor window, if one still exists,
    /// never wires a session) — CombinedUndo/ExecuteCommand degrade to
    /// legacy-only behavior in that case, they don't throw.</summary>
    public SC2MapSession Session;

    /// <summary>Lazy because Session may be assigned after this
    /// ScriptableObject is constructed (see EnsureTerrainShell) — building
    /// it eagerly in a field initializer would capture a null Session
    /// permanently. Also where _undoStack.CaptureExtra/ApplyExtra get wired
    /// (once, on first access) — see CaptureAuxState/ApplyAuxState below.</summary>
    private SC2TerrainCombinedUndo CombinedUndo
    {
        get
        {
            if (_combinedUndo == null)
            {
                _undoStack.CaptureExtra = CaptureAuxState;
                _undoStack.ApplyExtra = ApplyAuxState;
                _combinedUndo = new SC2TerrainCombinedUndo(_undoStack, Session?.History)
                {
                    CaptureRnmBlend = () => _paintMode?.CaptureActiveBlendState(),
                    ApplyRnmBlend = state => _paintMode?.ApplyActiveBlendState(state),
                };
            }
            return _combinedUndo;
        }
    }

    /// <summary>Symmetry/PreviewMode/active-brush-config all live outside
    /// SC2TerrainData (Symmetry is a data field but wasn't part of the
    /// snapshot before; PreviewMode is shell-only view state; the brush
    /// tool/radius/strength/hardness/etc. are SC2GeometryMode-only) — none
    /// of them were undoable before this pass. Folded into every checkpoint
    /// via SC2TerrainUndoStack.CaptureExtra/ApplyExtra so they revert in the
    /// same combined-undo order as everything else, without SC2TerrainUndoStack
    /// needing to know what "Extra" actually contains.</summary>
    private struct SC2TerrainAuxState
    {
        public SymmetryMode Symmetry;
        public Vector3 SymmetryCenter;
        // RadialCount is a real field of SC2TerrainData.Symmetry's own edit
        // surface (SC2GameViewEditorUI.DrawSymmetryMenu's "Count" slider,
        // right next to Symmetry/SymmetryCenter) but was never captured
        // here alongside them — an Undo/Redo spanning a "Change Symmetry"
        // checkpoint restored Mode/Center correctly but silently left
        // RadialCount at whatever it currently was, never reverted.
        public int RadialCount;
        public PreviewMode Prev;
        public SC2GeometryToolState? Geometry;
        public string MapName;
        public SC2MapRawData RawData;
        public bool? SimplifiedWireframe;
        public SC2ExportPanelState? Export;
    }

    private object CaptureAuxState() => new SC2TerrainAuxState
    {
        Symmetry = _data != null ? _data.Symmetry : SymmetryMode.None,
        SymmetryCenter = _data != null ? _data.SymmetryCenter : Vector3.zero,
        RadialCount = _data != null ? _data.RadialCount : 4,
        Prev = _prev,
        Geometry = _geoMode?.CaptureState(),
        MapName = _data?.MapName,
        RawData = _data?.RawData,
        SimplifiedWireframe = _simplifiedMode?.CaptureState(),
        Export = _exportPanel?.CaptureState(),
    };

    private void ApplyAuxState(object extra)
    {
        if (extra is not SC2TerrainAuxState aux)
            return;

        if (_data != null)
        {
            _data.Symmetry = aux.Symmetry;
            _data.SymmetryCenter = aux.SymmetryCenter;
            _data.RadialCount = aux.RadialCount;
            _data.MapName = aux.MapName;
            _data.RawData = aux.RawData;
        }
        _prev = aux.Prev;
        if (aux.Geometry.HasValue)
            _geoMode?.ApplyState(aux.Geometry.Value);
        if (aux.SimplifiedWireframe.HasValue)
            _simplifiedMode?.ApplyState(aux.SimplifiedWireframe.Value);
        if (aux.Export.HasValue)
            _exportPanel?.ApplyState(aux.Export.Value);
    }
    private GameObject        _previewGO;
    private MeshFilter        _meshFilter;
    private MeshRenderer      _meshRenderer;
    private Mesh              _previewMesh;
    private Material[]        _previewOwnedMaterials;

    // Sub-editors
    private SC2GeometryMode  _geoMode;
    private SC2MaterialsMode _matMode;
    private SC2PairsMode     _pairsMode;
    private SC2PaintMode     _paintMode;
    private SC2SimplifiedPreviewMode _simplifiedMode;
    private SC2ExportPanel   _exportPanel;

    // ── Background terrain editing ─────────────────────────────
    // Geometry/Paint (SC2GeometryMode/SC2PaintMode) are shared, unmodified
    // instances that read/write whichever surface ActiveRuntime/
    // ActiveTerrainData currently resolves to — see those two properties'
    // own doc comments, and SC2GeometryMode's own header comment for the
    // full rationale (this was an explicit user request: tool/radius/
    // strength/etc. selection is ONE state for both surfaces, not two
    // independent copies the way an earlier version of this — and the now-
    // removed SC2BackgroundEditMode class it used for Geometry/Paint —
    // worked). Materials/Pairs are separate instances per surface
    // (_bgMatMode/_bgPairsMode below) since they have no viewport/runtime
    // dependency to share and the user asked for both to just show stacked
    // instead. There is no more manual "Editing Target: Core/Background"
    // toggle at all — Geometry/Paint decide which surface the brush is
    // pointing at from where the cursor actually is
    // (DetermineActiveBrushTarget), and Materials/Pairs show both surfaces'
    // panels unconditionally.
    //
    // Undo for background DATA (Heights/TrianglePairIndex/Materials/Pairs)
    // is a fully separate, simpler stack — background data isn't part of
    // the document (SC2TerrainDocument only persists 3 scalar settings, not
    // the nested SC2TerrainData), so there's nothing for the combined
    // Session-aware undo system to coordinate with here. Its own Extra
    // channel (wired in EnsureBackgroundSubEditors) still bundles the
    // SHARED SC2GeometryMode/SC2PaintMode tool-state/RNM-blend snapshot,
    // same as the core's own CombinedUndo does via CaptureAuxState.
    //
    // Two separate tracking fields below, deliberately not one:
    // _activeBrushTargetIsBackground answers "what should the Geometry/
    // Paint sidebar panel show and which surface should this frame's
    // viewport click go to" (driven by raycasting under the cursor, locked
    // for the duration of an active stroke); _lastEditedTargetIsBackground
    // answers "which undo stack should Ctrl+Z pop from" (driven by
    // RecordUndoCheckpointFor/RecordUndoCheckpoint actually recording a
    // checkpoint) — a user editing the Background section of the always-
    // visible Materials/Pairs panel never touches the viewport at all, so
    // folding these into one field would leave Undo pointed at whatever the
    // brush last happened to hover, not what was actually just edited.
    private bool _activeBrushTargetIsBackground;
    private bool? _strokeTargetIsBackground;
    private bool _lastEditedTargetIsBackground;
    private readonly SC2TerrainUndoStack _bgUndoStack = new();
    private SC2MaterialsMode _bgMatMode;
    private SC2PairsMode     _bgPairsMode;

    /// <summary>Live GPU chunk runtime for the background terrain — a
    /// second SC2TerrainRuntime instance, same class the core's own
    /// _runtime uses, positioned at SC2BackgroundTerrain.GetWorldOffset via
    /// SetWorldOrigin and Init'd with the background's TrianglePairIndex so
    /// its carved-out core footprint actually renders no geometry there
    /// (see SC2TerrainRuntime.Init's own doc comment on holeTriPairIndex).
    /// Replaces the old SC2BackgroundTerrainPreview's periodically-rebuilt
    /// CPU mesh — this one updates live, same as the core.</summary>
    public SC2TerrainRuntime BackgroundRuntime => _bgRuntime;
    private SC2TerrainRuntime _bgRuntime;

    /// <summary>(Re)builds _bgRuntime if it doesn't exist yet, or its
    /// topology no longer matches the background data (e.g. Footprint Size
    /// changed and SC2BackgroundTerrain.EnsureGenerated just silently
    /// regenerated it) — same "grid dimensions changed, tear down and
    /// rebuild" pattern HandleUndoRedo's own ApplyRestoredSnapshot already
    /// uses for the core's _runtime. Cheap no-op on the steady-state path
    /// (background already built, topology unchanged). Also (re)builds the
    /// seam-stitching skirt whenever the runtime itself was just rebuilt —
    /// see SC2BackgroundSeamSkirt's own doc comment for why that one stays
    /// a separate mesh rather than becoming more chunks.</summary>
    /// <summary>Wired once, lazily (same pattern as CombinedUndo's own
    /// lazy getter) — rebuilds the seam skirt whenever a CORE height stamp
    /// (Geometry mode, core-targeted brush) reaches the core's own
    /// boundary, so sculpting the CORE side of the seam stays glued too.
    /// The background's own side already refreshes it directly from
    /// SC2GeometryMode/SC2PaintMode's own MouseUp handler (RefreshBackgroundRuntime)
    /// — this closes the other direction, which had no equivalent hook at
    /// all before (core sculpting never touched anything background-side).</summary>
    private bool _seamSkirtHeightHookWired;

    /// <summary>Set when the seam skirt's very first Rebuild (from
    /// EnsureBackgroundRuntime's own "just created _bgRuntime" branch) ran
    /// before the CORE's own _runtime existed yet — SampleLiveRuntimeHeight
    /// silently returns 0 for a null Runtime, so every core-side seam vertex
    /// sank to world Y=0 on map load, self-correcting only once something
    /// else (any later Rebuild call, e.g. from hovering the background)
    /// happened to fire after Runtime was finally set up. Checked once per
    /// tick (see OnGameViewGUI's own "keep it live" background block) so
    /// this heals itself the first frame Runtime becomes non-null, instead
    /// of needing user interaction to notice.</summary>
    private bool _seamPendingCoreRuntimeReady;

    private void RefreshSeamSkirtIfNearCoreBoundary(Vector3 center, float radius)
    {
        if (_data == null || !_data.BackgroundTerrainEnabled || _bgRuntime == null) return;
        var coreW = _data.GridWidth * _data.CellSize;
        var coreD = _data.GridHeight * _data.CellSize;
        // center is always inside/near the core's own rectangle here (this
        // only ever fires from core-side sculpting) — distance from an
        // interior point to the nearest edge is just the smallest of the
        // four straight-line gaps to each side.
        var distToNearestEdge = Mathf.Min(Mathf.Min(center.x, coreW - center.x), Mathf.Min(center.z, coreD - center.z));
        if (radius < distToNearestEdge) return; // stamp never reached the boundary
        SC2BackgroundSeamSkirt.Rebuild(_data, this);
    }

    private void EnsureBackgroundRuntime()
    {
        if (!_seamSkirtHeightHookWired)
        {
            _seamSkirtHeightHookWired = true;
            HeightBrushApplied += RefreshSeamSkirtIfNearCoreBoundary;
        }
        if (_data == null || !_data.BackgroundTerrainEnabled)
        {
            DestroyBackgroundRuntime();
            return;
        }
        var imported = GetImportedTerrainsForExport?.Invoke();
        if (imported != null && imported.Exists(t => t.IsBackgroundTerrain))
        {
            // A real imported terrain.bdf stands in for the procedural
            // background entirely — same skip condition SC2BackgroundTerrainPreview.Ensure
            // used to check.
            DestroyBackgroundRuntime();
            return;
        }

        var bg = SC2BackgroundTerrain.EnsureGenerated(_data);
        if (bg == null)
        {
            DestroyBackgroundRuntime();
            return;
        }

        if (_bgRuntime != null && (_bgRuntime.GridWidth != bg.GridWidth || _bgRuntime.GridHeight != bg.GridHeight
                || !Mathf.Approximately(_bgRuntime.CellSize, bg.CellSize)))
            DestroyBackgroundRuntime();

        if (_bgRuntime == null)
        {
            _bgRuntime = new SC2TerrainRuntime();
            _bgRuntime.Init(bg.GridWidth, bg.GridHeight, bg.CellSize, 200f, bg.TrianglePairIndex);
            _bgRuntime.SetWorldOrigin(SC2BackgroundTerrain.GetWorldOffset(_data));
            if (bg.Heights != null && bg.Heights.Length > 0)
                _bgRuntime.UploadHeightsCPU(bg.Heights);
            if (bg.TrianglePairIndex != null && bg.TrianglePairIndex.Length > 0)
                _bgRuntime.RestorePairMapFromData(bg);
            _bgRuntime.RestoreBlendRTFromData(bg);
            ApplyPreviewModeToChunksFor(_bgRuntime, bg);
            // See _seamPendingCoreRuntimeReady's own doc comment — this can
            // legitimately run before the core's own _runtime exists yet
            // (e.g. the very first frame after a map load), which would
            // silently sink every core-side seam vertex to Y=0.
            if (Runtime == null) _seamPendingCoreRuntimeReady = true;
            SC2BackgroundSeamSkirt.Rebuild(_data, this);
        }
    }

    /// <summary>Re-syncs _bgRuntime's GPU state from bg's CPU data without
    /// tearing it down — for changes that affect material/tiling/pair
    /// appearance but never the grid topology (Global UV Scale, Simplify
    /// Ratio, a Materials/Pairs edit). Safe to call even if the runtime
    /// isn't built yet (EnsureBackgroundRuntime is called first).</summary>
    internal void RefreshBackgroundRuntime()
    {
        // Ad-hoc timing instrumentation (SC2Log, same pattern
        // SC2PaintMode.HandleAssign's own MouseUp timer already uses) — a
        // quick, no-rebuild-needed alternative to the Unity Profiler for
        // diagnosing which step of a stroke's MouseUp costs what. See
        // Logs/SC2TerrainEditor.log (SC2 > Open Log File).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        EnsureBackgroundRuntime();
        var swAfterEnsure = sw.ElapsedMilliseconds;
        if (_bgRuntime == null || _data?.BackgroundTerrainData == null)
        {
            SC2Log.Log($"[SC2TerrainShell] RefreshBackgroundRuntime: EnsureBackgroundRuntime={swAfterEnsure}ms, no background — total={sw.ElapsedMilliseconds}ms");
            return;
        }
        var bg = _data.BackgroundTerrainData;
        var swApplyStart = System.Diagnostics.Stopwatch.StartNew();
        ApplyPreviewModeToChunksFor(_bgRuntime, bg);
        var swApplyMs = swApplyStart.ElapsedMilliseconds;
        var swSeamStart = System.Diagnostics.Stopwatch.StartNew();
        SC2BackgroundSeamSkirt.Rebuild(_data, this);
        var swSeamMs = swSeamStart.ElapsedMilliseconds;
        SC2Log.Log($"[SC2TerrainShell] RefreshBackgroundRuntime: EnsureBackgroundRuntime={swAfterEnsure}ms, ApplyPreviewModeToChunksFor={swApplyMs}ms, SeamSkirt.Rebuild={swSeamMs}ms, total={sw.ElapsedMilliseconds}ms");
    }

    private void DestroyBackgroundRuntime()
    {
        _bgRuntime?.Dispose();
        _bgRuntime = null;
        SC2BackgroundSeamSkirt.Destroy();
    }

    /// <summary>Pushes a just-Undo/Redo-restored background snapshot into
    /// _bgRuntime in place — mirrors ApplyRestoredSnapshot's own "common
    /// case: grid size unchanged, push straight into the live runtime"
    /// branch for the core. The background's grid dimensions never change
    /// from an Undo/Redo (only Regenerate/Footprint Size do, neither of
    /// which goes through this stack), so there's no grid-mismatch branch
    /// to guard here the way the core's own version needs.</summary>
    private void ApplyRestoredBackgroundSnapshot()
    {
        var bg = _data?.BackgroundTerrainData;
        if (bg == null) return;
        EnsureBackgroundRuntime();
        if (_bgRuntime == null) return;
        _bgRuntime.UploadHeightsCPU(bg.Heights);
        _bgRuntime.RestorePairMapFromData(bg);
        _bgRuntime.RestoreBlendRTFromData(bg);
        ApplyPreviewModeToChunksFor(_bgRuntime, bg);
        SC2BackgroundSeamSkirt.Rebuild(_data, this);
    }

    private void EnsureBackgroundSubEditors()
    {
        EnsureBackgroundRuntime();
        var bg = _data != null ? SC2BackgroundTerrain.EnsureGenerated(_data) : null;
        if (bg == null) return;
        if (_bgMatMode == null || _bgMatMode.Data != bg) _bgMatMode = new SC2MaterialsMode(this, bg);
        if (_bgPairsMode == null || _bgPairsMode.Data != bg) _bgPairsMode = new SC2PairsMode(this, bg);
        if (_bgUndoStack.CaptureExtra == null)
        {
            EnsureSubEditors(); // guarantees _geoMode/_paintMode exist before the closures below capture them
            // Bundles the brush's own SHARED tool state (SC2GeometryMode.
            // CaptureState/ApplyState — the same state the core's own
            // CombinedUndo folds into every checkpoint via CaptureAuxState,
            // now genuinely one object rather than a background-only copy)
            // with an RNM/blend-mask BlendRT snapshot
            // (SC2PaintMode.CaptureActiveBlendState/ApplyActiveBlendState) —
            // the background has no equivalent of the core's own dedicated
            // RNM-blend undo channel (CombinedUndo.RecordRnmBlendCheckpoint),
            // so its own snapshot rides along on this stack's Extra channel
            // instead.
            _bgUndoStack.CaptureExtra = () => (_geoMode.CaptureState(), _paintMode.CaptureActiveBlendState());
            _bgUndoStack.ApplyExtra = extra =>
            {
                if (extra is not ValueTuple<SC2GeometryToolState, object> t) return;
                _geoMode.ApplyState(t.Item1);
                _paintMode.ApplyActiveBlendState(t.Item2);
            };
        }
    }

    private Vector2 _scroll;
    private bool _pairCpuDataStale;
    private bool _previewDirty;
    private static Texture2D _brushRingTexture;
    private static Texture2D _unifiedBoxTexture;
    private static readonly System.Collections.Generic.Dictionary<string, string> NumericFieldText = new();
    /// <summary>Tracks the value DrawCompactSlider last RETURNED per key, so
    /// it can tell "the caller's value changed for a reason other than my
    /// own last frame's interaction" (Undo/Redo, Import, any other external
    /// write to the underlying field) apart from "the caller is just
    /// passing back the same value I gave it last frame" (the normal
    /// steady-state case). See DrawCompactSlider's own doc comment for the
    /// bug this fixes.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, float> LastSyncedValue = new();
    private double _lastPairSync;
    private double _lastSyncTime;
    private const double kPairSyncInterval = 0.5;
    private const double kSyncThrottle = 0.25;
    private const double kPreviewBuildDelay = 0.15;
    // Bumped from 131_072 (256x256 quads) to
    // the next detail step up, 524_288 (512x512 quads, nearest power of two)
    // — 4x the triangle budget, so terrain blocks carry real fine-grained
    // detail instead of the coarser default. See SC2TerrainUndoStack's own
    // doc comment for the resulting (still small) undo-snapshot memory cost.
    private const int kTargetTriCount = 524_288;
    // Scaled through SC2UiScale — same "content width computed too wide
    // once the real scrollbar got bigger" bug class as
    // SC2GameViewEditorUI.PanelScrollbarWidth, just this shell's own
    // parallel content-width reservation (GetUnifiedContentWidth/line 603).
    private static float kPanelScrollbarReserve => SC2UiScale.Pxf(34f);

    // Map size in world units
    private float _initMapSizeWU = 1024f;
    private Texture2D _initHeightmap;
    private float _initHeightScale = SC2TerrainData.MaxHeight;
    private bool _previewBuildQueued;
    private double _previewBuildAt;
    private Texture2D _waterDepthPreviewTex;
    private static int _activeTexturePickerId;
    private static string _activeTexturePickerKey;
    private bool _embeddedGui;

#if !UNITY_EDITOR
    // ---- Runtime (Build & Run) texture picker ---------------------------------
    // EditorGUIUtility.ShowObjectPicker / AssetDatabase browsing do not exist outside
    // the Unity Editor. In a standalone player we replace the native object picker
    // with a lightweight in-GUI popup so texture assignment keeps working identically
    // from the user's point of view (click swatch -> choose texture -> swatch updates).
    // Candidates come from any Resources folder bundled with the build plus any
    // textures already referenced elsewhere on the loaded SC2TerrainData asset.
    private static bool _runtimePickerOpen;
    private static string _runtimePickerKey;
    private static System.Collections.Generic.List<Texture2D> _runtimePickerCandidates;
    private static Vector2 _runtimePickerScroll;
    private static string _runtimePickerSearch = "";
    private static int _runtimePickerSelectedIndex = -1;
    private static double _runtimePickerLastClickTime;
    private static Texture2D _runtimePickerCurrent;
    private static string _runtimePickerPendingKey;
    private static Texture2D _runtimePickerPendingResult;
    private static bool _runtimePickerPendingIsSet;
#endif

    // Callback to repaint the host window
    public System.Action RepaintHost { get; set; }

    /// <summary>
    /// Fired whenever the preview toolbar (Textured/RNM/Mats/Pairs/Slope/
    /// Height) changes. The Textured/RNM/Mats/Pairs/Slope/Height toggle used
    /// to only affect the paintable SC2TerrainData mesh — imported
    /// terrain.bdf objects (SC2GameViewEditorUI._importedTerrains, a
    /// completely separate GameObject/Mesh/Material[] built by
    /// SC2TerrainBdfImporter) were never touched by it. The host subscribes
    /// here to apply an equivalent view to those too.
    /// </summary>
    public System.Action<PreviewMode> OnPreviewModeChanged { get; set; }

    /// <summary>
    /// Draws the "Imported" subtab's content — the imported-terrain.bdf
    /// list lives on the host (SC2GameViewEditorUI._importedTerrains), not
    /// here, so this is a callback the host wires in EnsureTerrainShell(),
    /// invoked with the same content width every other subtab panel uses.
    /// </summary>
    public System.Action<float> DrawImportedTerrainsContent { get; set; }

    /// <summary>Invoked at the top of SaveState, before persistence runs —
    /// same reasoning as DrawImportedTerrainsContent above: imported-terrain
    /// state lives on the host, not here, so SC2GameViewEditorUI wires this
    /// to SyncImportedTerrainsToDocument (EnsureTerrainShell()) so a save
    /// triggered directly from this panel's own "Save Terrain" button (not
    /// just Ctrl+S/File menu, which already called it separately before this
    /// hook existed) still captures the current imported-terrain state into
    /// SC2MapDocument.Terrain.ImportedEntries first.</summary>
    public System.Action PreSaveSync { get; set; }

    /// <summary>
    /// Lets SC2ExportPanel pull the current list of imported terrain.bdf
    /// objects to merge into "Export terrain.bdf" — same host-callback
    /// pattern as DrawImportedTerrainsContent, since the list itself lives
    /// on SC2GameViewEditorUI, not here.
    /// </summary>
    public System.Func<System.Collections.Generic.List<SC2ImportedTerrainExportData>> GetImportedTerrainsForExport { get; set; }

    /// <summary>Lighting Bake &gt; Imported Objects: the imported terrain.bdf
    /// objects with their layers and each layer's target RNM mask (host
    /// callback, same reasoning as GetImportedTerrainsForExport).</summary>
    public System.Func<System.Collections.Generic.List<SC2ImportedRnmObjectInfo>> GetImportedRnmTargets { get; set; }

    /// <summary>(objectId, layerIndex, maskName) — "" clears the target.</summary>
    public System.Action<string, int, string> SetImportedRnmTarget { get; set; }

    /// <summary>Re-applies the baked imported masks to the live imported
    /// materials (after a bake, an undo/redo, or a mask edit).</summary>
    public System.Action RefreshImportedRnmMasks { get; set; }

    /// <summary>Same live scene colliders SC2ObjectsShell's
    /// own GetImportedTerrainColliders already exposes (imported terrain.bdf
    /// GameObjects carry a real MeshCollider, set at import time), wired here
    /// too so any OTHER unified shell that holds a reference to this
    /// SC2TerrainShell (e.g. SC2CostsShell) can raycast against the actual
    /// imported surface instead of only the live analytic heightfield.
    /// Wired once, from SC2GameViewEditorUI.EnsureTerrainShell.</summary>
    public System.Func<System.Collections.Generic.List<Collider>> GetImportedTerrainColliders { get; set; }

    // ── View width tracking ──────────────────────────────────────────────────
    // EditorGUIUtility.currentViewWidth only reflects the enclosing EditorWindow's
    // width when this GUI is drawn from an actual EditorWindow.OnGUI (the standalone
    // Terrain Editor). When embedded in the unified shell, this panel is drawn from a
    // Camera's OnGUI (Game View overlay) instead, and currentViewWidth is a global set
    // by whichever GUIView last ran — it is not scoped to our panel and can differ
    // between the Layout and Repaint passes of the very same frame. That flips
    // width-dependent conditional layout (e.g. compact toggles) mid-frame, which shifts
    // auto-allocated control IDs for everything drawn after it and breaks keyboard focus
    // on fields such as Radius, Height, and Map Name. OnEmbeddedGUI receives the real,
    // stable panel width from the host so this can't happen.
    private float _effectiveViewWidth = -1f;
    private static float s_effectiveViewWidth = -1f;
    public float EffectiveViewWidth => _effectiveViewWidth >= 0f ? _effectiveViewWidth : EditorGUIUtility.currentViewWidth;
    private static float StaticEffectiveViewWidth => s_effectiveViewWidth >= 0f ? s_effectiveViewWidth : EditorGUIUtility.currentViewWidth;

    /// <summary>s_effectiveViewWidth (what DrawUnifiedNote/BeginUnifiedPanelBox
    /// size themselves against, via GetUnifiedContentWidth) was previously only
    /// ever refreshed by OnEmbeddedGUI — i.e. only while the Terrain tab itself
    /// was on screen. Every other embedded shell (Objects, Costs, Collision2,
    /// Environment, Scenario, Export) calling those same static helpers was
    /// sizing against whatever width happened to be cached from the last time
    /// Terrain was drawn, not its own actual panel width — harmless while both
    /// happened to match, but a real overflow-under-the-scrollbar bug once a
    /// tab's content got tall enough to need scrolling at a genuinely different
    /// width than Terrain's. Each embedded shell's own DrawEmbeddedGUI should
    /// call this with its own panelWidth before drawing anything that uses
    /// those helpers.</summary>
    public static void SyncEffectiveViewWidth(float panelWidth) => s_effectiveViewWidth = panelWidth;

    public void Repaint()
    {
        RepaintHost?.Invoke();
    }

    public void Dispose()
    {
        SaveTerrainState(writeAssets: false);
        DisposeSubEditors();
        DestroyPreview();
        SC2WaterPreview.Destroy();
        DestroyWaterDepthPreview();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public SC2TerrainData Data => _data;
    public SC2TerrainRuntime Runtime => _runtime;

    /// <summary>Whichever runtime SC2GeometryMode/SC2PaintMode should
    /// currently read/write — the core's own _runtime, or the background's
    /// _bgRuntime, decided by DetermineActiveBrushTarget/
    /// _activeBrushTargetIsBackground. Those two classes are shared,
    /// unmodified, between both surfaces (per explicit user request that
    /// tool/radius/strength/etc. selection be ONE state, not two
    /// independent copies), so they read this instead of a hardcoded
    /// Runtime now.</summary>
    public SC2TerrainRuntime ActiveRuntime => _activeBrushTargetIsBackground ? _bgRuntime : _runtime;

    /// <summary>Whichever SC2TerrainData SC2GeometryMode/SC2PaintMode
    /// should currently read/write — see ActiveRuntime's own doc
    /// comment.</summary>
    public SC2TerrainData ActiveTerrainData => _activeBrushTargetIsBackground ? _data?.BackgroundTerrainData : _data;

    /// <summary>World-space translation between SC2GeometryMode/
    /// SC2PaintMode's own world-space brush positions and ActiveRuntime's
    /// local grid space — zero for the core (which always sits at world
    /// origin), so every call site can apply this unconditionally rather
    /// than branching on which surface is active.</summary>
    public Vector3 ActiveWorldOffset => _activeBrushTargetIsBackground ? SC2BackgroundTerrain.GetWorldOffset(_data) : Vector3.zero;

    public EditorMode Mode => _mode;
    public PreviewMode Prev => _prev;
    public int SelPair => _pairsMode?.SelectedPairIdx ?? -1;

    public void SetMode(EditorMode mode)
    {
        if (_mode == mode)
            return;

        _mode = mode;
        if (_data != null && _data.IsInitialized)
            OnModeChanged();
        Repaint();
    }

    // ── Init / Cleanup ───────────────────────────────────────────────────────

    // Shown next to "Terrain Data" so a mismatch between what the Editor is
    // editing and what a standalone build actually starts with is visible
    // instead of silent. Both the Editor and a build now load the shared
    // SC2TerrainData asset from Resources (see SC2GameViewEditorUI
    // .SharedTerrainDataResourceName), so they should always match — this label
    // surfaces which asset that actually is, and flags the "not created yet"
    // state rather than silently inventing empty data.
    private string _dataSourceLabel = "";
    public string DataSourceLabel => _dataSourceLabel;

    private void UpdateDataSourceLabel(SC2TerrainData data)
    {
        if (data == null) { _dataSourceLabel = ""; return; }
#if UNITY_EDITOR
        var path = AssetDatabase.GetAssetPath(data);
        _dataSourceLabel = !string.IsNullOrEmpty(path)
            ? $"Source: project asset ({path})"
            : "Source: unsaved in-memory object (not a project asset) — Save/Export will not persist this to disk as a .asset";
#else
        _dataSourceLabel =
            $"Source: runtime object reference (source not tagged by loader).\n" +
            $"Loaded object: '{data.name}', MapName='{data.MapName}'.\n" +
            "This label is not proof of a LocalLow file load.";
#endif
    }

    public void SetData(SC2TerrainData data, string sourceLabel = null)
    {
        if (_data == data)
        {
            if (!string.IsNullOrEmpty(sourceLabel))
                _dataSourceLabel = sourceLabel;
            return;
        }
        SaveState(writeAssets: false);
        // SC2GameAssetScenePreview batches every placed prop/unit's live 3D
        // preview in static dictionaries keyed by numeric object Id
        // (SC2ObjProp.Id / SC2ObjUnitNode.Id) - never cleared anywhere until
        // now. Switching to a genuinely different SC2TerrainData (a real
        // map load, an Import Map completion, or picking a different asset)
        // left every previous map's preview instances resident; since a
        // freshly loaded/imported map's own object Ids very plausibly
        // collide with the previous map's (both commonly start numbering
        // from 1), SyncMissingAssetPreviews' HasPropPreview/HasUnitPreview
        // "already has a preview, skip" check could report true for the
        // WRONG, stale leftover entry - silently preventing the new map's
        // real prop/unit from ever getting its own preview created. This is
        // exactly the shape of bug a manual "Reindex" in the Browse dialog
        // used to appear to fix (Reindex's real job is unrelated - it just
        // rebuilds the blueprint catalog - but it happened to also trigger
        // enough other state changes that it papered over this one) and
        // stopped fixing once the real culprit was something else. Clear
        // unconditionally here, at the one real "terrain data identity
        // changed" choke point already guarded above (`if (_data == data)
        // return`), not per-frame.
        SC2GameAssetScenePreview.ClearAll();
        SC2ObjectMarkerRenderer.ClearAll();
        _data = data;
        ReresolveMaterialTextures(data);
        if (!string.IsNullOrEmpty(sourceLabel))
            _dataSourceLabel = sourceLabel;
        else
            UpdateDataSourceLabel(data);
        CombinedUndo.Reset();
        // Sync Collision2's document-side VerticalOffset from the freshly-
        // loaded data HERE (at the actual load/create moment), not only
        // whenever SC2Collision2Shell's own tab next happens to be drawn.
        // Without this, exporting a newly-loaded terrain without ever
        // opening the Collision2 tab this session would read whatever
        // stale value the document was last left at (a previous terrain's
        // offset, or the field's bare default) instead of this terrain's
        // real Collision2HeightOffset.
        if (Session?.Document != null && data != null)
            Session.Document.Collision2.Generation.VerticalOffset = data.Collision2HeightOffset;
        // Same reasoning, extended to Environment and Scenario: each of these previously only synced
        // into the document when its OWN shell's tab happened to be drawn
        // this session (SC2EnvironmentShell.Load()/SC2ScenarioShell.
        // LoadFromNewData()) — doing it here too means Export can rely on
        // the document being current regardless of which tabs the user
        // actually visited.
        if (Session?.Document != null && data != null)
        {
            data.EnsureEnvironmentHeader();
            var env = Session.Document.Environment;
            System.Array.Copy(data.EnvironmentHeader, env.WorkingHeader, SC2.MapEditor.Document.SC2EnvironmentDocument.HeaderSize);
            env.EnvironmentMap = data.EnvironmentMap;
            env.SkyMap = data.SkyMap;
            env.TexturePath3 = data.TexturePath3;
            env.ColorCorrectionCube = data.ColorCorrectionCube;
            env.FogOfWarCorrectionCube = data.FogOfWarCorrectionCube;

            Session.Document.Scenario.KnownFields.Name = data.MapName;
            // A different map must not inherit the previous one's lobby
            // minimap positions; a load/import assigns the real ones right
            // after this (LoadFromDisk / SC2MapImportOrchestrator).
            Session.Document.Scenario.LobbyStartPositions = new System.Collections.Generic.List<SC2.MapEditor.Document.SC2LobbyStartPosition>();
            // Same for the imported-layer RNM masks: they belong to one map.
            Session.Document.Terrain.ImportedRnm = new SC2.MapEditor.Document.SC2RnmDocument();
        }
        // SchedulePreviewBuild (not an immediate DisposeSubEditors/DestroyPreview
        // + next-frame EnsurePreview) — same reasoning as "Create flat terrain"/
        // "Import Heightmap" below: RebuildDiffuseArrays reads each Diffuse
        // texture's pixels via Graphics.Blit/ReadPixels right after
        // SC2RuntimeTerrainPersistence.TryLoad may have just forced potentially
        // thousands of textures to load via Resources.LoadAll — rebuilding on
        // the very next frame risked reading texture data before it had fully
        // settled, baking a dark/stale copy into the Texture2DArray that only
        // a later, unrelated rebuild (e.g. touching a material in the picker)
        // would ever correct. The 150ms delay here is the same margin already
        // trusted for that exact class of "let things settle first" concern.
        SchedulePreviewBuild();
        Repaint();
        // Lets sibling shells this class has no direct reference to (e.g.
        // SC2ObjectsShell, owned alongside this one by SC2GameViewEditorUI)
        // sync THEIR OWN document-mirrored state at the exact load/create
        // moment too, instead of only whenever their own tab next happens to
        // be drawn — same reasoning as the
        // Collision2/Environment/Scenario sync above, just for a shell this
        // class can't reach into directly.
        DataChanged?.Invoke(data);
    }

    /// <summary>Diffuse/Normal/Mask textures picked from a
    /// Custom import or the game archive resolve to an in-memory Texture2D
    /// with no real project asset/GUID — Unity's serializer silently drops
    /// such a reference across a save/reload, leaving the field null even
    /// though the material's own DiffuseOverridePath/NormalOverridePath/
    /// MaskOverridePath (a plain string, always serializes correctly) still
    /// points at the real source. SC2TerrainData.cs can't call
    /// SC2TerrainMaterialFactory itself (SC2.Core.asmdef is a deliberately
    /// minimal, standalone-testable assembly that the main Scripts/ code —
    /// where SC2TerrainMaterialFactory lives — depends on, not the other way
    /// around), so this lives here instead, at the same real "terrain data
    /// identity changed" choke point the Collision2/Environment/Scenario
    /// syncs right above already use. A cheap no-op for any material whose
    /// texture already resolved (e.g. a real Resources-backed asset, which
    /// Unity's own serializer already handles correctly on its own).</summary>
    private static void ReresolveMaterialTextures(SC2TerrainData data)
    {
        if (data?.Materials == null) return;
        foreach (var mat in data.Materials)
        {
            if (mat == null) continue;
            if (mat.Diffuse == null && !string.IsNullOrEmpty(mat.DiffuseOverridePath))
                mat.Diffuse = SC2TerrainMaterialFactory.ResolveTextureByPathOrName(mat.Name, mat.DiffuseOverridePath, mat.Name, "Diffuse");
            if (mat.Normal == null && !string.IsNullOrEmpty(mat.NormalOverridePath))
                mat.Normal = SC2TerrainMaterialFactory.ResolveTextureByPathOrName(mat.Name, mat.NormalOverridePath, mat.Name, "Normal");
            if (mat.Mask == null && !string.IsNullOrEmpty(mat.MaskOverridePath))
                mat.Mask = SC2TerrainMaterialFactory.ResolveTextureByPathOrName(mat.Name, mat.MaskOverridePath, mat.Name, "Mask");
        }
    }

    /// <summary>See SetData's own doc comment on why this exists.</summary>
    public System.Action<SC2TerrainData> DataChanged;

    /// <summary>Fired once per brush stamp application in Geometry mode
    /// (Raise/Lower/Smooth/Flatten/SetHeight — one call per SculptBrush/
    /// SculptSetHeight, including once per symmetry mirror), not once per
    /// whole stroke — hands (world center, radius) of the area whose
    /// heights just changed. SC2ObjectsShell/SC2DecalsShell subscribe to
    /// live-reproject their own AnchorToTerrain/AlignToSurface objects
    /// sitting in that circle, closing the same "floating/clipping until
    /// manually moved" gap ImportObjectsFromPath's own doc comment already
    /// describes and fixes for the one-time post-import case — this is the
    /// same fix, applied continuously during live sculpting instead of only
    /// once right after an import.</summary>
    public System.Action<Vector3, float> HeightBrushApplied;

    /// <summary>Fired once for the OLD and once for the NEW world region (X/Z rectangle) of an imported
    /// terrain object whose transform was edited (or restored by Undo/Redo): the ground under that region
    /// changed, so anchored Objects and aligned Decals there must re-sample it — the same job
    /// HeightBrushApplied does for sculpting, which imported geometry never triggers.</summary>
    public System.Action<Bounds> ImportedTerrainMoved;

    /// <summary>Call after an imported terrain object's transform changed. Drops the cached height grid
    /// and lets the colliders pick up the new transform (a raycast right after a move would otherwise
    /// still hit the old position) before subscribers re-sample the ground.</summary>
    public void NotifyImportedTerrainMoved(Bounds before, Bounds after)
    {
        InvalidateLiveHeightGrid();
        Physics.SyncTransforms();
        BumpTerrainGeometryVersion();
        ImportedTerrainMoved?.Invoke(before);
        ImportedTerrainMoved?.Invoke(after);
    }

    // ── Undo / Redo ──────────────────────────────────────────────────────────
    // Custom stack instead of UnityEditor.Undo: see SC2TerrainUndoStack for why
    // (it's a no-op outside the Editor, and never covered brush sculpting even
    // inside the Editor). Callers record a checkpoint immediately BEFORE they
    // mutate _data, then this shell's Undo()/Redo() swap the data back/forward.
    //
    // As of the Terrain UI cutover, a second source of undoable edits exists
    // (ISC2DocumentCommand via Session.History, currently just Material/Pair
    // rename — see SC2MaterialsPairsMode) — SC2TerrainCombinedUndo (CombinedUndo
    // above) is what makes one Undo/Redo button walk back through both in the
    // order edits actually happened, per the user's explicit choice to unify
    // rather than replace SC2TerrainUndoStack.

    // Must also check _bgUndoStack, not just CombinedUndo — a session that
    // only ever edited the background (never the core) has NOTHING in
    // CombinedUndo, so checking that alone made the global Ctrl+Z
    // dispatcher (SC2GameViewEditorUI.UndoTerrainEdit) never even consider
    // Terrain a candidate, reporting "Nothing to undo" despite _bgUndoStack
    // genuinely holding a real entry. Confirmed directly: "did a sculpt [on
    // the background], pressed Ctrl+Z, it says Nothing to undo."
    public bool CanUndo => _data != null && (CombinedUndo.CanUndo || _bgUndoStack.CanUndo);
    public bool CanRedo => _data != null && (CombinedUndo.CanRedo || _bgUndoStack.CanRedo);

    /// <summary>See SC2TerrainCombinedUndo.LastActionTimestamp and
    /// SC2TerrainUndoStack.LastActionTimestamp's own doc comment for why
    /// _bgUndoStack needs comparing here too — same reasoning as CanUndo
    /// just above.</summary>
    public long LastActionTimestamp => _data != null ? System.Math.Max(CombinedUndo.LastActionTimestamp, _bgUndoStack.LastActionTimestamp) : 0;

    /// <summary>See SC2TerrainCombinedUndo.LastRedoTimestamp — the global
    /// Redo dispatcher (SC2GameViewEditorUI.RedoTerrainEdit) must compare
    /// THIS, not LastActionTimestamp, when deciding which shell's Redo() to
    /// call; see that property's own doc comment for why the two differ.</summary>
    public long LastRedoTimestamp => _data != null ? System.Math.Max(CombinedUndo.LastRedoTimestamp, _bgUndoStack.LastRedoTimestamp) : 0;

    public void RecordUndoCheckpoint(string label)
    {
        _lastEditedTargetIsBackground = false;
        CombinedUndo.RecordLegacyCheckpoint(_data, label);
    }

    /// <summary>Data-aware checkpoint dispatcher for mode classes shared
    /// between the core and the background terrain (SC2GeometryMode/
    /// SC2PaintMode/SC2MaterialsMode/SC2PairsMode — see SC2TerrainShell's own
    /// "Background terrain editing" field-group comment). Routes by WHICH
    /// SC2TerrainData the edit is actually for (also updating
    /// _lastEditedTargetIsBackground, which Undo()/Redo() consult to decide
    /// which stack Ctrl+Z should pop — see that field's own doc comment for
    /// why it's tracked separately from the viewport brush's own target).
    /// Background gets a fully separate, simpler undo stack: that
    /// data isn't part of the document (SC2TerrainDocument only persists 3
    /// scalar settings, never the nested SC2TerrainData), so there's
    /// nothing for the Session-aware combined undo system to coordinate
    /// with, and mixing a background snapshot into the CORE's own undo
    /// stack would let a core Undo try to Apply a background snapshot onto
    /// the core's SC2TerrainData — wrong data entirely.</summary>
    public void RecordUndoCheckpointFor(SC2TerrainData data, string label)
    {
        if (data != null && _data != null && data == _data.BackgroundTerrainData)
        {
            _lastEditedTargetIsBackground = true;
            _bgUndoStack.RecordBeforeChange(data, label);
            return;
        }
        RecordUndoCheckpoint(label);
    }

    /// <summary>Call BEFORE painting an RNM blend stroke (SC2PaintMode's
    /// "Blend" sub-mode) — see SC2TerrainCombinedUndo's own doc comment for
    /// why this is a separate channel from RecordUndoCheckpoint.</summary>
    public void RecordRnmBlendCheckpoint() => CombinedUndo.RecordRnmBlendCheckpoint();

    /// <summary>Executes a real ISC2DocumentCommand (e.g. a Material/Pair
    /// rename) and records it in the same combined undo order as legacy
    /// checkpoints. Returns false without effect if no Session is wired.</summary>
    public bool ExecuteCommand(ISC2DocumentCommand command)
    {
        if (Session == null)
            return false;
        return CombinedUndo.ExecuteCommand(Session.Document, command);
    }

    /// <summary>Materials/Pairs have no working-copy of their own (unlike
    /// Environment's _m/_original) — SC2MaterialsMode/SC2PairsMode read and
    /// write SC2TerrainData.Materials[i]/Pairs[i] directly every frame. After
    /// a command-sourced Undo/Redo changes Session.Document.Terrain instead,
    /// this copies the new Name values back onto the live objects the GUI
    /// actually displays, keyed by the same stable Id both sides share.</summary>
    private void SyncMaterialsAndPairNamesFromDocument()
    {
        if (_data == null || Session?.Document?.Terrain == null)
            return;

        if (_data.Materials != null)
            foreach (var m in _data.Materials)
            {
                var doc = Session.Document.Terrain.FindMaterial(m.Id);
                if (doc != null)
                    m.Name = doc.Name;
            }

        if (_data.Pairs != null)
            foreach (var p in _data.Pairs)
            {
                var doc = Session.Document.Terrain.FindPair(p.Id);
                if (doc != null)
                    p.Name = doc.Name;
            }

        // An undo/redo may have changed the imported RNM masks too.
        RefreshImportedRnmMasks?.Invoke();
    }

    public bool Undo()
    {
        // _lastEditedTargetIsBackground only decides which stack to try
        // FIRST (whichever surface was edited most recently) — it must NOT
        // stay a hard gate once that stack runs out. It's never reset back
        // to false just because a background Undo exhausted _bgUndoStack
        // (only a fresh CORE edit does that, via RecordUndoCheckpoint/
        // RecordUndoCheckpointFor's core branch), so without this fallthrough,
        // painting background even once after painting core would make
        // Ctrl+Z permanently unable to reach the core's own CombinedUndo
        // history the moment _bgUndoStack ran dry — reported directly:
        // "Ctrl+Z doesn't work on core" after painting core then background.
        if (_lastEditedTargetIsBackground && _data != null && _data.BackgroundTerrainData != null
            && _bgUndoStack.Undo(_data.BackgroundTerrainData))
        {
            ApplyRestoredBackgroundSnapshot();
            MarkDirtyAndRepaint();
            return true;
        }

        if (_data == null || !CombinedUndo.Undo(_data, Session?.Document, SyncMaterialsAndPairNamesFromDocument))
            return false;

        ApplyRestoredSnapshot();
        return true;
    }

    public bool Redo()
    {
        // See Undo()'s own comment on why this falls through instead of
        // returning false the moment _bgUndoStack has nothing left to redo.
        if (_lastEditedTargetIsBackground && _data != null && _data.BackgroundTerrainData != null
            && _bgUndoStack.Redo(_data.BackgroundTerrainData))
        {
            ApplyRestoredBackgroundSnapshot();
            MarkDirtyAndRepaint();
            return true;
        }

        if (_data == null || !CombinedUndo.Redo(_data, Session?.Document, SyncMaterialsAndPairNamesFromDocument))
            return false;

        ApplyRestoredSnapshot();
        return true;
    }

    /// <summary>
    /// Pushes the just-restored snapshot into the live runtime in place.
    /// Deliberately does NOT call DisposeSubEditors()/DestroyPreview() (an
    /// earlier version did): that tears down the runtime's RenderTextures and
    /// chunk GameObjects synchronously and rebuilds them a frame later, which
    /// showed up as a one-frame blank/flicker every time Undo or Redo ran.
    /// Sculpting/painting never destroy the runtime for the same reason — they
    /// push new data into it (UploadHeightsCPU/RestorePairMapFromData) and let
    /// existing chunks update in place, which this now mirrors. Sub-editors
    /// keep their same Data reference (only its internal arrays/lists changed),
    /// so they don't need to be recreated either.
    /// </summary>
    private void ApplyRestoredSnapshot()
    {
        // Discard any async GPU pair-map readback already in flight from
        // before this Undo/Redo — see SC2PaintMode.InvalidatePendingPairSync's
        // own doc comment for the race this closes (painting a stroke then
        // immediately Ctrl+Z could otherwise get silently reapplied a frame
        // or two later by that stale readback completing).
        _paintMode?.InvalidatePendingPairSync();

        _data.InvalidateGeometryCache();
        foreach (var pair in _data.Pairs)
            pair?.InvalidateTriCount();

        if (!_data.IsInitialized)
        {
            // Undo went all the way back past terrain creation (e.g. undoing
            // "Init"/"Import HM" on a brand new terrain) — DrawEditorBody
            // switches to DrawInitPanel for this, which doesn't touch the
            // runtime at all, so there is nothing to push data into. Tear
            // down cleanly instead of leaving a stale mesh/RT alive that
            // nothing is driving anymore.
            DisposeSubEditors();
            DestroyPreview();
        }
        else if (_runtime != null && _runtime.GridWidth == _data.GridWidth && _runtime.GridHeight == _data.GridHeight)
        {
            // Common case (sculpting, painting, pair/material edits): grid
            // size never changes, so push the restored data straight into the
            // live runtime instead of destroying and rebuilding it — that
            // rebuild is what caused the one-frame blank/flicker before.
            _runtime.UploadHeightsCPU(_data.Heights);
            _runtime.RestorePairMapFromData(_data);
            // RestorePairMapFromData only pushes TrianglePairIndex back to the
            // GPU PairMapRT — the DERIVED preview textures built from it (the
            // Pairs preview's colour RT via RepaintPairColorRT, or the
            // per-pair material/shader setup for Textured/MatColors/etc.) are
            // separate render state this method never touched, so Undo/Redo
            // looked like it silently did nothing while the "Pairs" preview
            // mode was active — the CPU data (and every OTHER preview mode)
            // was already correct, confirmed by switching preview modes away
            // and back forcing exactly this same rebuild. This is the same
            // refresh ApplyPreviewModeToChunks does whenever the preview mode
            // itself changes; Undo/Redo needs it too since they can change
            // the same underlying pair/material data a mode switch doesn't.
            ApplyPreviewModeToChunks();
        }
        else
        {
            // Grid dimensions changed (shouldn't normally happen — the grid
            // resolution is fixed — but guard against it rather than upload
            // mismatched-size data into the runtime).
            DisposeSubEditors();
            DestroyPreview();
        }

        MarkDirtyAndRepaint();
    }

    public void OnGUI()
    {
        _effectiveViewWidth = -1f; // standalone window: EditorGUIUtility.currentViewWidth is valid here
        s_effectiveViewWidth = -1f;
        _embeddedGui = false;
        DrawModeToolbar();
        DrawPreviewToolbar();
        EditorGUILayout.Space(2);
        DrawEditorBody();
    }

    public void OnEmbeddedGUI(float panelWidth)
    {
        // The host passes the available left-panel content width. Keep that as
        // the outer shell width; inner scroll content reserves room for the
        // vertical scrollbar so it does not cover controls on the right edge.
        _effectiveViewWidth = panelWidth;
        s_effectiveViewWidth = _effectiveViewWidth;
        _embeddedGui = true;
        var outerWidth = Mathf.Max(1f, panelWidth);
        EditorGUILayout.BeginVertical(GUILayout.Width(outerWidth), GUILayout.MaxWidth(outerWidth));
        DrawPreviewToolbar();
        EditorGUILayout.Space(2);
        DrawEditorBody();
        EditorGUILayout.EndVertical();
        _embeddedGui = false;
    }

    private void DrawEditorBody()
    {
        var previousLabelWidth = EditorGUIUtility.labelWidth;
        var previousBackground = GUI.backgroundColor;
        var previousContent = GUI.contentColor;
        var previousColor = GUI.color;
        var outerWidth = Mathf.Max(1f, EffectiveViewWidth);
        var contentWidth = Mathf.Max(1f, outerWidth - kPanelScrollbarReserve);

        EditorGUIUtility.labelWidth = Mathf.Clamp(contentWidth * 0.38f, 68f, 104f);
        // GUI.backgroundColor/contentColor multiply whatever texture/textColor
        // a style already uses. SC2UnifiedSkin's textures and every label style
        // in this panel already bake in the correct final colors, so tinting on
        // top of that (contentColor here was ~0.90-0.95 per channel, applied to
        // EVERY label — including already-dim "secondary"/note text at ~0.56-0.74
        // per channel) compounded into text that read as blending into the dark
        // background across every subtab. Same bug shape as the backgroundColor
        // one above — leave text/backgrounds at their own defined color, untinted.
        GUI.backgroundColor = Color.white;
        GUI.contentColor = Color.white;
        GUI.color = Color.white;

        // ObjectField OUTSIDE ScrollView — otherwise focus is lost on every repaint
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginHorizontal(GUILayout.Width(contentWidth), GUILayout.MaxWidth(contentWidth));
        var nextData = (SC2TerrainData)EditorGUILayout.ObjectField("Terrain Data", _data, typeof(SC2TerrainData), false, GUILayout.Width(contentWidth), GUILayout.MaxWidth(contentWidth));
        EditorGUILayout.EndHorizontal();
        if (EditorGUI.EndChangeCheck())
        {
            SaveState(writeAssets: false);
            SetData(nextData);
        }
        if (!string.IsNullOrEmpty(_dataSourceLabel))
            DrawUnifiedNote(_dataSourceLabel);

        // Imported terrain.bdf objects have no SC2TerrainData at all (they're
        // a separate GameObject/Mesh/Material[] built by SC2TerrainBdfImporter,
        // owned by the host — see SC2GameViewEditorUI), so this tab skips the
        // _data existence/initialization gates every other subtab needs —
        // but it must still go through the SAME BeginScrollView/BeginVertical
        // wrapping below as every other subtab. An earlier version returned
        // before reaching that wrapping entirely, which is exactly why the
        // panel didn't scroll and its content overflowed the column's width
        // (nothing was constraining it to contentWidth without BeginVertical).
        if (_mode != EditorMode.Imported)
        {
            if (_data == null)
            {
                DrawNewDataPanel();
                RestorePanelGuiState(previousLabelWidth, previousBackground, previousContent, previousColor);
                return;
            }

            if (!_data.IsInitialized)
            {
                DrawInitPanel();
                RestorePanelGuiState(previousLabelWidth, previousBackground, previousContent, previousColor);
                return;
            }

            EnsureSubEditors();
            if (_data.BackgroundTerrainEnabled) EnsureBackgroundSubEditors();
            DrawSaveToolbar();
            EditorGUILayout.Space(4);
        }

        _scroll.x = 0f;
        _scroll = GUILayout.BeginScrollView(
            _scroll,
            false,
            true,
            GUIStyle.none,
            GUI.skin.verticalScrollbar,
            GUILayout.Width(outerWidth),
            GUILayout.MaxWidth(outerWidth));
        _scroll.x = 0f;
        EditorGUILayout.BeginVertical(GUILayout.Width(contentWidth), GUILayout.MaxWidth(contentWidth));
        // Geometry/Paint are a single brush now — no more manual "Editing
        // Target" toggle — so they route by _activeBrushTargetIsBackground
        // Geometry/Paint are shared instances that internally read whichever
        // surface is active (ActiveTerrainData/ActiveRuntime, kept in sync
        // with the viewport by DetermineActiveBrushTarget, called from
        // OnGameViewGUI) — no branching needed here any more. Materials/
        // Pairs have no viewport brush of their own to route by, so they
        // just show both surfaces' panels stacked.
        switch (_mode)
        {
            case EditorMode.Geometry:
                _geoMode.DrawPanel();
                break;
            case EditorMode.Materials:
                DrawUnifiedSectionLabel("Core");
                _matMode.DrawPanel();
                if (_data.BackgroundTerrainEnabled && _bgMatMode != null)
                {
                    EditorGUILayout.Space(10);
                    DrawUnifiedSectionLabel("Background");
                    _bgMatMode.DrawPanel();
                }
                break;
            case EditorMode.Imported:  DrawImportedTerrainsContent?.Invoke(contentWidth); break;
            case EditorMode.Pairs:
                DrawUnifiedSectionLabel("Core");
                _pairsMode.DrawPanel();
                if (_data.BackgroundTerrainEnabled && _bgPairsMode != null)
                {
                    EditorGUILayout.Space(10);
                    DrawUnifiedSectionLabel("Background");
                    _bgPairsMode.DrawPanel();
                }
                break;
            case EditorMode.Paint:
                _paintMode.DrawPanel();
                break;
            case EditorMode.Simplified: _simplifiedMode.DrawPanel(); break;
            case EditorMode.Export:    _exportPanel.DrawPanel(); break;
            case EditorMode.Water:     DrawWaterPanel(); break;
            case EditorMode.LightingBake: DrawLightingBakePanel(); break;
            case EditorMode.Background: DrawBackgroundPanel(); break;
        }
        EditorGUILayout.EndVertical();
        GUILayout.EndScrollView();
        _scroll.x = 0f;
        // _data can legitimately be null here for the Imported tab (it has
        // no SC2TerrainData at all) — guard against EditorUtility.SetDirty(null).
        if (GUI.changed && _data != null) EditorUtility.SetDirty(_data);

        RestorePanelGuiState(previousLabelWidth, previousBackground, previousContent, previousColor);
    }

    /// <summary>Terrain-geometry side of the water plane — a
    /// peer tab to Geometry, not a duplicate of Environment's own Water
    /// tab. Covers only what actually concerns the TERRAIN mesh/BSP: the
    /// generated plane's real-world size, and whether this map is using a
    /// synthetic plane or is bound to real water geometry an imported
    /// terrain.bdf already supplies. Every other water SHADER parameter
    /// (color, waves, elevation, depth falloff, ...) stays on Environment >
    /// Water, since those apply identically regardless of where the
    /// geometry itself came from.</summary>
    private void DrawWaterPanel()
    {
        if (_data == null)
        {
            DrawUnifiedNote("Create/load a terrain first — the water plane is generated relative to it.");
            return;
        }

        DrawUnifiedSectionLabel("Water Plane");

        var imported = GetImportedTerrainsForExport?.Invoke();
        var importedHasWater = imported != null && imported.Exists(
            t => t.Model.Materials.Exists(SC2HfieldProjectionService.IsWaterMaterial));

        if (importedHasWater)
        {
            DrawUnifiedNote("An imported terrain.bdf already carries real water geometry — its material is kept bound to the live WaterElevation/Sun/Color values instead of generating a separate synthetic plane on top of it. The Plane Size setting below has no effect while this is the case.");
        }
        else
        {
            var info = SC2TerrainExporter.BuildInfoModel(_data, Session, quiet: true);
            if (info.WaterEnabled == 0)
                DrawUnifiedNote("WaterEnabled is off (Environment > Water) — no plane is generated. Turn it on there to see one here.");
            else
                DrawUnifiedNote("A synthetic water plane is being generated for this from-scratch map (32x32 subdivided grid, real BSP tree) — see Plane Size below.");

            var newSize = Mathf.RoundToInt(DrawCompactSlider("Plane Size", _data.WaterPlaneSize, 128f, 8192f,
                onValueChangeStart: () => RecordUndoCheckpoint("Change Water Plane Size")));
            if (newSize != _data.WaterPlaneSize)
                _data.WaterPlaneSize = newSize;
            DrawUnifiedNote("World-space size (X and Z) of the generated plane — independent of the Costs/Hfield grid size, so the visible water surface can extend past, or stop short of, the playable footprint. Default 4096.");
        }

        EditorGUILayout.Space(6);
        DrawUnifiedNote("Color, waves, elevation and depth falloff live in Environment > Water — this tab only covers the plane's own geometry.");

        DrawWaterTexturePaths();
    }

    private static readonly string[] kWaterTextureSlotLabels =
    {
        "Diffuse1 (foam)", "Diffuse2 (foam)",
        "Normal1 (waves)", "Normal2 (waves)", "Normal3 (waves)", "Normal4 (waves)",
        "Mask1 (waterDepth — blank = auto from Map Name)", "Mask2",
        "Texture5 (wave animation)", "Texture6",
        "RNM Volume", "Environment", "Texture8",
    };

    /// <summary>The from-scratch water plane's (SC2WaterPlane)
    /// exported "Water" material texture paths — stored as plain path
    /// strings (SC2TerrainData.WaterTexturePaths), since that's what
    /// actually gets written into terrain.bdf, but shown here as real
    /// texture-preview pickers (same DrawTexturePicker/SC2TextureBrowser
    /// widget as ordinary materials' Diffuse/Normal/Mask slots) instead of
    /// plain text fields. Each slot's current path string is resolved to a
    /// live Texture2D for display via SC2TerrainMaterialFactory.
    /// LoadTexturePublic (silent on a miss); a newly-picked texture is
    /// converted back to a real game-path string via SC2TextureCatalog
    /// (mirroring DrawTextureField/BuildTextureReference's same round-trip
    /// for ordinary materials), not stored as a live Texture2D reference —
    /// the export format only ever wants the path string, real texture data
    /// always comes from the user's own game installation at load time.
    ///
    /// Moved here from SC2MaterialsMode (the Materials tab) —
    /// this is water-specific configuration, so it belongs
    /// on the Water tab (this method), not tucked into the unrelated
    /// Materials tab where it previously lived purely because that's where
    /// per-material texture pickers already existed to copy the pattern
    /// from.</summary>
    private void DrawWaterTexturePaths()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Water (export texture paths)", EditorStyles.boldLabel);

        BeginUnifiedPanelBox();
        DrawUnifiedNote(
            "Real vanilla defaults for the from-scratch water plane's exported material. Pick a texture the " +
            "same way as Diffuse/Normal/Mask on the Materials tab — only the real game path string is ever " +
            "exported into terrain.bdf, your own game installation resolves the actual .dds file when the map loads.");

        var currentUvScale = _data.WaterUvScale <= 0f ? 1024f : _data.WaterUvScale;
        var nextUvScale = EditorGUILayout.FloatField("UV Tiling Scale (world units/tile)", currentUvScale);
        if (nextUvScale > 0f && !Mathf.Approximately(nextUvScale, currentUvScale))
        {
            RecordUndoCheckpoint("Change Water UV Scale");
            _data.WaterUvScale = nextUvScale;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }
        EditorGUILayout.Space(4);

        if (_data.WaterTexturePaths == null || _data.WaterTexturePaths.Length != 13)
            _data.WaterTexturePaths = new string[13];

        for (var i = 0; i < 13; i++)
        {
            var isMask1Slot = i == 6;
            if (isMask1Slot && _data.WaterDepthAutoGenerate)
                EditorGUI.BeginDisabledGroup(true);

            var currentPath = _data.WaterTexturePaths[i] ?? "";
            var currentTex = SC2TerrainMaterialFactory.LoadTexturePublic(currentPath);
            var nextTex = DrawTexturePicker(kWaterTextureSlotLabels[i], currentTex, "Water:" + i) as Texture2D;
            if (nextTex != currentTex && !(isMask1Slot && _data.WaterDepthAutoGenerate))
            {
                RecordUndoCheckpoint("Change Water Texture Path");
                _data.WaterTexturePaths[i] = BuildWaterTexturePath(nextTex);
                EditorUtility.SetDirty(_data);
                MarkDirtyAndRepaint();
            }

            if (isMask1Slot && _data.WaterDepthAutoGenerate)
            {
                EditorGUI.EndDisabledGroup();
                DrawUnifiedNote("Generated automatically at export time — see \"Water Depth Texture\" below. Turn Auto-Generate off to pick a texture here manually instead.");
            }
        }
        EndUnifiedPanelBox();

        DrawWaterDepthGeneratorPanel();
    }

    /// <summary>Generates a real waterDepth texture for the water material's
    /// Mask1 slot, with a border-paint checkbox
    /// and tunable margin/falloff settings — the Mask1 slot above never had
    /// a real per-map texture generated for it (SC2WaterPlane.
    /// BuildWaterMaterial's own default path,
    /// "/maps/&lt;mapName&gt;/&lt;mapName&gt;.waterDepth.dds", pointed at a
    /// file nothing ever wrote). See SC2WaterDepthTextureGenerator for the
    /// actual algorithm; this panel only edits SC2TerrainData's settings and
    /// shows an on-demand live preview (never auto-regenerated per frame —
    /// the shoreline distance transform is real work at higher resolutions).</summary>
    private void DrawWaterDepthGeneratorPanel()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Water Depth Texture (Mask1 generator)", EditorStyles.boldLabel);
        BeginUnifiedPanelBox();
        DrawUnifiedNote(
            "Generates the real waterDepth texture at export time from this map's own heightfield vs. WaterElevation: " +
            "color = the water color (Environment > Water), alpha 0 = no water (land), alpha ramps from a low floor at " +
            "the shoreline (strong foam) up to fully opaque in open water (no foam) — matches SC2/Water's own shore-foam convention.");

        var nextAuto = EditorGUILayout.Toggle("Auto-Generate Water Depth Texture", _data.WaterDepthAutoGenerate);
        if (nextAuto != _data.WaterDepthAutoGenerate)
        {
            RecordUndoCheckpoint("Toggle Water Depth Auto-Generate");
            _data.WaterDepthAutoGenerate = nextAuto;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }

        EditorGUI.BeginDisabledGroup(!_data.WaterDepthAutoGenerate);

        var resOptions = new[] { 128, 256, 512, 1024, 2048 };
        var resLabels = new[] { "128", "256", "512", "1024", "2048" };
        var currentResIdx = System.Array.IndexOf(resOptions, _data.WaterDepthResolution);
        if (currentResIdx < 0) currentResIdx = 2;
        var nextResIdx = EditorGUILayout.Popup("Resolution", currentResIdx, resLabels);
        if (nextResIdx != currentResIdx)
        {
            RecordUndoCheckpoint("Change Water Depth Resolution");
            _data.WaterDepthResolution = resOptions[nextResIdx];
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }

        var nextMargin = DrawCompactSlider("Shore Margin (world units)", _data.WaterDepthMargin, 4f, 512f,
            onValueChangeStart: () => RecordUndoCheckpoint("Change Water Depth Margin"));
        if (!Mathf.Approximately(nextMargin, _data.WaterDepthMargin))
        {
            _data.WaterDepthMargin = nextMargin;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }
        DrawUnifiedNote("How far from the shoreline (world units) the alpha gradient/foam falloff spreads before reaching open water.");

        var nextFalloff = DrawCompactSlider("Alpha Falloff Coefficient", _data.WaterDepthFalloffPower, 0.25f, 8f,
            onValueChangeStart: () => RecordUndoCheckpoint("Change Water Depth Falloff"));
        if (!Mathf.Approximately(nextFalloff, _data.WaterDepthFalloffPower))
        {
            _data.WaterDepthFalloffPower = nextFalloff;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }
        DrawUnifiedNote("1 = linear ramp. Higher keeps foam strong closer to shore before rising sharply to open water; lower rises immediately.");

        var nextMinAlpha = DrawCompactSlider("Shoreline Min Alpha", _data.WaterDepthMinAlpha, 0.01f, 0.5f,
            onValueChangeStart: () => RecordUndoCheckpoint("Change Water Depth Min Alpha"));
        if (!Mathf.Approximately(nextMinAlpha, _data.WaterDepthMinAlpha))
        {
            _data.WaterDepthMinAlpha = nextMinAlpha;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }

        var nextBorder = EditorGUILayout.Toggle("Paint Water At Image Border", _data.WaterDepthPaintBorder);
        if (nextBorder != _data.WaterDepthPaintBorder)
        {
            RecordUndoCheckpoint("Toggle Water Depth Border Paint");
            _data.WaterDepthPaintBorder = nextBorder;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }
        DrawUnifiedNote("Forces the outer ring of the texture to read as water — for a water body that runs off the edge of the map's own footprint.");

        var nextImageBorderOutline = EditorGUILayout.Toggle("Outline Image Border (1px)", _data.WaterDepthImageBorderOutline);
        if (nextImageBorderOutline != _data.WaterDepthImageBorderOutline)
        {
            RecordUndoCheckpoint("Toggle Water Depth Image Border Outline");
            _data.WaterDepthImageBorderOutline = nextImageBorderOutline;
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }
        DrawUnifiedNote("Paints a hard 1-pixel ring of solid, fully-opaque water color around the texture's own outer edge — a visible frame around the whole texture, applied last so it always wins over the other options above.");

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Regenerate Preview", GUILayout.Height(SC2UiScale.Pxf(22f))))
            RegenerateWaterDepthPreview();

        if (_waterDepthPreviewTex != null)
        {
            var previewSize = Mathf.Min(SC2UiScale.Pxf(256f), GetUnifiedContentWidth());
            var rect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, _waterDepthPreviewTex, ScaleMode.ScaleToFit, true);
        }

        EditorGUI.EndDisabledGroup();
        EndUnifiedPanelBox();
    }

    private void RegenerateWaterDepthPreview()
    {
        DestroyWaterDepthPreview();
        var info = SC2TerrainExporter.BuildInfoModel(_data, Session, quiet: true);
        var waterColor = new Color(info.WaterColorR, info.WaterColorG, info.WaterColorB, 1f);
        _waterDepthPreviewTex = SC2WaterDepthTextureGenerator.GeneratePreviewTexture(
            _data, SampleLiveHeightGrid, info.WaterElevation, waterColor);
        MarkDirtyAndRepaint();
    }

    private void DestroyWaterDepthPreview()
    {
        if (_waterDepthPreviewTex != null) UnityEngine.Object.DestroyImmediate(_waterDepthPreviewTex);
        _waterDepthPreviewTex = null;
    }

    /// <summary>Inverse of the path->Texture2D resolution above — mirrors
    /// BuildTextureReference's own GamePath derivation (catalog lookup by
    /// the Resources-relative path SC2TerrainMaterialFactory registered when
    /// it loaded/picked this texture), but returns just the bare game-path
    /// string WaterTexturePaths actually stores, not a full
    /// SC2TextureReference (water has no document-side texture-slot command
    /// like ordinary materials do).</summary>
    private static string BuildWaterTexturePath(Texture2D texture) =>
        SC2TerrainMaterialFactory.ResolveGamePath(texture);

    /// <summary>Optional low-detail decorative terrain surrounding the
    /// playable core on all sides — a real second SC2TerrainData instance
    /// (SC2BackgroundTerrain.Generate), sharing the core's own CellSize/
    /// grid alignment so the boundary between them resolves via a plain
    /// per-cell inside/outside test and the SAME SC2TerrainSimplifier
    /// pipeline the core itself uses (this tab's SimplifyRatio slider is
    /// literally the same widget the Pairs tab uses, just pointed at the
    /// background's own single pair). See the "Decorative background
    /// terrain" plan for the full design.</summary>
    private void DrawBackgroundPanel()
    {
        if (_data == null)
        {
            DrawUnifiedNote("Create/load a terrain first — the background is generated relative to it.");
            return;
        }

        DrawUnifiedSectionLabel("Background Terrain");

        var newEnabled = EditorGUILayout.ToggleLeft(" Enabled", _data.BackgroundTerrainEnabled);
        if (newEnabled != _data.BackgroundTerrainEnabled)
        {
            RecordUndoCheckpoint("Toggle Background Terrain");
            _data.BackgroundTerrainEnabled = newEnabled;
            EditorUtility.SetDirty(_data);
            RefreshBackgroundRuntime();
            MarkDirtyAndRepaint();
        }

        if (!_data.BackgroundTerrainEnabled)
        {
            DrawUnifiedNote("Off by default — a disabled background contributes nothing to export, byte-for-byte identical to before this feature existed. Turn it on to generate a low-detail decorative surface around the playable core.");
            return;
        }

        var imported = GetImportedTerrainsForExport?.Invoke();
        var hasCustomBackground = imported != null && imported.Exists(t => t.IsBackgroundTerrain);
        if (hasCustomBackground)
        {
            DrawUnifiedNote("An imported terrain.bdf is flagged \"Use as Background Terrain\" (Imported tab) — the procedural generator below is skipped and that object's own geometry is exported/previewed instead.");
            return;
        }

        DrawUnifiedNote("A low-detail decorative surface is generated around the playable core, sharing its exact grid alignment so the seam is always exact regardless of SimplifyRatio choices on either side. Purely cosmetic — never contributes to Costs/Hfield, and only lightly to Collision2 near the seam.");

        var newSize = Mathf.RoundToInt(DrawCompactSlider("Footprint Size", _data.BackgroundTerrainSize, 512f, 16384f,
            onValueChangeStart: () => RecordUndoCheckpoint("Change Background Terrain Size")));
        if (newSize != _data.BackgroundTerrainSize)
        {
            _data.BackgroundTerrainSize = newSize;
            RefreshBackgroundRuntime();
        }
        DrawUnifiedNote("World-space outer size (X and Z), centered on the core. Clamped internally on a fine-CellSize map so generation always stays fast — a very large request may end up smaller than asked.");

        EditorGUILayout.Space(6);

        // Ensure-only (never overwrites hand edits) — see EnsureGenerated's
        // own doc comment. Only the very first enable actually builds
        // anything here; every later visit to this tab sees whatever the
        // user last painted/sculpted.
        SC2BackgroundTerrain.EnsureGenerated(_data);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Regenerate (discards edits)", GUILayout.Height(SC2UiScale.Pxf(22f))))
        {
            if (_data.BackgroundTerrainData == null || EditorUtility.DisplayDialog(
                    "Regenerate Background Terrain",
                    "This rebuilds the background from scratch at the current Footprint Size — any hand-painted geometry, materials, or pairs on it will be lost. Continue?",
                    "Regenerate", "Cancel"))
            {
                RecordUndoCheckpoint("Regenerate Background Terrain");
                SC2BackgroundTerrain.Regenerate(_data);
                RefreshBackgroundRuntime();
                MarkDirtyAndRepaint();
            }
        }
        GUILayout.EndHorizontal();
        DrawUnifiedNote("Regenerate applies a changed Footprint Size and resets the auto-generated falloff/hole — use it once after resizing, not as a routine action, since it discards hand edits.");

        var bg = _data.BackgroundTerrainData;
        if (bg != null && bg.Pairs.Count > 0)
        {
            var pair = bg.Pairs[0];
            EditorGUILayout.LabelField($"Source triangles: {bg.GridWidth * bg.GridHeight * 2:N0} (before simplify)");
            var newRatio = DrawCompactSlider("Simplify Ratio", pair.SimplifyRatio, 0f, 1f,
                onValueChangeStart: () => RecordUndoCheckpoint("Change Background Simplify Ratio"));
            if (!Mathf.Approximately(newRatio, pair.SimplifyRatio))
            {
                pair.SimplifyRatio = newRatio;
                // SimplifyRatio only affects the export-time
                // SC2TerrainLayerBuilder pass (BuildForExportOrPreview) —
                // the live chunk runtime always renders the full,
                // unsimplified grid (same as the core's own chunks, which
                // have no equivalent slider either), so there's nothing to
                // refresh on _bgRuntime itself here, only the seam skirt
                // (which DOES go through that same builder).
                SC2BackgroundSeamSkirt.Rebuild(_data, this);
                MarkDirtyAndRepaint();
            }
            DrawUnifiedNote("Same simplification the playable terrain's own pairs use — 0 keeps full source density, higher values reduce the exported/previewed triangle count. The boundary ring against the core is always locked regardless of this value, so the seam never opens up.");
            DrawUnifiedNote("Texture tiling (UV1) is set per pair — same \"Tiling Scale\" field as the core's own Pairs tab, reachable below in the Pairs tab's own Background section. Match it to whatever the core's own boundary pair uses to avoid a visible tiling-density jump at the seam.");
        }

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Bake Background Lighting", GUILayout.Height(SC2UiScale.Pxf(22f))))
        {
            if (bg != null) RecordUndoCheckpointFor(bg, "Bake Background Lighting");
            _bgBakeStatus = SC2RnmBakeService.BakeBackground(_data, GetImportedTerrainsForExport?.Invoke(), Session);
        }
        if (!string.IsNullOrEmpty(_bgBakeStatus))
            EditorGUILayout.LabelField(_bgBakeStatus, EditorStyles.miniLabel);
        DrawUnifiedNote("Same real per-texel RNM lighting bake as the core's own \"Bake Lighting\" (Lighting Bake tab) — reads the SAME Sun/Shadow settings (no separate copy for background). Shadow occlusion now considers BOTH zones together, so a tall core feature can shadow the background and vice versa.");

        EditorGUILayout.Space(8);
        DrawUnifiedNote("Geometry/Paint sculpt and paint whichever surface the brush is actually over — click on the background instead of switching a target here. Materials/Pairs show both the core's and the background's own lists on their tabs.");
    }

    /// <summary>Real per-texel RNM lighting bake (3 basis-
    /// direction passes + a heightmap horizon-march shadow pass). Moved out
    /// of the Paint tab into its own dedicated sub-tab (peer to Geometry),
    /// same rationale as the Water tab above. Each of the 4 real bake inputs
    /// (SunDirection, SunColor, SunIntensity, ShadowColor) has its own
    /// independent "sync from Environment" checkbox rather than one shared
    /// toggle, so the user can lock in a subset while the rest keep tracking
    /// Environment live — see SC2RnmBakeService.Bake and
    /// SC2TerrainData.RnmBakeSync*/RnmBakeOverride*.</summary>
    private void DrawLightingBakePanel()
    {
        if (_data == null)
        {
            DrawUnifiedNote("Create/load a terrain first — the bake needs real geometry to sample.");
            return;
        }

        DrawUnifiedSectionLabel("Lighting Bake Inputs");
        DrawUnifiedNote("Each parameter below either tracks Environment's current value live (Sync on), or uses a locally fixed value you set here (Sync off). Bake Lighting reads whichever is active for each parameter at the moment you click it.");

        DrawBakeSyncFloat3("Sun Direction", "sunDirection", ref _data.RnmBakeSyncSunDirection, ref _data.RnmBakeOverrideSunDirection);
        DrawBakeSyncColor("Sun Color", "sunColor", ref _data.RnmBakeSyncSunColor, ref _data.RnmBakeOverrideSunColor);
        DrawBakeSyncFloat("Sun Intensity", "sunIntensity", ref _data.RnmBakeSyncSunIntensity, ref _data.RnmBakeOverrideSunIntensity, 0f, 10f);
        DrawBakeSyncColor("Shadow Color", "shadowColor", ref _data.RnmBakeSyncShadowColor, ref _data.RnmBakeOverrideShadowColor);

        EditorGUILayout.Space(4);
        // No "Sync from Environment" concept here (there's no matching
        // info.bdf field to track) — a plain slider, same as this panel's
        // sibling tabs' own non-Environment-backed values (e.g. Simplify
        // Ratio). One shared value for both Bake Lighting (core) and
        // Background's own "Bake Background Lighting" button — see
        // SC2TerrainData.RnmBakeShadowSoftRadius's own doc comment for why
        // there's only one, matching every other Lighting Bake Input above.
        _data.RnmBakeShadowSoftRadius = Mathf.RoundToInt(DrawCompactSlider("Shadow Sharpness", _data.RnmBakeShadowSoftRadius, 0f, 24f, "rnmbake:shadowSharpness"));
        DrawUnifiedNote("Lower = sharper shadow edges, higher = softer. Applies to both this bake and the Background tab's own bake.");

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Bake Lighting", GUILayout.Height(SC2UiScale.Pxf(24f))))
        {
            RecordUndoCheckpoint("Bake Lighting");
            _bakeStatus = SC2RnmBakeService.Bake(_data, GetImportedTerrainsForExport?.Invoke(), Session, CommitImportedRnmMasks);
            EditorUtility.SetDirty(_data);
            MarkDirtyAndRepaint();
        }
        if (!string.IsNullOrEmpty(_bakeStatus))
            EditorGUILayout.LabelField(_bakeStatus, EditorStyles.miniLabel);
        DrawUnifiedNote("Manual trigger only — the shadow horizon-march is real per-texel work across up to 2048x2048 texels per mask, so it never runs silently on export. Also bakes the imported layers assigned to a mask below.");

        DrawImportedRnmBakeSection();
    }

    // ── Lighting Bake > Imported Objects ────────────────────────────────────
    // Masks of their own (SC2TerrainDocument.ImportedRnm), separate from the
    // core terrain's and the background's; each LAYER of an imported
    // terrain.bdf is assigned to one (or none). Bake Lighting bakes them by
    // the layers' own UV2.

    private readonly System.Collections.Generic.Queue<System.Action> _pendingImportedRnmActions = new();

    private void CommitImportedRnmMasks(System.Collections.Generic.List<SC2.MapEditor.Document.SC2RnmMaskSetDocument> masks)
    {
        ExecuteCommand(new SC2.MapEditor.Document.SC2SetImportedRnmMasksCommand(masks));
        RefreshImportedRnmMasks?.Invoke();
    }

    private void DrawImportedRnmBakeSection()
    {
        var document = Session?.Document;
        if (document == null) return;

        // Edits queued by the buttons below run here, at the top of the next
        // layout pass, so the control tree never changes between an event
        // pass and the repaint that follows it.
        while (_pendingImportedRnmActions.Count > 0) _pendingImportedRnmActions.Dequeue()();

        EditorGUILayout.Space(10);
        DrawUnifiedSectionLabel("Imported Objects");
        DrawUnifiedNote("Bake lighting onto imported terrain.bdf objects that bring their own UV2. Create masks here (separate from the terrain's mask01/background), assign each LAYER of an object to a mask — several layers or objects may share one mask if their UV2 doesn't overlap — then press Bake Lighting. The layer's material needs RNM Lighting on.");

        var masks = document.Terrain.ImportedRnm.MaskSets;
        var objects = GetImportedRnmTargets?.Invoke() ?? new System.Collections.Generic.List<SC2ImportedRnmObjectInfo>();

        // ---- masks
        SC2EnvFieldGui.SectionLabel("Masks");
        if (masks.Count == 0)
            DrawUnifiedNote("No masks yet — press \"+ New mask\".");
        var resLabels = new[] { "512", "1024", "2048", "4096" };
        foreach (var set in masks)
        {
            var users = 0;
            foreach (var o in objects) foreach (var l in o.Layers) if (l.MaskName == set.Name) users++;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{set.Name}  —  {(SC2.MapEditor.Document.SC2ImportedRnmMasks.HasBake(set) ? "baked" : "not baked")}, {users} layer(s)");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Delete", GUILayout.Width(SC2UiScale.Pxf(60f))))
            {
                var name = set.Name;
                _pendingImportedRnmActions.Enqueue(() => DeleteImportedRnmMask(name));
            }
            GUILayout.EndHorizontal();

            var resIndex = System.Array.IndexOf(SC2.MapEditor.Document.SC2ImportedRnmMasks.Resolutions, set.Width);
            var newRes = GUILayout.SelectionGrid(resIndex < 0 ? 1 : resIndex, resLabels, 4);
            if (newRes != resIndex && newRes >= 0)
            {
                var name = set.Name;
                var size = SC2.MapEditor.Document.SC2ImportedRnmMasks.Resolutions[newRes];
                _pendingImportedRnmActions.Enqueue(() => ResizeImportedRnmMask(name, size));
            }
            if (users > 1)
                DrawUnifiedNote($"{users} layers share \"{set.Name}\" — their UV2 islands must not overlap.");
        }
        if (GUILayout.Button("+ New mask"))
            _pendingImportedRnmActions.Enqueue(AddImportedRnmMask);

        // ---- layers
        EditorGUILayout.Space(6);
        SC2EnvFieldGui.SectionLabel("Layers");
        if (objects.Count == 0)
        {
            DrawUnifiedNote("No imported terrain.bdf in the scene — use File > Import terrain.bdf.");
            return;
        }
        var options = new string[masks.Count + 1];
        options[0] = "None";
        for (var i = 0; i < masks.Count; i++) options[i + 1] = masks[i].Name;
        foreach (var obj in objects)
        {
            GUILayout.Label(obj.Name, EditorStyles.boldLabel);
            for (var li = 0; li < obj.Layers.Count; li++)
            {
                var layer = obj.Layers[li];
                GUILayout.Label($"{layer.MaterialName}  ({layer.Triangles} tris)");
                var current = 0;
                for (var i = 0; i < masks.Count; i++) if (masks[i].Name == layer.MaskName) current = i + 1;
                var chosen = GUILayout.SelectionGrid(current, options, Mathf.Min(options.Length, 3));
                if (chosen != current)
                {
                    var objectId = obj.ObjectId; var layerIndex = li;
                    var maskName = chosen == 0 ? "" : masks[chosen - 1].Name;
                    _pendingImportedRnmActions.Enqueue(() =>
                    {
                        SetImportedRnmTarget?.Invoke(objectId, layerIndex, maskName);
                        MarkDirtyAndRepaint();
                    });
                }
            }
        }
    }

    private void AddImportedRnmMask()
    {
        var document = Session?.Document;
        if (document == null) return;
        var next = document.Terrain.ImportedRnm.CloneMaskSets();
        var name = SC2.MapEditor.Document.SC2ImportedRnmMasks.NextName(next);
        var size = SC2.MapEditor.Document.SC2ImportedRnmMasks.DefaultResolution;
        next.Add(new SC2.MapEditor.Document.SC2RnmMaskSetDocument { Id = name, Name = name, Width = size, Height = size });
        ExecuteCommand(new SC2.MapEditor.Document.SC2SetImportedRnmMasksCommand(next));
        MarkDirtyAndRepaint();
    }

    private void ResizeImportedRnmMask(string name, int size)
    {
        var document = Session?.Document;
        if (document == null) return;
        var next = document.Terrain.ImportedRnm.CloneMaskSets();
        var set = next.Find(s => s.Name == name);
        if (set == null || set.Width == size) return;
        // A new resolution invalidates whatever was baked at the old one.
        set.Width = size; set.Height = size;
        set.MaskBytes = default; set.BakedRgb0 = default; set.BakedRgb1 = default; set.BakedRgb2 = default; set.BakedShadowAlpha = default;
        ExecuteCommand(new SC2.MapEditor.Document.SC2SetImportedRnmMasksCommand(next));
        RefreshImportedRnmMasks?.Invoke();
        MarkDirtyAndRepaint();
    }

    private void DeleteImportedRnmMask(string name)
    {
        var document = Session?.Document;
        if (document == null) return;
        // Layers pointing at it go back to "None" first.
        var objects = GetImportedRnmTargets?.Invoke();
        if (objects != null)
            foreach (var obj in objects)
                for (var li = 0; li < obj.Layers.Count; li++)
                    if (obj.Layers[li].MaskName == name) SetImportedRnmTarget?.Invoke(obj.ObjectId, li, "");
        var next = document.Terrain.ImportedRnm.CloneMaskSets();
        next.RemoveAll(s => s.Name == name);
        ExecuteCommand(new SC2.MapEditor.Document.SC2SetImportedRnmMasksCommand(next));
        RefreshImportedRnmMasks?.Invoke();
        MarkDirtyAndRepaint();
    }

    // Override widgets below use SC2EnvFieldGui (the same real Vector3/
    // Slider/ColorField widgets Environment's own tabs use — a full RGB/HSV
    // wheel for color, not the plain EditorGUILayout.ColorField swatch,
    // which renders as an inert flat box under this project's Build-side
    // shim) so a locally-overridden bake input looks and behaves exactly
    // like the Environment field it's standing in for. Keys are prefixed
    // "rnmbake:" so this panel's own expanded/last-value state never
    // collides with Environment's identically-named Sun/Shadow fields.
    private void DrawBakeSyncFloat3(string label, string key, ref bool sync, ref Vector3 overrideValue)
    {
        sync = EditorGUILayout.ToggleLeft("Sync " + label + " from Environment", sync);
        if (!sync)
            overrideValue = SC2EnvFieldGui.Vector3Field(label + " (override)", overrideValue, "rnmbake:" + key);
        EditorGUILayout.Space(2);
    }

    private void DrawBakeSyncFloat(string label, string key, ref bool sync, ref float overrideValue, float min, float max)
    {
        sync = EditorGUILayout.ToggleLeft("Sync " + label + " from Environment", sync);
        if (!sync)
            overrideValue = SC2EnvFieldGui.Slider(label + " (override)", overrideValue, min, max, "rnmbake:" + key);
        EditorGUILayout.Space(2);
    }

    private void DrawBakeSyncColor(string label, string key, ref bool sync, ref Color overrideValue)
    {
        sync = EditorGUILayout.ToggleLeft("Sync " + label + " from Environment", sync);
        if (!sync)
            overrideValue = SC2EnvFieldGui.ColorField(label + " (override)", overrideValue, "rnmbake:" + key);
        EditorGUILayout.Space(2);
    }

    private static void RestorePanelGuiState(float labelWidth, Color background, Color content, Color color)
    {
        EditorGUIUtility.labelWidth = labelWidth;
        GUI.backgroundColor = background;
        GUI.contentColor = content;
        GUI.color = color;
    }

#if UNITY_EDITOR
    public void OnSceneGUI(SceneView sv)
    {
        if (_data == null || !_data.IsInitialized) return;
        if (_previewBuildQueued) return;
        EnsurePreview();
        EnsureSubEditors();
        _runtime?.UpdateLOD(sv.camera.transform.position);
        switch (_mode)
        {
            case EditorMode.Geometry:  _geoMode.OnSceneGUI(sv);  break;
            case EditorMode.Paint:     _paintMode.OnSceneGUI(sv); break;
        }
    }
#endif

    /// <summary>Decides whether this frame's Geometry/Paint brush should
    /// target the core or the background — no more manual toggle, so this
    /// is a raycast-priority pick instead: try the core's own runtime
    /// first, then the background's (translated into its local space by
    /// GetWorldOffset), same layered-fallback precedent this codebase
    /// already uses for imported-terrain-vs-procedural raycasts
    /// (TryRaycastImportedTerrain before RaycastTerrainHeight). Core is
    /// tried first deliberately: RaycastTerrainHeight's own XZ-bounds check
    /// has no idea the background's own footprint has the core's rectangle
    /// carved out of it, so clicking inside the core would otherwise also
    /// satisfy the background's bounding box.
    ///
    /// Sticky for the duration of an active stroke (_strokeTargetIsBackground,
    /// set on MouseDown/cleared on MouseUp) so a drag that crosses the seam
    /// can't silently hand off mid-stroke and leave whichever mode owned it
    /// never getting its MouseUp — same reasoning as each mode's own
    /// _isPainting flag already locking a stroke to itself across drag
    /// frames.</summary>
    private bool DetermineActiveBrushTarget(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        var e = Event.current;
        if (_strokeTargetIsBackground.HasValue)
        {
            var locked = _strokeTargetIsBackground.Value;
            if (e != null && e.type == EventType.MouseUp)
                _strokeTargetIsBackground = null;
            _activeBrushTargetIsBackground = locked;
            return locked;
        }

        var isBackground = _activeBrushTargetIsBackground;
        var ray = GameGuiPointToRay(camera, viewportRect, mousePosition);
        if (_runtime != null && RaycastTerrainHeight(_runtime, ray, out _))
        {
            isBackground = false;
        }
        else if (_bgRuntime != null)
        {
            var offset = SC2BackgroundTerrain.GetWorldOffset(_data);
            var localRay = new Ray(ray.origin - offset, ray.direction);
            if (RaycastTerrainHeight(_bgRuntime, localRay, out _))
                isBackground = true;
        }
        _activeBrushTargetIsBackground = isBackground;

        if (e != null && e.type == EventType.MouseDown && e.button == 0)
            _strokeTargetIsBackground = isBackground;

        return isBackground;
    }

    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        if (_data == null || !_data.IsInitialized || camera == null)
            return;
        if (_previewBuildQueued)
            return;
        if (_runtime == null)
            return;

        EnsureSubEditors();
        if (_data.BackgroundTerrainEnabled) EnsureBackgroundSubEditors();
        _runtime?.UpdateLOD(camera.transform.position);

        switch (_mode)
        {
            case EditorMode.Geometry:
                DetermineActiveBrushTarget(camera, viewportRect, mousePosition);
                _geoMode.OnGameViewGUI(camera, viewportRect, mousePosition);
                break;
            case EditorMode.Paint:
                DetermineActiveBrushTarget(camera, viewportRect, mousePosition);
                _paintMode.OnGameViewGUI(camera, viewportRect, mousePosition);
                break;
        }
    }

    public void Update()
    {
        // Live water plane, kept in sync every tick this
        // method runs (not gated behind _previewBuildQueued's own early
        // return below) - see SC2WaterPreview's own doc comment. Imported
        // terrains are passed through so Ensure can skip the synthetic
        // plane when one of them already carries real water geometry -
        // mirrors SC2TerrainExporter.BuildExportArtifacts' own
        // "alreadyHasWater" export-time check, which this preview didn't
        // have until now (a real gap: a Blender-imported map's own water
        // would render doubled-up with a synthetic plane at the SAME
        // elevation, or a wrong one if WaterElevation didn't match the
        // import's authored height).
        SC2WaterPreview.Ensure(_data, Session, _waterOverlayVisible, GetImportedTerrainsForExport?.Invoke());

        // Same "keep it live every tick" reasoning as SC2WaterPreview just
        // above. EnsureBackgroundRuntime is cheap once built (early-out on
        // the steady-state path); the preview-mode/seam-skirt refresh right
        // after it is a plain Material.Set* call, no mesh rebuild — mirrors
        // the old SC2BackgroundTerrainPreview's own cheap-every-tick
        // ApplyPreviewOverrides, just against the live runtime instead of a
        // periodically-rebuilt mesh, so the background always matches
        // whatever the core's own chunks are showing under the
        // Textured/RNM/Mats/Pairs/Slope/Height toggle.
        if (SC2BackgroundRuntimeSync.ConsumePending())
            RefreshBackgroundRuntime();
        else
            EnsureBackgroundRuntime();
        if (_bgRuntime != null && _data?.BackgroundTerrainData != null)
        {
            // Refreshes _sharedChunkMat's preview-mode state — the seam
            // skirt now renders with this exact same shared material
            // (SC2BackgroundSeamSkirt.Rebuild), so it picks this up for
            // free, no separate call needed.
            ApplyPreviewModeToChunksFor(_bgRuntime, _data.BackgroundTerrainData);
        }
        // Self-heal: the seam's first-ever Rebuild may have run before the
        // core's own Runtime existed (see _seamPendingCoreRuntimeReady's own
        // doc comment) — as soon as it does, rebuild once more so the
        // core-side seam vertices stop reading the Y=0 fallback, without
        // needing the user to hover the background to trigger it manually.
        if (_seamPendingCoreRuntimeReady && Runtime != null)
        {
            SC2BackgroundSeamSkirt.Rebuild(_data, this);
            _seamPendingCoreRuntimeReady = false;
        }

        // Keep every imported terrain.bdf object's SC2/Terrain material(s)
        // in sync with the live Environment tab (Sun/Shadow/Fog/
        // EnvironmentMap) — same "refresh every tick" reasoning as
        // SC2WaterPreview.Ensure just above, mirrored for the new
        // Sun/Fog/EnvironmentMap shader wiring instead of water.
        var importedForEnvironment = GetImportedTerrainsForExport?.Invoke();
        if (importedForEnvironment != null && importedForEnvironment.Count > 0)
        {
            var envInfo = SC2TerrainExporter.BuildInfoModel(_data, Session, quiet: true);
            SC2TerrainMaterialFactory.RefreshImportedTerrainEnvironment(importedForEnvironment, envInfo);
        }

        PushHeightGridOverlay();
        PushWireframeOverlay();

        if (_previewBuildQueued)
        {
            if (EditorApplication.timeSinceStartup < _previewBuildAt)
                return;

            _previewBuildQueued = false;
            if (_data != null && _data.IsInitialized)
            {
                EnsurePreview();
                ApplyPreviewModeToChunks();
                // Warm the live height grid here too, same debounce timing
                // as the preview mesh - so the first paint click after an
                // edit doesn't pay the rebuild cost synchronously mid-drag.
                _liveHeightGrid.EnsureFresh(_data, GetImportedTerrainsForExport);
                Repaint();
#if UNITY_EDITOR
                SceneView.RepaintAll();
#endif
            }
        }

        if (!_previewDirty) return;
        if (_data == null || !_data.IsInitialized || _runtime == null) return;
        _previewDirty = false;
        ApplyPreviewModeToChunks();
        DestroyLegacyPreviewMesh();
        Repaint();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
    }

    // ── Helpers exposed to sub-editors ───────────────────────────────────────

    public void RepaintPairColorRTPublic() => RepaintPairColorRT();
    public void SetBasePreviewVisible(bool visible)
    {
        _runtime?.SetVisible(visible);
        DestroyLegacyPreviewMesh();
        _simplifiedMode?.SetVisible(!visible);
    }
    public void SetBasePreviewVisiblePublic(bool visible) => SetBasePreviewVisible(visible);
    public void EnsurePairSyncPublic() => EnsurePairSync();
    public void FlushPaintToCPU()
    {
        EnsurePairSync(force: true);
        MarkPairCpuDataSynced();
    }
    public void MarkPairCpuDataStale() { _pairCpuDataStale = true; Repaint(); }
    public void MarkPairCpuDataSynced()
    {
        if (!_pairCpuDataStale) return;
        _pairCpuDataStale = false; Repaint();
    }
    // Live terrain brush strokes (Raise/Lower/Smooth/Paint/...) mutate
    // SC2TerrainData directly and never go through Session.History — so
    // Session.Document.Revision never changes for them. Anything that needs
    // to know "did the terrain's actual geometry change since I last looked"
    // (e.g. SC2DecalsShell.EnsurePreview, whose projected decal mesh would
    // otherwise keep rendering against stale terrain shape until something
    // unrelated happened to bump the document revision) polls this instead.
    // Static and shared across every SC2TerrainShell instance for the same
    // reason SyncEffectiveViewWidth is static — a cross-shell consumer has
    // no reference back to whichever SC2TerrainShell instance is live.
    private static int s_terrainGeometryVersion;
    public static int TerrainGeometryVersion => s_terrainGeometryVersion;

    /// <summary>Called directly by the few height-mutation finalize points
    /// (SC2GeometryMode's sculpt/import/raise-lower handlers) that sync
    /// Data.Heights and EditorUtility.SetDirty themselves instead of
    /// routing through MarkDirtyAndRepaint below — those still need to bump
    /// this same counter, just without the rest of MarkDirtyAndRepaint's
    /// (repaint/visibility) side effects re-running mid-stroke.</summary>
    public static void BumpTerrainGeometryVersion() => s_terrainGeometryVersion++;

    public void MarkDirtyAndRepaint()
    {
        if (_data != null) EditorUtility.SetDirty(_data);
        _simplifiedMode?.MarkDirty();
        _previewDirty = true;
        s_terrainGeometryVersion++;
        // Cheap (one SetActive call) and idempotent - resyncing here means
        // any setter that flips ImportOnlyMode (or anything else
        // ApplyRuntimeVisibility depends on) doesn't also need to remember
        // to call ApplyRuntimeVisibility itself; this is the one call every
        // such setter already makes.
        ApplyRuntimeVisibility();
        Repaint();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
    }

    /// <summary>Flush terrain state before export or save. Same logic as the private SaveState below (kept as one method to avoid the two silently drifting apart).</summary>
    public void SaveTerrainState(bool writeAssets) => SaveState(writeAssets);

    public bool IsPairCpuDataStale => _pairCpuDataStale;

    // ── Toolbar drawing ──────────────────────────────────────────────────────

    private void DrawModeToolbar()
    {
        // "Imported" (EditorMode value 6) is deliberately NOT one of this
        // row's buttons — it's reached a different way (selecting an
        // imported terrain object), same as before Water/LightingBake were
        // added. Since that leaves a gap in the enum's own int sequence,
        // this can't just cast DrawUnifiedSegmentGrid's returned INDEX
        // straight to (EditorMode) anymore (index 6 "Water" would silently
        // land on EditorMode.Imported=6, not EditorMode.Water=7) — look the
        // real mode up through this array instead, keyed by array
        // position, not by the enum's own underlying numeric value.
        var labels = new[] { "Geometry", "Materials", "Pairs", "Paint Pairs", "Simplified", "Export", "Water", "Lighting Bake", "Background" };
        var modes = new[] { EditorMode.Geometry, EditorMode.Materials, EditorMode.Pairs, EditorMode.Paint, EditorMode.Simplified, EditorMode.Export, EditorMode.Water, EditorMode.LightingBake, EditorMode.Background };
        var currentIndex = System.Array.IndexOf(modes, _mode);
        var nextIndex = DrawUnifiedSegmentGrid(Mathf.Max(0, currentIndex), labels, 3);
        var next = modes[Mathf.Clamp(nextIndex, 0, modes.Length - 1)];
        if (next != _mode)
        {
            _mode = next;
            OnModeChanged();
        }
    }

    private void DrawPreviewToolbar()
    {
        // Used to bail out entirely without an active/initialized
        // SC2TerrainData — but Textured/RNM/Mats/Pairs now also apply to
        // imported terrain.bdf objects (see OnPreviewModeChanged), which
        // have no SC2TerrainData at all. The toolbar must render regardless
        // so those stay usable; only the _data-specific bits below
        // (height-gradient panel, symmetry buttons) stay gated on it.
        if (!_embeddedGui)
            DrawUnifiedSectionLabel("Preview");
        var prev = _prev;
        var nextPrev = (PreviewMode)DrawUnifiedSegmentGrid(
            (int)_prev,
            new[] { "Textured", "RNM", "Mats", "Pairs", "Slope", "Height" },
            3);
        if (nextPrev != prev)
        {
            // Checkpoint BEFORE _prev is reassigned below — CaptureAuxState()
            // reads the CURRENT _prev, so recording after the assignment
            // would snapshot the already-new mode as the undo target (same
            // "record before, not after" rule as everywhere else). Only
            // checkpointed when real terrain data exists — otherwise this
            // toolbar can be driven purely by imported terrain.bdf objects
            // (see the comment above), which SC2TerrainUndoStack has nothing
            // to snapshot for; recording a checkpoint with a null _data would
            // still push a combined-undo marker that Undo could never
            // actually satisfy.
            if (_data != null)
                RecordUndoCheckpoint("Change Preview Mode");
            _prev = nextPrev;
            _previewDirty = true;
            ApplyPreviewModeNow();
        }

        // Gallery-style MatCap picker — 6 procedural looks matching 6
        // specific balls from Blender's own MatCap picker (see
        // TerrainView.shader's MatCapLit), instead of sampled textures.
        // Shades the Mats/Pairs/unassigned-Textured preview colors, so it's
        // shown regardless of which of the 6 modes above is active — an
        // unassigned/no-real-pair area uses it no matter what _prev says.
        _matCapPreset = DrawUnifiedSegmentGrid(_matCapPreset, new[] { "Standard", "Steel", "Zebra", "Tan", "Dark", "Normal" }, 3);
        _runtime?.SetMatCapPreset(_matCapPreset);

        if (_prev == PreviewMode.HeightGradient)
        {
            BeginUnifiedPanelBox();
            _heightGradientMin = DrawCompactSlider("Min (wu)", _heightGradientMin, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight, "preview-height-min");
            _heightGradientMax = DrawCompactSlider("Max (wu)", Mathf.Max(_heightGradientMax, _heightGradientMin + 1f), SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight, "preview-height-max");
            DrawHeightGradientBar(EditorGUILayout.GetControlRect(GUILayout.Height(SC2UiScale.Pxf(14f))), _heightGradientMin, _heightGradientMax);
            if (GUILayout.Button("Auto-Range", GUILayout.Height(SC2UiScale.Pxf(20f))))
            {
                AutoRangeHeightGradient();
                ApplyPreviewModeNow();
            }
            _runtime?.SetHeightGradientOverlay(true, _heightGradientMin, _heightGradientMax);
            EditorGUILayout.EndVertical();
        }
    }

    private void ApplyPreviewModeNow()
    {
        // Imported terrain.bdf objects have no SC2TerrainData at all, so
        // this must fire regardless of _data — only the paintable-mesh
        // logic below stays gated on it.
        OnPreviewModeChanged?.Invoke(_prev);

        if (_data == null || !_data.IsInitialized)
            return;

        EnsurePreview();
        _previewDirty = false;
        ApplyPreviewModeToChunks();
        DestroyLegacyPreviewMesh();
        EditorApplication.QueuePlayerLoopUpdate();
        Repaint();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
    }

    private static void DrawUnifiedSectionLabel(string text)
    {
        var style = SafeGuiStyle(EditorStyles.miniBoldLabel);
        style.normal.textColor = new Color(0.90f, 0.92f, 0.95f, 1f);
        EditorGUILayout.LabelField(text, style);
    }

    private static GUIStyle SafeGuiStyle(GUIStyle preferred)
    {
        // new GUIStyle(existingStyle) is a known Unity trap: it throws
        // NullReferenceException if the source style holds a reference to a
        // destroyed object (a font/texture killed by a domain reload, Play
        // Mode exit, or an assembly reload while SC2UnifiedSkin's baked skin
        // was still alive but one of its referenced assets wasn't). Falling
        // back to a fresh, guaranteed-safe style on failure instead of
        // crashing the whole IMGUI frame.
        if (preferred != null)
        {
            try { return new GUIStyle(preferred); }
            catch (System.NullReferenceException) { /* fall through */ }
        }

        if (GUI.skin != null && GUI.skin.label != null)
        {
            try { return new GUIStyle(GUI.skin.label); }
            catch (System.NullReferenceException) { /* fall through */ }
        }

        return new GUIStyle();
    }

    private static GUIStyle _unifiedPanelBoxStyle;

    public static void BeginUnifiedPanelBox()
    {
        // Cached (a fresh GUIStyle per call per frame is pure GC churn) and
        // styled as the same 9-sliced rounded card the top panel uses, so
        // panel sections and header chrome read as one design system instead
        // of square dark boxes next to rounded pills.
        if (_unifiedPanelBoxStyle == null || _unifiedPanelBoxStyle.normal.background == null)
        {
            _unifiedPanelBoxStyle = new GUIStyle(GUIStyle.none)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
                border = SC2UnifiedSkin.CardBorder,
                normal = { background = SC2UnifiedSkin.CardTexture }
            };
        }
        EditorGUILayout.BeginVertical(_unifiedPanelBoxStyle, GUILayout.MaxWidth(GetUnifiedContentWidth()));
    }

    public static void EndUnifiedPanelBox()
    {
        EditorGUILayout.EndVertical();
    }

    public static void DrawUnifiedNote(string text)
    {
        BeginUnifiedPanelBox();
        var style = SafeGuiStyle(EditorStyles.miniLabel);
        style.wordWrap = true;
        // This note sits on the card background BeginUnifiedPanelBox() just
        // opened (ColorSurface) — same lightness as the tab pills, so a dim
        // text color here reads as low-contrast "grey on grey" too.
        style.normal.textColor = new Color(0.90f, 0.92f, 0.95f, 1f);
        EditorGUILayout.LabelField(text, style);
        EndUnifiedPanelBox();
    }

    private static Texture2D GetUnifiedBoxTexture()
    {
        if (_unifiedBoxTexture != null)
            return _unifiedBoxTexture;

        _unifiedBoxTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        _unifiedBoxTexture.SetPixel(0, 0, new Color(0.106f, 0.114f, 0.129f, 1f));
        _unifiedBoxTexture.Apply(false, true);
        return _unifiedBoxTexture;
    }

    public static int DrawUnifiedSegmentGrid(int selected, string[] labels, int columns)
    {
        columns = Mathf.Max(1, columns);
        var rows = Mathf.CeilToInt(labels.Length / (float)columns);
        // Scaled through SC2UiScale, same as every other fixed row-height
        // this pass has touched: this grid draws its labels with native
        // EditorStyles.miniLabel and never gave textStyle an explicit
        // fontSize, so its text stayed the old small size while every
        // OTHER control around it (regular buttons, via SC2UnifiedSkin)
        // grew with Scale — the mismatch is what read as "squished".
        var rowHeight = SC2UiScale.Pxf(23f);
        var gridWidth = GetUnifiedContentWidth();
        var rect = GUILayoutUtility.GetRect(gridWidth, rows * rowHeight, GUILayout.Width(gridWidth), GUILayout.MaxWidth(gridWidth));
        rect.width = Mathf.Max(1f, gridWidth);

        var e = Event.current;
        var normal = new Color(0.184f, 0.192f, 0.219f, 1f);
        var hover = new Color(0.224f, 0.235f, 0.267f, 1f);
        var active = new Color(0.357f, 0.576f, 0.929f, 1f);
        var border = new Color(0.243f, 0.259f, 0.294f, 1f);
        var textStyle = SafeGuiStyle(EditorStyles.miniLabel);
        textStyle.alignment = TextAnchor.MiddleCenter;
        textStyle.clipping = TextClipping.Clip;
        textStyle.fontSize = SC2UiScale.F(10);
        // Cell background above (0.184-0.219) is a light-ish pill, same as the
        // main tab/subtab pills — a dimmer text color there reads as low
        // contrast "grey on grey". Full near-white instead.
        textStyle.normal.textColor = new Color(0.90f, 0.92f, 0.95f, 1f);
        var activeTextStyle = new GUIStyle(textStyle)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        for (var i = 0; i < labels.Length; i++)
        {
            var row = i / columns;
            var col = i % columns;
            var cell = new Rect(
                rect.x + col * rect.width / columns,
                rect.y + row * rowHeight,
                rect.width / columns,
                rowHeight);
            cell = Rect.MinMaxRect(Mathf.Floor(cell.x), Mathf.Floor(cell.y), Mathf.Floor(cell.xMax), Mathf.Floor(cell.yMax));

            var isActive = i == selected;
            var isHover = e != null && cell.Contains(e.mousePosition);
            EditorGUI.DrawRect(cell, isActive ? active : isHover ? hover : normal);
            EditorGUI.DrawRect(new Rect(cell.xMax - 1f, cell.y, 1f, cell.height), border);
            EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - 1f, cell.width, 1f), border);
            GUI.Label(cell, labels[i], isActive ? activeTextStyle : textStyle);

            if (e != null && e.type == EventType.MouseDown && e.button == 0 && cell.Contains(e.mousePosition))
            {
                selected = i;
                GUI.changed = true;
                e.Use();
            }
        }

        return selected;
    }

    private static float GetUnifiedContentWidth()
    {
        return Mathf.Max(1f, StaticEffectiveViewWidth - kPanelScrollbarReserve);
    }

    /// <summary>
    /// Public accessor for sub-panel classes (e.g. SC2ExportPanel) that need
    /// to hard-constrain their root container to prevent overflow in build & run.
    /// </summary>
    public static float GetPanelContentWidth() => GetUnifiedContentWidth();

    private void AutoRangeHeightGradient()
    {
        if (_data?.Heights == null || _data.Heights.Length == 0)
            return;

        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var height in _data.Heights)
        {
            if (height < min) min = height;
            if (height > max) max = height;
        }

        _heightGradientMin = Mathf.Clamp(min, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight);
        _heightGradientMax = Mathf.Clamp(Mathf.Max(max, _heightGradientMin + 1f), SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight);
    }

    private static void DrawHeightGradientBar(Rect rect, float min, float max)
    {
        for (var x = 0; x < (int)rect.width; x++)
        {
            var t = rect.width <= 1f ? 0f : (float)x / rect.width;
            EditorGUI.DrawRect(new Rect(rect.x + x, rect.y, 1, rect.height), EvaluateHeightGradient(t));
        }

        var left = SafeGuiStyle(EditorStyles.miniLabel);
        var right = SafeGuiStyle(EditorStyles.miniLabel);
        right.alignment = TextAnchor.MiddleRight;
        EditorGUI.LabelField(rect, $"{min:0.##} wu", left);
        EditorGUI.LabelField(rect, $"{max:0.##} wu", right);
    }

    public static Color EvaluateHeightGradient(float t)
    {
        if (t < .25f) return Color.Lerp(new Color(.05f, .30f, .90f), new Color(.10f, .70f, .30f), t / .25f);
        if (t < .55f) return Color.Lerp(new Color(.10f, .70f, .30f), new Color(.90f, .85f, .20f), (t - .25f) / .30f);
        if (t < .75f) return Color.Lerp(new Color(.90f, .85f, .20f), new Color(.80f, .40f, .10f), (t - .55f) / .20f);
        return Color.Lerp(new Color(.80f, .40f, .10f), new Color(.95f, .95f, .95f), (t - .75f) / .25f);
    }

    /// <summary>Keys currently mid-drag (mouse held on the slider handle) —
    /// used so DrawCompactSlider's optional onValueChangeStart fires exactly
    /// once per drag GESTURE, not once per frame of the drag. Without this,
    /// wiring a checkpoint into a continuously-dragged slider (e.g. brush
    /// Radius) would push one undo step per frame — hundreds in a couple of
    /// seconds — and silently evict real edits from the fixed-depth stack.</summary>
    private static readonly System.Collections.Generic.HashSet<string> DraggingSliderKeys = new();

    public static float DrawCompactSlider(string label, float value, float min, float max, string keySuffix = null, System.Action onValueChangeStart = null)
    {
        value = Round2(Mathf.Clamp(value, min, max));
        var key = $"{label}:{keySuffix ?? ""}:{min:0.###}:{max:0.###}";
        var controlName = "SC2Numeric_" + key;

        // If the incoming value differs from what this control last
        // RETURNED, and the control doesn't currently have keyboard focus
        // (so the user isn't mid-edit), the caller's value changed for a
        // reason OTHER than this widget's own last-frame interaction — e.g.
        // Undo/Redo, Import, or any other external write to the underlying
        // field. Drop the cached display text so the rest of this function
        // picks up the fresh value, instead of the TryParseFloat(cached
        // text) calls below silently overwriting it back with the stale
        // pre-Undo text. Without this, Undo on Collision2's Height
        // Offset/Polygon Budget would appear to do nothing, because
        // the very next redraw would use this stale cached text to recompute
        // `value`, discarding the Undo's effect and even re-committing the
        // stale value back onto the undo stack (since the caller then saw
        // its own `before` differ from this method's now-stale return
        // value and treated it as a fresh user edit).
        if (GUI.GetNameOfFocusedControl() != controlName
            && LastSyncedValue.TryGetValue(key, out var lastSynced)
            && !Mathf.Approximately(lastSynced, value))
        {
            NumericFieldText.Remove(key);
        }

        if (!NumericFieldText.TryGetValue(key, out var text))
            text = FormatNumber(value);

        // Both Width and MaxWidth: MaxWidth is advisory and can be ignored by
        // ExpandWidth children in scroll views; Width is a hard cap.
        var cw = GetUnifiedContentWidth();
        EditorGUILayout.BeginVertical(GUILayout.Width(cw), GUILayout.MaxWidth(cw));
        EditorGUILayout.BeginHorizontal(GUILayout.Width(cw), GUILayout.MaxWidth(cw));
        // Label takes remaining space after the fixed-width text field (56,
        // scaled below) + margin. This "60f" reservation must scale too:
        // if it stayed fixed while the real field it's reserving room FOR
        // grows with SC2UiScale, the label would claim more width than is
        // actually left once the (now bigger) field renders, crowding the
        // two together.
        var fieldWidth = SC2UiScale.Pxf(56f);
        var labelStyle = SafeGuiStyle(EditorStyles.miniLabel);
        labelStyle.fontSize = SC2UiScale.F(10);
        EditorGUILayout.LabelField(label, labelStyle, GUILayout.MinWidth(SC2UiScale.Pxf(48f)), GUILayout.MaxWidth(Mathf.Max(SC2UiScale.Pxf(48f), cw - fieldWidth - SC2UiScale.Pxf(4f))));

        var fieldHeight = SC2UiScale.Pxf(18f);
        var fieldRect = GUILayoutUtility.GetRect(fieldWidth, fieldHeight, GUILayout.Width(fieldWidth), GUILayout.Height(fieldHeight));
        var e = Event.current;
        GUI.SetNextControlName(controlName);
        var fieldStyle = SafeGuiStyle(EditorStyles.textField);
        fieldStyle.fontSize = SC2UiScale.F(10);
        var nextText = GUI.TextField(fieldRect, text, fieldStyle);
        NumericFieldText[key] = nextText;
        var focused = GUI.GetNameOfFocusedControl() == controlName;

        if (focused && e != null && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                var beforeCommit = value;
                CommitCompactSliderText(key, ref value, min, max);
                if (value != beforeCommit)
                    onValueChangeStart?.Invoke();
                GUI.FocusControl(null);
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                NumericFieldText[key] = FormatNumber(value);
                GUI.FocusControl(null);
                e.Use();
            }
        }

        if (TryParseFloat(NumericFieldText[key], out var typedValue))
            value = Mathf.Clamp(typedValue, min, max);

        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginDisabledGroup(focused);
        EditorGUI.BeginChangeCheck();
        var sliderValue = GUILayout.HorizontalSlider(Round2(value), min, max, GUILayout.Height(SC2UiScale.Pxf(16f)));
        var sliderChanged = EditorGUI.EndChangeCheck();
        EditorGUI.EndDisabledGroup();

        if (sliderChanged)
        {
            var newValue = Round2(Mathf.Clamp(sliderValue, min, max));
            // Fires once per drag gesture (Add returns true only the first
            // time this key is seen since the last MouseUp), not once per
            // frame of the drag — see DraggingSliderKeys' own doc comment.
            if (newValue != value && onValueChangeStart != null && DraggingSliderKeys.Add(key))
                onValueChangeStart();
            value = newValue;
            NumericFieldText[key] = FormatNumber(value);
        }
        else if (!focused && TryParseFloat(NumericFieldText[key], out var committedValue))
        {
            var newValue = Round2(Mathf.Clamp(committedValue, min, max));
            if (newValue != value)
                onValueChangeStart?.Invoke();
            value = newValue;
            NumericFieldText[key] = FormatNumber(value);
        }
        else if (!focused)
        {
            NumericFieldText[key] = FormatNumber(value);
        }

        if (e != null && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            DraggingSliderKeys.Remove(key);

        LastSyncedValue[key] = value;
        EditorGUILayout.EndVertical();
        return value;
    }

    private static void CommitCompactSliderText(string key, ref float value, float min, float max)
    {
        if (NumericFieldText.TryGetValue(key, out var text) && TryParseFloat(text, out var typedValue))
            value = Round2(Mathf.Clamp(typedValue, min, max));

        NumericFieldText[key] = FormatNumber(value);
    }

    private static float Round2(float value)
    {
        return Mathf.Round(value * 100f) / 100f;
    }

    private static string FormatNumber(float value)
    {
        return Round2(value).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryParseFloat(string text, out float value)
    {
        return float.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value)
            || float.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture,
                out value);
    }

    // Widened from Texture2D: a cubemap/volume field (info.bdf's
    // EnvironmentMap/SkyMap/ColorCorrectionCube/FogOfWarCorrectionCube,
    // see SC2InfoModelFieldDrawer.DrawTexturesTab) resolves to a real
    // Cubemap/Texture3D here — all IS-A Texture. The preview box always
    // draws via SC2TextureBrowser.DrawPreview instead of the raw value,
    // since neither the real Editor's preview machinery nor the Build-time
    // UnityEditorRuntimeShim can be trusted to render a non-Texture2D value
    // sensibly — DrawPreview also UV-crops a cached thumbnail to this
    // entry's own atlas cell when needed (a plain EditorGUI.DrawPreviewTexture
    // call here previously showed the whole shared atlas page uncropped).
    public static Texture DrawTexturePicker(string label, Texture texture, string pickerKey, params Texture[] siblingHints)
    {
        var labelStyle = SafeGuiStyle(EditorStyles.miniLabel);
        labelStyle.normal.textColor = new Color(0.18f, 0.62f, 1f);
        labelStyle.fontSize = SC2UiScale.F(10);
        EditorGUILayout.LabelField(label, labelStyle);

        texture = SC2TextureBrowser.Consume(texture, pickerKey, out _);

        // The clamp bounds here must scale too, or they cap the preview at a
        // fixed 168px ceiling regardless of Scale even though
        // GetUnifiedContentWidth() (the value being clamped) already scales
        // correctly.
        var fullWidth = GetUnifiedContentWidth();
        var size = Mathf.Clamp(fullWidth - SC2UiScale.Pxf(72f), SC2UiScale.Pxf(112f), SC2UiScale.Pxf(168f));
        // Long-standing bug fixed here: centering the box via
        // BeginHorizontal()+two FlexibleSpace() calls (the previous
        // approach) relies on that horizontal group correctly inheriting
        // the caller's own content width from the ambient layout stack.
        // Reachable through some callers' deeper nesting (e.g. Paint
        // Materials' Custom brush picker: DrawPanel -> DrawRnm ->
        // DrawCustomBrushPicker -> DrawTexturePickerWithPath -> here), that
        // inference drifted wider than the actual visible panel, rendering
        // the box mostly outside the left panel's right edge instead of
        // centered in it. Allocating the FULL available width directly and
        // centering via an explicit sub-rect sidesteps that layout-
        // inheritance question entirely — this box's own footprint is never
        // wider than fullWidth, which every caller already constrains its
        // own container to.
        var rowRect = GUILayoutUtility.GetRect(fullWidth, size, GUILayout.Width(fullWidth), GUILayout.MaxWidth(fullWidth));
        var rect = new Rect(rowRect.x + (rowRect.width - size) * 0.5f, rowRect.y, size, size);

        EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f, 1f));
        GUI.Box(rect, GUIContent.none);

        if (texture != null)
        {
            SC2TextureBrowser.DrawPreview(rect, texture, TextureUsageForPicker(pickerKey));
            var nameRect = new Rect(rect.x + SC2UiScale.Pxf(5f), rect.y + SC2UiScale.Pxf(5f), rect.width - SC2UiScale.Pxf(10f), SC2UiScale.Pxf(34f));
            DrawTexturePreviewLabel(nameRect, texture.name);
        }
        else
        {
            var textStyle = SafeGuiStyle(EditorStyles.centeredGreyMiniLabel);
            textStyle.alignment = TextAnchor.MiddleCenter;
            textStyle.normal.textColor = new Color(0.85f, 0.18f, 0.18f);
            textStyle.fontSize = SC2UiScale.F(10);
            GUI.Label(rect, "Select Texture2D", textStyle);
        }

        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            GUIUtility.keyboardControl = 0;
            SC2TextureBrowser.Open(pickerKey, texture, TextureUsageForPicker(pickerKey), siblingHints);
            e.Use();
        }

        return texture;
    }

    /// <summary>Materials/imported-terrain/
    /// decals/environment texture slots should show — and let the user
    /// directly EDIT — the actual relative path string that gets written
    /// into the exported file, not just a texture thumbnail with no visible
    /// path at all. Wraps the plain DrawTexturePicker above (unchanged,
    /// still used as-is by the Water/Heightmap/GeometryHeightmap pickers
    /// that don't need this) with an editable path text field underneath.
    ///
    /// <paramref name="path"/> is the CALLER's own current path string,
    /// independent of whichever Texture, if any, is currently resolved —
    /// passed by ref so typing here updates it directly. Typing a new value
    /// tries to resolve a real texture from it (LoadTexturePublicAny) for
    /// live preview; when nothing resolves, the typed path is still kept
    /// and returned as-is rather than discarded — the whole point of this
    /// control is pointing at a real path (e.g. an un-extracted vanilla
    /// texture) the editor can't currently load a preview for.
    ///
    /// Picking a texture via the thumbnail's own browser click (the
    /// existing DrawTexturePicker behavior, unchanged) does NOT touch
    /// <paramref name="path"/> here — each caller already has its own
    /// domain-specific path-from-texture resolution (catalog lookup,
    /// TryGetResourcePath, etc.) it runs when the returned texture differs
    /// from what was passed in; this wrapper only adds the reverse
    /// direction (path-to-texture) via the new text field.
    /// <paramref name="pickedViaBrowser"/> tells the caller WHICH of the two
    /// happened this frame (true = browser click changed the texture, false
    /// = the path field changed, texture follows only if it resolved) — a
    /// caller that stores a manual override string alongside its texture
    /// reference (Materials' DiffuseOverridePath etc.) needs this to know
    /// whether to clear that override (a fresh browser pick should win
    /// outright) or keep respecting the just-typed path.</summary>
    // An Imported-terrain
    // material's path field showed BLANK even though its texture had
    // already loaded successfully ("84/84 loaded") — the field only ever
    // displayed the manual OVERRIDE string (empty until the user typed
    // something), never the real path already known from parsing/resolving
    // the texture. Every caller now also passes a best-effort
    // resolvedFallbackPath — shown (but not written into `path`, so no
    // override is silently created) whenever `path` itself is empty.
    //
    // Same report asked for a lock toggle so a correctly-populated field
    // can't be edited by accident. Locked (the safe default) disables the
    // text box; unlocking is a deliberate click. Lock state is pure
    // editing-session UI state (never exported), keyed by pickerKey.
    private static readonly Dictionary<string, bool> _pathFieldLocked = new();

    /// <summary>Shared lock-toggle button for an editable export-path field,
    /// keyed by an arbitrary caller-chosen string (same key space as
    /// pickerKey). Defaults to locked (safe) the first time a given key is
    /// drawn. Returns the CURRENT locked state after handling this frame's
    /// click, for the caller to gate its own text field's GUI.enabled with.</summary>
    public static bool DrawPathLockToggle(string key)
    {
        if (!_pathFieldLocked.TryGetValue(key, out var locked)) { locked = true; _pathFieldLocked[key] = locked; }
        var lockStyle = SafeGuiStyle(GUI.skin.button);
        lockStyle.fontSize = SC2UiScale.F(10);
        var newLocked = GUILayout.Toggle(locked, locked ? "\U0001F512" : "\U0001F513", lockStyle, GUILayout.Width(SC2UiScale.Pxf(26f)));
        if (newLocked != locked) _pathFieldLocked[key] = newLocked;
        return _pathFieldLocked[key];
    }

    public static Texture DrawTexturePickerWithPath(string label, Texture texture, string pickerKey, ref string path, out bool pickedViaBrowser, string resolvedFallbackPath = null, params Texture[] siblingHints)
    {
        pickedViaBrowser = false;
        var picked = DrawTexturePicker(label, texture, pickerKey, siblingHints);
        if (picked != texture) pickedViaBrowser = true;

        var displayPath = string.IsNullOrEmpty(path) ? (resolvedFallbackPath ?? "") : path;
        var locked = DrawPathLockToggle(pickerKey + ":lock");

        var pathStyle = SafeGuiStyle(EditorStyles.textField);
        pathStyle.fontSize = SC2UiScale.F(9);
        var prevEnabled = GUI.enabled;
        GUI.enabled = prevEnabled && !locked;
        // GUILayout.TextField (plain UnityEngine, not EditorGUILayout) used
        // deliberately here: the shim's EditorGUILayout (UnityEditorRuntimeShim.cs,
        // #if !UNITY_EDITOR) only implements the label/no-style TextField
        // overloads, not the (text, GUIStyle, options) one — GUILayout itself
        // is real UnityEngine API in both Editor and Standalone player builds,
        // so this works in both without touching the shim.
        var newPath = GUILayout.TextField(displayPath, pathStyle, GUILayout.Width(GetUnifiedContentWidth()));
        GUI.enabled = prevEnabled;
        if (!locked && newPath != displayPath)
        {
            path = newPath;
            var resolved = SC2TerrainMaterialFactory.LoadTexturePublicAny(newPath);
            if (resolved != null) picked = resolved;
        }
        return picked;
    }

    private static SC2.MapEditor.Textures.SC2TextureUsage TextureUsageForPicker(string key)
    {
        var value = (key ?? "").ToLowerInvariant();
        if (value.Contains("normal")) return SC2.MapEditor.Textures.SC2TextureUsage.Normal;
        if (value.Contains("mask")) return SC2.MapEditor.Textures.SC2TextureUsage.Mask;
        if (value.Contains("height")) return SC2.MapEditor.Textures.SC2TextureUsage.Heightmap;
        if (value.Contains("rnm")) return SC2.MapEditor.Textures.SC2TextureUsage.Rnm;
        if (value.Contains("cube")) return SC2.MapEditor.Textures.SC2TextureUsage.ColorCorrectionCube;
        if (value.Contains("sky")) return SC2.MapEditor.Textures.SC2TextureUsage.Sky;
        if (value.Contains("env")) return SC2.MapEditor.Textures.SC2TextureUsage.Environment;
        if (value.Contains("diffuse")) return SC2.MapEditor.Textures.SC2TextureUsage.Diffuse;
        return SC2.MapEditor.Textures.SC2TextureUsage.Unknown;
    }

    private static void DrawTexturePreviewLabel(Rect rect, string text)
    {
        var shadow = SafeGuiStyle(EditorStyles.boldLabel);
        shadow.fontSize = SC2UiScale.F(10);
        shadow.wordWrap = true;
        shadow.clipping = TextClipping.Clip;
        shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        var label = new GUIStyle(shadow)
        {
            normal = { textColor = Color.white }
        };

        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, shadow);
        GUI.Label(rect, text, label);
    }

    /// <summary>Same shadow-backed readable-text trick as
    /// DrawTexturePreviewLabel above, centered on a single GUI point
    /// instead of filling a caller-given Rect — sized to the text itself
    /// rather than a fixed box, so it works for any short live readout
    /// (currently: the Measure tool's distance label).</summary>
    public static void DrawFloatingLabel(Vector2 guiPosition, string text, Color accentColor)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        var baseStyle = SafeGuiStyle(EditorStyles.boldLabel);
        baseStyle.alignment = TextAnchor.MiddleCenter;
        baseStyle.fontSize = SC2UiScale.F(12);
        baseStyle.wordWrap = false;
        var size = baseStyle.CalcSize(new GUIContent(text));
        var rect = new Rect(guiPosition.x - size.x * 0.5f - 4f, guiPosition.y - size.y * 0.5f - 14f, size.x + 8f, size.y + 4f);

        var shadow = new GUIStyle(baseStyle) { normal = { textColor = new Color(0f, 0f, 0f, 0.85f) } };
        var main = new GUIStyle(baseStyle) { normal = { textColor = accentColor } };
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, shadow);
        GUI.Label(rect, text, main);
    }

    /// <summary>sampleHeight (world X,Z -> world Y), when given, makes the
    /// ring hug the terrain surface instead of floating as a flat 2D circle
    /// — a Blender-sculpt-style brush cursor that visibly rises/dips over
    /// slopes and cliffs instead of appearing to float above or sink below
    /// them. Omit it (default) for the original flat stamped-texture ring —
    /// still exactly right for a preview that must NOT track the surface
    /// (e.g. SC2GeometryMode's SetHeight-tool preview, which shows the flat
    /// target height plane by design).</summary>
    public static void DrawGameViewBrush(Camera camera, Rect viewportRect, Vector3 worldPosition, float radius, Color outer, Color inner, float innerRadius = 0f, Func<float, float, float> sampleHeight = null, bool clipToViewport = true)
    {
        if (Event.current.type != EventType.Repaint || camera == null)
            return;

        var center = WorldToGameGui(camera, viewportRect, worldPosition);
        if (!viewportRect.Contains(center))
            return;

        if (sampleHeight != null)
        {
            DrawTerrainHuggingRing(camera, viewportRect, worldPosition, radius, outer, sampleHeight);
            if (innerRadius > 0.5f)
                DrawTerrainHuggingRing(camera, viewportRect, worldPosition, innerRadius, inner, sampleHeight);
            return;
        }

        var edge = WorldToGameGui(camera, viewportRect, worldPosition + Vector3.right * Mathf.Max(0.01f, radius));
        var screenRadius = Mathf.Max(4f, Vector2.Distance(center, edge));

        var previousGuiColor = GUI.color;
        var previousGuiEnabled = GUI.enabled;
        GUI.enabled = true;
        // Was defaulted to false on the assumption that "a mouse-following
        // brush cursor never needs this (the mouse, and so the cursor, is
        // only ever drawn while inside the viewport to begin with)" — true
        // for the CENTER point (checked just above), but false for the
        // ring's own edge: a large-radius brush (e.g. SC2GeometryMode's
        // SetHeight target-height ring) whose center sits safely inside the
        // viewport, near the left panel, still projects an edge well past
        // that boundary, and no caller actually ever opted into
        // clipToViewport=true to catch it (grep-confirmed: every call site
        // relied on this default). DrawGuiCircle draws one big textured
        // quad with no clipping of its own, so without this it painted
        // straight over whichever panel the ring's bounding box overlapped
        // — confirmed via a real screenshot of the SetHeight brush's cyan
        // ring covering the entire left panel. GUI.BeginGroup clips its
        // children AND remaps their coordinates to be relative to the
        // group's own top-left, hence the `- viewportRect.position` below.
        if (clipToViewport) GUI.BeginGroup(viewportRect);
        var drawCenter = clipToViewport ? center - viewportRect.position : center;
        DrawGuiCircle(drawCenter, screenRadius, outer, 48);
        if (innerRadius > 0.5f)
        {
            var innerEdge = WorldToGameGui(camera, viewportRect, worldPosition + Vector3.right * innerRadius);
            DrawGuiCircle(drawCenter, Mathf.Max(3f, Vector2.Distance(center, innerEdge)), inner, 48);
        }
        if (clipToViewport) GUI.EndGroup();
        GUI.color = previousGuiColor;
        GUI.enabled = previousGuiEnabled;
    }

    /// <summary>A small, fixed-screen-size filled dot marking a world
    /// position — for calling out where SC2TerrainData.GetSymmetricPositions'
    /// mirrored/radial copies of a brush will land, alongside (not instead
    /// of) DrawGameViewBrush's own full-size ring for the primary position.
    /// Deliberately a fixed PIXEL size (unlike the brush ring itself, which
    /// is world-space) so it stays clearly visible regardless of brush
    /// radius or camera zoom — a full duplicate ring at every mirrored
    /// position would visually compete with the primary brush and clutter
    /// tightly-clustered radial symmetry counts. sampleHeight (optional,
    /// same contract as DrawGameViewBrush) re-samples world Y at the
    /// marker's own X/Z so it hugs terrain that isn't perfectly symmetric
    /// in height, rather than reusing the primary position's own Y
    /// verbatim.</summary>
    public static void DrawSymmetryPointMarker(Camera camera, Rect viewportRect, Vector3 worldPosition, Color color, Func<float, float, float> sampleHeight = null, float pixelSize = 12f)
    {
        if (Event.current.type != EventType.Repaint || camera == null)
            return;
        if (sampleHeight != null)
            worldPosition = new Vector3(worldPosition.x, sampleHeight(worldPosition.x, worldPosition.z), worldPosition.z);
        var center = WorldToGameGui(camera, viewportRect, worldPosition);
        if (!viewportRect.Contains(center))
            return;
        var previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(center.x - pixelSize * 0.5f, center.y - pixelSize * 0.5f, pixelSize, pixelSize), GetSolidDotTexture(), ScaleMode.StretchToFill, true);
        GUI.color = previousColor;
    }

    /// <summary>Convenience wrapper every shell's own brush-drawing call
    /// site uses identically: draw DrawSymmetryPointMarker at every
    /// position GetSymmetricPositions(primaryPosition) returns EXCEPT the
    /// primary position itself (already shown by that shell's own full
    /// brush cursor). No-ops cleanly when allPositions is null/empty or
    /// Symmetry is None (GetSymmetricPositions then returns just the
    /// primary position, so the loop below finds nothing to draw).</summary>
    public static void DrawSymmetryPointMarkers(Camera camera, Rect viewportRect, Vector3 primaryPosition, IEnumerable<Vector3> allPositions, Color color, Func<float, float, float> sampleHeight = null)
    {
        if (allPositions == null) return;
        foreach (var pos in allPositions)
            if (pos != primaryPosition)
                DrawSymmetryPointMarker(camera, viewportRect, pos, color, sampleHeight);
    }

    private const int BrushRingSegments = 48;
    // How many neighbors on each side get averaged into every ring vertex's
    // height (see the smoothing pass below) — small enough to still track
    // real hills/valleys, large enough to absorb a single noisy/spiky sample
    // from jagged procedural terrain.
    private const int RingHeightSmoothingRadius = 2;

    private static void DrawTerrainHuggingRing(Camera camera, Rect viewportRect, Vector3 center, float radius, Color color, Func<float, float, float> sampleHeight)
    {
        var previousGuiColor = GUI.color;
        var previousGuiEnabled = GUI.enabled;
        GUI.enabled = true;

        // Sample every vertex's raw terrain height first, then smooth with a
        // small circular moving average before projecting to screen —
        // a reference unit's ring can span a couple hundred world units of
        // real (often noise-generated, intentionally jagged) procedural
        // terrain, and connecting BrushRingSegments independently-sampled
        // heights with straight segments turned every sharp local spike/
        // cliff into a visible flat chord cutting across the ring instead of
        // a smooth curve. Averaging each height with its neighbors keeps the
        // ring tracking the terrain's overall shape (hills/valleys) without
        // being whipsawed by single-sample noise.
        Span<float> rawHeight = stackalloc float[BrushRingSegments];
        for (var i = 0; i < BrushRingSegments; i++)
        {
            var angle = i / (float)BrushRingSegments * Mathf.PI * 2f;
            rawHeight[i] = sampleHeight(center.x + Mathf.Cos(angle) * radius, center.z + Mathf.Sin(angle) * radius);
        }
        Span<float> smoothHeight = stackalloc float[BrushRingSegments];
        for (var i = 0; i < BrushRingSegments; i++)
        {
            var sum = 0f;
            for (var k = -RingHeightSmoothingRadius; k <= RingHeightSmoothingRadius; k++)
                sum += rawHeight[((i + k) % BrushRingSegments + BrushRingSegments) % BrushRingSegments];
            smoothHeight[i] = sum / (RingHeightSmoothingRadius * 2 + 1);
        }

        Vector2? previousPoint = null;
        for (var i = 0; i <= BrushRingSegments; i++)
        {
            var idx = i % BrushRingSegments;
            var angle = idx / (float)BrushRingSegments * Mathf.PI * 2f;
            var worldX = center.x + Mathf.Cos(angle) * radius;
            var worldZ = center.z + Mathf.Sin(angle) * radius;
            var worldPoint = new Vector3(worldX, smoothHeight[idx], worldZ);
            var screenPoint = WorldToGameGui(camera, viewportRect, worldPoint);
            // The camera renders behind the WHOLE Game View — the left/right
            // panels and header are just opaque UI drawn on top of part of
            // that same image — so a ring segment outside viewportRect still
            // has a valid (but off-limits) GUI position. A brush cursor never
            // hit this (mouse-bound, so it stays near the middle of the
            // viewport), but a reference unit's large, static intel ring
            // easily extends past the viewport into a side panel, so each
            // segment is clipped here instead of drawn straight over it.
            if (previousPoint.HasValue
                && ClipSegmentToViewport(viewportRect, previousPoint.Value, screenPoint, out var clippedA, out var clippedB))
                DrawGuiLineSegment(clippedA, clippedB, color, 2f);
            previousPoint = screenPoint;
        }

        GUI.color = previousGuiColor;
        GUI.enabled = previousGuiEnabled;
    }

    /// <summary>Liang-Barsky line-vs-rect clip, shared by every GUI overlay
    /// that draws world-space lines over the Game View — the camera renders
    /// the FULL view, and side panels/header are just opaque UI drawn on top
    /// of part of that same image, so a line endpoint outside viewportRect
    /// is still a valid (but off-limits) GUI position that would otherwise
    /// paint straight over that panel. Promoted here from
    /// SC2GameViewEditorUI's own (functionally identical) private copy,
    /// originally written for the Measure tool, so DrawTerrainHuggingRing's
    /// large static rings can reuse the exact same algorithm instead of a
    /// second copy.</summary>
    public static bool ClipSegmentToViewport(Rect viewportRect, Vector2 a, Vector2 b, out Vector2 clippedA, out Vector2 clippedB)
    {
        var dx = b.x - a.x; var dy = b.y - a.y;
        float t0 = 0f, t1 = 1f;
        var p0 = -dx; var q0 = a.x - viewportRect.xMin;
        var p1 = dx; var q1 = viewportRect.xMax - a.x;
        var p2 = -dy; var q2 = a.y - viewportRect.yMin;
        var p3 = dy; var q3 = viewportRect.yMax - a.y;
        for (var i = 0; i < 4; i++)
        {
            var p = i == 0 ? p0 : i == 1 ? p1 : i == 2 ? p2 : p3;
            var q = i == 0 ? q0 : i == 1 ? q1 : i == 2 ? q2 : q3;
            if (Mathf.Approximately(p, 0f))
            {
                if (q < 0f) { clippedA = a; clippedB = b; return false; }
                continue;
            }
            var r = q / p;
            if (p < 0f) { if (r > t1) { clippedA = a; clippedB = b; return false; } if (r > t0) t0 = r; }
            else { if (r < t0) { clippedA = a; clippedB = b; return false; } if (r < t1) t1 = r; }
        }
        clippedA = new Vector2(a.x + dx * t0, a.y + dy * t0);
        clippedB = new Vector2(a.x + dx * t1, a.y + dy * t1);
        return true;
    }

    /// <summary>Build-safe line segment (no UnityEditor.Handles dependency,
    /// works identically in the Editor Game View and a real Build) — a
    /// single rotated, stretched white-texture quad. Same trick
    /// SC2ObjectsShell's own graph-edge line drawing already uses. Widened
    /// from private to public so the Measure tool (SC2GameViewEditorUI,
    /// a top-level "Global" tool rather than a per-shell one) can draw its
    /// own start-to-end line with this exact same primitive.</summary>
    public static void DrawGuiLineSegment(Vector2 a, Vector2 b, Color color, float width)
    {
        var delta = b - a;
        var length = delta.magnitude;
        if (length < 0.5f) return;
        var oldMatrix = GUI.matrix;
        var oldColor = GUI.color;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;
        GUI.color = oldColor;
    }

    // Widened from private to public so SC2BrushRadiusGesture (shared
    // F interactive brush-resize gesture) can convert its resize
    // anchor's world position into the exact same screen-pixel space
    // DrawGameViewBrush itself uses — the resize needs to invert that same
    // projection (screen pixels -> world units) to keep the live preview
    // circle's edge tracking the mouse cursor exactly, at any zoom level.
    public static Vector2 WorldToGameGui(Camera camera, Rect viewportRect, Vector3 world)
    {
        // The camera renders the full Game View. viewportRect is only the
        // unobscured part of that image after the unified UI covers its header
        // and left panel, so it must not be used to rescale camera coordinates.
        // Rescaling caused brush/object overlays to drift away from terrain when
        // the camera moved or zoomed.
        var pixel = SC2TopViewMirror.WorldToGui(camera, world);
        return new Vector2(pixel.x, pixel.y);
    }

    /// <summary>Sphere-vs-frustum test, shared by SC2GameAssetScenePreview
    /// (Props/Units) and SC2MarkerScenePreview (Markers). Without this, FPS
    /// drops hard specifically once props' 3D models are on screen, even
    /// with GPU-instancing/allocation already optimized elsewhere.
    /// Root cause: Graphics.DrawMeshInstanced culls the whole submitted
    /// batch as ONE group — it does not cull individual instances — and
    /// every instance of one blueprint/marker type across the ENTIRE map
    /// shares a single batch (grouped by type, never by position). So a
    /// batch's combined bounds span the whole map, meaning Unity's own
    /// batch-level cull almost never actually rejects anything: as soon as
    /// the camera can see even ONE instance of a widely-scattered type
    /// (e.g. decorative trees/rocks from the Props scatter brush), the GPU
    /// shades EVERY instance of that type anywhere on the map, every frame,
    /// regardless of how much of it is actually on screen. Testing each
    /// instance against the camera frustum here — cheap (a handful of
    /// plane-distance checks per instance) relative to the GPU vertex/
    /// fragment cost of drawing an entire off-screen mesh — and excluding
    /// the ones that fail before they're added to the per-frame instance
    /// buffer restores real, expected "only pay for what's visible" scaling
    /// instead of "pay for every instance of a type anywhere near the
    /// camera."</summary>
    public static bool IsSphereInFrustum(Plane[] frustumPlanes, Vector3 center, float radius)
    {
        for (var i = 0; i < frustumPlanes.Length; i++)
            if (frustumPlanes[i].GetDistanceToPoint(center) < -radius)
                return false;
        return true;
    }

    public static Ray GameGuiPointToRay(Camera camera, Rect viewportRect, Vector2 guiMousePosition)
    {
        // See WorldToGameGui: GUI positions are full-screen pixels, while the
        // viewport rect is a UI clipping rect rather than Camera.rect.
        return SC2TopViewMirror.ScreenPointToRay(camera, guiMousePosition);
    }

    /// <summary>Closest hit across real imported-terrain.bdf
    /// MeshColliders along (ray). Mirrors SC2ObjectsShell's own private
    /// TryRaycastImportedTerrain (same logic, generalized to a shared
    /// utility) so other shells (SC2CostsShell) can prefer the actual
    /// imported surface the same way Objects placement already does, instead
    /// of falling straight to RaycastTerrainHeight's live-plane-only analytic
    /// sample. Callers should try this FIRST and fall back to
    /// RaycastTerrainHeight only when it returns false — matching
    /// SC2TerrainExporter.MergeImportedTerrain's own real precedence, where
    /// imported geometry is what actually ships for any area it covers.</summary>
    public static bool TryRaycastImportedTerrain(System.Collections.Generic.List<Collider> colliders, Ray ray, out Vector3 hit)
    {
        hit = default;
        if (colliders == null || colliders.Count == 0) return false;

        var found = false;
        var closestDistance = float.MaxValue;
        foreach (var col in colliders)
        {
            if (col == null) continue;
            if (!col.Raycast(ray, out var rayHit, 1_000_000f)) continue;
            if (rayHit.distance >= closestDistance) continue;
            closestDistance = rayHit.distance;
            hit = rayHit.point;
            found = true;
        }
        return found;
    }

    public static bool RaycastTerrainHeight(SC2TerrainRuntime runtime, Ray ray, out Vector3 hit)
    {
        hit = default;
        if (runtime == null || runtime.GridWidth <= 0 || runtime.GridHeight <= 0 || runtime.CellSize <= 0f)
            return false;

        var maxX = runtime.GridWidth * runtime.CellSize;
        var maxZ = runtime.GridHeight * runtime.CellSize;
        if (!RaycastXZBounds(ray, maxX, maxZ, out var enter, out var exit))
            return false;

        if (float.IsNegativeInfinity(enter))
            enter = 0f;
        if (float.IsPositiveInfinity(exit))
            exit = 10000f;

        enter = Mathf.Max(0f, enter);
        if (exit <= enter)
            return false;

        const int steps = 256;
        var previousT = enter;
        var previousDelta = TerrainRayDelta(runtime, ray, previousT);
        if (Mathf.Abs(previousDelta) < 0.001f)
        {
            hit = ray.GetPoint(previousT);
            hit.y = SampleRuntimeHeight(runtime, hit.x, hit.z);
            return true;
        }

        for (var i = 1; i <= steps; i++)
        {
            var t = Mathf.Lerp(enter, exit, i / (float)steps);
            var delta = TerrainRayDelta(runtime, ray, t);
            if (Mathf.Abs(delta) < 0.001f || Mathf.Sign(delta) != Mathf.Sign(previousDelta))
            {
                var a = previousT;
                var b = t;
                for (var j = 0; j < 10; j++)
                {
                    var mid = (a + b) * 0.5f;
                    var midDelta = TerrainRayDelta(runtime, ray, mid);
                    if (Mathf.Sign(midDelta) == Mathf.Sign(previousDelta))
                        a = mid;
                    else
                        b = mid;
                }

                hit = ray.GetPoint((a + b) * 0.5f);
                hit.y = SampleRuntimeHeight(runtime, hit.x, hit.z);
                return true;
            }

            previousT = t;
            previousDelta = delta;
        }

        return false;
    }

    private static bool RaycastXZBounds(Ray ray, float maxX, float maxZ, out float enter, out float exit)
    {
        enter = float.NegativeInfinity;
        exit = float.PositiveInfinity;
        return ClipRayAxis(ray.origin.x, ray.direction.x, 0f, maxX, ref enter, ref exit)
            && ClipRayAxis(ray.origin.z, ray.direction.z, 0f, maxZ, ref enter, ref exit);
    }

    /// <summary>Public wrapper — reused by SC2LiveHeightGrid's own ray-march
    /// (same XZ-bounds clip, different heightmap source).</summary>
    public static bool RaycastXZBoundsPublic(Ray ray, float maxX, float maxZ, out float enter, out float exit) =>
        RaycastXZBounds(ray, maxX, maxZ, out enter, out exit);

    private static bool ClipRayAxis(float origin, float direction, float min, float max, ref float enter, ref float exit)
    {
        if (Mathf.Abs(direction) < 1e-6f)
            return origin >= min && origin <= max;

        var t0 = (min - origin) / direction;
        var t1 = (max - origin) / direction;
        if (t0 > t1)
        {
            var tmp = t0;
            t0 = t1;
            t1 = tmp;
        }

        enter = Mathf.Max(enter, t0);
        exit = Mathf.Min(exit, t1);
        return enter <= exit;
    }

    private static float TerrainRayDelta(SC2TerrainRuntime runtime, Ray ray, float t)
    {
        var p = ray.GetPoint(t);
        return p.y - SampleRuntimeHeight(runtime, p.x, p.z);
    }

    private static float SampleRuntimeHeight(SC2TerrainRuntime runtime, float worldX, float worldZ)
    {
        var gx = Mathf.Clamp(worldX / runtime.CellSize, 0f, runtime.GridWidth);
        var gz = Mathf.Clamp(worldZ / runtime.CellSize, 0f, runtime.GridHeight);
        var x0 = Mathf.FloorToInt(gx);
        var z0 = Mathf.FloorToInt(gz);
        var x1 = Mathf.Min(runtime.GridWidth, x0 + 1);
        var z1 = Mathf.Min(runtime.GridHeight, z0 + 1);
        var tx = gx - x0;
        var tz = gz - z0;
        var h00 = runtime.GetHeight(x0, z0);
        var h10 = runtime.GetHeight(x1, z0);
        var h01 = runtime.GetHeight(x0, z1);
        var h11 = runtime.GetHeight(x1, z1);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
    }

    private static void DrawGuiCircle(Vector2 center, float radius, Color color, int segments)
    {
        if (Event.current.type != EventType.Repaint)
            return;

        var previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(
            new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f),
            GetBrushRingTexture(),
            ScaleMode.StretchToFill,
            true);
        GUI.color = previousColor;
    }

    private static Texture2D GetBrushRingTexture()
    {
        if (_brushRingTexture != null)
            return _brushRingTexture;

        const int size = 64;
        _brushRingTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var center = (size - 1) * 0.5f;
        var outer = center - 1f;
        var inner = outer - 2.5f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - center;
            var dy = y - center;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            var alpha = Mathf.Clamp01(outer - d + 1f) * Mathf.Clamp01(d - inner + 1f);
            _brushRingTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }

        _brushRingTexture.Apply(false, true);
        return _brushRingTexture;
    }

    private static Texture2D _solidDotTexture;

    /// <summary>Same anti-aliased-circle bake as GetBrushRingTexture, minus
    /// the hollow center — a filled dot for DrawSymmetryPointMarker.</summary>
    private static Texture2D GetSolidDotTexture()
    {
        if (_solidDotTexture != null)
            return _solidDotTexture;

        const int size = 32;
        _solidDotTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var center = (size - 1) * 0.5f;
        var outer = center - 1f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - center;
            var dy = y - center;
            var d = Mathf.Sqrt(dx * dx + dy * dy);
            var alpha = Mathf.Clamp01(outer - d + 1f);
            _solidDotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }

        _solidDotTexture.Apply(false, true);
        return _solidDotTexture;
    }

    private static int StableTexturePickerId(string key)
    {
        unchecked
        {
            var hash = 5381;
            var text = "SC2TexturePicker:" + key;
            for (var i = 0; i < text.Length; i++)
                hash = ((hash << 5) + hash) ^ text[i];
            return hash;
        }
    }

    private static Texture2D PollTexturePicker(Texture2D current, string key, int controlId)
    {
#if UNITY_EDITOR
        if (_activeTexturePickerId != controlId || _activeTexturePickerKey != key)
            return current;
        if (EditorGUIUtility.GetObjectPickerControlID() != controlId)
            return current;

        var picked = EditorGUIUtility.GetObjectPickerObject() as Texture2D;
        if (picked != null && picked != current)
        {
            GUI.changed = true;
            EditorApplication.QueuePlayerLoopUpdate();
            return picked;
        }

        return current;
#else
        // Runtime (Build & Run) resolution happens in DrawTexturePicker via
        // _runtimePickerPending*; the native EditorGUIUtility picker protocol
        // used above does not exist outside the Editor.
        return current;
#endif
    }

#if !UNITY_EDITOR
    private static Texture2D ConsumeRuntimeTexturePickerResult(Texture2D current, string pickerKey)
    {
        if (!_runtimePickerPendingIsSet || _runtimePickerPendingKey != pickerKey)
            return current;

        var texture = _runtimePickerPendingResult;
        GUI.changed = true;
        _runtimePickerPendingIsSet = false;
        _runtimePickerPendingKey = null;
        _runtimePickerPendingResult = null;
        return texture;
    }

    private static void OpenRuntimeTexturePicker(string key, Texture2D current)
    {
        _runtimePickerOpen = true;
        _runtimePickerKey = key;
        _runtimePickerScroll = Vector2.zero;
        _runtimePickerSearch = "";
        _runtimePickerCurrent = current;
        _runtimePickerCandidates = CollectRuntimeTextureCandidates(current);
        _runtimePickerSelectedIndex = _runtimePickerCandidates != null ? Mathf.Max(0, _runtimePickerCandidates.IndexOf(current)) : -1;
    }

    private static System.Collections.Generic.List<Texture2D> CollectRuntimeTextureCandidates(Texture2D current)
    {
        var result = new System.Collections.Generic.List<Texture2D>();
        var seen = new System.Collections.Generic.HashSet<Texture2D>();

        void Add(Texture2D t)
        {
            if (t != null && seen.Add(t))
                result.Add(t);
        }

        Add(current);

        // Textures bundled under any Resources folder in the build. This is the
        // standard way to ship browsable assets to a standalone player, since the
        // Project/AssetDatabase browser used by the Editor object picker does not
        // exist at runtime.
        // Legacy picker path retained only for save compatibility. Never
        // enumerate/load the complete Resources library; the shared catalog
        // browser handles discovery from metadata.
        var loaded = System.Array.Empty<Texture2D>();
        for (var i = 0; i < loaded.Length; i++)
            Add(loaded[i]);

        result.Sort((a, b) =>
        {
            if (a == current) return -1;
            if (b == current) return 1;
            return string.Compare(TextureDisplayName(a), TextureDisplayName(b), System.StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    /// <summary>
    /// Draws the runtime texture-picker popup, if open. Must be called once per
    /// frame from the host's OnGUI, after all other panel GUI, so the popup draws
    /// on top and its clicks are not intercepted by underlying controls.
    /// </summary>
    private static void DrawLegacyRuntimeTexturePickerOverlay()
    {
        if (!_runtimePickerOpen)
            return;

        var screenRect = new Rect(0f, 0f, Screen.width, Screen.height);
        var prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(screenRect, Texture2D.whiteTexture);
        GUI.color = prevColor;

        var winWidth = Mathf.Min(760f, Screen.width - 40f);
        var winHeight = Mathf.Min(560f, Screen.height - 40f);
        var winRect = new Rect((Screen.width - winWidth) * 0.5f, (Screen.height - winHeight) * 0.5f, winWidth, winHeight);

        GUILayout.BeginArea(winRect, GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Texture Browser", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        GUILayout.Label(_runtimePickerKey ?? "", EditorStyles.miniLabel);
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);

        var candidates = _runtimePickerCandidates;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(SC2UiScale.Pxf(52f)));
        GUI.SetNextControlName("SC2_RuntimeTextureSearch");
        _runtimePickerSearch = GUILayout.TextField(_runtimePickerSearch ?? "", GUILayout.Height(SC2UiScale.Pxf(22f)));
        if (GUILayout.Button("x", GUILayout.Width(SC2UiScale.Pxf(24f)), GUILayout.Height(SC2UiScale.Pxf(22f))))
            _runtimePickerSearch = "";
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(Mathf.Max(260f, winWidth - 270f)));
        _runtimePickerScroll = GUILayout.BeginScrollView(_runtimePickerScroll, GUILayout.Height(winHeight - 118f));
        if (candidates == null || candidates.Count == 0)
        {
            GUILayout.Label("No textures found. Place browsable textures under a\n'Resources' folder so they are included in the build.", EditorStyles.miniLabel);
        }
        else
        {
            const float tile = 86f;
            var usableWidth = Mathf.Max(tile, winWidth - 300f);
            var columns = Mathf.Max(1, Mathf.FloorToInt(usableWidth / (tile + 8f)));
            var visible = FilterRuntimeTextureCandidates(candidates, _runtimePickerSearch);
            var visibleIndex = 0;
            while (visibleIndex < visible.Count)
            {
                GUILayout.BeginHorizontal();
                for (var c = 0; c < columns && visibleIndex < visible.Count; c++, visibleIndex++)
                {
                    var idx = visible[visibleIndex];
                    var tex = candidates[idx];
                    DrawRuntimeTextureTile(tex, idx, tile);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }
        GUILayout.EndScrollView();
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUILayout.Width(SC2UiScale.Pxf(230f)));
        DrawRuntimeTexturePreviewPanel();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        // Restores whatever GUI.enabled was BEFORE this button (not
        // hardcode true) — see SC2ObjectsShell.DrawUndoRedoRow's own
        // comment for why.
        var guiEnabledBeforeUseSelected = GUI.enabled;
        GUI.enabled = guiEnabledBeforeUseSelected && _runtimePickerSelectedIndex >= 0
            && _runtimePickerCandidates != null
            && _runtimePickerSelectedIndex < _runtimePickerCandidates.Count;
        if (GUILayout.Button("Use Selected", GUILayout.Height(SC2UiScale.Pxf(28f))))
            AcceptRuntimeTexture(_runtimePickerCandidates[_runtimePickerSelectedIndex]);
        GUI.enabled = guiEnabledBeforeUseSelected;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("None", GUILayout.Height(SC2UiScale.Pxf(26f))))
        {
            AcceptRuntimeTexture(null);
        }
        if (GUILayout.Button("Cancel", GUILayout.Height(SC2UiScale.Pxf(26f))))
            _runtimePickerOpen = false;
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        if (Event.current != null && Event.current.type == EventType.KeyDown)
        {
            if (Event.current.keyCode == KeyCode.Escape)
            {
                _runtimePickerOpen = false;
                Event.current.Use();
            }
            else if ((Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && _runtimePickerSelectedIndex >= 0
                && _runtimePickerCandidates != null
                && _runtimePickerSelectedIndex < _runtimePickerCandidates.Count)
            {
                AcceptRuntimeTexture(_runtimePickerCandidates[_runtimePickerSelectedIndex]);
                Event.current.Use();
            }
        }

        // Swallow input so it doesn't fall through to the panel/viewport underneath.
        var e = Event.current;
        if (e != null && e.type != EventType.Repaint && e.type != EventType.Layout)
            e.Use();
    }

    private static System.Collections.Generic.List<int> FilterRuntimeTextureCandidates(System.Collections.Generic.List<Texture2D> candidates, string search)
    {
        var result = new System.Collections.Generic.List<int>();
        var needle = (search ?? "").Trim();
        for (var i = 0; i < candidates.Count; i++)
        {
            var tex = candidates[i];
            if (tex == null)
                continue;

            if (needle.Length == 0 || TextureDisplayName(tex).IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(i);
        }
        return result;
    }

    private static void DrawRuntimeTextureTile(Texture2D tex, int index, float size)
    {
        var selected = index == _runtimePickerSelectedIndex;
        var tileRect = GUILayoutUtility.GetRect(size, size + 30f, GUILayout.Width(size), GUILayout.Height(size + 30f));
        EditorGUI.DrawRect(tileRect, selected ? new Color(0.23f, 0.36f, 0.58f, 1f) : new Color(0.14f, 0.15f, 0.18f, 1f));
        EditorGUI.DrawRect(new Rect(tileRect.x, tileRect.y, tileRect.width, 1f), selected ? new Color(0.42f, 0.66f, 1f, 1f) : new Color(0.26f, 0.28f, 0.32f, 1f));
        EditorGUI.DrawRect(new Rect(tileRect.x, tileRect.yMax - 1f, tileRect.width, 1f), selected ? new Color(0.42f, 0.66f, 1f, 1f) : new Color(0.08f, 0.09f, 0.10f, 1f));
        EditorGUI.DrawRect(new Rect(tileRect.x, tileRect.y, 1f, tileRect.height), selected ? new Color(0.42f, 0.66f, 1f, 1f) : new Color(0.26f, 0.28f, 0.32f, 1f));
        EditorGUI.DrawRect(new Rect(tileRect.xMax - 1f, tileRect.y, 1f, tileRect.height), selected ? new Color(0.42f, 0.66f, 1f, 1f) : new Color(0.08f, 0.09f, 0.10f, 1f));

        var rect = new Rect(tileRect.x + 6f, tileRect.y + 6f, size - 12f, size - 12f);
        EditorGUI.DrawRect(rect, new Color(0.10f, 0.11f, 0.13f, 1f));
        if (tex != null)
            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, false);

        var labelStyle = SafeGuiStyle(EditorStyles.miniLabel);
        labelStyle.alignment = TextAnchor.MiddleCenter;
        labelStyle.clipping = TextClipping.Clip;
        var labelRect = new Rect(tileRect.x + 4f, tileRect.yMax - 24f, tileRect.width - 8f, 20f);
        GUI.Label(labelRect, TextureDisplayName(tex), labelStyle);

        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && e.button == 0 && tileRect.Contains(e.mousePosition))
        {
            var now = (double)Time.realtimeSinceStartup;
            var doubleClick = _runtimePickerSelectedIndex == index && now - _runtimePickerLastClickTime < 0.35;
            _runtimePickerSelectedIndex = index;
            _runtimePickerLastClickTime = now;
            if (doubleClick)
                AcceptRuntimeTexture(tex);
            e.Use();
        }
    }

    private static void DrawRuntimeTexturePreviewPanel()
    {
        Texture2D selected = null;
        if (_runtimePickerCandidates != null && _runtimePickerSelectedIndex >= 0 && _runtimePickerSelectedIndex < _runtimePickerCandidates.Count)
            selected = _runtimePickerCandidates[_runtimePickerSelectedIndex];

        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true));
        GUILayout.Label("Preview", EditorStyles.boldLabel);
        var previewRect = GUILayoutUtility.GetRect(SC2UiScale.Pxf(206f), SC2UiScale.Pxf(206f), GUILayout.Width(SC2UiScale.Pxf(206f)), GUILayout.Height(SC2UiScale.Pxf(206f)));
        EditorGUI.DrawRect(previewRect, new Color(0.10f, 0.11f, 0.13f, 1f));
        if (selected != null)
            GUI.DrawTexture(previewRect, selected, ScaleMode.ScaleToFit, false);
        else
            GUI.Label(previewRect, "No texture selected", EditorStyles.centeredGreyMiniLabel);

        GUILayout.Space(6f);
        GUILayout.Label(selected != null ? TextureDisplayName(selected) : "None", EditorStyles.boldLabel);
        if (selected != null)
        {
            GUILayout.Label($"{selected.width} x {selected.height}", EditorStyles.miniLabel);
            GUILayout.Label(selected.format.ToString(), EditorStyles.miniLabel);
            GUILayout.Label(selected.mipmapCount > 1 ? $"{selected.mipmapCount} mip levels" : "No mipmaps", EditorStyles.miniLabel);
            if (selected == _runtimePickerCurrent)
                GUILayout.Label("Current value", EditorStyles.miniLabel);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
    }

    private static void AcceptRuntimeTexture(Texture2D texture)
    {
        _runtimePickerPendingResult = texture;
        _runtimePickerPendingKey = _runtimePickerKey;
        _runtimePickerPendingIsSet = true;
        _runtimePickerOpen = false;
    }

    /// <summary>
    /// Bare tex.name is genuinely ambiguous in this project — the real
    /// texture library has thousands of leaf-name collisions across
    /// different material subfolders (numbered diffuse/normal/specular sets
    /// like "1_d"/"2_d" repeat everywhere by the source game's own naming
    /// convention; a live-project check found 1243 colliding names, and a
    /// sample of them confirmed genuinely different content, not harmless
    /// duplicates). Before this, the runtime texture picker listed every
    /// same-named candidate identically — picking "the right one" out of
    /// several identically-labeled thumbnails was a guess with no way to
    /// verify it after the fact. When SC2TextureManifest knows about more
    /// than one candidate for this name, this appends the specific
    /// subfolder this exact Texture2D resolves to (via
    /// SC2TerrainMaterialFactory.ResolveResourcePath, which disambiguates by
    /// reference — see its own doc comment), so the list actually
    /// distinguishes them.
    /// </summary>
    private static string TextureDisplayName(Texture2D tex)
    {
        if (tex == null || string.IsNullOrEmpty(tex.name))
            return "<unnamed>";

        if (SC2TextureManifest.GetCandidatePaths(tex.name).Count <= 1)
            return tex.name;

        var path = SC2TerrainMaterialFactory.ResolveResourcePath(tex);
        if (string.IsNullOrEmpty(path))
            return tex.name;

        var root = SC2TerrainMaterialFactory.ResourcesTextureRoot + "/";
        var relative = path.StartsWith(root, System.StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
        var slash = relative.LastIndexOf('/');
        var subfolder = slash >= 0 ? relative[..slash] : "";
        return string.IsNullOrEmpty(subfolder) ? tex.name : $"{tex.name}  ({subfolder})";
    }
#endif

    public static void DrawRuntimeTexturePickerOverlay() => SC2TextureBrowser.DrawOverlay();

    // ── Panels ───────────────────────────────────────────────────────────────

    private void DrawNewDataPanel()
    {
#if UNITY_EDITOR
        // Create the asset directly under Assets/SC2/Resources/<name>.asset so
        // both the Editor and a build load the exact same data via
        // Resources.Load (see SC2GameViewEditorUI.SharedTerrainDataResourceName).
        // No file dialog / arbitrary location: the whole point is that the path
        // is fixed and known so the build can find it without any scene wiring.
        DrawUnifiedNote("No shared terrain data found. Create it under Resources so the Editor and a Build & Run player load the same asset.");
        if (GUILayout.Button("Create shared SC2TerrainData"))
        {
            const string dir = "Assets/SC2/Resources";
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            var path = $"{dir}/{SC2GameViewEditorUI.SharedTerrainDataResourceName}.asset";
            _data = ScriptableObject.CreateInstance<SC2TerrainData>();
            _data.name = SC2GameViewEditorUI.SharedTerrainDataResourceName;
            AssetDatabase.CreateAsset(_data, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            UpdateDataSourceLabel(_data);
            OnDataChanged();
        }
#else
        // A standalone build must never fabricate a blank in-memory instance —
        // that's exactly what silently diverged from the Editor's data. If the
        // shared Resources asset wasn't created (and thus baked into the build),
        // say so instead of inventing empty data.
        DrawUnifiedNote(
            "No terrain data is baked into this build.\n" +
            $"Create it in the Unity Editor first (it will be saved to Resources as '{SC2GameViewEditorUI.SharedTerrainDataResourceName}'), " +
            "then rebuild — the Editor and this build will then load the same asset.");
#endif
    }

    private void DrawInitPanel()
    {
        EditorGUILayout.LabelField("Terrain Initialization", SafeGuiStyle(EditorStyles.miniBoldLabel));
        DrawUnifiedNote(
            "Map size sets the PHYSICAL size (world units). Resolution is fixed " +
            $"at {kTargetTriCount:N0} triangles ({Mathf.RoundToInt(Mathf.Sqrt(kTargetTriCount / 2f))}x" +
            $"{Mathf.RoundToInt(Mathf.Sqrt(kTargetTriCount / 2f))} quads). " +
            "Larger maps get bigger cells, not more polygons.");

        EditorGUILayout.LabelField("Map Size (world units)", SafeGuiStyle(EditorStyles.miniBoldLabel));
        EditorGUILayout.BeginHorizontal();
        SizeButton(512); SizeButton(1024); SizeButton(2048);
        EditorGUILayout.EndHorizontal();
        GUI.SetNextControlName("SC2_InitMapSizeWU");
        _initMapSizeWU = EditorGUILayout.FloatField("Custom Size (wu)", _initMapSizeWU);

        var side = Mathf.Max(8, Mathf.RoundToInt(Mathf.Sqrt(kTargetTriCount / 2f)));
        var cellSize = _initMapSizeWU / side;
        var actualTris = side * side * 2;

        DrawUnifiedNote(
            $"Grid: {side}x{side} quads, CellSize={cellSize:F4} wu/quad, " +
            $"Verts: {(side + 1) * (side + 1):N0}, Tris: {actualTris:N0}");

        if (GUILayout.Button($"Create flat terrain ({SC2TerrainData.DefaultFlatHeight:0} wu)", GUILayout.Height(SC2UiScale.Pxf(30f))))
        {
            RecordUndoCheckpoint("Init");
            _data.InitializeFlatPlane(side, side, cellSize);
            // side is a fixed triangle-budget sculpting resolution, unrelated
            // to the physical map size picked above — CostsGridSize (the
            // exported hfield/Costs grid's own resolution) must track that
            // world-unit size instead, or a map created at "512" silently
            // exports/paints Costs at the 1024 default.
            _data.CostsGridSize = Mathf.RoundToInt(_initMapSizeWU);
            EditorUtility.SetDirty(_data);
            SchedulePreviewBuild();
        }
        EditorGUILayout.Space(4);
        // Wider range than SC2TerrainData.MaxHeight on purpose — same
        // reasoning as SC2GeometryMode's own "Height Scale" slider: the
        // final imported height is still clamped to [MinHeight, MaxHeight]
        // by ImportHeightmap, so this is a pre-clamp grayscale multiplier,
        // not a hard height cap.
        _initHeightScale = DrawCompactSlider("Max Height", _initHeightScale, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight * 8f);
#if UNITY_EDITOR
        // Editor workflow: pick any Texture2D asset already in the project
        // (EditorGUIUtility.ShowObjectPicker via DrawTexturePicker) — the
        // artist has already imported the heightmap as a normal asset, with
        // Read/Write enabled so ImportHeightmap's GetPixels() call works.
        _initHeightmap = DrawTexturePicker("Heightmap", _initHeightmap, "InitHeightmap") as Texture2D;
        EditorGUI.BeginDisabledGroup(_initHeightmap == null);
        if (GUILayout.Button("Import Heightmap", GUILayout.Height(SC2UiScale.Pxf(26f))))
        {
            RecordUndoCheckpoint("Import HM");
            _data.InitializeFlatPlane(side, side, cellSize);
            _data.CostsGridSize = Mathf.RoundToInt(_initMapSizeWU);
            try
            {
                _data.ImportHeightmap(_initHeightmap, _initHeightScale);
                EditorUtility.SetDirty(_data);
                SchedulePreviewBuild();
            }
            catch (UnityException ex)
            {
                EditorUtility.DisplayDialog(
                    "Heightmap import failed",
                    "Unity could not read this texture. Enable Read/Write on the texture import settings, or use a readable source texture.\n\n" + ex.Message,
                    "OK");
            }
        }
        EditorGUI.EndDisabledGroup();
#else
        // Build & Run: there is no project/AssetDatabase to pick a Texture2D
        // asset FROM — the object-picker button this used to reuse
        // (DrawTexturePicker's runtime path) only ever browses textures
        // already bundled under Resources, which will almost never include
        // an author's actual heightmap image. A real "import" in a build has
        // to read an arbitrary external file instead, via the same native
        // file dialog terrain.bdf/decals.bdf import already use.
        if (GUILayout.Button("Import Heightmap from file...", GUILayout.Height(SC2UiScale.Pxf(26f))))
            ImportHeightmapFromFile(side, cellSize);
        if (!string.IsNullOrEmpty(_lastHeightmapImportError))
            DrawUnifiedNote(_lastHeightmapImportError);
#endif

        if (_previewBuildQueued)
            EditorGUILayout.LabelField("Terrain preview will rebuild after UI repaint...", SafeGuiStyle(EditorStyles.miniLabel));
    }

#if !UNITY_EDITOR
    private string _lastHeightmapImportError;

    private void ImportHeightmapFromFile(int side, float cellSize)
    {
        var tex = TryLoadHeightmapFromFile(out _lastHeightmapImportError);
        if (tex == null) return;

        try
        {
            RecordUndoCheckpoint("Import HM");
            _data.InitializeFlatPlane(side, side, cellSize);
            _data.CostsGridSize = Mathf.RoundToInt(_initMapSizeWU);
            _data.ImportHeightmap(tex, _initHeightScale);
            SchedulePreviewBuild();
        }
        catch (UnityException ex)
        {
            _lastHeightmapImportError = "Heightmap import failed: " + ex.Message;
            SC2Log.LogException(ex);
        }
        finally
        {
            UnityEngine.Object.Destroy(tex);
        }
    }
#endif

#if !UNITY_EDITOR
    /// <summary>Shared Build &amp; Run heightmap-file loader — reads an
    /// arbitrary external image via the native OS file dialog (same as
    /// terrain.bdf/decals.bdf import) and decodes it through
    /// ImageConversion.LoadImage, which always produces a readable Texture2D
    /// regardless of any import Read/Write setting (there IS no import
    /// setting for a texture built this way). Used by both this class's own
    /// Init-panel "flatten + import" flow and SC2GeometryMode's in-place
    /// "Import" (which previously reused the Editor-only DrawTexturePicker
    /// object-field, browsing only textures already bundled under
    /// Resources in a build — and calling Texture2D.GetPixels() on whatever
    /// non-Read/Write-enabled texture that picker happened to surface,
    /// which is exactly what crashed with "texture data is either not
    /// readable, corrupted or does not exist").
    ///
    /// Caller owns disposing the returned texture. Returns null with `error`
    /// set to null (not an error — the user just cancelled the dialog) or to
    /// an explanatory message if the file couldn't be read/decoded.</summary>
    public static Texture2D TryLoadHeightmapFromFile(out string error)
    {
        error = null;
        var path = SC2RuntimeFileDialog.OpenFile("Select heightmap image", "Image (*.png)", "png");
        if (string.IsNullOrEmpty(path))
            return null;

        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(path);
        }
        catch (System.Exception ex)
        {
            error = "Could not read the selected file: " + ex.Message;
            SC2Log.LogException(ex);
            return null;
        }

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        var decoded = ImageConversion.LoadImage(tex, bytes);
        if (!decoded || tex.width <= 2 || tex.height <= 2)
        {
            UnityEngine.Object.Destroy(tex);
            error = "Unity could not decode the selected file as an image. If this is a " +
                "16-bit-per-channel PNG, re-export it as an 8-bit grayscale PNG and try again — Unity's runtime " +
                "ImageConversion.LoadImage does not reliably support 16-bit source images (only Editor-time asset " +
                "import does).";
            return null;
        }

        // ImageConversion.LoadImage can return true while having silently
        // produced blank/garbage pixel data instead of throwing — the known
        // trigger is a 16-bit-per-channel PNG (common for authored
        // heightmaps, for more precision than 8-bit banding), which Unity's
        // RUNTIME decoder does not handle the same way the Editor's own
        // asset importer does. A genuine heightmap image has SOME grayscale
        // variation; a uniform result is a strong signal the decode didn't
        // actually work, so this is caught here instead of silently
        // "succeeding" into a flat, unimported terrain that looks
        // indistinguishable from doing nothing at all.
        var pixels = tex.GetPixels32();
        var first = pixels[0].r;
        var hasVariation = false;
        var sampleStride = Mathf.Max(1, pixels.Length / 256);
        for (var i = 1; i < pixels.Length; i += sampleStride)
        {
            if (pixels[i].r != first) { hasVariation = true; break; }
        }
        if (!hasVariation)
        {
            UnityEngine.Object.Destroy(tex);
            error = $"Decoded image ({tex.width}x{tex.height}) has no grayscale variation — " +
                "this usually means the decode silently failed, most often with a 16-bit-per-channel PNG. " +
                "Re-export the heightmap as an 8-bit grayscale PNG and try again.";
            return null;
        }

        return tex;
    }
#endif

    private void SizeButton(float wu)
    {
        var active = Mathf.Approximately(_initMapSizeWU, wu);
        var c = GUI.color;
        if (active) GUI.color = new Color(0.6f, 1f, 0.7f);
        if (GUILayout.Button($"{wu:F0}", GUILayout.Height(SC2UiScale.Pxf(24f)))) _initMapSizeWU = wu;
        GUI.color = c;
    }

    // EditorUtility.DisplayDialog/Debug.Log aren't visible in a standalone
    // build, so where "Save Terrain" actually wrote the file (Application
    // .persistentDataPath depends on Company/Product Name, which isn't
    // knowable from source) needs to be shown directly in this panel — same
    // pattern already used for Export's result note.
    private string _lastSaveInfo;

    private void DrawSaveToolbar()
    {
        var compact = EffectiveViewWidth < 390f;
        BeginUnifiedPanelBox();
        var labelStyle = SafeGuiStyle(EditorStyles.miniLabel);
        labelStyle.wordWrap = true;
        EditorGUILayout.LabelField("Terrain asset stores heights, materials, pairs, pair map, RNM masks and export settings.", labelStyle);
        if (compact)
        {
            if (GUILayout.Button("Save Terrain", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(GetUnifiedContentWidth()), GUILayout.MaxWidth(GetUnifiedContentWidth())))
                SaveState(writeAssets: true);
        }
        else
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save Terrain", GUILayout.Width(SC2UiScale.Pxf(110f))))
                SaveState(writeAssets: true);
            EditorGUILayout.EndHorizontal();
        }
        if (!string.IsNullOrEmpty(_lastSaveInfo))
            DrawUnifiedNote(_lastSaveInfo);
        EndUnifiedPanelBox();
    }

    // ── Terrain lifecycle ────────────────────────────────────────────────────

    private void OnDataChanged()
    {
        DisposeSubEditors();
        DestroyPreview();
        Repaint();
    }

    private void SchedulePreviewBuild()
    {
        DisposeSubEditors();
        DestroyPreview();
        _previewBuildQueued = true;
        _previewBuildAt = EditorApplication.timeSinceStartup + kPreviewBuildDelay;
        Repaint();
        EditorApplication.QueuePlayerLoopUpdate();
        // Same trigger as the preview mesh rebuild - the live height grid
        // needs the same "geometry might have
        // changed" signal.
        _liveHeightGrid.MarkDirty();
    }

    // ── Live height grid ───────────────────────────
    // One generated heightmap, source-agnostic (live procedural terrain +
    // imported terrain.bdf, merged the same way BuildExportArtifacts does) -
    // see SC2LiveHeightGrid's own header comment for the full rationale.
    private readonly SC2LiveHeightGrid _liveHeightGrid = new();

    /// <summary>Call whenever something outside the normal edit-triggered
    /// SchedulePreviewBuild path might have changed live or imported
    /// geometry - import add/remove/undo/redo, ImportOnlyMode toggle.</summary>
    public void InvalidateLiveHeightGrid() => _liveHeightGrid.MarkDirty();

    /// <summary>See SC2LiveHeightGrid.EnsureFreshImmediate's own doc
    /// comment — forces the live height grid to reflect the CURRENT terrain
    /// right now instead of MarkDirty's normal debounce window, for a
    /// consumer (SC2DecalsShell.EnsurePreview) that queries it the same
    /// frame a stroke completes and cannot tolerate serving one-stroke-
    /// stale data.</summary>
    public void RefreshLiveHeightGridImmediate() => _liveHeightGrid.EnsureFreshImmediate(_data, GetImportedTerrainsForExport);

    /// <summary>Ensures the grid is fresh (cheap no-op if not dirty) and
    /// ray-marches it - the single height-query entry point every caller
    /// (Costs, and eventually Objects/drag) should use instead of picking
    /// between TryRaycastImportedTerrain/RaycastTerrainHeight themselves.</summary>
    public bool TryRaycastLiveHeightGrid(Ray ray, out Vector3 hit)
    {
        _liveHeightGrid.EnsureFresh(_data, GetImportedTerrainsForExport);
        return _liveHeightGrid.TryRaycast(ray, out hit);
    }

    /// <summary>Bilinear height lookup (world X,Z -> world Y) against the
    /// LIVE runtime heightfield (Runtime.GetHeight) directly — unlike
    /// SampleLiveHeightGrid below, this is never cached/debounced, so it
    /// reflects a Geometry-mode brush stamp the instant it's applied, not
    /// only after the stroke ends (SC2LiveHeightGrid.EnsureFresh only
    /// rebuilds from Data.Heights, which itself only re-syncs from the
    /// runtime on stroke MouseUp — see SC2GeometryMode's own comment on
    /// that). Used by SC2TerrainShell.HeightBrushApplied subscribers
    /// (Objects/Decals reprojection) that need this stroke's real height,
    /// not the previous one. Procedural terrain only, no imported-terrain
    /// raycast fallback — callers that need that too should combine this
    /// with their own TryRaycastImportedTerrain check, same as
    /// SC2ObjectsShell.SampleHeight does.</summary>
    public float SampleLiveRuntimeHeight(float x, float z)
    {
        if (Runtime == null || _data == null || _data.CellSize <= 0f || _data.GridWidth <= 0 || _data.GridHeight <= 0) return 0f;
        var fx = Mathf.Clamp(x / _data.CellSize, 0f, _data.GridWidth);
        var fz = Mathf.Clamp(z / _data.CellSize, 0f, _data.GridHeight);
        var x0 = Mathf.FloorToInt(fx); var z0 = Mathf.FloorToInt(fz);
        var x1 = Mathf.Min(x0 + 1, _data.GridWidth); var z1 = Mathf.Min(z0 + 1, _data.GridHeight);
        var tx = fx - x0; var tz = fz - z0;
        var h00 = Runtime.GetHeight(x0, z0); var h10 = Runtime.GetHeight(x1, z0);
        var h01 = Runtime.GetHeight(x0, z1); var h11 = Runtime.GetHeight(x1, z1);
        var hx0 = Mathf.Lerp(h00, h10, tx); var hx1 = Mathf.Lerp(h01, h11, tx);
        return Mathf.Lerp(hx0, hx1, tz);
    }

    /// <summary>Same as SampleLiveRuntimeHeight above, but against the
    /// background's own runtime — world (x,z) translated by
    /// SC2BackgroundTerrain.GetWorldOffset into the runtime's local grid
    /// space first. Used by SC2GeometryMode/SC2PaintMode's brush gizmo so the
    /// reticle hugs the surface mid-drag instead of the pre-stroke height
    /// (the same staleness class SampleLiveRuntimeHeight itself was added
    /// to fix for the core).</summary>
    public float SampleLiveBackgroundHeight(float worldX, float worldZ)
    {
        var bg = _data?.BackgroundTerrainData;
        if (_bgRuntime == null || bg == null || bg.CellSize <= 0f || bg.GridWidth <= 0 || bg.GridHeight <= 0) return 0f;
        var offset = SC2BackgroundTerrain.GetWorldOffset(_data);
        var x = worldX - offset.x;
        var z = worldZ - offset.z;
        var fx = Mathf.Clamp(x / bg.CellSize, 0f, bg.GridWidth);
        var fz = Mathf.Clamp(z / bg.CellSize, 0f, bg.GridHeight);
        var x0 = Mathf.FloorToInt(fx); var z0 = Mathf.FloorToInt(fz);
        var x1 = Mathf.Min(x0 + 1, bg.GridWidth); var z1 = Mathf.Min(z0 + 1, bg.GridHeight);
        var tx = fx - x0; var tz = fz - z0;
        var h00 = _bgRuntime.GetHeight(x0, z0); var h10 = _bgRuntime.GetHeight(x1, z0);
        var h01 = _bgRuntime.GetHeight(x0, z1); var h11 = _bgRuntime.GetHeight(x1, z1);
        var hx0 = Mathf.Lerp(h00, h10, tx); var hx1 = Mathf.Lerp(h01, h11, tx);
        return Mathf.Lerp(hx0, hx1, tz);
    }

    /// <summary>Ensures the grid is fresh and returns a bilinear height
    /// lookup (world X,Z -> world Y) — used by SC2CostsAutoGen.
    /// GenerateFromHeight so Costs auto-generation reads the same merged
    /// live+imported heightmap Costs painting itself already moved to,
    /// instead of SC2ObjectsExport.SampleTerrainHeight's live-only
    /// Data.Heights lookup (which made auto-generation silently wrong on a
    /// fully-imported map).</summary>
    public float SampleLiveHeightGrid(float x, float z)
    {
        _liveHeightGrid.EnsureFresh(_data, GetImportedTerrainsForExport);
        return _liveHeightGrid.SampleHeight(x, z);
    }

    /// <summary>Ensures the grid is fresh and hands back the WHOLE projected
    /// heightmap at once (world-Y*128-encoded, same packing
    /// SC2HfieldProjectionService.Project always used) — for a caller that
    /// needs every point, not one XZ query at a time (e.g.
    /// SC2HeightmapExporter, exporting at the same canonical CostsGridSize+1
    /// resolution hfield.bdf/Costs/Collision2 already use, and which
    /// correctly includes any merged imported terrain the live procedural
    /// mesh's own Data.Heights never did). Returns false if there's no
    /// terrain to project.</summary>
    public bool TryGetLiveHeightGridSnapshot(out ushort[] heightmap128, out int mapX, out int mapY)
    {
        _liveHeightGrid.EnsureFresh(_data, GetImportedTerrainsForExport);
        var model = _liveHeightGrid.Model;
        if (model?.Heightmap == null)
        {
            heightmap128 = null; mapX = 0; mapY = 0;
            return false;
        }
        heightmap128 = model.Heightmap;
        mapX = (int)model.MapX;
        mapY = (int)model.MapY;
        return true;
    }

    // ── Top-panel Visibility menu overlays ───────
    // OverlayTerrain/OverlayRNM: SC2GameViewEditorUI calls these every frame
    // (regardless of active tab) so the two flags stay effective even when
    // the Terrain tab isn't the one currently drawn — mirroring how
    // Costs/Collision2 already keep their own overlays visible cross-tab.

    private bool _terrainOverlayVisible = true;
    public bool TerrainOverlayVisible => _terrainOverlayVisible;

    /// <summary>Show/hide the live procedural terrain's runtime chunks.
    /// Deliberately separate from SetBasePreviewVisible (which also flips
    /// the Simplified-mode preview mesh's visibility) — hiding Terrain via
    /// this overlay should never make the Simplified preview appear.
    /// Previously only ever touched the live runtime - imported
    /// terrain.bdf objects (a completely separate GameObject/MeshRenderer
    /// tree owned by SC2GameViewEditorUI) had no connection to this flag at
    /// all, so "Terrain" visibility did nothing on a map built from
    /// imported terrain. Re-fires OnPreviewModeChanged (already the
    /// mechanism SC2GameViewEditorUI uses to keep imported terrain's
    /// rendering in sync with the Terrain tab) so the host can also apply
    /// this to imported terrain's renderers.</summary>
    public void SetTerrainOverlayVisible(bool visible)
    {
        if (_terrainOverlayVisible == visible) return;
        _terrainOverlayVisible = visible;
        ApplyRuntimeVisibility();
        OnPreviewModeChanged?.Invoke(_prev);
    }

    private void ApplyRuntimeVisibility() =>
        // ImportOnlyMode already makes the live plane
        // contribute zero geometry to the exported terrain.bdf/hfield.bdf/
        // collision2.bdf (SC2TerrainLayerBuilder.Build) - it must be hidden
        // from the live viewport too, or the huge flat default plane still
        // visually covers/obscures the actual imported terrain.bdf while
        // editing, even though it's correctly excluded at export time.
        _runtime?.SetVisible(_mode != EditorMode.Simplified && _terrainOverlayVisible
            && (_data == null || !_data.ImportOnlyMode));

    private bool _waterOverlayVisible = true;

    /// <summary>Show/hide the live water plane preview (the
    /// top-panel Visibility menu "Water" checkbox) — was previously a dead
    /// toggle with no consumer; passed straight into SC2WaterPreview.Ensure
    /// each Update() tick, same wiring pattern as SetTerrainOverlayVisible/
    /// SetForceHfieldOverlay/SetForceRnmOverlay just above.</summary>
    public void SetWaterOverlayVisible(bool visible) => _waterOverlayVisible = visible;

    private bool _heightGridOverlayVisible;

    /// <summary>Shows the 1-cell heightmap grid across
    /// the map — same wiring pattern as SetWaterOverlayVisible above (set
    /// a field, Update() pushes it every tick to both the live runtime and
    /// every imported terrain.bdf material, so newly-imported terrain
    /// picks up the current state automatically).
    ///
    /// Confirmed via a real footprint-vs-grid comparison
    /// screenshot: the base level must be exactly 1 real WORLD unit per
    /// cell (matching Footprint.SizeX/SizeZ's own units, and Unity's own
    /// world-space X/Z distance), not Data.CellSize — that field is the
    /// terrain HEIGHTFIELD's own sampling resolution (defaults to 2 world
    /// units per sample), a completely different concept from "how big one
    /// placement/footprint unit is." Using it here made every grid square
    /// span 2 world units, so a real 2x2-unit building's footprint visibly
    /// spanned a 4x4 area of grid squares instead of exactly 1.</summary>
    public void SetHeightGridOverlay(bool visible) => _heightGridOverlayVisible = visible;

    private void PushHeightGridOverlay()
    {
        const float cellSize = 1f;
        _runtime?.SetHeightGridOverlay(_heightGridOverlayVisible, cellSize);
        var imported = GetImportedTerrainsForExport?.Invoke();
        if (imported == null) return;
        foreach (var entry in imported)
        {
            var renderer = entry?.Transform != null ? entry.Transform.GetComponent<MeshRenderer>() : null;
            if (renderer == null) continue;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || !mat.HasProperty("_HeightGridEnabled")) continue;
                mat.SetFloat("_HeightGridEnabled", _heightGridOverlayVisible ? 1f : 0f);
                if (mat.HasProperty("_HeightGridCellSize"))
                    mat.SetFloat("_HeightGridCellSize", cellSize);
            }
        }
    }

    private bool _wireframeOverlayVisible;

    /// <summary>Blender-style edit-mode wireframe over the live terrain —
    /// same field+Update()-push wiring as SetHeightGridOverlay above, but
    /// cellSize (pushed below as _data.CellSize, the mesh's own real
    /// per-quad resolution) draws the actual triangulation instead of an
    /// arbitrary 1-world-unit reference grid.</summary>
    public void SetWireframeOverlay(bool visible) => _wireframeOverlayVisible = visible;

    private void PushWireframeOverlay()
    {
        var cellSize = _data != null ? _data.CellSize : 1f;
        _runtime?.SetWireframeOverlay(_wireframeOverlayVisible, cellSize);
        var imported = GetImportedTerrainsForExport?.Invoke();
        if (imported == null) return;
        foreach (var entry in imported)
        {
            var renderer = entry?.Transform != null ? entry.Transform.GetComponent<MeshRenderer>() : null;
            if (renderer == null) continue;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || !mat.HasProperty("_WireframeEnabled")) continue;
                mat.SetFloat("_WireframeEnabled", _wireframeOverlayVisible ? 1f : 0f);
                if (mat.HasProperty("_WireframeCellSize"))
                    mat.SetFloat("_WireframeCellSize", cellSize);
            }
        }
    }

    private bool _forceHfieldOverlay;
    public bool ForceHfieldOverlay => _forceHfieldOverlay;

    /// <summary>No dedicated hfield-grid visualization exists in this codebase
    /// (SC2HfieldProjectionService is a pure export-time CPU rasterizer, no
    /// GameObject/RenderTexture) — reuses the height-gradient shader tint
    /// (PreviewMode.HeightGradient's own mechanism) as the closest available
    /// approximation of "show height data," independent of the Terrain tab's
    /// own preview-mode selection. Also re-fires
    /// OnPreviewModeChanged so imported terrain.bdf objects (which only
    /// reacted to explicit Terrain-tab preview-mode picks before, never to
    /// this flag) get the same forced height-gradient view applied - see
    /// SC2GameViewEditorUI.ApplyPreviewModeToImportedTerrain's "effective
    /// mode" computation.</summary>
    public void SetForceHfieldOverlay(bool enabled)
    {
        if (_forceHfieldOverlay == enabled) return;
        _forceHfieldOverlay = enabled;
        ApplyPreviewModeToChunks();
        OnPreviewModeChanged?.Invoke(_prev);
    }

    private bool _forceRnmOverlay;
    public bool ForceRnmOverlay => _forceRnmOverlay;

    // Tracks the RnmBakeGeneration last used to refresh RnmPreviewMask0/1/2
    // in ApplyPreviewModeToChunksFor — see that method's own comment on why
    // this gate exists (that refresh is expensive and this method runs on
    // nearly every repaint while PreviewMode==4 is active).
    private int _lastRnmPreviewGeneration = -1;

    /// <summary>Forces the RNM-textured shader mode (PreviewMode.TexturedRNM's
    /// shaderMode==4) regardless of the Terrain tab's own selected preview
    /// mode. Only overrides the "normal" views (Textured/HeightGradient) —
    /// deliberately yields to MatColors/PairColors/Slope, which are explicit
    /// debug-view picks the user made inside the Terrain tab, not something
    /// this cross-cutting overlay should fight. Also re-fires
    /// OnPreviewModeChanged - see SetForceHfieldOverlay's own note, same
    /// reasoning applies here for imported terrain.</summary>
    public void SetForceRnmOverlay(bool enabled)
    {
        if (_forceRnmOverlay == enabled) return;
        _forceRnmOverlay = enabled;
        ApplyPreviewModeToChunks();
        OnPreviewModeChanged?.Invoke(_prev);
    }

    private void OnModeChanged()
    {
        EnsureSubEditors();
        SetBasePreviewVisible(_mode != EditorMode.Simplified);
        ApplyRuntimeVisibility();
        if (_mode == EditorMode.Paint) _previewDirty = true;
        if (_mode == EditorMode.Simplified)
            _simplifiedMode.Rebuild();
        if (_mode == EditorMode.Pairs || _mode == EditorMode.Export)
            EnsurePairSync();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
    }

    private void SaveState(bool writeAssets)
    {
        if (_data == null) return;
        PreSaveSync?.Invoke();
        if (_runtime != null)
        {
            _data.Heights = _runtime.GetHeightsCopy();
            _data.InvalidateGeometryCache();
            if (_paintMode != null)
            {
                // Explicit-target overload — always the CORE, regardless of
                // whichever surface the brush was last pointing at (see
                // SC2PaintMode.SyncPairMapToCPU's own doc comment).
                _paintMode.SyncPairMapToCPU(_runtime, _data);
                MarkPairCpuDataSynced();
            }
            _runtime.FlushActiveBlendGroup(_data);
        }
        // The ONLY place that used to push Environment
        // edits from the live, always-current document
        // (Session.Document.Environment.WorkingHeader, kept in sync by
        // SC2EnvironmentShell.MaybeSnapshot every frame regardless of which
        // tab is active) into Data.EnvironmentHeader — the field that
        // actually gets serialized below — was SC2EnvironmentShell.Commit(),
        // fired only by the Environment tab's OWN "Save Terrain (incl.
        // Environment)" button. Saving from the Terrain tab's identically-
        // named "Save Terrain" button (or Ctrl+S/RequestSave) skipped this
        // entirely, silently serializing a stale EnvironmentHeader. Doing it
        // here instead means every save path commits Environment
        // consistently, not just one specific button.
        var envDoc = Session?.Document?.Environment;
        if (envDoc?.WorkingHeader != null)
        {
            _data.EnvironmentHeader = (byte[])envDoc.WorkingHeader.Clone();
            _data.EnvironmentMap = envDoc.EnvironmentMap;
            _data.SkyMap = envDoc.SkyMap;
            _data.TexturePath3 = envDoc.TexturePath3;
            _data.ColorCorrectionCube = envDoc.ColorCorrectionCube;
            _data.FogOfWarCorrectionCube = envDoc.FogOfWarCorrectionCube;
        }
        _data.EnsureSerializableState();
        EditorUtility.SetDirty(_data);
        if (writeAssets)
        {
#if UNITY_EDITOR
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
#else
            // AssetDatabase.SaveAssets()/Refresh() are Editor-only APIs — the
            // UnityEditorRuntimeShim versions are no-ops, since there is no
            // project/asset database in a standalone player. Without this,
            // "Save Terrain" silently did nothing in Build & Run and every
            // edit was lost on exit.
            // Session threaded through so the document-package dual-write
            // can persist the ACTIVE Decals document instead of rebuilding
            // it from the original imported decals.bdf bytes (or nothing,
            // for a from-scratch map) — confirmed real via the MapDocument
            // audit: Save Terrain was silently discarding every catalog
            // entry/placement/symmetry group SC2DecalsShell had authored.
            SC2RuntimeTerrainPersistence.Save(_data, Session);
            var savePath = SC2RuntimeTerrainPersistence.GetSavePath(_data.MapName);
            var savedBytes = System.IO.File.Exists(savePath) ? new System.IO.FileInfo(savePath).Length : -1L;
            _lastSaveInfo = $"Saved to: {savePath}\n({savedBytes:N0} bytes, MapName='{_data.MapName}')";
#endif
        }
    }

    /// <summary>Public entry point for "Save Terrain" triggered from outside the panel (e.g. Ctrl+S, File menu).</summary>
    public bool RequestSave()
    {
        if (_data == null)
            return false;

        SaveState(writeAssets: true);
        return true;
    }

    /// <summary>Saved terrain names available to load (standalone builds only; see SC2RuntimeTerrainPersistence).</summary>
    public static System.Collections.Generic.List<string> ListSavedTerrains()
    {
#if !UNITY_EDITOR
        return SC2RuntimeTerrainPersistence.ListSavedTerrains();
#else
        return new System.Collections.Generic.List<string>();
#endif
    }

    /// <summary>Loads a previously-saved terrain by name (standalone builds only) and adopts it as the active data.</summary>
    public bool LoadFromDisk(string mapName) => LoadFromDisk(mapName, out _);

    /// <summary>loadedDocument out param (added for the same MapDocument
    /// audit fix as the Decals restore below): callers that need to sync
    /// scene state the document alone can't describe on its own — currently
    /// only imported terrain.bdf GameObjects, restored by
    /// SC2GameViewEditorUI.ApplyLoadedTerrainName — can now reach the full
    /// loaded document instead of just the bool success flag.</summary>
    public bool LoadFromDisk(string mapName, out SC2MapDocument loadedDocument)
    {
#if !UNITY_EDITOR
        if (!SC2RuntimeTerrainPersistence.TryLoad(mapName, out var loaded, out loadedDocument))
            return false;

        SetData(loaded,
            $"Source: loaded from saved runtime data '{loaded.name}'.\n" +
            $"Path: {SC2RuntimeTerrainPersistence.GetSavePath(mapName)}\n" +
            "This file existed and was parsed by SC2RuntimeTerrainPersistence.TryLoad.");

        // Restores the ACTIVE Decals document (catalog entries, placements,
        // symmetry groups) from what was actually saved — confirmed real
        // via the MapDocument audit that this data existed in the saved
        // package but nothing ever read it back into a live session.
        // loadedDocument is null on the legacy-JSON fallback path (no
        // document package existed), which SetData above already handled
        // correctly on its own.
        if (loadedDocument != null && Session?.Document?.Scenario != null)
        {
            var lobby = SC2.MapEditor.Document.SC2LobbyStartPosition.CloneList(loadedDocument.Scenario?.LobbyStartPositions);
            var realPositions = new System.Collections.Generic.Dictionary<string, Vector3>();
            foreach (var marker in loaded.ObjMarkers)
                if (!realPositions.ContainsKey(marker.Name)) realPositions[marker.Name] = marker.Position;
            SC2.MapEditor.Document.SC2LobbyStartPosition.AdoptBases(lobby, realPositions);
            Session.Document.Scenario.LobbyStartPositions = lobby;
        }
        if (loadedDocument?.Terrain?.ImportedRnm != null && Session?.Document?.Terrain != null)
            Session.Document.Terrain.ImportedRnm = new SC2.MapEditor.Document.SC2RnmDocument
            {
                MaskSets = loadedDocument.Terrain.ImportedRnm.CloneMaskSets(),
            };
        if (loadedDocument != null && Session?.Document?.Decals != null)
        {
            Session.Document.Decals.CopyFrom(loadedDocument.Decals);
            Session.Document.RaiseChanged(new SC2DocumentChangeSet
            {
                BeforeRevision = Session.Document.Revision,
                AfterRevision = Session.Document.Revision + 1,
                CommandName = "Load Terrain",
                DirectDirtyFlags = SC2.MapEditor.SmartExport.SC2DirtyFlags.Decals,
                ChangedDomains = new[] { SC2DomainId.Decals },
                InvalidatedDomains = System.Array.Empty<SC2DomainId>(),
                TimestampUtc = System.DateTime.UtcNow
            });
        }
        return true;
#else
        loadedDocument = null;
        return false;
#endif
    }

    /// <summary>
    /// Deletes a saved terrain's file from disk (standalone builds only). If it
    /// happens to be the terrain currently open, also discards the in-memory
    /// copy — its backing file is gone, so there is nothing left to save back to.
    /// </summary>
    public bool DeleteFromDisk(string mapName)
    {
#if !UNITY_EDITOR
        if (!SC2RuntimeTerrainPersistence.Delete(mapName))
            return false;

        if (_data != null && _data.MapName == mapName)
            SetData(null);
        return true;
#else
        return false;
#endif
    }

    /// <summary>
    /// Discards the current terrain (in-memory only — does not touch any saved
    /// file) and drops back to the "select or create a terrain" panel, letting
    /// the user start fresh without leaving Build &amp; Run. Works the same in
    /// the Editor (SetData(null) just shows the "Create new SC2TerrainData..."
    /// panel with the normal asset-creation flow).
    /// </summary>
    public void NewTerrain() => SetData(null);

    /// <summary>Shared side/cellSize derivation for a flat plane of the given
    /// physical size — same formula DrawInitPanel's own "Create flat terrain"
    /// button already uses, exposed here so the startup screen's "Create New
    /// Map"/"Open archive map" flows (which build a brand new SC2TerrainData
    /// directly, not through this shell's own init panel) can agree with it
    /// instead of guessing a different resolution.</summary>
    public static (int side, float cellSize) ComputeFlatPlaneGrid(float sizeWu)
    {
        var side = Mathf.Max(8, Mathf.RoundToInt(Mathf.Sqrt(kTargetTriCount / 2f)));
        var cellSize = sizeWu / side;
        return (side, cellSize);
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private void EnsurePreview()
    {
        DestroyLegacyPreviewMesh();
        if (_runtime != null) return;

        _runtime = new SC2TerrainRuntime();
        _runtime.Init(_data.GridWidth, _data.GridHeight, _data.CellSize, 200f);
        if (_data.Heights != null && _data.Heights.Length > 0)
            _runtime.UploadHeightsCPU(_data.Heights);
        if (_data.TrianglePairIndex != null && _data.TrianglePairIndex.Length > 0)
            _runtime.RestorePairMapFromData(_data);
        _runtime.RestoreBlendRTFromData(_data);
        ApplyRuntimeVisibility();
        ApplyPreviewModeToChunks();

        // The old single-mesh preview duplicates the runtime chunk terrain.
        // Keeping both active causes z-fighting at the default flat height (100 wu),
        // and once the runtime terrain is sculpted below 100 the stale mesh remains
        // visible above it as pale/white square artifacts. Runtime chunks are now
        // the only live terrain preview outside the Simplified tab.
    }

    private void DestroyLegacyPreviewMesh()
    {
        if (_previewGO != null) UnityEngine.Object.DestroyImmediate(_previewGO);
        if (_previewMesh != null) UnityEngine.Object.DestroyImmediate(_previewMesh);
        SC2PreviewMaterialFactory.Destroy(_previewOwnedMaterials);
        _previewGO = null;
        _meshFilter = null;
        _meshRenderer = null;
        _previewMesh = null;
        _previewOwnedMaterials = null;
    }

    private void DestroyPreview()
    {
        _runtime?.Dispose();
        _runtime = null;
        DestroyLegacyPreviewMesh();
        _simplifiedMode?.Destroy();
    }

    // ── Sub-editors ──────────────────────────────────────────────────────────

    private void EnsureSubEditors()
    {
        if (_geoMode == null || _geoMode.OwnerData != _data)
        {
            _geoMode = new SC2GeometryMode(this, _data);
            if (_data != null && _data.IsInitialized && !_previewBuildQueued) EnsurePreview();
        }
        if (_matMode == null || _matMode.Data != _data) _matMode = new SC2MaterialsMode(this, _data);
        if (_pairsMode == null || _pairsMode.Data != _data) _pairsMode = new SC2PairsMode(this, _data);
        if (_paintMode == null || _paintMode.OwnerData != _data) _paintMode = new SC2PaintMode(this, _data);
        if (_simplifiedMode == null || _simplifiedMode.Data != _data) _simplifiedMode = new SC2SimplifiedPreviewMode(this, _data);
        if (_exportPanel == null || _exportPanel.Data != _data) _exportPanel = new SC2ExportPanel(this, _data);
    }

    private void DisposeSubEditors()
    {
        _geoMode = null; _matMode = null; _pairsMode = null;
        _paintMode = null; _simplifiedMode = null; _exportPanel = null;
    }

    // ── Preview rendering (copied from TerrainEditorWindow) ──────────────────

    // internal (not private): SC2PairsMode's Global UV Scale controls
    // (moved there from the Background tab) need to re-apply the preview
    // after toggling/changing it, same as every other call site here.
    internal void ApplyPreviewModeToChunks() => ApplyPreviewModeToChunksFor(_runtime, _data);

    /// <summary>Parameterized so the background's own SC2TerrainRuntime can
    /// share this exact same Textured/RNM/Mats/Pairs/Slope logic instead of
    /// a second copy — the shell-level preview-mode state (_prev,
    /// _forceRnmOverlay, _forceHfieldOverlay, height-gradient min/max) is
    /// already a single, map-wide selector applying to both (matching how
    /// the old SC2BackgroundTerrainPreview.Ensure took the same
    /// bgPreviewMode int the core's own toggle drives).</summary>
    private void ApplyPreviewModeToChunksFor(SC2TerrainRuntime runtime, SC2TerrainData data)
    {
        if (runtime == null || data == null) return;

        if (_prev == PreviewMode.PairColors)
        {
            runtime.ApplyPairColourView(data, false);
            runtime.SetPreviewMode(2);
            runtime.SetHeightGradientOverlay(false, _heightGradientMin, _heightGradientMax);
            return;
        }
        if (_prev == PreviewMode.Slope)
        {
            runtime.SetPreviewMode(3);
            runtime.SetHeightGradientOverlay(false, _heightGradientMin, _heightGradientMax);
            return;
        }

        int shaderMode = _prev switch
        {
            // ForceRnmOverlay only overrides the "normal" views (Textured/
            // HeightGradient) — MatColors keeps its own explicit debug view.
            PreviewMode.Textured => _forceRnmOverlay ? 4 : 0,
            PreviewMode.TexturedRNM => 4,
            PreviewMode.MatColors => 1,
            PreviewMode.HeightGradient => _forceRnmOverlay ? 4 : 0,
            _ => 0,
        };
        var n = Mathf.Min(data.Pairs.Count, 72);
        var mats = new SC2TerrainRuntime.PairMaterialData[n];
        // RefreshRnmPreviewTextures rebuilds RnmPreviewMask0/1/2 from a
        // pair's baked bytes (a raw LoadRawTextureData+Apply across up to
        // MaskWidth*MaskHeight texels) — genuinely expensive, and this
        // method runs on essentially every repaint/mouse-move while a
        // preview mode is active (confirmed: 18k+ calls in one session), so
        // calling it unconditionally whenever shaderMode==4 was the actual
        // remaining RNM-mode-only lag (the array-rebuild gate added earlier
        // only covered RebuildRnmArrays itself, not this upstream step).
        // Only worth redoing when the bake data has actually changed.
        if (shaderMode == 4 && _lastRnmPreviewGeneration != _data.RnmBakeGeneration)
        {
            foreach (var pair in data.Pairs) pair.RefreshRnmPreviewTextures();
            _lastRnmPreviewGeneration = _data.RnmBakeGeneration;
        }
        for (var i = 0; i < n; i++)
        {
            var p = data.Pairs[i];
            var m1 = p.Mat1Index >= 0 && p.Mat1Index < data.Materials.Count ? data.Materials[p.Mat1Index] : null;
            var m2 = p.Mat2Index >= 0 && p.Mat2Index < data.Materials.Count ? data.Materials[p.Mat2Index] : null;
            mats[i] = new SC2TerrainRuntime.PairMaterialData
            {
                Diffuse1 = m1?.Diffuse, Diffuse2 = m2?.Diffuse,
                Color1 = m1?.Color ?? Color.grey, Color2 = m2?.Color ?? Color.grey,
                // EffectiveUvTilingScale is documented as living only on the
                // CORE's own SC2TerrainData (GlobalUvScaleEnabled/Scale are
                // never set on the background's own nested instance) — must
                // call it on _data (always core), not the passed-in `data`
                // (core OR background depending on caller). Calling it on
                // `data` here was the actual bug: background's own chunk
                // mesh always fell back to each pair's per-pair TilingScale,
                // silently ignoring Global UV Scale regardless of the
                // toggle, while the seam skirt (SC2BackgroundSeamSkirt,
                // which already correctly used coreData) looked right —
                // exactly the "only the seam respects it" symptom reported.
                TilingScale = _data.EffectiveUvTilingScale(p),
                Rnm0 = shaderMode == 4 && p.HasBakedRnmLighting ? p.RnmPreviewMask0 : null,
                Rnm1 = shaderMode == 4 && p.HasBakedRnmLighting ? p.RnmPreviewMask1 : null,
                Rnm2 = shaderMode == 4 && p.HasBakedRnmLighting ? p.RnmPreviewMask2 : null,
            };
        }
        // _data.RnmBakeGeneration (never `data` — the bake config, and now
        // its generation counter, only ever lives on the core, matching
        // "there's only one Lighting Bake config" for both core and
        // background bakes — see SC2RnmBakeService.BakeInternal's own
        // bakeSettingsSource parameter).
        runtime.SetAllPairMaterials(mats, shaderMode, _data.RnmBakeGeneration);
        runtime.SetHeightGradientOverlay(_prev == PreviewMode.HeightGradient || _forceHfieldOverlay, _heightGradientMin, _heightGradientMax);

        // RNM basis-texture UV domain — see SC2TerrainRuntime.
        // SetRnmUvTransform's own doc comment. Set unconditionally (cheap
        // uniform writes) rather than only under shaderMode==4, so toggling
        // into TexturedRNM later doesn't need this method to re-run first.
        if (data == _data)
            // data.GridWidth*CellSize/GridHeight*CellSize — NOT a hardcoded
            // 1024 — matches SC2TerrainExporter/SC2RnmBakeService's own
            // Uv2 span for the core (see their shared fix's comment): a
            // hardcoded 1024 only ever happened to be correct for an
            // exactly-1024-unit map, and otherwise showed the RNM basis
            // preview sampling the wrong (too-small) fraction of the mask.
            runtime.SetRnmUvTransform(Vector2.zero,
                new Vector2(Mathf.Max(1f, data.GridWidth * data.CellSize), Mathf.Max(1f, data.GridHeight * data.CellSize)));
        else
        {
            // ComputeBounds returns WORLD-space bounds (matches
            // SC2TerrainExporter's own worldPos - bgMinX), but this
            // runtime's own chunk mesh stores raw LOCAL grid-space
            // coordinates in uv0 (RebuildChunk's uv1arr, pre-SetWorldOrigin
            // offset — only the transform position shifts vertices into
            // world space, not the UV data baked at chunk-build time). Must
            // subtract the same world offset back out of the bounds so both
            // sides of "(uv0 - offset)" in the shader are in the SAME
            // (local) frame.
            SC2BackgroundTerrain.ComputeBounds(_data, _data.BackgroundTerrainSize,
                out var bgMinX, out var bgMinZ, out var bgMaxX, out var bgMaxZ, out _);
            var worldOffset = SC2BackgroundTerrain.GetWorldOffset(_data);
            runtime.SetRnmUvTransform(new Vector2(bgMinX - worldOffset.x, bgMinZ - worldOffset.z),
                new Vector2(Mathf.Max(1f, bgMaxX - bgMinX), Mathf.Max(1f, bgMaxZ - bgMinZ)));
        }
    }

    private void RepaintPairColorRT()
    {
        if (_runtime == null) return;
        _runtime.ApplyPairColourView(_data, false);
    }

    private void BuildPreviewMeshWithSubMeshes()
    {
        _data.InvalidateGeometryCache();
        var verts = _data.Vertices;
        if (_previewMesh != null) UnityEngine.Object.DestroyImmediate(_previewMesh);
        SC2PreviewMaterialFactory.Destroy(_previewOwnedMaterials);
        _previewOwnedMaterials = null;

        var mesh = new Mesh { name = "SC2Preview", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        var uv2 = new Vector2[verts.Length];
        var uv1 = new Vector2[verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            uv2[i] = new Vector2(verts[i].x / 1024f, verts[i].z / 1024f);
            uv1[i] = new Vector2(verts[i].x, verts[i].z);
        }
        mesh.SetUVs(1, uv2);
        mesh.SetUVs(0, uv1);

        var pairTris = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
        var unassigned = new System.Collections.Generic.List<int>();
        var trisArr = _data.Triangles;
        for (var t = 0; t < _data.TrianglePairIndex.Length; t++)
        {
            var pi = _data.TrianglePairIndex[t];
            if (pi < 0)
            { unassigned.Add(trisArr[t * 3]); unassigned.Add(trisArr[t * 3 + 1]); unassigned.Add(trisArr[t * 3 + 2]); }
            else
            {
                if (!pairTris.TryGetValue(pi, out var lst)) pairTris[pi] = lst = new System.Collections.Generic.List<int>();
                lst.Add(trisArr[t * 3]); lst.Add(trisArr[t * 3 + 1]); lst.Add(trisArr[t * 3 + 2]);
            }
        }
        var activePairs = new System.Collections.Generic.List<int>(pairTris.Keys);
        activePairs.Sort();
        var totalSubs = activePairs.Count + (unassigned.Count > 0 ? 1 : 0);
        mesh.subMeshCount = Mathf.Max(1, totalSubs == 0 ? 1 : totalSubs);
        var mats = new Material[mesh.subMeshCount];
        var si = 0;
        if (totalSubs == 0)
        { mesh.SetTriangles(_data.Triangles, 0); mats[0] = SC2PreviewMaterialFactory.MakeGrey("NoData"); }
        else
        {
            foreach (var pi in activePairs)
            { mesh.SetTriangles(pairTris[pi], si); mats[si] = BuildPreviewMaterial(pi); si++; }
            if (unassigned.Count > 0)
            { mesh.SetTriangles(unassigned, si); mats[si] = SC2PreviewMaterialFactory.MakeGrey("Unassigned"); }
        }
        mesh.RecalculateBounds();
        _previewMesh = mesh;
        _previewOwnedMaterials = mats;
        _meshFilter.sharedMesh = mesh;
        _meshRenderer.sharedMaterials = mats;
    }

    private Material BuildPreviewMaterial(int pairIdx)
    {
        var pair = _data.Pairs[pairIdx];
        var m1 = pair.Mat1Index >= 0 && pair.Mat1Index < _data.Materials.Count ? _data.Materials[pair.Mat1Index] : null;
        var m2 = pair.Mat2Index >= 0 && pair.Mat2Index < _data.Materials.Count ? _data.Materials[pair.Mat2Index] : null;
        var col1 = m1 != null ? m1.Color : Color.grey;
        var col2 = m2 != null ? m2.Color : Color.grey;

        // Shader.Find("Unlit/Color")/("Standard")/("Diffuse") all reliably
        // return null in a standalone build (Unity strips any shader
        // nothing references), which used to throw "Value cannot be null.
        // Parameter name: shader" on `new Material(...)` in every branch
        // below — the same crash already fixed for SC2/Terrain (see
        // SC2TerrainMaterialFactory.ResolveShader, reused here).
        if (_prev == PreviewMode.MatColors)
        {
            var mat = new Material(SC2FallbackShader.Get()) { name = pair.Name, color = col1 };
            return mat;
        }
        if (_prev == PreviewMode.PairColors)
        {
            var blended = Color.Lerp(col1, col2, 0.5f);
            var mat = new Material(SC2FallbackShader.Get()) { name = pair.Name, color = blended };
            return mat;
        }
        if (_prev == PreviewMode.Slope)
        {
            var mat = new Material(SC2FallbackShader.Get()) { name = pair.Name, color = Color.grey };
            return mat;
        }

        var sc2 = SC2TerrainMaterialFactory.ResolveShader("SC2/Terrain", "SC2Terrain");
        if (sc2 != null)
        {
            var mat = new Material(sc2) { name = pair.Name };
            if (m1?.Diffuse != null) mat.SetTexture("_Diffuse1", m1.Diffuse);
            if (m2?.Diffuse != null) mat.SetTexture("_Diffuse2", m2.Diffuse);
            if (m1?.Normal != null) mat.SetTexture("_Normal1", m1.Normal);
            if (m2?.Normal != null) mat.SetTexture("_Normal2", m2.Normal);
            if (m1?.Mask != null) mat.SetTexture("_Mask1", m1.Mask);
            if (m2?.Mask != null) mat.SetTexture("_Mask2", m2.Mask);
            var tiling = Vector2.one / pair.TilingScale;
            mat.SetTextureScale("_Diffuse1", tiling);
            mat.SetTextureScale("_Diffuse2", tiling);
            mat.SetTextureScale("_Normal1", tiling);
            mat.SetTextureScale("_Normal2", tiling);
            pair.EnsureRnmTexture();
            if (pair.RnmMaskTexture != null)
            {
                mat.SetTexture("_RNMMask0", pair.RnmMaskTexture);
                mat.SetTexture("_RNMMask1", pair.RnmMaskTexture);
                mat.SetTexture("_RNMMask2", pair.RnmMaskTexture);
            }
            mat.SetFloat("_EnableRNM", 0f);
            mat.SetFloat("_EnableAmbient", 1f);
            mat.SetFloat("_EnableBlending", 1f);
            mat.SetFloat("_EnableFog", 0f);
            mat.SetColor("_AmbientColor", Color.white);
            return mat;
        }

        // Unreachable in practice (ResolveShader always falls back to
        // SC2FallbackShader.Get() before returning null) — kept only as a
        // last-resort guard, using the same guaranteed-available fallback.
        var fb = new Material(SC2FallbackShader.Get()) { name = pair.Name, color = col1 };
        if (m1?.Diffuse != null) fb.mainTexture = m1.Diffuse;
        return fb;
    }

    // ── Internal sync helpers ────────────────────────────────────────────────

    private void EnsurePairSync(bool force = false)
    {
        EnsureSubEditors();
        if (_paintMode == null || _runtime == null) return;
        var now = EditorApplication.timeSinceStartup;
        if (!force && now - _lastPairSync < kPairSyncInterval) return;
        _lastPairSync = now;
        // Explicit-target overload — always the CORE (see
        // SC2PaintMode.SyncPairMapToCPU's own doc comment).
        _paintMode.SyncPairMapToCPU(_runtime, _data);
        MarkPairCpuDataSynced();
    }


    // ── Keyboard shortcuts ───────────────────────────────────────────────────

    public bool HandleKeyboardShortcut(Event e)
    {
        if (e == null || e.type != EventType.KeyDown)
            return false;

        var focusedControl = GUI.GetNameOfFocusedControl();
        if (EditorGUIUtility.editingTextField || (!string.IsNullOrEmpty(focusedControl) && focusedControl.StartsWith("SC2Numeric_")))
            return false;

        // Delegate to active sub-editor
        switch (_mode)
        {
            case EditorMode.Geometry:
                return _geoMode?.HandleKeyboardShortcut(e) == true;
            case EditorMode.Pairs:
                return _pairsMode?.HandleKeyboardShortcut(e) == true;
            case EditorMode.Paint:
                return _paintMode?.HandleKeyboardShortcut(e) == true;
            case EditorMode.Simplified:
                return _simplifiedMode?.HandleKeyboardShortcut(e) == true;
        }
        return false;
    }
}

