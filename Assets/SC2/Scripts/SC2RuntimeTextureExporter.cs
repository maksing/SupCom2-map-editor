#if !UNITY_EDITOR
using System;
using UnityEngine;

/// <summary>
/// Re-encodes a live Texture2D to a DDS file at runtime, for terrain texture
/// export in a standalone (Build &amp; Run) player.
///
/// Why this exists: the Editor exporter (SC2TerrainExporter.RegTex) exports
/// diffuse/normal/mask textures by finding their source asset file via
/// AssetDatabase.GetAssetPath(tex) and copying that file. AssetDatabase does
/// not exist outside the Editor — the UnityEditorRuntimeShim version of
/// GetAssetPath always returns "" there — so every one of those textures
/// silently failed to export in a build (no source Asset path to copy from
/// for a single texture), while RNM masks still exported fine because they're
/// built from raw mask bytes already held in memory (SC2MaterialPair.
/// RnmMaskBytes), never touching AssetDatabase at all. That mismatch is
/// exactly the "masks export, terrain textures don't" symptom.
///
/// Instead of copying a source file that has no accessible path at runtime,
/// this reads the texture's live pixel data (GetPixels32, which works for
/// any texture format as long as the texture is Read/Write enabled) and
/// re-encodes it to DXT5 with the same encoder already used for RNM masks.
///
/// Known limitation (not fixable from here): a texture imported as "Normal
/// map" type may have its channel data re-swizzled by Unity's importer for
/// in-engine sampling; GetPixels32 returns that in-engine representation, not
/// necessarily byte-identical to the original authored file the Editor path
/// copies. If SC2's shader expects the original unswizzled layout for normal
/// maps, those specific re-exported normal textures may need the source
/// texture's Texture Type set to "Default" (not "Normal map") in the actual
/// Unity project this package is dropped into.
/// </summary>
public static class SC2RuntimeTextureExporter
{
    public static bool TryExportAsDds(Texture2D tex, string destPath, out string error)
    {
        error = null;
        if (tex == null)
        {
            error = "texture is null";
            return false;
        }

        Color32[] pixels;
        try
        {
            // Most DDS textures assigned in the Materials tab (diffuse/normal/
            // mask) have Read/Write disabled by default in their Unity import
            // settings (saves memory) — GetPixels32 throws on those. That's why
            // masks exported fine (built from raw bytes already in memory) while
            // every material texture silently failed here. Fall back to a GPU
            // blit + ReadPixels round-trip, which only requires the destination
            // RenderTexture to be readable, not the source texture.
            pixels = tex.GetPixels32();
        }
        catch (Exception)
        {
            if (!TryReadPixelsViaBlit(tex, out pixels, out error))
                return false;
        }

        var w = tex.width;
        var h = tex.height;
        if (w <= 0 || h <= 0 || pixels.Length != w * h)
        {
            error = $"'{tex.name}' has unexpected pixel data ({pixels.Length} pixels for {w}x{h})";
            return false;
        }
        // GetPixels32 (and the ReadPixels fallback below) return Unity's
        // native bottom-up row order; SC2DdsEncoder.BuildMaterialDds expects
        // DDS's top-down order and never flips internally — without this,
        // every re-exported material texture came out vertically flipped.
        var flipped = new Color32[pixels.Length];
        for (var y = 0; y < h; y++)
            Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);

        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (var i = 0; i < flipped.Length; i++)
        {
            var c = flipped[i];
            rgb[i * 3] = c.r;
            rgb[i * 3 + 1] = c.g;
            rgb[i * 3 + 2] = c.b;
            alpha[i] = c.a;
        }

        try
        {
            // Material textures (Diffuse/Normal/Mask) get a full mip chain
            // and DXT1/DXT5 auto-selected by alpha usage — see
            // SC2DdsEncoder.BuildMaterialDds. RNM lighting masks are a
            // separate, unmipped single-basis-texture export path
            // (SC2TerrainExporter.ExportRnmMaskByName) and are unaffected.
            var dds = SC2DdsEncoder.BuildMaterialDds(w, h, rgb, alpha);
            System.IO.File.WriteAllBytes(destPath, dds);
            return true;
        }
        catch (Exception ex)
        {
            error = $"failed to encode/write '{tex.name}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Reads pixel data from a non-readable Texture2D by blitting it into a
    /// temporary RenderTexture (GPU-side copy, works regardless of the source
    /// texture's Read/Write import setting) and reading that back with
    /// ReadPixels — only the RenderTexture itself needs to be readable, which
    /// is always true for a freshly created one.
    ///
    /// Uses RenderTextureReadWrite.sRGB, not Linear: most diffuse/mask
    /// textures are imported with the "sRGB (Color Texture)" flag on, so
    /// Graphics.Blit's shader samples them with an implicit sRGB->linear
    /// conversion. An sRGB target re-encodes linear->sRGB on write, which
    /// cancels that conversion back out. A Linear target does not — it was
    /// the original bug here: ReadPixels then copied the raw (darker) linear
    /// values straight into RGBA32 bytes with no re-encoding, so every
    /// exported material texture came out visibly darkened/near-black
    /// compared to the source.
    /// </summary>
    private static bool TryReadPixelsViaBlit(Texture2D tex, out Color32[] pixels, out string error)
    {
        pixels = null;
        error = null;
        RenderTexture rt = null;
        var previous = RenderTexture.active;
        try
        {
            rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;

            var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            readable.Apply(false, false);
            pixels = readable.GetPixels32();
            UnityEngine.Object.Destroy(readable);
            return true;
        }
        catch (Exception ex)
        {
            error = $"'{tex.name}' could not be read via GPU blit either: {ex.Message}";
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }
}
#endif
