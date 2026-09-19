// SC2TerrainMaterialExporter.cs
//
// The reverse operation of SC2TerrainMaterialFactory: reads a scene's current
// "SC2/Terrain" material textures and updates TerrainMaterial.TexturePaths.
//
// Real mapping (verified byte-for-byte across all 11 materials of a real
// vanilla map):
//   shader prop  -> TexturePaths slot
//   _Diffuse1    -> [0]
//   _Diffuse2    -> [1]
//   _Normal1     -> [2]
//   _Normal2     -> [3]
//   _Mask1       -> [6]  (a real shader property, previously skipped due to
//                         a stale comment even though the UI already let it
//                         be edited)
//   _Mask2       -> [7]  (see above)
//   _Ramp        -> [8]  (metal/foam only)
//   _RNMMask*    -> [10] (base name without _0/_1/_2)
//
// Slots [4],[5],[9],[11],[12] are left untouched — no shader property.

using UnityEngine;
using BdfIO.Formats.Terrain;

public static class SC2TerrainMaterialExporter
{
    /// <summary>Shader property -> TerrainMaterial.TexturePaths slot, the
    /// mapping documented at the top of this file (also mirrored privately
    /// by SC2GameViewEditorUI.kImportedTexturePathSlots for display).</summary>
    public static readonly System.Collections.Generic.Dictionary<string, int> PathSlotByShaderProp = new()
    {
        { "_Diffuse1", 0 }, { "_Diffuse2", 1 }, { "_Normal1", 2 }, { "_Normal2", 3 },
        { "_Mask1", 6 }, { "_Mask2", 7 }, { "_Ramp", 8 }, { "_RNMMask0", 10 },
    };

    public static void ApplyFromScene(System.Collections.Generic.List<TerrainMaterial> materials, Material[] sharedMaterials, float[] authoredEnableRnm = null, System.Collections.Generic.Dictionary<string, string> texturePathOverrides = null, System.Func<string, Texture2D, bool, string> resolveRawResourcePath = null, System.Collections.Generic.Dictionary<string, bool> texturePathForceExport = null)
    {
        for (var i = 0; i < materials.Count && i < sharedMaterials.Length; i++)
        {
            var mat = materials[i];
            if (SC2HfieldProjectionService.IsWaterMaterial(mat)) continue;

            var unityMat = sharedMaterials[i];
            if (unityMat == null || unityMat.shader == null || unityMat.shader.name != "SC2/Terrain") continue;

            // A "file:"/"archive:" override (the Texture Browser's own
            // internal marker syntax for a Custom/archive-sourced pick — see
            // SC2GameViewEditorUI.DrawMaterialTextureSlot) used to leak
            // straight into TexturePaths verbatim, exactly the bug
            // SC2TerrainExporter.RegTex's own "file:"/"archive:" handling
            // already fixed for the regular Materials/Pairs export path —
            // resolve it the same way here: copy the real file into this
            // export's Textures folder and swap in the real, in-game path.
            // resolveRawResourcePath is null for BuildPreviewGeometryModel's
            // geometry-only preview (no texture paths read there at all),
            // in which case a raw override just passes through unchanged,
            // same as before this fix.
            string Override(string prop)
            {
                if (texturePathOverrides == null || !texturePathOverrides.TryGetValue(i + ":" + prop, out var p) || string.IsNullOrEmpty(p))
                    return null;
                if (resolveRawResourcePath == null) return p;
                var tex = unityMat.HasProperty(prop) ? unityMat.GetTexture(prop) as Texture2D : null;
                // The per-slot "copy into map on export" opt-in (same
                // materialIndex:shaderProp key as the override itself).
                var forceExport = texturePathForceExport != null
                    && texturePathForceExport.TryGetValue(i + ":" + prop, out var force) && force;
                return resolveRawResourcePath(p, tex, forceExport) ?? p;
            }

            // Real slots per the MaterialData structure.
            TryUpdateSlot(mat, 0, unityMat, "_Diffuse1", Override("_Diffuse1"));
            TryUpdateSlot(mat, 1, unityMat, "_Diffuse2", Override("_Diffuse2"));
            TryUpdateSlot(mat, 2, unityMat, "_Normal1", Override("_Normal1"));
            TryUpdateSlot(mat, 3, unityMat, "_Normal2", Override("_Normal2"));
            TryUpdateSlot(mat, 6, unityMat, "_Mask1", Override("_Mask1"));
            TryUpdateSlot(mat, 7, unityMat, "_Mask2", Override("_Mask2"));
            TryUpdateSlot(mat, 8, unityMat, "_Ramp", Override("_Ramp"));   // metal/foam Ramp

            // RNM Mask: _RNMMask0 is stored as the base name (without _0) in slot [10].
            // Recover the base name from the _RNMMask0 texture path.
            var rnmOverride = Override("_RNMMask0");
            if (!string.IsNullOrEmpty(rnmOverride))
            {
                mat.TexturePaths[10] = rnmOverride;
            }
            else if (unityMat.HasProperty("_RNMMask0"))
            {
                var maskTex = unityMat.GetTexture("_RNMMask0");
                if (maskTex != null)
                {
                    var maskPath = TextureToBdfPath(maskTex);
                    if (maskPath != null)
                    {
                        // Unity asset: '/maps/.../mask01_0.win.dds'
                        // Need to recover the base name: '/maps/.../mask01.dds'
                        // Steps: strip .dds -> strip .win -> strip _0/_1/_2
                        var basePath = maskPath;
                        if (basePath.EndsWith(".dds", System.StringComparison.OrdinalIgnoreCase))
                            basePath = basePath[..^4];
                        if (basePath.EndsWith(".win", System.StringComparison.OrdinalIgnoreCase))
                            basePath = basePath[..^4];
                        // Strip the _0, _1 or _2 (slice index) suffix.
                        if (basePath.Length > 2 && basePath[^2] == '_' &&
                            (basePath[^1] == '0' || basePath[^1] == '1' || basePath[^1] == '2'))
                            basePath = basePath[..^2];
                        mat.TexturePaths[10] = basePath + ".dds";
                    }
                }
            }

            if (unityMat.HasProperty("_TextureScroll"))
            {
                var ts = unityMat.GetVector("_TextureScroll");
                mat.TextureScroll[0] = ts.x;
                mat.TextureScroll[1] = ts.y;
            }

            var authoredRnm = authoredEnableRnm != null && i < authoredEnableRnm.Length ? authoredEnableRnm[i] : (float?)null;
            SyncShaderSetupAndStateFromScene(mat, unityMat, authoredRnm);
        }
    }

