using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// SC2Material, SC2MaterialPair and SymmetryMode moved to
// SC2TerrainMaterialModel.cs so they can be compiled into the standalone
// characterization test project without SC2TerrainData's own
// ScriptableObject dependency tree — see that file's header comment.

// SC2CostsBlockData moved to SC2TerrainMaterialModel.cs so it
// can be compiled into the standalone characterization test project (needed
// by SC2CostsLegacyBridge) without this file's own ScriptableObject
// dependency tree — see that file's header comment.

/// <summary>
/// Serialisable terrain data.
/// Heights stored as float[] — the only per-vertex data that needs to be serialised.
/// Geometry (positions, normals) is rebuilt at runtime by SC2TerrainRuntime.
/// Pair assignments stored as short[] (saves 50% vs int[] at 16k = 536M tris → 1GB vs 512MB).
/// </summary>
[CreateAssetMenu(fileName="SC2TerrainData",menuName="SC2/Terrain Data")]
public class SC2TerrainData : ScriptableObject, ISerializationCallbackReceiver
{
    public const float MinHeight = 0f;
    public const float MaxHeight = 512f;
    public const float DefaultFlatHeight = 100f;

    [Header("Map")]
    public string MapName="NewMap";
    /// <summary>The map's real in-game display name (_scenario.lua's
    /// ScenarioInfo.name, shown in the game's own map browser) — genuinely
    /// separate from MapName above, confirmed against a real vanilla map:
    /// name='!Eclipse map' while MapName='SC2_D3_Eclipse'
    /// is the technical identifier used for every file/folder name (terrain.bdf,
    /// maps/&lt;MapName&gt;/, the map/save/script path fields). Previously this
    /// editor conflated the two — MapName drove ScenarioInfo.name directly — so
    /// an imported map's real display name was silently lost on the very next
    /// export. Empty by default; SC2ScenarioShell.LoadFromNewData falls back to
    /// MapName only for a from-scratch map with no real display name yet.</summary>
    public string DisplayName="";
    public int    GridWidth=64, GridHeight=64;
    public float  CellSize=2f;
    public bool   IsInitialized;

    /// <summary>Raw BGRA32 pixels (448x206x4 = 369152 bytes) for the map's
    /// "_PC_mapshot.dds" export — the user-framed angled screenshot shown in
    /// a real game's map-select UI, captured once via the Export tab's
    /// "Capture Preview" button (SC2MapPreviewCapture) and re-used at every
    /// export until re-captured, unlike the minimap (which always renders
    /// fresh at export time — see SC2TerrainExporter.BuildExportArtifacts).
    /// Null until the user captures or imports one; export then writes a black
    /// image with the vanilla frame instead. Deliberately not part of the undo stack (see
    /// SC2TerrainShell.CaptureAuxState) — which camera angle was last
    /// screenshotted isn't an editing operation Ctrl+Z is expected to touch,
    /// unlike terrain edits.</summary>
    public byte[] MapShotBgraPixels;

    /// <summary>A custom minimap picture (1024x1024 PNG bytes) imported on Scenario > Preview. When set, every
    /// minimap (export, previews, the lobby spawn editor) uses it instead of rendering the map. Null = render.</summary>
    public byte[] CustomMinimapPng;

    /// <summary>A real "the live procedural plane
    /// contributes nothing to the shipped map" switch, for maps built purely
    /// from an imported terrain.bdf. The live SC2TerrainData still technically
    /// exists (IsInitialized/EnsurePairAssignments/every mode-gate keep
    /// working unchanged — nothing here assumes the live mesh can be null or
    /// empty), but SC2TerrainLayerBuilder.Build returns zero layers whenever
    /// this is set, so BuildExportArtifacts/SC2RnmBakeService never fold the
    /// live plane's geometry into the merged model at all.
    ///
    /// Before this existed, MergeImportedTerrain always appended the imported
    /// mesh ON TOP OF the live plane with no spatial exclusion — real,
    /// confirmed z-fighting where the two overlapped, and the live plane's
    /// own material showing through wherever the import didn't cover it
    /// (visible in-game as thin flickering seams and pale rectangular
    /// patches). This flag is the escape hatch for a pure-import workflow;
    /// a map that paints its own terrain leaves it off and nothing changes.</summary>
    public bool   ImportOnlyMode;

    /// <summary>DEAD — kept only so old saved maps and undo snapshots that persisted a value still deserialize. It used to make
    /// the exported terrain.bdf one EDB/BSP leaf per layer, a workaround for bright axis-aligned bands on a Blender import
    /// during the building-construction effect; the export now always recomputing the imported tangent frame made it unnecessary. SC2TerrainExporter no longer reads it and the Imported tab has no toggle for it.</summary>
    public bool   SingleBspNodePerLayer;

    /// <summary>DEAD — kept only so old saved maps/undo snapshots that
    /// persisted a value for this field still deserialize without error;
    /// SC2TerrainExporter.BuildExportArtifacts no longer reads it at all
    /// and the Terrain tab's toggle for it was removed: terrain.bdf always
    /// uses the vanilla-candidate splitter now, matching decals.bdf/
    /// collision2.bdf, which already used it unconditionally. See
    /// TerrainBspBuilder.FindBestVanillaCandidateSplit's own doc comment
    /// for why this splitter (reverse-engineered from SC2-NO-ENC.EXE) is
    /// the measured-correct choice — corpus-validated against all 56 real
    /// vanilla terrain.bdf files (mean shipped node-count ratio 1.00) vs.
    /// the full-sweep SAH search this field used to let you fall back to
    /// (24x worse by SAH's own cost metric on the same corpus). That SAH
    /// implementation (TerrainBspBuilder.FindBestSahSplit) itself is NOT
    /// removed — it's still real, working code, just no longer reachable
    /// from the main export path (the legacy Editor/TerrainMeshEditorWindow.cs
    /// tool still defaults to it).</summary>
    public bool   VanillaCandidateBspSplit = true;

    [Header("Materials")] public List<SC2Material>    Materials=new();
    [Header("Pairs")]     public List<SC2MaterialPair> Pairs=new();

    /// <summary>Editing layers of the blend mask, one stack per mask name (the name pairs share in
    /// SC2MaterialPair.RnmMaskName). The stack is the source of truth for that mask; the pairs'
    /// RnmMaskBytes stay the flattened composite the shader and the exporter read. A mask name without a
    /// stack is a map painted before layers existed: it gets a one-layer stack from its mask on first use.</summary>
    [Header("Blend mask layers")] public List<SC2BlendMaskLayerStack> BlendLayerStacks = new();

    public SC2BlendMaskLayerStack FindBlendLayerStack(string maskName)
    {
        if (BlendLayerStacks == null) return null;
        foreach (var stack in BlendLayerStacks)
            if (stack != null && string.Equals(stack.MaskName, maskName, StringComparison.Ordinal)) return stack;
        return null;
    }

    // Heights: (GridWidth+1)*(GridHeight+1) floats — only serialised data for geometry
    public float[] Heights=Array.Empty<float>();

    // Per-triangle pair assignment: short (max 32767 pairs >> 72 limit)
    // -1 = unassigned. Stored as short[] for memory efficiency at 16k grids.
    public short[] TrianglePairIndex=Array.Empty<short>();
    [NonSerialized] public int[] PairTriCounts=Array.Empty<int>(); // cached from last sync

    [Header("Symmetry")]
    public SymmetryMode Symmetry=SymmetryMode.None;
    public Vector3      SymmetryCenter;
    /// <summary>Only meaningful when Symmetry==Radial. 2..8 inclusive,
    /// including odd counts (3/5/7-player maps) — clamp at the point of use,
    /// not here, so a bad value round-tripped from an old save self-heals
    /// rather than silently resetting to a default.</summary>
    public int          RadialCount=4;

    // The RNM blend-mask brush (Paint tab, Blend sub-mode) can optionally
    // stamp an arbitrary loaded image (PNG etc.) instead of the built-in
    // round cosine-falloff brush — Pair (hard material assignment) painting
    // is unaffected, keeps the round brush always. Persisted with the map
    // (not session-only tool state like Radius/Strength), so the chosen
    // brush survives Save/Load — CustomBrushTexturePath is a resource-path
    // string (SC2TerrainMaterialFactory.TryGetResourcePath's own output
    // convention, e.g. "file:..."/"archive:..."), re-resolved to a live
    // Texture on load via ResolveFromResourcePath, same as every other
    // texture-path field.
    [Header("Paint Brush")]
    public bool   UseCustomBrush;
    public string CustomBrushTexturePath = "";
    /// <summary>Irregular stamp brushes
    /// (World Machine/Gaea-style grayscale masks) can optionally get a
    /// fresh random rotation each stroke instead of always sampling the
    /// stamp axis-aligned, so repeated stamps don't look identically tiled.
    /// Only meaningful while UseCustomBrush is on — see SC2PaintMode.
    /// HandleRnm, which rolls a new angle once per stroke (mouse-down),
    /// not per drag frame.</summary>
    public bool   RandomizeBrushRotation;
    /// <summary>A SEPARATE toggle from RandomizeBrushRotation — that one rolls a fresh
    /// angle once per stroke (mouse-down) and holds it fixed for the whole
    /// drag; this one, when also on, re-rolls on every single application
    /// (mouse-down AND every drag frame), so continuously dragging keeps
    /// stamping fresh random orientations instead of one fixed one per
    /// stroke. Only meaningful while both UseCustomBrush and
    /// RandomizeBrushRotation are on.</summary>
    public bool   RandomizeBrushRotationContinuous;

