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
            var distWorld = distTexels[i] * worldPerTexel;
            var t = Mathf.Clamp01(distWorld / margin);
            var a = Mathf.Lerp(minAlpha, 1f, Mathf.Pow(t, falloffPower));
            alpha[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
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