    /// <summary>_EnableXxx property name -&gt; real ShaderSetup flag name,
    /// the exact inverse of SC2TerrainMaterialFactory.ApplyShaderSetup's own
    /// switch. REFLECT/CHROMATIC_DISPERSION have no shader property (see
    /// that method's own doc comment — confirmed unused by both real shader
    /// files), so there's nothing to read back for them; any original-import
    /// ShaderSetup entries for those two are lost on a Shader-Setup-editing
    /// round-trip through the "Imported" tab. Not addressed here — flagged
    /// as a known, narrow limitation rather than silently preserving stale
    /// entries the live material has no way to represent or let the user see.</summary>
    private static readonly (string Property, string FlagName)[] kFlagPropertyMap =
    {
        ("_EnableRNM", "RNM_LIGHTING"),
        ("_EnableAmbient", "AMBIENT_LIGHTING"),
        ("_EnableDiffuseLighting", "DIFFUSE_LIGHTING"),
        ("_EnableSpecularLighting", "SPECULAR_LIGHTING"),
        ("_EnableBlending", "BLENDING"),
        ("_EnableFog", "FOG"),
        ("_EnableGlow", "GLOW"),
        ("_EnableEnvironmentMapping", "ENVIRONMENT_MAPPING"),
        ("_EnableDynamicShadows", "DYNAMIC_SHADOWS"),
        ("_EnableInfiniteDistance", "INFINITE_DISTANCE"),
    };

