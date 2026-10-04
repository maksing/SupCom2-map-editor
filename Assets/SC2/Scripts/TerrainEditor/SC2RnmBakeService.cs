using System;
using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;

/// <summary>
/// Bakes real per-texel RNM lighting into SC2MaterialPair's
/// RnmBakedRgb0/1/2 + RnmBakedShadowAlpha, replacing the hardcoded
/// neutral-128/opaque placeholder ExportRnmMaskByName otherwise falls back to.
/// Manual "Bake Lighting" trigger only (SC2TerrainShell) - never runs
/// automatically on export, since the shadow horizon-march is real per-texel
/// work across up to 2048x2048 texels per mask.
///
/// Critical detail this exists to account for: Resources/SC2Terrain.shader's
/// runtime fragment shader dots a TANGENT-SPACE normal-map normal directly
/// against RNM_BASIS_0/1/2, with NO transform to world space at runtime (the
/// vertex shader doesn't even read the mesh's own normal attribute) - so the
/// terrain's real geometric slope (world-space normal) has nowhere else to
/// enter the lighting equation except here, at bake time, via a per-texel
/// TBN built from the real interpolated mesh normal.
///
/// Mirrors SC2HfieldProjectionService.cs's structure/rasterization style.
/// </summary>
public static class SC2RnmBakeService
{
    /// <summary>Runs the full bake for every RNM mask referenced by
    /// <paramref name="data"/>'s pairs. Returns a short human-readable status
    /// for the "Bake Lighting" button to show.</summary>
    /// <param name="commitImportedMasks">Receives the complete new list of
    /// imported-layer RNM masks (SC2TerrainDocument.ImportedRnm) when the bake
    /// produced imported results — the caller stores it through its own undo
    /// path (SC2TerrainShell.ExecuteCommand) so the bake is undoable. Null
    /// skips baking imported layers.</param>
    public static string Bake(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session = null,
        Action<List<SC2RnmMaskSetDocument>> commitImportedMasks = null)
    {
        string status = null;
        RunNow(BeginBake(data, importedTerrains, session, commitImportedMasks, s => status = s));
        return status;
    }

    /// <summary>Runs a bake started with <see cref="BeginBake"/> / <see cref="BeginBakeBackground"/> right here, on the calling
    /// thread (what <see cref="Bake"/> and <see cref="BakeBackground"/> do).</summary>
    private static void RunNow(Func<Action> compute) => compute?.Invoke()?.Invoke();

    /// <summary>The background-thread form of <see cref="Bake"/>, for SC2BusyOverlay.RunInBackground. Call it on the main thread:
    /// it reads what only the main thread may read (the info model, the imported materials' textures) and returns the heavy
    /// function, which returns the action that stores the baked masks and must run on the main thread. Null when there is nothing
    /// to bake, after reporting why through <paramref name="onFinished"/>, which also receives the status line at the end.</summary>
    public static Func<Action> BeginBake(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session,
        Action<List<SC2RnmMaskSetDocument>> commitImportedMasks, Action<string> onFinished)
    {
        if (data == null) { onFinished?.Invoke("No active terrain to bake."); return null; }

        var info = SC2TerrainExporter.BuildInfoModel(data, session);
        var shadowRules = CopyShadowRules(data);
        var importedPlan = commitImportedMasks != null && session?.Document != null && session.BlobStore != null
            ? new ImportedBake
            {
                Objects = importedTerrains, Session = session, Commit = commitImportedMasks,
                BlendSamplers = BuildImportedBlendSamplers(importedTerrains),
            }
            : null;

        return () =>
        {
            SC2BusyOverlay.Report("Building the terrain geometry");
            var model = BuildBakeGeometry(data, importedTerrains, out var pairIndexPerLayer, out var importedLayers);
            if (model.Layers.Count == 0) return () => onFinished?.Invoke("Nothing to bake - no triangles are assigned to any pair.");

            // The shadow horizon-march runs against a UNIFIED heightmap
            // covering both the core's own footprint and (when enabled) the
            // decorative background's full extent — see BuildShadowHfield's
            // own doc comment. This bake's OUTPUT still only ever writes into
            // data.Pairs (core); only what can OCCLUDE the sun is unified.
            SC2BusyOverlay.Report("Building the shadow heightmap");
            var shadow = BuildShadowCasters(data, importedTerrains, shadowRules, model, pairIndexPerLayer, importedLayers);
            var receivers = ReceiverIdsPerLayer(pairIndexPerLayer, importedLayers, importedTerrains);

            if (importedPlan != null) importedPlan.Layers = importedLayers;
            var outcome = BakeInternal(model, pairIndexPerLayer, data.Pairs, shadow, receivers, shadow.Offset, data, info, importedPlan);
            return () => onFinished?.Invoke(ApplyBake(outcome, pairIndexPerLayer, data.Pairs, data, "RNM mask(s)", importedPlan));
        };
    }

    /// <summary>Background equivalent of Bake — same real per-texel bake
    /// (3 basis-direction lighting passes + heightmap horizon-march shadow),
    /// just run against the decorative background terrain's own geometry/
    /// pairs instead of the core's. Sun/Shadow inputs are read from
    /// <paramref name="coreData"/> (there is only one Lighting Bake config —
    /// the background has no separate copy of its own, matching how there's
    /// only one Environment/Sun setup for the whole map). Shadow occlusion
    /// uses the SAME unified core+background heightmap Bake() now builds
    /// (BuildShadowHfield) — a tall core feature can shadow the background
    /// and vice versa.</summary>
    public static string BakeBackground(SC2TerrainData coreData, List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session = null)
    {
        string status = null;
        RunNow(BeginBakeBackground(coreData, importedTerrains, session, s => status = s));
        return status;
    }