    // Alongside RandomizeBrushRotation above: the same irregular-stamp-brush
    // capability, for the Geometry
    // sculpt tools (Raise/Lower/Smooth/Flatten/SetHeight/Noise). Same
    // shape/persistence rationale as the Paint Brush fields above — see
    // SC2GeometryMode's mirrored custom-brush picker and SC2TerrainRuntime.
    // SculptBrush/SculptSetHeight's brushMask params.
    [Header("Geometry Brush")]
    public bool   UseCustomGeometryBrush;
    public string CustomGeometryBrushTexturePath = "";
    public bool   RandomizeGeometryBrushRotation;
    /// <summary>Same shape/semantics as RandomizeBrushRotationContinuous
    /// above, for the Geometry sculpt tools.</summary>
    public bool   RandomizeGeometryBrushRotationContinuous;

    [Header("Costs / Terrain Types")]
    // Cell count of the Costs/TerrainTypes/hfield grid, independent of
    // GridWidth/GridHeight (the sculpting resolution) — costs and
    // terrainTypes are properties of the FINAL exported map footprint, not of
    // whatever resolution the terrain happened to be sculpted at.
    //
    // This used to be a hardcoded `const int = 1024`. Measured directly
    // against every real vanilla map's own hfield.bdf (56 maps): MapX/MapY
    // are NOT always 1025 (= 1024 cells) — some real shipped maps use 513
    // (512 cells) and others use 2049 (2048 cells). A hardcoded 1024 made it
    // impossible to build a map at any of those other real, shipped sizes.
    // Now a real
    // per-map setting (SC2CostsShell exposes it as a size picker), still
    // defaulting to 1024 (the most common vanilla size) for every existing
    // map and every from-scratch InitializeFlatPlane call, so nothing
    // changes unless the user deliberately picks a different size.
    //
    // Changing this at runtime relies on EnsureCostsAndTerrainTypes' existing
    // mismatched-length recreate-as-default behavior (already there for
    // legacy-asset upgrades) to resize CostsBlocks/TerrainTypes — which does
    // mean previously-painted Costs/TerrainTypes data is lost on a real size
    // change, same as it would be on any other grid-dimension change.
    public int CostsGridSize = 1024;

    /// <summary>Material names (matched against TerrainMaterial.Name, real
    /// or imported) to skip ONLY when building Collision2's own TIN mesh —
    /// deliberately NOT applied to the shared hfield.bdf projection or to
    /// Costs (live height sampling/auto-generation), which must keep seeing
    /// every material's real height (a from-scratch confirmed requirement:
    /// excluding a material here must not make it disappear from hfield/
    /// Costs too, only stop it from consuming Collision2's own limited
    /// mesh budget). For decorative embedded geometry (rock/crystal
    /// clusters, etc. baked into an imported terrain.bdf as their own
    /// material) that isn't real walkable ground and shouldn't consume
    /// Collision2's limited 2500-triangle TIN budget refining its complex
    /// shape. User-marked via a per-material checkbox in the Imported
    /// Terrain panel — there's no reliable name-pattern heuristic for
    /// "this is decorative" the way "water" is for water materials. See
    /// SC2TerrainExporter.BuildCollision2Model, which projects its OWN
    /// second heightmap with this exclusion applied, separate from the
    /// shared hfieldModel every other consumer uses.</summary>
    public List<string> Collision2ExcludedMaterialNames = new();

    /// <summary>Whether this map has playable water — deliberately its own
    /// dedicated Costs-panel toggle, NOT derived from InfoModel.WaterEnabled
    /// (EnvironmentHeader): that field controls whether the decorative water
    /// surface renders at all (a visual setting), which is a different
    /// question from whether naval/amphibious units should have any
    /// passability grid here. Drives which real vanilla Costs block-set
    /// EnsureCostsAndTerrainTypes creates (LandOnlyCostsBlockNames vs
    /// PlayableWaterCostsBlockNames) — see SC2CostsShell's toggle, which
    /// also regenerates CostsBlocks live when this changes (with a confirm
    /// dialog if anything already looks hand-painted).</summary>
    public bool PlayableWater;

    /// <summary>When true, real map export writes CustomCollision2Bytes
    /// directly as collision2.win.bdf instead of calling
    /// SC2TerrainExporter.BuildCollision2Model to auto-generate one from the
    /// current terrain. Set by SC2Collision2Shell's "Use this mesh for map
    /// export" checkbox after importing a hand-corrected mesh (Export Mesh
    /// To Blender -> fix -> Import Corrected Mesh) or an existing
    /// collision2.bdf — before this existed, every real export silently
    /// discarded any imported/corrected Collision2 mesh and regenerated a
    /// fresh one from the terrain regardless, so the Collision2 tab's
    /// Import buttons had no way to ever actually affect the exported map —
    /// importing a custom collision mesh from either OBJ or collision2.bdf
    /// had no effect on the final export.</summary>
    public bool UseCustomCollision2;

    /// <summary>The real, already-serialized BdfContainer bytes of the
    /// custom Collision2 mesh (built via Collision2Bdf.Serialize the same
    /// way the real export path itself does) — written directly to
    /// collision2.win.bdf when UseCustomCollision2 is set, bypassing
    /// auto-generation entirely.</summary>
    public byte[] CustomCollision2Bytes;

    /// <summary>The Collision2 tab's current preview mesh (last Generate or import), as the same
    /// BdfContainer bytes CustomCollision2Bytes uses, so the preview survives an editor restart
    /// instead of showing "Not generated yet". Cleared whenever the preview goes stale (Height
    /// Offset / Polygon Budget changed). Never exported by itself — see UseCustomCollision2.</summary>
    public byte[] Collision2PreviewBytes;

    /// <summary>Preview view settings of the Collision2 tab. Stored negated / as plain flags so a
    /// save from before they existed loads with the preview shown and solid.</summary>
    public bool Collision2HidePreview;
    public bool Collision2Wireframe;

    /// <summary>World-space size (both X and Z) of the from-scratch water
    /// plane SC2WaterPlane generates — deliberately independent of
    /// CostsGridSize (which sizes the Costs/Hfield grid, a different,
    /// gameplay-passability concern) so the visible water surface can
    /// extend past, or stop short of, the playable footprint. Default 4096
    /// matches this codebase's largest confirmed real vanilla map size.</summary>
    public int WaterPlaneSize = 4096;

    /// <summary>13 texture-path slots for the from-scratch water plane's
    /// exported "Water" material (SC2WaterPlane), same TexturePaths[13]
    /// layout every other TerrainMaterial uses (Diffuse1/2, Normal1-4,
    /// Mask1/2, Texture5/6, RNM Volume, Environment, Texture8 — A.5).
    /// These are plain path STRINGS written into terrain.bdf, not Unity
    /// asset references — unlike SC2Material's Diffuse/Normal/Mask
    /// Texture2D fields (which need a real local asset this project can
    /// preview), the real DDS files these point at are never bundled here
    /// (copyrighted game assets); the user's own real game installation
    /// resolves them at load time, exactly like every real vanilla map's
    /// own terrain.bdf already does. Defaults to the real vanilla water
    /// material's own texture set (confirmed against the corpus) except
    /// slot 6 (waterDepth — genuinely map-specific),
    /// left blank here and filled in from MapName at export time unless the
    /// user has overridden it. Editable in the Terrain tab's Materials
    /// panel (SC2MaterialsMode) for anyone who wants different water
    /// textures.</summary>
    public string[] WaterTexturePaths =
    {
        "/textures/Terrain/Water/foam_001.dds",   // 0  Diffuse1
        "/textures/Terrain/Water/foam_002.dds",   // 1  Diffuse2
        "/textures/Terrain/Water/waves_001.dds",  // 2  Normal1
        "/textures/Terrain/Water/waves_002.dds",  // 3  Normal2
        "/textures/Terrain/Water/waves_003.dds",  // 4  Normal3
        "/textures/Terrain/Water/waves_004.dds",  // 5  Normal4
        "",                                        // 6  Mask1 (waterDepth — map-specific, see SC2WaterPlane)
        "",                                        // 7  Mask2
        "/textures/Terrain/Water/wave_animation_001.dds", // 8  Texture5
        "",                                        // 9  Texture6
        "",                                        // 10 RNM Volume
        "/environment/u05_env.dds",               // 11 Environment
        "",                                        // 12 Texture8
    };