    /// <summary>Real write-back for the "Imported" tab's Shader Setup/State
    /// UI (SC2GameViewEditorUI.cs) — without this, editing those toggles only
    /// ever changed the live in-memory Material, never what Smart Export
    /// actually writes into the .bdf (confirmed: MergeImportedTerrain copies
    /// imported.Model.Materials exactly as parsed at import time). Always
    /// rebuilds ShaderSetup from scratch (Clear() then one Add per currently-
    /// true flag) rather than appending — re-exporting the same material
    /// twice without further edits must produce the same single set of
    /// entries, not one more duplicate set stacked on the original
    /// import-time parse each time.</summary>
    private static void SyncShaderSetupAndStateFromScene(TerrainMaterial mat, Material unityMat, float? authoredEnableRnm = null)
    {
        // REFLECT/CHROMATIC_DISPERSION have no live material property to
        // read back at all (see kFlagPropertyMap's own doc comment) — carry
        // forward whatever the original import-time parse already had for
        // them instead of silently dropping them on every re-export, since
        // they're near-universal in the real corpus (672/724 materials).
        var preserved = mat.ShaderSetup.FindAll(e => e.Name == "REFLECT" || e.Name == "CHROMATIC_DISPERSION");

        mat.ShaderSetup.Clear();
        mat.ShaderSetup.AddRange(preserved);

        // RNM_LIGHTING is special-cased, NOT read from unityMat._EnableRNM
        // like every other flag below: that same live property is also
        // force-overridden by the Textured/RNM debug preview toolbar (see
        // SC2GameViewEditorUI.ApplyPreviewModeToImportedTerrain — Textured
        // forces it OFF, RNM forces it ON, on every material, purely for a
        // comparison view). Reading it directly here meant ANY export
        // performed while the toolbar wasn't specifically on "RNM" silently
        // stripped RNM_LIGHTING from every material's real exported
        // ShaderSetup — confirmed the actual root cause behind repeated
        // "RNM_LIGHTING not active" reports on real imported maps.
        // authoredEnableRnm (SC2ImportedTerrainExportData.AuthoredEnableRnm,
        // kept in sync with the user's own Shader Setup checkbox edits, never
        // with the preview toolbar) is the real source of truth when
        // supplied; falls back to the live property only for callers that
        // don't have it (keeps this method's old behavior otherwise).
        var enableRnm = authoredEnableRnm ?? (unityMat.HasProperty("_EnableRNM") ? unityMat.GetFloat("_EnableRNM") : 0f);
        if (enableRnm > 0.5f) mat.ShaderSetup.Add(new ShaderSetupEntry("RNM_LIGHTING", "1"));

        foreach (var (property, flagName) in kFlagPropertyMap)
        {
            if (property == "_EnableRNM") continue; // handled above
            if (!unityMat.HasProperty(property) || unityMat.GetFloat(property) <= 0.5f) continue;
            mat.ShaderSetup.Add(new ShaderSetupEntry(flagName, "1"));
        }
        if (unityMat.HasProperty("_EnableAlphaTest") && unityMat.GetFloat("_EnableAlphaTest") > 0.5f)
        {
            var threshold = unityMat.HasProperty("_AlphaTestThreshold") ? unityMat.GetFloat("_AlphaTestThreshold") : 16f;
            mat.ShaderSetup.Add(new ShaderSetupEntry("ALPHA_TEST", Mathf.RoundToInt(threshold).ToString()));
        }

        mat.State = SC2TerrainMaterialFactory.ReverseLookupBlendState(unityMat);
    }

    private static void TryUpdateSlot(TerrainMaterial mat, int slot, Material unityMat, string property, string overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath)) { mat.TexturePaths[slot] = overridePath; return; }
        if (!unityMat.HasProperty(property)) return;
        var tex = unityMat.GetTexture(property);
        if (tex == null) return;
        var path = TextureToBdfPath(tex);
        if (path != null) mat.TexturePaths[slot] = path;
    }

    private static string? TextureToBdfPath(Texture tex)
    {
#if UNITY_EDITOR
        var assetPath = UnityEditor.AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(assetPath)) return null;
        var root = SC2TerrainMaterialFactory.TextureRoot.Replace('\\', '/').TrimEnd('/') + "/";
        var normalizedAsset = assetPath.Replace('\\', '/');
        if (!normalizedAsset.StartsWith(root)) return null;
        var rel = normalizedAsset[root.Length..];
        return "/" + rel;
#else
        return null;
#endif
    }
}