    /// <summary>The background-thread form of <see cref="BakeBackground"/>; see <see cref="BeginBake"/> for how it is used.</summary>
    public static Func<Action> BeginBakeBackground(SC2TerrainData coreData, List<SC2ImportedTerrainExportData> importedTerrains,
        SC2MapSession session, Action<string> onFinished)
    {
        if (coreData == null) { onFinished?.Invoke("No active terrain to bake."); return null; }
        var bg = coreData.BackgroundTerrainData;
        if (!coreData.BackgroundTerrainEnabled || bg == null) { onFinished?.Invoke("Background terrain is not enabled."); return null; }

        var info = SC2TerrainExporter.BuildInfoModel(coreData, session);
        var shadowRules = CopyShadowRules(coreData);
        return () =>
        {
            SC2BusyOverlay.Report("Building the background geometry");
            var model = BuildBackgroundBakeGeometry(coreData, bg, out var pairIndexPerLayer, out _, out _);
            if (model.Layers.Count == 0) return () => onFinished?.Invoke("Nothing to bake - background has no triangles assigned to any pair.");

            // BuildBackgroundBakeGeometry's own `model` is already expressed in
            // background-local space (worldPos - sharedOffset) — exactly the
            // same frame BuildShadowHfield's own sharedOffset uses when
            // background is enabled (it must be, here) — so no further shift
            // is needed for THIS bake's own geometry (hfieldOffset passed as
            // zero); BuildShadowHfield handles shifting the core's own absolute-
            // world geometry into that shared frame internally.
            SC2BusyOverlay.Report("Building the shadow heightmap");
            var shadow = BuildShadowCasters(coreData, importedTerrains, shadowRules);
            var receivers = new List<string>();
            for (var li = 0; li < pairIndexPerLayer.Count; li++) receivers.Add(SC2ShadowCasting.BackgroundId);
            // bakeImportedWorldRnm: false — this bake's own model never merges imported terrain (see
            // BuildBackgroundBakeGeometry), so without this it would see zero imported layers every
            // time and wipe out whatever the core's own Bake() already baked.
            var outcome = BakeInternal(model, pairIndexPerLayer, bg.Pairs, shadow, receivers, Vector3.zero, coreData, info, null,
                bakeImportedWorldRnm: false);
            return () => onFinished?.Invoke(ApplyBake(outcome, pairIndexPerLayer, bg.Pairs, coreData, "background RNM mask(s)", null));
        };
    }

    /// <summary>A copy of the shadow rules (SC2ShadowCasting) to carry into a bake that runs on a worker thread.</summary>
    private static List<string> CopyShadowRules(SC2TerrainData data) =>
        data.RnmShadowExclusions != null ? new List<string>(data.RnmShadowExclusions) : new List<string>();

    /// <summary>The object id each layer of the core bake model belongs to: the core for a pair layer, the imported object's
    /// id for a merged imported layer.</summary>
    private static List<string> ReceiverIdsPerLayer(List<int> pairIndexPerLayer, List<ImportedLayerRef> importedLayers,
        List<SC2ImportedTerrainExportData> importedTerrains)
    {
        var ids = new List<string>(pairIndexPerLayer.Count);
        for (var li = 0; li < pairIndexPerLayer.Count; li++)
        {
            var imported = li < importedLayers.Count ? importedLayers[li] : null;
            ids.Add(imported == null ? SC2ShadowCasting.CoreId : ImportedObjectId(importedTerrains, imported.ObjectIndex));
        }
        return ids;
    }

    private static string ImportedObjectId(List<SC2ImportedTerrainExportData> importedTerrains, int objectIndex)
    {
        var id = importedTerrains != null && objectIndex >= 0 && objectIndex < importedTerrains.Count ? importedTerrains[objectIndex].Id : null;
        return string.IsNullOrEmpty(id) ? "imported:" + objectIndex : id;
    }

    /// <summary>The geometry that can shade a bake, kept per object (core, background, each imported terrain.bdf) so that
    /// what shades a texel can depend on the object the texel belongs to (SC2ShadowCasting). All of it is expressed in one frame
    /// whose origin is <see cref="Offset"/>, and a heightmap is only projected for each distinct set of casters that is asked
    /// for. Without rules there is one set, so the cost is that of a single heightmap.</summary>
    internal sealed class ShadowCasterField
    {
        /// <summary>What a caller whose own geometry is still in the core's world space subtracts from a position before looking
        /// it up in a heightmap of this field; zero when the frame is the world's own.</summary>
        public Vector3 Offset;
        public int MapX, MapY;

        private readonly List<(string Id, TerrainModel Model)> _casters = new List<(string, TerrainModel)>();
        private readonly List<string> _rules;
        private readonly Dictionary<string, HfieldModel> _heightmaps = new Dictionary<string, HfieldModel>();

        public ShadowCasterField(List<string> rules) { _rules = rules ?? new List<string>(); }

        public void AddCaster(string id, TerrainModel model) => _casters.Add((id, model));

        /// <summary>The heightmap of everything that shadows <paramref name="receiverId"/>.</summary>
        public HfieldModel For(string receiverId)
        {
            return Project(SC2ShadowCasting.CastersOnto(AllIds(), _rules, receiverId ?? ""));
        }

        /// <summary>The heightmap of every caster, with no rules applied.</summary>
        public HfieldModel All() => Project(AllIds());

        private List<string> AllIds()
        {
            var ids = new List<string>();
            foreach (var caster in _casters) ids.Add(caster.Id);
            return ids;
        }

        private HfieldModel Project(List<string> allowed)
        {
            var key = SC2ShadowCasting.SetKey(allowed);
            if (_heightmaps.TryGetValue(key, out var cached)) return cached;

            var models = new List<TerrainModel>();
            foreach (var caster in _casters)
                if (allowed.Contains(caster.Id)) models.Add(caster.Model);
            // No material exclusion - only Collision2's own separate projection respects
            // SC2TerrainData.Collision2ExcludedMaterialNames.
            return _heightmaps[key] = SC2HfieldProjectionService.ProjectMany(models, MapX, MapY);
        }
    }

    /// <summary>The heightmap for the shadow horizon-march's occlusion test with every object shading every other - what the
    /// point cloud bake sees, and what the lighting bake sees when no shadow rules are set. See
    /// <see cref="BuildShadowCasters"/> for what it covers. <paramref name="sharedOffset"/> is what a caller whose geometry is in
    /// the core's world space must SUBTRACT from a position before looking it up.</summary>
    internal static HfieldModel BuildShadowHfield(SC2TerrainData coreData, List<SC2ImportedTerrainExportData> importedTerrains,
        out Vector3 sharedOffset, out int mapX, out int mapY, TerrainModel prebuiltCoreModel = null)
    {
        ShadowCasterField field;
        if (prebuiltCoreModel != null)
        {
            // Owners are only needed to tell the objects apart, which this all-casters form does not do.
            var owners = new List<int>(); var layers = new List<ImportedLayerRef>();
            for (var li = 0; li < prebuiltCoreModel.Layers.Count; li++) { owners.Add(0); layers.Add(null); }
            field = BuildShadowCasters(coreData, importedTerrains, null, prebuiltCoreModel, owners, layers);
        }
        else
        {
            field = BuildShadowCasters(coreData, importedTerrains, null);
        }
        sharedOffset = field.Offset;
        mapX = field.MapX;
        mapY = field.MapY;
        return field.All();
    }