    /// <summary>World units per UV1/UV2 tile for the exported water plane
    /// (SC2WaterPlane.AppendToExportModel divides world X/Z by this to get
    /// each corner's UV1/UV2). A real editable field rather than a
    /// hardcoded 1024f, since a non-1024 map (see
    /// SC2TerrainData.CostsGridSize's own hardcoded-1024 caveat) or simply a
    /// different desired wave/foam tiling density needs this tunable rather
    /// than fixed. Also drives the live in-editor preview's own UV0 (see
    /// SC2WaterPlane.UpdatePreviewMesh) so the preview visually reflects
    /// whatever this is set to, not a fixed 0..1 unrelated to world scale.</summary>
    public float WaterUvScale = 1024f;

    /// <summary>When on, the Mask1 (waterDepth) texture referenced by
    /// WaterTexturePaths[6] is generated automatically at export time from
    /// this map's own real heightfield vs. WaterElevation, instead of using
    /// whatever texture (if any) is manually assigned to that slot — see
    /// SC2WaterDepthTextureGenerator.Generate. No tool
    /// in this project ever actually produced the real per-map
    /// "&lt;mapName&gt;.waterDepth.dds" file SC2WaterPlane.BuildWaterMaterial
    /// already defaults slot 6's path to, so without a manually-supplied
    /// texture that reference always pointed at a file that doesn't exist.
    /// When this is off, export behaves exactly as before (manual slot 6
    /// path only, or none).</summary>
    public bool WaterDepthAutoGenerate = false;

    /// <summary>Width/height (texels, always equal — square texture) of the
    /// generated waterDepth texture. Must be a multiple of 4 (DXT5 block
    /// size, see SC2DdsEncoder.BuildDxt5) — clamped to the nearest valid
    /// value wherever it's used, never asserted here so a stale saved value
    /// from a UI change (e.g. resolution list edit) never throws.</summary>
    public int WaterDepthResolution = 512;

    /// <summary>World units of shoreline-to-open-water distance over which
    /// generated alpha ramps from WaterDepthMinAlpha up to fully opaque
    /// (1.0) — a wider margin spreads the foam gradient (SC2Water.
    /// shader's shoreColor term reads alpha near 0 as strong foam, see that
    /// shader's own FoamFalloffPower comment) further out from the actual
    /// shoreline.</summary>
    public float WaterDepthMargin = 64f;

    /// <summary>Exponent shaping the alpha ramp across WaterDepthMargin:
    /// t = clamp01(distanceFromShore / WaterDepthMargin), alpha = lerp(
    /// WaterDepthMinAlpha, 1, t^WaterDepthFalloffPower). 1 = linear; &gt;1
    /// keeps alpha low (strong foam) longer right at the shore before
    /// rising sharply; &lt;1 rises immediately.</summary>
    public float WaterDepthFalloffPower = 2f;

    /// <summary>Alpha value written for a water texel sitting exactly at the
    /// shoreline (distance 0) — must stay above 0 (SC2Water.shader treats
    /// alpha==0 as "no water here at all", distinct from "water, but with
    /// maximum foam"), so this is a small positive floor rather than 0.</summary>
    public float WaterDepthMinAlpha = 0.04f;

    /// <summary>How many texels of the generated waterDepth texture's land side get a non-zero
    /// alpha instead of the usual hard 0 cutoff right at the coastline — i.e. how far the
    /// water-depth pixels extend into the terrain boundary. Applies to the Game View preview
    /// (on Regenerate Preview) and to the exported .waterDepth.dds. 0 = old hard-cutoff
    /// behavior.</summary>
    public float WaterDepthLandBleedTexels = 10f;

    /// <summary>When on, forces the outermost ring of texels of the
    /// generated waterDepth texture to read as water (alpha 1, ignoring the
    /// real land/water classification there) — for a water body
    /// that runs off the edge of the analyzed heightfield rect so the mask
    /// doesn't fade to "no water" right where the water actually continues
    /// past the texture's own bounds.</summary>
    public bool WaterDepthPaintBorder = true;

    /// <summary>When on, forces a hard 1-texel-wide ring of fully-opaque
    /// water color (alpha 255) around the OUTER EDGE of the generated
    /// texture itself (x/z == 0 or texSize-1) — a visible solid frame around
    /// the whole texture. Applied last, after every other pass (including
    /// WaterDepthPaintBorder and the shore-edge alpha shift — see
    /// SC2WaterDepthTextureGenerator.ShoreOutlineWidthTexels), so turning
    /// this on always wins the outer ring. Same "full alpha, not zero"
    /// reasoning as the shore-edge shift — a zero-alpha ring wouldn't read
    /// as a visible outline, just a gap.</summary>
    public bool WaterDepthImageBorderOutline = false;

    /// <summary>Optional low-detail decorative terrain surrounding the
    /// playable core on all sides — a real second SC2TerrainData instance
    /// (BackgroundTerrainData below), not a one-off mesh, sharing the
    /// core's own CellSize/grid alignment so the boundary between them can
    /// be resolved by SC2BackgroundTerrain's per-cell hole-carving (no
    /// clipping) and the SAME SC2TerrainSimplifier pipeline the core
    /// itself uses. Off by default: disabling this leaves export byte-for-
    /// byte identical to before this feature existed.</summary>
    public bool BackgroundTerrainEnabled;

    /// <summary>The background's own live grid/heights/materials/pairs —
    /// generated/refreshed by SC2BackgroundTerrain.Generate (regenerated
    /// deterministically from this core data + BackgroundTerrainSize on
    /// every call except for the one thing a user actually tunes, its
    /// single pair's SimplifyRatio, which is preserved across
    /// regeneration). Not a ScriptableObject asset on disk — created with
    /// HideFlags.DontSave, exactly like SC2WaterPreview's own preview
    /// objects — only the handful of scalar fields below are ever
    /// persisted (Undo/document), not this whole nested object.</summary>
    public SC2TerrainData BackgroundTerrainData;

    /// <summary>World-space outer size (both X and Z) of the background's
    /// square footprint, centered on the core's own rectangle. Rounded
    /// outward to the core's CellSize by SC2BackgroundTerrain.ComputeBounds
    /// so every background grid line coincides exactly with a core grid
    /// line — see that method's own doc comment.</summary>
    public int BackgroundTerrainSize = 4096;

    /// <summary>World units per UV1 tile for the background's exported
    /// material — superseded by GlobalUvScale below (no longer read by any
    /// live code path — see that field's own doc comment); left in place
    /// only for backward-compat with already-saved documents/undo
    /// snapshots that still carry a value here.</summary>
    public float BackgroundTerrainUvScale = 1024f;

    /// <summary>Kept only so saved maps and undo snapshots that carry it still load: the shared tiling size below is always in
    /// use now, there is no per-pair tiling any more. A map saved with this off is moved to the shared size by
    /// EnsureSerializableState (it takes its first pair's old size, so the look does not change), which sets this on.</summary>
    public bool GlobalUvScaleEnabled;

    /// <summary>The one UV1 tiling scale (world units per texture tile) for EVERY pair on both the playable core and the
    /// decorative background — guarantees the texture tiling frequency can never mismatch at the core/background seam (or
    /// between two core pairs). Lives on the core's own SC2TerrainData (not the background's nested instance) since it's one
    /// map-wide setting, not something either "zone" owns independently — background code reads it off the coreData
    /// reference it already carries everywhere (same pattern as BackgroundTerrainSize/Enabled).</summary>
    public float GlobalUvScale = 4f;

    /// <summary>Amplifies height's contribution to SC2TerrainUv1Unwrap's
    /// per-axis stretch profile (BuildProfiles multiplies each step's real
    /// height delta by this before folding it into the arc-length
    /// distance) — 1 is physically accurate (true 3D edge length), values
    /// above 1 deliberately exaggerate the correction so a steep area
    /// claims even more UV space than its real surface distance alone
    /// would give it. Same map-wide, core-owned setting as GlobalUvScale
    /// just above (applies to core AND background alike).</summary>
    public float Uv1HeightWeight = 1f;

    /// <summary>The UV1 tiling scale to actually use for <paramref name="pair"/>: the shared GlobalUvScale (floored), for every
    /// pair. Centralized here so the live preview/export/RNM-bake geometry builders — core and background alike — can never
    /// independently disagree. The pair argument is kept for the callers' sake; a pair's own TilingScale is no longer read.</summary>
    public float EffectiveUvTilingScale(SC2MaterialPair pair) => Mathf.Max(0.0001f, GlobalUvScale);

