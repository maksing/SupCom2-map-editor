using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using SC2.MapEditor.Document;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed partial class SC2GameViewEditorUI : MonoBehaviour
{
    private static readonly Vector3 DefaultMapCenter = new Vector3(512f, 0f, 512f);
    private const float DefaultTopDownHeight = 512f;
    // Scaled through SC2UiScale, not fixed constants — these three rows'
    // own BeginArea rects (DrawHeader/DrawSubtabs) stayed this literal
    // pixel size regardless of Scale while every style/button INSIDE them
    // (EnsureStyles) grew, so at higher Scale the taller real content no
    // longer fit its own row and either got clipped (BeginArea clips, it
    // doesn't wrap) or bled into the neighbouring row, with overlapping menu-bar/status text.
    // HeaderHeight (used to position _panelRect/_viewportRect below the
    // header) must track the same real total or the left panel/viewport
    // start too high, under where the taller header actually ends.
    private static float TitleBarHeight => SC2UiScale.Pxf(32f);
    private static float TabsRowHeight => SC2UiScale.Pxf(35.2f);
    private static float SubtabsRowHeight => SC2UiScale.Pxf(28.8f);
    private static float HeaderHeight => TitleBarHeight + TabsRowHeight + SubtabsRowHeight;
    private static SC2GameViewEditorUI _activeInstance;
#if !UNITY_EDITOR
    // See OnEnable's own comment on the wantsToQuit subscription for why
    // this is a static, subscribe-once-ever guard rather than per-instance.
    private static bool _wantsToQuitSubscribed;
#endif

    public bool ShowUI = true;
    public bool ShowPanel = true;
    /// <summary>Compact right-side panel (Measure tool button, reference
    /// units list, reference-image controls) — default ON, matching
    /// ShowPanel's own convention.</summary>
    public bool ShowReferencePanel;
    public bool EnableGameViewNavigation = true;
    public bool EnableHotkeys = true;
    public bool LogActions;
    private const string ZoomSpeedPrefKey = "SC2.GameView.ZoomSpeed";
    private float _zoomSpeed = -1f; // -1 = not loaded from PlayerPrefs yet

    /// <summary>Multiplier on ZoomCamera's per-scroll-tick zoom amount.
    /// 1.0 =
    /// the original hardcoded rate; range picked so 0.2 gives fine control
    /// for precise placement work and 3.0 covers a fast map-wide zoom
    /// without the slider needing an awkwardly large range for a value most
    /// users will only nudge slightly from default.
    /// This used to be a plain,
    /// non-persisted field, so it silently reset to 1f every time this
    /// component was recreated (editor re-entry, domain reload, Build
    /// relaunch) — unlike its sibling UI Scale slider drawn right below it
    /// in the same menu, which was already PlayerPrefs-backed (see
    /// SC2UiScale.Scale's own doc comment/pattern, mirrored here
    /// exactly).</summary>
    public float ZoomSpeed
    {
        get
        {
            if (_zoomSpeed < 0f) _zoomSpeed = Mathf.Clamp(PlayerPrefs.GetFloat(ZoomSpeedPrefKey, 1f), 0.2f, 3f);
            return _zoomSpeed;
        }
        set
        {
            var clamped = Mathf.Clamp(value, 0.2f, 3f);
            if (_zoomSpeed >= 0f && Mathf.Approximately(clamped, _zoomSpeed)) return;
            _zoomSpeed = clamped;
            PlayerPrefs.SetFloat(ZoomSpeedPrefKey, _zoomSpeed);
            PlayerPrefs.Save();
        }
    }

    public string MapName = "No map loaded";
    public string Status = "Ready";
    // Bump on every code change that must be verified in a Build & Run player.
    // Shown in the top status pill so a screenshot proves which build is running.
    public const string BuildTag = "camera-overlay-pixelspace-17";
    public SC2TerrainData RuntimeTerrainData;

    // The terrain data the Editor Game view and a standalone Build & Run player
    // both load, so they start from identical data instead of the build silently
    // spinning up a blank instance. Resources.Load works identically in the
    // Editor and in a build (no AssetDatabase needed), so the asset just has to
    // live under any Resources/ folder at this name — "Create new SC2TerrainData"
    // and Save target Assets/SC2/Resources/<name>.asset for exactly that reason.
    public const string SharedTerrainDataResourceName = "SC2TerrainData";

    /// <summary>
    /// Loads the shared SC2TerrainData asset from Resources — the same asset in
    /// the Editor and in a build. Returns null if it hasn't been created yet
    /// (first run before "Create new SC2TerrainData").
    /// </summary>
    public static SC2TerrainData LoadSharedTerrainData()
        => Resources.Load<SC2TerrainData>(SharedTerrainDataResourceName);

    private static string BuildTerrainLabel(string source, SC2TerrainData data)
    {
        if (data == null)
            return "";

        return
            $"Source: {source}.\n" +
            $"Loaded object: '{data.name}', MapName='{data.MapName}'.";
    }

    private static string SharedTerrainSourceLabel(SC2TerrainData data)
        => BuildTerrainLabel($"shared Resources asset '{SharedTerrainDataResourceName}' baked into the player", data);

    private static string RuntimeFieldTerrainSourceLabel(SC2TerrainData data)
        => BuildTerrainLabel("scene/inspector RuntimeTerrainData reference, not LocalLow JSON", data);

    /// <summary>
    /// Optional reference to the shared editor context. When set, overlay toggles
    /// in this GameView are synced bidirectionally with the context.
    /// </summary>
    public SC2EditorContext SharedContext;

    public bool OverlayTerrain = true;
    public bool OverlayHfield;
    public bool OverlayCosts;
    public bool OverlayCollision2;
    // The old design split "is this object
    // type visible" across TWO independent flags per type — a combined
    // "Objects" toggle gating only the flat 2D pick-dot for Props+Units
    // together, and a separate "Props" toggle gating only the REAL 3D
    // model — so unchecking Props hid the mesh but left its dot on screen,
    // and Units had no model-hide flag at all. Objects/Graphs are gone;
    // Props/Markers/Units below each now gate BOTH their dot and their real
    // model through one flag (see LateUpdate's HideProps/HideUnits lines
    // and SC2ObjectsShell.OnGameViewGUI's showProps/showUnits/showMarkers
    // params), and Graphs is split into one toggle per real path-graph kind
    // plus a separate Chains toggle covering props, markers (incl.
    // armies), units, and each path-graph kind separately.
    public bool OverlayProps = true;
    public bool OverlayMarkers = true;
    public bool OverlayUnits = true;
    public bool OverlayReferenceUnits = true;
    public bool OverlayGraphLand = true;
    public bool OverlayGraphAir = true;
    public bool OverlayGraphNaval = true;
    public bool OverlayGraphAmphibious = true;
    public bool OverlayChains = true;
    public bool OverlayAreas = true;
    public bool OverlayDecals = true;
    // Still not wired to any real rendering (task #136's one open gap):
    // showing real water requires SC2WaterMaterialFactory.Create(model, env),
    // which needs a live SC2Environment MonoBehaviour — that component only
    // exists in the separate, disconnected legacy Editor/ScdImportWindow.cs
    // scene-based workflow, not this unified session. Wiring this needs
    // either refactoring the factory to take plain data instead of the
    // MonoBehaviour, or spawning/finding an SC2Environment for the live
    // session — real new plumbing, not a flag-OR like the other 5.
    public bool OverlayWater = true;
    public bool OverlayRNM;
    // Shows the 1-cell heightmap grid across the map —
    // thin lines at every real terrain cell boundary (Data.CellSize),
    // procedural in the terrain shaders (see SetHeightGridOverlay). Off by
    // default, same reasoning as OverlayCosts — a fine grid across a large
    // map is a diagnostic/alignment aid the user opts into, not something
    // that should always be on screen.
    public bool OverlayGrid;
    // Blender-style edit-mode wireframe over the live terrain's real
    // triangulation (SC2TerrainRuntime.SetWireframeOverlay) — off by
    // default, same reasoning as OverlayGrid.
    public bool OverlayWireframe;

    private int _mainTab;
    private int _subTab;
    // The sub-tab each main tab was on when it was left, so coming back to it (Objects > Props, then
    // Terrain > Geometry, then Objects again) lands on Props instead of the first sub-tab.
    private readonly Dictionary<int, int> _lastSubTabByMainTab = new();

    /// <summary>Switches the main tab, remembering the sub-tab of the one being left. Without
    /// <paramref name="subTab"/> the target tab returns to the sub-tab it was last on.</summary>
    private void SelectMainTab(int mainTab, int? subTab = null)
    {
        if (mainTab != _mainTab) _lastSubTabByMainTab[_mainTab] = _subTab;
        _mainTab = mainTab;
        _subTab = subTab ?? (_lastSubTabByMainTab.TryGetValue(mainTab, out var remembered) ? remembered : 0);
    }
    // Tracks HandleObjectsGameViewInput's own objectsTabActive across
    // frames, purely to detect the true->false transition (leaving the
    // Objects tab entirely) so any armed placement/paint/pick tool gets
    // cancelled — otherwise it stays armed (harmless while the tab is
    // blocked, since allowInput is already false there) and silently
    // re-arms itself the moment the user comes back to the same sub-tab.
    private bool _wasObjectsTabActive;
    private string _tool = "Select";

    // ── Measure tool (Edit menu / "M") ──────────────────────────────────────
    // XZ tape-measure: click sets the start point, a live line+distance
    // follows the cursor until a second click locks the end point and adds
    // it to _measurements; the next click starts a fresh measurement. Locked
    // measurements stay visible (not just the most recent one) until deleted
    // — click near an existing one's line to select it, drag either endpoint
    // marker to edit it, Delete/Backspace to remove it. See
    // HandleMeasureToolGameViewInput's own doc comment.
    private bool _measureHasStart;
    /// <summary>A shared, mutable vertex — multiple measurements can
    /// reference the SAME instance (set up by snapping one measurement's
    /// endpoint onto another's, or by merging an existing one into another
    /// via drag), so moving it moves every measurement attached to it at
    /// once, the way a real connected point graph would. A measurement
    /// whose Start/End is its own private SC2MeasurePoint (referenced by no
    /// one else) behaves exactly like a plain Vector3 field would.</summary>
    private sealed class SC2MeasurePoint { public Vector3 Position; }
    private sealed class SC2Measurement { public SC2MeasurePoint Start = new(); public SC2MeasurePoint End = new(); }
    private readonly List<SC2Measurement> _measurements = new();
    /// <summary>The in-progress measurement's start point — a real
    /// SC2MeasurePoint (not a bare Vector3) so that snapping the start click
    /// onto an existing measurement's point can reference that SAME object
    /// directly, connecting the two from the moment this measurement locks.</summary>
    private SC2MeasurePoint _measureStartPoint;
    private int _selectedMeasurement = -1;
    /// <summary>-1 = not dragging, 0 = dragging Start, 1 = dragging End —
    /// which endpoint of _measurements[_selectedMeasurement] is currently
    /// being repositioned.</summary>
    private int _draggingMeasurementEndpoint = -1;

    // ── Map frame overlay (View menu) ───────────────────────────────────────
    // Configurable rectangle, centered on the map's own playable footprint
    // (CostsGridSize/2, CostsGridSize/2 — the same "the map" convention
    // Costs/hfield/decals/water-depth already use), width/height in world
    // (XZ) units. Session-only, like every other View menu toggle here
    // (Inspector Panel/Game Navigation/Hotkeys enabled/Measure) — not saved
    // into SC2TerrainData.
    private bool _showMapFrame;
    private float _mapFrameWidth = 1024f;
    private float _mapFrameHeight = 1024f;

    // ── Reference image overlay (View menu) ─────────────────────────────────
    // Loaded on demand (not a project asset), shown/hidden via
    // SC2Terrain.shader's own _ReferenceOverlayEnabled early-return — see
    // that shader's Properties comment for why it REPLACES the whole
    // terrain render rather than blending, and why world-space (not uv2)
    // is used to sample it. Session-only, not saved into SC2TerrainData.
    private Texture2D _referenceOverlayTex;
    private bool _showReferenceOverlay;
    // The world size (X, Z) the picture covers, around the map centre; (0, 0) = the whole map. A picture drawn for a smaller map than
    // the one being edited (e.g. for a 700 x 700 map, however many pixels it has) is set to that size.
    private Vector2 _referenceCover;

    private const string ReferenceOpacityPrefKey = "SC2.GameView.ReferenceOpacity";
    private float _referenceOpacity = -1f; // -1 = not loaded from PlayerPrefs yet

    /// <summary>How much of the reference image covers the selected view mode on the map, 0..1 (1 = it fully
    /// replaces the view). Remembered between editor sessions, not stored in the map.</summary>
    private float ReferenceOpacity
    {
        get
        {
            if (_referenceOpacity < 0f) _referenceOpacity = Mathf.Clamp01(PlayerPrefs.GetFloat(ReferenceOpacityPrefKey, 1f));
            return _referenceOpacity;
        }
        set
        {
            var clamped = Mathf.Clamp01(value);
            if (_referenceOpacity >= 0f && Mathf.Approximately(clamped, _referenceOpacity)) return;
            _referenceOpacity = clamped;
            PlayerPrefs.SetFloat(ReferenceOpacityPrefKey, _referenceOpacity);
            PlayerPrefs.Save();
            ApplyReferenceOverlayState();
        }
    }

    private Vector2 ReferenceMapWorldSize()
    {
        var data = _terrainShell?.Data;
        return data != null
            ? new Vector2(Mathf.Max(1f, data.GridWidth * data.CellSize), Mathf.Max(1f, data.GridHeight * data.CellSize))
            : new Vector2(1024f, 1024f);
    }

    /// <summary>The picture is scaled about the map centre: these are the world sizes it covers, so a picture drawn for a 700 x 700 map is
    /// fitted by entering 700, and a picture that is not square gets each side set on its own. Equal to the map's size (the default)
    /// it spans the whole map; outside the picture the map shows through.</summary>
    private void DrawReferenceCoverageField(float contentWidth)
    {
        var map = ReferenceMapWorldSize();
        var cover = new Vector2(_referenceCover.x > 0f ? _referenceCover.x : map.x, _referenceCover.y > 0f ? _referenceCover.y : map.y);
        // The label goes on its own wrapped line: this column is narrow, and a long label next to the box would widen the whole panel.
        SC2EnvFieldGui.WrapLabel("Image covers (world units)", contentWidth);
        var width = Mathf.Max(1f, SC2EnvFieldGui.FloatField("Width", cover.x, "ReferenceImageCoverageX"));
        var height = Mathf.Max(1f, SC2EnvFieldGui.FloatField("Height", cover.y, "ReferenceImageCoverageZ"));

        if (Mathf.Approximately(width, cover.x) && Mathf.Approximately(height, cover.y)) return;
        _referenceCover = Mathf.Approximately(width, map.x) && Mathf.Approximately(height, map.y) ? Vector2.zero : new Vector2(width, height);
        ApplyReferenceOverlayState();
    }

    // Percent slider shared by the View menu and the right panel.
    private void DrawReferenceOpacitySlider(string key)
    {
        var percent = SC2EnvFieldGui.Slider("Opacity (%)", ReferenceOpacity * 100f, 0f, 100f, key);
        ReferenceOpacity = Mathf.Clamp(percent, 0f, 100f) / 100f;
    }
    private bool _showFileMenu;
    private bool _showEditMenu;
    private bool _showViewMenu;
    private bool _showVisibilityMenu;
    private bool _showSymmetryMenu;
    private bool _showToolsMenu;
    private bool _showAutosaveMenu;
    private bool _showCustomizationMenu;
    private bool _stylesReady;
    private float _stylesLastScale = -1f;
    private int _stylesLastThemeVersion = -1;
    private bool _menuOverlayBlocksInput;

    /// <summary>True while a window that sits above the panels is open: the panels and the viewport take no input then.</summary>
    private bool AnyModalOpen(bool includeTextureBrowser = true)
    {
        var open = (includeTextureBrowser && SC2TextureBrowser.IsOpen) || (_mapImportPanel?.IsOpen ?? false) || (_infoBdfEditorPanel?.IsOpen ?? false)
            || (_lobbySpawnPanel?.IsOpen ?? false) || SC2HotkeysPanel.IsOpen || SC2ThemePanel.IsOpen || SC2BusyOverlay.IsActive;
#if !UNITY_EDITOR
        // The runtime confirm dialog only exists in a build (the Editor uses its own dialogs).
        open |= SC2RuntimeConfirm.IsOpen;
#endif
        return open;
    }

    private sealed class SC2ImportedTerrainEntry
    {
        // Stable identity for SC2ImportedTerrainEntryDocument round-tripping —
        // not used for anything else.
        public string Id = System.Guid.NewGuid().ToString("N");
        public GameObject Root;
        public string DisplayName;
        // Hidden in the viewport only (session state, like the Objects tab's hidden sets): the renderer is off, but the object stays in
        // the scene, keeps its collider and still ships in the export.
        public bool Hidden;
        public List<SC2TerrainMaterialFactory.TextureLoadAttempt> TextureAttempts;
        public BdfIO.Formats.Terrain.TerrainModel Model; // raw parsed model, for merging into "Export terrain.bdf" — see SC2TerrainExporter.MergeImportedTerrain
        // The exact bytes Model was parsed from (SC2TerrainBdfImporter.
        // ImportResult.RawBytes) — persisted into SC2MapDocument.Terrain.
        // ImportedEntries[].OriginalBdf so this entry can survive Save/Load
        // (previously this entry's whole existence was scene/session-only —
        // see SyncImportedTerrainsToDocument/RestoreImportedTerrainsFromDocument).
        public byte[] OriginalBdfBytes;

        // Captured right after import (the real, shaded SC2/Terrain
        // materials TerrainMeshBuilder built) so the preview-mode toggle
        // (Textured/RNM/Mats/Pairs/Slope/Height) can swap the renderer's
        // materials for a debug view and later restore the originals
        // exactly.
        public Material[] OriginalMaterials;
        // Real, current source of truth for each material's "RNM Lighting"
        // authored/edited state — captured from the live
        // materials' _EnableRNM right after import (before the Textured/RNM
        // preview toolbar ever touches it), and kept in sync with the user's
        // own Shader Setup checkbox edits (never with the toolbar's forced
        // override). Threaded through GetImportedTerrainsForExport as
        // SC2ImportedTerrainExportData.AuthoredEnableRnm so export reads
        // this instead of the toolbar-contaminated live property — see that
        // field's own doc comment for the bug this fixes.
        public float[] OriginalEnableRnm;
        // The actual relative path written
        // into terrain.bdf for each imported material's texture slot should
        // be visible AND directly editable, not just derived silently from
        // whichever Texture happens to be bound to the shader property. Live
        // Material objects have no room for a new string field, so this is a
        // side-table keyed by "materialIndex:shaderProp" — empty/absent means
        // no override, fall back to the existing Texture-based resolution
        // (SC2TerrainMaterialExporter.TextureToBdfPath) exactly as before.
        public Dictionary<string, string> TexturePathOverrides = new();
        // Per-slot "copy into map on export" opt-in (same key convention as
        // TexturePathOverrides) — see SC2VanillaArchiveClassifier.
        public Dictionary<string, bool> TexturePathForceExport = new();
        /// <summary>Per-layer (index = layer/material index of Model) name of
        /// the imported RNM mask this layer's lighting is baked onto — see
        /// SC2ImportedTerrainExportData.RnmBakeMaskNames. "" = not baked.</summary>
        public string[] RnmBakeMaskNames = System.Array.Empty<string>();
        public Material[] FlatColorMaterials;   // lazily built, cached for Mats view — flat HSV-by-index color
        public Material[] PairColorMaterials;   // lazily built, cached for Pairs view — blended avg(Diffuse1,Diffuse2) color
        public Material VertexColorMaterial;    // shared SC2/VertexColorUnlit material for Slope/Height
        public Material[] VertexColorSharedMaterials; // VertexColorMaterial repeated once per submesh
        public Color[] SlopeColors;             // lazily baked, cached per-vertex
        public Color[] HeightColors;            // lazily baked, cached per-vertex

        // Left-panel UI state for the Imported subtab — per-entry so
        // switching between multiple imported terrains keeps each one's
        // fold/scroll state independent.
        public bool TexturesExpanded;
        public bool MaterialsExpanded = true;
        public Vector2 TextureListScroll;
    }
    private readonly List<SC2ImportedTerrainEntry> _importedTerrains = new List<SC2ImportedTerrainEntry>();
    private int _selectedImportedTerrain = -1;

    // ── Imported terrain.bdf G/R/S gestures ──────────────────────────────────
    // Requested directly: Blender-style Move/Rotate/Scale gestures for the
    // Imported subtab's currently-selected object, with X/Y/Z constraining the
    // gesture to one world axis. Mirrors SC2ObjectsShell's Props Rotate/Scale
    // gesture shape (press hotkey, move mouse, click/Enter confirms, Esc
    // cancels) — see HandleImportedTerrainGameViewInput's own comment for why
    // Move here can't just be "click-drag the object" like Props: an imported
    // object has no viewport pick-dot of its own to grab, and a full 3-axis
    // Position/Rotation/Scale needs an X/Y/Z constraint Props never needed.
    private enum ImportedGestureKind { None, Move, Rotate, Scale }
    private enum ImportedGestureAxis { Free, X, Y, Z }
    private ImportedGestureKind _importedGestureKind = ImportedGestureKind.None;
    private ImportedGestureAxis _importedGestureAxis = ImportedGestureAxis.Free;
    private SC2ImportedTerrainEntry _importedGestureEntry;
    private Vector3 _importedGestureStartPosition;
    private Vector3 _importedGestureStartEuler;
    private Vector3 _importedGestureStartScale;
    private Vector3 _importedGestureStartGroundWorld;
    private Vector2 _importedGestureAnchorScreen;
    private Vector2 _importedGestureStartMouse;
    private float _importedGestureStartDistance;
    private Bounds? _importedGestureBoundsBefore;
    // Arbitrary, tunable sensitivity constants for the mouse-delta-driven
    // gestures that have no natural "distance from a pivot" reading — same
    // role as SC2DecalsShell's own hardcoded "* .5f" degrees-per-pixel
    // fallback for its Rotate gesture's degenerate (near-pivot) case.
    private const float ImportedMoveYWorldPerPixel = 0.1f;
    private const float ImportedRotateAxisDegreesPerPixel = 0.5f;
    /// <summary>Imported decals.bdf mesh roots (OpenDecalsBdfImport /
    /// OnMapImportComplete) — unlike imported terrain, decals have no real
    /// per-object management panel, so this list exists ONLY to let
    /// HandleViewportClick/PickRendererByBounds exclude them from generic
    /// Selection-based picking (same "camera flies off" bug imported
    /// terrain already had fixed — see IsGenericSelectionExcluded's own
    /// comment).</summary>
    private readonly List<GameObject> _importedDecalsRoots = new List<GameObject>();

    // ── Imported terrain.bdf undo ────────────────────────────────────────────
    // Whole-list snapshot, same philosophy as SC2CostsShell's own undo stack —
    // this data (Transform, per-material texture slots/shader flags, list
    // membership) lives entirely outside SC2TerrainData, so it can't share
    // SC2TerrainShell's undo at all. "Remove From Scene" no longer actually
    // destroys the GameObject: it deactivates it and keeps the entry (with all
    // its cached materials/texture-attempt data) alive in _importedTerrainsRemoved
    // so Undo can bring it back exactly, not just re-import it. The tradeoff
    // (deliberate, not an oversight): removed objects linger deactivated in the
    // scene for the rest of the session instead of being freed immediately —
    // bounded by how many times this button gets clicked in one sitting, and
    // cleaned up whenever the whole terrain resets (NewTerrain/LoadFromDisk).

    private const int ImportedUndoMaxSteps = SC2.MapEditor.Document.SC2ShellUndoCoordinator.MaxSteps;

    private static readonly string[] ImportedMaterialTextureProps =
        { "_Diffuse1", "_Diffuse2", "_Normal1", "_Normal2", "_Mask1", "_Mask2", "_Ramp", "_RNMMask0", "_RNMMask1", "_RNMMask2" };
    // Was only 4 of the 12 single-float shader properties the "Shader
    // Setup" panel (below, ~line 5670) actually exposes toggles/sliders
    // for — DiffuseLighting/SpecularLighting/Glow/EnvironmentMapping/
    // DynamicShadows/InfiniteDistance/AlphaTest(+its Threshold) were toggled
    // live on the material with no entry here to ever capture them, so they
    // silently reverted to the freshly-imported BDF's own defaults on every
    // Undo AND on every Save/Load round trip even though the UI showed them
    // changing. CaptureImportedEntrySnapshot/SyncImportedTerrainsToDocument/
    // ApplyImportedTerrainsSnapshot/RestoreImportedTerrainsFromDocument all
    // drive off this one array, so listing every property here is the only
    // change those 4 call sites needed.
    private static readonly string[] ImportedMaterialFlagProps =
    {
        "_EnableRNM", "_EnableAmbient", "_EnableDiffuseLighting", "_EnableSpecularLighting",
        "_EnableBlending", "_EnableFog", "_EnableGlow", "_EnableEnvironmentMapping",
        "_EnableDynamicShadows", "_EnableInfiniteDistance", "_EnableAlphaTest", "_AlphaTestThreshold",
    };
    // Vector-valued shader properties the Shader Setup panel also exposes
    // (Texture Scroll) — same "never captured anywhere" gap as the flags
    // above, just needing GetVector/SetVector instead of GetFloat/SetFloat.
    private static readonly string[] ImportedMaterialVectorProps = { "_TextureScroll" };

    private sealed class ImportedMaterialSnapshot
    {
        public Dictionary<string, Texture> Textures = new();
        public Dictionary<string, float> Flags = new();
        public Dictionary<string, Vector4> Vectors = new();
        // The "State" popup (TerrainAlphaBlendState/TerrainAdditiveState/
        // TerrainOpaqueState/None) — see SC2TerrainMaterialFactory.
        // ApplyBlendState/ReverseLookupBlendState. Null/empty means "None".
        public string BlendState;
    }

    private sealed class ImportedEntrySnapshot
    {
        public GameObject Root;
        public bool Active;
        public Vector3 Position, EulerAngles, Scale;
        public List<ImportedMaterialSnapshot> Materials = new();
        // The export path overrides (and their "copy into map" marks) live on the entry, not on the materials.
        public Dictionary<string, string> PathOverrides;
        public Dictionary<string, bool> ForceExport;
    }

    private sealed class ImportedTerrainsSnapshot
    {
        public List<ImportedEntrySnapshot> Entries = new();
        public int SelectedIndex;
        // Stamped at push time (fresh each time a snapshot crosses from one
        // stack to the other) — see ImportedTerrainsLastActionTimestamp's
        // own doc comment for why a per-snapshot timestamp, not one shared
        // scalar, is required for correct cross-domain dispatch ordering.
        public long Timestamp;
    }

    private readonly List<SC2ImportedTerrainEntry> _importedTerrainsRemoved = new();
    private readonly Stack<ImportedTerrainsSnapshot> _importedUndoStack = new();
    private readonly Stack<ImportedTerrainsSnapshot> _importedRedoStack = new();
    public int ImportedTerrainsUndoCount => _importedUndoStack.Count;
    public int ImportedTerrainsRedoCount => _importedRedoStack.Count;
    // RecordImportedTerrainsUndo used to never create a
    // timestamp candidate for the global Ctrl+Z dispatcher, so an imported-
    // terrain transform/material edit was invisible to it - the global
    // shortcut could undo a different subsystem's older action instead, or
    // report "Nothing to undo" while a real imported-terrain edit sat
    // unreachable except via this panel's own local Undo/Redo buttons.
    // Fixed (mirroring SC2ShellUndoCoordinator's own fix,
    // since this stack predates that shared class): derived from the
    // stack's own top-of-stack Timestamp (stamped by CaptureImportedTerrains
    // Snapshot at push time) rather than one shared scalar bumped only on
    // commit — self-corrects after Undo/Redo instead of staying stale and
    // incorrectly out-ranking a genuinely more recent action from another
    // domain. LastRedoTimestamp is separate from LastActionTimestamp because
    // Redo must replay in exact reverse of whatever was just undone, not in
    // original-recording order — see SC2ShellUndoCoordinator.
    // LastRedoTimestamp's own doc comment for the full reasoning.
    public bool ImportedTerrainsHasUndo => _importedUndoStack.Count > 0;
    public bool ImportedTerrainsHasRedo => _importedRedoStack.Count > 0;
    public long ImportedTerrainsLastActionTimestamp => _importedUndoStack.Count > 0 ? _importedUndoStack.Peek().Timestamp : 0;
    public long ImportedTerrainsLastRedoTimestamp => _importedRedoStack.Count > 0 ? _importedRedoStack.Peek().Timestamp : 0;

    private void RecordImportedTerrainsUndo()
    {
        _importedUndoStack.Push(CaptureImportedTerrainsSnapshot());
        if (_importedUndoStack.Count > ImportedUndoMaxSteps)
        {
            var tmp = _importedUndoStack.ToArray();
            _importedUndoStack.Clear();
            foreach (var s in tmp.Take(ImportedUndoMaxSteps).Reverse()) _importedUndoStack.Push(s);
        }
        _importedRedoStack.Clear();
    }

    private ImportedTerrainsSnapshot CaptureImportedTerrainsSnapshot()
    {
        var snap = new ImportedTerrainsSnapshot { SelectedIndex = _selectedImportedTerrain, Timestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
        foreach (var entry in _importedTerrains)
            snap.Entries.Add(CaptureImportedEntrySnapshot(entry));
        foreach (var entry in _importedTerrainsRemoved)
            snap.Entries.Add(CaptureImportedEntrySnapshot(entry));
        return snap;
    }

    private static ImportedEntrySnapshot CaptureImportedEntrySnapshot(SC2ImportedTerrainEntry entry)
    {
        var t = entry.Root != null ? entry.Root.transform : null;
        var snap = new ImportedEntrySnapshot
        {
            Root = entry.Root,
            Active = entry.Root != null && entry.Root.activeSelf,
            Position = t != null ? t.position : Vector3.zero,
            EulerAngles = t != null ? t.eulerAngles : Vector3.zero,
            Scale = t != null ? t.localScale : Vector3.one,
            PathOverrides = new Dictionary<string, string>(entry.TexturePathOverrides),
            ForceExport = new Dictionary<string, bool>(entry.TexturePathForceExport),
        };
        if (entry.OriginalMaterials != null)
            foreach (var mat in entry.OriginalMaterials)
            {
                var matSnap = new ImportedMaterialSnapshot();
                if (mat != null)
                {
                    foreach (var prop in ImportedMaterialTextureProps)
                        if (mat.HasProperty(prop))
                            matSnap.Textures[prop] = mat.GetTexture(prop);
                    foreach (var prop in ImportedMaterialFlagProps)
                        if (mat.HasProperty(prop))
                            matSnap.Flags[prop] = mat.GetFloat(prop);
                    foreach (var prop in ImportedMaterialVectorProps)
                        if (mat.HasProperty(prop))
                            matSnap.Vectors[prop] = mat.GetVector(prop);
                    matSnap.BlendState = SC2TerrainMaterialFactory.ReverseLookupBlendState(mat);
                }
                snap.Materials.Add(matSnap);
            }
        return snap;
    }

    /// <summary>Converts the live _importedTerrains list into
    /// SC2MapDocument.Terrain.ImportedEntries so it survives Save/Load —
    /// this list previously lived only in this MonoBehaviour's
    /// own field and live scene GameObjects, with no document
    /// representation at all. Called right before Save (SaveActiveTerrain),
    /// a push model matching how SC2EnvironmentShell/SC2DecalsShell already
    /// keep their own document sub-trees live-current rather than requiring
    /// BuildDocumentSnapshot to reach back into this class. Only entries
    /// still in _importedTerrains are persisted — _importedTerrainsRemoved
    /// exists purely to let THIS session's local Undo bring a removed entry
    /// back exactly; losing that specific recovery across a save/load cycle
    /// is an acceptable, minor scope cut (a removed object is gone from the
    /// map either way from the next session's perspective).</summary>
    private void SyncImportedTerrainsToDocument()
    {
        if (_mapSession?.Document?.Terrain == null || _mapSession.BlobStore == null) return;

        var list = new List<SC2ImportedTerrainEntryDocument>(_importedTerrains.Count);
        foreach (var entry in _importedTerrains)
        {
            if (entry?.Root == null || entry.OriginalBdfBytes == null || entry.OriginalBdfBytes.Length == 0) continue;

            var t = entry.Root.transform;
            var doc = new SC2ImportedTerrainEntryDocument
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName ?? entry.Root.name,
                OriginalBdf = _mapSession.BlobStore.Put(new System.IO.MemoryStream(entry.OriginalBdfBytes)),
                Position = new[] { t.position.x, t.position.y, t.position.z },
                EulerAngles = new[] { t.eulerAngles.x, t.eulerAngles.y, t.eulerAngles.z },
                Scale = new[] { t.localScale.x, t.localScale.y, t.localScale.z },
                PivotAtOrigin = true,
            };

            if (entry.OriginalMaterials != null)
                foreach (var mat in entry.OriginalMaterials)
                {
                    var ov = new SC2ImportedTerrainMaterialOverrideDocument();
                    if (mat != null)
                    {
                        foreach (var prop in ImportedMaterialTextureProps)
                            if (mat.HasProperty(prop))
                            {
                                var tex = mat.GetTexture(prop);
                                var path = tex != null ? SC2TerrainMaterialFactory.TryGetResourcePath(tex) : null;
                                if (!string.IsNullOrEmpty(path))
                                    ov.TextureResourcePaths[prop] = path;
                            }
                        foreach (var prop in ImportedMaterialFlagProps)
                            if (mat.HasProperty(prop))
                                ov.Flags[prop] = mat.GetFloat(prop);
                        foreach (var prop in ImportedMaterialVectorProps)
                            if (mat.HasProperty(prop))
                            {
                                var v = mat.GetVector(prop);
                                ov.Vectors[prop] = new[] { v.x, v.y, v.z, v.w };
                            }
                        ov.BlendState = SC2TerrainMaterialFactory.ReverseLookupBlendState(mat) ?? "";
                    }
                    // TexturePathForceExport lives on the entry keyed
                    // "materialIndex:shaderProp" (same as TexturePathOverrides),
                    // not on the Material — this override list is index-aligned
                    // with OriginalMaterials, so the next index to be added is
                    // this material's own.
                    var materialIndex = doc.MaterialOverrides.Count;
                    // "RNM Lighting" is saved from the AUTHORED value, not the
                    // live material's _EnableRNM: the Textured/RNM preview
                    // toolbar forces that property (Textured = off), so
                    // reading it here saved whatever the toolbar happened to
                    // show and the checkbox came back unchecked after every
                    // Save/Load. The restore reads the authored array back
                    // from this same value (OriginalEnableRnm is rebuilt from
                    // the restored material's _EnableRNM).
                    if (entry.OriginalEnableRnm != null && materialIndex < entry.OriginalEnableRnm.Length)
                        ov.Flags["_EnableRNM"] = entry.OriginalEnableRnm[materialIndex];
                    foreach (var prop in ImportedMaterialTextureProps)
                        if (entry.TexturePathForceExport.TryGetValue(materialIndex + ":" + prop, out var force) && force)
                            ov.TexturePathForceExport[prop] = true;
                    // The Environment texture has no material property: its path is saved as it is, whatever it points at.
                    var environmentKey = materialIndex + ":" + SC2TerrainMaterialExporter.EnvironmentKey;
                    if (entry.TexturePathOverrides.TryGetValue(environmentKey, out var environmentPath) && !string.IsNullOrEmpty(environmentPath))
                        ov.TextureResourcePaths[SC2TerrainMaterialExporter.EnvironmentKey] = environmentPath;
                    if (entry.TexturePathForceExport.TryGetValue(environmentKey, out var environmentForce) && environmentForce)
                        ov.TexturePathForceExport[SC2TerrainMaterialExporter.EnvironmentKey] = true;
                    if (materialIndex < entry.RnmBakeMaskNames.Length)
                        ov.RnmBakeMaskName = entry.RnmBakeMaskNames[materialIndex] ?? "";
                    doc.MaterialOverrides.Add(ov);
                }

            list.Add(doc);
        }

        _mapSession.Document.Terrain.ImportedEntries = list;
    }

    /// <summary>Reverse of SyncImportedTerrainsToDocument, called after a
    /// successful Load (SC2TerrainShell.LoadFromDisk) — destroys whatever
    /// imported-terrain GameObjects currently exist in the scene (a
    /// previously-loaded/from-scratch map's leftovers, since loading a
    /// terrain never cleared this list before this fix) and rebuilds
    /// _importedTerrains from the freshly-loaded document: re-parses each
    /// entry's original bytes (SC2TerrainBdfImporter.ImportFromBytes) to
    /// reconstruct the as-imported baseline mesh/materials, then re-applies
    /// its persisted transform/material overrides on top.</summary>
    private void RestoreImportedTerrainsFromDocument(List<SC2ImportedTerrainEntryDocument> entries)
    {
        foreach (var entry in _importedTerrains)
            if (entry?.Root != null) UnityEngine.Object.DestroyImmediate(entry.Root);
        foreach (var entry in _importedTerrainsRemoved)
            if (entry?.Root != null) UnityEngine.Object.DestroyImmediate(entry.Root);
        _importedTerrains.Clear();
        _importedTerrainsRemoved.Clear();
        _selectedImportedTerrain = -1;
        _importedUndoStack.Clear();
        _importedRedoStack.Clear();
        _importedRnmOriginals.Clear(); // keyed by the materials just destroyed

        if (entries == null || entries.Count == 0 || _mapSession?.BlobStore == null)
            return;

        foreach (var doc in entries)
        {
            if (doc.OriginalBdf.IsEmpty) continue;
            byte[] bytes;
            try
            {
                using var stream = _mapSession.BlobStore.OpenRead(doc.OriginalBdf.ContentHash);
                using var memory = new System.IO.MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SC2GameViewEditorUI] Failed to restore imported terrain '{doc.DisplayName}': {ex.Message}");
                continue;
            }

            SC2TerrainBdfImporter.ImportResult result;
            try
            {
                result = SC2TerrainBdfImporter.ImportFromBytes(bytes, doc.DisplayName);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SC2GameViewEditorUI] Failed to re-parse imported terrain '{doc.DisplayName}': {ex.Message}");
                continue;
            }

            var t = result.Root.transform;
            var savedPosition = new Vector3(doc.Position[0], doc.Position[1], doc.Position[2]);
            var savedEuler = new Vector3(doc.EulerAngles[0], doc.EulerAngles[1], doc.EulerAngles[2]);
            var savedScale = new Vector3(doc.Scale[0], doc.Scale[1], doc.Scale[2]);
            // A map saved before objects turned about their own origin placed the object relative to (0, 0, 0): the same placement
            // relative to the object's origin is another position.
            if (!doc.PivotAtOrigin && result.Root.GetComponent<SC2ImportedTerrainPivot>() is { } restoredPivot)
                savedPosition = SC2ImportedPivot.ConvertLegacyPosition(savedPosition, Quaternion.Euler(savedEuler), savedScale, restoredPivot.Value);
            t.position = savedPosition;
            t.eulerAngles = savedEuler;
            t.localScale = savedScale;

            var meshRenderer = result.Root.GetComponent<MeshRenderer>();
            var originalMaterials = meshRenderer != null ? meshRenderer.sharedMaterials : System.Array.Empty<Material>();
            // Restores DrawMaterialTextureSlot's own per-slot path-override
            // dictionary (SC2ImportedTerrainEntry.TexturePathOverrides), not
            // just the live material's bound texture — SC2TerrainMaterial
            // Exporter.ApplyFromScene reads ONLY this dictionary (never the
            // live texture's own resolved path) to decide what to actually
            // write into an exported terrain.bdf's TexturePaths for a
            // Custom-/archive-sourced pick, since TryUpdateSlot's own
            // fallback (UnityEditor.AssetDatabase.GetAssetPath) can only
            // ever resolve a REAL project asset, never one of these
            // in-memory-decoded textures. Leaving this dictionary empty
            // after a reload — as a prior version of this restore did —
            // meant a picked custom/archive texture kept rendering
            // correctly in the scene (the live material texture WAS
            // restored, just below) while silently exporting nothing (or
            // reverting to whatever the ORIGINAL, pre-pick .bdf path was)
            // for that exact slot, and the picker's own path field showed
            // blank instead of the path it had actually resolved to.
            var restoredPathOverrides = new Dictionary<string, string>();
            var restoredForceExport = new Dictionary<string, bool>();
            for (var i = 0; i < originalMaterials.Length && i < doc.MaterialOverrides.Count; i++)
            {
                var mat = originalMaterials[i];
                if (mat == null) continue;
                var ov = doc.MaterialOverrides[i];
                foreach (var kv in ov.TexturePathForceExport)
                    if (kv.Value) restoredForceExport[i + ":" + kv.Key] = true;
                foreach (var kv in ov.TextureResourcePaths)
                {
                    if (kv.Key == SC2TerrainMaterialExporter.EnvironmentKey)
                    {
                        restoredPathOverrides[i + ":" + kv.Key] = kv.Value;
                        continue;
                    }
                    // Only a "file:"/"archive:" tag needs to survive as an
                    // explicit override — a plain Resources-backed path
                    // resolves fine on its own via TryUpdateSlot's
                    // AssetDatabase fallback, exactly matching
                    // DrawMaterialTextureSlot's own "isRealAsset" condition
                    // for when a live pick sets vs. clears this entry.
                    if (kv.Value.StartsWith("file:", System.StringComparison.Ordinal) ||
                        kv.Value.StartsWith("archive:", System.StringComparison.Ordinal))
                        restoredPathOverrides[i + ":" + kv.Key] = kv.Value;
                    if (!mat.HasProperty(kv.Key)) continue;
                    // ResolveFromResourcePath, not LoadTexturePublicAny — kv.Value
                    // is a saved RESOURCE path (SC2TerrainMaterialFactory.
                    // TryGetResourcePath's own output, e.g. "archive:textures/...")
                    // captured by SyncImportedTerrainsToDocument, not a plain
                    // BDF-relative path. Passing it to LoadTexturePublicAny
                    // silently failed for every archive-sourced texture (see
                    // ResolveFromResourcePath's own doc comment).
                    //
                    // isLinear/disableMipmaps MUST match SC2TerrainMaterialFactory.
                    // Create's own per-property convention (_Normal*/_RNMMask*
                    // are raw vector/blend data, gamma-decoding them corrupts
                    // every value) — omitting this the first
                    // time reintroduced the old terrain-banding bug, since this
                    // restore now actually succeeds (before this fix it silently
                    // failed and left the correctly-tuned freshly-parsed texture
                    // in place, masking the missing tuning here).
                    var isLinearSlot = kv.Key.StartsWith("_Normal", System.StringComparison.Ordinal) ||
                                        kv.Key.StartsWith("_RNMMask", System.StringComparison.Ordinal);
                    var disableMipsSlot = kv.Key.StartsWith("_RNMMask", System.StringComparison.Ordinal);
                    var tex = SC2TerrainMaterialFactory.ResolveFromResourcePath(kv.Value, isLinearSlot, disableMipsSlot);
                    if (tex != null) mat.SetTexture(kv.Key, tex);
                }
                foreach (var kv in ov.Flags)
                    if (mat.HasProperty(kv.Key)) mat.SetFloat(kv.Key, kv.Value);
                foreach (var kv in ov.Vectors)
                    if (mat.HasProperty(kv.Key) && kv.Value != null && kv.Value.Length >= 4)
                        mat.SetVector(kv.Key, new Vector4(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
                SC2TerrainMaterialFactory.ApplyBlendState(mat, string.IsNullOrEmpty(ov.BlendState) ? null : ov.BlendState);
            }

            var entry = new SC2ImportedTerrainEntry
            {
                Id = string.IsNullOrEmpty(doc.Id) ? System.Guid.NewGuid().ToString("N") : doc.Id,
                Root = result.Root,
                DisplayName = doc.DisplayName,
                TextureAttempts = result.TextureAttempts,
                Model = result.Model,
                OriginalBdfBytes = bytes,
                OriginalMaterials = originalMaterials,
                OriginalEnableRnm = originalMaterials
                    .Select(m => m != null && m.HasProperty("_EnableRNM") ? m.GetFloat("_EnableRNM") : 0f)
                    .ToArray(),
                TexturePathOverrides = restoredPathOverrides,
                TexturePathForceExport = restoredForceExport,
                RnmBakeMaskNames = doc.MaterialOverrides.Select(o => o.RnmBakeMaskName ?? "").ToArray(),
            };
            _importedTerrains.Add(entry);
        }

        if (_importedTerrains.Count > 0)
            _selectedImportedTerrain = 0;
        // The freshly re-parsed materials carry their ORIGINAL RNM masks —
        // put the baked imported masks back on the layers that target one.
        RefreshImportedRnmMasks();
        _terrainShell?.InvalidateLiveHeightGrid();
    }

    /// <summary>World bounds of an imported terrain object (all its renderers), or null without any.</summary>
    private static Bounds? ImportedEntryBounds(SC2ImportedTerrainEntry entry)
    {
        if (entry?.Root == null) return null;
        Bounds? result = null;
        foreach (var renderer in entry.Root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null) continue;
            if (result.HasValue) { var b = result.Value; b.Encapsulate(renderer.bounds); result = b; }
            else result = renderer.bounds;
        }
        return result;
    }

    /// <summary>Tells Objects and Decals that the ground under an imported terrain object changed
    /// (moved / rotated / scaled / Undo), so anchored objects and aligned decals re-sample it.</summary>
    private void NotifyImportedTerrainChanged(Bounds? before, Bounds? after)
    {
        if (_terrainShell == null || (!before.HasValue && !after.HasValue)) return;
        _terrainShell.NotifyImportedTerrainMoved(before ?? after.Value, after ?? before.Value);
    }

    private void ApplyImportedTerrainsSnapshot(ImportedTerrainsSnapshot snap)
    {
        SC2ImportedTerrainEntry FindEntry(GameObject root) =>
            _importedTerrains.FirstOrDefault(e => e.Root == root)
            ?? _importedTerrainsRemoved.FirstOrDefault(e => e.Root == root);

        var rebuilt = new List<SC2ImportedTerrainEntry>();
        var removed = new List<SC2ImportedTerrainEntry>();
        var movedRegions = new List<(Bounds? Before, Bounds? After)>();
        foreach (var s in snap.Entries)
        {
            if (s.Root == null) continue;
            var entry = FindEntry(s.Root);
            if (entry == null) continue; // shouldn't happen — Root identity is always tracked in one of the two lists

            var boundsBefore = ImportedEntryBounds(entry);
            var rootTransform = entry.Root.transform;
            var transformChanged = rootTransform.position != s.Position || rootTransform.eulerAngles != s.EulerAngles
                || rootTransform.localScale != s.Scale || entry.Root.activeSelf != s.Active;
            rootTransform.position = s.Position;
            rootTransform.eulerAngles = s.EulerAngles;
            rootTransform.localScale = s.Scale;
            if (s.PathOverrides != null) entry.TexturePathOverrides = new Dictionary<string, string>(s.PathOverrides);
            if (s.ForceExport != null) entry.TexturePathForceExport = new Dictionary<string, bool>(s.ForceExport);
            if (transformChanged) movedRegions.Add((boundsBefore, ImportedEntryBounds(entry)));
            if (entry.OriginalMaterials != null)
                for (var i = 0; i < entry.OriginalMaterials.Length && i < s.Materials.Count; i++)
                {
                    var mat = entry.OriginalMaterials[i];
                    if (mat == null) continue;
                    foreach (var kv in s.Materials[i].Textures) mat.SetTexture(kv.Key, kv.Value);
                    foreach (var kv in s.Materials[i].Flags) mat.SetFloat(kv.Key, kv.Value);
                    foreach (var kv in s.Materials[i].Vectors) mat.SetVector(kv.Key, kv.Value);
                    SC2TerrainMaterialFactory.ApplyBlendState(mat, s.Materials[i].BlendState);
                }

            entry.Root.SetActive(s.Active);
            (s.Active ? rebuilt : removed).Add(entry);
        }

        _importedTerrains.Clear();
        _importedTerrains.AddRange(rebuilt);
        _importedTerrainsRemoved.Clear();
        _importedTerrainsRemoved.AddRange(removed);
        _selectedImportedTerrain = _importedTerrains.Count == 0 ? -1 : Mathf.Clamp(snap.SelectedIndex, 0, _importedTerrains.Count - 1);
        // Undo/redo can change which imported
        // terrain objects exist/are active and their transforms - the live
        // height grid needs to reflect that on the next paint.
        _terrainShell?.InvalidateLiveHeightGrid();
        foreach (var (before, after) in movedRegions) NotifyImportedTerrainChanged(before, after);
    }

    private void ImportedTerrainsUndo()
    {
        if (_importedUndoStack.Count == 0) return;
        _importedRedoStack.Push(CaptureImportedTerrainsSnapshot());
        ApplyImportedTerrainsSnapshot(_importedUndoStack.Pop());
    }

    private void ImportedTerrainsRedo()
    {
        if (_importedRedoStack.Count == 0) return;
        _importedUndoStack.Push(CaptureImportedTerrainsSnapshot());
        ApplyImportedTerrainsSnapshot(_importedRedoStack.Pop());
    }

    /// <summary>Reported directly: typing into an Imported object's Position/
    /// Rotation/Scale fields hung the editor on every single character. Root
    /// cause: Vector3Field's X/Y/Z sub-fields (both the real
    /// UnityEditor.EditorGUILayout one and this project's own standalone
    /// shim) report a changed value on every keystroke, and the call site
    /// below treats ANY change as a completed edit — running
    /// NotifyImportedTerrainChanged, which calls SC2DecalsShell.
    /// ReprojectDecalsInBounds, which forces a synchronous ~100-250ms
    /// live-height-grid rebuild (SC2LiveHeightGrid.EnsureFreshImmediate) —
    /// once per keystroke. DrawDelayedVector3Field's per-axis
    /// DelayedFloatField only reports a change once, on the frame an edit
    /// actually commits (Enter/Tab/focus-lost), so RecordImportedTerrainsUndo
    /// and the expensive call site above now also run once per edit instead
    /// of once per character — no extra dedup needed here, unlike the old
    /// live-scrubbing Vector3Field this replaces.</summary>
    private Vector3 DrawUndoableVector3Field(GameObject root, string label, Vector3 value, float width)
    {
        var next = DrawDelayedVector3Field(label, value, width);
        if (next != value)
            RecordImportedTerrainsUndo();
        return next;
    }

    /// <summary>Vector3Field composed from three DelayedFloatField axes
    /// instead of EditorGUILayout.Vector3Field's own live-updating X/Y/Z —
    /// see DrawUndoableVector3Field's own comment for why. Neither real
    /// Unity nor this project's standalone shim ships a delayed Vector3
    /// field, so this builds one from the delayed float primitive both
    /// already have.</summary>
    private static Vector3 DrawDelayedVector3Field(string label, Vector3 value, float width)
    {
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal(GUILayout.Width(width), GUILayout.MaxWidth(width));
        GUILayout.Label("X", GUILayout.Width(14f));
        value.x = EditorGUILayout.DelayedFloatField(value.x, GUILayout.ExpandWidth(true));
        GUILayout.Label("Y", GUILayout.Width(14f));
        value.y = EditorGUILayout.DelayedFloatField(value.y, GUILayout.ExpandWidth(true));
        GUILayout.Label("Z", GUILayout.Width(14f));
        value.z = EditorGUILayout.DelayedFloatField(value.z, GUILayout.ExpandWidth(true));
        EditorGUILayout.EndHorizontal();
        return value;
    }
    private SC2TerrainShell _terrainShell;
    /// <summary>Exposed for SC2InfoBdfEditorWindow — a standalone floating
    /// EditorWindow outside this class's own object graph that needs to
    /// find "the currently active map" to save into. Mirrors MapSession's
    /// existing public-getter pattern below.</summary>
    public SC2TerrainShell TerrainShell => _terrainShell;
    private int _terrainShellLastSubtab = -1;
    private GUIStyle _panelStyle;
    // The panel's padding on the left, top and bottom only: the right edge of a panel belongs to its scrollbar, which sits flush
    // against it (see DrawMainPanel). The header variant has no bottom padding, so the scrolled content starts right below it.
    private GUIStyle _panelContentStyle;
    private GUIStyle _panelHeaderStyle;
    private GUIStyle _headerStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _tabStyle;
    private GUIStyle _activeTabStyle;
    private GUIStyle _subtabStyle;
    private GUIStyle _activeSubtabStyle;
    private GUIStyle _menuBarButtonStyle;
    private GUIStyle _menuBarButtonActiveStyle;
    private GUIStyle _dropdownItemStyle;
    private GUIStyle _dropdownToggleStyle;
    private GUIStyle _dropdownSectionLabelStyle;
    private GUIStyle _miniTextStyle;
    private GUIStyle _secondaryTextStyle;
    private GUIStyle _statusPillStyle;
    private GUIStyle _coordsPillStyle;
    private Texture2D _statusDotTexture;
    private GUIStyle _cardStyle;
    private GUIStyle _shadowStyle;
    private GUIStyle _logoDotStyle;
    private GUIStyle _gizmoLabelStyle;
    private Texture2D _headerTexture;
    private Texture2D _panelTexture;
    private Texture2D _buttonTexture;
    private Texture2D _activeButtonTexture;
    private Texture2D _cardTexture;
    private Texture2D _cardShadowTexture;
    private Texture2D _accentTexture;
    private Texture2D _accentSoftTexture;
    private Texture2D _dividerTexture;
    private Texture2D _hoverTexture;
    private Texture2D _gizmoBallSolid;
    private Texture2D _gizmoBallHollow;
    private Texture2D _gizmoBackdrop;

    // ---- Palette: the editor-wide SC2Theme (navy panels, grey-blue controls, green accent) ----
    private static Color ColorBg => SC2Theme.Bg; // base chrome background
    private static Color ColorSurface => SC2Theme.Button; // buttons / tab pills
    private static Color ColorSurfaceHi => SC2Theme.ButtonHi; // hover / elevated
    private static Color ColorAccent => SC2Theme.Accent; // primary accent (blue)
    private static Color ColorAccentSoft => new Color(SC2Theme.Accent.r, SC2Theme.Accent.g, SC2Theme.Accent.b, 0.16f);
    private static Color ColorBorder => SC2Theme.LineStrong;
    private static Color ColorDivider => SC2Theme.Divider;
    private static readonly Color ColorShadow    = new Color(0f, 0f, 0f, 0.28f);
    private static Color ColorTextPrimary => SC2Theme.TextPrimary;
    // Used everywhere for notes/help text/inactive-tab labels/status secondary text.
    private static Color ColorTextSecondary => SC2Theme.TextSecondary;

    // Kept for backwards-compatible call sites; mapped onto the new palette.
    private static Color HeaderColor => SC2Theme.Bg;
    private static Color PanelColor => SC2Theme.Panel;
    private bool _orbiting;
    private bool _panning;
    private bool _flying;
    private Vector2 _lastMouse;
    private string _mouseMapCoordinates = "X -   Y -   Z -";
    private Rect _panelRect;
    private Vector2 _panelScroll;
    // Horizontal-only scroll position for DrawSubtabs' compact-mode strip.
    private Vector2 _subtabsScroll;
    // The vertical scrollbar of a side panel is always shown and sits flush against the panel's outer edge, so its width is
    // taken off the width every shell is given. It is the width SC2UnifiedSkin.StyleScrollbar gives the style
    // (SC2UiScale.Pxf(10f)); a different value would let content run under the bar or leave a dead strip beside it.
    private static float PanelScrollbarWidth => SC2UiScale.Pxf(10f);
    // The strip left between the content and the scrollbar, so controls whose own margins stick out a little past their row
    // still stay clear of the bar.
    private static float PanelContentGap => SC2UiScale.Pxf(6f);

    /// <summary>
    /// Actual width of the embedded inspector panel. EditorGUIUtility.currentViewWidth
    /// is NOT reliable here: this UI is drawn from a Camera's OnGUI (Game View overlay),
    /// not from an EditorWindow.OnGUI, so currentViewWidth reflects whatever GUIView last
    /// set it globally and can change between the Layout and Repaint passes of the very
    /// same frame. That flips conditional layout (e.g. "compact" toggles) mid-frame and
    /// shifts auto-allocated control IDs, which is what breaks keyboard focus on fields
    /// like Radius/Height/Map Name. Consumers should use this instead.
    /// </summary>
    public float PanelWidth => _panelRect.width;
    public float PanelContentWidth => Mathf.Max(1f, _panelRect.width - (_panelStyle != null ? _panelStyle.padding.left : 10f) - PanelContentGap - PanelScrollbarWidth);
    public GUIStyle TitleTextStyle => _titleStyle;
    public GUIStyle SmallTextStyle => _miniTextStyle;
    private Rect _fileMenuRect;
    private Rect _editMenuRect;
    private Rect _viewMenuRect;
    private Rect _visibilityMenuRect;
    private Rect _symmetryMenuRect;
    private Rect _toolsMenuRect;
    private Rect _autosaveMenuRect;
    private Rect _customizationMenuRect;
    private Rect _statusRect;
    private Rect _viewportRect;
    private Rect _navigationGizmoRect;
    private Rect _referencePanelRect;
    // Subtracts PanelScrollbarWidth same as the left panel's own
    // PanelContentWidth does — this column's content (unit list, Show*
    // toggles) routinely grows past the panel's fixed height and triggers
    // GUILayout's own vertical scrollbar, and without this the content's
    // declared width didn't leave room for it: the scrollbar then overlapped
    // the rightmost slice of every row (each unit's own "x" delete button,
    // sitting at the row's far right edge), making it visually present but
    // unclickable — the scrollbar's own input consumed the click instead.
    public float ReferencePanelContentWidth => Mathf.Max(1f, _referencePanelRect.width - (_panelStyle != null ? _panelStyle.padding.left : 10f) - PanelContentGap - PanelScrollbarWidth);
    private Vector3 _mapViewCenter = DefaultMapCenter;
    private float _mapViewSize = 512f;
    private bool _viewTransitionActive;
    private float _viewTransitionStart;
    private const float ViewTransitionDuration = 0.28f;
    private Vector3 _viewTransitionStartPosition;
    private Vector3 _viewTransitionTargetPosition;
    private Quaternion _viewTransitionStartRotation;
    private Quaternion _viewTransitionTargetRotation;

    private static readonly string[] GameMainTabs =
    {
        "Terrain",
        "Costs",
        "Collision2",
        "Objects",
        "Decals",
        "Environment",
        "Scenario",
        "Export"
    };

    private static readonly SC2EditorMainTab[] GameMainTabIds =
    {
        SC2EditorMainTab.Terrain,
        SC2EditorMainTab.Costs,
        SC2EditorMainTab.Collision2,
        SC2EditorMainTab.Objects,
        SC2EditorMainTab.Decals,
        SC2EditorMainTab.Environment,
        SC2EditorMainTab.Scenario,
        SC2EditorMainTab.Export
    };

    // Session object shared by the various document-backed commands/shells
    // (SC2CostsShell, SC2TerrainShell, etc.) alongside the legacy
    // RuntimeTerrainData / _terrainShell / _objectsShell data flow.
    private SC2MapSession _mapSession;
    public SC2MapSession MapSession => _mapSession;

    private void OnEnable()
    {
        _activeInstance = this;
        _mapSession = new SC2MapSession(new SC2MapDocument())
        {
            // Persistent, tied to the same save root SC2RuntimeTerrainPersistence
            // uses for Build & Run JSON saves (Application.persistentDataPath/
            // SC2Terrains) rather than a throwaway temp folder — the user's
            // explicit choice, made knowing ISC2BlobStore has no
            // compaction/GC policy yet (SC2DirectoryBlobStore's own doc
            // comment), so this directory will only ever grow across
            // sessions until that's addressed separately. Shared across all
            // maps (not per-map) since blobs are content-addressed by hash —
            // identical bytes from different maps dedup for free.
            //
            // Inlines SC2RuntimeTerrainPersistence.SaveDirectory's own path
            // expression rather than referencing that class directly: it's
            // compiled #if !UNITY_EDITOR only (Build & Run-only persistence),
            // so this file — which must compile in both contexts — can't
            // call into it here even though OnEnable() itself runs in both.
            BlobStore = SC2SaveStorage.CreateSessionBlobStore(),
        };
        // Keep the runtime build visible in screenshots: Editor Game View and
        // Build & Run can otherwise be mistaken for the same player instance.
        Status = $"Game View UI enabled  [build {BuildTag}]";
        Camera.onPreRender += ApplyTopViewMirrorPreRender;
        Camera.onPostRender += ApplyTopViewMirrorPostRender;
        _pendingInitialTopView = true;
#if UNITY_EDITOR
        EditorApplication.update += EditorTick;
#else
        // Open as a normal desktop window (OS title bar + controls) even if the
        // player was built with a fullscreen default.
        if (Application.isPlaying)
            SC2RuntimeWindow.EnsureWindowedOnStartup();
        // Without this, closing the app (window X / Alt+F4 / any future Exit
        // action, all of which route through Application.Quit under the
        // hood) would have no "save changes?" prompt at all — a real Build
        // could lose unsaved terrain edits with one accidental click.
        // wantsToQuit is the one interception point that covers every quit
        // path, not just an in-app button.
        //
        // This class has known cases of multiple
        // live instances existing at once (see the _activeInstance == this
        // ? return : ... guard in Update/HandleGameViewGUI/OnGUI below) —
        // OnEnable can fire more than once without a matching OnDisable in
        // between. Subscribing HandleWantsToQuit per-instance meant N live
        // instances produced N independent MessageBoxW calls for a single
        // quit request. A process-lifetime-once static guard, with the
        // handler itself reading _activeInstance rather than a captured
        // `this`, means exactly one dialog ever shows regardless of how
        // many instances exist or churn.
        if (!_wantsToQuitSubscribed)
        {
            Application.wantsToQuit += HandleWantsToQuit;
            _wantsToQuitSubscribed = true;
        }
#endif
    }

    private void OnDisable()
    {
        if (_activeInstance == this)
            _activeInstance = null;

        SC2SelectionOutline.Clear();
        Camera.onPreRender -= ApplyTopViewMirrorPreRender;
        Camera.onPostRender -= ApplyTopViewMirrorPostRender;
        GL.invertCulling = false;
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
        // wantsToQuit is intentionally left subscribed for the process's
        // whole lifetime (see the static-guard comment in OnEnable above) —
        // never unsubscribed here.
        _terrainShell?.Dispose();
        _terrainShell = null;
        _costsShell?.Dispose();
        _costsShell = null;
        _collision2Shell?.Dispose();
        _collision2Shell = null;
        _decalsShell?.Dispose();
        _decalsShell = null;
        _environmentShell = null;
        _objectsShell = null;
        _scenarioShell = null;
        _mapImportPanel = null;
        _mapSession = null;
    }

    private void LateUpdate()
    {
        // Must run before anything else below queues a single
        // Graphics.DrawMesh(camera:null) this frame — see
        // SC2MapPreviewCapture's own doc comment for why a pending
        // minimap/preview capture has to render before this method's own
        // prop/marker/pick-dot draw calls, not after.
        SC2MapPreviewCapture.ProcessPendingCapture();
        SC2StampPreview.HideIfStale();

        // THE fix for the editor-UI-baked-into-the-blend-mask bug. IMGUI
        // (OnGUI) captures the "current" render target ONCE at the start of the
        // frame's Repaint and renders the entire UI into it. If
        // RenderTexture.active was left pointing at BlendRT/PairMapRT by any GPU
        // op, the whole UI renders into that texture instead of the screen —
        // baking a screenshot of the interface into the terrain's blend mask.
        // Every reset placed INSIDE OnGUI runs too late: after IMGUI has already
        // captured its target for the frame. LateUpdate runs after all Update
        // logic and BEFORE this frame's camera render + OnGUI, so resetting the
        // active target here guarantees IMGUI captures the screen backbuffer, no
        // matter what left the target dangling on a previous frame.
        RenderTexture.active = null;

        // SC2GameAssetScenePreview batches every placed Unit/Prop's SCM mesh
        // by blueprint via Graphics.DrawMeshInstanced instead of one real
        // MeshRenderer GameObject per instance - an immediate-mode draw call
        // that (unlike a real MeshRenderer) Unity does not repeat on its own,
        // so it needs a real per-frame call site. LateUpdate runs
        // unconditionally in both the Editor and a standalone Build (Update()
        // above is play-mode-gated), matching every other Objects-tab preview
        // sync already anchored here.
        // Always resynced here, every frame, regardless of which tab is
        // active or whether the Visibility menu is even open — see
        // OverlayProps's own doc comment for why this can't just be a
        // GUI-click-time write like CommitOverlayToContext's pattern.
        SC2GameAssetScenePreview.HideProps = !OverlayProps;
        SC2GameAssetScenePreview.HideUnits = !OverlayUnits;
        // Resolved once and passed to both batches below so each can cull
        // individual instances against the real camera frustum before
        // submitting them to Graphics.DrawMeshInstanced — see
        // SC2TerrainShell.IsSphereInFrustum's own doc comment for why this
        // is needed (FPS dropped hard specifically once
        // props' models were on screen, even after the batching/allocation
        // fixes — DrawMeshInstanced only culls a submitted batch as one
        // group, never per instance, and a batch already spans a
        // blueprint's every instance across the whole map). May be null
        // (e.g. before the game camera resolves) — both methods treat that
        // as "skip culling this frame," same as before this was added.
        // Each of these 3 draw calls isolated in its own try/catch —
        // marker 3D gizmo models used to silently stop
        // rendering on a freshly-imported map, with NOTHING logged at all
        // (not even the "[SC2 Marker] GPU instancing is supported" line
        // SC2MarkerScenePreview.DrawAllBatches unconditionally logs once on
        // its very first call — meaning that method plausibly never even
        // ran). LateUpdate had no exception isolation between these 3
        // calls before this fix: an unhandled exception thrown partway
        // through Props/Units' own DrawAllBatches (e.g. a bad batch/
        // material on a newly-imported prop) would silently abort every
        // call AFTER it in this same method for that frame — Markers and
        // the pick-dot billboard renderer both sit after Props/Units here,
        // so either could go permanently dark this way with only Unity's
        // own generic exception log (easy to miss/filter past) as evidence,
        // never anything tagged "[SC2 Marker]" specifically. Each call is
        // now independent regardless of which theory turns out to be
        // right, and a real exception (if that's the actual cause) now
        // logs clearly instead of silently swallowing every later draw
        // call for the rest of that frame.
        var scenePreviewCamera = ResolveCamera();
        try { SC2GameAssetScenePreview.DrawAllBatches(scenePreviewCamera); }
        catch (System.Exception ex) { Debug.LogError("[SC2 LateUpdate] SC2GameAssetScenePreview.DrawAllBatches threw: " + ex); }

        // SC2MarkerScenePreview now batches real placed markers via
        // Graphics.DrawMeshInstanced the same way Props/Units do (see that
        // class's own doc comment) — same "needs a real per-frame call
        // site" reasoning as the Props/Units call directly above. Marker
        // visibility itself is already fully handled inside Sync() (called
        // from SC2ObjectsShell.OnGameViewGUI every frame, same as before) —
        // no separate Hide flag needed here, unlike Props/Units.
        try { SC2MarkerScenePreview.DrawAllBatches(scenePreviewCamera); }
        catch (System.Exception ex) { Debug.LogError("[SC2 LateUpdate] SC2MarkerScenePreview.DrawAllBatches threw: " + ex); }

        // The running preview of Effect markers (the game's particle emitters, approximated) - advances and draws every frame.
        try { SC2EffectPreview.DrawAll(scenePreviewCamera); }
        catch (System.Exception ex) { Debug.LogError("[SC2 LateUpdate] SC2EffectPreview.DrawAll threw: " + ex); }

        // GPU-instanced replacement for the Objects tab's IMGUI viewport
        // pick-dots (SC2ObjectMarkerRenderer, user-specced) — same "needs a
        // real per-frame call site" reasoning as the two calls above. Its
        // own Sync() is called from SC2ObjectsShell's dirty-gated block,
        // not here; this call only issues this frame's actual draw calls.
        try { SC2ObjectMarkerRenderer.DrawAllBatches(scenePreviewCamera); }
        catch (System.Exception ex) { Debug.LogError("[SC2 LateUpdate] SC2ObjectMarkerRenderer.DrawAllBatches threw: " + ex); }
    }

    // Ad-hoc main-thread stall watchdog (SC2Log) — user confirmed a real
    // stutter is still there even after moving SC2LiveHeightGrid's rebuild
    // off the main thread, so the actual freeze must be something that
    // stalls the WHOLE process (e.g. a GC pause triggered by that
    // background work's own allocation burst — .NET collections are
    // process-wide, not per-thread) rather than a synchronous call on this
    // thread specifically. Logs any Update()-to-Update() gap over
    // FrameStallThresholdMs with a timestamp, so it can be lined up against
    // SC2LiveHeightGrid's own "Background rebuild finished"/GC-delta log
    // line (both go through SC2Log, whose file writer timestamps every
    // line — see Logs/SC2TerrainEditor.log, unlike Player.log).
    private const double FrameStallThresholdMs = 50.0;
    private double _lastFrameWatchdogTime = -1;

    private void Update()
    {
        if (!Application.isPlaying)
            return;

        var frameNow = Time.realtimeSinceStartupAsDouble;
        if (_lastFrameWatchdogTime >= 0)
        {
            var frameDeltaMs = (frameNow - _lastFrameWatchdogTime) * 1000.0;
            if (frameDeltaMs > FrameStallThresholdMs)
                SC2Log.Log($"[FrameWatchdog] Main thread stalled for {frameDeltaMs:F0}ms.");
        }
        _lastFrameWatchdogTime = frameNow;
#if !UNITY_EDITOR
        // See HandleWantsToQuit/ScheduleForceQuit's own comments — the
        // user already answered Save/Don't Save, so this fires unconditionally
        // even if ShowUI/_activeInstance below would otherwise skip the rest
        // of this method.
        if (_forceQuitAt >= 0f && Time.realtimeSinceStartup >= _forceQuitAt)
        {
            _forceQuitAt = -1f;
            // Environment.Exit(0) crashes the app even when
            // deferred to this ordinary Update() frame — moving it out of
            // the reentrant WM_CLOSE/wantsToQuit callback stack alone does
            // NOT fix it. Environment.Exit runs the full
            // managed shutdown sequence (finalizers, AppDomain.ProcessExit),
            // which is a known crash source in IL2CPP/Mono builds when
            // native subsystems (audio, graphics, plugins) aren't expecting
            // it. Process.Kill() calls TerminateProcess directly instead —
            // no managed shutdown, no finalizers, nothing for a native
            // subsystem to race against; it just ends the process outright.
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { System.Environment.Exit(0); } // last-resort fallback if Kill() itself is denied
        }
#endif
        if (!ShowUI)
            return;
        if (_activeInstance != null && _activeInstance != this)
            return;
        if (_activeInstance == null)
            _activeInstance = this;

        // Wiring blueprint indexing to SC2TerrainShell.
        // DataChanged (fired from SetData/LoadFromDisk) alone is not
        // enough to guarantee the Unit/Prop Browser opens indexed — that
        // event-based path depends on
        // a real "data identity changed" transition happening AFTER
        // EnsureObjectsShell has already subscribed, which isn't guaranteed
        // for every load/session-restore path (e.g. a build's own runtime
        // persistence reconstructing SC2TerrainData through a different
        // route). Call it directly and unconditionally every frame instead —
        // trivially cheap (EnsureObjectsShell/EnsureAssetsIndexed/
        // EnsureIndexed are all early-return no-ops once already indexed or
        // in progress), and removes the dependency on that event's timing
        // entirely.
        EnsureObjectsShell();
        _objectsShell?.EnsureAssetsIndexed();

        TickFlyCamera();
        TickViewTransition();
        ApplyPendingInitialTopView();
        // Standalone-build path: EditorTick below is #if UNITY_EDITOR-only
        // and never exists here, so this Update() (which only fires while
        // Application.isPlaying — guaranteed true in a real build) is the
        // ONLY place that can ever resolve SC2TerrainShell's own deferred
        // SchedulePreviewBuild() (_previewBuildQueued), which nothing but
        // SC2TerrainShell.Update() itself clears. Without this call,
        // _previewBuildQueued stays true forever after any terrain load —
        // and SC2TerrainShell.OnGameViewGUI() early-returns unconditionally
        // whenever it's set, before ever reaching Geometry/Paint mode's own
        // OnGameViewGUI — so every brush (sculpt, Assign Pairs, RNM blend)
        // goes permanently dead, regardless of which one is selected. This
        // was a real regression: SetData() (used by LoadFromDisk) switched
        // from an immediate DestroyPreview() to SchedulePreviewBuild() to
        // fix the "terrain darker after load" race, but nothing here was
        // ever calling SC2TerrainShell.Update() to begin with — the bug
        // was latent (SchedulePreviewBuild() was already used by "Create
        // flat terrain"/"Import Heightmap") until SetData() started hitting
        // it on every single load, not just those two buttons.
        _terrainShell?.Update();
        SC2BusyOverlay.Tick(RepaintGameView);
        ShowBackgroundNotice();
        TickAutosave();

#if !UNITY_EDITOR
        // A single attempt in OnEnable was unreliable (window handle can still
        // be IntPtr.Zero for the first frame or two after the native window is
        // created) — keep retrying each frame until it actually applies. Cheap
        // no-op once done (see SC2RuntimeWindow.RetryEnableNativeMaximizeIfNeeded).
        SC2RuntimeWindow.RetryEnableNativeMaximizeIfNeeded();
#endif
    }

    // Baseline for the autosave countdown — restarted either by a real,
    // explicit Save (manual, any tab's own "Save Terrain" button) via
    // SC2TerrainShell.LastSaveRealtimeSeconds below, or directly by
    // TickAutosave itself right before it fires (a backup-only autosave
    // deliberately never touches LastSaveRealtimeSeconds — see
    // SC2TerrainShell.BeginAutosaveBackup's own doc comment for why).
    // -1 means "nothing observed yet this session/terrain".
    private double _autosaveBaselineRealtime = -1;
    private double _lastObservedSaveRealtime = -1;

    /// <summary>Called once per tick from both Update() (Play mode/standalone)
    /// and EditorTick() (Editor edit-mode) — same dual-call pattern
    /// _terrainShell.Update() itself already uses right above, since this
    /// tool is normally used in the Editor OUTSIDE Play mode. Default
    /// interval is 15 minutes (SC2SaveSettings); disabling autosave, or
    /// having no active terrain, simply skips the countdown.</summary>
    // Tells the user once when a background built for an older, coarser grid was rebuilt at the core's density.
    private void ShowBackgroundNotice()
    {
        var notice = SC2BackgroundTerrain.ConsumeNotice();
        if (notice != null) SetStatus(notice);
    }

    private void TickAutosave()
    {
        if (_terrainShell == null || _terrainShell.Data == null)
        {
            _autosaveBaselineRealtime = -1;
            return;
        }

        // Any save — manual or a previous autosave — restarts the countdown,
        // without every manual "Save Terrain" call site needing to know this
        // timer exists.
        var savedAt = _terrainShell.LastSaveRealtimeSeconds;
        if (savedAt > _lastObservedSaveRealtime)
        {
            _lastObservedSaveRealtime = savedAt;
            _autosaveBaselineRealtime = savedAt;
        }
        if (_autosaveBaselineRealtime < 0)
            _autosaveBaselineRealtime = Time.realtimeSinceStartupAsDouble; // nothing saved yet this session — start counting from now

        if (!SC2SaveSettings.AutosaveEnabled)
            return;

        var now = Time.realtimeSinceStartupAsDouble;
        if (now - _autosaveBaselineRealtime < SC2SaveSettings.AutosaveIntervalMinutes * 60.0)
            return;

        // Another long job (an export, a bake) owns the plate: the backup stays due and is taken when it ends.
        if (SC2BusyOverlay.IsActive)
            return;

        _autosaveBaselineRealtime = now;
        SC2Log.Log($"[Autosave] {SC2SaveSettings.AutosaveIntervalMinutes:0.#}-minute interval reached — backing up '{_terrainShell.Data.MapName}'.");
        // BeginAutosaveBackup, NOT RequestSave: autosave must only ever add a
        // rotating backup snapshot, never overwrite the canonical save file —
        // otherwise leaving the map open (and unsaved) for an hour would mean
        // there's no way back to what was on disk before that hour started.
        // See SC2TerrainShell.BeginAutosaveBackup's own doc comment.
        if (!SC2TerrainShell.AutosaveBackupsSupported)
            return;
        // The backup is written on a worker thread behind the busy plate, so the editor keeps running meanwhile.
        var shell = _terrainShell;
        var mapName = shell.Data.MapName;
        SC2BusyOverlay.RunInBackground("Autosaving", $"Writing a backup of '{mapName}'.",
            () => shell.BeginAutosaveBackup(ok =>
            {
                if (ok) SetStatus($"Autosave backup created for '{mapName}'.");
            }));
    }

#if UNITY_EDITOR
    private void EditorTick()
    {
        if (!ShowUI)
            return;
        if (_activeInstance != null && _activeInstance != this)
            return;
        if (_activeInstance == null)
            _activeInstance = this;

        // Same direct, unconditional trigger as Update() (see its own
        // comment) — needed here too since testing/using this editor mostly
        // happens in Edit mode (not Play mode), where Update() itself never
        // fires at all.
        EnsureObjectsShell();
        _objectsShell?.EnsureAssetsIndexed();

        // Runs on every EditorApplication.update tick regardless of Play
        // state (unlike TickFlyCamera/TickViewTransition below, which the
        // MonoBehaviour's own Update() already covers whenever
        // Application.isPlaying) — see Update()'s own comment for why this
        // call is required at all: nothing else ever resolves
        // SC2TerrainShell's deferred preview rebuild in Edit mode, where
        // Update() itself never fires. Idempotent to call twice in the same
        // frame (Play-mode-inside-the-Editor triggers both this and
        // Update()), so no extra guard is needed here.
        _terrainShell?.Update();
        // Idempotent like the rest: a queued job starts once and the plate
        // is released once, however many ticks reach it in one frame.
        SC2BusyOverlay.Tick(RepaintGameView);
        ShowBackgroundNotice();
        // Same idempotence as above — TickAutosave only ever fires once real
        // time has actually advanced past the interval, so calling it again
        // from Update() the same frame (Play mode inside the Editor) is a
        // harmless no-op.
        TickAutosave();

        if (!Application.isPlaying)
        {
            TickFlyCamera();
            TickViewTransition();
        }
        ApplyPendingInitialTopView();
    }
#endif

    private void OnGUI()
    {
        if (!ShowUI)
            return;
        if (_activeInstance != null && _activeInstance != this)
            return;
        if (_activeInstance == null)
            _activeInstance = this;

        // Defensive: some GPU calls (Graphics.Blit, and GUI.DrawTexture when
        // sampling a RenderTexture) leave RenderTexture.active pointed at that
        // RT as a side effect instead of restoring the screen backbuffer —
        // observed to be worse/more consistent in a standalone Build & Run
        // player than in the Editor. If that leaked state survives into a new
        // OnGUI frame, every IMGUI control drawn this frame renders into that
        // RT instead of the screen. This was the root cause of exported RNM
        // masks containing a captured frame of the editor's own UI. Force the
        // screen backbuffer as the render target before any drawing happens,
        // every frame, so no leaked state from elsewhere can ever propagate in.
        RenderTexture.active = null;

        SyncOverlayFromContext();

        // Swap in the unified dark skin for the whole overlay. This is what makes
        // every plain GUILayout.Button/TextField/Toggle/Slider across the embedded
        // terrain editor and inspectors match the top panel, instead of falling
        // back to Unity's legacy light-grey IMGUI skin. Restored at the end of OnGUI.
        // Must happen BEFORE EnsureStyles: the local styles (dropdown toggles etc.)
        // are derived from GUI.skin and would otherwise bake the legacy sprites in.
        var previousSkin = GUI.skin;
        GUI.skin = SC2UnifiedSkin.Get();

        EnsureStyles();

        var width = Screen.width;
        var height = Screen.height;
        UpdateLayoutRects(width, height);
        GUI.enabled = true;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.contentColor = Color.white;
        UpdateMouseMapCoordinates();

        // Mandatory first screen, both in the Editor Game View and in a real
        // Build & Run player — nothing below this (terrain tabs, menus,
        // viewport input) exists yet until the user configures a game
        // install and opens/creates a map, so none of it is drawn until then.
        if (!StartupComplete)
        {
            DrawStartupOverlay(width, height);
            GUI.enabled = true;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
            GUI.skin = previousSkin;
            return;
        }

        // Every dropdown (File/Edit/View/Visibility/Tools) opens directly under
        // the title bar and, depending on its item count, its card can extend
        // down far enough to visually cover the main-tabs row, the subtabs row,
        // and the top of the left panel/viewport. GUI.depth only controls PAINT
        // order (which is why the dropdown correctly renders on top) — it does
        // NOT reorder input hit-testing for plain immediate-mode controls, which
        // still resolve in the order they were drawn this frame. Without this
        // guard, whatever tab/button happens to sit underneath a dropdown item
        // gets first claim on the click (since it's drawn earlier in OnGUI) and
        // the dropdown item never receives it — exactly the "menu items are
        // unclickable" bug. So: while a menu is open, disable everything drawn
        // BEFORE DrawOpenMenus() that could overlap it, for real input events
        // only (Repaint/Layout must still run normally so layout stays stable).
        // Same exact hit-testing problem, one level down: SC2RuntimeDropdown/
        // SC2RuntimeObjectPicker/SC2RuntimeColorPalette are also drawn last
        // (DrawOverlays(), after DrawMainPanel) so they paint on top
        // correctly, but any button underneath their floating list —
        // "Clear All"/"Assign ALL to this pair" sitting right below the
        // Paint mode's pair-assign Popup, for instance — is drawn/evaluated
        // EARLIER in this same OnGUI call and claims the MouseDown first,
        // consuming it before the dropdown's own (later) buttons ever see
        // it. That's exactly why clicking an item in that dropdown fired
        // whatever control happened to be positioned underneath it instead.
        var runtimeOverlayOpen = false;
#if !UNITY_EDITOR
        runtimeOverlayOpen = SC2RuntimeDropdown.IsAnyOpen || SC2RuntimeObjectPicker.IsAnyOpen || SC2RuntimeColorPalette.IsAnyOpen;
#endif
        // The busy plate is modal too: nothing underneath it takes a click while a long job is queued or running.
        _menuOverlayBlocksInput = (AnyMenuOpen() || runtimeOverlayOpen || SC2BusyOverlay.IsActive)
            && Event.current != null
            && Event.current.type != EventType.Repaint
            && Event.current.type != EventType.Layout;

        // Must run before DrawHeader/DrawMainPanel below — see this method's
        // own doc comment for why (a focused numeric field's native TextField
        // consumes Ctrl+Z internally while DRAWING itself, which happens
        // inside DrawMainPanel).
        HandleGlobalUndoRedoSaveHotkey();

        // A modal (texture browser, import panels, confirm dialog, busy plate) is drawn after the panels, so the panels would see
        // a click, a key or the wheel first. GUI.enabled = false only stops the standard controls; many of ours read the event
        // themselves (thumbnails, foldout headers, clickable rows). So while a modal is open the panels are drawn against an
        // Ignore event, which none of them reacts to; the real event is put back before the menus and the modals are drawn.
        var maskedEventType = EventType.Ignore;
        var panelEventsMasked = false;
        if (Event.current != null && AnyModalOpen()
            && Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout
            && Event.current.type != EventType.Used && Event.current.type != EventType.Ignore)
        {
            maskedEventType = Event.current.type;
            panelEventsMasked = true;
            // The texture slots of the panels stay live while the browser is the only window open: a click on another slot
            // moves the browser there (see SC2TextureBrowser.TryTakePanelClick).
            SC2TextureBrowser.BeginPanelEventMask(Event.current, !AnyModalOpen(includeTextureBrowser: false));
            Event.current.type = EventType.Ignore;
        }

        DrawHeader(width);
        // DrawSubtabs applies this same _menuOverlayBlocksInput guard
        // internally itself (see its own GUI.enabled handling), so it's
        // already correctly protected regardless of call order here.
        DrawSubtabs(width);

        var previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !_menuOverlayBlocksInput;

        if (ShowPanel)
            DrawMainPanel(width, height);

        if (ShowReferencePanel)
            DrawReferencePanel();

        // Re-assert immediately before drawing, not just once at the top of
        // OnGUI: DrawMainPanel above can paint into BlendRT/PairMapRT
        // (Paint Pairs/Paint Blend brush strokes, mask-group switches) partway
        // through this same frame, and if any of those leave the active
        // render target in an unexpected state, the gizmo drawn right after
        // would render straight into that texture instead of the screen.
        RenderTexture.active = null;
        // Drawn AFTER the reference panel so it visually floats over the
        // panel's own top portion — the gizmo keeps its existing unchanged
        // Rect (see UpdateLayoutRects), no repositioning needed.
        DrawNavigationGizmo(width);
        DrawFloatingReferenceImage();
        GUI.enabled = previousEnabled;
        if (panelEventsMasked)
        {
            var slotTookClick = SC2TextureBrowser.EndPanelEventMask();
            if (Event.current != null && Event.current.type == EventType.Ignore)
            {
                Event.current.type = maskedEventType;
                // The click belonged to a slot, not to the browser or whatever is drawn after the panels.
                if (slotTookClick) Event.current.Use();
            }
        }

        // REVERTED: tried calling DrawOpenMenus() here (before
        // DrawHeader/DrawSubtabs/DrawMainPanel) on the theory that GUI.depth
        // reorders paint independent of call order, so input priority could
        // be fixed without breaking visual stacking. Confirmed WRONG via
        // real Build testing: GUI.depth reordering only applies to real
        // GUI.Window/GUILayout.Window-based UI, not plain GUILayout.BeginArea
        // content like DrawCard uses here - moving the call earlier broke
        // the ONLY thing actually keeping the dropdown on top (being drawn
        // last), without confirmed proof it even fixed the original click
        // issue. Back to drawing it last, matching every other overlay in
        // this file - correct paint order is proven, still investigating
        // the real fix for menu-item clicks.
        DrawOpenMenus();

        // Imported terrain.bdf objects are now edited inline as the
        // Terrain tab's "Imported" subtab (DrawImportedTerrainsLeftPanelContent,
        // wired via SC2TerrainShell.DrawImportedTerrainsContent) instead of a
        // floating overlay card — nothing to draw here anymore.

        // Shared catalog browser: the same UI and selection semantics in the
        // Unity Game view and in a standalone build. Draw last so it remains modal.
        SC2TerrainShell.DrawRuntimeTexturePickerOverlay();
        // Same reasoning — must draw last so it stays modal and on top.
        SC2HotkeysPanel.DrawOverlay();
        SC2ThemePanel.DrawOverlay();
#if !UNITY_EDITOR
        // Same reasoning for the runtime Popup/ColorField shims (UnityEditorRuntimeShim.cs):
        // their dropdown lists and color palettes are floating overlays that must be
        // drawn last so they render unclipped, on top of the panel that opened them.
        SC2RuntimeDropdown.DrawOverlays();
        SC2RuntimeColorPalette.DrawOverlays();
        // Same reasoning again for EditorGUILayout.ObjectField's picker shim
        // (e.g. the "Terrain Data" field) — see SC2RuntimeObjectPicker.
        SC2RuntimeObjectPicker.DrawOverlays();
        // Drawn last of all: a confirm dialog must sit on top of everything,
        // including the dropdowns above (e.g. confirming a terrain picked
        // from the "Delete Saved Terrain..." list).
        SC2RuntimeConfirm.DrawOverlays();

        if (SC2RuntimeDropdown.TryConsumeLabel(LoadTerrainDropdownId, out var loadTerrainName))
            ApplyLoadedTerrainName(loadTerrainName);
        if (SC2RuntimeDropdown.TryConsumeLabel(DeleteTerrainDropdownId, out var deleteTerrainName))
            RequestDeleteTerrainName(deleteTerrainName);
        if (SC2RuntimeDropdown.TryConsumeLabel(RestoreBackupDropdownId, out var restoreBackupId))
            RequestRestoreBackup(restoreBackupId);
#endif

        // Not gated behind #if !UNITY_EDITOR: SC2MapImportPanel/
        // SC2MapImportOrchestrator have no UnityEditor dependency, so this
        // draws identically in the Editor Game View and a build — the
        // unified "Import Map" pipeline (this replaced the old Build-only
        // SC2RuntimeScdImporter + its own separate report overlay).
        _mapImportPanel?.DrawOverlay(width, height);
        // Same reasoning — must actually work in a real Build & Run player,
        // not just the Editor (a real EditorWindow never can).
        _infoBdfEditorPanel?.DrawOverlay(width, height);
        _lobbySpawnPanel?.DrawOverlay(width, height);

        GUI.enabled = true;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.contentColor = Color.white;
        // Above everything, including the confirm dialog's own scrim.
        SC2BusyOverlay.Draw();
        GUI.skin = previousSkin;

        if (Event.current != null && Event.current.type == EventType.Used && Event.current.rawType == EventType.MouseDrag && _mainTab == 3)
            Debug.Log($"[SC2DragDiag] MouseDrag was used before the viewport handlers: kbd={GUIUtility.keyboardControl} hot={GUIUtility.hotControl} focused='{GUI.GetNameOfFocusedControl()}'");
        if (Event.current == null || Event.current.type != EventType.Used)
        {
            // Viewport overlays (Objects' Areas/Chains/Graph lines, Terrain's
            // paint cursor, Costs' grid, Collision2's preview mesh, Decals'
            // placement preview — all 5 Handle*GameViewInput calls below)
            // were drawing ON TOP of modals
            // (DrawRuntimeTexturePickerOverlay/_mapImportPanel/
            // _infoBdfEditorPanel, drawn above specifically "so it remains
            // modal") because these calls run at the very END of OnGUI,
            // AFTER those modals had already drawn this same frame.
            // Reordering the 5 calls to run earlier isn't safe here (they
            // also consume real input events, and GUI.enabled/skin/color
            // state is deliberately reset for them right below) — skipping
            // them entirely while a modal is genuinely open is also the more
            // correct behavior anyway: a modal should block viewport
            // painting/placing/navigation underneath it, not just visually
            // cover it.
            if (!AnyModalOpen())
            {
                ReleaseStuckNumericFocusOnViewportClick();

                // The viewport overlays below paint after the dropdown menus, so they have to leave the menu's area alone.
                SC2ObjectsShell.OverlayOccluder = AnyMenuOpen() && _openMenuRect.width > 0f
                    ? new Rect(_openMenuRect.x - 2f, _openMenuRect.y - 2f, _openMenuRect.width + 4f, _openMenuRect.height + 4f)
                    : Rect.zero;
#if !UNITY_EDITOR
                // The colour picker of the materials and pairs floats over the viewport the same way.
                var picker = SC2RuntimeColorPalette.PanelRect;
                if (picker.width > 0f)
                {
                    var pad = new Rect(picker.x - 2f, picker.y - 2f, picker.width + 4f, picker.height + 4f);
                    var current = SC2ObjectsShell.OverlayOccluder;
                    SC2ObjectsShell.OverlayOccluder = current.width > 0f
                        ? Rect.MinMaxRect(Mathf.Min(current.xMin, pad.xMin), Mathf.Min(current.yMin, pad.yMin), Mathf.Max(current.xMax, pad.xMax), Mathf.Max(current.yMax, pad.yMax))
                        : pad;
                }
#endif

                // Same reasoning as the reset before DrawNavigationGizmo above:
                // HandleTerrainGameViewInput below is what actually calls
                // PaintPair/PaintBlend (Graphics.Blit into PairMapRT/BlendRT), and
                // it runs after every panel/menu/overlay this frame has already
                // drawn — re-assert once more immediately before it so the brush
                // cursor it draws first (DrawGameViewGizmo, before the paint call
                // itself) can't inherit a leaked render target from anything above.
                RenderTexture.active = null;
                // Runs first and consumes its own clicks (e.Use()) so the
                // active tab's own paint/place/select handlers below never
                // also fire on the same click while Measure is active.
                HandleMeasureToolGameViewInput();
                // Same reasoning as Measure just above: reference-unit
                // placement/select/drag consumes its own click via e.Use()
                // for the specific cases it cares about (armed placement,
                // dragging, clicking an existing reference unit) and falls
                // through untouched otherwise, so it must run before the
                // active tab's own click handlers below get a chance to
                // consume the SAME event first (e.g. Objects/Markers'
                // click-to-select swallowing a placement click meant for a
                // reference unit, which is what broke placement originally —
                // this shell was previously called last, after every tab
                // handler had already had first crack at MouseDown).
                HandleReferenceUnitsGameViewInput();
                DrawMapFrameOverlay();
                // HandleTerrainGameViewInput draws its Geometry/Paint brush
                // ring ONLY when the Terrain tab is active and does nothing
                // else cross-tab, so suppressing the whole call while Measure
                // is active cleanly hides just that cursor. HandleCostsGameViewInput
                // and HandleObjectsGameViewInput are NOT safe to suppress the
                // same way — both deliberately keep drawing PASSIVE content
                // from any tab via their own Overlay* toggles (Costs tint
                // overlay, Objects' props/markers/path graphs/areas), fully
                // independent of whether their own tab's brush/placement
                // cursor is active; skipping them here previously made path
                // graphs vanish the moment Measure turned on. Their own
                // internal allowInput/tab-active checks already gate the
                // actual brush cursor drawing without needing to be skipped
                // wholesale from here.
                if (_tool != "Measure")
                    HandleTerrainGameViewInput();
                SyncTerrainOverlayFlags();
                HandleCostsGameViewInput();
                PushBuildableOverlay();
                HandleCollision2GameViewInput();
                HandleObjectsGameViewInput();
                HandleDecalsGameViewInput();
                HandleImportedTerrainGameViewInput();
                DrawSelectedImportedTerrainFrame();
                HandleHotkeys();

                // Consume navigation events when mouse is over the GameView UI.
                // This prevents the SceneView OnSceneGUI from receiving clicks on
                // our buttons/menus as navigation events.
                HandleGameViewNavigation();
            }
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        var selected = Selection.activeGameObject;
        if (selected == null)
            return;

        var meshFilters = selected.GetComponentsInChildren<MeshFilter>();
        Gizmos.color = new Color(1f, 0.85f, 0.05f, 1f);

        if (meshFilters.Length > 0)
        {
            for (var i = 0; i < meshFilters.Length; i++)
            {
                var meshFilter = meshFilters[i];
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;

                var t = meshFilter.transform;
                Gizmos.DrawWireMesh(meshFilter.sharedMesh, t.position, t.rotation, t.lossyScale);
            }

            return;
        }

        var renderers = selected.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Gizmos.DrawWireCube(selected.transform.position, Vector3.one * 3f);
            return;
        }

        for (var i = 0; i < renderers.Length; i++)
            Gizmos.DrawWireCube(renderers[i].bounds.center, renderers[i].bounds.size);
    }
#endif

    private void EnsureStyles()
    {
        // _headerTexture doubles as a liveness sentinel: baked textures use
        // HideFlags.HideAndDontSave and can still be destroyed by Unity on
        // assembly reloads / play-mode transitions. If that happened, every
        // GUIStyle here points at dead textures and the overlay renders as
        // grey/blank boxes — rebuild instead of trusting _stylesReady forever.
        // _gizmoBallSolid is checked too: it's a plain Texture2D (destroyable
        // the same way) but was added after _headerTexture, so a stale
        // instance that somehow kept _headerTexture alive while this one
        // lagged null would otherwise NRE inside GUI.Label every frame
        // (DrawGizmoAxisBall) instead of self-healing.
        // _stylesLastScale: rebuild whenever UI Scale changes too, not just
        // on the texture-liveness check above — this method is only run
        // once lazily (per the "no need to rebuild every frame" reasoning
        // right below), so a scale change needs its own explicit trigger.
        if (_stylesReady && _headerTexture != null && _gizmoBallSolid != null && _gizmoLabelStyle != null && Mathf.Approximately(_stylesLastScale, SC2UiScale.Scale) && _stylesLastThemeVersion == SC2Theme.RenderVersion)
            return;
        _stylesLastScale = SC2UiScale.Scale;
        _stylesLastThemeVersion = SC2Theme.RenderVersion;

        _headerTexture = MakeSolidTexture(ColorBg);
        // Panel uses the darker chrome color (same as the header) so that the
        // rounded Surface-colored cards inside it read as elevated elements —
        // one background/card relationship across the whole UI.
        _panelTexture = MakeSolidTexture(PanelColor);
        _buttonTexture = MakeSolidTexture(new Color(1f, 1f, 1f, 0f));
        _activeButtonTexture = MakeSolidTexture(ColorSurfaceHi);
        _hoverTexture = MakeSolidTexture(new Color(1f, 1f, 1f, 0.05f));
        _dividerTexture = MakeSolidTexture(Color.white);
        _accentTexture = MakeSolidTexture(ColorAccent);
        _accentSoftTexture = MakeSolidTexture(ColorAccentSoft);

        // 9-sliced rounded-corner textures: small baked texture + GUIStyle.border so
        // Unity stretches only the flat middle, keeping corners crisp at any size.
        _cardTexture = MakeRoundedTexture(28, 9, SC2Theme.Surface, SC2Theme.Line, 1.25f);
        _cardShadowTexture = MakeRoundedTexture(28, 10, ColorShadow, new Color(0, 0, 0, 0), 0f);
        var pillFill = MakeRoundedTexture(24, 12, ColorSurfaceHi, new Color(0, 0, 0, 0), 0f);
        var pillGhost = MakeRoundedTexture(24, 10, ColorSurface, ColorBorder, 1f);
        var pillHover = MakeRoundedTexture(24, 10, ColorSurfaceHi, ColorBorder, 1f);

        _panelStyle = new GUIStyle(GUIStyle.none)
        {
            padding = new RectOffset(10, 10, 8, 8),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            normal = { background = _panelTexture, textColor = ColorTextPrimary }
        };
        _panelContentStyle = new GUIStyle(GUIStyle.none)
        {
            padding = new RectOffset(_panelStyle.padding.left, 0, _panelStyle.padding.top, _panelStyle.padding.bottom),
            margin = new RectOffset(0, 0, 0, 0),
        };
        _panelHeaderStyle = new GUIStyle(GUIStyle.none)
        {
            padding = new RectOffset(_panelStyle.padding.left, 0, _panelStyle.padding.top, 0),
            margin = new RectOffset(0, 0, 0, 0),
        };
        _headerStyle = new GUIStyle(GUIStyle.none)
        {
            padding = new RectOffset(12, 12, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            normal = { background = _headerTexture, textColor = ColorTextPrimary }
        };
        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = SC2Type.Nav,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = ColorTextPrimary }
        };
        _logoDotStyle = new GUIStyle(GUI.skin.box)
        {
            fixedWidth = SC2UiScale.Pxf(8),
            fixedHeight = SC2UiScale.Pxf(8),
            margin = new RectOffset(0, 6, 8, 0),
            normal = { background = _accentTexture }
        };

        // Main tabs: flat text on the header; the active one is lit with the panel colour and has the accent line under it.
        var tabClear = MakeUnderlinedTexture(Color.clear, Color.clear, 0);
        var tabHover = MakeUnderlinedTexture(new Color(SC2Theme.Bg3.r, SC2Theme.Bg3.g, SC2Theme.Bg3.b, 0.55f), Color.clear, 0);
        var tabActive = MakeTabGlowTexture(SC2Theme.TabGlowTop, SC2Theme.TabGlowBottom, SC2Theme.TabGlowBottom);
        _tabStyle = new GUIStyle(GUI.skin.button)
        {
            fixedHeight = TabsRowHeight,
            fontSize = SC2Type.Nav,
            alignment = TextAnchor.MiddleCenter,
            border = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(SC2UiScale.Px(10), SC2UiScale.Px(10), 0, 0),
            normal = { background = tabClear, textColor = SC2Theme.TabText },
            hover = { background = tabHover, textColor = SC2Theme.TextBright },
            active = { background = tabHover, textColor = SC2Theme.TextBright },
            focused = { background = tabClear, textColor = SC2Theme.TabText }
        };
        _activeTabStyle = new GUIStyle(_tabStyle)
        {
            fontStyle = FontStyle.Bold,
            normal = { background = tabActive, textColor = SC2Theme.AccentText },
            hover = { background = tabActive, textColor = SC2Theme.AccentText },
            active = { background = tabActive, textColor = SC2Theme.AccentText },
            focused = { background = tabActive, textColor = SC2Theme.AccentText }
        };

        // Sub-tabs: flat text; the active one is a small outlined chip. Clipping is a deliberate safety net on top of
        // CompactSubtabLabel's abbreviations below — a label this method doesn't recognize (e.g. an unexpected Costs block
        // name) should get visually clipped to its button's own width instead of bleeding text into the neighbouring tab.
        var subClear = MakeRoundedTexture(24, 9, Color.clear, Color.clear, 0f);
        var subHover = MakeRoundedTexture(24, 9, new Color(SC2Theme.Bg3.r, SC2Theme.Bg3.g, SC2Theme.Bg3.b, 0.8f), Color.clear, 0f);
        var subActive = MakeRoundedTexture(24, 9, SC2Theme.Bg4, ColorBorder, 1f);
        _subtabStyle = new GUIStyle(GUI.skin.button)
        {
            fixedHeight = SC2UiScale.Pxf(22),
            fontSize = SC2Type.Body,
            alignment = TextAnchor.MiddleCenter,
            border = new RectOffset(10, 10, 10, 10),
            margin = new RectOffset(2, 2, 3, 3),
            padding = new RectOffset(SC2UiScale.Px(10), SC2UiScale.Px(10), 0, 0),
            clipping = TextClipping.Clip,
            normal = { background = subClear, textColor = SC2Theme.TabText },
            hover = { background = subHover, textColor = SC2Theme.TextBright },
            active = { background = subHover, textColor = SC2Theme.TextBright },
            focused = { background = subClear, textColor = SC2Theme.TabText }
        };
        _activeSubtabStyle = new GUIStyle(_subtabStyle)
        {
            normal = { background = subActive, textColor = SC2Theme.TextBright },
            hover = { background = subActive, textColor = SC2Theme.TextBright },
            active = { background = subActive, textColor = SC2Theme.TextBright },
            focused = { background = subActive, textColor = SC2Theme.TextBright }
        };

        // Menu-bar buttons (File/Edit/View/...): flat text with a soft hover highlight; the open menu's button stays lit.
        _menuBarButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fixedHeight = SC2UiScale.Pxf(24),
            fontSize = SC2Type.Nav,
            alignment = TextAnchor.MiddleCenter,
            border = new RectOffset(10, 10, 10, 10),
            margin = new RectOffset(1, 1, 0, 0),
            padding = new RectOffset(SC2UiScale.Px(10), SC2UiScale.Px(10), 0, 0),
            normal = { background = subClear, textColor = ColorTextPrimary },
            hover = { background = subHover, textColor = SC2Theme.TextBright },
            active = { background = subHover, textColor = SC2Theme.TextBright },
            focused = { background = subClear, textColor = ColorTextPrimary }
        };
        _menuBarButtonActiveStyle = new GUIStyle(_menuBarButtonStyle)
        {
            normal = { background = pillFill, textColor = SC2Theme.TextBright },
            focused = { background = pillFill, textColor = SC2Theme.TextBright }
        };

        // Dropdown menu items: flat list rows with hover highlight instead of default
        // chunky GUI.skin.button rows.
        // Sits directly on the dropdown card's own background (ColorSurface,
        // same lightness issue as the tab pills above) — full TextPrimary.
        _dropdownItemStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = SC2Type.Body,
            alignment = TextAnchor.MiddleLeft,
            fixedHeight = SC2UiScale.Pxf(22),
            border = new RectOffset(4, 4, 4, 4),
            margin = new RectOffset(0, 0, 1, 1),
            padding = new RectOffset(SC2UiScale.Px(8), SC2UiScale.Px(8), 0, 0),
            normal = { background = _buttonTexture, textColor = ColorTextPrimary },
            hover = { background = _hoverTexture, textColor = ColorTextPrimary },
            active = { background = _hoverTexture, textColor = ColorTextPrimary }
        };
        _dropdownToggleStyle = new GUIStyle(GUI.skin.toggle)
        {
            fontSize = SC2Type.Body,
            normal = { textColor = ColorTextPrimary },
            onNormal = { textColor = ColorTextPrimary }
        };
        _dropdownSectionLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = SC2Type.Caption,
            fontStyle = FontStyle.Bold,
            margin = new RectOffset(0, 0, 4, 2),
            normal = { textColor = ColorTextSecondary }
        };

        _miniTextStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = SC2Type.Body,
            normal = { textColor = ColorTextPrimary }
        };
        _secondaryTextStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = SC2Type.Caption,
            normal = { textColor = ColorTextSecondary }
        };
        // The status and the mouse coordinates sit in small bordered pills on the menu bar.
        var infoPill = MakeRoundedTexture(24, 10, SC2Theme.Bg2, SC2Theme.Line, 1f);
        _statusDotTexture = MakeRoundedTexture(16, 8, SC2Theme.Green, new Color(0, 0, 0, 0), 0f);
        _statusPillStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = SC2Type.Body,
            alignment = TextAnchor.MiddleLeft,
            // One line only: the box style wraps, and a status of several lines (an export's report) showed just its middle line.
            wordWrap = false,
            border = new RectOffset(9, 9, 9, 9),
            padding = new RectOffset(SC2UiScale.Px(24), SC2UiScale.Px(10), SC2UiScale.Px(2), SC2UiScale.Px(2)),
            // Clipping as a deliberate safety net (same reasoning as _subtabStyle/_activeSubtabStyle above) — a status
            // message longer than this pill's own (deliberately capped, see DrawHeader) width should clip to its own box
            // instead of overflowing into the menu bar/mouse-coordinates pill next to it.
            clipping = TextClipping.Clip,
            normal = { background = infoPill, textColor = SC2Theme.TextBright }
        };
        _coordsPillStyle = new GUIStyle(_statusPillStyle)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(SC2UiScale.Px(10), SC2UiScale.Px(10), SC2UiScale.Px(2), SC2UiScale.Px(2)),
            normal = { background = infoPill, textColor = SC2Theme.TextPrimary }
        };
        _cardStyle = new GUIStyle(GUIStyle.none)
        {
            padding = new RectOffset(10, 10, 8, 8),
            border = new RectOffset(9, 9, 9, 9),
            normal = { background = _cardTexture, textColor = ColorTextPrimary }
        };
        _shadowStyle = new GUIStyle(GUIStyle.none)
        {
            border = new RectOffset(10, 10, 10, 10),
            normal = { background = _cardShadowTexture }
        };
        // Navigation gizmo: plain circular textures (not 9-sliced/GUIStyle
        // backgrounds like the rest of the chrome) so the axis balls are true
        // circles, tinted per-axis at draw time — matches Blender's viewport
        // gizmo look (floating colored spheres, no card/box behind them).
        _gizmoBallSolid = MakeCircleTexture(48, 0f);
        _gizmoBallHollow = MakeCircleTexture(48, 5f);
        _gizmoBackdrop = MakeCircleTexture(64, 0f);
        // Built from scratch (not "new GUIStyle(GUI.skin.label)" like the other
        // text styles here) so it has no dependency on the current GUI.skin at
        // all — the label of a re-baked/rebuilt SC2UnifiedSkin, or any GUISkin
        // fragility, can't leave this one in a broken state.
        _gizmoLabelStyle = new GUIStyle
        {
            fontSize = SC2Type.Caption,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        };

        _stylesReady = true;
    }

    /// <summary>
    /// Bakes an anti-aliased circle texture: a solid filled disc when
    /// <paramref name="ringThickness"/> is 0, or a hollow ring of that pixel
    /// thickness otherwise (used for the gizmo's negative-axis balls).
    /// </summary>
    private static Texture2D MakeCircleTexture(int size, float ringThickness)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var half = size * 0.5f;
        var outerRadius = half - 1f;
        var innerRadius = outerRadius - ringThickness;
        var hollow = ringThickness > 0.01f;

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x + 0.5f - half;
            var dy = y + 0.5f - half;
            var dist = Mathf.Sqrt(dx * dx + dy * dy);
            var alpha = Mathf.Clamp01(0.5f - (dist - outerRadius));
            if (hollow)
                alpha *= Mathf.Clamp01(0.5f - (innerRadius - dist));
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }

        tex.Apply(false, true);
        return tex;
    }

    /// <summary>
    /// Bakes a small anti-aliased rounded-rectangle texture with an optional 1px border,
    /// meant to be used with a matching GUIStyle.border (9-slice) so it stretches cleanly
    /// to any control size without corner distortion.
    /// </summary>
    private static Texture2D MakeRoundedTexture(int size, float radius, Color fill, Color border, float borderWidth)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var hasBorder = border.a > 0f && borderWidth > 0f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = RoundedRectDistance(x + 0.5f, y + 0.5f, size, radius);
                var coverage = Mathf.Clamp01(0.5f - d);
                var pixel = fill;
                if (hasBorder && d > -borderWidth)
                {
                    var edgeT = Mathf.Clamp01((d + borderWidth) / borderWidth);
                    pixel = Color.Lerp(fill, border, edgeT);
                }
                pixel.a *= coverage;
                tex.SetPixel(x, y, pixel);
            }
        }

        tex.Apply(false, true);
        return tex;
    }

    private static float RoundedRectDistance(float x, float y, float size, float radius)
    {
        var half = size * 0.5f;
        var dx = Mathf.Abs(x - half) - (half - radius);
        var dy = Mathf.Abs(y - half) - (half - radius);
        var ax = Mathf.Max(dx, 0f);
        var ay = Mathf.Max(dy, 0f);
        var outside = Mathf.Sqrt(ax * ax + ay * ay) - radius;
        var inside = Mathf.Min(Mathf.Max(dx, dy), 0f);
        return outside + inside;
    }

    /// <summary>The active main tab: a blue-tinted gradient rising to a line at the bottom, stretched over the tab (no 9-slice).</summary>
    private static Texture2D MakeTabGlowTexture(Color top, Color bottom, Color line)
    {
        const int width = 4, height = 64, lineRows = 3;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        for (var y = 0; y < height; y++)
        {
            var color = y < lineRows ? line : Color.Lerp(bottom, top, (y - lineRows) / (float)(height - lineRows - 1));
            for (var x = 0; x < width; x++)
                tex.SetPixel(x, y, color);
        }
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>A small texture to be 9-sliced with only a bottom border: <paramref name="fill"/> above a line of <paramref name="lineHeight"/> pixels.</summary>
    private static Texture2D MakeUnderlinedTexture(Color fill, Color line, int lineHeight)
    {
        const int width = 4, height = 8;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                tex.SetPixel(x, y, y < lineHeight ? line : fill);
        tex.Apply(false, true);
        return tex;
    }

    private static Texture2D MakeSolidTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private static void DrawSolidRect(Rect rect, Color color, Texture2D fallbackTexture)
    {
#if UNITY_EDITOR
        EditorGUI.DrawRect(rect, color);
#else
        if (fallbackTexture == null)
            return;

        var previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, fallbackTexture, ScaleMode.StretchToFill, false);
        GUI.color = previousColor;
#endif
    }

    private void DrawHeader(int width)
    {
        var titleBarRect = new Rect(0, 0, width, TitleBarHeight);
        var tabsRect = new Rect(0, TitleBarHeight, width, TabsRowHeight);
        DrawSolidRect(titleBarRect, ColorBg, _dividerTexture);
        DrawSolidRect(tabsRect, ColorBg, _dividerTexture);
        DrawSolidRect(new Rect(0, TitleBarHeight - 1, width, 1), ColorDivider, _dividerTexture);

        GUILayout.BeginArea(titleBarRect, _headerStyle);
        GUILayout.BeginHorizontal(GUILayout.Height(TitleBarHeight));
        GUILayout.Space(2);
        DrawMenuButton("File", ref _showFileMenu, ref _fileMenuRect, 44);
        DrawMenuButton("Edit", ref _showEditMenu, ref _editMenuRect, 44);
        DrawMenuButton("View", ref _showViewMenu, ref _viewMenuRect, 46);
        DrawMenuButton("Visibility", ref _showVisibilityMenu, ref _visibilityMenuRect, 78);
        DrawMenuButton("Symmetry", ref _showSymmetryMenu, ref _symmetryMenuRect, 76);
        DrawMenuButton("Tools", ref _showToolsMenu, ref _toolsMenuRect, 48);
        DrawMenuButton("Backup", ref _showAutosaveMenu, ref _autosaveMenuRect, 56);
        DrawMenuButton("Customize", ref _showCustomizationMenu, ref _customizationMenuRect, 78);
        GUILayout.FlexibleSpace();

        // Drop the least-critical labels first on narrow windows (small Build &
        // Run resolutions) instead of letting them overflow past the right edge
        // of the title bar — the fixed-width labels + status pill together need
        // roughly 500px, which does not fit alongside the menu buttons at small
        // window sizes.
        var showMouseCoordinates = width >= 900;
        if (showMouseCoordinates)
            GUILayout.Label(_mouseMapCoordinates, _coordsPillStyle, GUILayout.Width(SC2UiScale.Pxf(190f)), GUILayout.Height(SC2UiScale.Pxf(20f)));
        GUILayout.Space(SC2UiScale.Pxf(6f));
        var statusWidth = Mathf.Min(SC2UiScale.Pxf(320f), Mathf.Max(SC2UiScale.Pxf(110f), width * 0.18f));
        // The status on one line, cut with "..." where it does not fit (a report of several lines is joined).
        var statusLine = (Status ?? "").Replace("\r\n", " · ").Replace("\n", " · ");
        GUILayout.Label(SC2StartupUi.Fit(statusLine, _statusPillStyle, statusWidth - _statusPillStyle.padding.horizontal), _statusPillStyle, GUILayout.Width(statusWidth), GUILayout.Height(SC2UiScale.Pxf(20f)));
        if (Event.current.type == EventType.Repaint && _statusDotTexture != null)
        {
            var pill = GUILayoutUtility.GetLastRect();
            var dot = SC2UiScale.Pxf(8f);
            GUI.DrawTexture(new Rect(pill.x + SC2UiScale.Pxf(10f), pill.center.y - dot * 0.5f, dot, dot), _statusDotTexture);
        }
        GUILayout.Space(4);

        // Minimize/maximize/close are exclusively the native OS title bar's
        // job — no in-UI duplicates. SC2RuntimeWindow.EnsureWindowedOnStartup
        // (called on enable) patches the real OS window's style bits directly
        // via Win32 so that native title bar's own maximize box actually
        // works on this project's Unity version, instead of adding a
        // custom button here to compensate for it being disabled.

        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        // An open dropdown's card can extend down far enough to cover this row
        // (see the comment on _menuOverlayBlocksInput in OnGUI) — disable it for
        // real input events so a tab underneath doesn't steal the dropdown's click.
        var previousEnabledTabs = GUI.enabled;
        GUI.enabled = previousEnabledTabs && !_menuOverlayBlocksInput;
        GUILayout.BeginArea(tabsRect, _headerStyle);
        GUILayout.BeginHorizontal(GUILayout.Height(TabsRowHeight));
        for (var i = 0; i < GameMainTabs.Length; i++)
        {
            var style = i == _mainTab ? _activeTabStyle : _tabStyle;
            // Was GUILayout.Height(26) — a raw unscaled literal that, as an
            // explicit GUILayoutOption, overrides the style's own already-
            // correctly-scaled fixedHeight (SC2UiScale.Pxf(26) in EnsureStyles)
            // instead of the other way around, forcing every main-tab button's
            // real rendered/hit-tested height back down to a fixed 26px
            // regardless of UI Scale. Let the style's fixedHeight govern
            // instead, same as every other segmented-tab button in this file.
            if (GUILayout.Button(GameMainTabs[i], style))
                SelectMainTab(i);
            if (i == _mainTab && Event.current.type == EventType.Repaint)
            {
                var tab = GUILayoutUtility.GetLastRect();
                var lineInset = SC2UiScale.Pxf(10f);
                var previousColor = GUI.color;
                GUI.color = SC2Theme.TabLine;
                GUI.DrawTexture(new Rect(tab.x + lineInset, tab.yMax - SC2UiScale.Pxf(2f), Mathf.Max(1f, tab.width - lineInset * 2f), Mathf.Max(2f, SC2UiScale.Pxf(2f))), _dividerTexture);
                GUI.color = previousColor;
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
        GUI.enabled = previousEnabledTabs;

        var dividerRect = new Rect(0, TitleBarHeight + TabsRowHeight - 1, width, 1);
        DrawSolidRect(dividerRect, ColorDivider, _dividerTexture);
    }


    private static void SC2EditorStylesDivider()
    {
        GUILayout.Space(6);
        var r = GUILayoutUtility.GetRect(1f, SC2UiScale.Pxf(18f), GUILayout.Width(1f));
        DrawSolidRect(r, ColorDivider, null);
        GUILayout.Space(6);
    }

    private void DrawMenuButton(string label, ref bool open, ref Rect rect, float width)
    {
        // width/24 are the CALLER's unscaled base pixel values — scaled
        // here, once, rather than at each of the 6 call sites, since without
        // scaling, at UI Scale > 1x the menu-bar's own bigger font
        // ("View"/"Visibility"/"Symmetry"/"Tools" etc.) no longer fits their
        // fixed-width buttons, so adjacent buttons' text visually runs
        // together ("ViewVisibilitySymmetrTools") — same root-cause shape
        // as the dropdown-menu clipping issue avoided just before this one,
        // just on the horizontal axis of the menu BAR itself instead of the
        // vertical axis of an open dropdown's card.
        var style = open ? _menuBarButtonActiveStyle : _menuBarButtonStyle;
        if (GUILayout.Button(label, style, GUILayout.ExpandWidth(false), GUILayout.Height(SC2UiScale.Pxf(22.4f))))
        {
            CloseMenusExcept(label);
            open = !open;
        }

        if (Event.current.type == EventType.Repaint)
            rect = GUILayoutUtility.GetLastRect();
    }

    /// <summary>The thin cyan line under the active sub-tab (drawn in the sub-tab area's own coordinates).</summary>
    private void DrawSubtabLine(Rect button)
    {
        if (Event.current.type != EventType.Repaint || _dividerTexture == null) return;
        var previous = GUI.color;
        GUI.color = SC2Theme.Cyan;
        var inset = SC2UiScale.Pxf(6f);
        GUI.DrawTexture(new Rect(button.x + inset, button.yMax - SC2UiScale.Pxf(2f), Mathf.Max(1f, button.width - inset * 2f), Mathf.Max(2f, SC2UiScale.Pxf(2f))), _dividerTexture);
        GUI.color = previous;
    }

    private void CloseMenusExcept(string label)
    {
        if (label != "File") _showFileMenu = false;
        if (label != "Edit") _showEditMenu = false;
        if (label != "View") _showViewMenu = false;
        if (label != "Visibility") _showVisibilityMenu = false;
        if (label != "Symmetry") _showSymmetryMenu = false;
        if (label != "Tools") _showToolsMenu = false;
        if (label != "Backup") _showAutosaveMenu = false;
        if (label != "Customize") _showCustomizationMenu = false;
    }

    private void DrawOpenMenus()
    {
        if (!AnyMenuOpen())
            return;

        var previousDepth = GUI.depth;
        GUI.depth = -1000;
        DrawMenuClickCatcher();

        if (_showFileMenu) DrawFileMenu();
        if (_showEditMenu) DrawEditMenu();
        if (_showViewMenu) DrawViewMenu();
        if (_showVisibilityMenu) DrawVisibilityMenu();
        if (_showSymmetryMenu) DrawSymmetryMenu();
        if (_showToolsMenu) DrawToolsMenu();
        if (_showAutosaveMenu) DrawAutosaveMenu();
        if (_showCustomizationMenu) DrawCustomizationMenu();

        GUI.depth = previousDepth;
    }

    private bool AnyMenuOpen()
    {
        return _showFileMenu || _showEditMenu || _showViewMenu || _showVisibilityMenu || _showSymmetryMenu || _showToolsMenu
            || _showAutosaveMenu || _showCustomizationMenu;
    }

    private void DrawMenuClickCatcher()
    {
        var e = Event.current;
        if (e == null)
            return;
        if (e.type == EventType.Used)
            return;

        if ((e.type == EventType.MouseDown || e.type == EventType.MouseUp) && !IsMouseOverOpenMenu(e.mousePosition) && e.mousePosition.y > TitleBarHeight)
        {
            CloseAllMenus();
            e.Use();
        }
    }

    private void CloseAllMenus()
    {
        _showFileMenu = false;
        _showEditMenu = false;
        _showViewMenu = false;
        _showVisibilityMenu = false;
        _showSymmetryMenu = false;
        _showToolsMenu = false;
        _showAutosaveMenu = false;
        _showCustomizationMenu = false;
        _openMenuRect = Rect.zero;
    }

    private void DrawFileMenu()
    {
        DrawMenuPanel(_fileMenuRect.x, TitleBarHeight, 210,
#if UNITY_EDITOR
            12,
#else
            13, // +1 for "Exit" below, Build-only — Application.Quit() is a documented no-op in the Editor.
#endif
            () =>
        {
            if (GUILayout.Button("New Terrain", _dropdownItemStyle)) { RequestNewTerrain(); _showFileMenu = false; }
            if (GUILayout.Button("Import Map...", _dropdownItemStyle)) { OpenMapDocumentImportPanel(); _showFileMenu = false; }
            if (GUILayout.Button("Import terrain.bdf", _dropdownItemStyle)) { OpenTerrainBdfImport(); _showFileMenu = false; }
            if (GUILayout.Button("Import map objects", _dropdownItemStyle)) { OpenMapObjectsImport(); _showFileMenu = false; }
            if (GUILayout.Button("Import decals.bdf", _dropdownItemStyle)) { OpenDecalsBdfImport(); _showFileMenu = false; }
            if (GUILayout.Button("Export .scd", _dropdownItemStyle)) { OpenScdExportWindow(); _showFileMenu = false; }
            if (GUILayout.Button("Save Terrain (Ctrl+S)", _dropdownItemStyle)) { SaveActiveTerrain(); _showFileMenu = false; }
            if (GUILayout.Button("Load Terrain...", _dropdownItemStyle)) { OpenLoadTerrainMenu(); _showFileMenu = false; }
            if (GUILayout.Button("Delete Saved Terrain...", _dropdownItemStyle)) { OpenDeleteTerrainMenu(); _showFileMenu = false; }
            if (GUILayout.Button("Restore Backup...", _dropdownItemStyle)) { OpenRestoreBackupMenu(); _showFileMenu = false; }
            if (GUILayout.Button("Diagnostics", _dropdownItemStyle)) { OpenBatchImportDiagnostics(); _showFileMenu = false; }
            if (GUILayout.Button("Show Game View UI", _dropdownItemStyle)) { OpenGameViewUiBootstrap(); _showFileMenu = false; }
#if !UNITY_EDITOR
            // Application.Quit() fires wantsToQuit (HandleWantsToQuit) the
            // same as the OS window's own close button/Alt+F4, so this
            // reuses the exact same "save changes?" prompt, not a second one.
            if (GUILayout.Button("Exit", _dropdownItemStyle)) { _showFileMenu = false; Application.Quit(); }
#endif
        });
    }

    private void DrawEditMenu()
    {
        // Move/Rotate/Scale tool-switch entries removed — per direct
        // instruction, G is no longer a hotkey at all (dragging an
        // already-selected prop is unconditional default behavior now, no
        // "Move tool" to switch into) and Rotate/Scale are no longer
        // persistent tool-switch modes — R/S now each arm a one-shot
        // interactive gesture on the current Props selection directly
        // (SC2ObjectsShell.OnGameViewGUI's own R/S handling), not a
        // menu-clickable mode toggle.
        DrawMenuPanel(_editMenuRect.x, TitleBarHeight, 190, 4, () =>
        {
            if (GUILayout.Button("Undo (Ctrl+Z)", _dropdownItemStyle)) { UndoTerrainEdit(); _showEditMenu = false; }
            if (GUILayout.Button("Redo (Ctrl+Y)", _dropdownItemStyle)) { RedoTerrainEdit(); _showEditMenu = false; }
            if (GUILayout.Button("Select (LMB)", _dropdownItemStyle)) { SetTool("Select"); _showEditMenu = false; }
            if (GUILayout.Button("Measure (M)", _dropdownItemStyle)) { ToggleMeasureTool(); _showEditMenu = false; }
        });
    }

    private void DrawViewMenu()
    {
        // Grouped into 4 sections (Panels / Hotkeys / Camera / Display) with
        // a section-label row before each, instead of one flat list — the
        // two real "go do a thing" buttons (Edit Hotkeys…, Reset View) sit
        // right next to the toggle/setting they act on, and render with
        // _dropdownAccentButtonStyle (a solid accent fill, same as an active
        // toggle pill) rather than _dropdownItemStyle's fully-transparent
        // ghost-button background — which reads as plain unclickable text
        // next to the solid toggles right beside it.
        //
        // Measure Tool / Map Frame / Reference Image moved out entirely — they
        // already live in the Reference Panel (DrawReferencePanel), and having
        // them here too was a second, easy-to-miss copy of the same controls.
        // Autosave moved to its own top-level "Backup" menu (DrawAutosaveMenu);
        // this one only ever needed to show/hide FROM the View menu, not live
        // permanently alongside window-chrome toggles.
        //
        // 4 MenuItemHeight-based rows (3 Panels toggles + Hotkeys toggle) + a
        // fixed per-scale budget for everything else whose real height isn't
        // MenuItemHeight-based (section labels, divider blocks, hint lines,
        // the Edit Hotkeys/Reset View buttons, the Zoom Speed/UI Scale slider
        // rows) — scaled by Scale too so it keeps pace with how big those rows
        // actually render now; see MenuItemHeight's own doc comment for why
        // this class of bug exists at all. Deliberately generous (this only
        // sizes the background card + hit-test rect, never clips GUILayout
        // content) rather than exactly tuned.
        var contentHeight = 4 * MenuItemHeight + 260f * SC2UiScale.Factor;
        DrawMenuPanel(_viewMenuRect.x, TitleBarHeight, 190, contentHeight, () =>
        {
            GUILayout.Label("PANELS", _dropdownSectionLabelStyle);
            ShowPanel = GUILayout.Toggle(ShowPanel, "Inspector Panel", _dropdownToggleStyle);
            ShowReferencePanel = GUILayout.Toggle(ShowReferencePanel, "Reference Panel", _dropdownToggleStyle);
            EnableGameViewNavigation = GUILayout.Toggle(EnableGameViewNavigation, "Game Navigation", _dropdownToggleStyle);

            GUILayout.Space(6);
            SC2EditorStylesDivider_Horizontal();
            GUILayout.Space(4);

            GUILayout.Label("HOTKEYS", _dropdownSectionLabelStyle);
            EnableHotkeys = GUILayout.Toggle(EnableHotkeys, "Hotkeys enabled", _dropdownToggleStyle);
            // A real button (GUI.skin.button — the same ghost-pill style
            // "Import/Export Heightmap..." use elsewhere), not
            // _dropdownItemStyle's fully-transparent list-row background —
            // this is a "go do a thing now" action, not a list row, and needs
            // to visibly read as clickable next to the toggle above it.
            if (GUILayout.Button("Edit Hotkeys…", GUILayout.Height(SC2UiScale.Pxf(26f)))) { SC2HotkeysPanel.Open(); _showViewMenu = false; }

            GUILayout.Space(6);
            SC2EditorStylesDivider_Horizontal();
            GUILayout.Space(4);

            GUILayout.Label("CAMERA", _dropdownSectionLabelStyle);
            if (GUILayout.Button("Reset View (Home)", GUILayout.Height(SC2UiScale.Pxf(26f)))) { ResetView(); _showViewMenu = false; }
            GUILayout.Space(2);
            // _miniTextStyle (TextPrimary), not _secondaryTextStyle: this sits on
            // the dropdown card's Surface-colored background, where
            // ColorTextSecondary doesn't reach a readable contrast ratio (same
            // "grey on grey" issue as the tab pills).
            GUILayout.Label("MMB: rotate camera", _miniTextStyle);
            GUILayout.Label("Shift+MMB: move over map", _miniTextStyle);
            GUILayout.Label("Wheel: zoom", _miniTextStyle);
#if !UNITY_EDITOR
            GUILayout.Label("F11: toggle fullscreen", _miniTextStyle);
#endif
            GUILayout.Label($"Zoom Speed: {ZoomSpeed:0.0}x", _miniTextStyle);
            ZoomSpeed = GUILayout.HorizontalSlider(ZoomSpeed, 0.2f, 3f);

            GUILayout.Space(6);
            SC2EditorStylesDivider_Horizontal();
            GUILayout.Space(4);

            GUILayout.Label("DISPLAY", _dropdownSectionLabelStyle);
            // UI Scale (fonts/buttons up to ~30% bigger, left panel up to
            // ~40% wider) is a live, persisted parameter (View
            // menu, same row shape as Zoom Speed above) rather than a
            // one-time hardcoded bump. Drives SC2UnifiedSkin/SC2EnvFieldGui/
            // this class's own chrome styles' font sizes and paddings, plus
            // the panel-width formula in UpdateLayoutRects — see
            // SC2UiScale's own doc comment for why this is real layout
            // numbers, not a GUI.matrix transform.
            GUILayout.Label($"UI Scale: {SC2UiScale.Scale:0.00}x", _miniTextStyle);
            SC2UiScale.Scale = GUILayout.HorizontalSlider(SC2UiScale.Scale, SC2UiScale.Min, SC2UiScale.Max);
        });
    }

    /// <summary>Split out of the View menu (which had grown a permanent
    /// "AUTOSAVE" section alongside window-chrome toggles) into its own
    /// top-level menu, matching Autosave's own weight as a save-behavior
    /// setting rather than a view preference. Labeled "Backup" (not
    /// "Autosave") since it covers both the autosave timer and how many
    /// backup snapshots it keeps, not just the timer itself.</summary>
    private void DrawAutosaveMenu()
    {
        var contentHeight = 1 * MenuItemHeight + 70f * SC2UiScale.Factor;
#if !UNITY_EDITOR
        // + the Build-only "Backups to keep" field and "Write legacy JSON copy" switch.
        contentHeight += 16f * SC2UiScale.Factor + MenuItemHeight * 2f;
#endif
        DrawMenuPanel(_autosaveMenuRect.x, TitleBarHeight, 190, contentHeight, () =>
        {
            GUILayout.Label("BACKUP", _dropdownSectionLabelStyle);
            var autosaveEnabled = GUILayout.Toggle(SC2SaveSettings.AutosaveEnabled, "Autosave enabled", _dropdownToggleStyle);
            if (autosaveEnabled != SC2SaveSettings.AutosaveEnabled) SC2SaveSettings.AutosaveEnabled = autosaveEnabled;
            GUILayout.Label($"Every {SC2SaveSettings.AutosaveIntervalMinutes:0} min", _miniTextStyle);
            var nextAutosaveMinutes = Mathf.Round(GUILayout.HorizontalSlider(SC2SaveSettings.AutosaveIntervalMinutes, 1f, 60f));
            if (!Mathf.Approximately(nextAutosaveMinutes, SC2SaveSettings.AutosaveIntervalMinutes)) SC2SaveSettings.AutosaveIntervalMinutes = nextAutosaveMinutes;
#if !UNITY_EDITOR
            // Backups only exist against the real disk persistence layer
            // (SC2RuntimeTerrainPersistence) — same Build-only scope as
            // Load/Delete Saved Terrain in the File menu.
            SC2SaveSettings.MaxBackupsPerMap = Mathf.Max(0, SC2EnvFieldGui.IntField("Backups to keep", SC2SaveSettings.MaxBackupsPerMap, "MaxBackupsPerMap"));
            // The whole-map JSON copy is the fallback when the document cannot be read, and the biggest file of a save.
            var writeLegacyJson = GUILayout.Toggle(SC2SaveSettings.WriteLegacyJson, "Also save a fallback copy (.json)", _dropdownToggleStyle);
            if (writeLegacyJson != SC2SaveSettings.WriteLegacyJson) SC2SaveSettings.WriteLegacyJson = writeLegacyJson;
#endif
        });
    }

    /// <summary>User-level appearance settings that are NOT part of the map
    /// document — persisted per-install (SC2CustomizationSettings, the same
    /// PlayerPrefs-backed pattern SC2SaveSettings already uses), so picking a
    /// decal marker color here follows the mapmaker across every map they
    /// open, instead of being baked into the .sc2terrain file itself.</summary>
    private void DrawCustomizationMenu()
    {
        var contentHeight = 1 * MenuItemHeight + 100f * SC2UiScale.Factor;
        DrawMenuPanel(_customizationMenuRect.x, TitleBarHeight, 210, contentHeight, () =>
        {
            GUILayout.Label("INTERFACE", _dropdownSectionLabelStyle);
            if (GUILayout.Button("Interface Colors…", GUILayout.Height(SC2UiScale.Pxf(26f))))
            {
                SC2ThemePanel.Open();
                _showCustomizationMenu = false;
            }
            GUILayout.Label("DECALS", _dropdownSectionLabelStyle);
            var nextColor = EditorGUILayout.ColorField("Marker Color", SC2CustomizationSettings.DecalMarkerColor);
            if (nextColor != SC2CustomizationSettings.DecalMarkerColor) SC2CustomizationSettings.DecalMarkerColor = nextColor;
            if (GUILayout.Button("Reset to Default", GUILayout.Height(SC2UiScale.Pxf(22f))))
                SC2CustomizationSettings.DecalMarkerColor = SC2CustomizationSettings.DefaultDecalMarkerColor;
        });
    }

    private void DrawVisibilityMenu()
    {
        // 2 Path rows (Land/Air) or 4 (+Naval/Amphibious) — same
        // Data.PlayableWater signal the Paths tab itself already uses to
        // decide the required graph set (SC2ObjectsShell.RequiredGraphKinds)
        // — a map with no playable water never has Naval/Amphibious graphs
        // to begin with, so showing those 2 extra toggles there would just
        // be dead controls.
        var playableWater = _objectsShell != null && _objectsShell.PlayableWater;
        // FIXED: was "11 + ..." — undercounted by 2, which shrank this
        // menu's computed hit-test rect (_openMenuRect, set from this
        // itemCount) below its actual rendered content height. The dropdown
        // still PAINTED every row correctly (GUILayout draws whatever
        // content this closure produces regardless of the rect passed to
        // DrawMenuPanel), but the last rows fell outside the rect used for
        // input/overlap hit-testing, contributing to clicks near the bottom
        // of this now much-taller (11->15) menu reaching whatever panel
        // content sat underneath instead of this dropdown's own toggles.
        // FIXED: was "15 + ..." — undercounted by 1 even before Decals was added below (Wireframe
        // was never included in the count), the same hit-test-rect-too-short bug class as the
        // "was 11 + ..." fix above.
        var itemCount = 17 + (playableWater ? 2 : 0); // Terrain/Hfield/Costs/Collision2 (4) + Props/Markers/Units/ReferenceUnits (4) + Land/Air (2) + Chains/Areas/Decals/Water/RNM/Grid/Wireframe (7) [+ Naval/Amphibious]

        DrawMenuPanel(_visibilityMenuRect.x, TitleBarHeight, 210, itemCount, () =>
        {
            DrawToggle("Terrain", ref OverlayTerrain);
            DrawToggle("Hfield", ref OverlayHfield);
            DrawToggle("Costs", ref OverlayCosts);
            DrawToggle("Collision2", ref OverlayCollision2);
            DrawToggle("Props", ref OverlayProps);
            DrawToggle("Markers", ref OverlayMarkers);
            DrawToggle("Units", ref OverlayUnits);
            DrawToggle("Reference Units", ref OverlayReferenceUnits);
            DrawToggle("Path: Land", ref OverlayGraphLand);
            DrawToggle("Path: Air", ref OverlayGraphAir);
            if (playableWater)
            {
                DrawToggle("Path: Naval", ref OverlayGraphNaval);
                DrawToggle("Path: Amphibious", ref OverlayGraphAmphibious);
            }
            DrawToggle("Chains", ref OverlayChains);
            DrawToggle("Areas", ref OverlayAreas);
            DrawToggle("Decals", ref OverlayDecals);
            DrawToggle("Water", ref OverlayWater);
            DrawToggle("RNM", ref OverlayRNM);
            DrawToggle("Grid", ref OverlayGrid);
            DrawToggle("Wireframe", ref OverlayWireframe);
        });
        // CommitOverlayToContext was never called anywhere - SyncOverlayFromContext
        // (runs at the very top of every OnGUI, before this menu is drawn) kept
        // overwriting a just-clicked local OverlayX field back to SharedContext's
        // own never-updated copy on the very next frame, so every checkbox visually
        // snapped back and no toggle's downstream effect (SetTerrainOverlayVisible,
        // HandleCostsGameViewInput's cross-tab branch, etc.) ever stuck. Commit here,
        // every frame this menu is open, so a click survives past the next repaint.
        CommitOverlayToContext();
    }

    private void DrawToolsMenu()
    {
        // 220, not the 260 this briefly needed for a since-removed
        // "(Editor only)" label suffix - the extra width pushed this card's
        // right edge further into the subtabs row than either label
        // actually needs, matching every other 190-220px dropdown here.
        DrawMenuPanel(_toolsMenuRect.x, TitleBarHeight, 220, 2, () =>
        {
            if (GUILayout.Button("Focus Selection", _dropdownItemStyle)) { FocusCamera(); _showToolsMenu = false; }
            // Unlike the reflection-based InvokeEditorStaticMethod calls
            // elsewhere in this menu, SC2InfoBdfEditorPanel is a plain
            // GUILayout overlay (not a real EditorWindow), so it's called
            // directly and works identically in the Editor Game view and a
            // real Build & Run player.
            if (GUILayout.Button("Info.bdf Editor...", _dropdownItemStyle))
            {
                OpenInfoBdfEditorPanel();
                _showToolsMenu = false;
            }
        });
    }

    /// <summary>Symmetry axis/mode/center is a cross-cutting setting shared
    /// by Terrain, Costs, and Objects (all three read Data.Symmetry/
    /// SymmetryCenter/RadialCount) — this menu lives next to Visibility
    /// rather than inside the Terrain tab for the same reason Visibility
    /// itself does: it's not a Terrain-only concern. Reads/writes
    /// _terrainShell.Data directly (the same SC2TerrainData every domain
    /// shell shares) rather than duplicating a second copy of the setting.</summary>
    private void DrawSymmetryMenu()
    {
        var data = _terrainShell?.Data;
        var contentHeight = data == null ? MenuItemHeight : 8 * MenuItemHeight + (data.Symmetry == SymmetryMode.Radial ? MenuItemHeight : 0f);
        DrawMenuPanel(_symmetryMenuRect.x, TitleBarHeight, 210, contentHeight, () =>
        {
            if (data == null)
            {
                GUILayout.Label("No terrain loaded", _dropdownItemStyle);
                return;
            }

            DrawSymmetryModeToggle(data, SymmetryMode.None, "None");
            DrawSymmetryModeToggle(data, SymmetryMode.X, "X");
            DrawSymmetryModeToggle(data, SymmetryMode.Y, "Y");
            DrawSymmetryModeToggle(data, SymmetryMode.XY, "XY");
            DrawSymmetryModeToggle(data, SymmetryMode.DiagonalTLBR, "Diagonal (TL-BR)");
            DrawSymmetryModeToggle(data, SymmetryMode.DiagonalTRBL, "Diagonal (TR-BL)");
            DrawSymmetryModeToggle(data, SymmetryMode.DiagonalBoth, "Diagonal (both)");
            DrawSymmetryModeToggle(data, SymmetryMode.Radial, "Radial");

            if (data.Symmetry == SymmetryMode.Radial)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Count", _miniTextStyle, GUILayout.Width(50));
                var newCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(data.RadialCount, 2, 8));
                GUILayout.Label(newCount.ToString(), _miniTextStyle, GUILayout.Width(20));
                GUILayout.EndHorizontal();
                if (newCount != data.RadialCount)
                {
                    _terrainShell.RecordUndoCheckpoint("Change Symmetry");
                    data.RadialCount = newCount;
                    EditorUtility.SetDirty(data);
                }
            }
        });
    }

    private void DrawSymmetryModeToggle(SC2TerrainData data, SymmetryMode mode, string label)
    {
        var isOn = data.Symmetry == mode;
        var newOn = GUILayout.Toggle(isOn, label, _dropdownToggleStyle);
        if (newOn && !isOn)
        {
            _terrainShell.RecordUndoCheckpoint("Change Symmetry");
            data.Symmetry = mode;
            EditorUtility.SetDirty(data);
        }
    }

    private static void SC2EditorStylesDivider_Horizontal()
    {
        var r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
        DrawSolidRect(r, ColorDivider, null);
    }

    // Scaled through SC2UiScale, not fixed constants: if
    // _dropdownItemStyle/_dropdownToggleStyle's own rendered row height grows
    // with UI Scale (EnsureStyles above) but this stays fixed, every dropdown
    // menu's computed card/hit-test rect (_openMenuRect, built from these two
    // values — see DrawMenuPanel below) undersizes itself relative to the
    // taller real content, pushing later rows outside the clickable area —
    // the same failure shape DrawVisibilityMenu's own "FIXED: was
    // undercounted by 2" comment already documents for a different instance
    // of this same bug class.
    private static float MenuItemHeight => SC2UiScale.Pxf(24f);   // dropdown row incl. margin
    private static float MenuChromeHeight => SC2UiScale.Pxf(18f); // card padding top+bottom

    /// <summary>
    /// Dropdown card sized to its content. The old fixed 260px height left a big
    /// empty card under short menus (File has 4 rows) that both looked broken and
    /// swallowed clicks meant for the viewport below it.
    /// </summary>
    /// <summary>Actual rect of the currently open dropdown, recorded by DrawMenuPanel.</summary>
    private Rect _openMenuRect;

    private void DrawMenuPanel(float x, float y, float width, float contentHeight, System.Action drawContent)
    {
        // width scaled here, once, so every Draw*Menu call site's literal
        // width (190/210/220...) gets more room for bigger text at higher
        // UI Scale without each one needing its own edit.
        var rect = new Rect(x, y + 4, width * SC2UiScale.Factor, contentHeight + MenuChromeHeight);
        _openMenuRect = rect;
        DrawCard(rect, _cardStyle, drawContent);
    }

    private void DrawMenuPanel(float x, float y, float width, int itemCount, System.Action drawContent)
    {
        DrawMenuPanel(x, y, width, itemCount * MenuItemHeight, drawContent);
    }

    private void DrawSubtabs(int width)
    {
        var mainTabId = GameMainTabIds[_mainTab];
        // Costs' sub-tabs aren't a fixed list — WS1: one tab per real Costs
        // block (3 land-only or 5 playable-water, matching Data.PlayableWater)
        // plus a trailing "Terrain Types" tab, so the count/labels vary with
        // the loaded map. EnsureCostsShell() first so _costsShell exists even
        // on the very first frame Costs becomes the active main tab (this
        // runs before HandleCostsGameViewInput, which normally ensures it).
        string[] tabs;
        if (mainTabId == SC2EditorMainTab.Costs)
        {
            EnsureCostsShell();
            tabs = _costsShell.GetSubtabLabels();
        }
        else
        {
            tabs = SC2EditorTabs.GetSubtabs(mainTabId);
        }
        _subTab = Mathf.Clamp(_subTab, 0, tabs.Length - 1);
        var rect = new Rect(0, TitleBarHeight + TabsRowHeight, width, SubtabsRowHeight);
        DrawSolidRect(rect, PanelColor, _dividerTexture);

        // Same reasoning as the main-tabs row in DrawHeader: an open dropdown's
        // card commonly extends down far enough to cover this whole row, so its
        // buttons must not be able to steal clicks meant for the dropdown.
        var previousEnabledSubtabs = GUI.enabled;
        GUI.enabled = previousEnabledSubtabs && !_menuOverlayBlocksInput;
        GUILayout.BeginArea(rect, _headerStyle);
        var usableWidth = Mathf.Max(1f, width - SC2UiScale.Pxf(20f));

        // Decide full-label vs. compact layout from the *actual* space the full
        // labels need, not a fixed screen-width guess. The old "width < 760"
        // heuristic only checked the viewport size, not how many/long the
        // subtabs for the current main tab are (e.g. Costs has 6 tabs incl.
        // "Islands/Validation"), so full-width buttons could sum to more than
        // usableWidth and run off the right edge of the screen.
        //
        // The "9px/char + 28px padding" estimate below assumes the BASE
        // (Scale=1) font metrics — scaled by SC2UiScale.Scale here (both
        // this estimate and the clamp bounds) since _subtabStyle's own
        // fontSize now scales too (EnsureStyles); otherwise at higher UI
        // Scale this row's real text no longer fits the widths this
        // heuristic computes for it, visually crowding/clipping the sub-tab
        // labels.
        var fullWidthSum = 0f;
        for (var i = 0; i < tabs.Length; i++)
            fullWidthSum += Mathf.Clamp(tabs[i].Length * SC2UiScale.Pxf(9f) + SC2UiScale.Pxf(28f), SC2UiScale.Pxf(78f), SC2UiScale.Pxf(148f)) + 4f; // +margin (2+2)
        var compactTabs = tabs.Length > 0 && fullWidthSum > usableWidth;

        if (compactTabs)
        {
            // Neither an equal-share divide (every tab gets
            // usableWidth/tabCount, however tiny) nor a fixed-minimum-width
            // floor (which just overflows instead) stays legible once a tab
            // list gets long enough (Environment has 16 sub-tabs after the
            // InfoBdf field remap). Every compact tab instead gets one
            // fixed, always-legible width
            // regardless of tab count, laid out in a horizontally
            // SCROLLABLE strip instead of forced into the row's own width —
            // when everything fits, the scrollbar simply never appears
            // (GUI.BeginScrollView already degrades to that automatically);
            // when it doesn't, the user scrolls instead of reading clipped
            // mid-word fragments.
            // Widened from 58f: several real tab labels (Terrain's own
            // "Geometry"/"Imported", not just Environment's long ones
            // CompactSubtabLabel already targets) clipped mid-word inside
            // the narrower fixed width once TextClipping.Clip kicked in,
            // since 58px was only ever sized against the short abbreviations
            // already in that switch, never against Terrain's own subtab
            // list.
            var compactTabWidth = SC2UiScale.Pxf(72f);
            var contentWidth = Mathf.Max(usableWidth, compactTabWidth * tabs.Length);
            var scrollViewRect = new Rect(0f, 0f, usableWidth, rect.height);
            var contentRect = new Rect(0f, 0f, contentWidth, rect.height);
            // A stray vertical scrollbar sliver can show up here, though this
            // row never scrolls vertically by design (contentRect's height
            // always equals scrollViewRect's).
            // Classic GUI.BeginScrollView gotcha: once the horizontal
            // scrollbar is shown, it eats some height from the actual
            // viewport, so content sized to the ORIGINAL (taller) viewport
            // now technically "overflows" by the scrollbar's own thickness —
            // Unity then shows a vertical scrollbar too, despite
            // alwaysShowVertical:false, since that flag only controls
            // whether it shows when NOT needed, not whether it can appear
            // when Unity's own internal math thinks it is. Passing
            // GUIStyle.none for the vertical scrollbar's style makes it
            // draw as nothing regardless, which is what's actually wanted
            // here — this row only ever needs horizontal scrolling.
            _subtabsScroll = GUI.BeginScrollView(scrollViewRect, _subtabsScroll, contentRect, false, false, GUI.skin.horizontalScrollbar, GUIStyle.none);
            for (var i = 0; i < tabs.Length; i++)
            {
                var style = i == _subTab ? _activeSubtabStyle : _subtabStyle;
                var btnRect = new Rect(i * compactTabWidth + 2f, 0f, compactTabWidth - 4f, rect.height);
                // A label wider than its button is cut with "..." instead of being clipped on both sides of the centre.
                var label = SC2StartupUi.Fit(CompactSubtabLabel(tabs[i]), style, btnRect.width - style.padding.horizontal);
                if (GUI.Button(btnRect, label, style))
                    _subTab = i;
                if (i == _subTab) DrawSubtabLine(btnRect);
            }
            GUI.EndScrollView();
        }
        else
        {
            GUILayout.BeginHorizontal();
            for (var i = 0; i < tabs.Length; i++)
            {
                var style = i == _subTab ? _activeSubtabStyle : _subtabStyle;
                var requestedWidth = Mathf.Clamp(tabs[i].Length * SC2UiScale.Pxf(9f) + SC2UiScale.Pxf(28f), SC2UiScale.Pxf(78f), SC2UiScale.Pxf(148f));
                var fitted = new GUIContent(SC2StartupUi.Fit(tabs[i], style, requestedWidth - style.padding.horizontal), tabs[i]);
                if (GUILayout.Button(fitted, style, GUILayout.Width(requestedWidth)))
                    _subTab = i;
                if (i == _subTab && Event.current.type == EventType.Repaint) DrawSubtabLine(GUILayoutUtility.GetLastRect());
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        GUILayout.EndArea();
        GUI.enabled = previousEnabledSubtabs;

        var dividerRect = new Rect(0, rect.yMax - 1, width, 1);
        DrawSolidRect(dividerRect, ColorDivider, _dividerTexture);

        // Consume events from this area to prevent SceneView navigation.
        // Gated on !_menuOverlayBlocksInput: this is a raw Event.Use(), not a
        // GUI.Button, so the GUI.enabled guard above (which correctly disables
        // the tab buttons themselves while a dropdown is open) has no effect
        // on it. An open dropdown's card commonly extends down over this exact
        // row, and without this gate every click meant for the dropdown was
        // being eaten here before DrawOpenMenus (drawn later) ever saw it —
        // root cause of "Tools/Symmetry menu items don't respond to clicks."
        var e = Event.current;
        if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition) && !_menuOverlayBlocksInput)
            e.Use();
    }

    private static string CompactSubtabLabel(string label)
    {
        switch (label)
        {
            // Terrain's own tabs (SC2EditorTabs.TerrainTabs) — "Geometry"
            // and "Imported" had no case at all (fell through to the
            // default below, which only truncates labels over 10 chars —
            // both are exactly 8, so they were returned verbatim and
            // clipped mid-word by the compact button's fixed width).
            // "Lighting Bake" did hit the default truncation but
            // "Lighting…" is still too wide for the compact box.
            case "Geometry":
                return "Geom.";
            case "Imported":
                return "Import";
            case "Lighting Bake":
                return "Light.";
            case "Materials":
                return "Mats";
            case "Simplified":
                return "Simpl.";
            case "Auto Generate":
                return "Auto";
            case "Paint Overrides":
                return "Paint";
            case "Paint Pairs":
                return "Pairs";
            case "Paint Blend":
                return "Blend";
            case "Background":
                return "Bkgnd.";
            case "Terrain Types":
                return "Types";
            case "Islands/Validation":
                return "Valid.";
            // CostsTabs' "Unit Layer" and CollisionTabs' "Collision2" are
            // both exactly 10 characters — the default case's old
            // "> 10" check let both through verbatim (same class of bug
            // as Geometry/Imported above, just at a different boundary).
            case "Unit Layer":
                return "Units";
            case "Collision2":
                return "Coll2";
            // EnvironmentTabs' long multi-word names — the original
            // motivating case for this whole compact-mode redesign
            // (16 subtabs), but never actually checked against the NEW
            // fixed-width box; the old ">9 chars" default truncation
            // ("Depth of…", "Render Pa…") is still too wide for it.
            case "Point Clouds":
                return "P.Cloud";
            case "Fog of War":
                return "FogWar";
            case "Depth of Field":
                return "DoField";
            case "Render Passes":
                return "R.Pass";
            case "Air Elevation":
                return "AirElev";
            // Real vanilla Costs block names (SC2CostsShell.GetSubtabLabels) —
            // long enough (up to 35 chars, e.g. "Land_Small_water_Surface_ShoreLine")
            // to overflow a compact sub-tab button unabbreviated, which is
            // exactly what happened before these cases existed (the "default"
            // case below returned the name verbatim).
            case "Land_Small":
                return "Small";
            case "Land_Medium":
                return "Medium";
            case "Land_Large":
                return "Large";
            case "Land_Small_water_Surface_ShoreLine":
                return "Sm. Shore";
            case "Land_medium":
                return "Medium";
            case "Land_Large_water_Abyss_ShoreLine":
                return "Lg. Abyss";
            case "Land_Large_water_Seafloor":
                return "Lg. Seafloor";
            case "Land_Medium_Water_Surface":
                return "Med. Surface";
            default:
                // Generic fallback for any other unrecognized label (a
                // renamed/custom Costs block, etc.) — truncate rather than
                // let it overflow; _subtabStyle's own Clip setting is a
                // second safety net for whatever this doesn't catch. Was
                // "> 10 chars, keep 9" — sized against the OLD variable-
                // width (78-148px) layout, not the compact mode's fixed,
                // much narrower box: even short-ish 8-char names like
                // "Geometry"/"Imported" clipped mid-word once returned
                // verbatim here. 6 chars is a safer budget
                // for the compact box at any UI Scale, since both the box
                // and the font scale together — the ratio between them
                // never changes with Scale, only with the label itself.
                return label.Length > 6 ? label.Substring(0, 5) + "…" : label;
        }
    }

    private void DrawCard(Rect rect, GUIStyle style, System.Action drawContent)
    {
        var shadowRect = new Rect(rect.x - 2, rect.y + 3, rect.width + 4, rect.height + 4);
        GUI.Box(shadowRect, GUIContent.none, _shadowStyle);
        GUILayout.BeginArea(rect, style);
        drawContent();
        GUILayout.EndArea();
    }

    private void DrawMainPanel(int width, int height)
    {
        DrawSolidRect(_panelRect, PanelColor, _dividerTexture);
        DrawSolidRect(new Rect(_panelRect.xMax - 1f, _panelRect.y, 1f, _panelRect.height), ColorDivider, _dividerTexture);

        // The area is the whole panel, with no padding of its own: the scrollbar of the one scroll view below has to sit flush
        // against the panel's right edge, which a padded area would keep it from reaching.
        GUILayout.BeginArea(_panelRect);
        // What a tab wants to keep in view while its content scrolls (Terrain's preview toolbar) is drawn above the scroll view.
        if (_mainTab == 0) DrawTerrainPinnedHeader();
        // Scrollable: some tabs' content (e.g. Environment's merged Water
        // tab, 6 foldouts that can all be expanded at once) can legitimately
        // exceed the panel's fixed height — previously just clipped/
        // inaccessible below the fold with no way to reach it, since
        // BeginArea alone doesn't scroll. The vertical bar is always shown, so it is in the same place on every tab whether or
        // not the content happens to overflow; the horizontal one never is (nothing in a panel should need it).
        _panelScroll = GUILayout.BeginScrollView(_panelScroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none,
            GUILayout.Width(_panelRect.width), GUILayout.ExpandHeight(true));
        _panelScroll.x = 0f;
        GUILayout.BeginVertical(_panelContentStyle, GUILayout.Width(_panelRect.width - PanelScrollbarWidth));
        {
            switch (_mainTab)
            {
                case 0:
                    DrawTerrainInspector();
                    break;
                case 1:
                    DrawCostsInspector();
                    break;
                case 2:
                    DrawCollisionInspector();
                    break;
                case 3:
                    DrawObjectsInspector();
                    break;
                case 4:
                    DrawDecalsInspector();
                    break;
                case 5:
                    DrawEnvironmentInspector();
                    break;
                case 6:
                    DrawScenarioInspector();
                    break;
                case 7:
                    DrawExportInspector();
                    break;
                case 8:
                    DrawDiagnosticsInspector();
                    break;
                default:
                    GUILayout.Label("Panel is ready for migration from the editor window.", _miniTextStyle);
                    break;
            }
        }
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        _panelScroll.x = 0f;
        GUILayout.EndArea();

        // Child IMGUI controls must receive MouseDown/MouseUp normally.
    }

    /// <summary>The Terrain tab's preview-mode toolbar, kept above the scrolled content so it stays in reach.</summary>
    private void DrawTerrainPinnedHeader()
    {
        EnsureTerrainShell();
        SyncTerrainShellSubtab();
        TryAdoptTerrainData();
        GUILayout.BeginVertical(_panelHeaderStyle, GUILayout.Width(_panelRect.width));
        _terrainShell.DrawEmbeddedHeader(PanelContentWidth);
        GUILayout.EndVertical();
    }

    /// <summary>Compact right-side panel (View menu > "Reference Panel",
    /// default on) — mirrors DrawMainPanel's own Rect/BeginArea/ScrollView/
    /// BeginVertical structure at a much narrower fixed width. Surfaces the
    /// Measure tool (additive to its existing Edit menu / View menu / "M"
    /// hotkey entry points, not a replacement), the reference-image load/
    /// show controls, and the reference-units placement list.</summary>
    private void DrawReferencePanel()
    {
        DrawSolidRect(_referencePanelRect, PanelColor, _dividerTexture);
        DrawSolidRect(new Rect(_referencePanelRect.x, _referencePanelRect.y, 1f, _referencePanelRect.height), ColorDivider, _dividerTexture);

        // The nav gizmo (_navigationGizmoRect) is drawn AFTER this panel so
        // it floats decoratively over the panel's top-left corner — fine for
        // the panel's own static BACKGROUND fill above (still drawn full-
        // height, so the gizmo visually sits "on top of" one continuous
        // column), but the SCROLLABLE content area itself must not overlap
        // it at all: an earlier fix only padded the top of the scrolled
        // CONTENT, which merely delayed the problem — once the user
        // scrolled, later rows still passed underneath the gizmo on their
        // way through that same fixed on-screen strip, since the gizmo's
        // position is fixed in screen space while content scrolls past it.
        // Shrinking the actual BeginArea/ScrollView rect to start below the
        // gizmo's bottom edge means no scroll position can ever bring a row
        // under it, not just the first one.
        var gizmoClearance = Mathf.Max(0f, _navigationGizmoRect.yMax - _referencePanelRect.y) + 10f;
        var contentAreaRect = new Rect(_referencePanelRect.x, _referencePanelRect.y + gizmoClearance,
            _referencePanelRect.width, Mathf.Max(1f, _referencePanelRect.height - gizmoClearance));

        // Same layout as DrawMainPanel: no padding on the area, so the always-shown scrollbar sits flush against the panel's edge.
        GUILayout.BeginArea(contentAreaRect);
        // Horizontal scrollbar explicitly suppressed (GUIStyle.none): this
        // column's own content width is capped to ReferencePanelContentWidth
        // everywhere below (see DrawRightPanelSection's own MaxWidth calls),
        // so any width overflow is a bug to fix at the source, not something
        // to let the user scroll sideways to see.
        _referencePanelScroll = GUILayout.BeginScrollView(_referencePanelScroll, false, true,
            GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none,
            GUILayout.Width(contentAreaRect.width), GUILayout.Height(contentAreaRect.height));
        _referencePanelScroll.x = 0f;
        GUILayout.BeginVertical(_panelContentStyle, GUILayout.Width(contentAreaRect.width - PanelScrollbarWidth));
        {
            var contentWidth = ReferencePanelContentWidth;
            var measureOn = GUILayout.Toggle(_tool == "Measure", "Measure", _dropdownToggleStyle, GUILayout.MaxWidth(contentWidth));
            if (measureOn != (_tool == "Measure")) ToggleMeasureTool();

            GUILayout.Space(6);
            SC2EditorStylesDivider_Horizontal();
            GUILayout.Space(4);

            GUILayout.Label("MAP FRAME", _dropdownSectionLabelStyle, GUILayout.MaxWidth(contentWidth));
            _showMapFrame = GUILayout.Toggle(_showMapFrame, "Show Border", _dropdownToggleStyle, GUILayout.MaxWidth(contentWidth));
            _mapFrameWidth = Mathf.Max(0.01f, SC2EnvFieldGui.FloatField("Width", _mapFrameWidth, "MapFrameWidth"));
            _mapFrameHeight = Mathf.Max(0.01f, SC2EnvFieldGui.FloatField("Height", _mapFrameHeight, "MapFrameHeight"));

            GUILayout.Space(6);
            SC2EditorStylesDivider_Horizontal();
            GUILayout.Space(4);

            GUILayout.Label("REFERENCE IMAGE", _dropdownSectionLabelStyle, GUILayout.MaxWidth(contentWidth));
            if (GUILayout.Button(_referenceOverlayTex != null ? "Change..." : "Load...", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.MaxWidth(contentWidth)))
                LoadReferenceImage();
            var nextFloating = GUILayout.Toggle(_showReferenceImageFloating, "Show in corner", _dropdownToggleStyle, GUILayout.MaxWidth(contentWidth));
            if (nextFloating != _showReferenceImageFloating) _showReferenceImageFloating = nextFloating;
            var nextOnMap = GUILayout.Toggle(_showReferenceOverlay, "Show on map", _dropdownToggleStyle, GUILayout.MaxWidth(contentWidth));
            if (nextOnMap != _showReferenceOverlay)
            {
                _showReferenceOverlay = nextOnMap;
                ApplyReferenceOverlayState();
            }
            DrawReferenceOpacitySlider("ReferenceOpacityPanel");
            if (_referenceOverlayTex != null) DrawReferenceCoverageField(contentWidth);

            GUILayout.Space(6);
            SC2EditorStylesDivider_Horizontal();
            GUILayout.Space(4);

            EnsureReferenceUnitsShell();
            _referenceUnitsShell.DrawRightPanelSection(ReferencePanelContentWidth);
        }
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private Vector2 _referencePanelScroll;
    private bool _showReferenceImageFloating;

    /// <summary>Bottom-right-corner picture-in-picture preview of the SAME
    /// texture the existing terrain-shader reference overlay uses
    /// (_referenceOverlayTex/LoadReferenceImage, untouched) — an
    /// independent second display mode, not a replacement: floats OVER the
    /// viewport (like the nav gizmo) rather than carving out layout space,
    /// so it never shrinks the usable 3D view.</summary>
    private void DrawFloatingReferenceImage()
    {
        if (!_showReferenceImageFloating || _referenceOverlayTex == null) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        var size = Mathf.Min(320f, _viewportRect.width * 0.25f, _viewportRect.height * 0.4f);
        if (size < 32f) return;
        var rect = new Rect(Screen.width - size - 12f, Screen.height - size - 12f, size, size);
        DrawSolidRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), ColorDivider, _dividerTexture);
        GUI.DrawTexture(rect, _referenceOverlayTex, ScaleMode.ScaleToFit);
    }

    private void DrawTerrainInspector()
    {
        EnsureTerrainShell();
        SyncTerrainShellSubtab();
        TryAdoptTerrainData();
        _terrainShell.OnEmbeddedGUI(PanelContentWidth);

        // Propagate data changes made inside the shell (e.g. "Create new
        // SC2TerrainData" or ObjectField pick inside OnEmbeddedGUI) back to
        // the public RuntimeTerrainData field so that the Inspector and
        // TryAdoptTerrainData stay in sync — bidirectional binding.
        if (_terrainShell.Data != RuntimeTerrainData)
            RuntimeTerrainData = _terrainShell.Data;
    }

    private void EnsureTerrainShell()
    {
        if (_terrainShell != null)
            return;

        _terrainShell = ScriptableObject.CreateInstance<SC2TerrainShell>();
        _terrainShell.hideFlags = HideFlags.HideAndDontSave;
        _terrainShell.Session = _mapSession;
        _terrainShell.RepaintHost = RepaintGameView;
        _terrainShell.OnPreviewModeChanged = ApplyPreviewModeToImportedTerrains;
        _terrainShell.DrawImportedTerrainsContent = DrawImportedTerrainsLeftPanelContent;
        _terrainShell.PreSaveSync = SyncImportedTerrainsToDocument;
        _terrainShell.GetImportedTerrainsForExport = GetImportedTerrainsForExport;
        _terrainShell.GetImportedRnmTargets = GetImportedRnmTargets;
        _terrainShell.SetImportedRnmTarget = SetImportedRnmTarget;
        _terrainShell.RefreshImportedRnmMasks = RefreshImportedRnmMasks;
        // Same colliders _objectsShell already gets (below), shared via
        // BuildImportedTerrainColliders so SC2CostsShell (which holds a
        // reference to this _terrainShell, not to this class) can raycast
        // the real imported surface too.
        _terrainShell.GetImportedTerrainColliders = BuildImportedTerrainColliders;
        // A map load/switch rebuilds Runtime's own shared chunk material
        // from scratch, resetting the reference-overlay shader properties
        // to their defaults — re-push whatever this class's own fields
        // currently say right after, so the overlay survives switching maps
        // instead of silently reverting to off.
        _terrainShell.DataChanged += _ => ApplyReferenceOverlayState();
        // Prop/unit blueprint indexing (and the Objects document sync just
        // below it) must not depend on the Objects tab having been opened
        // first — EnsureObjectsShell is otherwise only called from Objects-
        // tab-specific code paths, so on a session where the mapmaker never
        // visits that tab, _objectsShell (and its DataChanged subscriptions)
        // never even exists. This EnsureTerrainShell method, by contrast, is
        // the one bootstrap point every tab's own EnsureXShell chain already
        // funnels through — call it here, BEFORE the SetData call below
        // (which fires DataChanged for this method's own startup load), so
        // the subscription is already registered in time to catch it, not
        // just for later loads.
        EnsureObjectsShell();
#if UNITY_EDITOR

        // In the Editor, keep supporting an explicitly assigned RuntimeTerrainData
        // field for quick testing and asset selection.
        var runtimeDataWasAssigned = RuntimeTerrainData != null;
        if (RuntimeTerrainData == null)
            RuntimeTerrainData = LoadSharedTerrainData();
        if (RuntimeTerrainData != null)
            _terrainShell.SetData(RuntimeTerrainData,
                runtimeDataWasAssigned
                    ? RuntimeFieldTerrainSourceLabel(RuntimeTerrainData)
                    : SharedTerrainSourceLabel(RuntimeTerrainData));
#else
        // In a standalone build, do not trust the serialized scene/inspector
        // RuntimeTerrainData reference as startup data. It can silently bake an
        // old ScriptableObject into the player and survive deleting LocalLow or
        // Resources from the project after the fact. Startup data must come from
        // Resources; runtime JSON is only adopted through explicit LoadFromDisk.
        RuntimeTerrainData = LoadSharedTerrainData();
        if (RuntimeTerrainData != null)
            _terrainShell.SetData(RuntimeTerrainData, SharedTerrainSourceLabel(RuntimeTerrainData));
#endif
    }

    private void SyncTerrainShellSubtab()
    {
        if (_terrainShell == null)
            return;

        var mode = TerrainModeFromSubtab(_subTab);
        if (_terrainShellLastSubtab != _subTab || _terrainShell.Mode != mode)
        {
            _terrainShell.SetMode(mode);
            _terrainShellLastSubtab = _subTab;
        }
    }

    /// <summary>Paint Pairs/Paint Blend used to be two separate top-level
    /// tabs (subtab 3 and 4) mapping to the same EditorMode.Paint, with this
    /// caller forcing SC2PaintMode's own Assign/RNM sub-mode based on which
    /// one was selected — genuinely redundant with SC2PaintMode.DrawPanel's
    /// own in-panel Assign/Blend segmented toggle, which already let the
    /// user flip between the exact same two sub-modes from either tab. Now
    /// merged into one "Paint Materials" tab (SC2EditorTabs.TerrainTabs);
    /// the in-panel toggle is the only way to switch sub-modes, so there is
    /// nothing left here to force from the subtab index.</summary>
    private static SC2TerrainShell.EditorMode TerrainModeFromSubtab(int subtabIndex)
    {
        switch (subtabIndex)
        {
            case 1: return SC2TerrainShell.EditorMode.Materials;
            case 2: return SC2TerrainShell.EditorMode.Pairs;
            case 3: return SC2TerrainShell.EditorMode.Paint;
            case 4: return SC2TerrainShell.EditorMode.Simplified;
            case 5: return SC2TerrainShell.EditorMode.Imported;
            case 6: return SC2TerrainShell.EditorMode.Water;
            case 7: return SC2TerrainShell.EditorMode.LightingBake;
            case 8: return SC2TerrainShell.EditorMode.Background;
            default: return SC2TerrainShell.EditorMode.Geometry;
        }
    }

    private void TryAdoptTerrainData()
    {
        if (_terrainShell == null)
            return;

        // ---- RuntimeTerrainData → shell (inspector / external assignment) ----
        // Check every frame: the user may swap RuntimeTerrainData in the Inspector
        // at any time, not just when the shell is empty. SC2TerrainShell.SetData
        // already guards against no-op assignments (if _data == data) return).
#if UNITY_EDITOR
        if (RuntimeTerrainData != null && _terrainShell.Data != RuntimeTerrainData)
        {
            _terrainShell.SetData(RuntimeTerrainData, RuntimeFieldTerrainSourceLabel(RuntimeTerrainData));
            return;
        }
#endif

        // ---- Shared Resources asset → shell (Editor AND build) ----
        // When nothing is explicitly assigned, load the same asset from
        // Resources in both the Editor and a build so they never diverge. This
        // deliberately runs before the Editor-only Selection fallback below so
        // the two stay consistent (the Editor no longer silently adopts whatever
        // happens to be selected in the Project window when a shared asset
        // exists).
        if (_terrainShell.Data == null)
        {
            var shared = LoadSharedTerrainData();
            if (shared != null)
            {
                RuntimeTerrainData = shared;
                _terrainShell.SetData(shared, SharedTerrainSourceLabel(shared));
                return;
            }
        }

#if UNITY_EDITOR
        // ---- Project selection → shell (Editor only, last resort) ----
        // Only reached when no shared Resources asset exists yet (i.e. before
        // the first "Create new SC2TerrainData"), so the Editor still lets you
        // pick one to get started.
        if (_terrainShell.Data == null)
        {
            var selected = Selection.activeObject as SC2TerrainData;
            if (selected != null)
                _terrainShell.SetData(selected);
        }
#endif
    }

    private void HandleTerrainGameViewInput()
    {
        if (_mainTab != 0 || Event.current == null || IsMouseOverUi(Event.current.mousePosition))
            return;

        var camera = ResolveCamera();
        if (camera == null)
            return;

        EnsureTerrainShell();
        SyncTerrainShellSubtab();
        TryAdoptTerrainData();
        // Was a fresh full-screen Rect(0,0,Screen.width,Screen.height),
        // unlike every sibling Handle*GameViewInput call (Objects, Reference
        // Units, ...), which all correctly pass _viewportRect (excludes the
        // left panel/header — see UpdateLayoutRects). Passing the full
        // screen here meant the Terrain brush's own viewport-clip checks
        // (SC2GeometryMode/SC2PaintMode's DrawGameViewGizmo, the map frame
        // overlay's projection) were always clipping against a rect that
        // already included the left panel, so nothing ever got cut off at
        // the real panel boundary — confirmed via real screenshots of the
        // Set H/Raise brush rings and the map border both bleeding straight
        // over the panel and, for the map border, projecting as garbled
        // diagonal lines (consistent with the camera being driven by a
        // viewport rect wider than what it's actually rendering into).
        _terrainShell.OnGameViewGUI(camera, _viewportRect, Event.current.mousePosition);
    }

    /// <summary>Keeps OverlayTerrain/OverlayRNM (top-panel Visibility menu)
    /// effective regardless of active tab — runs every frame, unlike
    /// HandleTerrainGameViewInput above, which only runs while the Terrain
    /// tab itself is active. Mirrors HandleCostsGameViewInput/
    /// HandleCollision2GameViewInput's own cross-tab overlay handling.</summary>
    private void SyncTerrainOverlayFlags()
    {
        if (_terrainShell == null)
            return;
        _terrainShell.SetTerrainOverlayVisible(OverlayTerrain);
        _terrainShell.SetForceRnmOverlay(OverlayRNM);
        _terrainShell.SetForceHfieldOverlay(OverlayHfield);
        _terrainShell.SetWaterOverlayVisible(OverlayWater);
        _terrainShell.SetHeightGridOverlay(OverlayGrid);
        _terrainShell.SetWireframeOverlay(OverlayWireframe);

        // The simplified mesh replaces the live terrain, and SyncTerrainShellSubtab only runs while the Terrain tab is drawn,
        // so leaving for another main tab has to drop it here. Coming back finds the shell's mode differing from the
        // subtab and re-enters Simplified, which rebuilds the preview.
        if (_mainTab != 0 && _terrainShell.Mode == SC2TerrainShell.EditorMode.Simplified)
            _terrainShell.SetMode(SC2TerrainShell.EditorMode.Geometry);
    }

    private SC2CostsShell _costsShell;

    private void EnsureCostsShell()
    {
        EnsureTerrainShell();
        if (_costsShell == null)
        {
            _costsShell = new SC2CostsShell(_terrainShell) { RepaintHost = RepaintGameView, Session = _mapSession };
            // Same reasoning as EnsureObjectsShell's own DataChanged
            // subscription — keeps SC2MapDocument.Costs/Hfield populated at
            // the exact load/create moment, not only whenever the Costs tab
            // next happens to be drawn.
            _terrainShell.DataChanged += _ => _costsShell?.SyncDocumentFromLive();
            // A placed decal's own mesh completely hides the Costs paint
            // overlay wherever it sits (decals render as a
            // separate mesh on top, with no idea the overlay exists). Push
            // the overlay into SC2DecalsShell's preview material the same
            // way it's already pushed into the imported-terrain materials.
            EnsureDecalsShell();
            _costsShell.PushOverlayToDecalPreview = (tex, enabled, gridSize) => _decalsShell?.SetCostsOverlay(tex, enabled, gridSize);
        }
    }

    /// <summary>The reference building's "where can it be built" colouring goes through the Costs overlay's shader slot, so it
    /// is pushed after the Costs handling of the frame (which clears the slot when Costs is not shown) and yields to the Costs
    /// overlay whenever that is showing.</summary>
    private void PushBuildableOverlay()
    {
        var texture = _referenceUnitsShell?.BuildableOverlayTexture;
        if (texture == null || _mainTab == 1 || OverlayCosts) return;
        EnsureCostsShell();
        _costsShell.PushExternalOverlay(texture);
    }

    private void HandleCostsGameViewInput()
    {
        // mainTab index 1 = Costs (see SC2EditorTabs.MainTabs ordering, mirrored
        // by DrawMainPanel's switch(_mainTab)). Leaving the tab disables the
        // overlay shader parameter so Terrain/other tabs don't keep showing
        // the costs/terrainTypes tint — UNLESS OverlayCosts (top-panel
        // Visibility menu) is on, in which case the overlay stays visible
        // from any tab (painting itself still only works from the Costs tab,
        // via allowInput below — this only affects the overlay's visibility).
        var costsTabActive = _mainTab == 1;
        if (!costsTabActive)
        {
            // Disarm "Enable Painting" the moment the user leaves the Costs
            // tab (e.g. to Terrain) — without this, painting stayed armed in
            // the background, so switching back into Costs later (or a
            // stray click while OverlayCosts keeps the overlay visible from
            // another tab) could silently resume painting the user thought
            // they'd turned off. Independent of the OverlayCosts branch
            // below, which only controls the overlay's VISIBILITY, not the
            // paint-armed state.
            _costsShell?.DisarmPainting();
            if (!OverlayCosts)
            {
                _costsShell?.DisableOverlay();
                return;
            }
        }

        EnsureCostsShell();
        TryAdoptTerrainData();

        var camera = ResolveCamera();
        if (camera == null)
            return;

        // Painting is suppressed while the mouse is over the side panel/menus
        // — same mouse-over-UI gate HandleTerrainGameViewInput uses for
        // terrain painting. The overlay itself (PushOverlayToRuntime, called
        // unconditionally inside OnGameViewGUI below) is NOT gated on this —
        // it's a persistent shader-texture parameter, so it keeps showing
        // regardless of input/mouse state, painting toggle, or mouse-up.
        // Also gated on costsTabActive: OverlayCosts alone should only show
        // the overlay, never let painting fire from a different tab.
        var mousePos = Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f);
        var allowInput = costsTabActive && Event.current != null && !IsMouseOverUi(mousePos);
        // A single unhandled exception here (e.g. a null shader, per the
        // "Value cannot be null. Parameter name: shader" class of bug already
        // hit and fixed elsewhere in this project) would otherwise propagate
        // out of OnGUI mid-frame and leave the ENTIRE GameView UI blank for
        // every subsequent frame (nothing after the throw point gets drawn,
        // and this runs every frame once the Costs tab is active — so it
        // never recovers). Contain it here instead: log it once and disable
        // input handling until the tab is re-entered, so a bug in the Costs
        // overlay/painting path can't take down the rest of the editor UI.
        try
        {
            _costsShell.OnGameViewGUI(camera, new Rect(0f, 0f, Screen.width, Screen.height), mousePos, allowInput);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            SetStatus("Costs tab error (see log): " + ex.Message);
        }
    }

    private void TickFlyCamera()
    {
        if (!_flying)
            return;

        var moved = false;
        if (SC2Hotkeys.GetKey("Camera.MoveForward")) moved |= FlyCamera("Camera.MoveForward");
        if (SC2Hotkeys.GetKey("Camera.MoveLeft")) moved |= FlyCamera("Camera.MoveLeft");
        if (SC2Hotkeys.GetKey("Camera.MoveBack")) moved |= FlyCamera("Camera.MoveBack");
        if (SC2Hotkeys.GetKey("Camera.MoveRight")) moved |= FlyCamera("Camera.MoveRight");
        if (SC2Hotkeys.GetKey("Camera.MoveDown")) moved |= FlyCamera("Camera.MoveDown");
        if (SC2Hotkeys.GetKey("Camera.MoveUp")) moved |= FlyCamera("Camera.MoveUp");

#if UNITY_EDITOR
        if (moved)
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
#endif
    }

    // ---- Shared inspector building blocks -------------------------------------
    // All static tab inspectors use the same section header + description card +
    // fixed-height buttons, so they match the terrain shell's unified panel boxes
    // and the top-panel design language (one font scale, one card, one pill).

    private void DrawInspectorHeader(string title, string description)
    {
        GUILayout.Space(2);
        GUILayout.Label(title, _titleStyle);
        SC2TerrainShell.DrawUnifiedNote(description);
        GUILayout.Space(4);
    }

    private bool DrawInspectorButton(string label)
    {
        return GUILayout.Button(label, GUILayout.Height(26));
    }

    private void DrawCostsInspector()
    {
        DrawInspectorHeader("Costs",
            "Paint passability and terrain types over the sculpted terrain with the brush in the viewport. Both are saved with the terrain.");
        EnsureCostsShell();
        // Same reasoning as the try/catch in HandleCostsGameViewInput — an
        // exception thrown while drawing this panel's controls would
        // otherwise abort the rest of OnGUI for the frame (header, tab
        // strip, everything), which is the "whole UI goes blank/black"
        // symptom this guards against.
        try
        {
            _costsShell.DrawEmbeddedGUI(PanelContentWidth, _subTab);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            EditorGUILayout.HelpBox("Costs panel error (see log): " + ex.Message, MessageType.Error);
        }
    }

    private void DrawCollisionInspector()
    {
        DrawInspectorHeader("Collision2",
            "Collision2 is the collision mesh the game uses for the ground. It is generated from the terrain and simplified to a triangle budget you set with the Polygon Budget slider below (default 2500, up to 10000).");
        EnsureCollision2Shell();
        // Same reasoning as the try/catch in DrawCostsInspector — an
        // exception here would otherwise abort the rest of OnGUI for the
        // frame.
        try
        {
            _collision2Shell.DrawEmbeddedGUI(PanelContentWidth);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            EditorGUILayout.HelpBox("Collision2 panel error (see log): " + ex.Message, MessageType.Error);
        }

        EditorGUILayout.Space(6);
        if (DrawInspectorButton("Export Mesh To Blender"))
        {
            var path = PickCollision2ObjPath(save: true);
            if (!string.IsNullOrEmpty(path))
                SetStatus(_collision2Shell.ExportMeshToObj(path));
        }
        if (DrawInspectorButton("Import Corrected Mesh"))
        {
            var path = PickCollision2ObjPath(save: false);
            if (!string.IsNullOrEmpty(path))
                SetStatus(_collision2Shell.ImportCorrectedMeshObj(path));
        }
        if (DrawInspectorButton("Save Corrected Mesh As collision2.bdf..."))
        {
            string path;
#if UNITY_EDITOR
            path = UnityEditor.EditorUtility.SaveFilePanel("Save collision2.bdf", "", "collision2.win", "bdf");
#else
            path = SC2RuntimeFileDialog.SaveFile("Save collision2.bdf", "Collision2 BDF (*.bdf)", "bdf");
#endif
            if (!string.IsNullOrEmpty(path))
                SetStatus(_collision2Shell.SaveCorrectedMeshAsCollision2Bdf(path));
        }
        if (DrawInspectorButton("Import collision2.bdf..."))
        {
            string path;
#if UNITY_EDITOR
            path = UnityEditor.EditorUtility.OpenFilePanel("Import collision2.bdf", "", "bdf");
#else
            path = SC2RuntimeFileDialog.OpenFile("Import collision2.bdf", "Collision2 BDF (*.bdf)", "bdf");
#endif
            if (!string.IsNullOrEmpty(path))
                SetStatus(_collision2Shell.ImportCollision2Bdf(path));
        }
    }

    /// <summary>Shared file-picker for the Collision2 Blender round-trip
    /// buttons above — same Editor-vs-Build dual-path pattern already used
    /// throughout this codebase (e.g. SC2ObjectsShell.ImportObjectsFromFile,
    /// SC2InfoBdfEditorPanel.PickFile).</summary>
    private static string PickCollision2ObjPath(bool save)
    {
#if UNITY_EDITOR
        return save
            ? UnityEditor.EditorUtility.SaveFilePanel("Export Collision2 Mesh", "", "collision2_mesh", "obj")
            : UnityEditor.EditorUtility.OpenFilePanel("Import Corrected Collision2 Mesh", "", "obj");
#else
        return save
            ? SC2RuntimeFileDialog.SaveFile("Export Collision2 Mesh", "Wavefront OBJ (*.obj)", "obj")
            : SC2RuntimeFileDialog.OpenFile("Import Corrected Collision2 Mesh", "Wavefront OBJ (*.obj)", "obj");
#endif
    }

    private SC2Collision2Shell _collision2Shell;

    private void EnsureCollision2Shell()
    {
        EnsureTerrainShell();
        if (_collision2Shell == null)
            _collision2Shell = new SC2Collision2Shell(_terrainShell, _mapSession) { RepaintHost = RepaintGameView };
    }

    private void HandleCollision2GameViewInput()
    {
        // mainTab index 2 = Collision2 (see GameMainTabIds ordering, mirrored
        // by DrawMainPanel's switch(_mainTab)) — hides the preview mesh when
        // the user leaves this tab, same as Costs disables its overlay.
        // OverlayCollision2 (top-panel Visibility menu) keeps it visible from
        // any tab, same idea as the Costs overlay below — SetTabActive only
        // ever drives UpdatePreviewVisibility, never input handling, so OR-ing
        // it in here can't accidentally enable dragging/painting elsewhere.
        EnsureCollision2Shell();
        _collision2Shell.SetTabActive(_mainTab == 2 || OverlayCollision2);
    }

    private void DrawEnvironmentInspector()
    {
        DrawInspectorHeader("Environment",
            "Sun, water, fog, glow and the other lighting and atmosphere settings of the map (saved as info.bdf).");
        EnsureEnvironmentShell();
        // Same reasoning as the try/catch around Costs/Collision2's embedded
        // GUI — an exception here shouldn't blank the rest of the panel.
        try
        {
            _environmentShell.DrawEmbeddedGUI(PanelContentWidth, _subTab);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            EditorGUILayout.HelpBox("Environment panel error (see log): " + ex.Message, MessageType.Error);
        }
    }

    private SC2EnvironmentShell _environmentShell;

    private void EnsureEnvironmentShell()
    {
        EnsureTerrainShell();
        if (_environmentShell == null)
            _environmentShell = new SC2EnvironmentShell(_terrainShell, _mapSession) { RepaintHost = RepaintGameView };
    }

    private void DrawScenarioInspector()
    {
        DrawInspectorHeader("Scenario",
            "The map's name, its scripts, and the minimap and preview pictures the game shows for it.");
        EnsureScenarioShell();
        try
        {
            _scenarioShell.DrawEmbeddedGUI(PanelContentWidth, _subTab);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            EditorGUILayout.HelpBox("Scenario panel error (see log): " + ex.Message, MessageType.Error);
        }
    }

    private SC2ScenarioShell _scenarioShell;

    private void EnsureScenarioShell()
    {
        EnsureTerrainShell();
        if (_scenarioShell == null)
            _scenarioShell = new SC2ScenarioShell(_terrainShell, _mapSession) { RepaintHost = RepaintGameView, OpenLobbySpawnEditor = OpenLobbySpawnPanel };
    }

    private void DrawExportInspector()
    {
        DrawInspectorHeader("Export",
            "Write the map to .bdf and .lua files. Check the Validation tab first.");
        EnsureExportShell();
        try
        {
            _exportShell.DrawEmbeddedGUI(PanelContentWidth, _subTab);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            EditorGUILayout.HelpBox("Export panel error (see log): " + ex.Message, MessageType.Error);
        }
    }

    private SC2ExportShell _exportShell;

    private void EnsureExportShell()
    {
        EnsureTerrainShell();
        if (_exportShell == null)
            _exportShell = new SC2ExportShell(_terrainShell, _mapSession)
            {
                RepaintHost = RepaintGameView,
                ObjectsIssues = () =>
                {
                    EnsureObjectsShell();
                    return _objectsShell.CollectValidationIssues();
                },
            };
    }

    private void DrawObjectsInspector()
    {
        DrawInspectorHeader("Objects",
            "Armies, units, props, markers, path graphs, zones and chains of the map.");
        EnsureObjectsShell();
        try
        {
            using (SC2FramePerf.Measure("objects.panel"))
                _objectsShell.DrawEmbeddedGUI(PanelContentWidth, _subTab);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            EditorGUILayout.HelpBox("Objects panel error (see log): " + ex.Message, MessageType.Error);
        }
    }

    private SC2ObjectsShell _objectsShell;

    private void EnsureObjectsShell()
    {
        EnsureTerrainShell();
        if (_objectsShell == null)
        {
            _objectsShell = new SC2ObjectsShell(_terrainShell)
            {
                Session = _mapSession,
                RepaintHost = RepaintGameView,
                ReportStatus = SetStatus,
                RequestSubTab = i => _subTab = i,
                RequestCameraFocus = FocusCameraOnWorldPoint,
                // A prop placed via "Browse prop
                // blueprints" (which spawns immediately at this point, not
                // via click-to-place) must not land at what would feel like a
                // random spot on the map. A fixed
                // "camera.position + camera.forward*40" point bears no
                // reliable relationship to what's actually visible on
                // screen for this editor's typically steep/top-down camera
                // angle (a nearly-straight-down forward vector barely moves
                // the point horizontally; a shallow/grazing angle overshoots
                // far past the visible area). Raycast the real viewport
                // CENTER against the actual ground instead (imported
                // terrain first, live heightfield fallback — the same
                // precedence every real click-to-place already uses via
                // TryHandlePlacementClick), so this lands on the ground the
                // user is actually looking at.
                GetCameraFocusPoint = () =>
                {
                    var cam = ResolveCamera();
                    if (cam == null) return Vector3.zero;
                    var centerGui = new Vector2(_viewportRect.x + _viewportRect.width * 0.5f, _viewportRect.y + _viewportRect.height * 0.5f);
                    var ray = SC2TerrainShell.GameGuiPointToRay(cam, _viewportRect, centerGui);
                    if (SC2TerrainShell.TryRaycastImportedTerrain(BuildImportedTerrainColliders(), ray, out var hit))
                        return hit;
                    if (_terrainShell != null && SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
                        return hit;
                    // Nothing under the viewport center (e.g. camera pointed
                    // at open sky past the map edge) — fall back to the old
                    // forward-projection rather than Vector3.zero.
                    return cam.transform.position + cam.transform.forward * 40f;
                },
                GetImportedTerrainColliders = BuildImportedTerrainColliders
            };
            // SC2TerrainShell can't reach into SC2ObjectsShell directly
            // (siblings, both owned here), so it fires this event at the
            // exact load/create moment instead — see SC2TerrainShell.
            // DataChanged's own doc comment.
            _terrainShell.DataChanged += _ => _objectsShell?.EnsureObjectsDocumentSynced();
            // Prop/unit blueprint indexing must not wait for the Objects tab
            // or the Unit/Prop Browser modal to be opened first, which would
            // cause a noticeable stall right when it's first needed. Kick it
            // off at the same load/create moment instead, regardless of
            // which tab is active.
            _terrainShell.DataChanged += _ => _objectsShell?.EnsureAssetsIndexed();
            // See SC2TerrainShell.HeightBrushApplied's own doc comment —
            // keeps anchored Props/Markers/Units following live
            // Geometry-mode sculpting instead of only ever re-snapping once
            // at import time.
            _terrainShell.HeightBrushApplied += (center, radius) => _objectsShell?.ReprojectAnchoredObjectsInRadius(center, radius);
            // Same for a moved imported terrain object (its old and new region).
            _terrainShell.ImportedTerrainMoved += bounds => _objectsShell?.ReprojectAnchoredObjectsInBounds(bounds);
        }
    }

    private SC2ReferenceUnitsShell _referenceUnitsShell;

    private void EnsureReferenceUnitsShell()
    {
        EnsureTerrainShell();
        if (_referenceUnitsShell == null)
        {
            _referenceUnitsShell = new SC2ReferenceUnitsShell(_terrainShell)
            {
                RepaintHost = RepaintGameView,
                ReportStatus = SetStatus,
                GetWaterSurface = ReadWaterSurface,
            };
            // Switching/reloading a map must not leave selection/drag state
            // (or a stale intel cache) pointed at ids from the PREVIOUS
            // map's ObjReferenceUnits — same reasoning as _objectsShell's own
            // DataChanged hookups just above.
            _terrainShell.DataChanged += _ => _referenceUnitsShell?.OnDataChanged();
        }
    }

    /// <summary>The Y of the map's water surface, or null when its water is off (read the way the water plane itself reads it).</summary>
    private float? ReadWaterSurface()
    {
        var data = _terrainShell?.Data;
        if (data == null) return null;
        var info = SC2TerrainExporter.BuildInfoModel(data, _mapSession, quiet: true);
        return info.WaterEnabled != 0 ? info.WaterElevation : (float?)null;
    }

    /// <summary>Same raycast contract TryGetMeasureHit already implements —
    /// passed to SC2ReferenceUnitsShell as a delegate rather than
    /// duplicated, since building the imported-terrain collider list
    /// (BuildImportedTerrainColliders) is this class's own responsibility.</summary>
    private bool TryRaycastForPlacement(Camera camera, Vector2 guiMousePosition, out Vector3 hit)
    {
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, _viewportRect, guiMousePosition);
        if (SC2TerrainShell.TryRaycastImportedTerrain(BuildImportedTerrainColliders(), ray, out hit))
            return true;
        if (_terrainShell != null && SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
            return true;
        hit = default;
        return false;
    }

    /// <summary>Runs every frame regardless of which main tab is active —
    /// reference units are a global, always-visible aid, same reasoning
    /// Decals' EnsurePreview already uses for its own "stay visible from any
    /// tab" content.</summary>
    private void HandleReferenceUnitsGameViewInput()
    {
        EnsureReferenceUnitsShell();
        _referenceUnitsShell.DrawBrowserModal();
        var camera = ResolveCamera();
        if (camera == null) return;
        var mousePosition = Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f);
        var allowInput = Event.current != null && !IsMouseOverUi(mousePosition);
        _referenceUnitsShell.OnGameViewGUI(camera, _viewportRect, mousePosition, allowInput, OverlayReferenceUnits, TryRaycastForPlacement);
    }

    // "Import .scd (Document)" panel. Separate from _objectsShell/
    // _terrainShell on purpose: it reads and writes _mapSession, not legacy
    // SC2TerrainData.
    private SC2MapImportPanel _mapImportPanel;

    private void EnsureMapImportPanel()
    {
        EnsureTerrainShell();
        if (_mapImportPanel == null)
        {
            _mapImportPanel = new SC2MapImportPanel(_mapSession, () => _terrainShell.Data)
            {
                RepaintHost = RepaintGameView,
                ReportStatus = SetStatus,
                OnImportComplete = OnMapImportComplete,
            };
        }
    }

    private void OpenMapDocumentImportPanel()
    {
        EnsureMapImportPanel();
        _mapImportPanel.Open();
        SetStatus("Opened Import Map.");
    }

    /// <summary>Wires a completed SC2MapImportOrchestrator.Result into live
    /// scene/UI state — the panel/orchestrator only ever produce data
    /// (parsed models, imported SC2TerrainData fields), never touch
    /// _importedTerrains/_collision2Shell/scene GameObjects directly, same
    /// separation OpenTerrainBdfImport/OpenDecalsBdfImport already keep.</summary>
    private void OnMapImportComplete(SC2MapImportOrchestrator.Result result)
    {
        if (result?.TerrainImport?.Root != null)
        {
            var terrainImport = result.TerrainImport;
            var meshRenderer = terrainImport.Root.GetComponent<MeshRenderer>();
            var originalMaterials = meshRenderer != null ? meshRenderer.sharedMaterials : System.Array.Empty<Material>();
            var entry = new SC2ImportedTerrainEntry
            {
                Root = terrainImport.Root,
                DisplayName = terrainImport.Root.name,
                TextureAttempts = terrainImport.TextureAttempts,
                Model = terrainImport.Model,
                OriginalBdfBytes = terrainImport.RawBytes,
                OriginalMaterials = originalMaterials,
                OriginalEnableRnm = originalMaterials
                    .Select(m => m != null && m.HasProperty("_EnableRNM") ? m.GetFloat("_EnableRNM") : 0f)
                    .ToArray()
            };
            _importedTerrains.Add(entry);
            _selectedImportedTerrain = _importedTerrains.Count - 1;
            _terrainShell?.InvalidateLiveHeightGrid();
            SelectMainTab(0, System.Array.IndexOf(SC2EditorTabs.TerrainTabs, "Imported"));
            if (_terrainShell != null)
                ApplyPreviewModeToImportedTerrain(entry, _terrainShell.Prev);
        }

        // An empty decals.bdf (real map with no decal geometry) still gets a
        // real (but empty) GameObject from SC2DecalsBdfImporter.Import — no
        // point leaving that in the scene.
        if (result?.DecalsImport?.Root != null)
        {
            if (result.DecalsImport.Model != null && result.DecalsImport.Model.HasDecals)
            {
                result.DecalsImport.Root.name = "Decals (" + result.DecalsImport.Root.name + ")";
                _importedDecalsRoots.Add(result.DecalsImport.Root);
            }
            else
                UnityEngine.Object.DestroyImmediate(result.DecalsImport.Root);
        }

        if (result?.Collision2Preview != null)
        {
            EnsureCollision2Shell();
            _collision2Shell.ImportCollision2Model(result.Collision2Preview, "imported map's collision2.bdf");
        }
    }

    // Independent of _mapImportPanel above — reads/writes an arbitrary
    // loaded .bdf file plus (on Save) the active terrain's EnvironmentHeader,
    // not _mapSession/SC2MapDocument at all.
    private SC2InfoBdfEditorPanel _infoBdfEditorPanel;

    private void EnsureInfoBdfEditorPanel()
    {
        if (_infoBdfEditorPanel == null)
        {
            _infoBdfEditorPanel = new SC2InfoBdfEditorPanel(() => _terrainShell)
            {
                RepaintHost = RepaintGameView,
                ReportStatus = SetStatus,
            };
        }
    }

    // Lobby minimap spawn editor — opened from Scenario > Preview. Edits
    // only SC2MapDocument.Scenario.LobbyStartPositions (_scenario.lua).
    private SC2LobbySpawnPanel _lobbySpawnPanel;

    private void OpenLobbySpawnPanel()
    {
        EnsureScenarioShell();
        _lobbySpawnPanel ??= new SC2LobbySpawnPanel(() => _terrainShell, () => _mapSession, () => _scenarioShell)
        {
            RepaintHost = RepaintGameView,
            ReportStatus = SetStatus,
        };
        _lobbySpawnPanel.Open();
    }

    private void OpenInfoBdfEditorPanel()
    {
        EnsureInfoBdfEditorPanel();
        _infoBdfEditorPanel.Open();
        SetStatus("Opened Info.bdf Editor.");
    }

    /// <summary>Re-centers the camera on a world point (an Objects tab
    /// selection) without changing its current orientation or distance —
    /// same smoothed StartViewTransition the view-gizmo axis balls
    /// (LookAlong) already use, just targeting an explicit point instead of
    /// GetViewPivot()'s Selection.activeTransform (Objects are plain
    /// SC2TerrainData list entries, not GameObjects, so there is no
    /// Transform to select).</summary>
    private void FocusCameraOnWorldPoint(Vector3 worldPos)
    {
        var cam = ResolveCamera();
        if (cam == null) return;
        var currentRotation = cam.transform.rotation;
        var distance = Mathf.Max(20f, Vector3.Distance(cam.transform.position, worldPos));
        var targetPosition = worldPos - (currentRotation * Vector3.forward) * distance;
        StartViewTransition(cam, targetPosition, currentRotation);
        SetStatus("Focused camera on selected object.");
    }

    private void HandleObjectsGameViewInput()
    {
        // mainTab index 3 = Objects (GameMainTabIds ordering). Previously
        // bailed out entirely whenever the Objects tab wasn't active, so
        // the Visibility menu's Objects/Markers/Graphs/Areas checkboxes had
        // no effect at all from any other tab - unlike Terrain/Water/RNM/
        // Hfield/Costs/Collision2, which all explicitly stay visible
        // cross-tab. Now this keeps running (overlay-drawing only, via
        // allowInput=false below) whenever any relevant flag is on, and
        // only gates real interaction (placement/selection/drag/delete) on
        // actually being on the Objects tab.
        var objectsTabActive = _mainTab == 3;
        if (!objectsTabActive && !OverlayProps && !OverlayMarkers && !OverlayUnits
            && !OverlayGraphLand && !OverlayGraphAir && !OverlayGraphNaval && !OverlayGraphAmphibious
            && !OverlayChains && !OverlayAreas)
            return;

        EnsureObjectsShell();
        TryAdoptTerrainData();

        if (_wasObjectsTabActive && !objectsTabActive)
            _objectsShell.CancelActiveTools();
        _wasObjectsTabActive = objectsTabActive;

        var camera = ResolveCamera();
        if (camera == null)
            return;

        var mousePos = Event.current != null ? Event.current.mousePosition : new Vector2(-1f, -1f);
        var allowInput = objectsTabActive && Event.current != null && !IsMouseOverUi(mousePos);
        if (Event.current != null && objectsTabActive && (Event.current.rawType == EventType.MouseDrag || Event.current.rawType == EventType.MouseUp) && _objectsShell.DragDiagWanted)
            Debug.Log($"[SC2DragDiag] {Event.current.rawType} reaches the objects handler: type={Event.current.type} allowInput={allowInput} hot={GUIUtility.hotControl} tool={_tool}");
        // Same contain-the-exception reasoning as HandleCostsGameViewInput —
        // a bug in the viewport dot-picking code shouldn't blank the rest of
        // the GameView UI for every subsequent frame.
        try
        {
            using (SC2FramePerf.Measure("objects.viewport"))
                _objectsShell.OnGameViewGUI(camera, _viewportRect, mousePos, allowInput, _tool,
                    OverlayProps, OverlayUnits, OverlayMarkers,
                    OverlayGraphLand, OverlayGraphAir, OverlayGraphNaval, OverlayGraphAmphibious,
                    OverlayChains, OverlayAreas, objectsTabActive, AnyMenuOpen());
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            SetStatus("Objects tab viewport error (see log): " + ex.Message);
        }
    }

    /// <summary>Blender-style G/R/S gestures for the Imported subtab's
    /// currently-selected imported terrain.bdf object — see
    /// DrawUndoableVector3Field's own comment for the companion
    /// Position/Rotation/Scale hang fix. Unlike Props
    /// (SC2ObjectsShell), an imported object has no per-frame viewport
    /// pick-dot of its own and no click-drag-to-move today, so Move is a
    /// gesture here too (Decals already has exactly this G/R/S shape, see
    /// SC2DecalsShell.HandleEditHotkey/TickTransform) rather than reusing
    /// Props' "G removed, drag whatever's already selected" convention — that
    /// convention assumes a pickable in-viewport handle, which doesn't exist
    /// here. Gated on the Imported subtab being the active one (not just
    /// Terrain being the active main tab) so G/R/S don't fire while looking
    /// at Geometry/Materials/etc.</summary>
    private void HandleImportedTerrainGameViewInput()
    {
        var importedTabActive = _mainTab == 0 && _subTab >= 0 && _subTab < SC2EditorTabs.TerrainTabs.Length
            && SC2EditorTabs.TerrainTabs[_subTab] == "Imported";
        if (!importedTabActive)
        {
            if (_importedGestureKind != ImportedGestureKind.None)
                CancelImportedGesture();
            return;
        }
        if (_importedGestureKind == ImportedGestureKind.None)
            TrySelectImportedTerrainByClick();
        if (_selectedImportedTerrain < 0 || _selectedImportedTerrain >= _importedTerrains.Count)
            return;
        var entry = _importedTerrains[_selectedImportedTerrain];
        if (entry?.Root == null)
            return;

        var camera = ResolveCamera();
        if (camera == null)
            return;

        var e = Event.current;
        var mousePos = e != null ? e.mousePosition : new Vector2(-1f, -1f);

        try
        {
            if (_importedGestureKind != ImportedGestureKind.None)
            {
                // The gesture already owns entry from when it started —
                // re-fetching by _selectedImportedTerrain here would silently
                // retarget a Move/Rotate/Scale in progress onto a different
                // object if the user changed the panel's selection mid-drag.
                TickImportedGesture(camera, mousePos);
                return;
            }

            var allowInput = e != null && !IsMouseOverUi(mousePos) && !AnyMenuOpen();
            if (!allowInput || e.type != EventType.KeyDown)
                return;

            ImportedGestureKind kind;
            if (SC2Hotkeys.Matches("ImportedTerrain.MoveGesture", e)) kind = ImportedGestureKind.Move;
            else if (SC2Hotkeys.Matches("ImportedTerrain.RotateGesture", e)) kind = ImportedGestureKind.Rotate;
            else if (SC2Hotkeys.Matches("ImportedTerrain.ScaleGesture", e)) kind = ImportedGestureKind.Scale;
            else return;

            var anchorPixel = SC2TopViewMirror.WorldToGui(camera, entry.Root.transform.position);
            if (anchorPixel.z <= 0f)
                return;

            _importedGestureKind = kind;
            _importedGestureAxis = ImportedGestureAxis.Free;
            _importedGestureEntry = entry;
            _importedGestureStartPosition = entry.Root.transform.position;
            _importedGestureStartEuler = entry.Root.transform.eulerAngles;
            _importedGestureStartScale = entry.Root.transform.localScale;
            _importedGestureAnchorScreen = new Vector2(anchorPixel.x, anchorPixel.y);
            _importedGestureStartMouse = mousePos;
            _importedGestureStartDistance = Mathf.Max(1f, Vector2.Distance(_importedGestureAnchorScreen, mousePos));
            _importedGestureBoundsBefore = ImportedEntryBounds(entry);
            if (!TryGetImportedGestureGroundPoint(camera, mousePos, _importedGestureStartPosition.y, out _importedGestureStartGroundWorld))
                _importedGestureStartGroundWorld = _importedGestureStartPosition;
            RecordImportedTerrainsUndo();
            SetStatus($"{kind}: move mouse (X/Y/Z constrains an axis), click/Enter to confirm, Esc to cancel.");
            e.Use();
        }
        catch (System.Exception ex)
        {
            // Same contain-the-exception reasoning as HandleObjectsGameViewInput
            // just above — a bug here shouldn't blank the rest of the GameView
            // UI, and shouldn't leave a gesture stuck active forever either.
            SC2Log.LogException(ex);
            SetStatus("Imported terrain gesture error (see log): " + ex.Message);
            CancelImportedGesture();
        }
    }

    /// <summary>A left click on an imported terrain object in the viewport selects it (the same selection the panel's object buttons
    /// make). The click only counts when it lands on the object's own collider and nothing else (a menu, the panel, the Measure tool)
    /// has taken it; a click on empty ground leaves the selection alone.</summary>
    private void TrySelectImportedTerrainByClick()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.MouseDown || e.button != 0 || e.alt || _tool == "Measure") return;
        if (!_viewportRect.Contains(e.mousePosition) || IsMouseOverUi(e.mousePosition) || AnyMenuOpen()) return;
        var camera = ResolveCamera();
        if (camera == null) return;

        // A collider keeps the pose it had at the last physics step until transforms are synced, and the object may just have been moved.
        Physics.SyncTransforms();
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, _viewportRect, e.mousePosition);
        var picked = -1;
        var pickedDistance = float.MaxValue;
        for (var i = 0; i < _importedTerrains.Count; i++)
        {
            var root = _importedTerrains[i]?.Root;
            if (root == null || !root.activeInHierarchy || _importedTerrains[i].Hidden) continue;
            var collider = root.GetComponent<Collider>();
            if (collider == null || !collider.Raycast(ray, out var hit, 1_000_000f) || hit.distance >= pickedDistance) continue;
            pickedDistance = hit.distance;
            picked = i;
        }
        if (picked < 0) return;

        if (picked != _selectedImportedTerrain)
        {
            _selectedImportedTerrain = picked;
            SetStatus("Selected imported object: " + _importedTerrains[picked].DisplayName);
        }
        e.Use();
    }

    /// <summary>An outline along the silhouette of the imported terrain object selected in the Imported tab (the one being moved,
    /// rotated or scaled while a gesture runs), as Blender outlines a selected object, so it is visible in the viewport which object
    /// the panel is editing. A hidden object has nothing to outline.</summary>
    private void DrawSelectedImportedTerrainFrame()
    {
        var importedTabActive = _mainTab == 0 && _subTab >= 0 && _subTab < SC2EditorTabs.TerrainTabs.Length
            && SC2EditorTabs.TerrainTabs[_subTab] == "Imported";
        SC2ImportedTerrainEntry entry = null;
        if (importedTabActive)
        {
            if (_importedGestureKind != ImportedGestureKind.None) entry = _importedGestureEntry;
            else if (_selectedImportedTerrain >= 0 && _selectedImportedTerrain < _importedTerrains.Count) entry = _importedTerrains[_selectedImportedTerrain];
        }
        if (entry?.Root == null || entry.Hidden || !entry.Root.activeInHierarchy)
        {
            SC2SelectionOutline.Clear();
            return;
        }
        var camera = ResolveCamera();
        if (camera == null) { SC2SelectionOutline.Clear(); return; }
        _importedOutlineRenderers.Clear();
        foreach (var renderer in entry.Root.GetComponentsInChildren<Renderer>(false))
            if (renderer != null && renderer.enabled) _importedOutlineRenderers.Add(renderer);
        SC2SelectionOutline.Set(camera, _importedOutlineRenderers);
    }

    private readonly List<Renderer> _importedOutlineRenderers = new List<Renderer>();

    private bool TryGetImportedGestureGroundPoint(Camera camera, Vector2 mousePosition, float planeY, out Vector3 hit)
    {
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, _viewportRect, mousePosition);
        var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
        if (plane.Raycast(ray, out var enter))
        {
            hit = ray.GetPoint(enter);
            return true;
        }
        hit = default;
        return false;
    }

    private void TickImportedGesture(Camera camera, Vector2 mousePosition)
    {
        var e = Event.current;
        var entry = _importedGestureEntry;
        if (entry?.Root == null) { EndImportedGesture(false); return; }
        var t = entry.Root.transform;

        if (e != null && e.type == EventType.KeyDown &&
            (e.keyCode == KeyCode.X || e.keyCode == KeyCode.Y || e.keyCode == KeyCode.Z))
        {
            _importedGestureAxis = e.keyCode switch
            {
                KeyCode.X => ImportedGestureAxis.X,
                KeyCode.Y => ImportedGestureAxis.Y,
                _ => ImportedGestureAxis.Z,
            };
            // Re-anchor the ground-plane reference point for Move so
            // switching axis mid-drag doesn't jump the object by whatever
            // delta had already accumulated on the axis just left.
            if (_importedGestureKind == ImportedGestureKind.Move &&
                !TryGetImportedGestureGroundPoint(camera, mousePosition, _importedGestureStartPosition.y, out _importedGestureStartGroundWorld))
                _importedGestureStartGroundWorld = _importedGestureStartPosition;
            _importedGestureStartMouse = mousePosition;
            e.Use();
            return;
        }

        if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            t.position = _importedGestureStartPosition;
            t.eulerAngles = _importedGestureStartEuler;
            t.localScale = _importedGestureStartScale;
            EndImportedGesture(false);
            e.Use();
            return;
        }

        if (e != null && ((e.type == EventType.MouseDown && e.button == 0) ||
            (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))))
        {
            EndImportedGesture(true);
            e.Use();
            return;
        }

        switch (_importedGestureKind)
        {
            case ImportedGestureKind.Move: TickImportedMove(camera, entry, mousePosition); break;
            case ImportedGestureKind.Rotate: TickImportedRotate(entry, mousePosition); break;
            case ImportedGestureKind.Scale: TickImportedScale(entry, mousePosition); break;
        }
        RepaintGameView();
    }

    private void TickImportedMove(Camera camera, SC2ImportedTerrainEntry entry, Vector2 mousePosition)
    {
        var t = entry.Root.transform;
        if (_importedGestureAxis == ImportedGestureAxis.Y)
        {
            // No ground-plane hit means anything for a pure height drag from
            // a top-down camera — vertical screen delta maps straight to
            // world Y instead, same "pixels * arbitrary sensitivity" idea as
            // ImportedRotateAxisDegreesPerPixel below.
            var worldDelta = (_importedGestureStartMouse.y - mousePosition.y) * ImportedMoveYWorldPerPixel;
            t.position = new Vector3(_importedGestureStartPosition.x, _importedGestureStartPosition.y + worldDelta, _importedGestureStartPosition.z);
            SetStatus($"Move Y: {worldDelta:0.##} — click/Enter to confirm, Esc to cancel.");
            return;
        }

        if (!TryGetImportedGestureGroundPoint(camera, mousePosition, _importedGestureStartPosition.y, out var currentGround))
            return;
        var delta = currentGround - _importedGestureStartGroundWorld;
        if (_importedGestureAxis == ImportedGestureAxis.X) delta.z = 0f;
        else if (_importedGestureAxis == ImportedGestureAxis.Z) delta.x = 0f;
        t.position = _importedGestureStartPosition + new Vector3(delta.x, 0f, delta.z);
        SetStatus($"Move: {new Vector2(delta.x, delta.z).magnitude:0.##} — click/Enter to confirm, Esc to cancel.");
    }

    private void TickImportedRotate(SC2ImportedTerrainEntry entry, Vector2 mousePosition)
    {
        var t = entry.Root.transform;
        if (_importedGestureAxis == ImportedGestureAxis.X || _importedGestureAxis == ImportedGestureAxis.Z)
        {
            var deltaDegrees = (mousePosition.x - _importedGestureStartMouse.x) * ImportedRotateAxisDegreesPerPixel;
            var euler = _importedGestureStartEuler;
            if (_importedGestureAxis == ImportedGestureAxis.X) euler.x += deltaDegrees; else euler.z += deltaDegrees;
            t.eulerAngles = euler;
            SetStatus($"Rotate {_importedGestureAxis}: {deltaDegrees:0.#}° — click/Enter to confirm, Esc to cancel.");
            return;
        }

        // Default / Y axis: arc angle around the object's own on-screen
        // position, same convention SC2ObjectsShell's Props Rotate gesture
        // already uses for its (also Y-only) rotation.
        var start = _importedGestureStartMouse - _importedGestureAnchorScreen;
        var current = mousePosition - _importedGestureAnchorScreen;
        if (start.sqrMagnitude <= 4f || current.sqrMagnitude <= 4f) return;
        var deltaY = -(Mathf.Atan2(current.y, current.x) - Mathf.Atan2(start.y, start.x)) * Mathf.Rad2Deg;
        var eulerY = _importedGestureStartEuler;
        eulerY.y += deltaY;
        t.eulerAngles = eulerY;
        SetStatus($"Rotate: {deltaY:0.#}° — click/Enter to confirm, Esc to cancel.");
    }

    private void TickImportedScale(SC2ImportedTerrainEntry entry, Vector2 mousePosition)
    {
        var t = entry.Root.transform;
        var factor = Mathf.Max(1f, Vector2.Distance(_importedGestureAnchorScreen, mousePosition)) / _importedGestureStartDistance;
        var scale = _importedGestureStartScale;
        switch (_importedGestureAxis)
        {
            case ImportedGestureAxis.X: scale.x = Mathf.Max(0.01f, _importedGestureStartScale.x * factor); break;
            case ImportedGestureAxis.Y: scale.y = Mathf.Max(0.01f, _importedGestureStartScale.y * factor); break;
            case ImportedGestureAxis.Z: scale.z = Mathf.Max(0.01f, _importedGestureStartScale.z * factor); break;
            default:
                scale = _importedGestureStartScale * factor;
                scale = Vector3.Max(scale, new Vector3(0.01f, 0.01f, 0.01f));
                break;
        }
        t.localScale = scale;
        var axisLabel = _importedGestureAxis == ImportedGestureAxis.Free ? "" : " " + _importedGestureAxis;
        SetStatus($"Scale{axisLabel}: {factor:0.00}x — click/Enter to confirm, Esc to cancel.");
    }

    private void CancelImportedGesture()
    {
        var entry = _importedGestureEntry;
        if (entry?.Root != null)
        {
            entry.Root.transform.position = _importedGestureStartPosition;
            entry.Root.transform.eulerAngles = _importedGestureStartEuler;
            entry.Root.transform.localScale = _importedGestureStartScale;
        }
        EndImportedGesture(false);
    }

    private void EndImportedGesture(bool commit)
    {
        var entry = _importedGestureEntry;
        var kind = _importedGestureKind;
        var boundsBefore = _importedGestureBoundsBefore;
        _importedGestureKind = ImportedGestureKind.None;
        _importedGestureAxis = ImportedGestureAxis.Free;
        _importedGestureEntry = null;
        _importedGestureBoundsBefore = null;

        if (commit && entry?.Root != null)
        {
            // Deferred to exactly once per completed gesture, not once per
            // tick — see DrawUndoableVector3Field's own comment for why
            // doing this per-frame during a live drag would reintroduce the
            // same class of hang this session already fixed for the
            // Position/Rotation/Scale text fields.
            _terrainShell?.InvalidateLiveHeightGrid();
            NotifyImportedTerrainChanged(boundsBefore, ImportedEntryBounds(entry));
            SetStatus($"{kind} applied.");
        }
        else
        {
            SetStatus("Cancelled.");
        }
        RepaintGameView();
    }

    private void DrawDiagnosticsInspector()
    {
        DrawInspectorHeader("Diagnostics",
            "Diagnostics target vanilla import, BDF parsing, scene build and export validation.");
        if (DrawInspectorButton("Batch Import Vanilla Maps")) SetStatus("Batch import diagnostics queued.");
        if (DrawInspectorButton("Run BDF Parse Checks")) SetStatus("BDF parse checks queued.");
        if (DrawInspectorButton("Open Last Report")) ExecuteEditorMenu("SC2/Open Log File");
    }

    private void DrawToggle(string label, ref bool value)
    {
        value = GUILayout.Toggle(value, label, _dropdownToggleStyle);
    }

    private void UpdateLayoutRects(int width, int height)
    {
        // headerHeight now comes from the shared HeaderHeight constant (title bar + tabs + subtabs)
        // Objects' Units > Groups sub-tab specifically can need more than
        // the normal 240-300px range for its rows' inline action buttons
        // not to clip at the right edge. Computed directly (not from a
        // value the Groups tree left behind last time it drew) so the panel
        // is correctly sized on THIS same frame even right after a map
        // import switches away from the Objects tab before the tree ever
        // gets a chance to draw and report its own width need. Gated on
        // both the active main tab AND sub-tab (ComputeGroupsPanelMinWidth
        // itself also checks the Groups-vs-Units pill) so only Units >
        // Groups ever widens the panel — Armies/Props/Markers/Paths/Areas/
        // Chains, and the flat Units list, have no group tree to clip
        // against and must not stay stuck at a stale wide width just
        // because deep groups exist elsewhere. _objectsShell can still be
        // null here on the very first frame the Objects tab is ever opened
        // (EnsureObjectsShell only runs later this same frame, from
        // DrawObjectsInspector).
        // Panel width's own base fraction (0.30) scales up together with
        // SC2UiScale (View menu > UI Scale) — a bigger Scale means bigger
        // fonts/buttons need more room, and this ties both to the one same
        // parameter instead of a second, independent "panel width" setting.
        // NOTE: _objectsShell.ComputeGroupsPanelMinWidth's own row-width
        // formula (SC2ObjectsShell.cs, "depth * 14f + 280f") is NOT scaled
        // by SC2UiScale yet — left as a known follow-up, not fixed in this
        // pass. Its only effect if left unscaled is that a deep Units>Groups
        // tree's own extra width demand doesn't grow with Scale; the base
        // 0.30 fraction above still does.
        var minPanelWidthFloor = SC2UiScale.Pxf(240f);
        var minPanelWidth = _mainTab == 3 ? Mathf.Max(minPanelWidthFloor, _objectsShell?.ComputeGroupsPanelMinWidth(_subTab) ?? 0f) : minPanelWidthFloor;
        var maxPanelWidth = Mathf.Max(minPanelWidth, Mathf.Min(Mathf.Max(SC2UiScale.Pxf(272f), minPanelWidth), width * 0.6f));
        var panelWidth = Mathf.Clamp(width * 0.30f * SC2UiScale.Factor, minPanelWidth, maxPanelWidth);

        _panelRect = new Rect(0f, HeaderHeight, panelWidth, Mathf.Max(1f, height - HeaderHeight));
        // Hit-test region for the title bar's right-side cluster (map name +
        // mouse coordinates + status pill) — scaled the same as those labels'
        // own widths (DrawHeader) so clicks over the real (now wider at
        // higher Scale) cluster still register as "over the header," not the
        // viewport underneath it.
        var statusRectWidth = SC2UiScale.Pxf(620f);
        _statusRect = new Rect(width - statusRectWidth, 0, statusRectWidth, TitleBarHeight);
        _navigationGizmoRect = new Rect(width - 118, HeaderHeight + 10f, 104, 104);

        // Compact right-side panel — about as wide as the nav gizmo above
        // (104px + a small margin), spanning the full right-edge column from
        // the header to the bottom of the screen. The gizmo keeps its own
        // unchanged position/size and is simply drawn AFTER this panel, so
        // it visually floats over the panel's own top portion — no separate
        // gizmo-repositioning logic needed.
        var referencePanelWidth = ShowReferencePanel ? SC2UiScale.Pxf(118f) : 0f;
        _referencePanelRect = new Rect(width - referencePanelWidth, HeaderHeight, referencePanelWidth, Mathf.Max(1f, height - HeaderHeight));

        var left = ShowPanel ? panelWidth : 0f;
        var right = referencePanelWidth;
        _viewportRect = new Rect(left, HeaderHeight, Mathf.Max(1f, width - left - right), Mathf.Max(1f, height - HeaderHeight));
    }

    private void UpdateMouseMapCoordinates()
    {
        var cam = ResolveCamera();
        if (cam == null || Event.current == null)
        {
            _mouseMapCoordinates = "X -   Y -   Z -";
            return;
        }

        var mouse = Event.current.mousePosition;
        var ray = SC2TopViewMirror.ScreenPointToRay(cam, mouse);

        // Raycasting against a flat Y=0 plane to resolve the mouse's ground
        // X/Z, then sampling height at THAT X/Z, is only correct for a
        // dead-flat, zero-elevation map — for a camera looking down at an
        // angle (the normal case here), a Y=0 plane intersection drifts away
        // from the point actually under the cursor by an amount proportional
        // to how far the real surface height is from 0 (terrain above
        // water, hills, the default ~100 flat height, all qualify). The
        // drifted X/Z then gets a height sample from wherever it landed
        // instead of from the real point under the cursor — commonly a
        // nearby lower/water-covered spot, which reads as "Y only ever
        // shows the water height" rather than a water-exclusion bug.
        // TryRaycastLiveHeightGrid ray-marches against the actual
        // merged live+imported height surface (the same one Costs painting/
        // Objects placement already use), converging on the true X/Y/Z in
        // one pass instead of guessing X/Z first and sampling Y second.
        if (_terrainShell != null && _terrainShell.TryRaycastLiveHeightGrid(ray, out var hit))
        {
            _mouseMapCoordinates = string.Format(System.Globalization.CultureInfo.InvariantCulture, "X {0:0.0}   Y {1:0.0}   Z {2:0.0}", hit.x, hit.y, hit.z);
            return;
        }

        // Fallback for when no terrain shell/height grid exists yet (or the
        // ray misses the grid's bounds entirely) — flat Y=0 plane, same as
        // before, better than showing nothing.
        var ground = new Plane(Vector3.up, Vector3.zero);
        float enter;
        if (!ground.Raycast(ray, out enter))
        {
            _mouseMapCoordinates = "X -   Y -   Z -";
            return;
        }

        var p = ray.GetPoint(enter);
        _mouseMapCoordinates = string.Format(System.Globalization.CultureInfo.InvariantCulture, "X {0:0.0}   Y {1:0.0}   Z {2:0.0}", p.x, p.y, p.z);
    }

    // ── Navigation gizmo (Blender-style viewport axis widget) ───────────────
    // Six axis "balls" projected from the camera's current orientation onto a
    // 2D circle, like Blender's top-right viewport gizmo: solid colored discs
    // with a letter for +X/+Y/+Z, smaller hollow rings (no letter) for the
    // negative axes, no connecting lines and no boxed card behind them — just
    // the balls floating over a faint circular backdrop. All six are always
    // clickable regardless of which way they currently face (so you can click
    // a "far side" ring to snap the view there), and nearer balls are drawn
    // on top of farther ones for a correct sphere-like overlap.
    //
    // Not implemented (out of scope for this pass): Blender's extra 26-way
    // corner/edge click zones for isometric-style views — this keeps the
    // original 6-direction (X/Y/Z ±) navigation the tool already had.

    private struct GizmoAxis
    {
        public Vector2 Position;
        public float Depth;      // camera-space Z of the axis direction: >0 faces the camera
        public float Size;
        public Color Color;
        public string Label;
        public bool Solid;
        public System.Action Action;
    }

    private void DrawNavigationGizmo(int width)
    {
        // Defensive: EnsureStyles() already ran earlier this frame and should
        // have baked these, but if anything ever leaves them null (stale
        // instance state, a destroyed HideAndDontSave texture the sentinel
        // check didn't catch, etc.) skip drawing instead of NREing inside
        // GUI.DrawTexture/GUI.Label and aborting the rest of OnGUI for the frame.
        if (_gizmoBallSolid == null || _gizmoBallHollow == null || _gizmoBackdrop == null || _gizmoLabelStyle == null)
        {
            EnsureStyles();
            if (_gizmoBallSolid == null || _gizmoBallHollow == null || _gizmoBackdrop == null || _gizmoLabelStyle == null)
                return;
        }

        var cam = ResolveCamera();
        var r = _navigationGizmoRect;
        var center = new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);

        // A clearly visible grey disc behind the balls, like Blender's gizmo —
        // the earlier near-black/near-transparent version was easy to miss
        // entirely against a busy 3D viewport.
        var prevBackdropColor = GUI.color;
        GUI.color = new Color(0.20f, 0.21f, 0.23f, 0.6f);
        GUI.DrawTexture(CenteredRect(center, r.width), _gizmoBackdrop);
        GUI.color = prevBackdropColor;

        var axisX = new Color(0.847f, 0.208f, 0.271f, 1f);
        var axisY = new Color(0.522f, 0.769f, 0.184f, 1f);
        var axisZ = new Color(0.161f, 0.557f, 0.898f, 1f);
        var axisRadius = r.width * 0.36f;

        var axes = new[]
        {
            BuildGizmoAxis(cam, Vector3.right,   center, axisRadius, axisX, "X", true,  () => LookAlong(Vector3.left, Vector3.up, "X")),
            BuildGizmoAxis(cam, Vector3.left,    center, axisRadius, axisX, "",  false, () => LookAlong(Vector3.right, Vector3.up, "-X")),
            BuildGizmoAxis(cam, Vector3.up,      center, axisRadius, axisY, "Y", true,  () => LookAlong(Vector3.down, Vector3.forward, "Top")),
            BuildGizmoAxis(cam, Vector3.down,    center, axisRadius, axisY, "",  false, () => LookAlong(Vector3.up, Vector3.forward, "Bottom")),
            BuildGizmoAxis(cam, Vector3.forward, center, axisRadius, axisZ, "Z", true,  () => LookAlong(Vector3.back, Vector3.up, "Z")),
            BuildGizmoAxis(cam, Vector3.back,    center, axisRadius, axisZ, "",  false, () => LookAlong(Vector3.forward, Vector3.up, "-Z")),
        };

        // Depth-sort so nearer axis balls are drawn on top of farther ones,
        // instead of a fixed draw order that could look wrong from some angles.
        System.Array.Sort(axes, (a, b) => a.Depth.CompareTo(b.Depth));

        var e = Event.current;
        var hoverIndex = -1;
        for (var i = 0; i < axes.Length; i++)
        {
            if (e != null && CenteredRect(axes[i].Position, axes[i].Size + 6f).Contains(e.mousePosition))
                hoverIndex = i;
        }

        for (var i = 0; i < axes.Length; i++)
            DrawGizmoAxisBall(axes[i], i == hoverIndex);

        // Small center button — resets pan/zoom/rotation back to the default
        // top-down overview; Home does the same thing as a hotkey.
        var centerSize = r.width * 0.15f;
        var centerRect = CenteredRect(center, centerSize);
        var centerHover = hoverIndex < 0 && e != null && centerRect.Contains(e.mousePosition);
        var prevCenterColor = GUI.color;
        GUI.color = centerHover ? new Color(0.42f, 0.66f, 1f, 0.95f) : new Color(0.5f, 0.53f, 0.58f, 0.8f);
        GUI.DrawTexture(centerRect, _gizmoBallSolid);
        GUI.color = prevCenterColor;

        if (e != null && e.type == EventType.MouseDown && e.button == 0)
        {
            if (hoverIndex >= 0)
            {
                axes[hoverIndex].Action?.Invoke();
                e.Use();
            }
            else if (centerHover)
            {
                ResetView();
                e.Use();
            }
        }
    }

    private static GizmoAxis BuildGizmoAxis(Camera cam, Vector3 worldAxis, Vector2 center, float radius, Color color, string label, bool solid, System.Action action)
    {
        var local = cam != null
            ? cam.transform.InverseTransformDirection(worldAxis.normalized)
            : worldAxis.normalized;

        // IMPORTANT: do NOT normalize (local.x, -local.y) onto the circle
        // boundary. That was the bug that made the gizmo look broken/only
        // half its balls visible: it forced every axis onto the same fixed
        // ring regardless of how it's actually facing, so whenever the
        // camera looked straight down (or along) any single world axis — the
        // terrain editor's default view! — that axis's ball collapsed via a
        // "too close to center" fallback onto the SAME screen position as
        // whichever other axis happened to occupy that angle, hiding one
        // completely behind the other (only 4 of 6 balls ever showed).
        // Using the raw (unnormalized) local x/y — exactly what Blender's own
        // gizmo does — makes a ball smoothly slide toward the center as it
        // becomes more axis-aligned with the view, and only ever reaches
        // dead-center when it's the one unique axis the camera is looking
        // straight along, so no two balls can ever land on the same spot.
        var depth = local.z;
        var position = center + new Vector2(local.x, -local.y) * radius;

        var baseSize = solid ? 21f : 15f;
        var size = baseSize * Mathf.Lerp(0.88f, 1.12f, (depth + 1f) * 0.5f);

        return new GizmoAxis
        {
            Position = position,
            Depth = depth,
            Size = size,
            Color = color,
            Label = label,
            Solid = solid,
            Action = action
        };
    }

    private void DrawGizmoAxisBall(GizmoAxis axis, bool hover)
    {
        var size = axis.Size * (hover ? 1.18f : 1f);
        var color = hover ? Color.Lerp(axis.Color, Color.white, 0.35f) : axis.Color;
        if (axis.Depth < 0f)
            color.a *= 0.82f; // subtle "facing away" depth cue — still fully clickable

        var previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(CenteredRect(axis.Position, size), axis.Solid ? _gizmoBallSolid : _gizmoBallHollow);
        GUI.color = previousColor;

        if (axis.Solid && !string.IsNullOrEmpty(axis.Label) && _gizmoLabelStyle != null)
            GUI.Label(CenteredRect(axis.Position, size), axis.Label, _gizmoLabelStyle);
    }

    private static Rect CenteredRect(Vector2 center, float size)
    {
        return new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size);
    }

    private void SetTool(string toolName)
    {
        _tool = toolName;
        SetStatus("Tool selected: " + toolName);
    }

    /// <summary>Shared by the Edit menu item, the View menu's own Toggle
    /// (see DrawViewMenu — its pressed state IS "is Measure active"), and
    /// the "M" hotkey, so all three agree on the exact same on/off
    /// semantics: activating clears any in-progress (not yet locked) start
    /// point from a previous session; deactivating just switches back to
    /// Select without also forcing a reset, so re-activating later resumes
    /// wherever the user left off (including every locked measurement)
    /// instead of always starting fresh.</summary>
    private void ToggleMeasureTool()
    {
        if (_tool == "Measure")
        {
            SetTool("Select");
            return;
        }
        SetTool("Measure");
        _measureHasStart = false;
        _draggingMeasurementEndpoint = -1;
        SetStatus("Measure: click to set start point.");
    }

    /// <summary>The Measure tool's entire input+draw loop — a "Global" tool
    /// (Edit menu / View menu / "M" hotkey), not tied to whichever main
    /// tab/shell is otherwise active, so it lives here rather than inside
    /// any one shell.
    ///
    /// Click 1 sets the start point; while no end point is locked yet, a
    /// live line + distance follows the cursor; click 2 locks the end point
    /// into a new entry in _measurements (kept — every earlier measurement
    /// stays visible, not just the most recent one) and immediately arms a
    /// fresh start point for the next click. Distance is XZ-only (world
    /// height/Y deliberately excluded, per the feature request this
    /// implements) — matches how map distances/placement grids are reasoned
    /// about everywhere else in this editor (SnapXZ, GetSymmetricPositions'
    /// own "Y untouched" convention).
    ///
    /// Clicking near an existing locked measurement's endpoint marker
    /// selects it and starts dragging that endpoint (released on MouseUp);
    /// clicking near its line body (not an endpoint) just selects it,
    /// without starting a fresh in-progress measurement. Delete/Backspace
    /// removes the selected measurement. Escape cancels an in-progress
    /// endpoint drag, then clears the selection, then cancels an
    /// in-progress (not yet locked) measurement, then exits the tool
    /// entirely (back to Select) — each press handling exactly one of those
    /// in order. Consumes (e.Use()) any click/key it handles so the active
    /// tab's own paint/place/select handlers never also fire on the same
    /// input — see this method's call site's own comment for why it must
    /// run first, before those.</summary>
    private void HandleMeasureToolGameViewInput()
    {
        if (_tool != "Measure") return;
        var camera = ResolveCamera();
        if (camera == null) return;

        var e = Event.current;
        if (e != null && e.type != EventType.Used && _viewportRect.Contains(e.mousePosition))
        {
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (_draggingMeasurementEndpoint >= 0) _draggingMeasurementEndpoint = -1;
                else if (_selectedMeasurement >= 0) { _selectedMeasurement = -1; SetStatus("Measure: click to set start point, or click an existing measurement to select it."); }
                else if (_measureHasStart) { _measureHasStart = false; SetStatus("Measure: click to set start point."); }
                else SetTool("Select");
                e.Use();
            }
            else if ((e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace))
                     && _selectedMeasurement >= 0 && _draggingMeasurementEndpoint < 0)
            {
                _measurements.RemoveAt(_selectedMeasurement);
                _selectedMeasurement = -1;
                SetStatus("Measurement deleted.");
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && _draggingMeasurementEndpoint >= 0
                     && _selectedMeasurement >= 0 && _selectedMeasurement < _measurements.Count
                     && TryGetMeasureHit(camera, e.mousePosition, out var draggedTo))
            {
                var m = _measurements[_selectedMeasurement];
                var pt = _draggingMeasurementEndpoint == 0 ? m.Start : m.End;
                // Mutates the SHARED point object in place — any other
                // measurement whose Start/End also references this same
                // instance moves right along with it, live during the drag.
                pt.Position = draggedTo;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _draggingMeasurementEndpoint >= 0)
            {
                // Dropping the dragged endpoint near a DIFFERENT existing
                // point merges into it — this measurement's Start/End field
                // now references that shared point object directly (any
                // other measurement already using the point being LEFT
                // behind keeps using it, unaffected, at wherever the drag
                // left it).
                if (_selectedMeasurement >= 0 && _selectedMeasurement < _measurements.Count)
                {
                    var m = _measurements[_selectedMeasurement];
                    var current = _draggingMeasurementEndpoint == 0 ? m.Start : m.End;
                    if (TryFindNearestMeasurementPoint(camera, e.mousePosition, current, out var target))
                    {
                        if (_draggingMeasurementEndpoint == 0) m.Start = target; else m.End = target;
                        SetStatus("Measure: connected to existing point.");
                    }
                }
                _draggingMeasurementEndpoint = -1;
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                // Selecting/dragging an existing measurement only makes sense
                // when nothing is currently being placed — while an in-progress
                // measurement is awaiting its end point (_measureHasStart), any
                // click always completes THAT measurement instead. Without this
                // guard, aiming the second point of a new measurement at an
                // existing measurement's endpoint silently selected that
                // existing measurement instead of finishing the new one there
                // (a real reported bug) — it falls through to the snap-to-point
                // branch below instead, which is what actually connects the two
                // measurements' points.
                if (!_measureHasStart && TryHitMeasurementEndpoint(camera, e.mousePosition, out var endpointMeasurement, out var endpoint))
                {
                    _selectedMeasurement = endpointMeasurement;
                    _draggingMeasurementEndpoint = endpoint;
                    SetStatus("Measure: dragging endpoint — release to set. Delete to remove this measurement.");
                    e.Use();
                }
                else if (!_measureHasStart && TryHitMeasurementLine(camera, e.mousePosition, out var lineMeasurement))
                {
                    _selectedMeasurement = lineMeasurement;
                    var picked = _measurements[lineMeasurement];
                    var pickedDistance = Vector2.Distance(new Vector2(picked.Start.Position.x, picked.Start.Position.z), new Vector2(picked.End.Position.x, picked.End.Position.z));
                    SetStatus($"Measure: selected ({pickedDistance:0.00} u). Drag an endpoint to edit, Delete to remove.");
                    e.Use();
                }
                else if (TryGetMeasureHit(camera, e.mousePosition, out var clicked))
                {
                    // Snap onto an existing measurement's endpoint when close
                    // enough, REUSING that same SC2MeasurePoint object (not
                    // just copying its coordinates) — this is what actually
                    // connects the two measurements: moving the shared point
                    // later moves both.
                    var pointObj = TryFindNearestMeasurementPoint(camera, e.mousePosition, null, out var snapped)
                        ? snapped : new SC2MeasurePoint { Position = clicked };
                    _selectedMeasurement = -1;
                    if (!_measureHasStart)
                    {
                        _measureStartPoint = pointObj;
                        _measureHasStart = true;
                        SetStatus("Measure: click to set end point (Esc to cancel).");
                    }
                    else
                    {
                        _measurements.Add(new SC2Measurement { Start = _measureStartPoint, End = pointObj });
                        var lockedDistance = Vector2.Distance(new Vector2(_measureStartPoint.Position.x, _measureStartPoint.Position.z), new Vector2(pointObj.Position.x, pointObj.Position.z));
                        _measureHasStart = false;
                        SetStatus($"Measure: {lockedDistance:0.00} units (XZ). Click to start a new measurement, or click an existing one to select it.");
                    }
                    e.Use();
                }
            }
        }

        for (var i = 0; i < _measurements.Count; i++)
            DrawMeasureOverlay(camera, _measurements[i].Start.Position, _measurements[i].End.Position, isLive: false, selected: i == _selectedMeasurement);

        if (!_measureHasStart) return;
        if (e == null || !_viewportRect.Contains(e.mousePosition) || !TryGetMeasureHit(camera, e.mousePosition, out var endPoint))
            endPoint = _measureStartPoint.Position;
        // Preview the same snap the actual click will apply, so the line
        // visibly connects to an existing measurement's point WHILE aiming,
        // not just after clicking.
        else if (TryFindNearestMeasurementPoint(camera, e.mousePosition, null, out var snappedPreview))
            endPoint = snappedPreview.Position;
        DrawMeasureOverlay(camera, _measureStartPoint.Position, endPoint, isLive: true, selected: false);
    }

    /// <summary>Screen-space search for the nearest existing measurement
    /// endpoint to snap a new/completing/dragged point onto, so two
    /// measurements can share the exact same SC2MeasurePoint object instead
    /// of two points that merely look close together on screen. Same pixel
    /// radius as TryHitMeasurementEndpoint for consistency, but returns the
    /// POINT OBJECT to attach to (not a copied position) — this is used
    /// while actively placing/dragging a point, not selecting.
    /// <paramref name="exclude"/> is the point currently being placed/
    /// dragged, if any (pass null when there isn't one, e.g. placing a
    /// brand new measurement's first point) — skipped so a drag can't
    /// trivially "snap" onto the very point it's already holding.</summary>
    private bool TryFindNearestMeasurementPoint(Camera camera, Vector2 guiMousePosition, SC2MeasurePoint exclude, out SC2MeasurePoint found)
    {
        const float snapPixelRadius = 10f;
        found = null;
        var best = snapPixelRadius;
        foreach (var m in _measurements)
        {
            if (m.Start != exclude)
            {
                var d = Vector2.Distance(guiMousePosition, SC2TerrainShell.WorldToGameGui(camera, _viewportRect, m.Start.Position));
                if (d < best) { best = d; found = m.Start; }
            }
            if (m.End != exclude)
            {
                var d = Vector2.Distance(guiMousePosition, SC2TerrainShell.WorldToGameGui(camera, _viewportRect, m.End.Position));
                if (d < best) { best = d; found = m.End; }
            }
        }
        return found != null;
    }

    /// <summary>Screen-space hit test against every locked measurement's two
    /// endpoint markers — used to pick which measurement AND which of its
    /// endpoints a click should start dragging. Picks the single closest
    /// endpoint across all measurements within HitPixelRadius, so an
    /// overlapping pair of measurements always resolves to exactly one.</summary>
    private bool TryHitMeasurementEndpoint(Camera camera, Vector2 guiMousePosition, out int measurementIndex, out int endpoint)
    {
        const float hitPixelRadius = 10f;
        measurementIndex = -1; endpoint = -1;
        var best = hitPixelRadius;
        for (var i = 0; i < _measurements.Count; i++)
        {
            var m = _measurements[i];
            var startDist = Vector2.Distance(guiMousePosition, SC2TerrainShell.WorldToGameGui(camera, _viewportRect, m.Start.Position));
            if (startDist < best) { best = startDist; measurementIndex = i; endpoint = 0; }
            var endDist = Vector2.Distance(guiMousePosition, SC2TerrainShell.WorldToGameGui(camera, _viewportRect, m.End.Position));
            if (endDist < best) { best = endDist; measurementIndex = i; endpoint = 1; }
        }
        return measurementIndex >= 0;
    }

    /// <summary>Screen-space hit test against every locked measurement's own
    /// line body (not its endpoints — TryHitMeasurementEndpoint already
    /// covers those, and is checked first, so a click right on an endpoint
    /// never falls through to just selecting the line instead of dragging
    /// it). Picks the single closest line within HitPixelRadius.</summary>
    private bool TryHitMeasurementLine(Camera camera, Vector2 guiMousePosition, out int measurementIndex)
    {
        const float hitPixelRadius = 6f;
        measurementIndex = -1;
        var best = hitPixelRadius;
        for (var i = 0; i < _measurements.Count; i++)
        {
            var m = _measurements[i];
            var startGui = SC2TerrainShell.WorldToGameGui(camera, _viewportRect, m.Start.Position);
            var endGui = SC2TerrainShell.WorldToGameGui(camera, _viewportRect, m.End.Position);
            var dist = DistancePointToGuiSegment(guiMousePosition, startGui, endGui);
            if (dist < best) { best = dist; measurementIndex = i; }
        }
        return measurementIndex >= 0;
    }

    private static float DistancePointToGuiSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Liang-Barsky line clip — shortens segment a-b down to just
    /// the portion inside _viewportRect (returns false, with a/b unchanged,
    /// when none of it is visible). Used by DrawMeasureOverlay so a
    /// measurement's line/label never render on top of the left panel/
    /// header, which visually covers part of the same camera image the
    /// Game View renders behind it.</summary>
    private bool ClipGuiSegmentToViewport(Vector2 a, Vector2 b, out Vector2 clippedA, out Vector2 clippedB)
        => SC2TerrainShell.ClipSegmentToViewport(_viewportRect, a, b, out clippedA, out clippedB);

    /// <summary>Same imported-terrain-first, live-procedural-fallback
    /// raycast order every other placement/painting tool in this file
    /// already uses (see e.g. EnsureObjectsShell's GetCameraFocusPoint).</summary>
    private bool TryGetMeasureHit(Camera camera, Vector2 guiMousePosition, out Vector3 hit)
    {
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, _viewportRect, guiMousePosition);
        if (SC2TerrainShell.TryRaycastImportedTerrain(BuildImportedTerrainColliders(), ray, out hit))
            return true;
        if (_terrainShell != null && SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
            return true;
        hit = default;
        return false;
    }

    private void DrawMeasureOverlay(Camera camera, Vector3 start, Vector3 end, bool isLive, bool selected)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        var startGui = SC2TerrainShell.WorldToGameGui(camera, _viewportRect, start);
        var endGui = SC2TerrainShell.WorldToGameGui(camera, _viewportRect, end);
        // Live (in-progress, not yet locked) stays the original amber;
        // selected (locked, click-picked for editing/deletion) gets its own
        // distinct color so it's obvious which measurement Delete/dragging
        // would affect; every other locked measurement keeps the original
        // green.
        var lineColor = isLive ? new Color(1f, 0.85f, 0.2f, 0.9f)
            : selected ? new Color(0.4f, 0.75f, 1f, 0.95f)
            : new Color(0.3f, 1f, 0.55f, 0.95f);

        // The camera renders behind the WHOLE Game View — the left panel/
        // header are just opaque UI drawn on top of part of that same image
        // — so an endpoint sitting behind them still has a valid (but
        // off-limits) GUI position. DrawSymmetryPointMarker already skips
        // itself when its point falls outside _viewportRect; DrawGuiLineSegment/
        // DrawFloatingLabel don't (they're shared primitives used elsewhere
        // that assume the caller already stayed in-bounds), so this method
        // clips its own line to the visible rect and only labels the visible
        // portion, instead of drawing straight over the panel.
        if (!ClipGuiSegmentToViewport(startGui, endGui, out var clippedStart, out var clippedEnd)) return;

        SC2TerrainShell.DrawGuiLineSegment(clippedStart, clippedEnd, lineColor, selected ? 3f : 2f);
        var markerSize = selected ? 11f : 8f;
        SC2TerrainShell.DrawSymmetryPointMarker(camera, _viewportRect, start, Color.white, pixelSize: markerSize);
        SC2TerrainShell.DrawSymmetryPointMarker(camera, _viewportRect, end, Color.white, pixelSize: markerSize);

        var distanceXz = Vector2.Distance(new Vector2(start.x, start.z), new Vector2(end.x, end.z));
        var midGui = (clippedStart + clippedEnd) * 0.5f;
        SC2TerrainShell.DrawFloatingLabel(midGui, $"{distanceXz:0.00} u", lineColor);
    }

    /// <summary>View menu's "Show Map Border" — a configurable Width x
    /// Height rectangle (world/XZ units) centered on the map's own playable
    /// footprint, drawn as a terrain-hugging polyline (subdivided per edge,
    /// each point's Y re-sampled from the live terrain — same technique
    /// SC2TerrainShell.DrawGameViewBrush's sampleHeight path already uses
    /// for the brush ring) so it reads as a real ground-level boundary
    /// marker rather than a flat plane floating over hills/sinking into
    /// valleys. A pure IMGUI overlay drawn after the 3D camera render (like
    /// every other cursor/marker in this file), so it's always on top of
    /// the terrain by construction — no depth test to fight.</summary>
    private void DrawMapFrameOverlay()
    {
        if (!_showMapFrame) return;
        var e = Event.current;
        if (e == null || e.type != EventType.Repaint) return;
        var camera = ResolveCamera();
        if (camera == null || _terrainShell == null) return;
        var data = _terrainShell.Data;
        if (data == null) return;

        var halfW = Mathf.Max(0.01f, _mapFrameWidth) * 0.5f;
        var halfH = Mathf.Max(0.01f, _mapFrameHeight) * 0.5f;
        var centerX = data.CostsGridSize * 0.5f;
        var centerZ = data.CostsGridSize * 0.5f;

        var corners = new[]
        {
            new Vector2(centerX - halfW, centerZ - halfH),
            new Vector2(centerX + halfW, centerZ - halfH),
            new Vector2(centerX + halfW, centerZ + halfH),
            new Vector2(centerX - halfW, centerZ + halfH),
        };

        const int segmentsPerEdge = 24;
        var color = new Color(1f, 0.2f, 0.85f, 0.95f);
        // Vector3, not Vector2: .z is SC2TopViewMirror.WorldToGui's own camera-space depth
        // (positive = in front of the camera) — see the skip check below for why this is
        // load-bearing, not just carried along for no reason.
        Vector3? previousGui = null;
        for (var edge = 0; edge < 4; edge++)
        {
            var a = corners[edge];
            var b = corners[(edge + 1) % 4];
            var startIndex = edge == 0 ? 0 : 1; // corner 0 of this edge == last corner of the previous one, already placed
            for (var i = startIndex; i <= segmentsPerEdge; i++)
            {
                var t = i / (float)segmentsPerEdge;
                var worldX = Mathf.Lerp(a.x, b.x, t);
                var worldZ = Mathf.Lerp(a.y, b.y, t);
                var worldY = _terrainShell.SampleLiveRuntimeHeight(worldX, worldZ);
                var guiPoint = SC2TopViewMirror.WorldToGui(camera, new Vector3(worldX, worldY, worldZ));
                // A point BEHIND the camera (guiPoint.z <= 0) still projects to a valid-looking
                // on-screen X/Y — Camera.WorldToScreenPoint mirrors it to roughly the opposite
                // side of the screen rather than returning something ClipSegmentToViewport's
                // plain 2D rect test could reject. With a rectangle this large (the whole map),
                // a steep-enough camera angle puts one or more corners behind the camera, and
                // the segment to/from that corner then draws as a long diagonal line straight
                // across the screen instead of being clipped away — confirmed real (reported
                // screenshot: an X of magenta lines crossing the whole Game View). Skipping any
                // segment with a behind-camera endpoint avoids the mirrored point entirely,
                // rather than trying to clip a line that is only valid in front of the camera.
                if (previousGui.HasValue && previousGui.Value.z > 0f && guiPoint.z > 0f &&
                    SC2TerrainShell.ClipSegmentToViewport(_viewportRect, previousGui.Value, guiPoint, out var clippedA, out var clippedB))
                    SC2TerrainShell.DrawGuiLineSegment(clippedA, clippedB, color, 3f);
                previousGui = guiPoint;
            }
        }
    }

    /// <summary>View menu's "Load Reference Image...". Reuses
    /// SC2CustomTextureImporter.TryLoad — the same general-purpose "load any
    /// image format off disk" helper the custom brush/decal pickers already
    /// use, so PNG/JPG/DDS all work here too, not just one format.</summary>
    private void LoadReferenceImage()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Load Reference Image", "", "png,jpg,jpeg,dds");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Load Reference Image", "Image (*.png;*.jpg;*.jpeg;*.dds)", "png;*.jpg;*.jpeg;*.dds");
#endif
        if (string.IsNullOrEmpty(path)) return;
        if (!SC2CustomTextureImporter.TryLoad(path, out var tex, out var error))
        {
            SetStatus("Reference image failed to load: " + error);
            return;
        }
        if (_referenceOverlayTex != null) UnityEngine.Object.Destroy(_referenceOverlayTex);
        _referenceOverlayTex = tex;
        _showReferenceOverlay = true;
        _referenceCover = Vector2.zero;
        ApplyReferenceOverlayState();
        SetStatus("Reference image loaded: " + System.IO.Path.GetFileName(path));
    }

    /// <summary>Pushes the current _referenceOverlayTex/_showReferenceOverlay
    /// state onto the live SC2TerrainRuntime — called right after loading/
    /// toggling, and again on SC2TerrainShell.DataChanged (see
    /// EnsureTerrainShell's subscription), since a map load/switch rebuilds
    /// Runtime's own shared chunk material from scratch, resetting its
    /// shader properties back to defaults regardless of what this class's
    /// own fields still say.</summary>
    private void ApplyReferenceOverlayState()
    {
        var runtime = _terrainShell?.Runtime;
        if (runtime == null) return;
        var worldSize = ReferenceMapWorldSize();
        var zoom = new Vector2(_referenceCover.x > 0f ? worldSize.x / _referenceCover.x : 1f, _referenceCover.y > 0f ? worldSize.y / _referenceCover.y : 1f);
        runtime.SetReferenceOverlay(_referenceOverlayTex, _showReferenceOverlay, worldSize, ReferenceOpacity, zoom);
    }

#if !UNITY_EDITOR
    private const int LoadTerrainDropdownId = 0x5C2A1CAD;
    private const int DeleteTerrainDropdownId = 0x5C2A1CAE;
    private const int RestoreBackupDropdownId = 0x5C2A1CAF;
#endif

    private void OpenLoadTerrainMenu()
    {
#if !UNITY_EDITOR
        var names = SC2TerrainShell.ListSavedTerrains();
        if (names.Count == 0)
        {
            SetStatus("No saved terrains found on disk.");
            return;
        }
        var anchor = new Rect(_fileMenuRect.x, TitleBarHeight + 4f, 220f, TitleBarHeight);
        SC2RuntimeDropdown.Toggle(LoadTerrainDropdownId, names.ToArray(), anchor);
#else
        SetStatus("Load Terrain from disk only applies to standalone builds — in the Editor, assign a Terrain Data asset directly.");
#endif
    }

    private void OpenDeleteTerrainMenu()
    {
#if !UNITY_EDITOR
        var names = SC2TerrainShell.ListSavedTerrains();
        if (names.Count == 0)
        {
            SetStatus("No saved terrains found on disk.");
            return;
        }
        var anchor = new Rect(_fileMenuRect.x, TitleBarHeight + 4f, 220f, TitleBarHeight);
        SC2RuntimeDropdown.Toggle(DeleteTerrainDropdownId, names.ToArray(), anchor);
#else
        SetStatus("Delete Saved Terrain only applies to standalone builds — in the Editor, delete the .asset file from the Project window.");
#endif
    }

    private void OpenRestoreBackupMenu()
    {
#if !UNITY_EDITOR
        var mapName = _terrainShell?.Data?.MapName;
        if (string.IsNullOrEmpty(mapName))
        {
            SetStatus("No active terrain.");
            return;
        }
        var backups = SC2RuntimeTerrainPersistence.ListBackups(mapName);
        if (backups.Count == 0)
        {
            SetStatus($"No backups found for '{mapName}' (see View > Autosave > Backups to keep).");
            return;
        }
        var anchor = new Rect(_fileMenuRect.x, TitleBarHeight + 4f, 220f, TitleBarHeight);
        SC2RuntimeDropdown.Toggle(RestoreBackupDropdownId, backups.ToArray(), anchor);
#else
        SetStatus("Backups only apply to standalone builds — in the Editor, saving goes through the asset database, not the backup rotation.");
#endif
    }

    private void RequestNewTerrain()
    {
#if UNITY_EDITOR
        if (_terrainShell == null || _terrainShell.Data == null ||
            EditorUtility.DisplayDialog("New Terrain", "Start a new terrain? Any unsaved changes to the current one will be lost.", "Yes", "Cancel"))
        {
            ApplyNewTerrain();
        }
#else
        if (_terrainShell == null || _terrainShell.Data == null)
        {
            ApplyNewTerrain();
            return;
        }
        SC2RuntimeConfirm.Open(
            "New Terrain",
            "Start a new terrain? Any unsaved changes to the current one will be lost.",
            "New",
            ApplyNewTerrain);
#endif
    }

    private void ApplyNewTerrain()
    {
        EnsureTerrainShell();
        _terrainShell?.NewTerrain();
        // A previous map's imported terrain.bdf GameObjects/list would
        // otherwise linger in the scene indefinitely — NewTerrain/SetData
        // never cleared this list at all before this fix (same gap
        // RestoreImportedTerrainsFromDocument's own doc comment describes
        // for Load). No entries to restore for a genuinely blank map.
        RestoreImportedTerrainsFromDocument(null);
        RuntimeTerrainData = null;
        SetStatus("Started a new terrain.");
    }

#if !UNITY_EDITOR
    private void ApplyLoadedTerrainName(string mapName)
    {
        EnsureTerrainShell();
        if (_terrainShell != null && _terrainShell.LoadFromDisk(mapName, out var loadedDocument))
        {
            RuntimeTerrainData = _terrainShell.Data;
            RestoreImportedTerrainsFromDocument(loadedDocument?.Terrain?.ImportedEntries);
            // The imported terrains exist now, after the data-changed notification that already told the decals to rebuild: the height
            // grid and the decals' projection surfaces must be taken again so the decals are clipped against the imported models too,
            // not only against the core terrain (which lies under them).
            _terrainShell.InvalidateLiveHeightGrid();
            _decalsShell?.InvalidatePreviewCache();
            ResetView(instant: true); // same reasoning as AdoptNewlyCreatedTerrainData's own call
            SetStatus("Loaded terrain: " + mapName);
        }
        else
        {
            SetStatus("Failed to load terrain: " + mapName);
        }
    }

    private void RequestDeleteTerrainName(string mapName)
    {
        SC2RuntimeConfirm.Open(
            "Delete Saved Terrain",
            $"Permanently delete the saved terrain '{mapName}'? This cannot be undone.",
            "Delete",
            () => ApplyDeleteTerrainName(mapName));
    }

    private void ApplyDeleteTerrainName(string mapName)
    {
        EnsureTerrainShell();
        if (_terrainShell != null && _terrainShell.DeleteFromDisk(mapName))
        {
            if (_terrainShell.Data == null)
                RuntimeTerrainData = null;
            SetStatus("Deleted terrain: " + mapName);
        }
        else
        {
            SetStatus("Failed to delete terrain: " + mapName);
        }
    }

    /// <summary>No confirmation dialog — unlike Delete, this can't destroy anything.</summary>
    private void RequestDuplicateTerrainName(string mapName)
    {
        var newName = GetUniqueDuplicateName(mapName);
        if (SC2TerrainShell.DuplicateOnDisk(mapName, newName))
            SetStatus($"Duplicated terrain: {mapName} -> {newName}");
        else
            SetStatus("Failed to duplicate terrain: " + mapName);
    }

    /// <summary>backupId doubles as its own label — see
    /// SC2RuntimeTerrainPersistence.BackupStampFormat's own doc comment.</summary>
    private void RequestRestoreBackup(string backupId)
    {
        var mapName = _terrainShell?.Data?.MapName;
        if (string.IsNullOrEmpty(mapName)) return;
        SC2RuntimeConfirm.Open(
            "Restore Backup",
            $"Restore '{mapName}' to the backup from {backupId.Replace('_', ' ')}? " +
            "This overwrites the currently saved file on disk (your in-memory unsaved edits are untouched until you reload).",
            "Restore",
            () => ApplyRestoreBackup(mapName, backupId));
    }

    private void ApplyRestoreBackup(string mapName, string backupId)
    {
        if (SC2RuntimeTerrainPersistence.RestoreBackup(mapName, backupId))
        {
            ApplyLoadedTerrainName(mapName); // reload so the in-memory state matches what was just restored on disk
            SetStatus($"Restored backup from {backupId.Replace('_', ' ')}.");
        }
        else
        {
            SetStatus("Failed to restore backup (see log).");
        }
    }
#endif

    /// <summary>One candidate for the global Undo/Redo dispatcher below — a
    /// shell's HasUndo/HasRedo + LastActionTimestamp (see each shell's
    /// identical property doc comment) plus the actual Undo()/Redo() delegate
    /// to invoke if it wins. Without this, the global Ctrl+Z/Y shortcut would
    /// only ever call _terrainShell.Undo()/Redo() — every other domain's
    /// edits (Objects/Costs/Collision2/Scenario/Environment) would only be
    /// reachable through that domain's OWN on-screen button, never through
    /// the keyboard shortcut at all.</summary>
    private readonly struct UndoRedoCandidate
    {
        public readonly bool Available;
        public readonly long Timestamp;
        public readonly System.Action Invoke;

        public UndoRedoCandidate(bool available, long timestamp, System.Action invoke)
        {
            Available = available;
            Timestamp = timestamp;
            Invoke = invoke;
        }
    }

    private bool DispatchToMostRecent(UndoRedoCandidate[] candidates)
    {
        var bestIndex = -1;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (!candidates[i].Available) continue;
            if (bestIndex == -1 || candidates[i].Timestamp > candidates[bestIndex].Timestamp)
                bestIndex = i;
        }
        if (bestIndex == -1) return false;
        candidates[bestIndex].Invoke();
        return true;
    }

    private void UndoTerrainEdit()
    {
        var candidates = new[]
        {
            new UndoRedoCandidate(_terrainShell != null && _terrainShell.CanUndo, _terrainShell?.LastActionTimestamp ?? 0, () => _terrainShell.Undo()),
            new UndoRedoCandidate(_objectsShell != null && _objectsShell.HasUndo, _objectsShell?.LastActionTimestamp ?? 0, () => _objectsShell.DoUndo()),
            new UndoRedoCandidate(_costsShell != null && _costsShell.HasUndo, _costsShell?.LastActionTimestamp ?? 0, () => _costsShell.Undo()),
            new UndoRedoCandidate(_collision2Shell != null && _collision2Shell.HasUndo, _collision2Shell?.LastActionTimestamp ?? 0, () => _collision2Shell.Undo()),
            new UndoRedoCandidate(_scenarioShell != null && _scenarioShell.HasUndo, _scenarioShell?.LastActionTimestamp ?? 0, () => _scenarioShell.Undo()),
            new UndoRedoCandidate(_environmentShell != null && _environmentShell.HasUndo, _environmentShell?.LastActionTimestamp ?? 0, () => _environmentShell.Undo()),
            // Decals commands are real ISC2DocumentCommand entries on the
            // shared history, so this dispatcher must ask the Decals shell
            // whether it holds the most recent action too.
            new UndoRedoCandidate(_decalsShell != null && _decalsShell.HasUndo, _decalsShell?.LastActionTimestamp ?? 0, () => _decalsShell.Undo()),
            // Imported terrain's local undo stack needs its own timestamp
            // candidate here too, same class of gap as Decals above.
            new UndoRedoCandidate(ImportedTerrainsHasUndo, ImportedTerrainsLastActionTimestamp, ImportedTerrainsUndo),
        };
        SetStatus(DispatchToMostRecent(candidates) ? "Undo" : "Nothing to undo.");
    }

    private void RedoTerrainEdit()
    {
        var candidates = new[]
        {
            // Terrain/Objects/Costs each keep their own SC2ShellUndoCoordinator
            // (legacy snapshots interleaved with real document commands) —
            // LastRedoTimestamp, not LastActionTimestamp, is the correct
            // signal here since Redo must replay in exact reverse of
            // whatever was just undone; see SC2ShellUndoCoordinator.
            // LastRedoTimestamp's own doc comment for why they differ.
            new UndoRedoCandidate(_terrainShell != null && _terrainShell.CanRedo, _terrainShell?.LastRedoTimestamp ?? 0, () => _terrainShell.Redo()),
            new UndoRedoCandidate(_objectsShell != null && _objectsShell.HasRedo, _objectsShell?.LastRedoTimestamp ?? 0, () => _objectsShell.DoRedo()),
            new UndoRedoCandidate(_costsShell != null && _costsShell.HasRedo, _costsShell?.LastRedoTimestamp ?? 0, () => _costsShell.Redo()),
            new UndoRedoCandidate(_collision2Shell != null && _collision2Shell.HasRedo, _collision2Shell?.LastActionTimestamp ?? 0, () => _collision2Shell.Redo()),
            new UndoRedoCandidate(_scenarioShell != null && _scenarioShell.HasRedo, _scenarioShell?.LastActionTimestamp ?? 0, () => _scenarioShell.Redo()),
            new UndoRedoCandidate(_environmentShell != null && _environmentShell.HasRedo, _environmentShell?.LastActionTimestamp ?? 0, () => _environmentShell.Redo()),
            new UndoRedoCandidate(_decalsShell != null && _decalsShell.HasRedo, _decalsShell?.LastActionTimestamp ?? 0, () => _decalsShell.Redo()),
            new UndoRedoCandidate(ImportedTerrainsHasRedo, ImportedTerrainsLastRedoTimestamp, ImportedTerrainsRedo),
        };
        SetStatus(DispatchToMostRecent(candidates) ? "Redo" : "Nothing to redo.");
    }

    private void SaveActiveTerrain()
    {
        // SyncImportedTerrainsToDocument runs via SC2TerrainShell.PreSaveSync
        // (wired in EnsureTerrainShell), covering every SaveState call site —
        // this button, Ctrl+S, and the Terrain tab's own "Save Terrain" button.
        if (_terrainShell != null && _terrainShell.RequestSave())
            SetStatus("Terrain saved.");
        else
            SetStatus("No terrain to save.");
    }

#if !UNITY_EDITOR
    // The subscribe-once-per-process guard in OnEnable alone does not
    // prevent stacked dialogs — the remaining failure mode is re-entrancy,
    // not multi-subscription: SC2RuntimeQuitConfirm.Ask blocks
    // on a real native MessageBoxW call, but the underlying OS window keeps
    // receiving WM_CLOSE for every extra click on its own X button while
    // that dialog is still up (GetActiveWindow() can resolve to the wrong
    // handle depending on focus/timing, so the SYSTEMMODAL owner doesn't
    // reliably disable it) — each extra click re-fires Application.
    // wantsToQuit, and HandleWantsToQuit had nothing stopping it from
    // opening a second, third, Nth MessageBoxW nested inside the first
    // one's still-running modal loop, which is exactly what a cascade of
    // stacked "SC2 Map Editor" windows looks like. Guard with a static
    // re-entrancy flag: while a confirm dialog is already up, any further
    // quit query is simply treated as "not now" (return false) instead of
    // spawning another dialog.
    private static bool _quitConfirmShowing;

    /// <summary>Application.wantsToQuit handler (subscribed exactly once
    /// per process — see the static guard in OnEnable) — the single
    /// interception point for every way this Build can be asked to close
    /// (window X button, Alt+F4, Application.Quit() from anywhere).
    /// Returning false cancels the quit outright (the OS window stays open,
    /// exactly as if the user had cancelled a native "unsaved changes"
    /// dialog); returning true lets it proceed immediately. Static and reads
    /// _activeInstance rather than a captured instance reference, since the
    /// subscription itself is now static/singleton — see OnEnable's comment
    /// for why a captured `this` was the actual root cause of the first
    /// round of the stacked-dialogs bug; see the field comment above for
    /// the second round's re-entrancy root cause.</summary>
    private static bool HandleWantsToQuit()
    {
        if (_quitConfirmShowing)
            return false; // Already asking — don't stack a second native dialog on top.

        // Still on the mandatory startup screen (game-install setup/Create
        // New Map/Open Map) — the user hasn't actually opened or created
        // anything THIS session yet, so there's nothing to lose no matter
        // what terrainShell.Data currently holds. Without this check, a
        // ScriptableObject-backed RuntimeTerrainData left over
        // IsInitialized=true from a PREVIOUS Play session (Unity persists a
        // ScriptableObject asset's state across Play sessions in the
        // Editor, unlike a scene MonoBehaviour) prompted "Save changes to
        // 'NewMap'?" before the user had even reached the startup screen's
        // own buttons.
        if (_activeInstance?.StartupComplete != true)
            return true;

        var terrainShell = _activeInstance?._terrainShell;
        if (terrainShell?.Data == null || !terrainShell.Data.IsInitialized)
            return true; // Nothing loaded - nothing to lose, quit immediately without prompting.

        _quitConfirmShowing = true;
        try
        {
            switch (SC2RuntimeQuitConfirm.Ask(terrainShell.Data.MapName))
            {
                case SC2RuntimeQuitConfirm.Result.Save:
                    terrainShell.RequestSave();
                    ScheduleForceQuit();
                    return true;
                case SC2RuntimeQuitConfirm.Result.DontSave:
                    ScheduleForceQuit();
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _quitConfirmShowing = false;
        }
    }

    // Calling Environment.Exit(0) directly from inside this handler crashes
    // the app instead of exiting cleanly. This handler runs on the same
    // call stack as the
    // native WM_CLOSE/wantsToQuit chain (see the re-entrancy comment above)
    // — forcibly tearing down the process from inside that still-active
    // native callback stack is exactly the kind of reentrant teardown that
    // can crash Unity's native layer. Deferring the hard-exit to the next
    // ordinary Update() frame (a normal, non-reentrant call stack) keeps the
    // same "don't trust Unity's own quit bookkeeping" guarantee without the
    // crash risk.
    private static float _forceQuitAt = -1f;

    private static void ScheduleForceQuit()
    {
        _forceQuitAt = Time.realtimeSinceStartup + 0.5f;
    }
#endif

    private void RepaintGameView()
    {
#if UNITY_EDITOR
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
#endif
    }

    private void SetStatus(string status)
    {
        Status = status;
        if (LogActions)
            Debug.Log("[SC2 GameView UI] " + status);

#if UNITY_EDITOR
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
#endif
    }

    private void CreatePlaceholder(string name, PrimitiveType type)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.position = ResolveCamera() != null
            ? ResolveCamera().transform.position + ResolveCamera().transform.forward * 25f
            : Vector3.zero;

        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        Selection.activeGameObject = go;

        SetStatus("Created: " + name);
    }

    private void ExecuteEditorMenu(string menuPath)
    {
#if UNITY_EDITOR
        if (!EditorApplication.ExecuteMenuItem(menuPath))
            SetStatus("Menu not found: " + menuPath);
        else
            SetStatus("Executed: " + menuPath);
#else
        SetStatus("Editor menu unavailable outside Unity Editor.");
#endif
    }

    // "Export .scd" / "Diagnostics" / "Show Game View UI" need to call
    // ExportWindow.ShowWindow() / SC2VanillaImportDiagnostics.RunFromMenu() /
    // SC2GameViewEditorBootstrap.EnsureGameViewUi() — but those classes live
    // under an "Editor" folder, which Unity compiles into a separate
    // Editor-only assembly that this Scripts/-assembly file cannot reference
    // directly at compile time (that's the whole point of the special
    // "Editor" folder name — it's excluded from player builds). A direct
    // call is therefore a compile error (CS0103), which is exactly why
    // EditorApplication.ExecuteMenuItem(...) was used originally: it doesn't
    // need a compile-time type reference. Reflection gets the same
    // "no compile-time reference needed" property while being more
    // debuggable than a bare menu-path string match (it reports a missing
    // type/method distinctly from a failed invocation).
    private void OpenScdExportWindow() => InvokeEditorStaticMethod("ExportWindow", "ShowWindow", "Opened SC2 Map Export.");

    /// <summary>
    /// Imports a standalone terrain.bdf (already fully authored elsewhere —
    /// materials, masks, texture paths and shader setup baked in, e.g. from
    /// a Blender export) directly as a shaded mesh. Unlike Import .scd, this
    /// needs no Editor-only tooling (SC2TerrainBdfImporter has no
    /// UnityEditor dependency), so the same call path runs in the Editor and
    /// in a build — only the file-picking dialog differs.
    /// </summary>
    private void OpenTerrainBdfImport()
    {
        string path;
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Select terrain.bdf", "", "bdf");
#else
        path = SC2RuntimeFileDialog.OpenFile("Select terrain.bdf", "Terrain BDF (*.bdf)", "bdf");
#endif
        if (string.IsNullOrEmpty(path))
        {
            SetStatus("Terrain BDF import cancelled.");
            return;
        }

        try
        {
            var result = SC2TerrainBdfImporter.Import(path);
            var meshRenderer = result.Root.GetComponent<MeshRenderer>();
            var originalMaterials = meshRenderer != null ? meshRenderer.sharedMaterials : System.Array.Empty<Material>();
            var entry = new SC2ImportedTerrainEntry
            {
                Root = result.Root,
                DisplayName = result.Root.name,
                TextureAttempts = result.TextureAttempts,
                Model = result.Model,
                OriginalBdfBytes = result.RawBytes,
                OriginalMaterials = originalMaterials,
                OriginalEnableRnm = originalMaterials
                    .Select(m => m != null && m.HasProperty("_EnableRNM") ? m.GetFloat("_EnableRNM") : 0f)
                    .ToArray()
            };
            _importedTerrains.Add(entry);
            _selectedImportedTerrain = _importedTerrains.Count - 1;
            // The live height grid must include this newly imported terrain
            // on the next paint.
            _terrainShell?.InvalidateLiveHeightGrid();
            // Jump to Terrain -> Imported so the newly imported object's
            // editing panel (transform/textures/materials) is immediately
            // visible, same as switching subtabs by hand.
            SelectMainTab(0, System.Array.IndexOf(SC2EditorTabs.TerrainTabs, "Imported"));
            var found = result.TextureAttempts?.Count(a => a.Found) ?? 0;
            var total = result.TextureAttempts?.Count ?? 0;
            var statusMsg = $"Imported '{result.Root.name}' from terrain.bdf. Textures: {found}/{total} loaded.";

            // Importing a map's terrain.bdf alone leaves Armies/Units/
            // Chains/Areas empty (they only exist in that same map's
            // _save.lua/_scenario.lua, never in terrain.bdf itself), which
            // would otherwise require separately going to the Objects tab
            // and clicking "Import map objects" with the
            // SAME file a second time. Chain that same import here
            // automatically, against the same path, whenever a live terrain
            // (SC2TerrainData) already exists to receive it - if none does
            // yet, leave it to the Objects tab's own explicit button.
            if (_terrainShell != null && _terrainShell.Data != null && _terrainShell.Data.IsInitialized)
            {
                EnsureObjectsShell();
                var objSummary = _objectsShell.ImportObjectsFromPath(path);
                statusMsg += "  Objects: " + objSummary;
            }

            SetStatus(statusMsg);

            // Newly imported terrains should immediately reflect whatever
            // preview mode (Textured/RNM/Mats/...) is currently active,
            // not always default to raw Textured.
            if (_terrainShell != null)
                ApplyPreviewModeToImportedTerrain(entry, _terrainShell.Prev);
        }
        catch (System.Exception ex)
        {
            SetStatus("Terrain BDF import failed: " + ex.Message);
            Debug.LogError($"[SC2] Terrain BDF import failed: {ex}");
        }
    }

    /// <summary>File > Import map objects: the props, markers, path graphs, armies, units, chains and areas of a map, read from
    /// its mapobjs.bdf and the _save.lua / _scenario.lua next to it, into the current terrain.</summary>
    private void OpenMapObjectsImport()
    {
        if (_terrainShell == null || _terrainShell.Data == null || !_terrainShell.Data.IsInitialized)
        {
            SetStatus("Create or load a terrain first: map objects are imported into it.");
            return;
        }
        try
        {
            EnsureObjectsShell();
            SetStatus(_objectsShell.ImportMapObjectsFromFile());
        }
        catch (System.Exception ex)
        {
            SetStatus("Map objects import failed: " + ex.Message);
            Debug.LogError($"[SC2] Map objects import failed: {ex}");
        }
    }

    /// <summary>
    /// Imports a standalone decals.win.bdf directly as one merged, shaded
    /// mesh — same build-compatible pattern as OpenTerrainBdfImport
    /// (SC2DecalsBdfImporter has no UnityEditor dependency; only the file
    /// picker differs between Editor and a build). This diagnostic action
    /// drops the baked mesh into the scene at its existing world-space
    /// position. Authoring, placement and atlas management live in the
    /// top-level Decals workspace.
    /// </summary>
    private void OpenDecalsBdfImport()
    {
        string path;
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Select decals.bdf", "", "bdf");
#else
        path = SC2RuntimeFileDialog.OpenFile("Select decals.bdf", "Decals BDF (*.bdf)", "bdf");
#endif
        if (string.IsNullOrEmpty(path))
        {
            SetStatus("Decals BDF import cancelled.");
            return;
        }

        try
        {
            var result = SC2DecalsBdfImporter.Import(path);
            if (!result.Model.HasDecals)
            {
                SetStatus($"Imported '{result.Root.name}': this decals.bdf has no decal geometry (empty map).");
                return;
            }

            _importedDecalsRoots.Add(result.Root);
            SetStatus($"Imported '{result.Root.name}' from decals.bdf: {result.Model.Vertices.Count} verts, " +
                       $"{result.Model.Indices.Count / 3} tris. Diffuse atlas {(result.DiffuseTextureFound ? "loaded" : "NOT found")}, " +
                       $"normal atlas {(result.NormalTextureFound ? "loaded" : "not found")}.");
        }
        catch (System.Exception ex)
        {
            SetStatus("Decals BDF import failed: " + ex.Message);
            Debug.LogError($"[SC2] Decals BDF import failed: {ex}");
        }
    }

    /// <summary>
    /// Applies the Textured/RNM/Mats/Pairs/Slope/Height preview toolbar to
    /// every currently imported terrain.bdf object — see
    /// SC2TerrainShell.OnPreviewModeChanged, which this is wired to.
    /// Imported meshes have no SC2TerrainData/Pairs at all, so this offers
    /// an analogous view for each mode, not the identical one:
    ///  - Textured: real baked materials with _EnableRNM forced OFF (flat/
    ///    ambient-only look).
    ///  - RNM: same materials with _EnableRNM forced ON. Forcing both
    ///    explicitly (instead of "restore to baked value" for Textured) is
    ///    what actually makes these two look different — a material already
    ///    authored with RNM_LIGHTING baked in had the same _EnableRNM value
    ///    either way, so the two buttons rendered identically before.
    ///  - Mats: per-pixel smooth D1/D2 blend, proportional to the real RNM
    ///    mask alpha (not a hard threshold — the blend genuinely mixes the
    ///    two textures over a gradient area, so a hard cutoff misrepresents
    ///    it), colored by texture IDENTITY — see BuildMatPairPreviewMaterials.
    ///    A "material" is an individual texture, so this shows exactly how
    ///    much each single texture contributes at each point on the mesh.
    ///  - Pairs: one flat color per whole submesh, keyed on the (D1, D2)
    ///    texture-pair IDENTITY — see BuildPairFlatColorMaterials. A
    ///    "material pair" is the (D1, D2) combination itself, so this is a
    ///    single solid color per unique pair, not a per-pixel blend.
    ///  - Slope/Height: real per-vertex data baked straight from the
    ///    imported mesh (normals for slope, Y position for height) into
    ///    vertex colors, rendered with the plain SC2/VertexColorUnlit
    ///    shader — same idea as the paintable terrain's gradient views, just
    ///    computed directly from this mesh instead of the chunk pipeline.
    /// </summary>
    /// <summary>
    /// Feeds SC2ExportPanel's "Export terrain.bdf" button — see
    /// SC2TerrainExporter.MergeImportedTerrain. Only entries that actually
    /// have a parsed source Model (i.e. came from SC2TerrainBdfImporter, not
    /// some hypothetical future import path without one) are included.
    /// </summary>
    private List<SC2ImportedTerrainExportData> GetImportedTerrainsForExport()
    {
        var result = new List<SC2ImportedTerrainExportData>();
        foreach (var entry in _importedTerrains)
        {
            if (entry.Root == null || entry.Model == null) continue;
            result.Add(new SC2ImportedTerrainExportData
            {
                Id = entry.Id,
                Model = entry.Model,
                Transform = entry.Root.transform,
                Pivot = entry.Root.GetComponent<SC2ImportedTerrainPivot>() is { } pivotHolder ? pivotHolder.Value : Vector3.zero,
                Name = entry.DisplayName,
                AuthoredEnableRnm = entry.OriginalEnableRnm,
                TexturePathOverrides = entry.TexturePathOverrides,
                TexturePathForceExport = entry.TexturePathForceExport,
                RnmBakeMaskNames = entry.RnmBakeMaskNames,
                Materials = entry.OriginalMaterials
            });
        }
        return result;
    }

    // ── Lighting Bake > Imported Objects ────────────────────────────────────
    // Which imported RNM mask (SC2TerrainDocument.ImportedRnm) each LAYER of
    // each imported terrain.bdf is baked onto. The list/UI lives in
    // SC2TerrainShell.DrawLightingBakePanel; this class owns the entries.

    private static void EnsureRnmTargetArray(SC2ImportedTerrainEntry entry)
    {
        var layers = entry.Model?.Layers.Count ?? 0;
        if (entry.RnmBakeMaskNames.Length == layers) return;
        var resized = new string[layers];
        for (var i = 0; i < layers; i++)
            resized[i] = i < entry.RnmBakeMaskNames.Length ? entry.RnmBakeMaskNames[i] ?? "" : "";
        entry.RnmBakeMaskNames = resized;
    }

    private List<SC2ImportedRnmObjectInfo> GetImportedRnmTargets()
    {
        var result = new List<SC2ImportedRnmObjectInfo>();
        foreach (var entry in _importedTerrains)
        {
            if (entry?.Root == null || entry.Model == null) continue;
            EnsureRnmTargetArray(entry);
            var info = new SC2ImportedRnmObjectInfo { ObjectId = entry.Id, Name = entry.DisplayName ?? entry.Root.name };
            for (var i = 0; i < entry.Model.Layers.Count; i++)
            {
                var material = i < entry.Model.Materials.Count ? entry.Model.Materials[i].Name : "";
                info.Layers.Add(new SC2ImportedRnmLayerInfo
                {
                    MaterialName = string.IsNullOrEmpty(material) ? $"(unnamed material {i})" : material,
                    Triangles = CountImportedLayerTriangles(entry.Model, i),
                    MaskName = entry.RnmBakeMaskNames[i],
                });
            }
            result.Add(info);
        }
        return result;
    }

    private void SetImportedRnmTarget(string objectId, int layerIndex, string maskName)
    {
        var entry = _importedTerrains.FirstOrDefault(e => e.Id == objectId);
        if (entry?.Model == null) return;
        EnsureRnmTargetArray(entry);
        if (layerIndex < 0 || layerIndex >= entry.RnmBakeMaskNames.Length) return;
        entry.RnmBakeMaskNames[layerIndex] = maskName ?? "";
        RefreshImportedRnmMasks();
    }

    // One texture triple per imported mask, shared by every layer targeting it.
    private readonly Dictionary<string, Texture2D[]> _importedRnmTextures = new();

    // The RNM masks (and _HasRNM) a material had BEFORE a baked imported mask
    // replaced them, so un-assigning the layer puts them back.
    private sealed class OriginalRnmSlots
    {
        public Texture[] Masks = new Texture[3];
        public float HasRnm;
    }
    private readonly Dictionary<Material, OriginalRnmSlots> _importedRnmOriginals = new();

    /// <summary>Puts each baked imported mask onto the live materials of the
    /// layers that target it (`_RNMMask0/1/2`, same RGB+alpha composition the
    /// export writes: slice 0 = light + shadow alpha, slice 1 = light + blend
    /// alpha, slice 2 = light + opaque), so the viewport shows the bake.</summary>
    private void RefreshImportedRnmMasks()
    {
        foreach (var textures in _importedRnmTextures.Values)
            foreach (var texture in textures)
                if (texture != null) UnityEngine.Object.Destroy(texture);
        _importedRnmTextures.Clear();

        var session = _mapSession;
        if (session?.Document == null || session.BlobStore == null) return;
        var rnm = session.Document.Terrain.ImportedRnm;

        foreach (var entry in _importedTerrains)
        {
            if (entry?.OriginalMaterials == null || entry.Model == null) continue;
            EnsureRnmTargetArray(entry);
            for (var i = 0; i < entry.RnmBakeMaskNames.Length && i < entry.OriginalMaterials.Length; i++)
            {
                var name = entry.RnmBakeMaskNames[i];
                var material = entry.OriginalMaterials[i];
                if (material == null) continue;

                Texture2D[] textures = null;
                var set = string.IsNullOrEmpty(name) ? null : SC2ImportedRnmMasks.Find(rnm, name);
                if (SC2ImportedRnmMasks.HasBake(set))
                {
                    if (!_importedRnmTextures.TryGetValue(name, out textures))
                        _importedRnmTextures[name] = textures = BuildImportedRnmTextures(session.BlobStore, set);
                }

                if (textures == null)
                {
                    // Not targeted (or nothing baked yet): give back whatever
                    // masks the material had before a bake replaced them.
                    if (_importedRnmOriginals.TryGetValue(material, out var original))
                    {
                        for (var slice = 0; slice < 3; slice++)
                            if (material.HasProperty("_RNMMask" + slice)) material.SetTexture("_RNMMask" + slice, original.Masks[slice]);
                        if (material.HasProperty("_HasRNM")) material.SetFloat("_HasRNM", original.HasRnm);
                        _importedRnmOriginals.Remove(material);
                    }
                    continue;
                }

                if (!_importedRnmOriginals.ContainsKey(material))
                {
                    var original = new OriginalRnmSlots { HasRnm = material.HasProperty("_HasRNM") ? material.GetFloat("_HasRNM") : 0f };
                    for (var slice = 0; slice < 3; slice++)
                        original.Masks[slice] = material.HasProperty("_RNMMask" + slice) ? material.GetTexture("_RNMMask" + slice) : null;
                    _importedRnmOriginals[material] = original;
                }
                for (var slice = 0; slice < 3; slice++)
                    if (material.HasProperty("_RNMMask" + slice)) material.SetTexture("_RNMMask" + slice, textures[slice]);
                if (material.HasProperty("_HasRNM")) material.SetFloat("_HasRNM", 1f);
            }
        }
        RepaintGameView();
    }

    private static Texture2D[] BuildImportedRnmTextures(ISC2BlobStore store, SC2RnmMaskSetDocument set)
    {
        var rgb = new[]
        {
            SC2ImportedRnmMasks.Read(store, set.BakedRgb0),
            SC2ImportedRnmMasks.Read(store, set.BakedRgb1),
            SC2ImportedRnmMasks.Read(store, set.BakedRgb2),
        };
        var shadow = SC2ImportedRnmMasks.Read(store, set.BakedShadowAlpha);
        var blend = SC2ImportedRnmMasks.ReadBlendAlpha(store, set);
        int w = set.Width, h = set.Height, n = w * h;
        if (rgb.Any(a => a == null || a.Length != n * 3) || shadow == null || shadow.Length != n) return null;

        var textures = new Texture2D[3];
        for (var slice = 0; slice < 3; slice++)
        {
            var raw = new byte[n * 4];
            for (var i = 0; i < n; i++)
            {
                // The baked arrays are in DDS row order (row 0 on top, as exported), while raw texture data puts row 0
                // at the bottom (V = 0), so each row is written mirrored to make the preview sample like the game.
                var src = i;
                var dst = ((h - 1 - i / w) * w + i % w) * 4;
                raw[dst] = rgb[slice][src * 3];
                raw[dst + 1] = rgb[slice][src * 3 + 1];
                raw[dst + 2] = rgb[slice][src * 3 + 2];
                raw[dst + 3] = slice == 0 ? shadow[src] : slice == 1 ? (blend != null ? blend[src] : (byte)0) : (byte)255;
            }
            // Linear, no mips, clamped — same handling as the imported
            // _RNMMask* textures themselves (raw lighting data, not colour).
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = $"ImportedRNM{slice}_{set.Name}", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            tex.LoadRawTextureData(raw);
            tex.Apply(false);
            textures[slice] = tex;
        }
        return textures;
    }

    private static int CountImportedLayerTriangles(BdfIO.Formats.Terrain.TerrainModel model, int layerIndex)
    {
        if (model == null || layerIndex < 0 || layerIndex >= model.Layers.Count) return 0;
        var layer = model.Layers[layerIndex];
        var tStart = layer.CumulativeIndices / 3;
        var tEnd = layerIndex + 1 < model.Layers.Count ? model.Layers[layerIndex + 1].CumulativeIndices / 3 : model.Triangles.Count;
        return Mathf.Max(0, tEnd - tStart);
    }

    /// <summary>Real, permanent removal of one material/layer's geometry
    /// from an imported terrain.bdf object — for a decorative embedded
    /// object, e.g. a crystal cluster, that should not exist in the
    /// scene/export at all, distinct from Collision2ExcludedMaterialNames'
    /// non-destructive "still real, just don't eat Collision2's triangle
    /// budget" exclusion. Vertices/Triangles are one flat buffer shared
    /// across all layers (BdfIO.Formats.Terrain.TerrainModel's own
    /// convention - CumulativeVertices/CumulativeIndices mark each layer's
    /// range within it), so removing a layer means slicing its range out of
    /// both flat lists and re-basing every SUBSEQUENT layer's cumulative
    /// offsets by the removed counts.</summary>
    private void DeleteImportedMaterialLayer(SC2ImportedTerrainEntry entry, int layerIndex)
    {
        var model = entry?.Model;
        if (model == null || entry.Root == null || layerIndex < 0 || layerIndex >= model.Layers.Count)
            return;

        var layer = model.Layers[layerIndex];
        var vStart = layer.CumulativeVertices;
        var vEnd = layerIndex + 1 < model.Layers.Count ? model.Layers[layerIndex + 1].CumulativeVertices : model.Vertices.Count;
        var tStart = layer.CumulativeIndices / 3;
        var tEnd = layerIndex + 1 < model.Layers.Count ? model.Layers[layerIndex + 1].CumulativeIndices / 3 : model.Triangles.Count;
        var removedVerts = Mathf.Max(0, vEnd - vStart);
        var removedTris = Mathf.Max(0, tEnd - tStart);

        if (removedVerts > 0) model.Vertices.RemoveRange(vStart, removedVerts);
        if (removedTris > 0) model.Triangles.RemoveRange(tStart, removedTris);
        model.Layers.RemoveAt(layerIndex);
        if (layerIndex < model.Materials.Count) model.Materials.RemoveAt(layerIndex);
        if (layerIndex < entry.RnmBakeMaskNames.Length)
            entry.RnmBakeMaskNames = entry.RnmBakeMaskNames.Where((_, i) => i != layerIndex).ToArray();

        for (var i = layerIndex; i < model.Layers.Count; i++)
        {
            model.Layers[i].CumulativeVertices -= removedVerts;
            model.Layers[i].CumulativeIndices -= removedTris * 3;
        }

        // Nothing left to render, collide with or export: drop the whole
        // imported object instead of leaving an empty mesh (and its
        // sidebar entry) behind. Destroyed, not deactivated like "Remove
        // From Scene" — this deletion isn't undoable, and undo snapshots
        // already skip entries whose Root is gone.
        if (model.Layers.Count == 0 || model.Triangles.Count == 0)
        {
            var name = entry.DisplayName;
            var index = _importedTerrains.IndexOf(entry);
            _importedTerrains.Remove(entry);
            _importedTerrainsRemoved.Remove(entry);
            var emptyMesh = entry.Root.GetComponent<MeshFilter>()?.sharedMesh;
            UnityEngine.Object.DestroyImmediate(entry.Root);
            if (emptyMesh != null) UnityEngine.Object.DestroyImmediate(emptyMesh);
            _selectedImportedTerrain = _importedTerrains.Count == 0
                ? -1
                : Mathf.Clamp(index, 0, _importedTerrains.Count - 1);
            _terrainShell?.InvalidateLiveHeightGrid();
            _terrainShell?.MarkDirtyAndRepaint();
            SetStatus($"Deleted the last material geometry — removed imported terrain '{name}'.");
            return;
        }

        // Rebuild the live mesh+materials from the now-modified model,
        // reusing the exact same real builder used at import time - build
        // into a scratch GameObject and steal its output onto the existing
        // one, rather than duplicating TerrainMeshBuilder's own mesh/
        // material construction here.
        var scratch = TerrainMeshBuilder.Build(model, null, new TerrainMeshBuilder.Options { UseSC2TerrainShader = true });
        var newMesh = scratch.GetComponent<MeshFilter>().sharedMesh;
        var newMaterials = scratch.GetComponent<MeshRenderer>().sharedMaterials;
        UnityEngine.Object.DestroyImmediate(scratch);
        // The object keeps the origin it was imported with, whatever layers are left in the model.
        if (entry.Root.GetComponent<SC2ImportedTerrainPivot>() is { } objectPivot) SC2TerrainBdfImporter.ShiftMesh(newMesh, objectPivot.Value);

        var meshFilter = entry.Root.GetComponent<MeshFilter>();
        var oldMesh = meshFilter != null ? meshFilter.sharedMesh : null;
        if (meshFilter != null) meshFilter.sharedMesh = newMesh;
        if (oldMesh != null) UnityEngine.Object.DestroyImmediate(oldMesh);

        var meshRenderer = entry.Root.GetComponent<MeshRenderer>();
        if (meshRenderer != null) meshRenderer.sharedMaterials = newMaterials;
        entry.OriginalMaterials = newMaterials;
        entry.OriginalEnableRnm = newMaterials
            .Select(m => m != null && m.HasProperty("_EnableRNM") ? m.GetFloat("_EnableRNM") : 0f)
            .ToArray();
        // Stale caches keyed off the old material array/mesh (submesh count
        // and vertex count both just changed).
        entry.FlatColorMaterials = null;
        entry.PairColorMaterials = null;
        entry.VertexColorSharedMaterials = null;
        entry.SlopeColors = null;
        entry.HeightColors = null;

        // The MeshCollider's sharedMesh reference is now stale (pointing at
        // the just-destroyed old mesh) - height-query raycasts depend on
        // this collider actually matching current geometry.
        var collider = entry.Root.GetComponent<MeshCollider>();
        if (collider != null) collider.sharedMesh = newMesh;

        _terrainShell?.InvalidateLiveHeightGrid();
        _terrainShell?.MarkDirtyAndRepaint();
        SetStatus($"Deleted material geometry from '{entry.DisplayName}'.");
    }

    private void ApplyPreviewModeToImportedTerrains(SC2TerrainShell.PreviewMode mode)
    {
        foreach (var entry in _importedTerrains)
            ApplyPreviewModeToImportedTerrain(entry, mode);
    }

    private void ApplyPreviewModeToImportedTerrain(SC2ImportedTerrainEntry entry, SC2TerrainShell.PreviewMode mode)
    {
        if (entry?.Root == null || entry.OriginalMaterials == null) return;
        var meshRenderer = entry.Root.GetComponent<MeshRenderer>();
        var meshFilter = entry.Root.GetComponent<MeshFilter>();
        if (meshRenderer == null) return;

        // "Terrain" Visibility-menu checkbox previously only hid the live
        // procedural terrain (SC2TerrainShell.SetTerrainOverlayVisible) -
        // imported terrain.bdf objects are a separate GameObject tree this
        // shell owns, so the flag had no effect on them at all. Toggle just
        // the renderer, not the whole GameObject (SetActive is reserved for
        // real deletion — see "Remove From Scene" — and the collider must
        // stay enabled regardless, so height-sampling/raycasting keep
        // working while the mesh is merely hidden).
        meshRenderer.enabled = !entry.Hidden && (_terrainShell == null || _terrainShell.TerrainOverlayVisible);

        // "Hfield"/"RNM" Visibility-menu checkboxes previously only forced
        // the live procedural terrain's shader mode (SC2TerrainShell.
        // ApplyPreviewModeToChunks) - they never affected imported terrain
        // unless the user also happened to already have that exact
        // PreviewMode selected in the Terrain tab, so toggling them alone
        // showed nothing on an imported-terrain map. Compute the same
        // "effective mode" the live runtime already applies (an explicit
        // MatColors/PairColors/Slope pick always wins; a forced Hfield
        // overlay wins over a plain Textured/TexturedRNM/HeightGradient
        // view; a forced RNM overlay only affects the "normal" Textured
        // view) and apply THAT instead of the raw mode below.
        var effectiveMode = mode;
        if (mode == SC2TerrainShell.PreviewMode.Textured || mode == SC2TerrainShell.PreviewMode.TexturedRNM || mode == SC2TerrainShell.PreviewMode.HeightGradient)
        {
            var forceHfield = _terrainShell != null && _terrainShell.ForceHfieldOverlay;
            var forceRnm = _terrainShell != null && _terrainShell.ForceRnmOverlay;
            effectiveMode = mode == SC2TerrainShell.PreviewMode.HeightGradient || forceHfield
                ? SC2TerrainShell.PreviewMode.HeightGradient
                : mode == SC2TerrainShell.PreviewMode.TexturedRNM || forceRnm
                    ? SC2TerrainShell.PreviewMode.TexturedRNM
                    : SC2TerrainShell.PreviewMode.Textured;
        }

        switch (effectiveMode)
        {
            case SC2TerrainShell.PreviewMode.Textured:
            case SC2TerrainShell.PreviewMode.TexturedRNM:
                var enableRnm = effectiveMode == SC2TerrainShell.PreviewMode.TexturedRNM ? 1f : 0f;
                foreach (var mat in entry.OriginalMaterials)
                    if (mat != null && mat.HasProperty("_EnableRNM"))
                        mat.SetFloat("_EnableRNM", enableRnm);
                meshRenderer.sharedMaterials = entry.OriginalMaterials;
                break;

            case SC2TerrainShell.PreviewMode.MatColors:
                entry.FlatColorMaterials ??= BuildMatPairPreviewMaterials(entry.OriginalMaterials, discrete: false);
                meshRenderer.sharedMaterials = entry.FlatColorMaterials;
                SetStatus("Mats preview: " + DescribeColors(entry.FlatColorMaterials));
                break;

            case SC2TerrainShell.PreviewMode.PairColors:
                entry.PairColorMaterials ??= BuildPairFlatColorMaterials(entry.OriginalMaterials);
                meshRenderer.sharedMaterials = entry.PairColorMaterials;
                SetStatus("Pairs preview: " + DescribeColors(entry.PairColorMaterials));
                break;

            case SC2TerrainShell.PreviewMode.Slope:
                ApplyVertexColorPreview(entry, meshFilter, meshRenderer, isSlope: true);
                break;

            case SC2TerrainShell.PreviewMode.HeightGradient:
                ApplyVertexColorPreview(entry, meshFilter, meshRenderer, isSlope: false);
                break;
        }
    }

    /// <summary>
    /// "Mats" = a "material" is an individual texture (Diffuse1/Diffuse2) —
    /// colors are assigned by texture IDENTITY (reference equality;
    /// Resources.Load/AssetDatabase both cache and return the same instance
    /// for repeated loads of the same asset) so the literal same source
    /// texture always renders as the same color everywhere on the mesh, and
    /// the shared colorByTexture cache is built once across ALL submeshes
    /// (not per-material), so a texture reused across different pairs still
    /// gets one consistent color.
    ///
    /// Uses SC2/MatPairPreview to do a smooth per-pixel D1/D2 blend,
    /// proportional to the same RNM mask alpha the real shader blends
    /// with (discrete=false — a hard threshold would misrepresent an area
    /// that's genuinely a gradual mix of both textures) — shows how much
    /// each texture actually contributes at each point instead of one flat
    /// color for the whole submesh (which hid this entirely: a genuine
    /// two-texture blend within ONE submesh wasn't visible at all with a
    /// flat color, and two submeshes with different D1/D2 pairs but a
    /// similar average color looked identical).
    ///
    /// A material with no real RNM mask defaults to a black mask (alpha=0
    /// everywhere), so it just shows flat Color1 — correct for a material
    /// that only ever uses a single diffuse texture.
    /// </summary>
    /// <summary>The "Water" Visibility checkbox would otherwise have no
    /// effect while the "Mats" (and, same root cause, "Pairs"/"Slope"/
    /// "Height") preview mode is active, since those modes replace
    /// EVERY submesh's material with a synthetic debug-color one, including
    /// water's — SC2WaterPlane.ApplyImportedWaterVisibility only ever finds
    /// and toggles _SC2WaterVisible on materials whose shader is literally
    /// "SC2/Water", so once that submesh's material becomes e.g.
    /// SC2/MatPairPreview instead, the toggle has nothing left to act on and
    /// the debug-color water submesh renders unconditionally. Fixed at the
    /// source: water's own submesh is exempted from every one of these
    /// color-preview modes and keeps its real, original material — so the
    /// existing _SC2WaterVisible mechanism keeps controlling it exactly the
    /// same regardless of which top-level preview mode is selected.</summary>
    private static bool IsWaterShaded(Material mat) => mat != null && mat.shader != null && mat.shader.name == "SC2/Water";

    private static Material[] BuildMatPairPreviewMaterials(Material[] originals, bool discrete)
    {
        // Shader.Find(...) "works" in the Editor (it can locate any
        // compiled shader) but reliably returns null in an actual
        // standalone build — Unity strips shaders nothing references —
        // which used to throw "Value cannot be null. Parameter name:
        // shader" here, the same crash already fixed for SC2/Terrain (see
        // SC2TerrainMaterialFactory.ResolveShader). Resources.Load finds
        // SC2/MatPairPreview reliably since it lives under Resources/.
        var shader = Resources.Load<Shader>("SC2MatPairPreview") ?? SC2FallbackShader.Get();
        var colorByTexture = new Dictionary<object, Color>();
        var result = new Material[originals.Length];
        for (var i = 0; i < originals.Length; i++)
        {
            var mat = originals[i];
            if (IsWaterShaded(mat)) { result[i] = mat; continue; }
            var d1 = GetTextureSafe(mat, "_Diffuse1");
            var d2 = GetTextureSafe(mat, "_Diffuse2");
            // d1 == null means either "this material genuinely has no
            // _Diffuse1 slot" (mat_Water — fine to share one grey) or "the
            // source .dds failed to load" (a real per-submesh problem that
            // should NOT look identical to every other failed submesh) — key
            // on the submesh index in the latter case so failed loads stay
            // visually distinguishable instead of collapsing into one color.
            var color1 = ColorForKey(d1 != null ? (object)d1 : MissingKey(mat, "_Diffuse1", i), colorByTexture);
            var color2 = d2 != null ? ColorForKey((object)d2, colorByTexture) : color1;
            var mask = GetTextureSafe(mat, "_RNMMask1");

            var preview = new Material(shader) { name = (mat != null ? mat.name : "mat") + "_mat" };
            if (preview.HasProperty("_Color1"))
            {
                preview.SetColor("_Color1", color1);
                preview.SetColor("_Color2", color2);
                if (mask != null) preview.SetTexture("_RNMMask1", mask);
                preview.SetFloat("_Discrete", discrete ? 1f : 0f);
            }
            else
            {
                // Fallback shader has no per-pixel blend support — flat
                // Color1 is still better than crashing.
                preview.color = color1;
            }
            result[i] = preview;
        }
        return result;
    }

    /// <summary>
    /// "Pairs" = a material pair is the (Diffuse1, Diffuse2) texture
    /// COMBINATION itself — one flat color per submesh, keyed on that exact
    /// pair (not per-pixel blended like Mats): two submeshes sharing the
    /// same D1 but with different D2 (or vice versa) are different pairs
    /// and get different colors; only an exact (D1, D2) match reuses a
    /// color. Uses its own cache, independent from Mats' per-texture
    /// identity cache, so a pair's color has no fixed relationship to
    /// either of its component textures' individual Mats color.
    /// </summary>
    private static Material[] BuildPairFlatColorMaterials(Material[] originals)
    {
        var shader = SC2FallbackShader.Get();
        var colorByPair = new Dictionary<object, Color>();
        var result = new Material[originals.Length];
        for (var i = 0; i < originals.Length; i++)
        {
            var mat = originals[i];
            if (IsWaterShaded(mat)) { result[i] = mat; continue; }
            var d1 = GetTextureSafe(mat, "_Diffuse1");
            var d2 = GetTextureSafe(mat, "_Diffuse2");
            // Only fall back to a per-submesh key when BOTH textures failed
            // to resolve — a pair with at least one real texture still keys
            // on that identity so genuinely identical pairs share a color.
            var pairKey = (d1 != null || d2 != null) ? (object)(d1, d2) : MissingKey(mat, "_Pair", i);
            var color = ColorForKey(pairKey, colorByPair);
            result[i] = new Material(shader) { name = (mat != null ? mat.name : "mat") + "_pair", color = color };
        }
        return result;
    }

    /// <summary>
    /// Synthetic dictionary key for "this texture slot has no real texture
    /// assigned" (as opposed to null meaning "this shader doesn't even have
    /// the property"), scoped per submesh index so multiple failed loads
    /// don't collide on the same cache entry — see GetTextureSafe.
    /// </summary>
    private static object MissingKey(Material mat, string property, int index) =>
        (mat != null ? mat.name : "mat", property, index);

    /// <summary>
    /// mat.GetTexture(prop) on a material whose shader doesn't have that
    /// property (e.g. mat_Water, which uses "SC2/Water" — no _Diffuse1/
    /// _Diffuse2 at all) logs a Unity console warning and returns null —
    /// checking HasProperty first avoids the spurious warning; the null
    /// result itself is legitimate (that material simply has no texture in
    /// that slot) and is handled by ColorForKey below.
    ///
    /// A property that DOES exist but was never explicitly SetTexture'd
    /// (e.g. SC2TerrainMaterialFactory.TrySetTexture silently skipped it
    /// because the source .dds failed to resolve on disk) still returns the
    /// shader's declared default — "white" for _Diffuse1/_Diffuse2 in
    /// SC2/Terrain.shader — which is ONE shared built-in Texture2D instance
    /// across every material that hit this. Treating that the same as a
    /// real distinct texture made ColorForKey's reference-equality cache
    /// collapse every failed-load material into a single shared color (the
    /// "Mats button paints everything one color" bug), so it's normalized
    /// to null here too, same as a missing property.
    /// </summary>
    private static Texture GetTextureSafe(Material mat, string property)
    {
        if (mat == null || !mat.HasProperty(property)) return null;
        var tex = mat.GetTexture(property);
        return IsBuiltinDefaultTexture(tex) ? null : tex;
    }

    private static bool IsBuiltinDefaultTexture(Texture tex) =>
        tex != null && (tex == Texture2D.whiteTexture || tex == Texture2D.blackTexture ||
                        tex == Texture2D.grayTexture || tex == Texture2D.normalTexture);

    /// <summary>
    /// Returns a stable color for a given key (texture, or texture-pair
    /// tuple), assigning a new one from a cycling HSV palette the first
    /// time that exact key is seen and reusing it for every later match —
    /// this is what makes identical textures/pairs render as the same
    /// color instead of an arbitrary color per submesh index.
    ///
    /// A null key (e.g. a material with no Diffuse1 at all — mat_Water)
    /// must NOT be looked up in the dictionary: Dictionary&lt;TKey,
    /// TValue&gt;.TryGetValue throws ArgumentNullException for a null
    /// reference-type key, which used to crash the whole Mats/Pairs
    /// preview build the instant any Water submesh was present — the
    /// button did nothing because the exception aborted
    /// BuildMatPairPreviewMaterials before it ever reached
    /// meshRenderer.sharedMaterials = ... — every null key instead gets a
    /// single shared flat grey, without touching the cache.
    /// </summary>
    private static Color ColorForKey<TKey>(TKey key, Dictionary<TKey, Color> cache)
    {
        if (key == null)
            return Color.grey;
        if (cache.TryGetValue(key, out var existing))
            return existing;
        var hue = (cache.Count % 12) / 12f;
        var color = Color.HSVToRGB(hue, 0.6f, 0.85f);
        cache[key] = color;
        return color;
    }

    /// <summary>
    /// Console output isn't visible in a shipped standalone player, so this
    /// puts the actual applied hex colors into the status bar (already
    /// visible in-game) whenever Mats/Pairs preview is applied — a way to
    /// confirm from inside a build whether the two modes are computing
    /// different colors.
    /// </summary>
    private static string DescribeColors(Material[] mats)
    {
        var parts = new string[mats.Length];
        for (var i = 0; i < mats.Length; i++)
            parts[i] = DescribeColor(mats[i]);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Material.color requires the shader to have a property named exactly
    /// "_Color" — SC2/MatPairPreview (used for Mats) has "_Color1"/
    /// "_Color2" instead, so calling .color on it logged a Unity warning
    /// every single frame this ran. Read whichever color property the
    /// material's shader actually exposes.
    /// </summary>
    private static string DescribeColor(Material mat)
    {
        if (mat == null) return "null";
        if (mat.HasProperty("_Color")) return "#" + ColorUtility.ToHtmlStringRGB(mat.color);
        if (mat.HasProperty("_Color1"))
        {
            var c1 = ColorUtility.ToHtmlStringRGB(mat.GetColor("_Color1"));
            var c2 = mat.HasProperty("_Color2") ? ColorUtility.ToHtmlStringRGB(mat.GetColor("_Color2")) : "?";
            return $"#{c1}/#{c2}";
        }
        return "?";
    }

    /// <summary>
    /// Bakes slope (from vertex normals) or height (from vertex Y) into the
    /// imported mesh's vertex color stream and swaps in a plain vertex-
    /// color-unlit material so it displays. The baked color arrays and
    /// material are cached per entry/mode — cheap to swap back and forth
    /// afterward, just SetColors + a material array swap.
    /// </summary>
    private void ApplyVertexColorPreview(SC2ImportedTerrainEntry entry, MeshFilter meshFilter, MeshRenderer meshRenderer, bool isSlope)
    {
        var mesh = meshFilter != null ? meshFilter.sharedMesh : null;
        if (mesh == null) return;

        if (isSlope)
        {
            entry.SlopeColors ??= ComputeSlopeColors(mesh);
            mesh.SetColors(entry.SlopeColors);
        }
        else
        {
            entry.HeightColors ??= ComputeHeightColors(mesh);
            mesh.SetColors(entry.HeightColors);
        }

        if (entry.VertexColorMaterial == null)
        {
            var shader = Resources.Load<Shader>("SC2VertexColorUnlit") ?? SC2FallbackShader.Get();
            entry.VertexColorMaterial = new Material(shader) { name = "VertexColorPreview" };
        }
        if (entry.VertexColorSharedMaterials == null || entry.VertexColorSharedMaterials.Length != mesh.subMeshCount)
        {
            entry.VertexColorSharedMaterials = new Material[mesh.subMeshCount];
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                // Water submesh exempted from the debug-color view (same fix
                // as BuildMatPairPreviewMaterials/BuildPairFlatColorMaterials
                // — see IsWaterShaded's own doc comment) — keeps its real
                // material so the "Water" Visibility toggle's
                // _SC2WaterVisible mechanism still works in Slope/Height too.
                var original = entry.OriginalMaterials != null && i < entry.OriginalMaterials.Length ? entry.OriginalMaterials[i] : null;
                entry.VertexColorSharedMaterials[i] = IsWaterShaded(original) ? original : entry.VertexColorMaterial;
            }
        }
        meshRenderer.sharedMaterials = entry.VertexColorSharedMaterials;
    }

    private static Color[] ComputeSlopeColors(Mesh mesh)
    {
        var normals = mesh.normals;
        var colors = new Color[normals.Length];
        for (var i = 0; i < normals.Length; i++)
        {
            // 0 = flat (facing straight up), 1 = vertical/overhanging.
            var slope01 = Mathf.Clamp01(1f - Vector3.Dot(normals[i].normalized, Vector3.up));
            colors[i] = Color.Lerp(new Color(0.2f, 0.8f, 0.2f), new Color(0.9f, 0.15f, 0.1f), slope01);
        }
        return colors;
    }

    private static Color[] ComputeHeightColors(Mesh mesh)
    {
        var verts = mesh.vertices;
        var colors = new Color[verts.Length];
        if (verts.Length == 0) return colors;

        var minY = float.MaxValue;
        var maxY = float.MinValue;
        foreach (var v in verts) { if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y; }
        var range = Mathf.Max(0.0001f, maxY - minY);

        for (var i = 0; i < verts.Length; i++)
        {
            var t = Mathf.Clamp01((verts[i].y - minY) / range);
            colors[i] = SC2TerrainShell.EvaluateHeightGradient(t);
        }
        return colors;
    }

    private static bool GetMaterialFlag(Material mat, string prop) => mat.HasProperty(prop) && mat.GetFloat(prop) > 0.5f;

    /// <summary>Looks up a real, as-parsed-from-terrain.bdf flag value by
    /// name — untouched by the Textured/RNM preview toolbar, which only
    /// ever mutates the LIVE Material's shader properties, never this list.
    /// Real ShaderSetup values are the string "1"/"0" (or a numeric
    /// threshold for ALPHA_TEST specifically) — non-"0" counts as set,
    /// matching how SC2TerrainMaterialFactory.ApplyShaderSetup itself
    /// interprets these entries.</summary>
    private static bool GetShaderSetupBool(List<BdfIO.Formats.Terrain.ShaderSetupEntry> shaderSetup, string flagName)
    {
        if (shaderSetup == null) return false;
        foreach (var entry in shaderSetup)
            if (string.Equals(entry.Name, flagName, System.StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrEmpty(entry.Value) && entry.Value != "0";
        return false;
    }
    private void SetMaterialFlag(Material mat, string prop, bool value)
    {
        if (!mat.HasProperty(prop)) return;
        if (GetMaterialFlag(mat, prop) == value) return;
        RecordImportedTerrainsUndo();
        mat.SetFloat(prop, value ? 1f : 0f);
    }

    // Maps a shader property to the exact TerrainMaterial.TexturePaths slot
    // SC2TerrainMaterialExporter.ApplyFromScene actually writes for it — the
    // same mapping that file's own header comment documents. Used only to
    // show the real, already-parsed original path as a display fallback
    // (see DrawMaterialTextureSlot); _RNMMask1/_RNMMask2 have no slot of
    // their own (all 3 RNM slices share base path slot 10, derived only from
    // _RNMMask0 — see ApplyFromScene), so they're absent here and simply
    // show blank until the user types something, same as before this fix.
    private static readonly Dictionary<string, int> kImportedTexturePathSlots = new()
    {
        { "_Diffuse1", 0 }, { "_Diffuse2", 1 }, { "_Normal1", 2 }, { "_Normal2", 3 },
        { "_Mask1", 6 }, { "_Mask2", 7 }, { "_Ramp", 8 }, { "_RNMMask0", 10 },
    };

    /// <summary>The other two textures of the same material layer (Diffuse1 / Normal1 / Mask1 form one, the "2" slots another):
    /// the texture browser suggests a slot's counterpart by the vanilla suffix convention from them.</summary>
    private static Texture2D[] SiblingTextures(Material mat, string shaderProp)
    {
        var result = new List<Texture2D>();
        if (!shaderProp.StartsWith("_Diffuse", System.StringComparison.Ordinal) && !shaderProp.StartsWith("_Normal", System.StringComparison.Ordinal)
            && !shaderProp.StartsWith("_Mask", System.StringComparison.Ordinal))
            return result.ToArray();
        var layer = shaderProp.EndsWith("2", System.StringComparison.Ordinal) ? "2" : "1";
        foreach (var kind in new[] { "_Diffuse", "_Normal", "_Mask" })
        {
            var prop = kind + layer;
            if (prop == shaderProp || !mat.HasProperty(prop)) continue;
            if (mat.GetTexture(prop) is Texture2D texture) result.Add(texture);
        }
        return result.ToArray();
    }

    private void DrawMaterialTextureSlot(SC2ImportedTerrainEntry entry, int materialIndex, Material mat, string shaderProp, string label, string pickerKeyPrefix)
    {
        if (!mat.HasProperty(shaderProp)) return;
        var current = mat.GetTexture(shaderProp) as Texture2D;
        var overrideKey = materialIndex + ":" + shaderProp;
        entry.TexturePathOverrides.TryGetValue(overrideKey, out var pathDraft);
        // This field can otherwise show blank even though the texture has
        // already loaded successfully — fall back to the path actually
        // parsed from the real terrain.bdf at import time (entry.Model,
        // untouched by any later edit) rather than only ever showing the
        // manual override.
        string fallbackPath = null;
        if (entry.Model != null && materialIndex < entry.Model.Materials.Count &&
            kImportedTexturePathSlots.TryGetValue(shaderProp, out var slot) &&
            slot < entry.Model.Materials[materialIndex].TexturePaths.Length)
            fallbackPath = entry.Model.Materials[materialIndex].TexturePaths[slot];
        var picked = SC2TerrainShell.DrawTexturePickerWithPath(label, current, pickerKeyPrefix + ":" + shaderProp, ref pathDraft, out var pickedViaBrowser, fallbackPath, SiblingTextures(mat, shaderProp)) as Texture2D;
        ApplyImportedTexturePick(entry, materialIndex, mat, shaderProp, current, picked, pickedViaBrowser, ref pathDraft);

        // Per-texture "copy into map on export" opt-in — only offered when
        // this slot actually points at a texture served by a vanilla game
        // archive (anything else is always copied, or never needs to be).
        var effectivePath = !string.IsNullOrEmpty(pathDraft) ? pathDraft : (picked != null ? SC2TerrainMaterialFactory.TryGetResourcePath(picked) : null);
        if (string.IsNullOrEmpty(effectivePath)) effectivePath = fallbackPath;
        if (SC2VanillaArchiveClassifier.IsVanillaSlotPath(effectivePath))
        {
            entry.TexturePathForceExport.TryGetValue(overrideKey, out var forceExport);
            var newForceExport = GUILayout.Toggle(forceExport, "Copy into map on export");
            if (newForceExport != forceExport)
            {
                RecordImportedTerrainsUndo();
                if (newForceExport) entry.TexturePathForceExport[overrideKey] = true;
                else entry.TexturePathForceExport.Remove(overrideKey);
            }
        }
    }

    /// <summary>Applies what a slot's picker (or its folded thumbnail) produced this frame: binds the picked texture, drops the
    /// debug-preview caches that were built from the old one, and keeps the export path override in step.</summary>
    private void ApplyImportedTexturePick(SC2ImportedTerrainEntry entry, int materialIndex, Material mat, string shaderProp,
        Texture2D current, Texture2D picked, bool pickedViaBrowser, ref string pathDraft)
    {
        var overrideKey = materialIndex + ":" + shaderProp;
        if (picked != current)
        {
            RecordImportedTerrainsUndo();
            mat.SetTexture(shaderProp, picked);
            // The Mats/Pairs debug materials are built once from the textures
            // that were assigned at that moment (cached on the entry) - now
            // that this picker is reachable while those modes are active, a
            // stale cache would keep showing the old texture's colour.
            if (entry.FlatColorMaterials != null || entry.PairColorMaterials != null)
            {
                entry.FlatColorMaterials = null;
                entry.PairColorMaterials = null;
                if (_terrainShell != null)
                    ApplyPreviewModeToImportedTerrain(entry, _terrainShell.Prev);
            }
        }
        // A Custom- or archive-sourced browser pick resolves to an in-memory
        // Texture2D with no real project asset/GUID — Unity cannot
        // serialize a reference to that object at all, so clearing the
        // override here (as this used to do unconditionally) silently lost
        // the texture the moment the map was reopened or re-exported, since
        // nothing else records where it actually came from. Same fix as
        // SC2MaterialsPairsMode.DrawTextureField: keep the override (set to
        // the just-picked texture's own resolved "file:"/"archive:" path)
        // for anything that isn't a plain Resources-backed real asset; only
        // clear it for a real asset pick, where it would just be redundant.
        if (pickedViaBrowser)
        {
            var resolvedPath = picked != null ? SC2TerrainMaterialFactory.TryGetResourcePath(picked) : null;
            var isRealAsset = string.IsNullOrEmpty(resolvedPath) ||
                (!resolvedPath.StartsWith("file:", System.StringComparison.Ordinal) &&
                 !resolvedPath.StartsWith("archive:", System.StringComparison.Ordinal));
            pathDraft = isRealAsset ? "" : resolvedPath;
        }
        if (!string.IsNullOrEmpty(pathDraft)) entry.TexturePathOverrides[overrideKey] = pathDraft;
        else entry.TexturePathOverrides.Remove(overrideKey);
        // A different texture starts back at the default.
        if (picked != current) entry.TexturePathForceExport.Remove(overrideKey);
    }

    /// <summary>
    /// Content for the Terrain tab's "Imported" subtab — migrated from a
    /// floating overlay card (drawn on top of everything, with its own
    /// BeginArea/GUI.depth/close button) to a plain left-panel subtab, drawn
    /// inline by SC2TerrainShell.DrawEditorBody exactly like Geometry/
    /// Materials/Export Preview/etc. (see SC2TerrainShell.DrawImportedTerrainsContent).
    /// Since the source mesh is already fully authored (materials/masks/
    /// shaders baked in by the export pipeline), the only geometry edits
    /// offered here are the whole-object Transform (position/rotation/
    /// scale) — no heightmap/pair painting, which only makes sense for the
    /// separate paintable SC2TerrainData workflow. Material textures and
    /// per-material shader flags remain editable in case a texture needs
    /// pointing at a different asset in this project, or a lighting flag
    /// needs a tweak.
    /// </summary>
    /// <summary>Every real MeshCollider currently sitting on an active
    /// imported-terrain.bdf GameObject (SC2TerrainBdfImporter.Import adds one
    /// at import time). Shared by _objectsShell and _terrainShell's own
    /// GetImportedTerrainColliders wiring (EnsureObjectsShell/EnsureTerrainShell)
    /// so both raycast against the exact same real surface instead of two
    /// independently-collected lists that could drift apart.</summary>
    private List<Collider> BuildImportedTerrainColliders()
    {
        var list = new List<Collider>();
        foreach (var entry in _importedTerrains)
        {
            if (entry?.Root == null || !entry.Root.activeInHierarchy) continue;
            var col = entry.Root.GetComponent<Collider>();
            if (col != null) list.Add(col);
        }
        return list;
    }

    // A map can carry a dozen materials, each with a dozen texture slots and a flag list, so the cards start folded: a folded card
    // still shows its textures as thumbnails and takes a new one on a click. Keyed by entry id and material index; absent = folded.
    private readonly HashSet<string> _expandedImportedMaterials = new();
    private bool _importedShaderSetupExpanded = true;

    private void DrawImportedMaterials(SC2ImportedTerrainEntry entry, Material[] mats)
    {
        // The AUTHORED materials, not meshRenderer.sharedMaterials: the Mats/
        // Pairs/Slope/Height preview modes swap the live renderer over to
        // synthetic debug materials that have none of the _Diffuse1/_Normal1/
        // _Mask1/... properties, so reading the live array made every texture
        // slot (and the shader flags) vanish in those modes.
        if (mats.Length > 1)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Collapse all"))
                for (var i = 0; i < mats.Length; i++) _expandedImportedMaterials.Remove(entry.Id + ":" + i);
            if (GUILayout.Button("Expand all"))
                for (var i = 0; i < mats.Length; i++) _expandedImportedMaterials.Add(entry.Id + ":" + i);
            GUILayout.EndHorizontal();
            EditorGUILayout.Space(SC2UiScale.Pxf(6f));
        }
        for (var i = 0; i < mats.Length; i++)
            if (mats[i] != null) DrawImportedMaterialCard(entry, i, mats[i]);
    }

    private void DrawImportedMaterialCard(SC2ImportedTerrainEntry entry, int i, Material mat)
    {
        var stateKey = entry.Id + ":" + i;
        var expanded = _expandedImportedMaterials.Contains(stateKey);
        var key = entry.DisplayName + ":" + i;
        SC2TerrainShell.BeginUnifiedPanelBox();

        // The arrow is a real button and the name beside it folds the card too, so the whole header row is a target.
        var rowHeight = SC2UiScale.Pxf(24f);
        GUILayout.BeginHorizontal();
        var toggle = GUILayout.Button(expanded ? "▾" : "▸", GUILayout.Width(rowHeight), GUILayout.Height(rowHeight));
        var nameRect = GUILayoutUtility.GetRect(0f, rowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));
        var e = Event.current;
        if (e.type == EventType.Repaint)
        {
            var nameStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, fontSize = SC2Type.Body };
            GUI.Label(nameRect, mat.name, nameStyle);
        }
        if (e.type == EventType.MouseDown && e.button == 0 && nameRect.Contains(e.mousePosition))
        {
            toggle = true;
            e.Use();
        }
        GUILayout.EndHorizontal();
        if (toggle)
        {
            expanded = !expanded;
            if (expanded) _expandedImportedMaterials.Add(stateKey);
            else _expandedImportedMaterials.Remove(stateKey);
        }

        if (expanded)
        {
            DrawMaterialTextureSlot(entry, i, mat, "_Diffuse1", "Diffuse1", key);
            DrawMaterialTextureSlot(entry, i, mat, "_Normal1", "Normal1", key);
            DrawMaterialTextureSlot(entry, i, mat, "_Mask1", "Mask1", key);
            DrawMaterialTextureSlot(entry, i, mat, "_Diffuse2", "Diffuse2", key);
            DrawMaterialTextureSlot(entry, i, mat, "_Normal2", "Normal2", key);
            DrawMaterialTextureSlot(entry, i, mat, "_Mask2", "Mask2", key);
            DrawMaterialTextureSlot(entry, i, mat, "_Ramp", "Ramp", key);
            DrawImportedEnvironmentSlot(entry, i, mat);
            DrawMaterialTextureSlot(entry, i, mat, "_RNMMask0", "RNM Mask 0", key);
            DrawMaterialTextureSlot(entry, i, mat, "_RNMMask1", "RNM Mask 1", key);
            DrawMaterialTextureSlot(entry, i, mat, "_RNMMask2", "RNM Mask 2", key);
            DrawImportedShaderSetup(entry, i, mat);
        }
        else
        {
            DrawImportedCollapsedSlots(entry, i, mat, key);
        }
        SC2TerrainShell.EndUnifiedPanelBox();
        EditorGUILayout.Space(SC2UiScale.Pxf(8f));
    }

    /// <summary>What a folded card still shows: the Diffuse, Normal and Mask of its first material as thumbnails, and those of the
    /// second one too when it uses any. A click on a thumbnail opens the texture browser for that slot.</summary>
    private void DrawImportedCollapsedSlots(SC2ImportedTerrainEntry entry, int i, Material mat, string key)
    {
        var size = SC2UiScale.Pxf(34f);
        EditorGUILayout.Space(SC2UiScale.Pxf(4f));
        DrawImportedCollapsedRow(entry, i, mat, key, "1", size);
        var secondUsed = false;
        foreach (var kind in new[] { "_Diffuse2", "_Normal2", "_Mask2" })
            if (mat.HasProperty(kind) && mat.GetTexture(kind) != null) secondUsed = true;
        if (secondUsed)
        {
            EditorGUILayout.Space(SC2UiScale.Pxf(4f));
            DrawImportedCollapsedRow(entry, i, mat, key, "2", size);
        }
        // Ramp and Environment, then the three RNM masks: unlike the layer rows these show even while empty, since an empty
        // one is as much a statement about the material as a filled one.
        EditorGUILayout.Space(SC2UiScale.Pxf(4f));
        GUILayout.BeginHorizontal();
        DrawImportedCollapsedSlot(entry, i, mat, key, "_Ramp", "Ramp", size);
        DrawImportedCollapsedEnvironment(entry, i, size);
        GUILayout.EndHorizontal();
        EditorGUILayout.Space(SC2UiScale.Pxf(4f));
        GUILayout.BeginHorizontal();
        DrawImportedCollapsedSlot(entry, i, mat, key, "_RNMMask0", "RNM0", size);
        DrawImportedCollapsedSlot(entry, i, mat, key, "_RNMMask1", "RNM1", size);
        DrawImportedCollapsedSlot(entry, i, mat, key, "_RNMMask2", "RNM2", size);
        GUILayout.EndHorizontal();
    }

    private void DrawImportedCollapsedRow(SC2ImportedTerrainEntry entry, int i, Material mat, string key, string layer, float size)
    {
        GUILayout.BeginHorizontal();
        DrawImportedCollapsedSlot(entry, i, mat, key, "_Diffuse" + layer, "D" + layer, size, SC2.MapEditor.Textures.SC2TextureUsage.Diffuse);
        DrawImportedCollapsedSlot(entry, i, mat, key, "_Normal" + layer, "N" + layer, size, SC2.MapEditor.Textures.SC2TextureUsage.Normal);
        DrawImportedCollapsedSlot(entry, i, mat, key, "_Mask" + layer, "M" + layer, size, SC2.MapEditor.Textures.SC2TextureUsage.Mask);
        GUILayout.EndHorizontal();
    }

    /// <param name="usage">What the browser should offer; left out for the slots whose picker key already tells (Ramp, RNM masks).</param>
    private void DrawImportedCollapsedSlot(SC2ImportedTerrainEntry entry, int i, Material mat, string key, string shaderProp,
        string letter, float size, SC2.MapEditor.Textures.SC2TextureUsage? usage = null)
    {
        if (!mat.HasProperty(shaderProp)) return;
        var current = mat.GetTexture(shaderProp) as Texture2D;
        // The browser session id is the one the unfolded card's picker uses (see DrawMaterialTextureSlot).
        var pickerKey = key + ":" + shaderProp;
        var picked = SC2TerrainShell.DrawTextureThumb(pickerKey, letter, usage ?? SC2TerrainShell.TextureUsageForPicker(pickerKey), size, current,
            SiblingTextures(mat, shaderProp)) as Texture2D;
        if (picked == current) return;
        entry.TexturePathOverrides.TryGetValue(i + ":" + shaderProp, out var pathDraft);
        ApplyImportedTexturePick(entry, i, mat, shaderProp, current, picked, true, ref pathDraft);
    }

    // The Environment texture (terrain.bdf material slot 11) has no shader property: the live terrain does not draw it, so the
    // slot only edits what the export writes. Its path lives in the same per-slot override dictionary as the other slots, under
    // the "_Environment" key, and is saved with the material overrides.
    private readonly Dictionary<string, (string Path, Texture Texture)> _importedEnvironmentPreviews = new();

    private static string ImportedEnvironmentKey(int materialIndex) => materialIndex + ":" + SC2TerrainMaterialExporter.EnvironmentKey;

    // The key picks the browser's starting filter ("env" = Environment textures); the entry id is hex, so it cannot contain
    // another category's word the way a display name could. Shared by the unfolded slot and the folded thumbnail.
    private static string ImportedEnvironmentPickerKey(SC2ImportedTerrainEntry entry, int materialIndex) =>
        "ImportedEnv" + entry.Id + "-" + materialIndex + ":Environment";

    /// <summary>The slot's current path pieces: the override the user set (empty = none), the path the imported file had, and the one
    /// that is exported (the override, else the file's own).</summary>
    private static void ImportedEnvironmentPaths(SC2ImportedTerrainEntry entry, int materialIndex, out string overridePath,
        out string originalPath, out string effectivePath)
    {
        entry.TexturePathOverrides.TryGetValue(ImportedEnvironmentKey(materialIndex), out overridePath);
        overridePath ??= "";
        originalPath = null;
        if (entry.Model != null && materialIndex < entry.Model.Materials.Count)
        {
            var paths = entry.Model.Materials[materialIndex].TexturePaths;
            if (paths != null && paths.Length > 11) originalPath = paths[11];
        }
        effectivePath = overridePath.Length > 0 ? overridePath : originalPath ?? "";
    }

    /// <summary>The texture to show for the exported path, loaded once per path.</summary>
    private Texture ImportedEnvironmentPreview(string pickerKey, string effectivePath)
    {
        if (!_importedEnvironmentPreviews.TryGetValue(pickerKey, out var cached) || cached.Path != effectivePath)
        {
            Texture preview = null;
            if (effectivePath.Length > 0)
                preview = effectivePath.StartsWith("archive:", System.StringComparison.Ordinal) || effectivePath.StartsWith("file:", System.StringComparison.Ordinal)
                    ? SC2TerrainMaterialFactory.ResolveFromResourcePath(effectivePath)
                    : SC2TerrainMaterialFactory.LoadTexturePublicAny(effectivePath);
            cached = (effectivePath, preview);
            _importedEnvironmentPreviews[pickerKey] = cached;
        }
        return cached.Texture;
    }

    /// <summary>There is no material property to read the texture back from at export, so a browser pick is always stored as a
    /// path: an archive or custom file as its tagged resource path (copied or referenced by the export), anything else as the
    /// clean game path.</summary>
    private static string ImportedEnvironmentOverrideFromPick(Texture picked)
    {
        if (picked == null) return "";
        var resourcePath = SC2TerrainMaterialFactory.TryGetResourcePath(picked);
        return !string.IsNullOrEmpty(resourcePath) &&
               (resourcePath.StartsWith("archive:", System.StringComparison.Ordinal) || resourcePath.StartsWith("file:", System.StringComparison.Ordinal))
            ? resourcePath
            : SC2TerrainMaterialFactory.ResolveGamePath(picked);
    }

    private void StoreImportedEnvironmentOverride(SC2ImportedTerrainEntry entry, int materialIndex, string newOverride)
    {
        var overrideKey = ImportedEnvironmentKey(materialIndex);
        RecordImportedTerrainsUndo();
        if (newOverride.Length > 0) entry.TexturePathOverrides[overrideKey] = newOverride;
        else entry.TexturePathOverrides.Remove(overrideKey);
        // A different texture starts back at the default.
        entry.TexturePathForceExport.Remove(overrideKey);
    }

    private void DrawImportedCollapsedEnvironment(SC2ImportedTerrainEntry entry, int materialIndex, float size)
    {
        ImportedEnvironmentPaths(entry, materialIndex, out var overridePath, out _, out var effectivePath);
        var pickerKey = ImportedEnvironmentPickerKey(entry, materialIndex);
        var current = ImportedEnvironmentPreview(pickerKey, effectivePath);
        var picked = SC2TerrainShell.DrawTextureThumb(pickerKey, "Env", SC2.MapEditor.Textures.SC2TextureUsage.Environment, size, current);
        if (picked != current)
        {
            var newOverride = ImportedEnvironmentOverrideFromPick(picked);
            if (newOverride != overridePath) StoreImportedEnvironmentOverride(entry, materialIndex, newOverride);
        }
    }

    private void DrawImportedEnvironmentSlot(SC2ImportedTerrainEntry entry, int materialIndex, Material mat)
    {
        var overrideKey = ImportedEnvironmentKey(materialIndex);
        ImportedEnvironmentPaths(entry, materialIndex, out var overridePath, out var originalPath, out var effectivePath);

        var pickerKey = ImportedEnvironmentPickerKey(entry, materialIndex);
        var preview = ImportedEnvironmentPreview(pickerKey, effectivePath);
        var pathDraft = overridePath;
        var picked = SC2TerrainShell.DrawTexturePickerWithPath("Environment", preview, pickerKey, ref pathDraft, out var viaBrowser, originalPath);
        var newOverride = overridePath;
        if (viaBrowser) newOverride = ImportedEnvironmentOverrideFromPick(picked);
        else if (pathDraft != overridePath) newOverride = pathDraft;
        if (newOverride != overridePath)
        {
            StoreImportedEnvironmentOverride(entry, materialIndex, newOverride);
            overridePath = newOverride;
            effectivePath = newOverride.Length > 0 ? newOverride : originalPath ?? "";
        }

        if (overridePath.StartsWith("archive:", System.StringComparison.Ordinal) && SC2VanillaArchiveClassifier.IsVanillaSlotPath(overridePath))
        {
            entry.TexturePathForceExport.TryGetValue(overrideKey, out var forceExport);
            var newForceExport = GUILayout.Toggle(forceExport, "Copy into map on export");
            if (newForceExport != forceExport)
            {
                RecordImportedTerrainsUndo();
                if (newForceExport) entry.TexturePathForceExport[overrideKey] = true;
                else entry.TexturePathForceExport.Remove(overrideKey);
            }
        }

        var environmentMapping = GetMaterialFlag(mat, "_EnableEnvironmentMapping");
        if (environmentMapping && effectivePath.Length == 0)
            SC2TerrainShell.DrawUnifiedNote("ENVIRONMENT_MAPPING is on but no Environment texture is set.");
        else if (!environmentMapping && effectivePath.Length > 0)
            SC2TerrainShell.DrawUnifiedNote("The Environment texture has no effect while ENVIRONMENT_MAPPING is off.");
    }

    private void DrawImportedShaderSetup(SC2ImportedTerrainEntry entry, int i, Material mat)
    {
        if (mat.HasProperty("_EnableRNM"))
        {
            // Real, complete 12-flag set (56-map corpus survey) —
            // was only 4 of these before. REFLECT/CHROMATIC_DISPERSION
            // have no shader property at all (confirmed unused by
            // both terrain.hlsl and global_constants.hlsl), so they
            // aren't offered here — there'd be nothing for the toggle
            // to actually control.
            _importedShaderSetupExpanded = SC2TerrainShell.DrawFoldoutHeader("Shader setup", _importedShaderSetupExpanded);
            if (!_importedShaderSetupExpanded) return;

            // The Textured/RNM preview toolbar (top of the viewport)
            // and this checkbox are two genuinely independent
            // things — the toolbar is only a debug DISPLAY mode
            // (ApplyPreviewModeToImportedTerrain force-overrides the
            // live _EnableRNM shader property on every material for
            // a side-by-side comparison view), while this checkbox
            // is the real, persistent, EXPORTED RNM_LIGHTING value.
            // Reading/writing the live _EnableRNM property directly
            // here would make this checkbox change every time the
            // toolbar changes, so it would never actually be
            // independent, just delayed. Reads/writes
            // entry.OriginalEnableRnm directly instead, with
            // NO reference to the live material property at all —
            // this checkbox's own visual state (and therefore what
            // gets exported, via AuthoredEnableRnm/
            // SyncShaderSetupAndStateFromScene) no longer has any
            // connection whatsoever to the currently-selected
            // toolbar mode. It intentionally does not preview the
            // live 3D effect of toggling it while a forcing debug
            // mode (Textured/RNM) is selected — that's what the
            // toolbar itself is for.
            if (entry.OriginalEnableRnm != null && i < entry.OriginalEnableRnm.Length)
            {
                var authoredRnm = entry.OriginalEnableRnm[i] > 0.5f;
                var newAuthoredRnm = GUILayout.Toggle(authoredRnm, "RNM Lighting");
                if (newAuthoredRnm != authoredRnm)
                {
                    RecordImportedTerrainsUndo();
                    entry.OriginalEnableRnm[i] = newAuthoredRnm ? 1f : 0f;
                }
            }
            else
            {
                // No persistent authored value available for this
                // entry (shouldn't happen for anything imported
                // after this fix) — fall back to the old
                // toolbar-tied behavior rather than hiding the
                // control entirely.
                SetMaterialFlag(mat, "_EnableRNM", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableRNM"), "RNM Lighting"));
            }
            SetMaterialFlag(mat, "_EnableAmbient", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableAmbient"), "Ambient Lighting"));
            SetMaterialFlag(mat, "_EnableDiffuseLighting", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableDiffuseLighting"), "Diffuse Lighting"));
            SetMaterialFlag(mat, "_EnableSpecularLighting", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableSpecularLighting"), "Specular Lighting"));
            SetMaterialFlag(mat, "_EnableBlending", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableBlending"), "Blending"));
            SetMaterialFlag(mat, "_EnableFog", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableFog"), "Fog"));
            SetMaterialFlag(mat, "_EnableGlow", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableGlow"), "Glow"));
            SetMaterialFlag(mat, "_EnableEnvironmentMapping", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableEnvironmentMapping"), "Environment Mapping"));
            SetMaterialFlag(mat, "_EnableDynamicShadows", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableDynamicShadows"), "Dynamic Shadows"));
            SetMaterialFlag(mat, "_EnableInfiniteDistance", GUILayout.Toggle(GetMaterialFlag(mat, "_EnableInfiniteDistance"), "Infinite Distance"));

            var alphaTestOn = GetMaterialFlag(mat, "_EnableAlphaTest");
            var newAlphaTestOn = GUILayout.Toggle(alphaTestOn, "Alpha Test");
            SetMaterialFlag(mat, "_EnableAlphaTest", newAlphaTestOn);
            if (newAlphaTestOn && mat.HasProperty("_AlphaTestThreshold"))
            {
                var threshold = mat.GetFloat("_AlphaTestThreshold");
                var newThreshold = EditorGUILayout.Slider("Alpha Test Threshold", threshold, 0f, 255f);
                if (!Mathf.Approximately(newThreshold, threshold))
                {
                    RecordImportedTerrainsUndo();
                    mat.SetFloat("_AlphaTestThreshold", newThreshold);
                }
            }

            if (mat.HasProperty("_TextureScroll"))
            {
                var scroll = mat.GetVector("_TextureScroll");
                var newScroll = EditorGUILayout.Vector2Field("Texture Scroll", new Vector2(scroll.x, scroll.y));
                if (newScroll.x != scroll.x || newScroll.y != scroll.y)
                {
                    RecordImportedTerrainsUndo();
                    mat.SetVector("_TextureScroll", new Vector4(newScroll.x, newScroll.y, 0, 0));
                }
            }

            if (mat.HasProperty("_SrcBlend"))
            {
                var stateOptions = new[] { "None", "TerrainAlphaBlendState", "TerrainAdditiveState", "TerrainOpaqueState" };
                var currentState = SC2TerrainMaterialFactory.ReverseLookupBlendState(mat) ?? "None";
                var stateIndex = System.Array.IndexOf(stateOptions, currentState);
                if (stateIndex < 0) stateIndex = 0;
                var newStateIndex = EditorGUILayout.Popup("State", stateIndex, stateOptions, SC2UiScale.PopupStyle());
                if (newStateIndex != stateIndex)
                {
                    RecordImportedTerrainsUndo();
                    SC2TerrainMaterialFactory.ApplyBlendState(mat, stateOptions[newStateIndex] == "None" ? null : stateOptions[newStateIndex]);
                }
            }
        }
    }

    /// <summary>The Imported tab's main switch, drawn as a large button at the very top: it changes what the whole map ships.
    /// Off by default (the live plane and the import both ship). On: the live plane's painted geometry contributes zero
    /// triangles to terrain.bdf/hfield.bdf/collision2.bdf — see SC2TerrainData.ImportOnlyMode.</summary>
    private void DrawImportOnlyToggle(SC2TerrainData data, float width)
    {
        var on = data.ImportOnlyMode;
        // A real button (the skin's own look, with its border), outlined in the accent while the mode is on.
        var style = new GUIStyle(GUI.skin.button)
        {
            fixedHeight = SC2UiScale.Pxf(38f),
            fontSize = SC2Type.Body,
            fontStyle = FontStyle.Bold,
            margin = new RectOffset(0, 0, 0, 6),
            clipping = TextClipping.Clip,
        };
        if (on)
        {
            style.normal.background = style.onNormal.background;
            style.hover.background = style.onHover.background;
            style.normal.textColor = SC2Theme.AccentText;
            style.hover.textColor = SC2Theme.AccentText;
        }
        if (GUILayout.Button(on ? "Import-only map: ON" : "Import-only map: OFF", style, GUILayout.Width(width), GUILayout.MaxWidth(width)))
        {
            _terrainShell.RecordUndoCheckpoint("Toggle Import-Only Mode");
            data.ImportOnlyMode = !on;
            _terrainShell.MarkDirtyAndRepaint();
            // Whether the live plane contributes to the merged model just changed.
            _terrainShell.InvalidateLiveHeightGrid();
        }
        SC2TerrainShell.DrawUnifiedNote(on
            ? "Only the imported terrain ships: the painted live plane adds no geometry to terrain, hfield or collision2."
            : "The painted live plane and the imported terrain both ship. Turn on if the live plane z-fights with the import or shows through gaps in it.");
        EditorGUILayout.Space(SC2UiScale.Pxf(6f));
    }

    private bool _deleteGeometryExpanded;

    private void SetImportedHidden(SC2ImportedTerrainEntry entry, bool hidden)
    {
        if (entry == null || entry.Hidden == hidden) return;
        entry.Hidden = hidden;
        var meshRenderer = entry.Root != null ? entry.Root.GetComponent<MeshRenderer>() : null;
        // Only the renderer: the collider stays, so placement, decals and the height grid see the object as before (see
        // ApplyPreviewModeToImportedTerrain). The Terrain visibility toggle still has the last word.
        if (meshRenderer != null) meshRenderer.enabled = !hidden && (_terrainShell == null || _terrainShell.TerrainOverlayVisible);
    }

    private void DrawImportedVisibilityControls(SC2ImportedTerrainEntry entry, float width)
    {
        var visible = GUILayout.Toggle(!entry.Hidden, " Visible in viewport", GUILayout.Width(width), GUILayout.MaxWidth(width));
        if (visible == entry.Hidden) SetImportedHidden(entry, !visible);

        if (_importedTerrains.Count > 1)
        {
            var anyHidden = _importedTerrains.Any(x => x != null && x.Hidden);
            EditorGUILayout.BeginHorizontal(GUILayout.Width(width), GUILayout.MaxWidth(width));
            if (GUILayout.Button("Only This"))
                foreach (var other in _importedTerrains) SetImportedHidden(other, other != entry);
            var previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && anyHidden;
            if (GUILayout.Button("Show All"))
                foreach (var other in _importedTerrains) SetImportedHidden(other, false);
            GUI.enabled = previousEnabled;
            EditorGUILayout.EndHorizontal();
        }
        SC2TerrainShell.DrawUnifiedNote("Hiding only takes the object out of the viewport. It still ships in the export and still counts for placement and decals.");
        EditorGUILayout.Space(SC2UiScale.Pxf(6f));
    }

    private void DrawImportedTerrainsLeftPanelContent(float width)
    {
        _importedTerrains.RemoveAll(e => e.Root == null);
        _importedTerrainsRemoved.RemoveAll(e => e.Root == null);

        var data = _terrainShell.Data;
        if (_importedTerrains.Count > 0 && data != null)
            DrawImportOnlyToggle(data, width);

        if (_importedTerrains.Count == 0)
        {
            SC2TerrainShell.DrawUnifiedNote("No terrain.bdf imported yet — use File → Import terrain.bdf.");
            return;
        }
        if (_selectedImportedTerrain < 0 || _selectedImportedTerrain >= _importedTerrains.Count)
            _selectedImportedTerrain = 0;

        if (_importedTerrains.Count > 1)
        {
            var names = new string[_importedTerrains.Count];
            for (var i = 0; i < _importedTerrains.Count; i++)
                names[i] = _importedTerrains[i].Hidden ? _importedTerrains[i].DisplayName + " (hidden)" : _importedTerrains[i].DisplayName;
            _selectedImportedTerrain = SC2TerrainShell.DrawUnifiedSegmentGrid(_selectedImportedTerrain, names, Mathf.Min(3, names.Length));
            EditorGUILayout.Space(SC2UiScale.Pxf(8f));
        }

        var entry = _importedTerrains[_selectedImportedTerrain];
        if (entry.Root == null) return;

        DrawImportedVisibilityControls(entry, width);

        SC2TerrainShell.DrawUnifiedSectionLabel("Transform");
        var t = entry.Root.transform;
        var prevPos = t.position; var prevEuler = t.eulerAngles; var prevScale = t.localScale;
        var boundsBeforeEdit = ImportedEntryBounds(entry);
        t.position = DrawUndoableVector3Field(entry.Root, "Position", t.position, width);
        var euler = DrawUndoableVector3Field(entry.Root, "Rotation", t.eulerAngles, width);
        if (euler != t.eulerAngles) t.eulerAngles = euler;
        t.localScale = DrawUndoableVector3Field(entry.Root, "Scale", t.localScale, width);
        // The live height grid (SC2LiveHeightGrid) merges imported terrain
        // at its CURRENT transform - repositioning it here is exactly the
        // kind of change that has no other notification hook (unlike
        // terrain edits/import add-remove, which already invalidate it
        // elsewhere).
        if (t.position != prevPos || t.eulerAngles != prevEuler || t.localScale != prevScale)
        {
            _terrainShell?.InvalidateLiveHeightGrid();
            NotifyImportedTerrainChanged(boundsBeforeEdit, ImportedEntryBounds(entry));
        }
        EditorGUILayout.Space(SC2UiScale.Pxf(10f));

        var meshRenderer = entry.Root.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            entry.MaterialsExpanded = SC2TerrainShell.DrawFoldoutHeader("Materials", entry.MaterialsExpanded);
            if (entry.MaterialsExpanded)
                DrawImportedMaterials(entry, entry.OriginalMaterials ?? meshRenderer.sharedMaterials);
        }

        // Real geometry deletion — distinct from the reversible Collision2
        // exclusion above. Per-layer-INDEX (not per distinct name, unlike
        // the exclusion list): two layers could in principle share a
        // material name, and deletion must only ever remove the one the
        // user actually clicked, not every same-named layer.
        {
            var materials = entry.Model?.Materials;
            if (materials != null && materials.Count > 0)
            {
                _deleteGeometryExpanded = SC2TerrainShell.DrawFoldoutHeader("Delete material geometry", _deleteGeometryExpanded);
                if (_deleteGeometryExpanded)
                    SC2TerrainShell.DrawUnifiedNote("Permanently removes a material's triangles from the imported mesh. Cannot be undone. Asks to confirm.");
                for (var mi = 0; _deleteGeometryExpanded && mi < materials.Count; mi++)
                {
                    var mat = materials[mi];
                    var label = string.IsNullOrEmpty(mat.Name) ? $"(unnamed material {mi})" : mat.Name;
                    EditorGUILayout.BeginHorizontal(GUILayout.Width(width), GUILayout.MaxWidth(width));
                    EditorGUILayout.LabelField(label, GUILayout.MinWidth(40), GUILayout.ExpandWidth(true));
                    var doDelete = GUILayout.Button("Delete", GUILayout.Width(60));
                    EditorGUILayout.EndHorizontal();
                    if (doDelete)
                    {
                        var triCount = CountImportedLayerTriangles(entry.Model, mi);
                        if (EditorUtility.DisplayDialog("Delete material geometry",
                            $"Permanently remove '{label}' ({triCount} triangles) from this imported object?\n\nThis cannot be undone."
                            + (materials.Count == 1 ? "\n\nThis is the last material — the whole imported object will be removed." : ""),
                            "Delete", "Cancel"))
                        {
                            DeleteImportedMaterialLayer(entry, mi);
                            // The materials list this loop is iterating just
                            // shrank - stop drawing this frame rather than
                            // continue over now-invalid indices.
                            return;
                        }
                    }
                }
                EditorGUILayout.Space(SC2UiScale.Pxf(6f));
            }
        }

        EditorGUILayout.Space(8);
        if (GUILayout.Button("Remove From Scene", GUILayout.Height(26), GUILayout.Width(width), GUILayout.MaxWidth(width)))
        {
            // Deactivated, not destroyed — see _importedTerrainsRemoved's own
            // doc comment: this is what makes "Remove" undoable at all,
            // rather than requiring a fresh re-import to bring it back.
            RecordImportedTerrainsUndo();
            entry.Root.SetActive(false);
            _importedTerrainsRemoved.Add(entry);
            _importedTerrains.RemoveAt(_selectedImportedTerrain);
            _selectedImportedTerrain = -1;
            // The live height grid must stop including this terrain on the
            // next paint.
            _terrainShell?.InvalidateLiveHeightGrid();
        }
        // Debug.Log/LogWarning output (SC2TerrainMaterialFactory.LoadTexture,
        // SC2TerrainBdfImporter's texture copy step) isn't visible in a
        // shipped standalone player — no console there — so this is the only
        // way to see which textures actually resolved after importing a
        // terrain.bdf in a build. A map can reference 50-100+ textures, so
        // this list is foldable (collapsed lists were the actual ask behind
        // "no collapsing of long tabs") and scrolls within its own bounded
        // height instead of stretching the whole panel — both the fold and
        // the fixed-height inner scroll are new on top of what was here
        // before.
        if (entry.TextureAttempts != null)
        {
            var foundCount = entry.TextureAttempts.Count(a => a.Found);
            entry.TexturesExpanded = SC2TerrainShell.DrawFoldoutHeader(
                $"Textures: {foundCount}/{entry.TextureAttempts.Count} loaded", entry.TexturesExpanded);
            if (entry.TexturesExpanded)
            {
                SC2TerrainShell.BeginUnifiedPanelBox();
                // Row-height assumption (18f) and the row's own Label
                // height (16f) below both scale with SC2UiScale now — this
                // list uses native EditorStyles.miniLabel (a real Unity
                // Editor style, not SC2UnifiedSkin), which was previously
                // left completely unscaled: at higher UI Scale everything
                // ELSE in the panel got bigger while this list's rows
                // stayed tiny AND the scroll view's own total-height
                // estimate (still based on the old fixed 18f/row) drifted
                // out of sync with the now-explicitly-bigger font's real
                // per-row height, which is what visually reads as rows
                // overlapping once scrolled.
                var rowFontSize = SC2Type.Body;
                var rowHeight = SC2UiScale.Pxf(18f);
                var listHeight = Mathf.Min(260f, entry.TextureAttempts.Count * rowHeight + 4f);
                entry.TextureListScroll = GUILayout.BeginScrollView(entry.TextureListScroll, GUILayout.Height(listHeight));
                foreach (var attempt in entry.TextureAttempts)
                {
                    GUILayout.BeginHorizontal(GUILayout.Width(width - 32f), GUILayout.MaxWidth(width - 32f));
                    var prevContent = GUI.contentColor;
                    GUI.contentColor = attempt.Found ? new Color(0.4f, 0.8f, 0.4f) : new Color(0.9f, 0.4f, 0.4f);
                    var okStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = rowFontSize };
                    GUILayout.Label(attempt.Found ? "OK" : "X", okStyle, GUILayout.Width(SC2UiScale.Pxf(22f)));
                    GUI.contentColor = prevContent;
                    // wordWrap=false + Clip: EditorStyles.miniLabel wraps by
                    // default, so a long path used to spill onto a second
                    // line (inflating row height) and, for paths wider than
                    // the row, still overflow past the panel edge — clipping
                    // alone doesn't do anything while wordWrap is still on.
                    var pathStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false, clipping = TextClipping.Clip, fontSize = rowFontSize };
                    // Show what actually got bound (ResolvedFrom), not just
                    // the requested logical path — this project's texture
                    // library has real leaf-name collisions across
                    // different subfolders, so "found: yes" alone can't
                    // confirm the RIGHT file was matched, only that SOME
                    // file was.
                    var label = string.IsNullOrEmpty(attempt.ResolvedFrom) || attempt.ResolvedFrom == attempt.RequestedPath
                        ? attempt.RequestedPath
                        : $"{attempt.RequestedPath}  ->  {attempt.ResolvedFrom}";
                    GUILayout.Label(label, pathStyle, GUILayout.Height(rowHeight - 2f), GUILayout.MaxWidth(Mathf.Max(32f, width - 60f)));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                SC2TerrainShell.EndUnifiedPanelBox();
            }
            EditorGUILayout.Space(6);
        }

    }

    private void OpenBatchImportDiagnostics()
    {
#if UNITY_EDITOR
        InvokeEditorStaticMethod("SC2VanillaImportDiagnostics", "RunFromMenu", "Batch import diagnostics started.");
#else
        // In a build, "Diagnostics" and "Import" are the same runtime
        // pipeline (the unified SC2MapImportOrchestrator, no UnityEditor
        // dependency) — its own per-stage OK/FAIL breakdown IS the
        // diagnostics, so just open the same panel used for a real import.
        OpenMapDocumentImportPanel();
#endif
    }

    private void OpenGameViewUiBootstrap() => InvokeEditorStaticMethod("SC2GameViewEditorBootstrap", "EnsureGameViewUi", "Game View UI ensured.");

    private void InvokeEditorStaticMethod(string typeName, string methodName, string successStatus)
    {
#if UNITY_EDITOR
        try
        {
            System.Type type = null;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }

                foreach (var candidate in types)
                {
                    if (candidate != null && candidate.Name == typeName)
                    {
                        type = candidate;
                        break;
                    }
                }
                if (type != null) break;
            }

            if (type == null)
            {
                SetStatus($"Editor type not found: {typeName}");
                return;
            }

            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                SetStatus($"Editor method not found: {typeName}.{methodName}");
                return;
            }

            method.Invoke(null, null);
            SetStatus(successStatus);
        }
        catch (System.Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            SetStatus($"Failed to invoke {typeName}.{methodName}: {inner.Message}");
            Debug.LogError($"[SC2] Failed to invoke {typeName}.{methodName}: {inner}");
        }
#else
        SetStatus($"{typeName}.{methodName} is an Editor-only tool — not available in a standalone build.");
#endif
    }

    private void HandleGameViewNavigation()
    {
        if (!EnableGameViewNavigation)
            return;

        var e = Event.current;
        if (e == null)
            return;
        if (e.type == EventType.Used)
            return;

        // When the mouse is over the GameView UI, consume all navigation
        // events so they don't propagate to the SceneView OnSceneGUI.
        if (IsMouseOverUi(e.mousePosition))
        {
            if (e.type == EventType.MouseDown || e.type == EventType.MouseUp || e.type == EventType.MouseDrag || e.type == EventType.ScrollWheel)
                e.Use();
            return;
        }

        if (e.type == EventType.ScrollWheel)
        {
            ZoomCamera(e.delta.y);
            e.Use();
            return;
        }

        if (e.type == EventType.KeyDown && SC2Hotkeys.Matches("Global.FocusCamera", e))
        {
            FocusCamera();
            e.Use();
            return;
        }

        if (e.type == EventType.MouseDown)
        {
            _lastMouse = e.mousePosition;
            var shiftPressed = e.shift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            _orbiting = e.button == 2 && !shiftPressed;
            _panning = e.button == 2 && shiftPressed;
            _flying = e.button == 1 && !e.alt;

            if (!_orbiting && !_panning && !_flying && e.button == 0)
                HandleViewportClick(e);
        }

        if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)
        {
            _orbiting = false;
            _panning = false;
            _flying = false;
        }

        if (e.type == EventType.MouseDrag && (_orbiting || _panning))
        {
            var previousMouse = _lastMouse;
            var delta = e.mousePosition - _lastMouse;
            _lastMouse = e.mousePosition;
            var shiftPressed = e.shift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (_panning || shiftPressed)
            {
                _orbiting = false;
                _panning = true;
                PanCamera(previousMouse, e.mousePosition, delta);
            }
            else
            {
                OrbitCamera(delta);
            }

            e.Use();
        }

        if (e.type == EventType.KeyDown && _flying)
        {
            var cameraActionId = ResolveCameraMoveAction(e);
            if (cameraActionId != null && FlyCamera(cameraActionId))
                e.Use();
        }
    }

    /// <summary>Global Undo/Redo/Save (Ctrl+Z/Y/S), checked and — if matched
    /// — consumed BEFORE any panel draws this frame. Deliberately called
    /// early in OnGUI (before DrawHeader/DrawMainPanel), NOT from
    /// HandleHotkeys() below (which runs much later, after every panel this
    /// frame has already drawn). With a Collision2
    /// numeric field (e.g. "Polygon Budget"/"Height Offset",
    /// SC2Numeric_-named) still holding keyboard focus — which Unity IMGUI
    /// keeps indefinitely once clicked into, until something else
    /// explicitly claims it — Ctrl+Z would otherwise do nothing, even if the
    /// checks inside HandleHotkeys() were simply reordered to run before its
    /// own text-field-focus guard, since reordering only changes the
    /// order of checks WITHIN HandleHotkeys(); it doesn't change WHEN
    /// HandleHotkeys() itself runs. Unity's native TextField/FloatField
    /// controls implement their OWN internal Ctrl+Z (undo the last typed
    /// character) whenever they hold keyboard focus, and consume the
    /// KeyDown event — marking it EventType.Used — as part of DRAWING
    /// themselves inside DrawMainPanel, which runs before HandleHotkeys()
    /// is ever called. By the time HandleHotkeys() checked
    /// e.type == EventType.KeyDown, the event was already Used and the
    /// whole block that calls HandleHotkeys() (gated on
    /// "Event.current.type != EventType.Used") was skipped outright.
    /// Calling this method here, before any panel draws, gives the global
    /// document Undo/Redo/Save first claim on the event regardless of what
    /// currently has keyboard focus.</summary>
    private void HandleGlobalUndoRedoSaveHotkey()
    {
        // A background job (export, bake, autosave) is reading the data: no undo, save or second export meanwhile.
        if (!EnableHotkeys || _flying || SC2BusyOverlay.IsActive)
            return;
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
            return;
        if (SC2Hotkeys.Matches("Global.Undo", e)) { UndoTerrainEdit(); e.Use(); return; }
        if (SC2Hotkeys.Matches("Global.Redo", e)) { RedoTerrainEdit(); e.Use(); return; }
        if (SC2Hotkeys.Matches("Global.Save", e)) { SaveActiveTerrain(); e.Use(); return; }
        if (SC2Hotkeys.Matches("Global.Export", e)) { ExportMapFromHotkey(); e.Use(); }
    }

    /// <summary>The Export hotkey: the Export tab's Export button, from any tab.</summary>
    private void ExportMapFromHotkey()
    {
        EnsureExportShell();
        var started = _exportShell.RunExport(result => SetStatus(string.IsNullOrEmpty(result) ? "Export finished." : result));
        SetStatus(started ? "Exporting map..." : "Export not started: no map loaded, or the previous export is still rendering its minimap.");
    }

    private void HandleHotkeys()
    {
        if (!EnableHotkeys)
            return;

        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
            return;

        if (_flying)
            return;

        // Ctrl+Z/Y/S must be checked in HandleGlobalUndoRedoSaveHotkey(),
        // called much earlier in OnGUI (before DrawMainPanel), not here —
        // reordering the checks within THIS method alone would not be
        // enough (Ctrl+Z would still be silently blocked with a Collision2
        // numeric field focused), because Unity's native TextField/FloatField controls implement
        // their OWN internal Ctrl+Z (undo last typed character) and consume
        // the KeyDown event as part of DRAWING themselves — which happens
        // in DrawMainPanel, well before this method (called later in the
        // same OnGUI) ever sees the event. See that method's doc comment
        // for the full explanation.

        // Was #if UNITY_EDITOR-gated, so this guard was silently absent from
        // Build compiles - typing e.g. "s" into the Map Name field also fired
        // the Scale-tool hotkey in a real Build, but not in the Editor.
        // UnityEditor.EditorGUIUtility.editingTextField resolves correctly in
        // both contexts (the real UnityEditor API in-Editor, a
        // GUIUtility.keyboardControl != 0-based Build-only shim otherwise —
        // see UnityEditorRuntimeShim.cs), same as SC2TerrainShell.
        // HandleKeyboardShortcut's own unconditional use of it.
        var focusedControl = GUI.GetNameOfFocusedControl();
        if (EditorGUIUtility.editingTextField || (!string.IsNullOrEmpty(focusedControl) && focusedControl.StartsWith("SC2Numeric_")))
            return;

#if !UNITY_EDITOR
        // True exclusive/borderless fullscreen: no native OS window control can
        // do this (Windows' own maximize keeps the title bar + taskbar), so it
        // needs a keyboard shortcut rather than an in-UI button next to the
        // native title bar. F11 is the conventional fullscreen-toggle key.
        if (e.keyCode == KeyCode.F11) { SC2RuntimeWindow.ToggleFullscreen(); e.Use(); return; }
#endif

        // G removed entirely as a hotkey per direct instruction — moving an
        // already-selected prop is unconditional default behavior now (see
        // SC2ObjectsShell's canMove), no "Move tool" to switch into. R/S
        // are no longer global tool-switch keys either — SC2ObjectsShell.
        // OnGameViewGUI now binds them directly to an interactive rotate/
        // scale gesture on the current Props selection (only S/R, nothing
        // else) instead of a persistent _tool state nothing else in the
        // codebase ever actually read (Rotate/Scale were dead tool states
        // before this change — confirmed via full-repo search).
        if (SC2Hotkeys.Matches("Global.MeasureTool", e)) { ToggleMeasureTool(); e.Use(); return; }
        if (SC2Hotkeys.Matches("Global.FocusCamera", e)) { FocusCamera(); e.Use(); return; }
        if (SC2Hotkeys.Matches("Global.ResetView", e)) { ResetView(); e.Use(); return; }
        if (SC2Hotkeys.Matches("Global.CycleMainTab", e))
        {
            SelectMainTab((_mainTab + 1) % GameMainTabs.Length);
            SetStatus("Tab selected: " + GameMainTabs[_mainTab]);
            e.Use();
        }
    }

    /// <summary>Any text/numeric field can keep IMGUI keyboard focus (and its blinking caret)
    /// indefinitely once clicked into — Unity never clears GUIUtility.keyboardControl just because
    /// the user clicked somewhere that isn't itself a keyboard-focus-requesting control (a raw
    /// viewport click, a button, empty space between panel controls). Left unhandled,
    /// HandleHotkeys()'s own stuck-focus guard (needed so typing into that same field doesn't also
    /// fire a tool hotkey) stays tripped forever after the FIRST field anyone ever clicks this
    /// session, silently killing M/F/Tab, on top of the caret itself staying visible with nothing
    /// left to edit. Delegates to SC2TerrainShell.ClearFocusIfClickMissedField, which every
    /// text/numeric field in this project registers its own on-screen rect with while focused —
    /// covers a click ANYWHERE (viewport or elsewhere in the same panel), not just the viewport,
    /// while a click back into the SAME field (to reposition the caret) is unaffected.</summary>
    private void ReleaseStuckNumericFocusOnViewportClick()
    {
        // A click on the bare viewport never belongs to a text field, so whatever holds the keyboard goes, named or not: a field
        // that was removed from the panel while it had the caret (a mode switch swaps "Height above" for "Y") keeps the focus id
        // with an empty name, which the per-field check below cannot match.
        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && !IsMouseOverUi(e.mousePosition))
        {
            if (GUIUtility.hotControl != 0)
            {
                Debug.Log($"[SC2DragDiag] released a stuck hotControl={GUIUtility.hotControl} on a viewport click");
                GUIUtility.hotControl = 0;
            }
            if (GUIUtility.keyboardControl != 0)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                return;
            }
        }
        SC2TerrainShell.ClearFocusIfClickMissedField();
    }

    private bool IsMouseOverUi(Vector2 mouse)
    {
        if (mouse.y <= HeaderHeight)
            return true;
        if (ShowPanel && _panelRect.Contains(mouse))
            return true;
        if (ShowReferencePanel && _referencePanelRect.Contains(mouse))
            return true;
        if (_statusRect.Contains(mouse))
            return true;
        if (_navigationGizmoRect.Contains(mouse))
            return true;
        if (IsMouseOverOpenMenu(mouse))
            return true;

        return false;
    }

    private bool IsMouseOverOpenMenu(Vector2 mouse)
    {
        // _openMenuRect is recorded by DrawMenuPanel from the rect actually drawn,
        // so hit-testing can never drift from rendering again (the old copy-pasted
        // hardcoded 260px rects had already diverged from the drawn cards).
        return AnyMenuOpen() && _openMenuRect.Contains(mouse);
    }

    private void HandleViewportClick(Event e)
    {
        if (_tool != "Select")
            return;

        if (!_viewportRect.Contains(e.mousePosition))
            return;

        var cam = ResolveCamera();
        if (cam == null)
            return;

        var ray = SC2TopViewMirror.ScreenPointToRay(cam, e.mousePosition);

        // Imported terrain.bdf objects have a real MeshCollider (for
        // Costs/Objects/drag height-query raycasts) - but _tool never
        // actually changes away from its "Select" default anywhere in this
        // file, so THIS click-to-select handler also runs on every ordinary
        // viewport click regardless of which tab is active, and that
        // collider would otherwise get silently selected on every Costs
        // paint / Objects placement click. Selecting it then hijacks
        // GetViewPivot() (used by orbit/pan) to the terrain's raw
        // transform.position instead of the actual clicked ground point -
        // for an imported terrain repositioned away from its
        // mesh-authored origin, orbiting around that far-away point looks
        // like the camera suddenly flying off. Terrain has no meaningful
        // generic-Selection-based editing in this UI anyway (it's edited
        // via the dedicated Terrain/Costs/Collision2 tabs, never via the
        // Unity Inspector), so it's excluded from this raycast entirely
        // rather than allowed to win. Imported decals.bdf meshes have the
        // exact same real MeshRenderer/bounds and would hit this same bug —
        // they must be included in this same exclusion check (rather than a
        // second parallel one), or PickRendererByBounds would happily pick
        // one and GetViewPivot() would jump orbit/pan to its raw
        // transform.position.
        var hits = Physics.RaycastAll(ray, 100000f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (IsGenericSelectionExcluded(hit.collider.gameObject))
                continue;
            Selection.activeGameObject = hit.collider.gameObject;
            SetStatus("Selected: " + hit.collider.gameObject.name);
            e.Use();
            return;
        }

        var picked = PickRendererByBounds(ray);
        if (picked != null)
        {
            Selection.activeGameObject = picked;
            SetStatus("Selected renderer: " + picked.name);
            e.Use();
        }
        else
        {
            SetStatus("Nothing selected");
        }
    }

    /// <summary>See HandleViewportClick's own comment — excludes imported
    /// terrain.bdf AND decals.bdf roots from generic viewport
    /// object-selection (both would otherwise hijack GetViewPivot() via
    /// Selection.activeTransform and break orbit/pan). The background
    /// terrain's own chunk GameObjects (SC2TerrainRuntime, same as the
    /// core's) have no MeshCollider at all — same as the
    /// core's own chunks — so they're never a physics-raycast hit in the
    /// first place and need no exclusion entry here (unlike the old
    /// SC2BackgroundTerrainPreview, which used a real MeshCollider purely
    /// for SC2BackgroundEditMode's CPU brush raycast).</summary>
    private bool IsGenericSelectionExcluded(GameObject go)
    {
        if (go != null && go.GetComponentInParent<SC2DecalAuthoredPreviewMarker>() != null) return true;
        if (_importedDecalsRoots.Contains(go)) return true;
        foreach (var entry in _importedTerrains)
            if (entry?.Root == go) return true;
        return false;
    }

    private GameObject PickRendererByBounds(Ray ray)
    {
        var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
        GameObject best = null;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer == null || !renderer.enabled || renderer.gameObject == gameObject || IsGenericSelectionExcluded(renderer.gameObject))
                continue;

            float distance;
            if (!renderer.bounds.IntersectRay(ray, out distance))
                continue;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = renderer.gameObject;
            }
        }

        return best;
    }

    private Camera ResolveCamera()
    {
        var cam = GetComponent<Camera>();
        if (cam != null)
            return cam;

        cam = GetComponentInParent<Camera>();
        if (cam != null)
            return cam;

        if (Camera.main != null)
            return Camera.main;

        return FindFirstObjectByType<Camera>();
    }

    private void OrbitCamera(Vector2 delta)
    {
        var cam = ResolveCamera();
        if (cam == null)
            return;

        var pivot = GetViewPivot();
        // Mirroring the render vertically (SC2TopViewMirror) reverses the
        // apparent rotational sense of any on-screen circular motion (a
        // general property of 2D reflections - worked out algebraically: a
        // point moving as (cos t, sin t) in screen space, Y-down convention,
        // traces clockwise; negating just Y gives (cos t, -sin t), which
        // traces counter-clockwise) - so with the mirror active, dragging
        // right made the view yaw the "wrong" way. Negate delta.x here to
        // compensate, matching the un-mirrored feel. Pitch (delta.y) isn't
        // touched: it doesn't trace the same kind of in-plane screen
        // rotation near top-down.
        var yawDelta = SC2TopViewMirror.Active ? -delta.x : delta.x;
        cam.transform.RotateAround(pivot, Vector3.up, yawDelta * 0.25f);
        cam.transform.RotateAround(pivot, cam.transform.right, -delta.y * 0.25f);
        // Deliberately does NOT touch SC2TopViewMirror.Active. An earlier
        // version cleared it here (even behind an orientation threshold),
        // which meant starting to orbit away from Top un-mirrored the
        // render mid-drag - a jump/flicker the mirror should never produce
        // on its own. Only an explicit view switch (LookAlong's other
        // presets) turns it off now; free orbit leaves whatever mirror
        // state was already in effect alone, however far it rotates.
    }

    private void PanCamera(Vector2 previousMouse, Vector2 currentMouse, Vector2 delta)
    {
        var cam = ResolveCamera();
        if (cam == null)
            return;

        if (cam.orthographic)
        {
            var scale = Mathf.Max(0.1f, _mapViewSize) * 0.0025f;
            _mapViewCenter += (-cam.transform.right * delta.x + cam.transform.up * delta.y) * scale;
            _mapViewCenter.y = 0f;
            StabilizeMapCameraPosition();
            return;
        }

        Vector3 previousGround;
        Vector3 currentGround;
        if (TryGetGroundPoint(cam, previousMouse, out previousGround) && TryGetGroundPoint(cam, currentMouse, out currentGround))
        {
            cam.transform.position += previousGround - currentGround;
            return;
        }

        var perspectiveScale = Mathf.Max(0.1f, Vector3.Distance(cam.transform.position, GetViewPivot())) * 0.0015f;
        cam.transform.position += (-cam.transform.right * delta.x + cam.transform.up * delta.y) * perspectiveScale;
    }

    private static bool TryGetGroundPoint(Camera cam, Vector2 guiMousePosition, out Vector3 point)
    {
        point = Vector3.zero;
        if (cam == null)
            return false;

        var ray = SC2TopViewMirror.ScreenPointToRay(cam, guiMousePosition);
        var ground = new Plane(Vector3.up, Vector3.zero);
        float enter;
        if (!ground.Raycast(ray, out enter))
            return false;

        point = ray.GetPoint(enter);
        return true;
    }

    private void ZoomCamera(float delta)
    {
        var cam = ResolveCamera();
        if (cam == null)
            return;

        if (cam.orthographic)
        {
            _mapViewSize = Mathf.Clamp(_mapViewSize * (1f + delta * 0.06f * ZoomSpeed), 16f, 8192f);
            cam.orthographicSize = _mapViewSize;
        }
        else
            cam.transform.position += cam.transform.forward * (-delta * 2f * ZoomSpeed);
    }

    private void LookAlong(Vector3 cameraForward, Vector3 up, string label)
    {
        var cam = ResolveCamera();
        if (cam == null)
            return;

        var pivot = GetViewPivot();
        var targetRotation = Quaternion.LookRotation(cameraForward.normalized, up.normalized);
        Vector3 targetPosition;
        if (cam.orthographic && cameraForward.normalized == Vector3.down)
        {
            _mapViewCenter = new Vector3(pivot.x, 0f, pivot.z);
            targetPosition = _mapViewCenter - (targetRotation * Vector3.forward) * DefaultTopDownHeight;
        }
        else
        {
            var distance = Mathf.Max(DefaultTopDownHeight, Vector3.Distance(cam.transform.position, pivot));
            targetPosition = pivot - (targetRotation * Vector3.forward) * distance;
        }
        StartViewTransition(cam, targetPosition, targetRotation);
        // Only "Top" matches the game's own top-down screen convention (see
        // SC2TopViewMirror) - every other preset (X/-X/Z/-Z/Bottom) must
        // leave it off.
        SC2TopViewMirror.Active = label == "Top";
        SetStatus("View axis: " + label);
    }

    /// <summary>True from OnEnable until the first Update()/EditorTick() —
    /// applies ResetView(instant: true) exactly once so the very first
    /// rendered frame is already in the correct, mirrored top-down
    /// orientation, not just after the animated ResetView() the button
    /// normally triggers. Deferred out of OnEnable itself because the
    /// resolved Camera isn't guaranteed ready that early (see ResolveCamera's
    /// GetComponentInParent/Camera.main/FindFirstObjectByType fallbacks).</summary>
    private bool _pendingInitialTopView;

    private void ApplyPendingInitialTopView()
    {
        if (!_pendingInitialTopView) return;
        if (ResolveCamera() == null) return;
        ResetView(instant: true);
        _pendingInitialTopView = false;
    }

    /// <summary>The map's true geometric center, from its actual grid size — NOT
    /// DefaultMapCenter, which is only correct for a 1024-world-unit map (512,0,512 happens to
    /// be exactly its center). A 512 or 2048 map has a different center; ResetView using the
    /// hardcoded constant regardless of map size was landing off-center on anything but 1024.</summary>
    private Vector3 ComputeMapCenter()
    {
        var data = _terrainShell?.Data;
        if (data != null && data.IsInitialized && data.GridWidth > 0 && data.GridHeight > 0 && data.CellSize > 0f)
            return new Vector3(data.GridWidth * data.CellSize * 0.5f, 0f, data.GridHeight * data.CellSize * 0.5f);
        return DefaultMapCenter;
    }

    /// <summary>
    /// Returns the camera to its starting top-down overview — pan, zoom and
    /// rotation all reset to the same defaults used on first load. Unlike
    /// LookAlong (used by the gizmo's axis balls), this ignores the current
    /// selection/pivot entirely: it's a hard reset, not a "look from this
    /// axis toward whatever I'm currently focused on".
    /// </summary>
    private void ResetView() => ResetView(instant: false);

    /// <param name="instant">Skips the animated fly-to transition and snaps
    /// the transform directly — used once, on the very first tick after
    /// load (see _pendingInitialTopView), so the correct top-down
    /// orientation AND its mirror are both already in effect on the first
    /// rendered frame, not only after the user manually presses Reset
    /// View.</param>
    private void ResetView(bool instant)
    {
        var cam = ResolveCamera();
        if (cam == null)
            return;

        _mapViewCenter = ComputeMapCenter();
        _mapViewSize = 512f;
        if (cam.orthographic)
            cam.orthographicSize = _mapViewSize;

        var targetRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        var targetPosition = _mapViewCenter - (targetRotation * Vector3.forward) * DefaultTopDownHeight;
        if (instant)
        {
            _viewTransitionActive = false;
            cam.transform.SetPositionAndRotation(targetPosition, targetRotation);
        }
        else
        {
            StartViewTransition(cam, targetPosition, targetRotation);
        }
        SC2TopViewMirror.Active = true; // ResetView always lands on the "Top" orientation
        SetStatus(instant ? "View ready." : "View reset.");
    }

    /// <summary>
    /// Mirrors the camera's vertical screen axis (see SC2TopViewMirror's
    /// doc comment) for exactly this camera's own render call — nothing
    /// else. camera.projectionMatrix and GL.invertCulling are both reset
    /// again in OnPostRender below, so no other rendering (other cameras,
    /// UI, gizmos) is ever affected, and cam.ScreenPointToRay/
    /// WorldToScreenPoint see the camera's normal, unmirrored projection at
    /// every other point in the frame (OnGUI, input handling) — those go
    /// through SC2TopViewMirror.ScreenPointToRay/WorldToGui instead, which
    /// compensate without needing this matrix held mirrored.
    /// </summary>
    private void ApplyTopViewMirrorPreRender(Camera cam)
    {
        if (!SC2TopViewMirror.Active || cam != ResolveCamera()) return;

        var m = cam.projectionMatrix;
        m.m11 *= -1f; // flip the vertical (Z-on-screen) axis only — m00 (X) untouched
        cam.projectionMatrix = m;
        GL.invertCulling = true; // compensate winding for the mirrored projection
    }

    private void ApplyTopViewMirrorPostRender(Camera cam)
    {
        if (!SC2TopViewMirror.Active || cam != ResolveCamera()) return;

        cam.ResetProjectionMatrix();
        GL.invertCulling = false;
    }

    private void StartViewTransition(Camera cam, Vector3 targetPosition, Quaternion targetRotation)
    {
        _viewTransitionActive = true;
        _viewTransitionStart = Time.realtimeSinceStartup;
        _viewTransitionStartPosition = cam.transform.position;
        _viewTransitionStartRotation = cam.transform.rotation;
        _viewTransitionTargetPosition = targetPosition;
        _viewTransitionTargetRotation = targetRotation;
    }

    private void TickViewTransition()
    {
        if (!_viewTransitionActive)
            return;

        var cam = ResolveCamera();
        if (cam == null)
        {
            _viewTransitionActive = false;
            return;
        }

        var t = Mathf.Clamp01((Time.realtimeSinceStartup - _viewTransitionStart) / ViewTransitionDuration);
        t = t * t * (3f - 2f * t);
        cam.transform.position = Vector3.Lerp(_viewTransitionStartPosition, _viewTransitionTargetPosition, t);
        cam.transform.rotation = Quaternion.Slerp(_viewTransitionStartRotation, _viewTransitionTargetRotation, t);

        if (t >= 1f)
            _viewTransitionActive = false;

        RepaintGameView();
    }

    private Vector3 GetViewPivot()
    {
        var selected = Selection.activeTransform;
        if (selected != null)
            return selected.position;

        var cam = ResolveCamera();
        if (cam != null)
        {
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var ground = new Plane(Vector3.up, Vector3.zero);
            float enter;
            if (ground.Raycast(ray, out enter))
                return ray.GetPoint(enter);
        }

        return ComputeMapCenter();
    }

    /// <summary>Dispatches by action id rather than a literal KeyCode so a
    /// rebound Camera.Move* key still resolves to the right direction
    /// instead of this table silently going stale.</summary>
    private bool FlyCamera(string cameraActionId)
    {
        var cam = ResolveCamera();
        if (cam == null)
            return false;

        // Forward/Back/Left/Right are flattened to the horizontal plane so
        // they never drift the camera's height — only Up/Down (Vector3.up)
        // should do that. Without this, Forward/Back along the camera's raw
        // (downward-tilted) look direction doubled as an unintended zoom
        // toward/away from the terrain.
        var direction = Vector3.zero;
        switch (cameraActionId)
        {
            case "Camera.MoveForward": direction += HorizontalOnly(cam.transform.forward); break;
            case "Camera.MoveBack": direction -= HorizontalOnly(cam.transform.forward); break;
            case "Camera.MoveRight": direction += HorizontalOnly(cam.transform.right); break;
            case "Camera.MoveLeft": direction -= HorizontalOnly(cam.transform.right); break;
            case "Camera.MoveUp": direction += Vector3.up; break;
            case "Camera.MoveDown": direction -= Vector3.up; break;
        }

        if (direction == Vector3.zero)
            return false;

        cam.transform.position += direction.normalized * 2f;
        return true;
    }

    /// <summary>Projects onto the XZ plane and re-normalizes — falls back to
    /// Vector3.zero on the near-straight-down-look degenerate case instead
    /// of normalizing a near-zero vector into a NaN.</summary>
    private static Vector3 HorizontalOnly(Vector3 v)
    {
        var flat = new Vector3(v.x, 0f, v.z);
        return flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.zero;
    }

    private static readonly string[] CameraMoveActionIds =
    {
        "Camera.MoveForward", "Camera.MoveBack", "Camera.MoveLeft",
        "Camera.MoveRight", "Camera.MoveUp", "Camera.MoveDown",
    };

    /// <summary>Which Camera.Move* action (if any) this KeyDown event
    /// matches — used by the single-event fly-while-RMB-held path in
    /// HandleGameViewNavigation, as opposed to TickFlyCamera's own
    /// per-frame GetKey polling for continuous hold.</summary>
    private static string ResolveCameraMoveAction(Event e)
    {
        foreach (var actionId in CameraMoveActionIds)
            if (SC2Hotkeys.Matches(actionId, e))
                return actionId;
        return null;
    }

    private void StabilizeMapCameraPosition()
    {
        var cam = ResolveCamera();
        if (cam == null || !cam.orthographic)
            return;

        _mapViewSize = Mathf.Clamp(_mapViewSize <= 0f ? cam.orthographicSize : _mapViewSize, 16f, 8192f);
        cam.transform.position = _mapViewCenter - cam.transform.forward * DefaultTopDownHeight;
        cam.orthographicSize = _mapViewSize;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 10000f;
    }

    private void FocusCamera()
    {
        var cam = ResolveCamera();
        var selection = Selection.activeTransform;
        if (cam == null || selection == null)
            return;

        var bounds = CalculateBounds(selection.gameObject);
        if (cam.orthographic)
        {
            _mapViewCenter = new Vector3(bounds.center.x, 0f, bounds.center.z);
            _mapViewSize = Mathf.Max(16f, Mathf.Max(bounds.extents.x, bounds.extents.z) * 2.5f);
            StabilizeMapCameraPosition();
        }
        else
        {
            cam.transform.position = bounds.center - cam.transform.forward * Mathf.Max(20f, bounds.extents.magnitude * 2.5f);
        }
        Status = "Focused: " + selection.name;
    }

    private static Bounds CalculateBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(go.transform.position, Vector3.one * 8f);

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    private void SyncOverlayFromContext()
    {
        if (SharedContext == null)
            return;

        if (OverlayTerrain != SharedContext.OverlayTerrain
         || OverlayHfield != SharedContext.OverlayHfield
         || OverlayCosts != SharedContext.OverlayCosts
         || OverlayCollision2 != SharedContext.OverlayCollision2
         || OverlayProps != SharedContext.OverlayProps
         || OverlayMarkers != SharedContext.OverlayMarkers
         || OverlayUnits != SharedContext.OverlayUnits
         || OverlayReferenceUnits != SharedContext.OverlayReferenceUnits
         || OverlayGraphLand != SharedContext.OverlayGraphLand
         || OverlayGraphAir != SharedContext.OverlayGraphAir
         || OverlayGraphNaval != SharedContext.OverlayGraphNaval
         || OverlayGraphAmphibious != SharedContext.OverlayGraphAmphibious
         || OverlayChains != SharedContext.OverlayChains
         || OverlayAreas != SharedContext.OverlayAreas
         || OverlayDecals != SharedContext.OverlayDecals
         || OverlayWater != SharedContext.OverlayWater
         || OverlayRNM != SharedContext.OverlayRNM
         || OverlayGrid != SharedContext.OverlayGrid
         || OverlayWireframe != SharedContext.OverlayWireframe)
        {
            OverlayTerrain = SharedContext.OverlayTerrain;
            OverlayHfield = SharedContext.OverlayHfield;
            OverlayCosts = SharedContext.OverlayCosts;
            OverlayCollision2 = SharedContext.OverlayCollision2;
            OverlayProps = SharedContext.OverlayProps;
            OverlayMarkers = SharedContext.OverlayMarkers;
            OverlayUnits = SharedContext.OverlayUnits;
            OverlayReferenceUnits = SharedContext.OverlayReferenceUnits;
            OverlayGraphLand = SharedContext.OverlayGraphLand;
            OverlayGraphAir = SharedContext.OverlayGraphAir;
            OverlayGraphNaval = SharedContext.OverlayGraphNaval;
            OverlayGraphAmphibious = SharedContext.OverlayGraphAmphibious;
            OverlayChains = SharedContext.OverlayChains;
            OverlayAreas = SharedContext.OverlayAreas;
            OverlayDecals = SharedContext.OverlayDecals;
            OverlayWater = SharedContext.OverlayWater;
            OverlayRNM = SharedContext.OverlayRNM;
            OverlayGrid = SharedContext.OverlayGrid;
            OverlayWireframe = SharedContext.OverlayWireframe;
        }
    }

    private void CommitOverlayToContext()
    {
        if (SharedContext == null)
            return;

        SharedContext.ApplyOverlayState(
            OverlayTerrain, OverlayHfield, OverlayCosts, OverlayCollision2,
            OverlayProps, OverlayMarkers, OverlayUnits, OverlayReferenceUnits,
            OverlayGraphLand, OverlayGraphAir, OverlayGraphNaval, OverlayGraphAmphibious,
            OverlayChains, OverlayAreas, OverlayDecals, OverlayWater, OverlayRNM, OverlayGrid, OverlayWireframe);
    }
}