    /// <summary>Collects what can occlude the sun for a bake, per object. The sun's rays travel at most
    /// <see cref="SC2RnmMaskBaker.ShadowMaxDistance"/>, so the frame covers the core's playable area plus that much around it
    /// when imported geometry stands outside it, and the whole decorative background when it is enabled - all expressed in the
    /// background's own local coordinate space (worldPos - sharedOffset, ComputeBounds' minX/minZ as origin), so a tall core
    /// feature shadows the background and vice versa. <paramref name="prebuiltCoreModel"/> is the core (+ imported) geometry a
    /// caller has already built with BuildBakeGeometry (it is only read here, with the owner of each layer given by the two
    /// per-layer lists that call returned), so it is not built a second time.</summary>
    private static ShadowCasterField BuildShadowCasters(SC2TerrainData coreData, List<SC2ImportedTerrainExportData> importedTerrains,
        List<string> shadowRules, TerrainModel prebuiltCoreModel = null, List<int> pairIndexPerLayer = null,
        List<ImportedLayerRef> importedLayers = null)
    {
        var field = new ShadowCasterField(shadowRules);
        var coreModel = prebuiltCoreModel;
        if (coreModel == null)
            coreModel = BuildBakeGeometry(coreData, importedTerrains, out pairIndexPerLayer, out importedLayers);
        var owners = ReceiverIdsPerLayer(pairIndexPerLayer, importedLayers, importedTerrains);

        var gridSize = coreData.CostsGridSize;
        var coreShift = Vector3.zero;
        TerrainModel background = null;
        if (coreData.BackgroundTerrainEnabled && coreData.BackgroundTerrainData != null)
        {
            SC2BackgroundTerrain.ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out _);
            field.Offset = new Vector3(minX, 0f, minZ);
            coreShift = -field.Offset;
            field.MapX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, maxX - minX)) + 1, 2, MaxShadowHfieldSize);
            field.MapY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, maxZ - minZ)) + 1, 2, MaxShadowHfieldSize);

            // The background's own kept mesh - BuildForExportOrPreview's vertices are already local (no shift needed).
            background = new TerrainModel { Version = 6 };
            var bgBuild = SC2BackgroundTerrain.BuildForExportOrPreview(coreData, out _);
            foreach (var builtLayer in bgBuild.Layers)
            {
                var s = builtLayer.Mesh;
                if (s.Triangles == null || s.Triangles.Length == 0) continue;
                AppendRaw(background, s.Vertices, s.Triangles, Vector3.zero);
            }
        }
        else if (HasGeometryBeyondPlayableArea(coreModel, gridSize))
        {
            // Imported geometry stands outside the playable area (a frame of tall blocks around the map edge, say). A
            // heightmap clipped to the playable area would not see it: the horizon march would only touch the one grid
            // column the blocks straddle, which shades the edge strip in ragged, stepped bands instead of with the blocks'
            // real shadows. The grid grows by as far as a shadow ray can travel; the geometry is shifted into it.
            var margin = Mathf.Min((int)SC2RnmMaskBaker.ShadowMaxDistance, (MaxShadowHfieldSize - 1 - gridSize) / 2);
            field.Offset = new Vector3(-margin, 0f, -margin);
            coreShift = -field.Offset;
            field.MapX = gridSize + 1 + 2 * margin;
            field.MapY = field.MapX;
        }
        else
        {
            field.MapX = gridSize + 1;
            field.MapY = gridSize + 1;
        }

        // One caster per object, in the order the core, the imported objects, the background.
        var ownerOrder = new List<string>();
        foreach (var owner in owners) if (!ownerOrder.Contains(owner)) ownerOrder.Add(owner);
        foreach (var owner in ownerOrder)
        {
            var model = new TerrainModel { Version = 6 };
            var ownerId = owner;
            AppendShifted(model, coreModel, coreShift, li => li < owners.Count && owners[li] == ownerId);
            field.AddCaster(ownerId, model);
        }
        if (background != null) field.AddCaster(SC2ShadowCasting.BackgroundId, background);
        return field;
    }

    private const int MaxShadowHfieldSize = 8193;

    /// <summary>Whether any solid (non-water) vertex of <paramref name="model"/> lies outside the square playable area
    /// [0, <paramref name="gridSize"/>] on X or Z.</summary>
    private static bool HasGeometryBeyondPlayableArea(TerrainModel model, int gridSize)
    {
        const float tolerance = 0.5f;
        var limit = gridSize + tolerance;
        for (var li = 0; li < model.Layers.Count; li++)
        {
            if (li < model.Materials.Count && SC2HfieldProjectionService.IsWaterMaterial(model.Materials[li])) continue;
            var vertEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeVertices : model.Vertices.Count;
            for (var vi = model.Layers[li].CumulativeVertices; vi < vertEnd; vi++)
            {
                var v = model.Vertices[vi];
                if (v.PosX < -tolerance || v.PosX > limit || v.PosZ < -tolerance || v.PosZ > limit) return true;
            }
        }
        return false;
    }

    /// <summary>Appends every layer of <paramref name="source"/> (or only the ones <paramref name="includeLayer"/> accepts) into
    /// <paramref name="target"/> as its own new layer, translating every vertex position by <paramref name="shift"/> -
    /// triangle indices are already LOCAL to their own layer (this codebase's established convention, confirmed in
    /// BuildBakeGeometry's own comments), so they copy across unchanged. Only positions matter here (this model is only ever
    /// used to build a heightmap for shadow occlusion, never serialized or lit directly) - UV/normal fields are left at their
    /// TerrainVertex default.</summary>
    private static void AppendShifted(TerrainModel target, TerrainModel source, Vector3 shift, Func<int, bool> includeLayer = null)
    {
        for (var li = 0; li < source.Layers.Count; li++)
        {
            if (includeLayer != null && !includeLayer(li)) continue;
            var srcLayer = source.Layers[li];
            var vertBase = srcLayer.CumulativeVertices;
            var vertEnd = li + 1 < source.Layers.Count ? source.Layers[li + 1].CumulativeVertices : source.Vertices.Count;
            var triStart = srcLayer.CumulativeIndices / 3;
            var triEnd = li + 1 < source.Layers.Count ? source.Layers[li + 1].CumulativeIndices / 3 : source.Triangles.Count;

            var layer = new BdfIO.Formats.Terrain.TerrainLayer { CumulativeVertices = target.Vertices.Count, CumulativeIndices = target.Triangles.Count * 3 };
            target.Layers.Add(layer);
            for (var vi = vertBase; vi < vertEnd; vi++)
            {
                var v = source.Vertices[vi];
                target.Vertices.Add(new TerrainVertex { PosX = v.PosX + shift.x, PosY = v.PosY + shift.y, PosZ = v.PosZ + shift.z });
            }
            for (var t = triStart; t < triEnd; t++)
                target.Triangles.Add(source.Triangles[t]);
            target.Materials.Add(li < source.Materials.Count ? source.Materials[li] : new TerrainMaterial());
        }
    }

    /// <summary>Appends one raw vertex/triangle chunk (not itself an
    /// existing TerrainModel's layer) as a new layer, shifted by
    /// <paramref name="shift"/> — used for the background's own kept mesh
    /// whose SC2TerrainSimplifierResult/raw arrays aren't
    /// TerrainModel-shaped to begin with.</summary>
    private static void AppendRaw(TerrainModel target, Vector3[] verts, int[] tris, Vector3 shift)
    {
        var layer = new BdfIO.Formats.Terrain.TerrainLayer { CumulativeVertices = target.Vertices.Count, CumulativeIndices = target.Triangles.Count * 3 };
        target.Layers.Add(layer);
        for (var vi = 0; vi < verts.Length; vi++)
        {
            var v = verts[vi] + shift;
            target.Vertices.Add(new TerrainVertex { PosX = v.x, PosY = v.y, PosZ = v.z });
        }
        for (var i = 0; i < tris.Length; i += 3)
            target.Triangles.Add(new TerrainTriangle { V0 = (uint)tris[i], V1 = (uint)tris[i + 1], V2 = (uint)tris[i + 2] });
        target.Materials.Add(new TerrainMaterial());
    }

    /// <summary>Shared bake body for both Bake and BakeBackground — takes an
    /// already-built geometry model and its shadow casters and writes real baked lighting
    /// into <paramref name="pairs"/>. <paramref name="bakeSettingsSource"/>
    /// supplies the RnmBakeSync*/RnmBakeOverride* Lighting Bake Inputs
    /// (always the CORE's SC2TerrainData — there is only one such config,
    /// shared by both bakes). <paramref name="receiverIdPerLayer"/> names the object each layer of
    /// <paramref name="model"/> belongs to, which picks the heightmap of <paramref name="shadow"/> that shades it.
    /// <paramref name="hfieldOffset"/> is subtracted
    /// from each texel's world position before it's looked up against
    /// that heightmap — nonzero exactly when `model`'s own
    /// coordinate frame doesn't already match the (possibly padded or unified,
    /// background-local) frame of the casters — see BuildShadowCasters.
    ///
    /// Only computes: it returns the baked masks and touches neither <paramref name="pairs"/> nor the terrain data, so it can run on
    /// a worker thread. <see cref="ApplyBake"/> stores the outcome and must run on the main thread.</summary>
    private static BakeOutcome BakeInternal(TerrainModel model, List<int> pairIndexPerLayer, List<SC2MaterialPair> pairs,
        ShadowCasterField shadow, List<string> receiverIdPerLayer, Vector3 hfieldOffset, SC2TerrainData bakeSettingsSource, InfoModel info,
        ImportedBake imported = null, bool bakeImportedWorldRnm = true)
    {
        SC2BusyOverlay.Report("Preparing the lighting bake");
        var tangents = SC2RnmMaskBaker.ComputeVertexTangents(model);

        // Sunlight refracts at the
        // water surface but keeps travelling in a mostly-directional beam
        // through the water column, scattering more the deeper it goes -
        // real underwater shadow edges are softer (wider penumbra) than dry-
        // land ones, not identical to them. WaterElevation/MaxWaterDepth are
        // the same real per-map info.bdf fields the water plane/Costs' water
        // detection already use - reused here rather than inventing a second
        // water-depth source.
        var waterEnabled = info.WaterEnabled != 0;
        var waterElevation = info.WaterElevation;
        var maxWaterDepth = info.MaxWaterDepth > 1f ? info.MaxWaterDepth : SC2RnmMaskBaker.DefaultMaxWaterDepthForBlur;
        // User-configurable shadow sharpness (Lighting Bake Inputs) — see
        // SC2TerrainData.RnmBakeShadowSoftRadius's own doc comment. The
        // underwater pass keeps the same "4x the dry-land radius" ratio the
        // old hardcoded ShadowSoftRadiusUnderwaterMax=24 (4x the old
        // hardcoded ShadowSoftRadius=6) always had, just scaled off
        // whatever radius the user picks now instead of a second fixed
        // constant.
        var shadowSoftRadius = Mathf.Max(0, bakeSettingsSource.RnmBakeShadowSoftRadius);
        var shadowSoftRadiusUnderwater = Mathf.Max(shadowSoftRadius, shadowSoftRadius * 4);
        var (sr, sg, sb) = info.SunColor;
        var sunColor = bakeSettingsSource.RnmBakeSyncSunColor ? new Color(sr, sg, sb, 1f) : bakeSettingsSource.RnmBakeOverrideSunColor;
        var sunIntensity = bakeSettingsSource.RnmBakeSyncSunIntensity ? info.SunIntensity : bakeSettingsSource.RnmBakeOverrideSunIntensity;
        var (shr, shg, shb, _) = info.ShadowColor;
        var shadowColor = bakeSettingsSource.RnmBakeSyncShadowColor ? new Color(shr, shg, shb, 1f) : bakeSettingsSource.RnmBakeOverrideShadowColor;
        Vector3 sunDirection;
        if (bakeSettingsSource.RnmBakeSyncSunDirection)
        {
            var (sdx, sdy, sdz) = info.SunDirection;
            sunDirection = new Vector3(sdx, sdy, sdz);
        }
        else
        {
            sunDirection = bakeSettingsSource.RnmBakeOverrideSunDirection;
        }
        if (sunDirection.sqrMagnitude < 1e-6f) sunDirection = new Vector3(0.3f, 0.8f, 0.3f);
        sunDirection.Normalize();
        // Confirmed via a real reference map's own SunDirection value
        // (Y = +0.5, i.e. tilted well above the horizon, not the small-noise
        // near-zero a rounding/sign artifact would produce): SunDirection is
        // the direction TO the sun (points up into the sky), not the
        // direction light travels FROM the sun as SC2Environment.
        // ApplyToScene's own Quaternion.LookRotation(SunDirection) call
        // assumes (that function's comment already flags this exact sign as
        // unverified: "if the light comes from above the wrong way, check
        // the sign" - this bake had the same wrong assumption, now fixed;
        // ApplyToScene itself is untouched, out of scope here). No negation
        // needed - SunDirection already IS "direction to the light" for
        // standard N.L lighting.
        var toSun = sunDirection;

        var maskGroups = new Dictionary<string, List<int>>(); // maskName -> layer indices
        for (var li = 0; li < pairIndexPerLayer.Count; li++)
        {
            var pi = pairIndexPerLayer[li];
            if (pi < 0 || pi >= pairs.Count) continue; // imported terrain - shadow-casting geometry only, no mask of its own
            var maskName = string.IsNullOrEmpty(pairs[pi].RnmMaskName) ? "mask01" : pairs[pi].RnmMaskName;
            if (!maskGroups.TryGetValue(maskName, out var list)) maskGroups[maskName] = list = new List<int>();
            list.Add(li);
        }

        var settings = new SC2RnmMaskBaker.Settings
        {
            ToSun = toSun, HfieldOffset = hfieldOffset,
            HfieldForLayer = li => shadow.For(li < receiverIdPerLayer.Count ? receiverIdPerLayer[li] : null),
            WaterEnabled = waterEnabled, WaterElevation = waterElevation, MaxWaterDepth = maxWaterDepth,
            ShadowSoftRadius = shadowSoftRadius, ShadowSoftRadiusUnderwater = shadowSoftRadiusUnderwater,
            SunColor = sunColor, ShadowColor = shadowColor, SunIntensity = sunIntensity,
        };

        var outcome = new BakeOutcome();
        var maskNumber = 0;
        var coreTexelWorld = 0f;
        foreach (var kv in maskGroups)
        {
            var layerIndices = kv.Value;
            maskNumber++;
            SC2BusyOverlay.Report($"Baking lighting: mask {maskNumber} of {maskGroups.Count}", (maskNumber - 1f) / maskGroups.Count);
            // All pairs sharing one mask name share one texture, so they must
            // share one resolution too - take the first as representative.
            var repPair = pairs[pairIndexPerLayer[layerIndices[0]]];
            var result = SC2RnmMaskBaker.BakeMaskGroup(model, tangents, layerIndices, repPair.MaskWidth, repPair.MaskHeight, settings);
            outcome.PairMasks.Add((layerIndices, result));
            if (coreTexelWorld <= 0f && repPair.MaskWidth > 0)
                coreTexelWorld = Mathf.Max(1f, bakeSettingsSource.GridWidth * bakeSettingsSource.CellSize) / repPair.MaskWidth;
        }

        // The masks of imported objects pack each object's floor and walls into their own UV2 at whatever density the artist
        // chose (a few world units per texel for a large floor, against the core's fraction of a unit), so a penumbra of a
        // fixed number of texels would come out many times wider there. Their shadows keep the width in world units that the
        // Shadow Sharpness setting gives on the core's own masks.
        settings.ReferenceTexelWorld = coreTexelWorld;

        // Imported terrain.bdf layers that were given a target mask
        // (Lighting Bake > Imported Objects) are baked by THEIR OWN UV2 into
        // masks of their own — a namespace apart from the pair masks above.
        if (imported != null) BakeImportedMasks(model, tangents, settings, imported, outcome);

        // Separate from the per-object masks above (and independent of
        // whether that feature's session/blob-store is even available) — a
        // single, always-on, world-space-sampled bake so SC2Decal.shader can
        // give a decal sitting on imported terrain a reasonable, baked-
        // consistent tint even when nobody has configured a per-object mask
        // for it. MUST run after BakeImportedMasks just above — both read/
        // write the SAME imported layers' Uv2U/Uv2V, and BakeImportedMasks
        // needs to see the ORIGINAL per-object UV2 first. Gated on
        // bakeImportedWorldRnm — BakeBackground's OWN model never has a -1
        // (imported) layer at all, so without this gate every background
        // bake would see zero imported layers and WIPE the real data an
        // earlier core Bake() already produced.
        if (bakeImportedWorldRnm)
            outcome.World = BakeImportedWorldRnm(model, tangents, pairIndexPerLayer, settings);

        return outcome;
    }

    /// <summary>What a bake computed, waiting to be stored by <see cref="ApplyBake"/> on the main thread.</summary>
    private sealed class BakeOutcome
    {
        public readonly List<(List<int> Layers, SC2RnmMaskBaker.MaskResult Result)> PairMasks =
            new List<(List<int>, SC2RnmMaskBaker.MaskResult)>();
        public readonly List<(string Name, SC2RnmMaskBaker.MaskResult Result)> ImportedMasks =
            new List<(string, SC2RnmMaskBaker.MaskResult)>();
        /// <summary>Null when this bake does not produce the world-space imported-terrain texture (the background's does not).</summary>
        public WorldRnmResult World;
    }

    /// <summary>The single world-space RGBA texture of the imported terrain (see BakeImportedWorldRnm); an empty
    /// <see cref="Rgba"/> clears it.</summary>
    private sealed class WorldRnmResult
    {
        public byte[] Rgba = Array.Empty<byte>();
        public int Width, Height;
        public float OriginX, OriginZ, SpanX, SpanZ;
        public bool KeepsPlacement; // true when the placement fields below hold real values
    }

    /// <summary>Stores a computed bake: the masks into <paramref name="pairs"/>, the imported masks through their commit callback,
    /// and the world-space imported texture into <paramref name="coreData"/>. Main thread. Returns the status line.</summary>
    private static string ApplyBake(BakeOutcome outcome, List<int> pairIndexPerLayer, List<SC2MaterialPair> pairs,
        SC2TerrainData coreData, string unitLabel, ImportedBake imported)
    {
        foreach (var (layerIndices, result) in outcome.PairMasks)
        {
            foreach (var li in layerIndices)
            {
                var pair = pairs[pairIndexPerLayer[li]];
                pair.RnmBakedRgb0 = result.Rgb0;
                pair.RnmBakedRgb1 = result.Rgb1;
                pair.RnmBakedRgb2 = result.Rgb2;
                pair.RnmBakedShadowAlpha = result.ShadowAlpha;
            }
        }

        var importedCount = imported != null ? StoreImportedMasks(outcome, imported) : 0;

        if (outcome.World != null)
        {
            var world = outcome.World;
            coreData.ImportedRnmBakedRgba = world.Rgba;
            coreData.ImportedRnmWidth = world.Width;
            coreData.ImportedRnmHeight = world.Height;
            if (world.KeepsPlacement)
            {
                coreData.ImportedRnmWorldOriginX = world.OriginX;
                coreData.ImportedRnmWorldOriginZ = world.OriginZ;
                coreData.ImportedRnmWorldSpanX = world.SpanX;
                coreData.ImportedRnmWorldSpanZ = world.SpanZ;
            }
        }

        coreData.RnmBakeGeneration++;
        var bakedCount = outcome.PairMasks.Count;
        return importedCount > 0
            ? $"Baked lighting for {bakedCount} {unitLabel} + {importedCount} imported mask(s)."
            : $"Baked lighting for {bakedCount} {unitLabel}.";
    }

    /// <summary>One merged imported layer of the bake model: which imported
    /// object/layer it came from and the imported RNM mask it targets ("" =
    /// none, geometry only casts shadow).</summary>
    private sealed class ImportedLayerRef
    {
        public int ObjectIndex;
        public int LayerIndex;
        public string MaskName = "";
    }

    private sealed class ImportedBake
    {
        public List<ImportedLayerRef> Layers;
        public List<SC2ImportedTerrainExportData> Objects;
        public SC2MapSession Session;
        public Action<List<SC2RnmMaskSetDocument>> Commit;
        /// <summary>(object index, layer index) -> the layer's slice-1 blend sampler, read from the material's texture up front on
        /// the main thread (a texture can only be read there); a layer without one has no entry.</summary>
        public Dictionary<(int Object, int Layer), Func<float, float, byte>> BlendSamplers;
    }

    /// <summary>Reads the blend sampler of every imported layer that is assigned a target mask. Main thread: it reads textures.</summary>
    private static Dictionary<(int Object, int Layer), Func<float, float, byte>> BuildImportedBlendSamplers(
        List<SC2ImportedTerrainExportData> importedTerrains)
    {
        var samplers = new Dictionary<(int, int), Func<float, float, byte>>();
        if (importedTerrains == null) return samplers;
        for (var oi = 0; oi < importedTerrains.Count; oi++)
        {
            var names = importedTerrains[oi].RnmBakeMaskNames;
            var materials = importedTerrains[oi].Materials;
            if (names == null || materials == null) continue;
            for (var li = 0; li < names.Length && li < materials.Length; li++)
            {
                if (string.IsNullOrEmpty(names[li])) continue;
                var sampler = BuildBlendSampler(materials[li]);
                if (sampler != null) samplers[(oi, li)] = sampler;
            }
        }
        return samplers;
    }

    /// <summary>Bakes every imported layer that targets an existing imported
    /// mask, one mask at a time (all layers sharing a mask in one pass, so they
    /// end up in the same arrays). The results go into <paramref name="outcome"/>;
    /// <see cref="StoreImportedMasks"/> stores them.</summary>
    private static void BakeImportedMasks(TerrainModel model, Vector3[] tangents, SC2RnmMaskBaker.Settings settings, ImportedBake imported,
        BakeOutcome outcome)
    {
        var rnm = imported.Session.Document.Terrain.ImportedRnm;

        var groups = new Dictionary<string, List<int>>();
        for (var li = 0; li < model.Layers.Count && li < imported.Layers.Count; li++)
        {
            var layerRef = imported.Layers[li];
            if (layerRef == null || string.IsNullOrEmpty(layerRef.MaskName)) continue;
            if (SC2ImportedRnmMasks.Find(rnm, layerRef.MaskName) == null) continue;
            if (!groups.TryGetValue(layerRef.MaskName, out var list)) groups[layerRef.MaskName] = list = new List<int>();
            list.Add(li);
        }
        if (groups.Count == 0) return;

        Func<float, float, byte> SamplerFor(int li)
        {
            var layerRef = imported.Layers[li];
            return imported.BlendSamplers != null && imported.BlendSamplers.TryGetValue((layerRef.ObjectIndex, layerRef.LayerIndex), out var sampler)
                ? sampler : null;
        }

        var maskNumber = 0;
        foreach (var group in groups)
        {
            maskNumber++;
            SC2BusyOverlay.Report($"Baking imported masks: {maskNumber} of {groups.Count}");
            var set = SC2ImportedRnmMasks.Find(rnm, group.Key);
            var result = SC2RnmMaskBaker.BakeMaskGroup(model, tangents, group.Value, set.Width, set.Height, settings, SamplerFor);
            outcome.ImportedMasks.Add((group.Key, result));
        }
    }

    /// <summary>Stores the imported masks a bake computed into the blob store and hands the complete new mask list to
    /// <see cref="ImportedBake.Commit"/>. Main thread. Returns how many masks were stored.</summary>
    private static int StoreImportedMasks(BakeOutcome outcome, ImportedBake imported)
    {
        if (outcome.ImportedMasks.Count == 0) return 0;
        var store = imported.Session.BlobStore;
        var next = imported.Session.Document.Terrain.ImportedRnm.CloneMaskSets();
        foreach (var (name, result) in outcome.ImportedMasks)
        {
            var set = SC2ImportedRnmMasks.Find(new SC2RnmDocument { MaskSets = next }, name);
            set.BakedRgb0 = SC2ImportedRnmMasks.Put(store, result.Rgb0);
            set.BakedRgb1 = SC2ImportedRnmMasks.Put(store, result.Rgb1);
            set.BakedRgb2 = SC2ImportedRnmMasks.Put(store, result.Rgb2);
            set.BakedShadowAlpha = SC2ImportedRnmMasks.Put(store, result.ShadowAlpha);

            // MaskBytes: neutral RGB + the carried-over slice-1 blend alpha
            // (same RGBA layout SC2MaterialPair.RnmMaskBytes uses).
            var n = set.Width * set.Height;
            var rgba = new byte[n * 4];
            for (var i = 0; i < n; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 128;
                rgba[i * 4 + 3] = result.Blend[i];
            }
            set.MaskBytes = SC2ImportedRnmMasks.Put(store, rgba);
        }
        imported.Commit(next);
        return outcome.ImportedMasks.Count;
    }

    /// <summary>Bakes ONE world-space RGBA texture covering the union of every imported-terrain
    /// layer's world bounds (pairIndexPerLayer[li] &lt; 0 — "shadow-casting geometry only, no pair
    /// mask", see BuildBakeGeometry's own tagging), storing the SAME combined-and-contrast-stretched
    /// value SC2TerrainView.shader's own PreviewMode==4 live-terrain preview already computes
    /// per-pixel at runtime — decals don't need the 3 separate basis layers any more than that
    /// preview does. The result is stored into the terrain data's ImportedRnm* fields by ApplyBake (no
    /// session/blob-store dependency at bake time — SC2RuntimeTerrainPersistence handles turning
    /// this in-memory byte array into a real saved blob separately). Reuses the exact same
    /// SC2RnmMaskBaker.BakeMaskGroup rasterizer the pair masks above use, by temporarily
    /// overwriting the imported layers' own Uv2U/Uv2V to world-space-normalized coordinates —
    /// BakeMaskGroup rasterizes purely by each vertex's own UV2, so this one substitution is the
    /// only change needed to redirect it at a world-space domain instead of a per-object one. MUST
    /// run after BakeImportedMasks — see this method's own call site comment.</summary>
    private static WorldRnmResult BakeImportedWorldRnm(TerrainModel model, Vector3[] tangents, List<int> pairIndexPerLayer,
        SC2RnmMaskBaker.Settings settings)
    {
        SC2BusyOverlay.Report("Baking the imported terrain's world lighting");
        var layerIndices = new List<int>();
        for (var li = 0; li < pairIndexPerLayer.Count; li++)
            if (pairIndexPerLayer[li] < 0) layerIndices.Add(li);
        if (layerIndices.Count == 0) return new WorldRnmResult();

        var minX = float.MaxValue; var minZ = float.MaxValue;
        var maxX = float.MinValue; var maxZ = float.MinValue;
        foreach (var li in layerIndices)
        {
            var layer = model.Layers[li];
            var vertEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeVertices : model.Vertices.Count;
            for (var vi = layer.CumulativeVertices; vi < vertEnd; vi++)
            {
                var v = model.Vertices[vi];
                if (v.PosX < minX) minX = v.PosX; if (v.PosX > maxX) maxX = v.PosX;
                if (v.PosZ < minZ) minZ = v.PosZ; if (v.PosZ > maxZ) maxZ = v.PosZ;
            }
        }
        if (minX > maxX) return new WorldRnmResult(); // degenerate — layers tagged imported but with no actual vertices

        const float pad = 8f; // world units, so real edge geometry isn't sampled exactly at the UV border
        minX -= pad; minZ -= pad; maxX += pad; maxZ += pad;
        var spanX = Mathf.Max(1f, maxX - minX);
        var spanZ = Mathf.Max(1f, maxZ - minZ);

        foreach (var li in layerIndices)
        {
            var layer = model.Layers[li];
            var vertEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeVertices : model.Vertices.Count;
            for (var vi = layer.CumulativeVertices; vi < vertEnd; vi++)
            {
                var v = model.Vertices[vi];
                v.Uv2U = (v.PosX - minX) / spanX;
                v.Uv2V = (v.PosZ - minZ) / spanZ;
                model.Vertices[vi] = v; // TerrainVertex is a struct — List<T>'s indexer needs an explicit write-back
            }
        }

        // Fixed default for v1, no new UI control — matches this codebase's
        // own existing "fixed resolution, not tied to physical area"
        // precedent (SC2TerrainRuntime.RebuildRnmArrays' own 1024x1024).
        const int resolution = 1024;
        var result = SC2RnmMaskBaker.BakeMaskGroup(model, tangents, layerIndices, resolution, resolution, settings);

        var n = resolution * resolution;
        var rgba = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var m0 = result.Rgb0[i * 3 + c] / 255f;
                var m1 = result.Rgb1[i * 3 + c] / 255f;
                var m2 = result.Rgb2[i * 3 + c] / 255f;
                // Same fixed equal-weight sum + contrast stretch SC2TerrainView.shader's live-terrain
                // preview already uses — see this method's own doc comment.
                var combined = (m0 + m1 + m2) * 0.5774f;
                combined = Mathf.Clamp01((combined - 0.5f) * 2.2f + 0.5f);
                rgba[i * 4 + c] = (byte)Mathf.RoundToInt(combined * 255f);
            }
            rgba[i * 4 + 3] = result.Covered[i] ? (byte)255 : (byte)0; // real per-texel coverage, before gap-fill
        }

        return new WorldRnmResult
        {
            Rgba = rgba, Width = resolution, Height = resolution,
            OriginX = minX, OriginZ = minZ, SpanX = spanX, SpanZ = spanZ, KeepsPlacement = true,
        };
    }

    /// <summary>A (u, v) → alpha sampler over the material's current
    /// <c>_RNMMask1</c> texture (its slice-1 D1/D2 blend), or null when there
    /// is none / it can't be read on the CPU — the bake then writes 0.</summary>
    private static Func<float, float, byte> BuildBlendSampler(Material material)
    {
        if (material == null || !material.HasProperty("_RNMMask1")) return null;
        var texture = material.GetTexture("_RNMMask1") as Texture2D;
        if (texture == null || !texture.isReadable) return null;
        Color32[] pixels;
        try { pixels = texture.GetPixels32(); }
        catch (Exception) { return null; }
        int w = texture.width, h = texture.height;
        return (u, v) => pixels[Mathf.Clamp((int)(v * h), 0, h - 1) * w + Mathf.Clamp((int)(u * w), 0, w - 1)].a;
    }

    /// <summary>Lightweight equivalent of SC2TerrainExporter.BuildExportArtifacts's
    /// model-building step (same layer-building + imported-terrain merge),
    /// without the texture registration/dialogs/BSP-rebuild that function
    /// also does - this bake only needs geometry (positions/normals/UV2),
    /// never textures or the final serialized BSP.</summary>
    private static TerrainModel BuildBakeGeometry(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains,
        out List<int> pairIndexPerLayer, out List<ImportedLayerRef> importedLayers)
    {
        var model = new TerrainModel { Version = 6 };
        pairIndexPerLayer = new List<int>();
        // Parallel to model.Layers: null for a core pair layer, the source
        // object/layer (and its target mask) for a merged imported layer.
        importedLayers = new List<ImportedLayerRef>();

        // Same outer-perimeter lock the real exporter uses (see its own
        // comment) — this bake's geometry must match what's actually
        // exported, including the map edge surviving simplification intact,
        // or the lighting bake would be computed against a different (and
        // possibly edge-truncated) mesh than the one that ships.
        var build = SC2TerrainLayerBuilder.Build(data, sortByTriangleCount: true, null,
            SC2TerrainSimplifier.FindOuterPerimeterVertices(data));
        // Same per-axis unwrap as the real exporter (SC2TerrainUv1Unwrap) —
        // built once for the whole core terrain, sampled per pair below with
        // that pair's own tiling scale. Keeping the bake's own UV1 in sync
        // with the exporter's is NOT cosmetic: ComputeVertexTangents derives
        // the tangent basis this bake lights against from UV1's real
        // gradient (see its own doc comment), and that basis now genuinely
        // depends on the mapping (per-axis stretch vs. planar), not just a
        // magnitude that normalizes away.
        SC2TerrainUv1Unwrap.BuildProfiles(data.Heights, data.GridWidth, data.GridHeight, data.CellSize,
            out var coreUv1ProfileU, out var coreUv1ProfileV, data.Uv1HeightWeight);
        // Core's own real world footprint for Uv2 — see
        // SC2TerrainExporter's identical fix for why this can't be a
        // hardcoded 1024 (this bake rasterizes lighting BY Uv2 texel
        // position — RasterizeTriangleGeometry's own p0/p1/p2 — so a wrong
        // span here bakes real lighting into only a fraction of the mask,
        // e.g. a 512 map's own bottom-left quarter).
        var coreSpanX = Mathf.Max(1f, data.GridWidth * data.CellSize);
        var coreSpanZ = Mathf.Max(1f, data.GridHeight * data.CellSize);
        for (var si = 0; si < build.Layers.Count; si++)
        {
            var builtLayer = build.Layers[si];
            var pair = data.Pairs[builtLayer.PairIndex];
            var s = builtLayer.Mesh;

            var layer = new BdfIO.Formats.Terrain.TerrainLayer { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
            model.Layers.Add(layer);
            pairIndexPerLayer.Add(builtLayer.PairIndex);
            importedLayers.Add(null);

            // Uses the same GlobalUvScale-aware resolver as everywhere else.
            var uv1Scale = data.EffectiveUvTilingScale(pair);
            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var pos = s.Vertices[vi]; var n = s.Normals[vi];
                var uv = SC2TerrainUv1Unwrap.Sample(coreUv1ProfileU, coreUv1ProfileV, data.GridWidth, data.GridHeight, data.CellSize, pos.x, pos.z);
                model.Vertices.Add(new TerrainVertex
                {
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    Uv1U = uv.x / uv1Scale, Uv1V = uv.y / uv1Scale,
                    Uv2U = pos.x / coreSpanX, Uv2V = pos.z / coreSpanZ,
                    NormalX = n.x, NormalY = n.y, NormalZ = n.z,
                });
            }
            for (var i = 0; i < s.Triangles.Length; i += 3)
                model.Triangles.Add(new TerrainTriangle { V0 = (uint)s.Triangles[i], V1 = (uint)s.Triangles[i + 1], V2 = (uint)s.Triangles[i + 2] });

            // Just enough for SC2HfieldProjectionService.Project's water
            // exclusion check (mat.Name/mat.ShaderFile) - full texture paths
            // aren't needed since this model is never serialized to terrain.bdf.
            model.Materials.Add(new TerrainMaterial { Name = pair.Name });
        }

        if (importedTerrains != null)
        {
            for (var oi = 0; oi < importedTerrains.Count; oi++)
            {
                var imported = importedTerrains[oi];
                var before = model.Layers.Count;
                SC2TerrainExporter.MergeImportedTerrain(model, imported);
                for (var li = before; li < model.Layers.Count; li++)
                {
                    pairIndexPerLayer.Add(-1); // no associated pair - never one of the pair masks
                    // Merged layers keep the source model's layer order, so the
                    // per-layer target array indexes by (li - before).
                    var localLayer = li - before;
                    var names = imported.RnmBakeMaskNames;
                    importedLayers.Add(new ImportedLayerRef
                    {
                        ObjectIndex = oi,
                        LayerIndex = localLayer,
                        MaskName = names != null && localLayer < names.Length ? names[localLayer] ?? "" : "",
                    });
                }
            }
        }

        return model;
    }

    /// <summary>Background equivalent of BuildBakeGeometry — builds the
    /// decorative background's own merged model (main mesh,
    /// SC2BackgroundTerrain.BuildForExportOrPreview) in the
    /// background's LOCAL coordinate space (ComputeBounds' own minX/minZ as
    /// origin, i.e. worldPos - offset) rather than absolute world space —
    /// the background is centered on the core's centroid and routinely
    /// offset from world origin by hundreds or thousands of world units, so
    /// staying in absolute world space here would need an equally large
    /// hfield covering empty space between world origin and the background
    /// itself. UV2 in this local space is exactly localPos/span — the SAME
    /// rebased [0,1] convention CreateLivePairMaterial/the real export
    /// already use for RNM mask sampling, since local position IS
    /// worldPosition-offset by construction (GetWorldOffset/ComputeBounds).
    ///
    /// This model's OWN vertex positions are only ever used as bake OUTPUT
    /// geometry (lighting-direction dot products, UV2 texel rasterization,
    /// tangent basis) — mapX/mapY are returned only for backward-compat
    /// with callers that used to build a background-only shadow hfield
    /// directly from this model; BakeBackground itself now sources shadow
    /// occlusion from BuildShadowHfield's unified core+background heightmap
    /// instead, in this exact same local frame — see that method's own doc
    /// comment for why cross-shadowing between the two zones needed a
    /// shared hfield in the first place.</summary>
    private static TerrainModel BuildBackgroundBakeGeometry(SC2TerrainData coreData, SC2TerrainData bg,
        out List<int> pairIndexPerLayer, out int mapX, out int mapY)
    {
        var model = new TerrainModel { Version = 6 };
        pairIndexPerLayer = new List<int>();

        var bgBuild = SC2BackgroundTerrain.BuildForExportOrPreview(coreData, out var offset);
        SC2BackgroundTerrain.ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out _);
        var spanX = Mathf.Max(1f, maxX - minX);
        var spanZ = Mathf.Max(1f, maxZ - minZ);
        // +1 for an inclusive grid (mapX/mapY are vertex counts, matching
        // every other hfield-shaped grid in this codebase), clamped to a
        // sane upper bound so a very large Footprint Size doesn't demand an
        // unreasonably huge one-shot allocation — the bake is a manual,
        // occasional trigger, not something that needs to scale unbounded.
        mapX = Mathf.Clamp(Mathf.CeilToInt(spanX) + 1, 2, 8193);
        mapY = Mathf.Clamp(Mathf.CeilToInt(spanZ) + 1, 2, 8193);

        // Background's own per-axis unwrap, in the SAME local frame as
        // s.Vertices below (bg's own Heights/grid, not the core's).
        SC2TerrainUv1Unwrap.BuildProfiles(bg.Heights, bg.GridWidth, bg.GridHeight, bg.CellSize,
            out var bgUv1ProfileU, out var bgUv1ProfileV, coreData.Uv1HeightWeight);

        foreach (var builtLayer in bgBuild.Layers)
        {
            var pi = builtLayer.PairIndex;
            if (pi < 0 || pi >= bg.Pairs.Count) continue;
            var pair = bg.Pairs[pi];
            var s = builtLayer.Mesh;
            if (s.Triangles == null || s.Triangles.Length == 0) continue;
            var uvScale = coreData.EffectiveUvTilingScale(pair);

            var layer = new BdfIO.Formats.Terrain.TerrainLayer
                { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
            model.Layers.Add(layer);
            pairIndexPerLayer.Add(pi);

            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var localPos = s.Vertices[vi]; // already local — BuildForExportOrPreview's own vertices need +offset to reach world space, so they're local as-is
                var n = s.Normals[vi];
                var uv = SC2TerrainUv1Unwrap.Sample(bgUv1ProfileU, bgUv1ProfileV, bg.GridWidth, bg.GridHeight, bg.CellSize, localPos.x, localPos.z);
                model.Vertices.Add(new TerrainVertex
                {
                    PosX = localPos.x, PosY = localPos.y, PosZ = localPos.z,
                    Uv1U = uv.x / uvScale, Uv1V = uv.y / uvScale,
                    Uv2U = localPos.x / spanX, Uv2V = localPos.z / spanZ,
                    NormalX = n.x, NormalY = n.y, NormalZ = n.z,
                });
            }
            for (var i = 0; i < s.Triangles.Length; i += 3)
                model.Triangles.Add(new TerrainTriangle { V0 = (uint)s.Triangles[i], V1 = (uint)s.Triangles[i + 1], V2 = (uint)s.Triangles[i + 2] });

            model.Materials.Add(new TerrainMaterial { Name = pair.Name });
        }

        return model;
    }
}