    public List<SC2CostsBlockData> CostsBlocks = new();
    public ushort[] TerrainTypes = Array.Empty<ushort>();

    /// <summary>Real vanilla TerrainTypes id for "Default" (plain terrain,
    /// no special surface) — verified against an actual hfield.bdf, where
    /// the only two values that occur are 2 ("Default") and 3
    /// ("Chasm_Bottom"). A freshly-initialized grid must start at 2, not 0 —
    /// 0 isn't a real/known id at all.</summary>
    public const ushort DefaultTerrainTypeId = 2;

    [Header("Collision2")]
    /// <summary>Uniform vertical shift applied to every Collision2 mesh
    /// vertex after generation (SC2Collision2MeshBuilder computes heights/
    /// error purely from the raw hfield — this is a separate, final offset
    /// on top of that). Raises the collision surface slightly above the
    /// visual terrain so camera/air-unit collision doesn't clip into small
    /// visual overlaps or float below the ground at seams. Default 0.25
    /// world units.</summary>
    public float Collision2HeightOffset = 0.25f;

    /// <summary>Ground TIN triangle budget for Collision2 generation
    /// (SC2Collision2MeshBuilder's maxTriangles) — user-adjustable 2500
    /// (the vanilla-observed convention, kept as the default) up to 10000.
    /// The binary format itself has no fixed-size ceiling here (vertex/
    /// triangle counts are plain UInt32 fields in Collision2Bdf.Serialize),
    /// so this is a real, format-safe budget increase, not just a display
    /// change — see SC2Collision2Shell's slider and
    /// SC2CollisionDocument.SC2CollisionGenerationSettings.MaxPolygons
    /// (the document-side field this legacy fallback mirrors).</summary>
    public int Collision2PolygonBudget = 2500;

    [Header("RNM Lighting Bake")]
    /// <summary>When true (default), SC2RnmBakeService.Bake reads this
    /// parameter live from Environment (info.bdf) at bake time, same as
    /// before per-parameter sync existed. When false, the matching
    /// RnmBakeOverride* field below is used instead and Environment changes
    /// have no effect on this parameter until re-enabled — lets the user
    /// lock in one bake input (e.g. keep a fixed SunDirection) while still
    /// tuning others live. Four independent flags, not one shared toggle,
    /// so each input can be locked or synced separately.</summary>
    public bool RnmBakeSyncSunDirection = true;
    public bool RnmBakeSyncSunColor = true;
    public bool RnmBakeSyncSunIntensity = true;
    public bool RnmBakeSyncShadowColor = true;
    /// <summary>Local override values, only read when the matching Sync flag
    /// above is false. Defaults mirror SC2RnmBakeService's own zero-SunDirection
    /// fallback and its reference-map-derived ShadowColor comment.</summary>
    public Vector3 RnmBakeOverrideSunDirection = new Vector3(0.3f, 0.8f, 0.3f);
    public Color RnmBakeOverrideSunColor = Color.white;
    public float RnmBakeOverrideSunIntensity = 1f;
    public Color RnmBakeOverrideShadowColor = new Color(0.64f, 0.77f, 0.92f, 1f);
    /// <summary>Shared by both Bake() and BakeBackground() (there is only
    /// one Lighting Bake config for the whole map, same as every field
    /// above) — the box-blur radius (in mask texels) SC2RnmBakeService's
    /// own BoxBlurOcclusion applies to the shadow horizon-march's hard 0/1
    /// result before it's baked in. Lower = sharper/harder shadow edges,
    /// higher = softer/wider penumbra. Default (6) matches the value this
    /// was hardcoded to before it became user-configurable, so an existing
    /// map's baked look doesn't change until the user actually touches
    /// this slider.</summary>
    public int RnmBakeShadowSoftRadius = 6;
    /// <summary>The caster/receiver pairs the lighting bake leaves out (see SC2ShadowCasting); empty = every object (core,
    /// background, each imported terrain.bdf) shadows every object. Shared by the core and the background bake, like the
    /// other Lighting Bake inputs.</summary>
    public List<string> RnmShadowExclusions = new();

    /// <summary>Bumped once per completed Bake()/BakeBackground() call (see
    /// SC2RnmBakeService.BakeInternal). NonSerialized — this only needs to
    /// invalidate in-memory caches for the current session, never persist.
    /// SC2TerrainRuntime.RebuildRnmArrays uses this to skip re-uploading its
    /// (expensive, full CPU-readback) baked-lighting Texture2DArray on every
    /// SetAllPairMaterials call while PreviewMode==4 is active — without it,
    /// simply leaving the RNM preview mode on while sculpting/painting (each
    /// of which calls ApplyPreviewModeToChunks to refresh other state) redid
    /// that full rebuild on every such call, a real perf regression that had
    /// no equivalent cost before real baked-data sampling existed.</summary>
    [NonSerialized] public int RnmBakeGeneration;

    /// <summary>Baked world-space RGBA texture covering the union of every imported terrain.bdf
    /// object's world bounds (SC2RnmBakeService.BakeImportedWorldRnm) — RGB is the SAME combined,
    /// contrast-stretched value SC2TerrainView.shader's own PreviewMode==4 live-terrain preview
    /// already computes per-pixel at runtime ((m0+m1+m2)*0.5774, then the RNM_CONTRAST stretch); A is
    /// 255 where a real triangle actually covered that texel (SC2RnmMaskBaker.MaskResult.Covered,
    /// before gap-fill), 0 elsewhere — SC2Decal.shader's own "is there real baked data here, or fall
    /// through to plain light" test. Empty means "no imported terrain, or never baked" — same idiom
    /// as SC2MaterialPair.RnmBakedRgb0's own doc comment.</summary>
    public byte[] ImportedRnmBakedRgba = Array.Empty<byte>();
    public int ImportedRnmWidth, ImportedRnmHeight;
    public float ImportedRnmWorldOriginX, ImportedRnmWorldOriginZ;
    public float ImportedRnmWorldSpanX = 1f, ImportedRnmWorldSpanZ = 1f;

    /// <summary>Mirrors SC2MaterialPair.HasBakedRnmLighting's own gate.</summary>
    public bool HasImportedRnmBake =>
        ImportedRnmBakedRgba.Length == ImportedRnmWidth * ImportedRnmHeight * 4 && ImportedRnmWidth > 0 && ImportedRnmHeight > 0;

    [Header("Point cloud (unit lighting)")]
    /// <summary>Environment > Point Clouds: the sky light colour and its intensity, how much of the baked ground
    /// light a unit picks up from below, how much sky light leaks into the lower hemisphere, the blur growth per
    /// slice (in texels), and whether the terrain's sun shadow goes into the alpha. See SC2PointCloudBaker.</summary>
    public Color PointCloudSkyColor = new Color(0.75f, 0.88f, 1f, 1f);
    public float PointCloudSkyIntensity = 0.5f;
    public float PointCloudGroundReflection = 0.5f;
    public float PointCloudSkyInGround = 0.35f;
    public float PointCloudBlurTexels = 1f;
    public bool PointCloudSunShadow = true;
    /// <summary>The box the point cloud is baked over (world units). A zero size means "not set yet": the bake then
    /// fits it to the map. Once volumes are baked, export writes this box into info.bdf's PointCloudOrigin/Size so
    /// the file and the volumes always match.</summary>
    public Vector3 PointCloudBoxOrigin;
    public Vector3 PointCloudBoxSize;
    /// <summary>The last baked volumes as complete DXT5 volume DDS files (pcloud_up / pcloud_down), null until
    /// Environment > Point Clouds > Bake Point Cloud has run. Export writes them as they are; the box they were baked for
    /// is the info.bdf PointCloudOrigin/Size at that moment.</summary>
    public byte[] PointCloudUpDds;
    public byte[] PointCloudDownDds;

