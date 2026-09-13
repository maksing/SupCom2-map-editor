using System;
using UnityEngine;

/// <summary>
/// Generates the real per-map "waterDepth" texture (SC2WaterTexturePaths[6],
/// aka Mask1 on the exported "Water" material) — no tool
/// in this project ever actually produced the file
/// SC2WaterPlane.BuildWaterMaterial already defaults that slot's path to
/// ("/maps/&lt;mapName&gt;/&lt;mapName&gt;.waterDepth.dds"), so without a
/// manually-authored texture that reference always pointed at a file that
/// simply doesn't exist.
///
/// Format: RGB is a flat fill of the map's own
/// configured water color (SC2Water.shader's shoreColor/foam term is driven
/// entirely by alpha — see that shader's own FoamFalloffPower comment — so
/// RGB carries no per-pixel signal here; filling every texel including "no
/// water" ones with the same flat color, rather than leaving them black,
/// means DXT5's independent RGB/alpha block compression can never introduce
/// a dark color fringe at a hard alpha edge). Alpha 0 = no water (land).
/// Alpha elsewhere ramps from a small positive floor (WaterDepthMinAlpha) at
/// the shoreline up to fully opaque (1.0) over WaterDepthMargin world units
/// of distance into open water, shaped by WaterDepthFalloffPower — matching
/// SC2Water.shader's own alpha convention (alpha near 0 -> strong shore
/// foam, alpha 1 -> open water, no foam).
/// </summary>
public static class SC2WaterDepthTextureGenerator
{
    // 5 texels — see SC2TerrainData.WaterDepthShoreOutline's own doc
    // comment for what this width means and why it's fixed, not exposed as
    // its own slider.
    private const float ShoreOutlineWidthTexels = 5f;


    /// <summary>Land/water classification + shoreline distance grid, shared
    /// by <see cref="Generate"/> and anything that wants to inspect the
    /// intermediate result (kept internal-only for now — no external caller
    /// needs it yet).</summary>
    private static bool[] ClassifyWater(SC2TerrainData data, Func<float, float, float> sampleHeight,
        float waterElevation, int texSize, float gridSize)
    {
        var isWater = new bool[texSize * texSize];
        for (var tz = 0; tz < texSize; tz++)
        {
            var wz = (tz + 0.5f) / texSize * gridSize;
            for (var tx = 0; tx < texSize; tx++)
            {
                var wx = (tx + 0.5f) / texSize * gridSize;
                var h = sampleHeight(wx, wz);
                isWater[tz * texSize + tx] = (waterElevation - h) > 0f;
            }
        }

        if (data.WaterDepthPaintBorder)
        {
            for (var tx = 0; tx < texSize; tx++)
            {
                isWater[tx] = true;                                   // z = 0
                isWater[(texSize - 1) * texSize + tx] = true;          // z = texSize-1
            }
            for (var tz = 0; tz < texSize; tz++)
            {
                isWater[tz * texSize] = true;                          // x = 0
                isWater[tz * texSize + (texSize - 1)] = true;          // x = texSize-1
            }
        }

        return isWater;
    }

    /// <summary>Two-pass chamfer distance transform (texel units) from the
    /// nearest non-water texel — an approximate Euclidean distance field,
    /// standard/cheap enough for a texture-resolution grid (up to 2048^2)
    /// run once per export or preview refresh, not per frame.</summary>
    private static float[] DistanceToShoreTexels(bool[] isWater, int texSize)
    {
        const float d1 = 1f;
        const float d2 = 1.41421356f;
        var dist = new float[texSize * texSize];
        for (var i = 0; i < dist.Length; i++) dist[i] = isWater[i] ? float.MaxValue * 0.25f : 0f;

        for (var z = 0; z < texSize; z++)
        for (var x = 0; x < texSize; x++)
        {
            var idx = z * texSize + x;
            if (!isWater[idx]) continue;
            var best = dist[idx];
            if (x > 0) best = Mathf.Min(best, dist[idx - 1] + d1);
            if (z > 0) best = Mathf.Min(best, dist[idx - texSize] + d1);
            if (x > 0 && z > 0) best = Mathf.Min(best, dist[idx - texSize - 1] + d2);
            if (x < texSize - 1 && z > 0) best = Mathf.Min(best, dist[idx - texSize + 1] + d2);
            dist[idx] = best;
        }

        for (var z = texSize - 1; z >= 0; z--)
        for (var x = texSize - 1; x >= 0; x--)
        {
            var idx = z * texSize + x;
            if (!isWater[idx]) continue;
            var best = dist[idx];
            if (x < texSize - 1) best = Mathf.Min(best, dist[idx + 1] + d1);
            if (z < texSize - 1) best = Mathf.Min(best, dist[idx + texSize] + d1);
            if (x < texSize - 1 && z < texSize - 1) best = Mathf.Min(best, dist[idx + texSize + 1] + d2);
            if (x > 0 && z < texSize - 1) best = Mathf.Min(best, dist[idx + texSize - 1] + d2);
            dist[idx] = best;
        }

        return dist;
    }

