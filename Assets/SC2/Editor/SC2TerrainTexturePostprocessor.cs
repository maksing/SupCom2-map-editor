// SC2TerrainTexturePostprocessor.cs
//
// Real vanilla textures are frequently non-power-of-two (e.g. dirt_01_d.win.dds,
// 1896x1896, 11 embedded mips — confirmed via a third-party DDS viewer + NVIDIA
// Texture Tools to be a perfectly valid classic-header BC1/DXT1 file).
//
// npotScale = ToNearest (Unity's own default) was tried as a way to force
// Unity to regenerate a fresh mip chain via decompress/resize/recompress,
// hoping that would sidestep a separately-confirmed bug in Unity's decode of
// this file's own embedded mips (Texture2D Inspector: mip 0 correct, every
// deeper mip pure block noise). Confirmed WRONG by direct observation: this
// is the project's original default, and restoring it reintroduced the
// original full-frame RGB static bug immediately and universally (not just
// for this one file) — the resize round-trip itself, not just trusting the
// original embedded mips, is what's unreliable for this class of file.
//
// npotScale = None avoids the resize entirely, importing the file's real
// dimensions/mips as-is — confirmed to fix the RGB static for the large
// majority of real vanilla textures. A small number of NPOT files with deep
// mip chains (e.g. dirt_01_d specifically) still show mip-level-dependent
// corruption at a distance even with this set; no simple npotScale setting
// fixes that remaining case — it needs a real decode bypass (see git history
// around commits 312be98/6109844 for the fuller fix that was tried and
// reverted after introducing an unrelated blend regression, kept here as a
// reference for revisiting rather than re-guessing from scratch).
using System.IO;
using UnityEditor;

public class SC2TerrainTexturePostprocessor : AssetPostprocessor
{
    // Every real water-data .dds under
    // SC2Textures imports through Unity's built-in IHV/DDS importer
    // (UnityEditor.IHVImageFormatImporter, BaseType=UnityEditor.AssetImporter), NOT
    // TextureImporter — OnPreprocessTexture is a TextureImporter-only hook and
    // is never invoked for these assets at all.
    // IHVImageFormatImporter also has no public sRGBTexture property, so even a
    // successful cast couldn't fix it here. The real fix is SC2WaterTextureImportFixer
    // (direct .meta YAML rewrite + ForceUpdate reimport, menu: SC2/Textures/Fix
    // Water Texture Import Settings). This hook still legitimately handles the
    // sibling *.waterDepth.png "editor preview" textures below — those really
    // are TextureImporter-based and this fix already applies correctly to them.
    void OnPreprocessTexture()
    {
        if (!IsSc2CopiedTexturePath(assetPath))
            return;
        var isWaterData = IsSc2WaterDataTexturePath(assetPath);
        if (assetImporter is not TextureImporter importer)
            return;
        importer.npotScale = TextureImporterNPOTScale.None;

        // Real vanilla water "data" textures (wave-normal maps,
        // the wave-animation ramp, per-map waterDepth/Mask1) are sampled as
        // raw [0,1] RGB by SC2Water.shader — the decompiled
        // water.hlsl never calls UnpackNormal on them —
        // not gamma-decoded color and not Unity's repacked "Normal map"
        // channel layout. SC2WaterMaterialFactory.EnsureLinearImport already
        // forces this correctly, but ONLY from live Editor-side code paths
        // (#if UNITY_EDITOR) - the exact same texture resolved through a
        // real Standalone Build's Resources.Load-only fallback has no
        // equivalent correction available at runtime (TextureImporter
        // settings don't exist post-build), so a Build only renders
        // correctly if these settings were ALREADY baked in before that
        // build was made. Confirmed via a real Player.log: normalSum.z
        // (whitecap trigger) reads as if every wave-normal texture were
        // still sRGB/normal-map-packed, saturating "Add whitecap foam"
        // across the whole water surface instead of the few real crest
        // highlights the authored WaterWhiteCapCutoff (~3.94, near the
        // theoretical max of 4.0) is tuned for. This hook makes the setting
        // correct the moment Unity ever imports/reimports the asset,
        // independent of whether any runtime code touched it first —
        // guaranteeing the NEXT build has it right.
        if (isWaterData)
        {
            importer.sRGBTexture = false;
            importer.textureType = TextureImporterType.Default;
        }
    }

    internal static bool IsSc2CopiedTexturePath(string assetPath) =>
        assetPath.StartsWith("Assets/SC2Textures/", System.StringComparison.OrdinalIgnoreCase) ||
        assetPath.StartsWith("Assets/SC2/Resources/SC2Textures/", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>waves_001..004 (Normal1-4), wave_animation_001 (Ramp), and
    /// any per-map "<MapName>.waterDepth.dds" (Mask1) — the exact set
    /// SC2WaterMaterialFactory routes through LoadTexturePublicRawData
    /// (isLinear:true, forceDefaultType:true). Deliberately does NOT match
    /// foam_001/foam_002 (Diffuse1/2) — those stay real sRGB color
    /// textures, loaded via the ordinary LoadTexturePublic path.</summary>
    internal static bool IsSc2WaterDataTexturePath(string assetPath)
    {
        var fileName = Path.GetFileName(assetPath).ToLowerInvariant();
        return fileName.StartsWith("waves_") ||
               fileName.StartsWith("wave_animation_") ||
               fileName.Contains(".waterdepth.");
    }
}
