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
        if (data == null) return "No active terrain to bake.";

        var model = BuildBakeGeometry(data, importedTerrains, out var pairIndexPerLayer, out var importedLayers);
        if (model.Layers.Count == 0) return "Nothing to bake - no triangles are assigned to any pair.";

        // The shadow horizon-march runs against a UNIFIED heightmap
        // covering both the core's own footprint and (when enabled) the
        // decorative background's full extent — see BuildShadowHfield's
        // own doc comment. This bake's OUTPUT still only ever writes into
        // data.Pairs (core); only what can OCCLUDE the sun is unified.
        var hfield = BuildShadowHfield(data, importedTerrains, out var hfieldOffset, out _, out _);

        var info = SC2TerrainExporter.BuildInfoModel(data, session);
        var imported = commitImportedMasks != null && session?.Document != null && session.BlobStore != null
            ? new ImportedBake { Layers = importedLayers, Objects = importedTerrains, Session = session, Commit = commitImportedMasks }
            : null;
        return BakeInternal(model, pairIndexPerLayer, data.Pairs, hfield, hfieldOffset, data, info, "RNM mask(s)", imported);
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
        if (coreData == null) return "No active terrain to bake.";
        var bg = coreData.BackgroundTerrainData;
        if (!coreData.BackgroundTerrainEnabled || bg == null) return "Background terrain is not enabled.";

        var model = BuildBackgroundBakeGeometry(coreData, bg, out var pairIndexPerLayer, out _, out _);
        if (model.Layers.Count == 0) return "Nothing to bake - background has no triangles assigned to any pair.";

        // BuildBackgroundBakeGeometry's own `model` is already expressed in
        // background-local space (worldPos - sharedOffset) — exactly the
        // same frame BuildShadowHfield's own sharedOffset uses when
        // background is enabled (it must be, here) — so no further shift
        // is needed for THIS bake's own geometry (hfieldOffset passed as
        // zero); BuildShadowHfield handles shifting the core's own absolute-
        // world geometry into that shared frame internally.
        var hfield = BuildShadowHfield(coreData, importedTerrains, out _, out _, out _);
        var info = SC2TerrainExporter.BuildInfoModel(coreData, session);
        return BakeInternal(model, pairIndexPerLayer, bg.Pairs, hfield, Vector3.zero, coreData, info, "background RNM mask(s)");
    }

    /// <summary>Builds the heightmap used ONLY for the shadow horizon-
    /// march's occlusion test (SC2RnmBakeService.IsShadowed) — separate
    /// from whatever geometry a given bake writes its own lighting OUTPUT
    /// against. When the decorative background is enabled, this spans BOTH
    /// the core's own footprint AND the background's full extent (main
    /// mesh + seam skirt), all expressed in the background's own local
    /// coordinate space (worldPos - sharedOffset, i.e. ComputeBounds' own
    /// minX/minZ as origin — background's own footprint is always a
    /// superset of the core's, since it's centered on and surrounds the
    /// core by construction) — so a tall core feature correctly shadows
    /// the background and vice versa. <paramref name="sharedOffset"/> is
    /// what a caller whose OWN geometry is still in absolute world space
    /// (i.e. Bake's own core model) must SUBTRACT from a world position
    /// before looking it up against this hfield; it's Vector3.zero when
    /// background is disabled (hfield stays core-only, exactly as before
    /// this fix) or for a caller (BakeBackground) whose own geometry is
    /// already in that same local frame.</summary>
    internal static HfieldModel BuildShadowHfield(SC2TerrainData coreData, List<SC2ImportedTerrainExportData> importedTerrains,
        out Vector3 sharedOffset, out int mapX, out int mapY)
    {
        if (!coreData.BackgroundTerrainEnabled || coreData.BackgroundTerrainData == null)
        {
            sharedOffset = Vector3.zero;
            mapX = coreData.CostsGridSize + 1;
            mapY = coreData.CostsGridSize + 1;
            var coreOnly = BuildBakeGeometry(coreData, importedTerrains, out _, out _);
            // No material exclusion - only Collision2's own separate
            // projection respects SC2TerrainData.Collision2ExcludedMaterialNames.
            return SC2HfieldProjectionService.Project(coreOnly, null, mapX, mapY);
        }

        var bg = coreData.BackgroundTerrainData;
        SC2BackgroundTerrain.ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out _);
        sharedOffset = new Vector3(minX, 0f, minZ);
        var spanX = Mathf.Max(1f, maxX - minX);
        var spanZ = Mathf.Max(1f, maxZ - minZ);
        mapX = Mathf.Clamp(Mathf.CeilToInt(spanX) + 1, 2, 8193);
        mapY = Mathf.Clamp(Mathf.CeilToInt(spanZ) + 1, 2, 8193);

        var unified = new TerrainModel { Version = 6 };

        // Core (+ any imported terrain.bdf objects) — built in absolute
        // world space, shifted into the shared (background-local) frame.
        var coreModel = BuildBakeGeometry(coreData, importedTerrains, out _, out _);
        AppendShifted(unified, coreModel, -sharedOffset);

        // Background's own kept mesh — BuildForExportOrPreview's vertices
        // are already local (no shift needed).
        var bgBuild = SC2BackgroundTerrain.BuildForExportOrPreview(coreData, out _);
        foreach (var builtLayer in bgBuild.Layers)
        {
            var s = builtLayer.Mesh;
            if (s.Triangles == null || s.Triangles.Length == 0) continue;
            AppendRaw(unified, s.Vertices, s.Triangles, Vector3.zero);
        }

        // Seam skirt — BuildSeamSkirt's own vertices are in core WORLD
        // space, shift into local like the core geometry above.
        SC2BackgroundTerrain.BuildSeamSkirt(coreData, out var skirtVerts, out var skirtNormals, out var skirtTris);
        if (skirtTris.Length > 0)
            AppendRaw(unified, skirtVerts, skirtTris, -sharedOffset);

        return SC2HfieldProjectionService.Project(unified, null, mapX, mapY);
    }

    /// <summary>Appends every layer of <paramref name="source"/> into
    /// <paramref name="target"/> as its own new layer, translating every
    /// vertex position by <paramref name="shift"/> — triangle indices are
    /// already LOCAL to their own layer (this codebase's established
    /// convention, confirmed in BuildBakeGeometry's own comments), so they
    /// copy across unchanged. Only positions matter here (this model is
    /// only ever used to build a heightmap for shadow occlusion, never
    /// serialized or lit directly) — UV/normal fields are left at their
    /// TerrainVertex default.</summary>
    private static void AppendShifted(TerrainModel target, TerrainModel source, Vector3 shift)
    {
        for (var li = 0; li < source.Layers.Count; li++)
        {
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
    /// and seam skirt, whose SC2TerrainSimplifierResult/raw arrays aren't
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
    /// already-built geometry model/hfield and writes real baked lighting
    /// into <paramref name="pairs"/>. <paramref name="bakeSettingsSource"/>
    /// supplies the RnmBakeSync*/RnmBakeOverride* Lighting Bake Inputs
    /// (always the CORE's SC2TerrainData — there is only one such config,
    /// shared by both bakes). <paramref name="hfieldOffset"/> is subtracted
    /// from each texel's world position before it's looked up against
    /// <paramref name="hfield"/> — nonzero exactly when `model`'s own
    /// coordinate frame doesn't already match the (possibly unified,
    /// background-local) hfield's — see BuildShadowHfield.</summary>
    private static string BakeInternal(TerrainModel model, List<int> pairIndexPerLayer, List<SC2MaterialPair> pairs,
        HfieldModel hfield, Vector3 hfieldOffset, SC2TerrainData bakeSettingsSource, InfoModel info, string unitLabel,
        ImportedBake imported = null)
    {
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
            ToSun = toSun, Hfield = hfield, HfieldOffset = hfieldOffset,
            WaterEnabled = waterEnabled, WaterElevation = waterElevation, MaxWaterDepth = maxWaterDepth,
            ShadowSoftRadius = shadowSoftRadius, ShadowSoftRadiusUnderwater = shadowSoftRadiusUnderwater,
            SunColor = sunColor, ShadowColor = shadowColor, SunIntensity = sunIntensity,
        };

        var bakedCount = 0;
        foreach (var kv in maskGroups)
        {
            var layerIndices = kv.Value;
            // All pairs sharing one mask name share one texture, so they must
            // share one resolution too - take the first as representative.
            var repPair = pairs[pairIndexPerLayer[layerIndices[0]]];
            var result = SC2RnmMaskBaker.BakeMaskGroup(model, tangents, layerIndices, repPair.MaskWidth, repPair.MaskHeight, settings);

            foreach (var li in layerIndices)
            {
                var pair = pairs[pairIndexPerLayer[li]];
                pair.RnmBakedRgb0 = result.Rgb0;
                pair.RnmBakedRgb1 = result.Rgb1;
                pair.RnmBakedRgb2 = result.Rgb2;
                pair.RnmBakedShadowAlpha = result.ShadowAlpha;
            }
            bakedCount++;
        }

        // Imported terrain.bdf layers that were given a target mask
        // (Lighting Bake > Imported Objects) are baked by THEIR OWN UV2 into
        // masks of their own — a namespace apart from the pair masks above.
        var importedCount = imported != null ? BakeImportedMasks(model, tangents, settings, imported) : 0;

        bakeSettingsSource.RnmBakeGeneration++;
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
    }

    /// <summary>Bakes every imported layer that targets an existing imported
    /// mask, one mask at a time (all layers sharing a mask in one pass, so they
    /// end up in the same arrays), and hands the complete new mask list to
    /// <see cref="ImportedBake.Commit"/>. Returns how many masks were baked.</summary>
    private static int BakeImportedMasks(TerrainModel model, Vector3[] tangents, SC2RnmMaskBaker.Settings settings, ImportedBake imported)
    {
        var store = imported.Session.BlobStore;
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
        if (groups.Count == 0) return 0;

        var samplerCache = new Dictionary<int, Func<float, float, byte>>();
        Func<float, float, byte> SamplerFor(int li)
        {
            if (samplerCache.TryGetValue(li, out var cached)) return cached;
            var layerRef = imported.Layers[li];
            Material material = null;
            var materials = layerRef.ObjectIndex < imported.Objects.Count ? imported.Objects[layerRef.ObjectIndex].Materials : null;
            if (materials != null && layerRef.LayerIndex < materials.Length) material = materials[layerRef.LayerIndex];
            return samplerCache[li] = BuildBlendSampler(material);
        }

        var next = rnm.CloneMaskSets();
        foreach (var group in groups)
        {
            var set = SC2ImportedRnmMasks.Find(new SC2RnmDocument { MaskSets = next }, group.Key);
            var result = SC2RnmMaskBaker.BakeMaskGroup(model, tangents, group.Value, set.Width, set.Height, settings, SamplerFor);
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
        return groups.Count;
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
    /// decorative background's own merged model (main mesh + seam skirt,
    /// SC2BackgroundTerrain.BuildForExportOrPreview/BuildSeamSkirt) in the
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
        // Core's own profile, for the seam skirt below (its vertices are
        // core world-space, bridging core-to-background, not background-local).
        SC2TerrainUv1Unwrap.BuildProfiles(coreData.Heights, coreData.GridWidth, coreData.GridHeight, coreData.CellSize,
            out var coreUv1ProfileU, out var coreUv1ProfileV, coreData.Uv1HeightWeight);

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

        // Seam skirt — appended as its own layer tagged pair index 0 (the
        // skirt is visually pair 0's own surface, matching how the real
        // export/preview merge it into pair 0's material) rather than
        // spliced into an existing layer's vertex/triangle range the way
        // the exporter does — that splice exists purely to avoid a second
        // TerrainMaterial entry in the exported file, a concern that
        // doesn't apply here since this model is never serialized. A plain
        // extra layer is simplest and BakeInternal's maskGroups grouping
        // already merges any number of layers sharing one pair/mask
        // correctly regardless of how many there are.
        SC2BackgroundTerrain.BuildSeamSkirt(coreData, out var skirtVerts, out var skirtNormals, out var skirtTris);
        if (skirtTris.Length > 0 && bg.Pairs.Count > 0)
        {
            var pair0 = bg.Pairs[0];
            var uvScale = coreData.EffectiveUvTilingScale(pair0);
            var layer = new BdfIO.Formats.Terrain.TerrainLayer
                { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
            model.Layers.Add(layer);
            pairIndexPerLayer.Add(0);

            for (var vi = 0; vi < skirtVerts.Length; vi++)
            {
                var localPos = skirtVerts[vi] - offset; // BuildSeamSkirt's own vertices are already in core WORLD space
                var n = skirtNormals[vi];
                // Sampled against the CORE's own profile using skirtVerts[vi]
                // directly (core world space, pre-offset) — the skirt
                // bridges the core terrain's edge, not the background's.
                var uv = SC2TerrainUv1Unwrap.SampleExtended(coreUv1ProfileU, coreUv1ProfileV, coreData.GridWidth, coreData.GridHeight, coreData.CellSize,
                    skirtVerts[vi].x, skirtVerts[vi].z);
                model.Vertices.Add(new TerrainVertex
                {
                    PosX = localPos.x, PosY = localPos.y, PosZ = localPos.z,
                    Uv1U = uv.x / uvScale, Uv1V = uv.y / uvScale,
                    Uv2U = localPos.x / spanX, Uv2V = localPos.z / spanZ,
                    NormalX = n.x, NormalY = n.y, NormalZ = n.z,
                });
            }
            for (var i = 0; i < skirtTris.Length; i += 3)
                model.Triangles.Add(new TerrainTriangle { V0 = (uint)skirtTris[i], V1 = (uint)skirtTris[i + 1], V2 = (uint)skirtTris[i + 2] });

            model.Materials.Add(new TerrainMaterial { Name = pair0.Name });
        }

        return model;
    }
}