    [Header("Environment (info.bdf)")]
    /// <summary>Raw 508-byte EnvironmentHeader (BdfIO.Formats.Info.InfoModel.Header
    /// layout, byte-for-byte) — every Sun/Water/Fog/Glow/etc. field InfoModel
    /// exposes is just a computed accessor over this same buffer, so storing
    /// it directly (rather than duplicating ~80 parallel fields on this
    /// class) keeps the procedural workflow's Environment data byte-identical
    /// to whatever InfoModel/InfoBdf would produce, with zero drift risk.
    /// SC2EnvironmentShell (Scripts/Unified) wraps this into an InfoModel for
    /// editing; SC2TerrainExporter writes it out as info.bdf at export time.
    /// Defaults to a real vanilla map's own Environment bytes
    /// (SC2EnvironmentDefaults, sourced from a real info.bdf)
    /// rather than all-zero — a from-scratch procedural map now
    /// starts with sensible Sun/Water/Fog/Glow values instead of a blank
    /// state nothing in this editor was ever exercised against.</summary>
    public byte[] EnvironmentHeader = SC2EnvironmentDefaults.CloneDefaultHeader();
    public string EnvironmentMap = "";
    public string SkyMap = "";
    public string TexturePath3 = "";
    public string ColorCorrectionCube = "";
    public string FogOfWarCorrectionCube = "";
    /// <summary>Per-slot provenance tag ("archive:&lt;entryPath&gt;" or "") and
    /// "copy into map on export" opt-in for the 5 texture paths above, in
    /// SC2EnvironmentTextureSlot order (EnvironmentMap, SkyMap, TexturePath3,
    /// ColorCorrectionCube, FogOfWarCorrectionCube) — see
    /// SC2EnvironmentTextureExport. Lives here too (not just in the document)
    /// because SC2EnvironmentLegacyBridge.BuildFromLegacy rebuilds the
    /// document's Environment from this data on a runtime save.</summary>
    public string[] EnvironmentTextureSourceTags = { "", "", "", "", "" };
    public bool[] EnvironmentTextureForceExport = new bool[5];

    /// <summary>Name of the Area (SC2ObjZone.Name, e.g. "AREA_01") passed to
    /// ScenarioFramework.SetPlayableArea(...) in the exported _script.lua's
    /// OnPopulate() — confirmed against a real vanilla _script.lua (user-
    /// supplied) to be effectively the ONLY per-map-varying line in that
    /// whole file, every other line is fixed engine boilerplate (camera
    /// defaults, music cue, debug test-picker hotkeys). Empty means "not yet
    /// chosen" — export falls back to the first Zone whose Role is
    /// "Playable" (SC2ObjZone.GuessRole already recognizes "AREA_01"-style
    /// names), or an empty argument if the map has no such zone at all.</summary>
    public string PlayableAreaName = "";

    /// <summary>Every Costs block besides block 0 is a pure computed
    /// view auto-derived from block 0 (see SC2CostsShell.
    /// RecomputeDerivedCostsBlocks) — EXCEPT the 5-block playable-water
    /// set's Land_Medium_Water_Surface block ("shore hint"), which the user
    /// can still hand-paint directly on its own Costs tab like any normal
    /// block. This mask records which cells of THAT block were explicitly
    /// hand-painted (1 = pinned) so the auto-resync preserves them verbatim
    /// instead of overwriting them with the freshly computed value every
    /// time block 0 changes. Never exported into costs.bdf/hfield.bdf —
    /// purely a live-editing bookkeeping field. Sized/reset alongside
    /// TerrainTypes by EnsureCostsAndTerrainTypes below.</summary>
    public byte[] CostsShoreHintOverrideMask = Array.Empty<byte>();

    /// <summary>Real vanilla Costs block-set names: a map WITHOUT playable
    /// water uses these 3 blocks —
    /// no "Water_*" names exist in the real format at all.</summary>
    public static readonly string[] LandOnlyCostsBlockNames = { "Land_Small", "Land_Medium", "Land_Large" };

    /// <summary>Real vanilla Costs block-set names for a map WITH playable
    /// water. Every name still
    /// starts with "Land_" (these are land-unit passability grids that
    /// additionally tolerate some depth of water, not separate naval-unit
    /// blocks — see SC2CostsAutoGen.ClassifyBlockName). Casing/suffixes
    /// (e.g. lowercase "medium") are exactly as given, not normalized.</summary>
    public static readonly string[] PlayableWaterCostsBlockNames =
    {
        "Land_Small_water_Surface_ShoreLine",
        "Land_medium",
        "Land_Large_water_Abyss_ShoreLine",
        "Land_Large_water_Seafloor",
        "Land_Medium_Water_Surface",
    };

    /// <summary>Creates the real vanilla costs blocks (all-passable) and a
    /// TerrainTypes grid filled with DefaultTerrainTypeId if either is
    /// missing/mis-sized — so a from-scratch procedural map always has
    /// something paintable/exportable instead of null/empty/all-zero
    /// arrays. Only ever ADDS missing entries (never renames/replaces
    /// existing ones), so this is safe to call on an already-loaded map
    /// whose CostsBlocks came from an import or an older save — it can't
    /// silently discard already-painted data just because the block-name
    /// set looks different from what a from-scratch map would get today.</summary>
    public void EnsureCostsAndTerrainTypes()
    {
        var cells = CostsGridSize * CostsGridSize;
        if (CostsBlocks == null) CostsBlocks = new List<SC2CostsBlockData>();
        var names = PlayableWater ? PlayableWaterCostsBlockNames : LandOnlyCostsBlockNames;
        for (var i = CostsBlocks.Count; i < names.Length; i++)
        {
            var nav = new byte[cells];
            for (var c = 0; c < nav.Length; c++) nav[c] = 1;
            CostsBlocks.Add(new SC2CostsBlockData { Name = names[i], Navmesh = nav });
        }
        foreach (var block in CostsBlocks)
        {
            if (block.Navmesh == null || block.Navmesh.Length != cells)
            {
                var nav = new byte[cells];
                for (var c = 0; c < nav.Length; c++) nav[c] = 1;
                block.Navmesh = nav;
            }
            if (block.OutlineOpenMask == null || block.OutlineOpenMask.Length != cells)
                block.OutlineOpenMask = new byte[cells];
        }

        if (TerrainTypes == null || TerrainTypes.Length != cells)
        {
            TerrainTypes = new ushort[cells];
            for (var c = 0; c < TerrainTypes.Length; c++) TerrainTypes[c] = DefaultTerrainTypeId;
        }

        if (CostsShoreHintOverrideMask == null || CostsShoreHintOverrideMask.Length != cells)
            CostsShoreHintOverrideMask = new byte[cells];
    }

    /// <summary>Guards EnvironmentHeader against being null or mis-sized
    /// (e.g. an older saved asset from before this field existed) — mirrors
    /// EnsureCostsAndTerrainTypes' own guard pattern. Falls back to the same
    /// real-map defaults the field initializer above uses, not a blank
    /// buffer, so a legacy asset missing this field entirely gets a sensible
    /// starting point rather than silently-invisible zeroed Environment.</summary>
    public void EnsureEnvironmentHeader()
    {
        if (EnvironmentHeader == null || EnvironmentHeader.Length != 508)
            EnvironmentHeader = SC2EnvironmentDefaults.CloneDefaultHeader();
    }

    /// <summary>Assigns a
    /// stable Id to any Material/Pair that doesn't have one yet (a fresh
    /// entry, or one from an asset saved before Id existed). Safe to call
    /// repeatedly and often: already-assigned Ids are left untouched, so
    /// calling this doesn't itself invalidate anything referencing them.</summary>
    public void EnsureStableIds()
    {
        foreach (var m in Materials)
            if (string.IsNullOrEmpty(m.Id))
                m.Id = Guid.NewGuid().ToString("N");
        foreach (var p in Pairs)
        {
            if (string.IsNullOrEmpty(p.Id))
                p.Id = Guid.NewGuid().ToString("N");

            // Backfill Mat1Id/Mat2Id from the CURRENT index for any pair
            // saved before these fields existed (or created without going
            // through the picker) — this is the only place index is ever
            // read to derive the Id going forward; every live edit path
            // (SC2MaterialsMode.MatPicker, Add Pair, Delete Material) reads
            // and writes Mat1Id/Mat2Id directly from here on.
            if (string.IsNullOrEmpty(p.Mat1Id) && p.Mat1Index >= 0 && p.Mat1Index < Materials.Count)
                p.Mat1Id = Materials[p.Mat1Index].Id;
            if (string.IsNullOrEmpty(p.Mat2Id) && p.Mat2Index >= 0 && p.Mat2Index < Materials.Count)
                p.Mat2Id = Materials[p.Mat2Index].Id;
        }
    }

    [Header("Objects (mapobjs.bdf + _save.lua Armies/Areas/Chains)")]
    // See Scripts/TerrainEditor/SC2ObjectsModel.cs for the field-level design
    // notes — kept here as plain
    // List<> fields (not MonoBehaviours) so this whole domain is build-
    // compatible and round-trips through SC2RuntimeTerrainPersistence the
    // same way Costs/Collision2/Environment already do.
    public List<SC2ObjProp> ObjProps = new();
    public List<SC2ObjMarker> ObjMarkers = new();
    public List<SC2ObjGraph> ObjGraphs = new();
    public List<SC2ObjArmy> ObjArmies = new();
    public List<SC2ObjUnitNode> ObjUnits = new();
    public List<SC2ObjZone> ObjZones = new();
    public List<SC2ObjChain> ObjChains = new();
    /// <summary>Live symmetry-linked groups (practical-recommendations #3) —
    /// editing one member propagates to the rest via SC2ObjectsShell's
    /// symmetry-propagation helpers. Separate from "Duplicate with
    /// symmetry" (SC2ObjectsShell.DuplicatePropWithSymmetry/
    /// DuplicateMarkerWithSymmetry), which remains a one-shot, unlinked copy.</summary>
    public List<SC2ObjSymmetryGroup> ObjSymmetryGroups = new();
    /// <summary>Real units/buildings placed purely as an in-editor visual
    /// scale reference — see SC2ObjReferenceUnit's own doc comment.
    /// Deliberately never read by SC2ObjectsExport.cs.</summary>
    public List<SC2ObjReferenceUnit> ObjReferenceUnits = new();

