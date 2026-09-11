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
    // Tangent-space RNM basis - exact match to Resources/SC2Terrain.shader's
    // RNM_BASIS_0/1/2 (azimuth 0/120/240 deg, elevation 35.26 deg).
    private static readonly Vector3 Basis0 = new(0.8165f, 0f, 0.5774f);
    private static readonly Vector3 Basis1 = new(-0.4082f, 0.7071f, 0.5774f);
    private static readonly Vector3 Basis2 = new(-0.4082f, -0.7071f, 0.5774f);

    // Matches SC2HfieldProjectionService.Project's own height*128f raw encoding.
    private const float HeightRawScale = 128f;

    // Horizon-march shadow tuning - not corpus-derived, reasonable defaults
    // pending real in-game calibration (same "implement with reasonable
    // defaults, calibrate later" approach the Costs generation code uses).
    private const float ShadowMaxDistance = 512f;
    private const float ShadowStepSize = 4f;
    // Measured, not guessed: across all 186 vanilla masks (5.8M real surface
    // texels, UV-gap texels excluded) slice 0's alpha is strongly bimodal —
    // 43.9% sit at exactly 0.0 and 45.5% at exactly 1.0, with only ~10%
    // in between. Since terrain.hlsl multiplies this straight into the
    // specular term, a shadowed texel in a real map contributes NO specular
    // at all. The previous 40 (0.157) left 16% of the highlight alive
    // everywhere the horizon march found shade.
    private const byte ShadowedAlpha = 0;
    private const byte LitAlpha = 255;

    /// <summary>1 / (3 * 0.5774) = 1/sqrt(3). See FinalizeLighting: the game
    /// sums all three basis slices without normalizing (its own `col /= sum`
    /// is commented out), so each slice has to carry its share of the light
    /// rather than the whole of it.</summary>
    private const float BasisWeightNormalization = 0.57735027f;

    // Calibrated against 3 real vanilla masks (SC2_MP_001/lighting/mask01_0/1/2,
    // decoded directly) AND that same map's real info.bdf (also decoded
    // directly). First pass added a flat scalar "ambient floor" so no
    // channel went near-black — that fixed the brightness collapse but was
    // still wrong: it only ever scaled SunColor by a scalar, so all 3 masks
    // came out the SAME hue at different brightness. The real masks have
    // genuinely DIFFERENT hues (cool/blue-leaning R:G:B ratios that don't
    // match SunColor=(1.0,0.94,0.77), a warm color, at all) - confirming a
    // second, distinct light color is involved, not just a dimmer version of
    // the sun. InfoModel.ShadowColor for this same reference map decodes to
    // (0.64,0.77,0.92) - a cool blue, matching the real masks' hue direction
    // far better than SunColor does. This is exactly what a "shadow color"
    // field should mean: the ambient/sky-bounce tint a fully shadow-facing
    // surface picks up, as distinct from the sun's own direct color - so the
    // real per-basis bake should Lerp between ShadowColor and SunColor by
    // each basis direction's own clamped dot product, not scale one color by
    // a scalar. See RasterizeTriangleLighting.

    /// <summary>Runs the full bake for every RNM mask referenced by
    /// <paramref name="data"/>'s pairs. Returns a short human-readable status
    /// for the "Bake Lighting" button to show.</summary>
    public static string Bake(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session = null)
    {
        if (data == null) return "No active terrain to bake.";

        var model = BuildBakeGeometry(data, importedTerrains, out var pairIndexPerLayer);
        if (model.Layers.Count == 0) return "Nothing to bake - no triangles are assigned to any pair.";

        // The shadow horizon-march runs against a UNIFIED heightmap
        // covering both the core's own footprint and (when enabled) the
        // decorative background's full extent — see BuildShadowHfield's
        // own doc comment. This bake's OUTPUT still only ever writes into
        // data.Pairs (core); only what can OCCLUDE the sun is unified.
        var hfield = BuildShadowHfield(data, importedTerrains, out var hfieldOffset, out _, out _);

        var info = SC2TerrainExporter.BuildInfoModel(data, session);
        return BakeInternal(model, pairIndexPerLayer, data.Pairs, hfield, hfieldOffset, data, info, "RNM mask(s)");
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
    private static HfieldModel BuildShadowHfield(SC2TerrainData coreData, List<SC2ImportedTerrainExportData> importedTerrains,
        out Vector3 sharedOffset, out int mapX, out int mapY)
    {
        if (!coreData.BackgroundTerrainEnabled || coreData.BackgroundTerrainData == null)
        {
            sharedOffset = Vector3.zero;
            mapX = coreData.CostsGridSize + 1;
            mapY = coreData.CostsGridSize + 1;
            var coreOnly = BuildBakeGeometry(coreData, importedTerrains, out _);
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
        var coreModel = BuildBakeGeometry(coreData, importedTerrains, out _);
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
        HfieldModel hfield, Vector3 hfieldOffset, SC2TerrainData bakeSettingsSource, InfoModel info, string unitLabel)
    {
        var tangents = ComputeVertexTangents(model);

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
        var maxWaterDepth = info.MaxWaterDepth > 1f ? info.MaxWaterDepth : DefaultMaxWaterDepthForBlur;
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

        var bakedCount = 0;
        foreach (var kv in maskGroups)
        {
            var layerIndices = kv.Value;
            // All pairs sharing one mask name share one texture, so they must
            // share one resolution too - take the first as representative.
            var repPair = pairs[pairIndexPerLayer[layerIndices[0]]];
            var mw = repPair.MaskWidth;
            var mh = repPair.MaskHeight;
            var n = mw * mh;
            var rgb0 = new byte[n * 3];
            var rgb1 = new byte[n * 3];
            var rgb2 = new byte[n * 3];
            var shadowAlpha = new byte[n];
            var written = new bool[n];

            // Two-pass bake: pass 1 rasterizes the RAW per-basis NdotL (before
            // occlusion) and a hard 0/1 occlusion flag per texel; pass 2 box-
            // blurs the occlusion into a soft [0,1] penumbra before it's
            // applied to RGB/alpha. A single-pass hard cutoff (the original
            // implementation) produced a visibly hard 1-texel-wide shadow
            // edge in-game with no falloff, unlike real vanilla masks' smooth
            // shadow gradient - see FinalizeLighting/BoxBlurOcclusion below.
            var t0Raw = new float[n];
            var t1Raw = new float[n];
            var t2Raw = new float[n];
            var occlusionRaw = new float[n];
            var depthUnderwater = new float[n];

            foreach (var li in layerIndices)
            {
                var layer = model.Layers[li];
                var vertBase = layer.CumulativeVertices;
                var triStart = layer.CumulativeIndices / 3;
                var triEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeIndices / 3 : model.Triangles.Count;
                for (var t = triStart; t < triEnd; t++)
                {
                    var tri = model.Triangles[t];
                    var i0 = vertBase + (int)tri.V0;
                    var i1 = vertBase + (int)tri.V1;
                    var i2 = vertBase + (int)tri.V2;
                    var v0 = model.Vertices[i0];
                    var v1 = model.Vertices[i1];
                    var v2 = model.Vertices[i2];
                    RasterizeTriangleGeometry(v0, v1, v2, tangents[i0], tangents[i1], tangents[i2],
                        mw, mh, toSun, hfield, hfieldOffset, t0Raw, t1Raw, t2Raw, occlusionRaw, written,
                        waterEnabled, waterElevation, depthUnderwater);
                }
            }

            var occlusionSoft = BoxBlurOcclusion(occlusionRaw, written, mw, mh, shadowSoftRadius);
            // Underwater texels get a second, much wider blur pass blended in
            // by depth (0 at the waterline, full at/beyond maxWaterDepth) -
            // scattering widens the penumbra the deeper the point sits below
            // the surface, so a shallow-water shadow stays close to the dry-
            // land radius while a deep one approaches shadowSoftRadiusUnderwater.
            // Only pay for the second SAT pass when this map actually has
            // water at all.
            var occlusionFinal = occlusionSoft;
            if (waterEnabled)
            {
                var occlusionSoftUnderwater = BoxBlurOcclusion(occlusionRaw, written, mw, mh, shadowSoftRadiusUnderwater);
                occlusionFinal = new float[n];
                for (var idx = 0; idx < n; idx++)
                {
                    if (!written[idx]) continue;
                    var depthFactor = Mathf.Clamp01(depthUnderwater[idx] / maxWaterDepth);
                    occlusionFinal[idx] = Mathf.Lerp(occlusionSoft[idx], occlusionSoftUnderwater[idx], depthFactor);
                }
            }
            FinalizeLighting(t0Raw, t1Raw, t2Raw, occlusionFinal, written, sunColor, shadowColor, sunIntensity,
                rgb0, rgb1, rgb2, shadowAlpha);

            FillGapsRgb(rgb0, rgb1, rgb2, shadowAlpha, written, mw, mh);

            foreach (var li in layerIndices)
            {
                var pair = pairs[pairIndexPerLayer[li]];
                pair.RnmBakedRgb0 = rgb0;
                pair.RnmBakedRgb1 = rgb1;
                pair.RnmBakedRgb2 = rgb2;
                pair.RnmBakedShadowAlpha = shadowAlpha;
            }
            bakedCount++;
        }

        return $"Baked lighting for {bakedCount} {unitLabel}.";
    }

    /// <summary>Lightweight equivalent of SC2TerrainExporter.BuildExportArtifacts's
    /// model-building step (same layer-building + imported-terrain merge),
    /// without the texture registration/dialogs/BSP-rebuild that function
    /// also does - this bake only needs geometry (positions/normals/UV2),
    /// never textures or the final serialized BSP.</summary>
    private static TerrainModel BuildBakeGeometry(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, out List<int> pairIndexPerLayer)
    {
        var model = new TerrainModel { Version = 6 };
        pairIndexPerLayer = new List<int>();

        var build = SC2TerrainLayerBuilder.Build(data, sortByTriangleCount: true);
        for (var si = 0; si < build.Layers.Count; si++)
        {
            var builtLayer = build.Layers[si];
            var pair = data.Pairs[builtLayer.PairIndex];
            var s = builtLayer.Mesh;

            var layer = new BdfIO.Formats.Terrain.TerrainLayer { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
            model.Layers.Add(layer);
            pairIndexPerLayer.Add(builtLayer.PairIndex);

            // Uses the same GlobalUvScale-aware resolver as everywhere
            // else, though a uniform per-triangle scale factor cancels out
            // of the tangent DIRECTION ComputeVertexTangents derives from
            // it regardless (only the magnitude scales, and that gets
            // normalized away) — kept consistent anyway rather than
            // resolving a value nothing else in the app would agree with.
            var uv1Scale = data.EffectiveUvTilingScale(pair);
            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var pos = s.Vertices[vi]; var n = s.Normals[vi];
                model.Vertices.Add(new TerrainVertex
                {
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    Uv1U = pos.x / uv1Scale, Uv1V = pos.z / uv1Scale,
                    Uv2U = pos.x / 1024f, Uv2V = pos.z / 1024f,
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
            foreach (var imported in importedTerrains)
            {
                var before = model.Layers.Count;
                SC2TerrainExporter.MergeImportedTerrain(model, imported);
                for (var li = before; li < model.Layers.Count; li++)
                    pairIndexPerLayer.Add(-1); // no associated pair - shadow-casting geometry only
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
                model.Vertices.Add(new TerrainVertex
                {
                    PosX = localPos.x, PosY = localPos.y, PosZ = localPos.z,
                    Uv1U = localPos.x / uvScale, Uv1V = localPos.z / uvScale,
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
                model.Vertices.Add(new TerrainVertex
                {
                    PosX = localPos.x, PosY = localPos.y, PosZ = localPos.z,
                    Uv1U = localPos.x / uvScale, Uv1V = localPos.z / uvScale,
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

    /// <summary>Real per-vertex tangent, computed from each triangle's UV1
    /// derivative (Lengyel's standard tangent-space formula - the same thing
    /// Blender's "Tangent" node computes from a UV map), NOT a Gram-Schmidt
    /// approximation against a fixed world axis. Verified against 3 real
    /// vanilla masks across ~154k vertices from every SC2_MP_*/SC2_D1_* map:
    /// this real tangent correlates far better with actual mask brightness
    /// (0.45-0.50 per channel) than a world-X-Gram-Schmidt approximation did
    /// (0.10-0.30, inconsistent across channels). Accumulated unnormalized
    /// per-triangle (naturally weighted by the triangle's UV/area scale),
    /// normalized per-vertex at the end - same pattern as Unity's own
    /// per-vertex normal averaging.</summary>
    private static Vector3[] ComputeVertexTangents(TerrainModel model)
    {
        var tangents = new Vector3[model.Vertices.Count];
        for (var li = 0; li < model.Layers.Count; li++)
        {
            var layer = model.Layers[li];
            var vertBase = layer.CumulativeVertices;
            var triStart = layer.CumulativeIndices / 3;
            var triEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeIndices / 3 : model.Triangles.Count;
            for (var t = triStart; t < triEnd; t++)
            {
                var tri = model.Triangles[t];
                var i0 = vertBase + (int)tri.V0;
                var i1 = vertBase + (int)tri.V1;
                var i2 = vertBase + (int)tri.V2;
                if (i0 >= tangents.Length || i1 >= tangents.Length || i2 >= tangents.Length) continue;
                var a = model.Vertices[i0];
                var b = model.Vertices[i1];
                var c = model.Vertices[i2];

                var abx = b.PosX - a.PosX; var aby = b.PosY - a.PosY; var abz = b.PosZ - a.PosZ;
                var acx = c.PosX - a.PosX; var acy = c.PosY - a.PosY; var acz = c.PosZ - a.PosZ;

                var du1 = b.Uv1U - a.Uv1U; var dv1 = b.Uv1V - a.Uv1V;
                var du2 = c.Uv1U - a.Uv1U; var dv2 = c.Uv1V - a.Uv1V;
                var denom = du1 * dv2 - du2 * dv1;
                if (Mathf.Abs(denom) < 1e-8f) continue; // degenerate UV triangle

                var f = 1f / denom;
                var tangent = new Vector3(
                    f * (dv2 * abx - dv1 * acx),
                    f * (dv2 * aby - dv1 * acy),
                    f * (dv2 * abz - dv1 * acz));

                tangents[i0] += tangent;
                tangents[i1] += tangent;
                tangents[i2] += tangent;
            }
        }
        for (var i = 0; i < tangents.Length; i++)
            if (tangents[i].sqrMagnitude > 1e-12f)
                tangents[i] = tangents[i].normalized;
        return tangents;
    }

    private static void RasterizeTriangleGeometry(
        TerrainVertex v0, TerrainVertex v1, TerrainVertex v2,
        Vector3 tan0, Vector3 tan1, Vector3 tan2,
        int mw, int mh, Vector3 toSun, HfieldModel hfield, Vector3 hfieldOffset,
        float[] t0Raw, float[] t1Raw, float[] t2Raw, float[] occlusionRaw, bool[] written,
        bool waterEnabled, float waterElevation, float[] depthUnderwater)
    {
        // Rasterize in TEXEL space (UV2 * mask dims), not world XZ - mask
        // resolution (e.g. 2048x2048) is independent of the hfield's own
        // fixed 1025x1025 grid.
        var p0 = new Vector2(v0.Uv2U * mw, v0.Uv2V * mh);
        var p1 = new Vector2(v1.Uv2U * mw, v1.Uv2V * mh);
        var p2 = new Vector2(v2.Uv2U * mw, v2.Uv2V * mh);

        var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))));
        var maxX = Mathf.Min(mw - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))));
        var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))));
        var maxY = Mathf.Min(mh - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))));
        if (minX > maxX || minY > maxY) return;

        var denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
        if (Mathf.Abs(denom) < 1e-8f) return; // degenerate triangle in UV2 space

        var n0 = new Vector3(v0.NormalX, v0.NormalY, v0.NormalZ);
        var n1 = new Vector3(v1.NormalX, v1.NormalY, v1.NormalZ);
        var n2 = new Vector3(v2.NormalX, v2.NormalY, v2.NormalZ);

        for (var ty = minY; ty <= maxY; ty++)
        for (var tx = minX; tx <= maxX; tx++)
        {
            var w0 = ((p1.y - p2.y) * (tx - p2.x) + (p2.x - p1.x) * (ty - p2.y)) / denom;
            var w1 = ((p2.y - p0.y) * (tx - p2.x) + (p0.x - p2.x) * (ty - p2.y)) / denom;
            var w2 = 1f - w0 - w1;
            const float eps = 1e-4f;
            if (w0 < -eps || w1 < -eps || w2 < -eps) continue;

            var worldX = w0 * v0.PosX + w1 * v1.PosX + w2 * v2.PosX;
            var worldY = w0 * v0.PosY + w1 * v1.PosY + w2 * v2.PosY;
            var worldZ = w0 * v0.PosZ + w1 * v1.PosZ + w2 * v2.PosZ;
            var normal = (w0 * n0 + w1 * n1 + w2 * n2).normalized;

            var idx = ty * mw + tx;

            // Per-texel TBN using the REAL per-vertex tangent (computed from
            // each triangle's UV1 derivative - Lengyel's standard formula,
            // matching what Blender's own "Tangent" node computes from a UV
            // map - not a Gram-Schmidt approximation against a fixed world
            // axis). Verified directly against 3 real vanilla masks across
            // ~154k vertices from every SC2_MP_*/SC2_D1_* map: this real
            // tangent + B=Cross(N,T) correlates 0.45-0.50 per channel with
            // actual mask brightness, clearly better than the previous
            // world-X-Gram-Schmidt approximation (0.10-0.30, inconsistent).
            // Barycentric interpolation of 3 already-unit tangents isn't
            // guaranteed to stay exactly perpendicular to the interpolated
            // normal, so re-orthogonalize (Gram-Schmidt) before use.
            var t = (w0 * tan0 + w1 * tan1 + w2 * tan2);
            t -= normal * Vector3.Dot(t, normal);
            if (t.sqrMagnitude < 1e-8f) t = Vector3.Cross(Vector3.forward, normal); // degenerate (no reliable UV tangent here) - fallback reference axis
            t.Normalize();
            var b = Vector3.Cross(normal, t);

            var w0dir = t * Basis0.x + b * Basis0.y + normal * Basis0.z;
            var w1dir = t * Basis1.x + b * Basis1.y + normal * Basis1.z;
            var w2dir = t * Basis2.x + b * Basis2.y + normal * Basis2.z;

            // Each basis channel is a Lerp between ShadowColor (what a fully
            // shadow-facing surface picks up - ambient/sky-bounce tint) and
            // SunColor (what a fully sun-facing surface picks up), weighted
            // by that basis direction's own clamped dot product against the
            // sun. This is what actually produces per-channel HUE variation
            // matching real vanilla masks - a flat scalar dimming of one
            // shared color (the previous approach) can only ever change
            // brightness, never hue, which real reference masks clearly do
            // (confirmed by decoding 3 real masks directly: their R:G:B
            // ratios differ from each other and don't match SunColor's own
            // ratio, but do lean toward ShadowColor's cooler tone).
            // Terrain self-shadowing (horizon-blocked by distant geometry, as
            // opposed to a texel's own local slope facing away from the sun)
            // must darken RGB directly, not just the alpha channel - confirmed
            // by Resources/SC2Terrain.shader's own runtime math: mask0.alpha
            // ("sunOcclusion") only ever gates DYNAMIC (unit/building) shadow
            // blending, never applies on its own when there's no dynamic
            // shadow caster. The shader's own comment on this is explicit:
            // baked-shadowed areas must already "include bounce light" in the
            // RNM RGB values themselves. Without this, a valley blocked by a
            // neighboring ridge would bake bright (local slope faces the sun)
            // even though it's actually in permanent shadow.
            //
            // Raw (unoccluded) NdotL per basis and a hard 0/1 occlusion flag
            // are stashed here, NOT combined yet - BoxBlurOcclusion softens
            // the occlusion signal across neighbouring texels first (see
            // Bake()), then FinalizeLighting applies it. Combining them here
            // directly would bake a hard, 1-texel-wide shadow edge with no
            // penumbra, which is exactly the "shadows come out too hard
            // in-game" symptom this two-pass split fixes.
            // hfieldOffset is nonzero exactly when this triangle's own
            // world-space frame doesn't already match hfield's (the
            // unified core+background heightmap is expressed in
            // background-local space — see BuildShadowHfield) — subtracted
            // here only for the hfield lookup, never affecting worldX/Y/Z
            // themselves (still used below for the water-depth calc, which
            // needs the real absolute world Y regardless of XZ frame).
            var occluded = IsShadowed(worldX - hfieldOffset.x, worldY - hfieldOffset.y, worldZ - hfieldOffset.z, toSun, hfield);

            t0Raw[idx] = Mathf.Clamp01(Vector3.Dot(w0dir, toSun));
            t1Raw[idx] = Mathf.Clamp01(Vector3.Dot(w1dir, toSun));
            t2Raw[idx] = Mathf.Clamp01(Vector3.Dot(w2dir, toSun));
            occlusionRaw[idx] = occluded ? 1f : 0f;
            depthUnderwater[idx] = waterEnabled ? Mathf.Max(0f, waterElevation - worldY) : 0f;
            written[idx] = true;
        }
    }

    // Texel radius for the box blur that turns IsShadowed's hard 0/1 result
    // into a soft penumbra - wider values give a softer/wider shadow edge.
    // User-configurable — see BakeInternal's own shadowSoftRadius local and
    // SC2TerrainData.RnmBakeShadowSoftRadius's doc comment.

    // Underwater shadow softening (see Bake()'s own comment for the
    // physical reasoning) — a fallback depth (world units) for maps whose
    // info.bdf has no real MaxWaterDepth set (<=1, effectively "never
    // configured") to blend against instead of dividing by a near-zero
    // depth. Not corpus-derived - a reasonable default pending real in-game
    // calibration, same as ShadowMaxDistance/ShadowStepSize above.
    private const float DefaultMaxWaterDepthForBlur = 64f;

    /// <summary>Averages occlusionRaw (0/1) over a (2*radius+1) square of
    /// neighbouring WRITTEN texels via a summed-area table, giving an O(1)-
    /// per-texel box blur regardless of radius. Unwritten texels (map edges/
    /// UV gaps) are excluded from both the sum and the neighbour count, so
    /// they don't pull the blur toward "lit" at real geometry boundaries.</summary>
    private static float[] BoxBlurOcclusion(float[] raw, bool[] written, int w, int h, int radius)
    {
        var W = w + 1;
        var sumVal = new double[W * (h + 1)];
        var sumCnt = new double[W * (h + 1)];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * w + x;
            var v = written[idx] ? raw[idx] : 0.0;
            var c = written[idx] ? 1.0 : 0.0;
            var sIdx = (y + 1) * W + (x + 1);
            sumVal[sIdx] = v + sumVal[sIdx - 1] + sumVal[sIdx - W] - sumVal[sIdx - W - 1];
            sumCnt[sIdx] = c + sumCnt[sIdx - 1] + sumCnt[sIdx - W] - sumCnt[sIdx - W - 1];
        }

        double Query(double[] sat, int x0, int y0, int x1, int y1) =>
            sat[(y1 + 1) * W + (x1 + 1)] - sat[y0 * W + (x1 + 1)] - sat[(y1 + 1) * W + x0] + sat[y0 * W + x0];

        var result = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * w + x;
            if (!written[idx]) continue;
            var x0 = Mathf.Max(0, x - radius);
            var x1 = Mathf.Min(w - 1, x + radius);
            var y0 = Mathf.Max(0, y - radius);
            var y1 = Mathf.Min(h - 1, y + radius);
            var cnt = Query(sumCnt, x0, y0, x1, y1);
            result[idx] = cnt > 0 ? (float)(Query(sumVal, x0, y0, x1, y1) / cnt) : 0f;
        }
        return result;
    }

    /// <summary>Combines the raw per-basis NdotL with the softened occlusion
    /// penumbra into final RGB/alpha bytes. Occlusion pulls each basis'
    /// NdotL toward 0 (full ShadowColor) proportionally to how shadowed the
    /// blurred signal says this texel is - a soft 0..1 value at a penumbra
    /// edge, not a hard cutoff.</summary>
    private static void FinalizeLighting(
        float[] t0Raw, float[] t1Raw, float[] t2Raw, float[] occlusionSoft, bool[] written,
        Color sunColor, Color shadowColor, float sunIntensity,
        byte[] rgb0, byte[] rgb1, byte[] rgb2, byte[] shadowAlpha)
    {
        for (var idx = 0; idx < written.Length; idx++)
        {
            if (!written[idx]) continue;
            var occ = occlusionSoft[idx];
            var t0 = t0Raw[idx] * (1f - occ);
            var t1 = t1Raw[idx] * (1f - occ);
            var t2 = t2Raw[idx] * (1f - occ);

            // Each slice must be pre-divided by the basis weight sum, because
            // the game ADDS all three back together without normalizing:
            //
            //   dp.i = saturate(dot(tsnormal, BASIS_i));
            //   col  = dp.x*c0 + dp.y*c1 + dp.z*c2;
            //   //col /= sum;      <-- commented out in terrain.hlsl
            //
            // For an unperturbed tangent-space normal (0,0,1) every dp equals
            // the bases' shared z component 0.5774, so the three slices are
            // summed with a total weight of 3*0.5774 = sqrt(3) = 1.732.
            // Writing the full lighting value into each slice therefore
            // reconstructs 1.732x too much light: measured on the export, the
            // slices averaged 176/255 = 0.686, reconstructing to 1.19 — past
            // full white, so bright areas clipped and specular rode on top of
            // an already-saturated base. Real vanilla masks sit exactly where
            // this division puts us: 176/1.732 = 102, against 108 (SC2_MP_302)
            // and 55-86 (SC2_MP_001).
            var c0 = Color.Lerp(shadowColor, sunColor, t0) * (sunIntensity * BasisWeightNormalization);
            var c1 = Color.Lerp(shadowColor, sunColor, t1) * (sunIntensity * BasisWeightNormalization);
            var c2 = Color.Lerp(shadowColor, sunColor, t2) * (sunIntensity * BasisWeightNormalization);

            WriteRgb(rgb0, idx, c0.r, c0.g, c0.b);
            WriteRgb(rgb1, idx, c1.r, c1.g, c1.b);
            WriteRgb(rgb2, idx, c2.r, c2.g, c2.b);

            shadowAlpha[idx] = (byte)Mathf.RoundToInt(Mathf.Lerp(LitAlpha, ShadowedAlpha, occ));
        }
    }

    private static void WriteRgb(byte[] rgb, int idx, float r, float g, float bch)
    {
        rgb[idx * 3] = (byte)Mathf.RoundToInt(Mathf.Clamp01(r) * 255f);
        rgb[idx * 3 + 1] = (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f);
        rgb[idx * 3 + 2] = (byte)Mathf.RoundToInt(Mathf.Clamp01(bch) * 255f);
    }

    /// <summary>Heightmap horizon-marching self-shadow test - marches from
    /// (worldX,worldY,worldZ) toward the sun (toSun) in fixed XZ steps,
    /// comparing the ray's rising height against the sampled hfield heightmap
    /// at each step. Standard, efficient technique for heightfield
    /// self-shadowing - avoids needing a queryable 3D mesh/BSP raycaster
    /// (TerrainBspBuilder's BSP is a write-only serialization target for the
    /// game engine, not exposed for C# queries).</summary>
    private static bool IsShadowed(float worldX, float worldY, float worldZ, Vector3 toSun, HfieldModel hfield)
    {
        var horizontal = new Vector2(toSun.x, toSun.z);
        var horizontalLen = horizontal.magnitude;
        if (toSun.y <= 0f) return true; // sun at/below the horizon from this texel - always shadowed
        if (horizontalLen < 1e-5f) return false; // sun directly overhead - nothing can occlude it
        horizontal /= horizontalLen;
        var slope = toSun.y / horizontalLen; // rise per unit horizontal distance

        var steps = Mathf.CeilToInt(ShadowMaxDistance / ShadowStepSize);
        for (var i = 1; i <= steps; i++)
        {
            var d = i * ShadowStepSize;
            var sampleX = worldX + horizontal.x * d;
            var sampleZ = worldZ + horizontal.y * d;
            if (sampleX < 0f || sampleX >= hfield.MapX - 1 || sampleZ < 0f || sampleZ >= hfield.MapY - 1)
                return false; // marched off the map edge - nothing left to occlude it
            var rayHeight = worldY + d * slope;
            var terrainHeight = SampleHeightmapBilinear(hfield, sampleX, sampleZ);
            if (terrainHeight > rayHeight) return true;
        }
        return false;
    }

    private static float SampleHeightmapBilinear(HfieldModel hfield, float x, float z)
    {
        var x0 = Mathf.FloorToInt(x);
        var z0 = Mathf.FloorToInt(z);
        var x1 = Mathf.Min(x0 + 1, (int)hfield.MapX - 1);
        var z1 = Mathf.Min(z0 + 1, (int)hfield.MapY - 1);
        var fx = x - x0;
        var fz = z - z0;
        float H(int gx, int gz) => hfield.Heightmap[gz * (int)hfield.MapX + gx] / HeightRawScale;
        var h00 = H(x0, z0);
        var h10 = H(x1, z0);
        var h01 = H(x0, z1);
        var h11 = H(x1, z1);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
    }

    /// <summary>Mirrors SC2HfieldProjectionService.FillGaps, extended to 3 RGB
    /// arrays + shadow alpha at once - texels no triangle covers (mask
    /// resolution finer than geometry, or map edges) get a nearest-neighbour
    /// average instead of staying at zero/black.</summary>
    private static void FillGapsRgb(byte[] rgb0, byte[] rgb1, byte[] rgb2, byte[] shadowAlpha, bool[] written, int w, int h)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            var anyFilled = false;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (written[i]) continue;
                float r0 = 0, g0 = 0, b0 = 0, r1 = 0, g1 = 0, b1 = 0, r2 = 0, g2 = 0, b2 = 0, a = 0;
                var n = 0;
                foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    var j = ny * w + nx;
                    if (!written[j]) continue;
                    r0 += rgb0[j * 3]; g0 += rgb0[j * 3 + 1]; b0 += rgb0[j * 3 + 2];
                    r1 += rgb1[j * 3]; g1 += rgb1[j * 3 + 1]; b1 += rgb1[j * 3 + 2];
                    r2 += rgb2[j * 3]; g2 += rgb2[j * 3 + 1]; b2 += rgb2[j * 3 + 2];
                    a += shadowAlpha[j];
                    n++;
                }
                if (n > 0)
                {
                    rgb0[i * 3] = (byte)(r0 / n); rgb0[i * 3 + 1] = (byte)(g0 / n); rgb0[i * 3 + 2] = (byte)(b0 / n);
                    rgb1[i * 3] = (byte)(r1 / n); rgb1[i * 3 + 1] = (byte)(g1 / n); rgb1[i * 3 + 2] = (byte)(b1 / n);
                    rgb2[i * 3] = (byte)(r2 / n); rgb2[i * 3 + 1] = (byte)(g2 / n); rgb2[i * 3 + 2] = (byte)(b2 / n);
                    shadowAlpha[i] = (byte)(a / n);
                    written[i] = true;
                    anyFilled = true;
                }
            }
            if (!anyFilled) break;
        }
    }
}
