using System.Collections.Generic;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>SC2TerrainLegacyBridge
    /// only ever produces ProceduralHeightmap today
    /// — the legacy SC2TerrainData this bridges from has no authoring-mode
    /// concept of its own; imported terrain.bdf objects are tracked entirely
    /// separately (SC2GameViewEditorUI's _importedTerrains), not folded into
    /// SC2TerrainData at all. Recognizing Imported/Hybrid here would require
    /// unifying that separate tracking first — a distinct, larger task.</summary>
    public enum SC2TerrainAuthoringMode
    {
        ProceduralHeightmap,
        ImportedTerrainBdf,
        Hybrid
    }

    /// <summary>
    /// The Terrain domain
    /// document. Built today ONLY by SC2TerrainLegacyBridge as a read-only
    /// snapshot of the live SC2TerrainData; nothing writes
    /// live edits through this document yet — that remains a much larger,
    /// separate undertaking. Not yet attached to
    /// SC2MapDocument itself (that wiring, plus the equivalent for every
    /// other domain, is a separate "removing dual ownership" concern).
    /// </summary>
    public sealed class SC2TerrainDocument
    {
        public SC2TerrainAuthoringMode AuthoringMode = SC2TerrainAuthoringMode.ProceduralHeightmap;
        public SC2TerrainGrid Grid = new();
        public List<SC2TerrainLayerDocument> Layers = new();
        /// <summary>Imported terrain.bdf objects placed alongside/instead of
        /// the procedural heightmap — see SC2ImportedTerrainEntryDocument's
        /// own doc comment. Independent of AuthoringMode/Layers above (both
        /// still describe the procedural side only); a map can have a
        /// nonempty ImportedEntries list while AuthoringMode stays
        /// ProceduralHeightmap — recognizing Hybrid/ImportedTerrainBdf here
        /// is unrelated bridge-classification work, not a prerequisite for
        /// imported terrain surviving Save/Load.</summary>
        public List<SC2ImportedTerrainEntryDocument> ImportedEntries = new();
        public List<SC2MaterialDocument> Materials = new();
        public List<SC2MaterialPairDocument> MaterialPairs = new();
        public SC2TrianglePairMap PairAssignments = new();
        /// <summary>Left null by SC2TerrainLegacyBridge — see SC2BlendDocument's
        /// own doc comment for why this pass doesn't populate it.</summary>
        public SC2BlendDocument Blend;
        public SC2RnmDocument Rnm = new();

        /// <summary>RNM masks baked for LAYERS of imported terrain.bdf objects
        /// (Terrain > Lighting Bake > Imported Objects), by those layers' own
        /// UV2. A namespace of its own, apart from <see cref="Rnm"/> (the core
        /// terrain's and the background's masks), so an imported mask can never
        /// collide with `mask01`/`background`.</summary>
        public SC2RnmDocument ImportedRnm = new();

        /// <summary>Environment > Point Clouds settings and the baked pcloud_up / pcloud_down volumes (complete DDS
        /// files as blobs; empty = not baked).</summary>
        public Color PointCloudSkyColor = new Color(0.75f, 0.88f, 1f, 1f);
        public float PointCloudSkyIntensity = 0.5f;
        public float PointCloudGroundReflection = 0.5f;
        public float PointCloudSkyInGround = 0.35f;
        public float PointCloudBlurTexels = 1f;
        public bool PointCloudSunShadow = true;
        public Vector3 PointCloudBoxOrigin, PointCloudBoxSize;
        public SC2BlobReference PointCloudUp;
        public SC2BlobReference PointCloudDown;

        /// <summary>Decal lighting consistency — see SC2RnmBakeService.BakeImportedWorldRnm's own
        /// doc comment. Separate from ImportedRnm above (the per-object, mapmaker-controlled masks
        /// feature) — this is a single, always-on, world-space-sampled bake SC2Decal.shader falls
        /// back to for a decal sitting on imported terrain with no per-object mask configured.</summary>
        public SC2BlobReference ImportedRnmBakedRgba;
        public int ImportedRnmWidth, ImportedRnmHeight;
        public float ImportedRnmWorldOriginX, ImportedRnmWorldOriginZ;
        public float ImportedRnmWorldSpanX = 1f, ImportedRnmWorldSpanZ = 1f;

        /// <summary>The Lighting Bake tab's own 4 sync toggles + override
        /// values (SC2TerrainData.RnmBakeSync*/RnmBakeOverride*) — had no
        /// document schema home at all, so a save/reload silently reset every
        /// bake input back to its SC2TerrainData field default (sync on,
        /// class-default override values) regardless of what the user had
        /// configured, same gap class as ImportOnlyMode/BSP options below.</summary>
        public bool RnmBakeSyncSunDirection = true;
        public bool RnmBakeSyncSunColor = true;
        public bool RnmBakeSyncSunIntensity = true;
        public bool RnmBakeSyncShadowColor = true;
        public Vector3 RnmBakeOverrideSunDirection = new Vector3(0.3f, 0.8f, 0.3f);
        public Color RnmBakeOverrideSunColor = Color.white;
        public float RnmBakeOverrideSunIntensity = 1f;
        public Color RnmBakeOverrideShadowColor = new Color(0.64f, 0.77f, 0.92f, 1f);
        public int RnmBakeShadowSoftRadius = 6;
        /// <summary>Mirrors SC2TerrainData.RnmShadowExclusions: the caster/receiver pairs the lighting bake leaves out, as
        /// SC2ShadowCasting keys. Empty = every object shadows every object.</summary>
        public List<string> RnmShadowExclusions = new();

        public SC2TerrainSimplificationSettings Simplification = new();
        public SC2TerrainNormalsSettings Normals = new();
        public SC2SymmetrySettings Symmetry = new();

        /// <summary>Mirrors SC2TerrainData.IsInitialized/
        /// DataVersion — small scalars with no prior home anywhere in
        /// SC2MapDocument. Needed by the persistence Stage 2 reverse bridge
        /// (SC2MapDocumentToLegacyBridge) to reconstruct a legacy
        /// SC2TerrainData that behaves identically to the original (an
        /// uninitialized document must not silently look "initialized" to
        /// legacy code paths that gate on this flag, e.g. SC2TerrainData's
        /// own ClampHeight/InvalidateGeometryCache early-outs).</summary>
        public bool IsInitialized;
        public int DataVersion;

        /// <summary>Export/BSP-construction options — these three had no document schema home
        /// at all, so a Save/Load cycle through the document package silently
        /// reset them to their SC2TerrainData field defaults regardless of
        /// what the user had configured (confirmed real via a reload
        /// losing all three).</summary>
        public bool ImportOnlyMode;
        public bool SingleBspNodePerLayer;
        public bool VanillaCandidateBspSplit = true;
        /// <summary>Same gap, same fix — the name of the Objects Area chosen
        /// as the map's playable rectangle.</summary>
        public string PlayableAreaName = "";

        /// <summary>Decorative background terrain — the 3
        /// scalar settings a user configures on the Background tab itself.</summary>
        public bool BackgroundTerrainEnabled;
        public int BackgroundTerrainSize = 4096;
        public float BackgroundTerrainUvScale = 1024f;

        /// <summary>One shared
        /// UV1 tiling scale for every pair on both zones when enabled; see
        /// SC2TerrainData.GlobalUvScaleEnabled/GlobalUvScale's own doc
        /// comment.</summary>
        public bool GlobalUvScaleEnabled;
        public float GlobalUvScale = 4f;

        /// <summary>Amplifies height's contribution to the UV1 stretch
        /// correction; see SC2TerrainData.Uv1HeightWeight's own doc
        /// comment.</summary>
        public float Uv1HeightWeight = 1f;

        /// <summary>The
        /// nested SC2TerrainData instance was assumed to be fully
        /// regenerable from the 3 scalars above plus the live core terrain
        /// (SC2BackgroundTerrain.Generate) — true only while the feature
        /// was flat/falloff heights with one default, textureless pair.
        /// Since then it grew real hand-authored state with no procedural
        /// source at all (assigned Diffuse/Normal/Mask textures, per-pair
        /// ShaderFlags/State/TextureScroll, hand-painted RnmMaskBytes, baked
        /// RnmBakedRgb0/1/2/ShadowAlpha, CPU-sculpted Heights, manually
        /// painted TrianglePairIndex) — none of which this document ever
        /// captured, so every reload (the document package is ALWAYS
        /// preferred over the legacy JSON fallback — see
        /// SC2RuntimeTerrainPersistence.TryLoad) silently regenerated a
        /// brand-new default background, discarding all of it.
        ///
        /// BackgroundTerrainData is itself a full SC2TerrainData, so this
        /// reuses SC2TerrainDocument recursively rather than duplicating
        /// its entire schema a second time — see SC2TerrainLegacyBridge.
        /// BuildFromLegacy(SC2TerrainData,...)'s own recursive call and
        /// SC2MapDocumentToLegacyBridge.ApplyToLegacy's ApplyTerrainResult
        /// helper for the two directions. Null when
        /// BackgroundTerrainEnabled is false or no background has ever
        /// been generated for this map.</summary>
        public SC2TerrainDocument Background;

        public SC2MaterialDocument FindMaterial(string id)
        {
            foreach (var m in Materials)
                if (m.Id == id)
                    return m;
            return null;
        }

        public SC2MaterialPairDocument FindPair(string id)
        {
            foreach (var p in MaterialPairs)
                if (p.Id == id)
                    return p;
            return null;
        }
    }
}