    /// <summary>Raw _save.lua/_scenario.lua text captured at import time (if
    /// this terrain originated from a real .scd), used by SC2ObjectsExport
    /// as the patch base so export only rewrites the Markers/Areas/Armies/
    /// Chains/StartPositions blocks it manages and leaves everything else
    /// (ScenarioInfo, Configurations, etc.) untouched. Empty for a map
    /// created from scratch in this editor — SC2ObjectsExport falls back to
    /// a minimal generated skeleton in that case.</summary>
    public string ObjSaveLuaTextOriginal = "";
    public string ObjScenarioLuaTextOriginal = "";

    /// <summary>Next value AllocateObjectId() will hand out. Every Obj*
    /// item's Id is stable identity for selection/undo across list
    /// mutations (insert/delete/import shift indices, so index-based
    /// selection breaks; an Id survives all of that).</summary>
    public int NextObjectId = 1;

    public int AllocateObjectId() => NextObjectId++;

    // Last list sizes EnsureObjectIds actually finished a scan against — see
    // its own comment for why this cache exists.
    [NonSerialized] private int _idsEnsuredPropsCount = -1, _idsEnsuredMarkersCount = -1, _idsEnsuredGraphsCount = -1,
        _idsEnsuredArmiesCount = -1, _idsEnsuredUnitsCount = -1, _idsEnsuredZonesCount = -1,
        _idsEnsuredChainsCount = -1, _idsEnsuredSymmetryGroupsCount = -1, _idsEnsuredReferenceUnitsCount = -1;

    /// <summary>Backfills Id=0 items (old data saved before Ids existed, or
    /// anything constructed with `new()` and never assigned one) with fresh
    /// unique ids. Safe to call every frame — genuinely a no-op (not just a
    /// no-change) once everything has an id: called every OnGUI event
    /// whenever the Objects shell drives the viewport (SC2ObjectsShell.
    /// OnGameViewGUI), so on a map with many props/markers this full
    /// 8-list-times-2-passes scan was real, measurable per-event cost with
    /// nothing to show for it in the overwhelmingly common case nothing
    /// actually needs backfilling. Every list here only ever grows via an
    /// explicit Add elsewhere in this codebase, and every such Add already
    /// assigns a real id immediately (AllocateObjectId()) — the only
    /// realistic way a zero-id item appears is a document load/import of
    /// old data, which changes these Counts. Skipping the scan whenever
    /// every list's Count still matches the last completed scan turns the
    /// per-event cost back into the actual no-op the old comment
    /// promised.</summary>
    public void EnsureObjectIds()
    {
        EnsureObjectsLists();
        if (_idsEnsuredPropsCount == ObjProps.Count && _idsEnsuredMarkersCount == ObjMarkers.Count &&
            _idsEnsuredGraphsCount == ObjGraphs.Count && _idsEnsuredArmiesCount == ObjArmies.Count &&
            _idsEnsuredUnitsCount == ObjUnits.Count && _idsEnsuredZonesCount == ObjZones.Count &&
            _idsEnsuredChainsCount == ObjChains.Count && _idsEnsuredSymmetryGroupsCount == ObjSymmetryGroups.Count &&
            _idsEnsuredReferenceUnitsCount == ObjReferenceUnits.Count)
            return;

        // A deserialized document can contain valid non-zero Ids while its
        // persisted counter is stale. Advance the counter before assigning
        // anything new so AllocateObjectId never reuses an existing identity.
        var maxExistingId = 0;
        foreach (var p in ObjProps) maxExistingId = Mathf.Max(maxExistingId, p.Id);
        foreach (var m in ObjMarkers) maxExistingId = Mathf.Max(maxExistingId, m.Id);
        foreach (var g in ObjGraphs) maxExistingId = Mathf.Max(maxExistingId, g.Id);
        foreach (var a in ObjArmies) maxExistingId = Mathf.Max(maxExistingId, a.Id);
        foreach (var u in ObjUnits) maxExistingId = Mathf.Max(maxExistingId, u.Id);
        foreach (var z in ObjZones) maxExistingId = Mathf.Max(maxExistingId, z.Id);
        foreach (var c in ObjChains) maxExistingId = Mathf.Max(maxExistingId, c.Id);
        foreach (var s in ObjSymmetryGroups) maxExistingId = Mathf.Max(maxExistingId, s.Id);
        foreach (var r in ObjReferenceUnits) maxExistingId = Mathf.Max(maxExistingId, r.Id);
        NextObjectId = Mathf.Max(NextObjectId, maxExistingId + 1);
        foreach (var p in ObjProps) if (p.Id == 0) p.Id = AllocateObjectId();
        foreach (var m in ObjMarkers) if (m.Id == 0) m.Id = AllocateObjectId();
        foreach (var g in ObjGraphs) if (g.Id == 0) g.Id = AllocateObjectId();
        foreach (var a in ObjArmies) if (a.Id == 0) a.Id = AllocateObjectId();
        foreach (var u in ObjUnits) if (u.Id == 0) u.Id = AllocateObjectId();
        foreach (var z in ObjZones) if (z.Id == 0) z.Id = AllocateObjectId();
        foreach (var c in ObjChains) if (c.Id == 0) c.Id = AllocateObjectId();
        foreach (var s in ObjSymmetryGroups) if (s.Id == 0) s.Id = AllocateObjectId();
        foreach (var r in ObjReferenceUnits) if (r.Id == 0) r.Id = AllocateObjectId();
        SC2ObjectGraphRefs.MigrateLegacyIndexes(this);

        _idsEnsuredPropsCount = ObjProps.Count;
        _idsEnsuredMarkersCount = ObjMarkers.Count;
        _idsEnsuredGraphsCount = ObjGraphs.Count;
        _idsEnsuredArmiesCount = ObjArmies.Count;
        _idsEnsuredUnitsCount = ObjUnits.Count;
        _idsEnsuredZonesCount = ObjZones.Count;
        _idsEnsuredChainsCount = ObjChains.Count;
        _idsEnsuredSymmetryGroupsCount = ObjSymmetryGroups.Count;
        _idsEnsuredReferenceUnitsCount = ObjReferenceUnits.Count;
    }

    public void EnsureObjectsLists()
    {
        ObjProps ??= new List<SC2ObjProp>();
        ObjMarkers ??= new List<SC2ObjMarker>();
        ObjGraphs ??= new List<SC2ObjGraph>();
        ObjArmies ??= new List<SC2ObjArmy>();
        ObjUnits ??= new List<SC2ObjUnitNode>();
        ObjZones ??= new List<SC2ObjZone>();
        ObjChains ??= new List<SC2ObjChain>();
        ObjSymmetryGroups ??= new List<SC2ObjSymmetryGroup>();
        ObjReferenceUnits ??= new List<SC2ObjReferenceUnit>();
    }

    [Header("Export")] public SC2MapRawData RawData;

    [Header("Save State")]
    public int DataVersion = 2;

    // ── Dirty tracking (not serialised) ──────────────────────────────────────
    [NonSerialized] private bool _pairListsDirty=true;

    public void MarkPairListsDirty() => _pairListsDirty=true;

    public void FlushPairLists()
    {
        // TriangleIndices removed — nothing to flush.
        // Pair counts are computed on-demand via GetTriangleCount().
        foreach(var p in Pairs) p.InvalidateTriCount();
        _pairListsDirty=false;
    }
    public void RebuildPairTriangleLists() { _pairListsDirty=true; FlushPairLists(); }

    // ── Init ─────────────────────────────────────────────────────────────────

    public void InitializeFlatPlane(int w,int h,float cell)
    {
        GridWidth=w;GridHeight=h;CellSize=cell;IsInitialized=true;
        Heights=new float[(w+1)*(h+1)];
        for (var i = 0; i < Heights.Length; i++)
            Heights[i] = DefaultFlatHeight;
        SymmetryCenter=new Vector3(w*cell*.5f,0,h*cell*.5f);
        EnsureDefaultMaterialsAndPair();
        ResetPairIndex();
        EnsureMinimumArmies(w, h, cell);
        InvalidateGeometryCache();
    }

