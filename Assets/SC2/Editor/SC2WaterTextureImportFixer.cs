// SC2WaterTextureImportFixer.cs
//
// waves_001-004.win.dds / wave_animation_001.win.dds / *.waterDepth.dds all
// import through Unity's built-in UnityEditor.IHVImageFormatImporter (the DDS/IHV
// format importer), NOT TextureImporter — confirmed via reflection
// (IHVImageFormatImporter.BaseType == UnityEditor.AssetImporter, no
// TextureImporter anywhere in its hierarchy, and no public sRGBTexture
// property/method exists on it at all). AssetPostprocessor.OnPreprocessTexture
// is a TextureImporter-only hook and is never invoked for these assets
// (confirmed: zero [SC2Postprocess] log output for any waves_*/wave_animation_*
// .dds even across a full "Reimport All Assets" — see SC2TerrainTexturePostprocessor's
// own comment for the fuller history of this investigation).
//
// The only reliable fix is a direct text rewrite of the .meta file's own
// "sRGBTexture: 1" line (a real, if unexposed via public C# API, serialized
// field on IHVImageFormatImporter — confirmed present via direct .meta
// inspection) followed by a forced reimport, which re-reads the corrected
// .meta and produces a texture that actually samples as linear at runtime.
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class SC2WaterTextureImportFixer
{
    private static readonly string[] Roots = { "Assets/SC2Textures", "Assets/SC2/Resources/SC2Textures" };

    [MenuItem("SC2/Textures/Fix Water Texture Import Settings (sRGB)")]
    public static void FixWaterDataImportSettingsMenu()
    {
        var (fixedCount, alreadyOkCount, skippedCount) = FixWaterDataImportSettings();
        Debug.Log($"[SC2WaterTextureImportFixer] Done. Fixed={fixedCount} AlreadyCorrect={alreadyOkCount} Skipped={skippedCount}");
        EditorUtility.DisplayDialog("SC2 Water Texture Fix",
            $"Fixed: {fixedCount}\nAlready correct: {alreadyOkCount}\nSkipped: {skippedCount}\n\nSee Console for per-file details.",
            "OK");
    }

    public static (int fixedCount, int alreadyOkCount, int skippedCount) FixWaterDataImportSettings()
    {
        int fixedCount = 0, alreadyOkCount = 0, skippedCount = 0;
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.GetFiles(root, "*.dds", SearchOption.AllDirectories))
            {
                var assetPath = file.Replace('\\', '/');
                if (!SC2TerrainTexturePostprocessor.IsSc2WaterDataTexturePath(assetPath))
                    continue;

                var metaPath = assetPath + ".meta";
                if (!File.Exists(metaPath))
                {
                    Debug.LogWarning($"[SC2WaterTextureImportFixer] '{assetPath}': no .meta file found - skipping.");
                    skippedCount++;
                    continue;
                }

                var metaText = File.ReadAllText(metaPath);
                if (!metaText.Contains("IHVImageFormatImporter:"))
                {
                    Debug.Log($"[SC2WaterTextureImportFixer] '{assetPath}': not IHVImageFormatImporter-backed - skipping (real TextureImporter assets are already handled by SC2TerrainTexturePostprocessor).");
                    skippedCount++;
                    continue;
                }

                if (Regex.IsMatch(metaText, @"^  sRGBTexture: 0\s*$", RegexOptions.Multiline))
                {
                    alreadyOkCount++;
                    continue;
                }

                var newText = Regex.Replace(metaText, @"^(  sRGBTexture: )1(\s*)$", "${1}0${2}", RegexOptions.Multiline);
                if (newText == metaText)
                {
                    Debug.LogWarning($"[SC2WaterTextureImportFixer] '{assetPath}': found IHVImageFormatImporter block but no matching 'sRGBTexture: 1' line - .meta layout differs from expected, skipping.");
                    skippedCount++;
                    continue;
                }

                File.WriteAllText(metaPath, newText);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                Debug.Log($"[SC2WaterTextureImportFixer] '{assetPath}': sRGBTexture 1 -> 0, reimported.");
                fixedCount++;
            }
        }
        AssetDatabase.Refresh();
        return (fixedCount, alreadyOkCount, skippedCount);
    }
}
