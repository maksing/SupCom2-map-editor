using System;
using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;

/// <summary>
/// The per-texel RNM lighting rasteriser behind SC2RnmBakeService: bakes the
/// three basis-direction lighting slices plus the sun-occlusion alpha for a
/// group of model layers into one mask, by each vertex's UV2. Pure geometry/
/// lighting math (no SC2TerrainData) so the same code bakes the terrain's
/// pair masks, the background's, and the masks of imported terrain.bdf layers
/// with their own UV2 - and can be unit-tested on a synthetic model.
/// </summary>
public static class SC2RnmMaskBaker
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
    public const float ShadowMaxDistance = 512f;
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


    /// <summary>Everything a mask bake needs besides the geometry - resolved
    /// once per Bake from the Lighting Bake inputs / info.bdf.</summary>
    public sealed class Settings
    {
        public Vector3 ToSun;
        public HfieldModel Hfield;
        /// <summary>Subtracted from a texel's world position before the
        /// heightmap lookup (see SC2RnmBakeService.BuildShadowHfield).</summary>
        public Vector3 HfieldOffset;
        /// <summary>Optional: the heightmap that shades the layer with this index, for a bake where what shadows a layer
        /// depends on which object it belongs to (SC2ShadowCasting). Null, or a null result, = <see cref="Hfield"/>. It has to
        /// share <see cref="HfieldOffset"/>'s frame.</summary>
        public Func<int, HfieldModel> HfieldForLayer;
        /// <summary>World units one texel of the reference mask covers (the core's), or 0 for none. When set, the shadow's
        /// penumbra keeps the width in world units that <see cref="ShadowSoftRadius"/> gives on that mask: a texel that covers
        /// more ground (the floor of an imported object, packed coarsely into its own UV2) is blurred over fewer texels, one that
        /// covers less over more. At 0 every texel is blurred by <see cref="ShadowSoftRadius"/> texels, whatever it covers.</summary>
        public float ReferenceTexelWorld;
        /// <summary>The march's view of each heightmap this bake meets, made once per heightmap however many masks use it.</summary>
        internal readonly Dictionary<HfieldModel, ShadowGrid> Grids = new Dictionary<HfieldModel, ShadowGrid>();
        public bool WaterEnabled;
        public float WaterElevation;
        public float MaxWaterDepth = DefaultMaxWaterDepthForBlur;
        public int ShadowSoftRadius;
        public int ShadowSoftRadiusUnderwater;
        public Color SunColor = Color.white;
        public Color ShadowColor = new Color(0.64f, 0.77f, 0.92f, 1f);
        public float SunIntensity = 1f;
    }

    public sealed class MaskResult
    {
        public int Width, Height;
        public byte[] Rgb0, Rgb1, Rgb2, ShadowAlpha;
        /// <summary>Slice-1 blend alpha (0 where no sampler was given).</summary>
        public byte[] Blend;
        /// <summary>Texels a triangle actually covered, before the gap fill.</summary>
        public bool[] Covered;
    }

    /// <summary>Bakes every triangle of <paramref name="layerIndices"/> into one
    /// mask of <paramref name="mw"/> x <paramref name="mh"/> texels, rasterised
    /// in UV2 texel space. All layers that share a mask must be passed in ONE
    /// call so they end up in the same arrays.
    ///
    /// Two-pass bake: pass 1 rasterizes the RAW per-basis NdotL (before
    /// occlusion) and a hard 0/1 occlusion flag per texel; pass 2 box-blurs the
    /// occlusion into a soft [0,1] penumbra before it's applied to RGB/alpha. A
    /// single-pass hard cutoff produced a visibly hard 1-texel-wide shadow edge
    /// in-game with no falloff, unlike real vanilla masks' smooth shadow
    /// gradient - see FinalizeLighting/BoxBlurOcclusion.</summary>
    /// <param name="blendSamplerForLayer">Optional: for a layer index, a
    /// (u, v) to blend-alpha sampler (or null) - see RasterizeTriangleGeometry.</param>
    public static MaskResult BakeMaskGroup(TerrainModel model, Vector3[] tangents, IReadOnlyList<int> layerIndices,
        int mw, int mh, Settings settings, Func<int, Func<float, float, byte>> blendSamplerForLayer = null)
    {
        var n = mw * mh;
        var rgb0 = new byte[n * 3];
        var rgb1 = new byte[n * 3];
        var rgb2 = new byte[n * 3];
        var shadowAlpha = new byte[n];
        var blend = new byte[n];
        var written = new bool[n];

        var t0Raw = new float[n];
        var t1Raw = new float[n];
        var t2Raw = new float[n];
        var occlusionRaw = new float[n];
        var depthUnderwater = new float[n];
        // Where each covered texel sits in the world and which heightmap shades it; the sun test itself runs afterwards, in parallel.
        var posX = new float[n];
        var posY = new float[n];
        var posZ = new float[n];
        var gridOfTexel = new byte[n];
        var texelWorld = new float[n]; // world units across one texel, where a triangle covers it
        var grids = new List<ShadowGrid>();

        foreach (var li in layerIndices)
        {
            var layer = model.Layers[li];
            var vertBase = layer.CumulativeVertices;
            var triStart = layer.CumulativeIndices / 3;
            var triEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeIndices / 3 : model.Triangles.Count;
            var sampler = blendSamplerForLayer?.Invoke(li);
            var layerGrid = (byte)GridIndex(settings, grids, settings.HfieldForLayer?.Invoke(li) ?? settings.Hfield);
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
                    mw, mh, settings.ToSun, t0Raw, t1Raw, t2Raw, posX, posY, posZ, gridOfTexel, layerGrid, texelWorld, written,
                    settings.WaterEnabled, settings.WaterElevation, depthUnderwater, blend, sampler);
            }
        }

        // Every texel's sun test is independent of the others, so the rows are shared out over all cores.
        var toSun = settings.ToSun;
        var offset = settings.HfieldOffset;
        System.Threading.Tasks.Parallel.For(0, mh, row =>
        {
            for (var idx = row * mw; idx < (row + 1) * mw; idx++)
            {
                if (!written[idx]) continue;
                var grid = grids[gridOfTexel[idx]];
                occlusionRaw[idx] = grid != null && IsShadowed(posX[idx] - offset.x, posY[idx] - offset.y, posZ[idx] - offset.z, toSun, grid) ? 1f : 0f;
            }
        });

        var reference = settings.ReferenceTexelWorld;
        var densityAware = reference > 0f;
        var occlusionSoft = BoxBlurOcclusion(occlusionRaw, written, mw, mh, settings.ShadowSoftRadius,
            densityAware ? (Func<int, float>)(idx => ScaleRadius(settings.ShadowSoftRadius, reference, texelWorld[idx])) : null);
        // Underwater texels get a second, much wider blur pass blended in
        // by depth (0 at the waterline, full at/beyond maxWaterDepth) -
        // scattering widens the penumbra the deeper the point sits below
        // the surface, so a shallow-water shadow stays close to the dry-
        // land radius while a deep one approaches the underwater radius.
        // Only pay for the second SAT pass when this map actually has
        // water at all.
        var occlusionFinal = occlusionSoft;
        if (settings.WaterEnabled)
        {
            var occlusionSoftUnderwater = BoxBlurOcclusion(occlusionRaw, written, mw, mh, settings.ShadowSoftRadiusUnderwater,
                densityAware ? (Func<int, float>)(idx => ScaleRadius(settings.ShadowSoftRadiusUnderwater, reference, texelWorld[idx])) : null);
            occlusionFinal = new float[n];
            for (var idx = 0; idx < n; idx++)
            {
                if (!written[idx]) continue;
                var depthFactor = Mathf.Clamp01(depthUnderwater[idx] / settings.MaxWaterDepth);
                occlusionFinal[idx] = Mathf.Lerp(occlusionSoft[idx], occlusionSoftUnderwater[idx], depthFactor);
            }
        }
        FinalizeLighting(t0Raw, t1Raw, t2Raw, occlusionFinal, written, settings.SunColor, settings.ShadowColor, settings.SunIntensity,
            rgb0, rgb1, rgb2, shadowAlpha);

        var covered = (bool[])written.Clone();
        FillGapsRgb(rgb0, rgb1, rgb2, shadowAlpha, written, mw, mh);

        return new MaskResult
        {
            Width = mw, Height = mh, Rgb0 = rgb0, Rgb1 = rgb1, Rgb2 = rgb2, ShadowAlpha = shadowAlpha, Blend = blend, Covered = covered,
        };
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
    public static Vector3[] ComputeVertexTangents(TerrainModel model)
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

    /// <param name="blendOut">Optional, with <paramref name="blendSampler"/>:
    /// receives, for every texel this triangle covers, the value the sampler
    /// returns for that texel's UV2 coordinate - used to carry an existing
    /// mask's slice-1 blend alpha over to a freshly baked mask.</param>
    private static void RasterizeTriangleGeometry(
        TerrainVertex v0, TerrainVertex v1, TerrainVertex v2,
        Vector3 tan0, Vector3 tan1, Vector3 tan2,
        int mw, int mh, Vector3 toSun,
        float[] t0Raw, float[] t1Raw, float[] t2Raw, float[] posX, float[] posY, float[] posZ, byte[] gridOfTexel, byte layerGrid,
        float[] texelWorld, bool[] written,
        bool waterEnabled, float waterElevation, float[] depthUnderwater,
        byte[] blendOut = null, Func<float, float, byte> blendSampler = null)
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

        // World units across one texel of this triangle: the square root of its area on the ground over its area on the mask.
        var edge1 = new Vector3(v1.PosX - v0.PosX, v1.PosY - v0.PosY, v1.PosZ - v0.PosZ);
        var edge2 = new Vector3(v2.PosX - v0.PosX, v2.PosY - v0.PosY, v2.PosZ - v0.PosZ);
        var worldPerTexel = Mathf.Sqrt(Vector3.Cross(edge1, edge2).magnitude / Mathf.Abs(denom));

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
            // The occlusion itself is tested after the whole mask is rasterised (BakeMaskGroup), from the position
            // kept here. The heightmap's frame offset (nonzero exactly when this triangle's own world-space frame
            // doesn't already match the heightmap's - see BuildShadowCasters) is subtracted there, only for the
            // lookup, so these positions stay absolute (the water-depth calc below needs the real world Y).
            posX[idx] = worldX; posY[idx] = worldY; posZ[idx] = worldZ;
            gridOfTexel[idx] = layerGrid;
            texelWorld[idx] = worldPerTexel;

            t0Raw[idx] = Mathf.Clamp01(Vector3.Dot(w0dir, toSun));
            t1Raw[idx] = Mathf.Clamp01(Vector3.Dot(w1dir, toSun));
            t2Raw[idx] = Mathf.Clamp01(Vector3.Dot(w2dir, toSun));
            depthUnderwater[idx] = waterEnabled ? Mathf.Max(0f, waterElevation - worldY) : 0f;
            if (blendOut != null && blendSampler != null)
                blendOut[idx] = blendSampler((tx + 0.5f) / mw, (ty + 0.5f) / mh);
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
    public const float DefaultMaxWaterDepthForBlur = 64f;

    /// <summary>Averages occlusionRaw (0/1) over a (2*radius+1) square of
    /// neighbouring WRITTEN texels via a summed-area table, giving an O(1)-
    /// per-texel box blur regardless of radius. Unwritten texels (map edges/
    /// UV gaps) are excluded from both the sum and the neighbour count, so
    /// they don't pull the blur toward "lit" at real geometry boundaries.</summary>
    internal static float[] BoxBlurOcclusion(float[] raw, bool[] written, int w, int h, int radius, Func<int, float> radiusAt = null)
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

        float Blur(int x, int y, int r)
        {
            var x0 = Mathf.Max(0, x - r);
            var x1 = Mathf.Min(w - 1, x + r);
            var y0 = Mathf.Max(0, y - r);
            var y1 = Mathf.Min(h - 1, y + r);
            var cnt = Query(sumCnt, x0, y0, x1, y1);
            return cnt > 0 ? (float)(Query(sumVal, x0, y0, x1, y1) / cnt) : 0f;
        }

        var result = new float[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * w + x;
            if (!written[idx]) continue;
            // A radius that falls between two whole texels blends the blur of the one below with the one above, so the
            // penumbra width follows the setting smoothly instead of in steps of a whole texel.
            var r = radiusAt != null ? radiusAt(idx) : radius;
            var whole = (int)r;
            var fraction = r - whole;
            var blur = Blur(x, y, whole);
            result[idx] = fraction > 1e-4f ? Mathf.Lerp(blur, Blur(x, y, whole + 1), fraction) : blur;
        }
        return result;
    }

    /// <summary>The blur radius, in texels (fractional), that gives a texel covering <paramref name="texelWorld"/> world units
    /// the same penumbra width in world units as <paramref name="radius"/> texels give on a mask whose texel covers
    /// <paramref name="referenceTexelWorld"/>. A texel no triangle measured (0) keeps <paramref name="radius"/>; the result is
    /// held between 0 and four times <paramref name="radius"/>.</summary>
    internal static float ScaleRadius(int radius, float referenceTexelWorld, float texelWorld)
    {
        if (radius <= 0 || referenceTexelWorld <= 0f || texelWorld <= 0f) return radius;
        return Mathf.Clamp(radius * referenceTexelWorld / texelWorld, 0f, radius * 4f);
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
    internal static bool IsShadowed(float worldX, float worldY, float worldZ, Vector3 toSun, ShadowGrid grid)
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
            if (sampleX < 0f || sampleX >= grid.MapX - 1 || sampleZ < 0f || sampleZ >= grid.MapY - 1)
                return false; // marched off the map edge - nothing left to occlude it
            var rayHeight = worldY + d * slope;

            // The ray only climbs, so once it is above everything in a block of the map it stays above all of it, and the
            // steps that land in that block need no sampling. Same answer as testing every step, only faster.
            var skipTo = grid.StepsSafeToSkip(sampleX, sampleZ, rayHeight, d, horizontal.x, horizontal.y, ShadowStepSize);
            if (skipTo > i) { i = skipTo - 1; continue; }

            var terrainHeight = grid.SampleBilinear(sampleX, sampleZ);
            if (terrainHeight > rayHeight) return true;
        }
        return false;
    }

    /// <summary>Which of the <see cref="ShadowGrid"/> list belongs to a heightmap (added to it on first use); null = none.</summary>
    private static int GridIndex(Settings settings, List<ShadowGrid> grids, HfieldModel hfield)
    {
        if (hfield == null)
        {
            var none = grids.IndexOf(null);
            if (none >= 0) return none;
            grids.Add(null);
            return grids.Count - 1;
        }
        if (!settings.Grids.TryGetValue(hfield, out var grid)) settings.Grids[hfield] = grid = new ShadowGrid(hfield);
        var index = grids.IndexOf(grid);
        if (index >= 0) return index;
        if (grids.Count >= byte.MaxValue) throw new InvalidOperationException("Too many different shadow heightmaps in one mask.");
        grids.Add(grid);
        return grids.Count - 1;
    }

    /// <summary>A heightmap for the horizon march, with the highest value of every 16x16 and 64x64 block of cells next to it so
    /// the march can leap over stretches that cannot hold a hit.</summary>
    internal sealed class ShadowGrid
    {
        private const int Fine = 16, Coarse = 64;
        // Added to a block's maximum before it is compared with the ray: the bilinear sample is mathematically never above the
        // highest of its four cells, but the float arithmetic can land a hair over.
        private const float Margin = 1e-3f;

        public readonly int MapX, MapY;
        private readonly ushort[] _heights;
        private readonly float[] _fineMax, _coarseMax;
        private readonly int _fineW, _coarseW;

        public ShadowGrid(HfieldModel hfield)
        {
            MapX = (int)hfield.MapX; MapY = (int)hfield.MapY;
            _heights = hfield.Heightmap;
            _fineW = (MapX + Fine - 1) / Fine;
            _coarseW = (MapX + Coarse - 1) / Coarse;
            _fineMax = BlockMax(Fine, _fineW);
            _coarseMax = BlockMax(Coarse, _coarseW);
        }

        // A block of cells [b*size, b*size + size] on each axis (one cell more than its width, which is the last cell a
        // bilinear sample that starts inside the block reads).
        private float[] BlockMax(int size, int blocksX)
        {
            var blocksY = (MapY + size - 1) / size;
            var max = new ushort[blocksX * blocksY];
            for (var z = 0; z < MapY; z++)
            {
                var rowBlock = z / size;
                var rowBlockBelow = z % size == 0 && z > 0 ? rowBlock - 1 : -1; // the block above also reads this row as its last
                var rowOffset = z * MapX;
                for (var x = 0; x < MapX; x++)
                {
                    var h = _heights[rowOffset + x];
                    var colBlock = x / size;
                    var colBlockLeft = x % size == 0 && x > 0 ? colBlock - 1 : -1;
                    Raise(max, rowBlock * blocksX + colBlock, h);
                    if (colBlockLeft >= 0) Raise(max, rowBlock * blocksX + colBlockLeft, h);
                    if (rowBlockBelow >= 0)
                    {
                        Raise(max, rowBlockBelow * blocksX + colBlock, h);
                        if (colBlockLeft >= 0) Raise(max, rowBlockBelow * blocksX + colBlockLeft, h);
                    }
                }
            }
            var result = new float[max.Length];
            for (var i = 0; i < max.Length; i++) result[i] = max[i] / HeightRawScale;
            return result;
        }

        private static void Raise(ushort[] max, int index, ushort value)
        {
            if (value > max[index]) max[index] = value;
        }

        /// <summary>The step to continue the march at when the ray, at step distance <paramref name="d"/> and height
        /// <paramref name="rayHeight"/>, is above everything in the block it is in, so that every step up to there is a miss;
        /// 0 when it is not above a block. Steps are <paramref name="stepSize"/> apart along the unit direction (dirX, dirZ).</summary>
        public int StepsSafeToSkip(float x, float z, float rayHeight, float d, float dirX, float dirZ, float stepSize)
        {
            var coarse = ExitStep(_coarseMax, _coarseW, Coarse, x, z, rayHeight, d, dirX, dirZ, stepSize);
            if (coarse > 0) return coarse;
            return ExitStep(_fineMax, _fineW, Fine, x, z, rayHeight, d, dirX, dirZ, stepSize);
        }

        private static int ExitStep(float[] blockMax, int blocksX, int size, float x, float z, float rayHeight, float d,
            float dirX, float dirZ, float stepSize)
        {
            var bx = (int)x / size; var bz = (int)z / size;
            if (rayHeight <= blockMax[bz * blocksX + bx] + Margin) return 0;

            // The ray leaves the block (as sampled: the cell index of a sample is floor(position)) at the first distance where
            // an axis crosses the block's edge.
            var exit = float.MaxValue;
            if (dirX > 1e-6f) exit = Mathf.Min(exit, d + ((bx + 1) * size - x) / dirX);
            else if (dirX < -1e-6f) exit = Mathf.Min(exit, d + (bx * size - x) / dirX);
            if (dirZ > 1e-6f) exit = Mathf.Min(exit, d + ((bz + 1) * size - z) / dirZ);
            else if (dirZ < -1e-6f) exit = Mathf.Min(exit, d + (bz * size - z) / dirZ);
            if (exit >= float.MaxValue) return 0;

            // Every step before this one lies inside the block; the small deduction keeps float rounding from letting the
            // last of them stray over the edge.
            return Mathf.FloorToInt((exit - 0.01f) / stepSize);
        }

        public float SampleBilinear(float x, float z)
        {
            var x0 = Mathf.FloorToInt(x);
            var z0 = Mathf.FloorToInt(z);
            var x1 = Mathf.Min(x0 + 1, MapX - 1);
            var z1 = Mathf.Min(z0 + 1, MapY - 1);
            var fx = x - x0;
            var fz = z - z0;
            float H(int gx, int gz) => _heights[gz * MapX + gx] / HeightRawScale;
            var h00 = H(x0, z0);
            var h10 = H(x1, z0);
            var h01 = H(x0, z1);
            var h11 = H(x1, z1);
            return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        }
    }

    /// <summary>Texels no triangle covers (a map edge, a UV gap, the hole the background leaves for the core) take the average of
    /// their already-filled neighbours, ring by ring from the covered texels outward until nothing is left, so no texel stays at
    /// the default dark value. A gap of any width is filled: the background's own hole is hundreds of texels across, and a texel
    /// left dark there bleeds through bilinear filtering into the texels next to the seam as a black band.</summary>
    internal static void FillGapsRgb(byte[] rgb0, byte[] rgb1, byte[] rgb2, byte[] shadowAlpha, bool[] written, int w, int h)
    {
        var queued = new bool[w * h];
        var frontier = new List<int>();
        var ring = new List<(int Index, byte R0, byte G0, byte B0, byte R1, byte G1, byte B1, byte R2, byte G2, byte B2, byte A)>();
        var neighbours = new int[4];

        int Neighbours(int i)
        {
            var x = i % w; var y = i / w; var count = 0;
            if (x > 0) neighbours[count++] = i - 1;
            if (x < w - 1) neighbours[count++] = i + 1;
            if (y > 0) neighbours[count++] = i - w;
            if (y < h - 1) neighbours[count++] = i + w;
            return count;
        }

        for (var i = 0; i < written.Length; i++)
        {
            if (written[i]) continue;
            var count = Neighbours(i);
            for (var k = 0; k < count; k++)
                if (written[neighbours[k]]) { queued[i] = true; frontier.Add(i); break; }
        }

        while (frontier.Count > 0)
        {
            // Every value of a ring is computed from the previous ring only, then the ring is committed at once.
            ring.Clear();
            foreach (var i in frontier)
            {
                float r0 = 0, g0 = 0, b0 = 0, r1 = 0, g1 = 0, b1 = 0, r2 = 0, g2 = 0, b2 = 0, a = 0;
                var n = 0;
                var count = Neighbours(i);
                for (var k = 0; k < count; k++)
                {
                    var j = neighbours[k];
                    if (!written[j]) continue;
                    r0 += rgb0[j * 3]; g0 += rgb0[j * 3 + 1]; b0 += rgb0[j * 3 + 2];
                    r1 += rgb1[j * 3]; g1 += rgb1[j * 3 + 1]; b1 += rgb1[j * 3 + 2];
                    r2 += rgb2[j * 3]; g2 += rgb2[j * 3 + 1]; b2 += rgb2[j * 3 + 2];
                    a += shadowAlpha[j];
                    n++;
                }
                ring.Add((i, (byte)(r0 / n), (byte)(g0 / n), (byte)(b0 / n), (byte)(r1 / n), (byte)(g1 / n), (byte)(b1 / n),
                    (byte)(r2 / n), (byte)(g2 / n), (byte)(b2 / n), (byte)(a / n)));
            }

            frontier.Clear();
            foreach (var t in ring)
            {
                var i = t.Index;
                rgb0[i * 3] = t.R0; rgb0[i * 3 + 1] = t.G0; rgb0[i * 3 + 2] = t.B0;
                rgb1[i * 3] = t.R1; rgb1[i * 3 + 1] = t.G1; rgb1[i * 3 + 2] = t.B1;
                rgb2[i * 3] = t.R2; rgb2[i * 3 + 1] = t.G2; rgb2[i * 3 + 2] = t.B2;
                shadowAlpha[i] = t.A;
                written[i] = true;
            }
            foreach (var t in ring)
            {
                var count = Neighbours(t.Index);
                for (var k = 0; k < count; k++)
                {
                    var j = neighbours[k];
                    if (written[j] || queued[j]) continue;
                    queued[j] = true;
                    frontier.Add(j);
                }
            }
        }
    }
}