    /// <summary>Every real vanilla map (D1 multiplayer + CA campaign corpus)
    /// has at least 2 start positions - a from-scratch map previously left
    /// this to the user to fill in manually via "Add Start Position"
    /// (Objects tab), so a brand-new map started out failing its own
    /// minimum-2-armies validation. Auto-creates ARMY_1/ARMY_2 at two
    /// separated placeholder spots (opposite sides of the map, same flat
    /// default height InitializeFlatPlane just set) so a fresh map is
    /// already valid; the user repositions/renames from there exactly like
    /// any other marker. Only runs at fresh-terrain-init time, not on every
    /// load/validate - unlike Materials/Pairs (always render-critical),
    /// armies aren't something that should be silently re-added if a user
    /// deliberately removes them from an existing map mid-edit.
    ///
    /// Unlike every OTHER army-creation path (SC2ObjectsShell's "Add Start
    /// Position"/"Place Army in viewport"/etc, all of which call
    /// EnsureArmyMetadataLinked + LinkMarkersWithSymmetry), this one runs
    /// before SC2ObjectsShell exists at all - it's plain SC2TerrainData
    /// construction, not a shell action - so it has to build both the
    /// SC2ObjArmy metadata link and the symmetry group by hand here instead
    /// of calling those shell methods. Skipping this left ARMY_1/ARMY_2 as
    /// Start-Position markers with no matching entry in the Armies list
    /// (nothing populated ObjArmies) and with no symmetry link (so dragging
    /// one didn't move the other even with a symmetry mode later turned on)
    /// - both real, reported bugs, not a design choice.</summary>
    private void EnsureMinimumArmies(int w, int h, float cell)
    {
        if (ObjMarkers == null) ObjMarkers = new List<SC2ObjMarker>();
        if (ObjArmies == null) ObjArmies = new List<SC2ObjArmy>();
        if (ObjSymmetryGroups == null) ObjSymmetryGroups = new List<SC2ObjSymmetryGroup>();
        var armies = ObjMarkers
            .Where(m => m != null && SC2MarkerDeduplication.IsArmyStartMarker(m.Name))
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        if (armies.Count >= 2) return;

        var z = h * cell * 0.5f;
        var defaultPositions = new[]
        {
            new Vector3(w * cell * 0.25f, DefaultFlatHeight, z),
            new Vector3(w * cell * 0.75f, DefaultFlatHeight, z)
        };

        while (armies.Count < 2)
        {
            var number = 1;
            while (ObjMarkers.Any(m => string.Equals(m.Name, $"ARMY_{number}", StringComparison.OrdinalIgnoreCase))) number++;
            var army = new SC2ObjMarker
            {
                Id = AllocateObjectId(), Name = $"ARMY_{number}", Type = "Blank Marker",
                Position = defaultPositions[armies.Count]
            };
            ObjMarkers.Add(army);
            armies.Add(army);
        }

        foreach (var army in armies)
            if (!ObjArmies.Any(existing => string.Equals(existing.Name, army.Name, StringComparison.OrdinalIgnoreCase)))
                ObjArmies.Add(new SC2ObjArmy { Id = AllocateObjectId(), Name = army.Name, MarkerId = army.Id });

        // Placed as a true mirror pair (0.25/0.75 across the map's X extent,
        // same Z) regardless of Symmetry's own current value (always None
        // this early, before the user has had any chance to set it) — link
        // them now so a symmetry mode turned on later immediately makes
        // dragging one move the other, same as any other linked pair.
        // GetSymmetricPositions itself is a no-op while Symmetry is still
        // None (PropagateSymmetryMove's own reasoning), so this is safe to
        // do unconditionally rather than gating on the current mode.
        ObjSymmetryGroups.Add(new SC2ObjSymmetryGroup
        {
            Id = AllocateObjectId(),
            Kind = "Marker",
            Members =
            {
                new SC2ObjSymmetryMember { ObjectId = armies[0].Id, SlotIndex = 0 },
                new SC2ObjSymmetryMember { ObjectId = armies[1].Id, SlotIndex = 1 },
            }
        });
    }

    private void EnsureDefaultMaterialsAndPair()
    {
        if (Materials == null) Materials = new List<SC2Material>();
        if (Pairs == null) Pairs = new List<SC2MaterialPair>();

        while (Materials.Count < 2)
        {
            Materials.Add(new SC2Material
            {
                Name = Materials.Count == 0 ? "Default Grass" : "Default Rock",
                Color = Materials.Count == 0 ? new Color(0.28f, 0.68f, 0.30f) : new Color(0.48f, 0.48f, 0.44f)
            });
        }

        if (Pairs.Count == 0)
        {
            Pairs.Add(new SC2MaterialPair
            {
                Name = "Default Pair",
                Mat1Index = 0,
                Mat2Index = 1,
                PairColor = new Color(0.35f, 0.75f, 0.35f),
                ShaderFlags = new List<SC2ShaderFlag>
                {
                    new SC2ShaderFlag{ Key = "AMBIENT_LIGHTING", Value = true },
                    new SC2ShaderFlag{ Key = "DIFFUSE_LIGHTING", Value = true },
                    new SC2ShaderFlag{ Key = "SPECULAR_LIGHTING", Value = true },
                    new SC2ShaderFlag{ Key = "RNM_LIGHTING", Value = true },
                    new SC2ShaderFlag{ Key = "BLENDING", Value = true },
                    new SC2ShaderFlag{ Key = "FOG", Value = true },
                    new SC2ShaderFlag{ Key = "DYNAMIC_SHADOWS", Value = true },
                    new SC2ShaderFlag{ Key = "REFLECT", Value = true },
                    new SC2ShaderFlag{ Key = "CHROMATIC_DISPERSION", Value = true },
                }
            });
        }
    }

    private void ResetPairIndex()
    {
        var n=GridWidth*GridHeight*2;
        TrianglePairIndex=new short[n];
        var def=Pairs.Count>0?(short)0:(short)-1;
        for(var i=0;i<n;i++) TrianglePairIndex[i]=def;
        if(def==0&&Pairs.Count>0)
            Pairs[0].InvalidateTriCount(); // no List needed
        _pairListsDirty=false;
    }

    public void AutoAssignFirstPair()
    {
        if(Pairs.Count==0||TrianglePairIndex.Length==0)return;
        for(var i=0;i<TrianglePairIndex.Length;i++) if(TrianglePairIndex[i]>=0)return;
        AssignAllTrianglesToPair(0);
    }

    // ── Pair assignment (O(1) per tri) ────────────────────────────────────────

    public void AssignTriangleToPair(int ti,int pi)
    {
        if((uint)ti>=(uint)TrianglePairIndex.Length)return;
        if(TrianglePairIndex[ti]==pi)return;
        var old=TrianglePairIndex[ti];
        if(old>=0&&old<Pairs.Count) Pairs[old].InvalidateTriCount();
        TrianglePairIndex[ti]=(short)Mathf.Clamp(pi,-1,short.MaxValue);
        if(pi>=0&&pi<Pairs.Count) Pairs[pi].InvalidateTriCount();
    }

    public void AssignAllTrianglesToPair(int pi)
    {
        // Invalidate cached counts
        foreach(var p in Pairs) p.InvalidateTriCount();
        var n=TrianglePairIndex.Length;
        var spi=(short)Mathf.Clamp(pi,-1,short.MaxValue);
        // O(N) array fill — fast (no List alloc)
        for(var i=0;i<n;i++) TrianglePairIndex[i]=spi;
        _pairListsDirty=false;
    }

    // ── Pair paint spatial query ───────────────────────────────────────────
    // The "rt" (SC2TerrainRuntime) parameter these two methods used to take
    // was never actually read in either body — dropped so this
    // file has no compile-time dependency on SC2TerrainRuntime.cs, needed to
    // give SC2TerrainData its own narrow, UnityEditor-free .asmdef (see
    // SC2.Core.asmdef) that an EditMode test assembly can reference (Unity
    // does not allow a custom asmdef to reference the default Assembly-CSharp
    // at all). Their only caller was the former runtime-only terrain shell,
    // confirmed dead/orphaned code (flagged separately for deletion).

    public void PaintPairAt(Vector3 center,float radius,int pairIdx)
    {
        foreach(var symPos in GetSymmetricPositions(center))
            PaintPairRegion(symPos,radius,pairIdx);
    }