    /// <summary>Resolution actually used for generation — clamped to a
    /// sane range and rounded down to a multiple of 4 (DXT5 block size, see
    /// SC2DdsEncoder.BuildDxt5), so a stale/edited SC2TerrainData.
    /// WaterDepthResolution value can never produce an invalid texture
    /// size.</summary>
    public static int ResolveTexSize(int requested)
    {
        var size = Mathf.Clamp(requested, 64, 2048);
        size -= size % 4;
        return size < 4 ? 4 : size;
    }

    /// <param name="sampleHeight">World (X,Z) -&gt; world Y. Pass a live
    /// editor sampler (e.g. SC2LiveHeightGrid.SampleHeight) for an in-editor
    /// preview, or a closure over the real export hfieldModel (mirroring
    /// SC2TerrainExporter.BuildExportArtifacts' own SampleLiveHeightForDecals)
    /// when generating the file that actually ships.</param>
    public static void Generate(SC2TerrainData data, Func<float, float, float> sampleHeight,
        float waterElevation, Color waterColor, out byte[] rgb, out byte[] alpha, out int texSize)
    {
        texSize = ResolveTexSize(data.WaterDepthResolution);
        var gridSize = Mathf.Max(1, data.CostsGridSize);

        var isWater = ClassifyWater(data, sampleHeight, waterElevation, texSize, gridSize);
        var distTexels = DistanceToShoreTexels(isWater, texSize);

        var worldPerTexel = (float)gridSize / texSize;
        var margin = Mathf.Max(0.01f, data.WaterDepthMargin);
        var falloffPower = Mathf.Max(0.01f, data.WaterDepthFalloffPower);
        var minAlpha = Mathf.Clamp01(data.WaterDepthMinAlpha);

        var texelCount = texSize * texSize;
        rgb = new byte[texelCount * 3];
        alpha = new byte[texelCount];

        var r = (byte)Mathf.RoundToInt(Mathf.Clamp01(waterColor.r) * 255f);
        var g = (byte)Mathf.RoundToInt(Mathf.Clamp01(waterColor.g) * 255f);
        var b = (byte)Mathf.RoundToInt(Mathf.Clamp01(waterColor.b) * 255f);
        for (var i = 0; i < texelCount; i++)
        {
            rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;

            if (!isWater[i]) { alpha[i] = 0; continue; }
            // See SC2TerrainData.WaterDepthShoreOutline's own doc comment —
            // a hard flat-255 override here (an earlier version of this)
            // created a visible jagged seam right where the override band
            // ended and the normal gradient resumed, since distTexels is a
            // discretized per-texel distance field (a circle/diagonal
            // approximated in axis-aligned texel steps) — a solid block of
            // one constant value butting up against it stood out sharply.
            // Instead, shift the EFFECTIVE distance every water texel
            // computes its alpha from by the outline width: the exact same
            // continuous Lerp/Pow curve below just starts ShoreOutlineWidthTexels
            // texels further along, so the shore itself lands on whatever
            // alpha the curve would naturally reach at that distance
            // (visibly higher than WaterDepthMinAlpha, never a flat/solid
            // block) and everything past it is a seamless continuation of
            // the same fade — never dropping back down to the bare
            // WaterDepthMinAlpha floor right at the coastline.
            var effectiveDistTexels = data.WaterDepthShoreOutline ? distTexels[i] + ShoreOutlineWidthTexels : distTexels[i];
            var distWorld = effectiveDistTexels * worldPerTexel;
            var t = Mathf.Clamp01(distWorld / margin);
            var a = Mathf.Lerp(minAlpha, 1f, Mathf.Pow(t, falloffPower));
            alpha[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
        }

        // See SC2TerrainData.WaterDepthImageBorderOutline's own doc comment —
        // applied last, as a hard override over everything above (including
        // WaterDepthPaintBorder). A visible solid stroke of fully-opaque
        // water color (RGB is already the flat water fill above) around the
        // texture's own outer edge, not a zero-alpha cutout — alpha 0 there
        // would just read as "not water" and disappear instead of showing
        // as an outline.
        if (data.WaterDepthImageBorderOutline)
        {
            for (var x = 0; x < texSize; x++)
            {
                alpha[x] = 255;                                  // z = 0
                alpha[(texSize - 1) * texSize + x] = 255;         // z = texSize-1
            }
            for (var z = 0; z < texSize; z++)
            {
                alpha[z * texSize] = 255;                         // x = 0
                alpha[z * texSize + (texSize - 1)] = 255;         // x = texSize-1
            }
        }
    }

    /// <summary>Convenience wrapper for the Water tab's live preview —
    /// builds a real Texture2D (RGBA32) from Generate's raw byte arrays so
    /// the UI can just GUI.DrawTexture it, no DDS round-trip needed for an
    /// in-editor look.</summary>
    public static Texture2D GeneratePreviewTexture(SC2TerrainData data, Func<float, float, float> sampleHeight,
        float waterElevation, Color waterColor)
    {
        Generate(data, sampleHeight, waterElevation, waterColor, out var rgb, out var alpha, out var texSize);
        var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false, false)
        {
            name = "WaterDepthPreview",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color32[texSize * texSize];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], alpha[i]);
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return tex;
    }
}