    private void PaintPairRegion(Vector3 center,float radius,int pairIdx)
    {
        // Compute affected grid range
        var cell=CellSize; var stride=GridWidth+1;
        var x0=Mathf.Max(0,Mathf.FloorToInt((center.x-radius)/cell));
        var x1=Mathf.Min(GridWidth-1,Mathf.CeilToInt((center.x+radius)/cell));
        var z0=Mathf.Max(0,Mathf.FloorToInt((center.z-radius)/cell));
        var z1=Mathf.Min(GridHeight-1,Mathf.CeilToInt((center.z+radius)/cell));
        var r2=radius*radius; var spi=(short)Mathf.Clamp(pairIdx,-1,short.MaxValue);

        for(var gz=z0;gz<=z1;gz++)
        for(var gx=x0;gx<=x1;gx++)
        {
            // Each grid cell = 2 triangles (upper-left and lower-right)
            var wx=gx*cell+cell*.5f; var wz=gz*cell+cell*.5f;
            var dx=wx-center.x; var dz=wz-center.z;
            if(dx*dx+dz*dz>r2)continue;
            // Tri index: row-major, 2 tris per quad
            var ti=(gz*GridWidth+gx)*2;
            if(ti<TrianglePairIndex.Length)   TrianglePairIndex[ti]  =spi;
            if(ti+1<TrianglePairIndex.Length) TrianglePairIndex[ti+1]=spi;
        }
        _pairListsDirty=true;
    }

    // ── Symmetry ──────────────────────────────────────────────────────────────

    public List<Vector3> GetSymmetricPositions(Vector3 pos) =>
        SC2SymmetryMath.GetSlots(Symmetry,SymmetryCenter,RadialCount,pos);

    // Was returning Materials[p.Mat1Index].Color (the underlying material's
    // own diffuse tint) instead of the pair's own explicitly user-set
    // SC2MaterialPair.PairColor (the "Pair Color" field edited in the Pairs
    // tab, SC2MaterialsPairsMode.cs) - every swatch/preview-mesh caller
    // (Simplified preview, Paint assign-pair swatch, Export panel's
    // per-pair list) wants this method to identify THIS pair visually, not
    // whichever material happens to be Mat1, so a pair's preview color
    // never matched the color the user actually picked for it.
    public Color PairColor(int pi)
    {
        if(pi<0||pi>=Pairs.Count)return Color.grey;
        return Pairs[pi].PairColor;
    }
    // ── Computed geometry (derived from Heights on demand) ───────────────────
    // Exporter and Simplifier use these. Not stored — built from Heights.

    /// <summary>Build flat vertex array from Heights. Call before export.</summary>
    public Vector3[] BuildVertices()
    {
        var vw = GridWidth + 1; var vh = GridHeight + 1;
        var verts = new Vector3[vw * vh];
        for (var z = 0; z < vh; z++)
        for (var x = 0; x < vw;  x++)
        {
            var i = z * vw + x;
            verts[i] = new Vector3(x * CellSize,
                                   i < Heights.Length ? Heights[i] : 0f,
                                   z * CellSize);
        }
        return verts;
    }

    /// <summary>Build index array for regular grid. Matches TrianglePairIndex ordering.</summary>
    public int[] BuildTriangles()
    {
        var vw = GridWidth + 1;
        var tris = new int[GridWidth * GridHeight * 6];
        var t = 0;
        for (var z = 0; z < GridHeight; z++)
        for (var x = 0; x < GridWidth;  x++)
        {
            var i00 = z*vw+x; var i10=i00+1; var i01=(z+1)*vw+x; var i11=i01+1;
            tris[t++]=i00; tris[t++]=i01; tris[t++]=i10;
            tris[t++]=i10; tris[t++]=i01; tris[t++]=i11;
        }
        return tris;
    }

    /// <summary>
    /// Vertices property — computed from Heights.
    /// Cached until geometry changes (Heights updated).
    /// </summary>
    [NonSerialized] private Vector3[] _cachedVerts;
    [NonSerialized] private int[]     _cachedTris;

    public Vector3[] Vertices
    {
        get
        {
            if (_cachedVerts == null || _cachedVerts.Length != (GridWidth+1)*(GridHeight+1))
                _cachedVerts = BuildVertices();
            return _cachedVerts;
        }
    }

    public int[] Triangles
    {
        get
        {
            if (_cachedTris == null || _cachedTris.Length != GridWidth*GridHeight*6)
                _cachedTris = BuildTriangles();
            return _cachedTris;
        }
    }

    /// <summary>Call after Heights[] is modified to invalidate cached geometry.</summary>
    public void InvalidateGeometryCache() { _cachedVerts = null; _cachedTris = null; }

    public void OnBeforeSerialize()
    {
        EnsureSerializableState();
    }

    public void OnAfterDeserialize()
    {
        _cachedVerts = null;
        _cachedTris = null;
        PairTriCounts = Array.Empty<int>();
        EnsureSerializableState();
        foreach (var p in Pairs) p?.InvalidateTriCount();
    }

    private void OnValidate()
    {
        EnsureSerializableState();
        InvalidateGeometryCache();
    }

    public void EnsureSerializableState()
    {
        GridWidth = Mathf.Max(1, GridWidth);
        GridHeight = Mathf.Max(1, GridHeight);
        CellSize = Mathf.Max(0.0001f, CellSize);

        var heightCount = (GridWidth + 1) * (GridHeight + 1);
        if (Heights == null || Heights.Length != heightCount)
        {
            var next = new float[heightCount];
            if (Heights != null) Array.Copy(Heights, next, Math.Min(Heights.Length, next.Length));
            Heights = next;
        }
        for (var i = 0; i < Heights.Length; i++)
            Heights[i] = ClampHeight(Heights[i]);

        var triCount = GridWidth * GridHeight * 2;
        if (TrianglePairIndex == null || TrianglePairIndex.Length != triCount)
        {
            var next = new short[triCount];
            for (var i = 0; i < next.Length; i++) next[i] = -1;
            if (TrianglePairIndex != null) Array.Copy(TrianglePairIndex, next, Math.Min(TrianglePairIndex.Length, next.Length));
            TrianglePairIndex = next;
        }

        if (Materials == null) Materials = new List<SC2Material>();
        if (Pairs == null) Pairs = new List<SC2MaterialPair>();
        if (RnmShadowExclusions == null) RnmShadowExclusions = new List<string>();
        if (BlendLayerStacks == null) BlendLayerStacks = new List<SC2BlendMaskLayerStack>();
        BlendLayerStacks.RemoveAll(stack => stack == null);
        foreach (var stack in BlendLayerStacks) stack.Normalize();
        EnsureDefaultMaterialsAndPair();
        EnsurePairAssignments();
        for (var i = 0; i < Pairs.Count; i++)
        {
            var p = Pairs[i];
            if (p == null) continue;
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"Pair {i + 1}";
            if (string.IsNullOrWhiteSpace(p.RnmMaskName)) p.RnmMaskName = "mask01";
            p.MaskWidth = Mathf.Max(4, p.MaskWidth);
            p.MaskHeight = Mathf.Max(4, p.MaskHeight);
            p.TilingScale = Mathf.Max(0.01f, p.TilingScale);
            p.SimplifyRatio = Mathf.Clamp01(p.SimplifyRatio);
            if (p.ShaderFlags == null) p.ShaderFlags = new List<SC2ShaderFlag>();
            if (p.RnmMaskBytes == null) p.RnmMaskBytes = Array.Empty<byte>();
        }

        // The shared tiling size is the only one now. A map saved while per-pair tiling was in use takes its first pair's
        // size as the shared one, so its look does not change when it is opened.
        if (!GlobalUvScaleEnabled)
        {
            if (Pairs.Count > 0 && Pairs[0] != null && Pairs[0].TilingScale > 0f) GlobalUvScale = Pairs[0].TilingScale;
            GlobalUvScaleEnabled = true;
        }
    }

    private void EnsurePairAssignments()
    {
        if (TrianglePairIndex == null || TrianglePairIndex.Length == 0 || Pairs.Count == 0)
            return;

        var hasAssignedTriangle = false;
        for (var i = 0; i < TrianglePairIndex.Length; i++)
        {
            if (TrianglePairIndex[i] >= 0)
            {
                hasAssignedTriangle = true;
                break;
            }
        }

        if (!hasAssignedTriangle)
            AssignAllTrianglesToPair(0);
    }

    /// <summary>Import heightmap texture to Heights[] (CPU path, for export/serialise).</summary>
    public void ImportHeightmap(Texture2D hmap, float scale = 100f)
    {
        if (!IsInitialized) return;
        var vw = GridWidth+1; var vh = GridHeight+1;
        var pixels = hmap.GetPixels(); var hw = hmap.width; var hh = hmap.height;
        for (var z = 0; z < vh; z++)
        for (var x = 0; x < vw;  x++)
        {
            var px = Mathf.Clamp(Mathf.RoundToInt((float)x/GridWidth*(hw-1)),0,hw-1);
            var py = Mathf.Clamp(Mathf.RoundToInt((float)z/GridHeight*(hh-1)),0,hh-1);
            Heights[z*vw+x] = ClampHeight(pixels[py*hw+px].grayscale * scale);
        }
        InvalidateGeometryCache();
    }

    public static float ClampHeight(float height) => Mathf.Clamp(height, MinHeight, MaxHeight);


}
