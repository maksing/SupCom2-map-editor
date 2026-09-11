// SC2TerrainMaterialFactory.cs
//
// Builds a Unity Material (SC2/Terrain) from a BdfIO.Formats.Terrain.TerrainMaterial.
// Maps TexturePaths[0..12] (13 slots: "Diffuse1/2, Normal1-4, Mask1/2, Ramp,
// Texture6, RNM Volume, Environment, Texture8") to shader properties — based
// on an empirical byte-level dump of a real vanilla map's materials (see
// below); some slots are high-confidence, others are provisional.
//
// Empirical mapping (mat[0], a "ground" material, verified byte-for-byte):
// struct: <6I 2f 13I> = name,shaderFile,shaderPrefix,ssCount,ssOffset,state,scrollX,scrollY,tex[0..12]
//   tex[0]  = "/Textures\\Gravel040_2K-PNG_Color.dds"    -> Diffuse1
//   tex[1]  = "/Textures\\Ground046_2K-PNG_Color.dds"    -> Diffuse2
//   tex[2]  = "/Textures\\Gravel040_2K-PNG_NormalDX.dds" -> Normal1
//   tex[3]  = "/Textures\\Ground046_2K-PNG_NormalDX.dds" -> Normal2
//   tex[4]  = null
//   tex[5]  = null
//   tex[6]  = "/textures/Engine/black_d.dds"             -> auxiliary (specular/mask?)
//   tex[7]  = "/textures/Engine/black_d.dds"             -> auxiliary
//   tex[8]  = null
//   tex[9]  = null
//   tex[10] = "/maps/.../lighting/mask01.dds"            -> RNM Mask (base name, see _0/_1/_2)
//   tex[11] = null
//   tex[12] = null

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;

public static class SC2TerrainMaterialFactory
{
    /// <summary>
    /// Base path where map textures live, in AssetDatabase format
    /// ("Assets/..."), used only by UnityEditor.AssetDatabase.LoadAssetAtPath.
    /// </summary>
    public static string TextureRoot = "Assets/SC2Textures";

    /// <summary>
    /// Same root, but in the format Resources.Load expects — it does NOT
    /// accept an "Assets/" prefix (paths are relative to the Resources/
    /// folder itself, not the project), so this is a separate constant
    /// rather than TextureRoot unchanged. Resources.Load calls that
    /// mistakenly used TextureRoot directly ended up looking for
    /// ".../Resources/Assets/SC2Textures/..." — with an extra "Assets/" —
    /// when the real project layout (and what Resources.Load actually
    /// searches) is ".../Resources/SC2Textures/...".
    /// </summary>
    public static string ResourcesTextureRoot = "SC2Textures";

    /// <summary>Folder name under Application.streamingAssetsPath mirroring
    /// every texture also copied to Resources/ResourcesTextureRoot — real,
    /// uncompressed loose files on disk, readable via File.ReadAllBytes both
    /// in the Editor and in a real Standalone Windows Build (unlike
    /// Resources/, whose contents get packed into the Player's data files
    /// at build time and are never File.ReadAllBytes-accessible again).
    /// Exists specifically so LoadTextureUncached can decode real vanilla
    /// DDS bytes itself via SC2CustomTextureImporter instead of trusting
    /// Unity's own native DDS importer — confirmed via Unity's own Texture
    /// Inspector mip-level preview that Unity's native import produces
    /// correct mip 0 but garbage for every deeper embedded mip level of a
    /// real vanilla non-power-of-two compressed texture (dirt_01_d.win.dds,
    /// 1896x1896) — a native-importer limitation no TextureImporter setting
    /// was found to work around, not something this project's code caused.
    /// </summary>
    public static string StreamingAssetsTextureRoot = "SC2Textures";

    /// <summary>One texture resolution attempt, recorded while diagnostic capture is active (see BeginDiagnosticCapture).</summary>
    public struct TextureLoadAttempt
    {
        public string RequestedPath;   // raw path as written in terrain.bdf
        public bool Found;
        public string ResolvedFrom;    // the exact Resources.Load/AssetDatabase path that worked, or null
    }

    /// <summary>
    /// Maps each successfully-resolved Texture2D to the exact Resources-relative
    /// path (rooted at ResourcesTextureRoot) that a Resources.Load call actually
    /// returned it from — populated only in LoadTextureCore's Resources.Load
    /// branches (the Editor's own Resources fallback, and the whole Build
    /// branch, which has no AssetDatabase to ask instead). NOT the same thing
    /// as tex.name: two genuinely different textures in different source
    /// folders can share the same leaf file name (a "diffuse.dds" under both a
    /// Grass/ and a Rock/ material folder, say), which made SC2RuntimeTerrainPersistence's
    /// old name-only save/resolve ambiguous — this gives it something
    /// unambiguous to prefer when available (Unity doesn't allow two
    /// different assets at the same Resources path in one build).
    /// </summary>
    // Widened from Texture2D so cubemap/volume (Texture3D) assets resolved
    // by the info.bdf texture-field picker (SC2TerrainShell.DrawTexturePicker
    // + SC2CustomTextureImporter's Cubemap/Texture3D support) can share this
    // same reverse-lookup cache — Texture2D/Cubemap/Texture3D are all IS-A
    // Texture, so every existing Texture2D caller still compiles unchanged.
    private static readonly Dictionary<Texture, string> _resourcePathByTexture = new();

    /// <summary>Returns the Resources-relative path this texture was actually
    /// loaded from, if known — null if it was never resolved via
    /// Resources.Load through this factory (e.g. assigned directly through
    /// the Editor's generic AssetDatabase-backed texture picker instead of
    /// terrain.bdf import).</summary>
    public static string TryGetResourcePath(Texture tex) =>
        tex != null && _resourcePathByTexture.TryGetValue(tex, out var path) ? path : null;

    public static void RegisterResourcePath(Texture texture, string resourcePath)
    {
        if (texture == null || string.IsNullOrWhiteSpace(resourcePath)) return;
        _resourcePathByTexture[texture] = resourcePath.Replace('\\', '/');
    }

    /// <summary>
    /// Like TryGetResourcePath, but also falls back to SC2TextureManifest's
    /// reverse lookup when this factory never loaded the texture itself —
    /// the common case for materials whose Diffuse/Normal/Mask was assigned
    /// directly through the generic runtime texture picker instead of
    /// terrain.bdf import. Several different subfolders can share the same
    /// leaf name (see SC2TextureManifest's own doc comment), so each
    /// manifest candidate is actually loaded and compared by reference
    /// before being accepted — this confirms it really is THIS texture, not
    /// just a same-named one, and caches the result for next time.
    /// </summary>
    public static string ResolveResourcePath(Texture2D tex)
    {
        if (tex == null)
            return null;

        var cached = TryGetResourcePath(tex);
        if (cached != null)
            return cached;

        foreach (var candidate in SC2TextureManifest.GetCandidatePaths(tex.name))
        {
            var loaded = Resources.Load<Texture2D>(candidate);
            if (loaded == tex)
            {
                _resourcePathByTexture[tex] = candidate;
                return candidate;
            }
        }
        return null;
    }

    /// <summary>The inverse of ResolveResourcePath: resolves a saved/
    /// serialized texture reference (a Resources-relative resourcePath,
    /// falling back to a bare name) back to a live Texture2D. Moved here
    /// out of SC2RuntimeTerrainPersistence.
    /// ResolveTexture (a private, near-identical method, #if !UNITY_EDITOR-
    /// gated) so SC2MapDocumentToLegacyBridge.ScriptableObject.cs's document
    /// -&gt; SC2TerrainData reverse mapping can reuse the exact same
    /// resolution logic instead of a second, drifting copy — both
    /// SC2TerrainMaterialFactory and SC2RuntimeTerrainPersistence already
    /// compile into the same implicit assembly, so this was a pure move, not
    /// a new dependency.
    ///
    /// Prefers <paramref name="resourcePath"/> (Resources.Load with a full
    /// path is unambiguous by construction — Unity doesn't allow two
    /// different assets at the same Resources path in one build) or a
    /// "file:"-prefixed custom-texture path (SC2CustomTextureImporter).
    /// Falls back to a by-name search over SC2TextureCatalog for saves made
    /// before a resourcePath was ever recorded, or a texture assigned
    /// without going through this factory at all — genuinely ambiguous if
    /// two different textures share the same bare name, but there is no way
    /// to do better without a saved path.</summary>
    /// <param name="materialName">Diagnostic only — identifies which
    /// material this slot belongs to in the log line below.</param>
    /// <param name="slotLabel">Diagnostic only — "Diffuse"/"Normal"/"Mask",
    /// so a failure log line says exactly which slot didn't resolve.</param>
    public static Texture2D ResolveTextureByPathOrName(string name, string resourcePath = null, string materialName = null, string slotLabel = null)
    {
        if (!string.IsNullOrEmpty(resourcePath))
        {
            if (resourcePath.StartsWith("file:", System.StringComparison.Ordinal))
            {
                var filePath = resourcePath.Substring(5);
                if (SC2CustomTextureImporter.TryLoad(filePath, out var custom, out var customError))
                {
                    custom.name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(filePath) : name;
                    RegisterResourcePath(custom, resourcePath);
                    return custom;
                }
                Debug.LogWarning($"[SC2TerrainMaterialFactory] {materialName}.{slotLabel}: custom texture " +
                                 $"'{filePath}' could not be loaded: {customError}");
            }
            // Game-archive-sourced textures (picked via the
            // texture browser's Usage='Any' — or any usage — filter against
            // an archive entry) were never handled here at all, only "file:"
            // (on-disk custom) and a plain Resources path — an archive-
            // sourced material texture silently failed to re-resolve on
            // reload even after the override-path preservation fix below.
            if (resourcePath.StartsWith("archive:", System.StringComparison.Ordinal))
            {
                var entryPath = resourcePath.Substring(8);
                string archiveError = null;
                if (SC2GameArchiveRegistry.TryGetReader(out var reader) &&
                    SC2CustomTextureImporter.TryLoadFromArchive(reader, entryPath, out var archiveTex, out archiveError) &&
                    archiveTex is Texture2D archiveTex2D)
                {
                    archiveTex2D.name = string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(entryPath) : name;
                    RegisterResourcePath(archiveTex2D, resourcePath);
                    return archiveTex2D;
                }
                Debug.LogWarning($"[SC2TerrainMaterialFactory] {materialName}.{slotLabel}: archive texture " +
                                 $"'{entryPath}' could not be loaded: {archiveError}");
            }
            var byPath = Resources.Load<Texture2D>(resourcePath);
            if (byPath != null)
            {
                RegisterResourcePath(byPath, resourcePath);
                return byPath;
            }
            Debug.LogWarning($"[SC2TerrainMaterialFactory] {materialName}.{slotLabel}: saved resourcePath " +
                              $"'{resourcePath}' did not resolve via Resources.Load — falling back to by-name search for '{name}'.");
        }

        if (string.IsNullOrEmpty(name))
            return null;

        var candidates = 0;
        foreach (var entry in SC2TextureCatalog.Instance.Entries)
        {
            if (!string.Equals(entry.Name, name, System.StringComparison.Ordinal)) continue;
            candidates++;
            Texture2D candidate = null;
            if (!string.IsNullOrEmpty(entry.ResourcePath))
                candidate = Resources.Load<Texture2D>(entry.ResourcePath);
            else if (!string.IsNullOrEmpty(entry.GamePath))
                SC2CustomTextureImporter.TryLoad(entry.GamePath, out candidate, out _);
            if (candidate == null) continue;
            RegisterResourcePath(candidate,
                !string.IsNullOrEmpty(entry.ResourcePath) ? entry.ResourcePath : "file:" + entry.GamePath);
            return candidate;
        }

        Debug.LogWarning($"[SC2TerrainMaterialFactory] {materialName}.{slotLabel}: no texture named '{name}' " +
                          $"found among {candidates} matching catalog entries (resourcePath was " +
                          $"{(string.IsNullOrEmpty(resourcePath) ? "not saved" : $"'{resourcePath}', see above")}).");
        return null;
    }

    // Not thread-safe — Unity's own material/texture APIs aren't either, so
    // this matches the existing single-threaded usage (Create/LoadTexture
    // always run on the main thread during import).
    private static List<TextureLoadAttempt> _captureBuffer;

    /// <summary>
    /// Starts recording every LoadTexture call's outcome (path tried, found
    /// or not, and what actually resolved it) into the returned list. Needed
    /// because Debug.Log output isn't visible in a shipped standalone
    /// player — this is the only way to get texture-loading diagnostics
    /// in front of the user there. Call EndDiagnosticCapture when done
    /// (usually right after TerrainMeshBuilder.Build, which is what
    /// triggers all the LoadTexture calls via SC2TerrainMaterialFactory.Create).
    /// </summary>
    public static List<TextureLoadAttempt> BeginDiagnosticCapture()
    {
        _captureBuffer = new List<TextureLoadAttempt>();
        return _captureBuffer;
    }

    public static void EndDiagnosticCapture() => _captureBuffer = null;

    public static Material Create(TerrainMaterial mat)
    {
        // Shader-aware detection (checks ShaderFile as well as an exact
        // "Water" name match) — was a strict name-only check, silently
        // missing water materials named anything else even though their
        // shader file says water. Shared with SC2WaterMaterialFactory.cs/
        // SC2TerrainBdfImporter.cs/SC2TerrainMaterialExporter.cs/
        // Editor/ScdImportWindow.cs so the 5 (now-former) independent checks
        // can't disagree with each other again.
        if (SC2HfieldProjectionService.IsWaterMaterial(mat))
        {
            var waterShader = ResolveShader("SC2/Water", "SC2Water");
            var waterMat = new Material(waterShader) { name = "mat_Water" };
            // Water texture slots (verified byte-for-byte, mat[10]):
            //   tex[0]=foam_001, tex[1]=foam_002
            //   tex[2..5]=waves_001..004
            //   tex[6]=waterDepth, tex[8]=wave_animation_001
            // SC2WaterMaterialFactory assigns these separately by file name.
            waterMat.color = new Color(0.15f, 0.35f, 0.55f, 0.6f);
            return waterMat;
        }

        var shader = ResolveShader("SC2/Terrain", "SC2Terrain");
        var material = new Material(shader) { name = $"mat_{mat.Name}" };

        // Real mapping of TexturePaths[0..12] (verified byte-for-byte across
        // all 11 materials of a real vanilla map):
        //   tex[0]  = Diffuse1       tex[1]  = Diffuse2
        //   tex[2]  = Normal1        tex[3]  = Normal2
        //   tex[6]  = Specular1      tex[7]  = Specular2
        //   tex[8]  = Ramp           tex[10] = RNM Mask base
        //   tex[11] = Environment

        TrySetTexture(material, "_Diffuse1", mat.TexturePaths[0]);
        TrySetTexture(material, "_Diffuse2", mat.TexturePaths[1]);
        TrySetNormal(material,  "_Normal1",  mat.TexturePaths[2]);
        TrySetNormal(material,  "_Normal2",  mat.TexturePaths[3]);

        // Mask1/Mask2 (tex[6]/tex[7]) — specular/mask textures.
        // Ground materials use black_d.dds (neutral specular).
        TrySetTexture(material, "_Mask1", mat.TexturePaths[6], optional: true);
        TrySetTexture(material, "_Mask2", mat.TexturePaths[7], optional: true);

        // Ramp (tex[8]) — only on metal/foam materials. _HasRamp mirrors
        // SC2Water.shader's _HasMask1Tex pattern: a bound "black" {} default
        // texture isn't reliably distinguishable from a real one inside the
        // shader itself, so this drives DiffuseFormula/SpecularFormula/
        // ComputeEnvironment's real-ramp-vs-analytic-formula branch (see
        // SC2Terrain.shader's own Properties comment).
        TrySetTexture(material, "_Ramp", mat.TexturePaths[8], optional: true);
        material.SetFloat("_HasRamp", mat.TexturePaths[8] != null ? 1f : 0f);

        // RNM masks (tex[10] = base name without the slice suffix):
        //   mask01_0.win.dds = basis0 (RGB)
        //   mask01_1.win.dds = basis1 (RGB) + A = blend factor
        //   mask01_2.win.dds = basis2 (RGB)
        // isLinear: true — these encode raw basis-vector/blend data, not
        // color, same reasoning as _Normal1/2 below. This is the real cause
        // behind imported terrain rendering as full-frame RGB static/noise:
        // a freshly-copied texture (SC2TerrainBdfImporter.EnsureTexturesAvailable's
        // plain File.Copy, no AssetPostprocessor anywhere in this project to
        // override it) gets Unity's *default* sRGB="Color Texture" import
        // setting — gamma-decoding what's actually raw linear vector bytes
        // corrupts every value, and RNM's basis-blend math amplifies that
        // into per-pixel noise rather than a subtle tint shift.
        // TexturePaths[10] can be an empty string (not null) when a real
        // material has no RNM mask authored. `!= null` alone wrongly treats
        // "" as "has real data", entering this block to load a bogus
        // "_0"-suffixed path and setting _HasRNM=1 below despite no real
        // mask ever loading — reproducing exactly the "RNM_LIGHTING not
        // active" black-screen bug.
        var maskBasePath = mat.TexturePaths[10];
        if (!string.IsNullOrEmpty(maskBasePath))
        {
            var maskBase = StripExtension(NormalizePath(maskBasePath));
            // disableMipmaps: true — RNM masks encode raw basis-vector/blend
            // data, not color; any
            // mip level (real embedded or auto-generated) averages that data
            // incorrectly and produces visible striping on the real rendered
            // terrain. See LoadTextureUncached's EnsureLinearImport call and
            // SC2CustomTextureImporter.TryBuildDdsFlat's disableMipmaps
            // branch for where this is actually enforced.
            TrySetTexture(material, "_RNMMask0", maskBase + "_0", isLinear: true, disableMipmaps: true);
            TrySetTexture(material, "_RNMMask1", maskBase + "_1", isLinear: true, disableMipmaps: true); // A = blend factor
            TrySetTexture(material, "_RNMMask2", maskBase + "_2", optional: true, isLinear: true, disableMipmaps: true);
        }
        // Same "shader can't tell a real texture from its own property
        // default" reasoning as _HasRamp above — see SC2Terrain.shader's own
        // _HasRNM Properties comment for the exact black-screen bug this
        // closes (RNM branch entered with no real mask data, e.g. via the
        // "RNM" debug preview toolbar forcing _EnableRNM on every material).
        material.SetFloat("_HasRNM", maskBasePath != null ? 1f : 0f);

        ApplyShaderSetup(material, mat.ShaderSetup);
        material.SetVector("_TextureScroll", new Vector4(mat.TextureScroll[0], mat.TextureScroll[1], 0, 0));
        ApplyBlendState(material, mat.State);

        return material;
    }

    /// <summary>
    /// terrain.bdf materials carry a State string (e.g. "TerrainAlphaBlendState",
    /// seen on cloud-plane materials) that the shader used to ignore
    /// entirely — every material rendered fully opaque with alpha forced to
    /// 1 regardless of the diffuse texture's actual alpha channel, so
    /// alpha-blend materials (clouds, translucent decals, ...) showed up as
    /// solid opaque blocks instead of blending with the terrain beneath
    /// them. SC2Terrain.shader now exposes _SrcBlend/_DstBlend/_ZWrite as
    /// material properties for exactly this — set them (plus the material's
    /// render queue, which a shader's Tags block can't do per-material) here
    /// based on State.
    /// </summary>
    /// <summary>Real corpus survey (56 vanilla maps, 724 materials) found a
    /// 4th real State value beyond the 3 originally known:
    /// "TerrainAdditiveState" (3 materials — glow-style overlays) and
    /// "TerrainOpaqueState" (2 materials — functionally identical to the
    /// null/default case, made explicit here rather than only working by
    /// falling through the else branch). "WaterState" is handled entirely
    /// separately by SC2WaterMaterialFactory (a different shader), never
    /// reaches this method.</summary>
    /// <summary>Public so the "Imported" terrain UI (SC2GameViewEditorUI's
    /// Shader Setup section) can apply a State selection directly to a live
    /// Material, the same way import-time material creation already does —
    /// see also ReverseLookupBlendState below, its inverse for populating a
    /// State dropdown's initial selection from a live material's current
    /// blend properties.</summary>
    public static void ApplyBlendState(Material material, string? state)
    {
        var alphaBlend = state != null && state.IndexOf("AlphaBlend", System.StringComparison.OrdinalIgnoreCase) >= 0;
        var additive = state != null && state.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) >= 0;
        if (alphaBlend)
        {
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else if (additive)
        {
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else
        {
            // Covers both the null/default case and the explicit
            // "TerrainOpaqueState" — both mean plain opaque.
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetFloat("_ZWrite", 1f);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }
    }

    /// <summary>Same threshold SC2Environment.cs's own private helper of the
    /// same name already uses.</summary>
    private static bool IsEffectivelyBlack(Color c) => c.r <= 0.001f && c.g <= 0.001f && c.b <= 0.001f;

    /// <summary>Inverse of ApplyBlendState — inspects a live material's
    /// current _SrcBlend/_DstBlend/_ZWrite and returns the matching State
    /// name, for populating a State dropdown's initial selection. Both
    /// "None" (never set) and "TerrainOpaqueState" produce identical blend
    /// values (One/Zero, ZWrite on) — this can't tell them apart from blend
    /// state alone, same as ApplyBlendState's own else-branch already
    /// treats them as interchangeable, so this returns null (displayed as
    /// "None") for that case rather than guessing.</summary>
    public static string? ReverseLookupBlendState(Material material)
    {
        if (material == null || !material.HasProperty("_SrcBlend")) return null;
        var src = material.GetFloat("_SrcBlend");
        var dst = material.GetFloat("_DstBlend");
        var zwrite = material.HasProperty("_ZWrite") ? material.GetFloat("_ZWrite") : 1f;
        if (Mathf.Approximately(src, (float)UnityEngine.Rendering.BlendMode.SrcAlpha) &&
            Mathf.Approximately(dst, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha))
            return "TerrainAlphaBlendState";
        if (Mathf.Approximately(src, (float)UnityEngine.Rendering.BlendMode.One) &&
            Mathf.Approximately(dst, (float)UnityEngine.Rendering.BlendMode.One) &&
            Mathf.Approximately(zwrite, 0f))
            return "TerrainAdditiveState";
        return null;
    }

    /// <summary>Real per-map Sun/Shadow/Fog/EnvironmentMap data, following
    /// the exact per-material wiring pattern SC2WaterMaterialFactory.ApplyTo
    /// already uses for SC2/Water (info.SunDirection/SunColor/CoreFogColor*
    /// -&gt; per-material SetColor/SetVector, no Shader.SetGlobal* calls
    /// anywhere in this project). Callable repeatedly (mirrors
    /// SC2WaterPreview.Ensure/BindImportedWater's "refresh every tick" —
    /// see SC2TerrainShell.Update's own call to this for imported terrain
    /// materials) so live edits to the Environment tab are reflected
    /// without re-importing. ShadowColor (parsed, UI-editable, but
    /// previously consumed by nothing) now drives the shader's real
    /// AMBIENT_LIGHTING/RNM-shadow-blend behavior instead of the old
    /// editor-only hardcoded _AmbientColor constant.
    ///
    /// Same zero-Environment fallback SC2Environment.ApplyToScene already
    /// established for props/units (task: "Fix Sun/ambient collapsing to
    /// black light on zero-Environment maps") — a from-scratch map's
    /// Environment header is genuinely all-zero bytes until authored/
    /// imported, so SunColor/ShadowColor read back as literal (0,0,0,0).
    /// Reported live: materials without RNM_LIGHTING (the non-RNM branch's
    /// `color *= _ShadowColor.rgb`) rendered fully black on such a map.
    /// Without this same floor/fallback here, this shader reintroduces
    /// exactly the bug that fix already closed for SC2/Unit.</summary>
    public static void ApplyEnvironment(Material material, InfoModel info)
    {
        if (material == null || info == null) return;

        var rawSunColor = new Color(info.SunColorR, info.SunColorG, info.SunColorB);
        var sunColor = IsEffectivelyBlack(rawSunColor) ? Color.white : rawSunColor;
        var sunIntensity = Mathf.Max(0.01f, info.SunIntensity);
        var sunDirection = new Vector3(info.SunDirectionX, info.SunDirectionY, info.SunDirectionZ);
        if (sunDirection.sqrMagnitude < 0.0001f) sunDirection = new Vector3(0.3f, 0.8f, 0.5f);

        var rawShadowColor = new Color(info.ShadowColorR, info.ShadowColorG, info.ShadowColorB);
        var shadowColor = IsEffectivelyBlack(rawShadowColor)
            ? new Color(0.25f, 0.25f, 0.28f, 1f)
            : new Color(info.ShadowColorR, info.ShadowColorG, info.ShadowColorB, info.ShadowColorA);

        material.SetColor("_ShadowColor", shadowColor);
        material.SetColor("_SunColor", sunColor * sunIntensity);
        material.SetVector("_SunDirection", new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, 0f));
        material.SetColor("_SC2FogColor", new Color(info.CoreFogColorR, info.CoreFogColorG, info.CoreFogColorB, info.CoreFogColorA));
        material.SetVector("_SC2FogAmounts", new Vector4(info.CoreFogAmount1, info.CoreFogAmount2, info.CoreFogAmount3, 0f));
        material.SetVector("_SC2FogDistances", new Vector4(info.CoreFogDist1, info.CoreFogDist2, 0f, 0f));

        if (material.HasProperty("_EnableEnvironmentMapping") && material.GetFloat("_EnableEnvironmentMapping") > 0.5f
            && !string.IsNullOrEmpty(info.EnvironmentMap))
        {
            var cube = LoadTexturePublicAny(info.EnvironmentMap) as Cubemap;
            if (cube != null) material.SetTexture("_EnvironmentMap", cube);
        }
    }

    /// <summary>Refreshes every real imported-terrain.bdf object's SC2/Terrain
    /// material(s) with the current InfoModel's Sun/Shadow/Fog/EnvironmentMap
    /// values — mirrors SC2WaterPlane.BindImportedWater's exact "find each
    /// imported object's own material by shader name, ApplyTo it every tick"
    /// pattern, just for SC2/Terrain instead of SC2/Water. Live-painted/Pairs-
    /// mode terrain uses a completely different shader (Hidden/SC2/TerrainView,
    /// SC2TerrainRuntime's own shared chunk material) and never reaches this —
    /// Pairs' own State/ShaderFlags/TextureScroll fields are pure export-time
    /// metadata for the resulting terrain.bdf, not live-rendering input.</summary>
    public static void RefreshImportedTerrainEnvironment(System.Collections.Generic.List<SC2ImportedTerrainExportData> importedTerrains, InfoModel info)
    {
        if (importedTerrains == null || info == null) return;
        foreach (var imported in importedTerrains)
        {
            var go = imported.Transform != null ? imported.Transform.gameObject : null;
            var renderer = go != null ? go.GetComponent<MeshRenderer>() : null;
            if (renderer == null) continue;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || mat.shader == null || mat.shader.name != "SC2/Terrain") continue;
                ApplyEnvironment(mat, info);
            }
        }
    }

    /// <summary>
    /// Resolves a shader by its ShaderLab name, guaranteed to work in a
    /// standalone build, not just the Editor.
    ///
    /// The bug this fixes: SC2Terrain.shader/SC2Water.shader used to live
    /// under Shaders/ (not a Resources/ folder), loaded only via
    /// Shader.Find("SC2/Terrain" or "SC2/Water"). In the Editor, Shader.Find
    /// can locate any shader compiled into the project, so that "worked"
    /// there — but a build strips any shader nothing references (no
    /// material/scene asset uses it, since these materials are only ever
    /// created procedurally at runtime via `new Material(shader)`), so
    /// Shader.Find returned null and `new Material(null)` crashed with
    /// "Value cannot be null. Parameter name: shader" — exactly the error
    /// seen importing a terrain.bdf in a build (see SC2FallbackShader's doc
    /// comment for the same failure mode elsewhere in this codebase). Both
    /// shaders now live under Resources/, which Unity always includes in a
    /// build regardless of references, so Resources.Load finds them
    /// reliably; Shader.Find and SC2FallbackShader stay as fallbacks.
    /// </summary>
    public static Shader ResolveShader(string shaderName, string resourcesName)
    {
        var shader = Resources.Load<Shader>(resourcesName) ?? Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError($"[SC2Terrain] Shader '{shaderName}' not found (neither Resources/{resourcesName} nor Shader.Find) — using fallback.");
            shader = SC2FallbackShader.Get();
        }
        return shader;
    }

    /// <summary>
    /// Normalizes a path from terrain.bdf:
    ///  '/Textures\\\\Name.dds' -> 'Textures/Name.dds'
    ///  '/maps/.../lighting/mask01.dds' -> 'maps/.../lighting/mask01.dds'
    /// </summary>
    public static string NormalizePath(string bdfPath)
    {
        // SC2 paths contain a single '\' (U+005C): '/Textures\Name.dds'
        // char.Replace reliably replaces every '\' in the string.
        return bdfPath.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Strips the extension (.dds) and the .win suffix if present:
    /// 'lighting/mask01.dds' -> 'lighting/mask01'
    /// 'lighting/mask01.win.dds' -> 'lighting/mask01'
    /// </summary>
    public static string StripExtension(string path)
    {
        // Strip .dds
        if (path.EndsWith(".dds", System.StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        // Strip .win if it's still there
        if (path.EndsWith(".win", System.StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return path;
    }

    /// <summary>Real corpus survey (56 vanilla maps) confirmed all 12
    /// ShaderSetup flag names and no others — REFLECT/CHROMATIC_DISPERSION
    /// are the only 2 with no visible effect in either terrain.hlsl or
    /// global_constants.hlsl (both read in full), so they're intentionally
    /// NOT wired to any _EnableXxx property here; everything else now has
    /// real shader behavior. ALPHA_TEST's stored Value is a real numeric
    /// threshold (observed 4..128 in the corpus), not just presence.</summary>
    /// <summary>Public so background terrain's own live preview material
    /// builder (SC2BackgroundTerrain.CreateLivePairMaterial) can apply a
    /// pair's ShaderFlags the same way real import-time material creation
    /// already does — same reasoning as ApplyBlendState above.</summary>
    public static void ApplyShaderSetup(Material material, System.Collections.Generic.List<ShaderSetupEntry> shaderSetup)
    {
        material.SetFloat("_EnableRNM", 0);
        material.SetFloat("_EnableAmbient", 0);
        material.SetFloat("_EnableDiffuseLighting", 0);
        material.SetFloat("_EnableSpecularLighting", 0);
        material.SetFloat("_EnableBlending", 0);
        material.SetFloat("_EnableFog", 0);
        material.SetFloat("_EnableGlow", 0);
        material.SetFloat("_EnableEnvironmentMapping", 0);
        material.SetFloat("_EnableDynamicShadows", 0);
        material.SetFloat("_EnableAlphaTest", 0);
        material.SetFloat("_EnableInfiniteDistance", 0);

        foreach (var entry in shaderSetup)
        {
            switch (entry.Name)
            {
                case "RNM_LIGHTING": material.SetFloat("_EnableRNM", 1); break;
                case "AMBIENT_LIGHTING": material.SetFloat("_EnableAmbient", 1); break;
                case "DIFFUSE_LIGHTING": material.SetFloat("_EnableDiffuseLighting", 1); break;
                case "SPECULAR_LIGHTING": material.SetFloat("_EnableSpecularLighting", 1); break;
                case "BLENDING": material.SetFloat("_EnableBlending", 1); break;
                case "FOG": material.SetFloat("_EnableFog", 1); break;
                case "GLOW": material.SetFloat("_EnableGlow", 1); break;
                case "ENVIRONMENT_MAPPING": material.SetFloat("_EnableEnvironmentMapping", 1); break;
                case "DYNAMIC_SHADOWS": material.SetFloat("_EnableDynamicShadows", 1); break;
                case "INFINITE_DISTANCE": material.SetFloat("_EnableInfiniteDistance", 1); break;
                case "ALPHA_TEST":
                    material.SetFloat("_EnableAlphaTest", 1);
                    if (float.TryParse(entry.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold))
                        material.SetFloat("_AlphaTestThreshold", threshold);
                    break;
                // REFLECT/CHROMATIC_DISPERSION: real files confirm neither is
                // read by terrain.hlsl/global_constants.hlsl — round-tripped
                // on export (SC2TerrainExporter) but intentionally no shader
                // property here.
            }
        }
    }

    private static void TrySetTexture(Material material, string property, string? path, bool optional = false, bool isLinear = false, bool disableMipmaps = false)
    {
        if (path is null) return;
        var tex = LoadTexture(path, optional, isLinear, disableMipmaps: disableMipmaps);
        // HasProperty guard: a material's active SC2/Terrain shader variant
        // doesn't always declare every texture slot (e.g. "_Ramp" is only
        // meaningful for metal/foam materials — see the tex[8] comment at
        // this method's caller) — calling SetTexture for a property the
        // shader genuinely doesn't expose is a harmless no-op, but Unity
        // logs a warning for it every single time regardless. Confirmed in
        // the field: a real play session's Player.log had 25 000+ of these
        // for two ordinary ground/crystal materials that simply have no
        // real _Ramp texture, from repeated terrain-material rebuilds.
        if (tex != null && material.HasProperty(property)) material.SetTexture(property, tex);
    }

    /// <summary>Public entry point for the same multi-root texture search
    /// (Assets/SC2Textures in the Editor, Resources/SC2Textures in a Build —
    /// see LoadTextureCore) other factories in this codebase need too, e.g.
    /// SC2WaterMaterialFactory: a real map .scd import already copies
    /// referenced textures (including water's foam/wave/depth .dds files)
    /// into this same root via ScdImportWindow.CopyTexturesFromScd, so a
    /// from-scratch map that happens to share texture paths with an
    /// already-imported map (or once game-archive loading is wired up
    /// fully) can genuinely resolve real textures here, not just fail
    /// every time. optional=true (the default here, unlike LoadTextureCore's
    /// own default) suppresses the "not found" warning — appropriate for
    /// any caller building a best-effort preview rather than a real,
    /// expected-to-exist export texture.</summary>
    public static Texture2D? LoadTexturePublic(string? bdfPath, bool optional = true) =>
        string.IsNullOrEmpty(bdfPath) ? null : LoadTexture(bdfPath, optional);

    /// <summary>Loads numeric texture data without sRGB conversion or Unity's
    /// Normal Map channel swizzle. SC2 water wave maps are sampled as raw RGB
    /// by water.hlsl, despite their normal-map semantics.</summary>
    public static Texture2D? LoadTexturePublicRawData(string? bdfPath, bool optional = true) =>
        string.IsNullOrEmpty(bdfPath) ? null : LoadTexture(bdfPath, optional, isLinear: true, forceDefaultType: true);

    /// <summary>Resolves a RESOURCE PATH string — i.e. exactly what
    /// TryGetResourcePath/RegisterResourcePath hand back for an already-
    /// loaded texture ("file:&lt;absolute path&gt;", "archive:&lt;entry
    /// path&gt;", or a bare Resources-relative path) — back to a live
    /// Texture. NOT the same input shape as LoadTexturePublic/
    /// LoadTexturePublicAny, which take a plain BDF-relative path
    /// ("/textures/Name.dds") and search multiple roots for it; this is the
    /// counterpart for round-tripping an already-resolved source tag.
    ///
    /// Found via a real Player.log while diagnosing an
    /// Environment-texture bug: SC2GameViewEditorUI.RestoreImportedTerrains
    /// FromDocument used to pass a saved resource-path string (captured by
    /// SyncImportedTerrainsToDocument via TryGetResourcePath, so genuinely
    /// "archive:..." for any archive-sourced imported-terrain texture)
    /// straight into LoadTexturePublicAny — which treats its argument as a
    /// plain path and searches for a literal file/entry named
    /// "archive:textures/...", which of course never exists. Silently failed
    /// every time for any material texture resolved from a configured game
    /// archive, restoring nothing (falling back to whatever the fresh
    /// terrain.bdf re-parse already bound, which is usually fine, but any
    /// genuine manual override made in this session before saving was
    /// silently dropped on the next load).</summary>
    public static Texture? ResolveFromResourcePath(string? resourcePath, bool isLinear = false, bool disableMipmaps = false)
    {
        if (string.IsNullOrEmpty(resourcePath)) return null;
        if (resourcePath.StartsWith("file:", System.StringComparison.Ordinal))
        {
            var filePath = resourcePath.Substring(5);
            // TryLoad (not TryLoadAny — that overload has no linear/mip
            // control) — "file:" resource paths are only ever custom flat
            // textures (SC2CustomTextureImporter.Import's own convention),
            // never cube/volume, so Texture2D is the right shape here.
            if (SC2CustomTextureImporter.TryLoad(filePath, out Texture2D tex, out _, isLinear, disableMipmaps))
            {
                RegisterResourcePath(tex, resourcePath);
                return tex;
            }
            return null;
        }
        if (resourcePath.StartsWith("archive:", System.StringComparison.Ordinal))
        {
            if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null;
            var entryPath = resourcePath.Substring("archive:".Length);
            if (SC2CustomTextureImporter.TryLoadFromArchive(reader, entryPath, out var tex, out _, isLinear, disableMipmaps))
            {
                RegisterResourcePath(tex, resourcePath);
                return tex;
            }
            return null;
        }
        // Backward-compatible fallback for saves made before this source-
        // tagged form existed (a bare Resources-relative path, or a plain
        // BDF-relative path an older LoadTexturePublicAny call already
        // resolved and saved verbatim). Resources.Load can't apply
        // isLinear/disableMipmaps after the fact — matches this branch's
        // pre-existing behavior, unaffected by this method's new params.
        var resources = Resources.Load<Texture>(resourcePath);
        if (resources != null) { RegisterResourcePath(resources, resourcePath); return resources; }
        return LoadTexturePublicAny(resourcePath);
    }

    /// <summary>Like LoadTexturePublic, but also resolves Cubemap/Texture3D
    /// assets — needed for info.bdf's EnvironmentMap/SkyMap/
    /// ColorCorrectionCube/FogOfWarCorrectionCube fields (see
    /// SC2InfoModelFieldDrawer.DrawTexturesTab), which LoadTexturePublic can
    /// never resolve since every one of its lookups is hard-typed to
    /// Texture2D (AssetDatabase.LoadAssetAtPath&lt;Texture2D&gt;/
    /// Resources.Load&lt;Texture2D&gt;) — confirmed the real bug behind
    /// "textures aren't auto-assigned on info.bdf import," not a `.win`
    /// suffix issue specifically (that part was already handled correctly).
    /// Reuses the exact same candidate-path generation (.win/case variants,
    /// both search roots) LoadTexturePublic's own resolution already relies
    /// on, then falls back to the vanilla texture catalog (matched by leaf
    /// filename) for cube/volume files Unity's own DDS importer can't
    /// import as any asset type at all — see
    /// SC2TextureCatalogBuilder's raw-filesystem fallback scan.</summary>
    public static Texture? LoadTexturePublicAny(string? bdfPath, bool optional = true)
    {
        if (string.IsNullOrEmpty(bdfPath)) return null;

        var flat = LoadTexturePublic(bdfPath, true);
        if (flat != null) return flat;

        var normalized = NormalizePath(bdfPath);
        var basePath = StripExtension(normalized);
        var basePaths = new[] { basePath, CapitalizeFirstSegment(basePath) };
        var candidates = basePaths.SelectMany(bp => new[] { bp, bp + ".win" }).ToArray();

#if UNITY_EDITOR
        foreach (var root in new[] { TextureRoot, "Assets" })
        foreach (var candidate in candidates)
        {
            var assetPath = $"{root}/{candidate}.dds";
            var cube = UnityEditor.AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
            if (cube != null) { RegisterResourcePath(cube, assetPath); return cube; }
            var volume = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);
            if (volume != null) { RegisterResourcePath(volume, assetPath); return volume; }
        }
#endif
        foreach (var candidate in candidates)
        {
            var resourcePath = $"{ResourcesTextureRoot}/{candidate}";
            var cube = Resources.Load<Cubemap>(resourcePath);
            if (cube != null) { RegisterResourcePath(cube, resourcePath); return cube; }
            var volume = Resources.Load<Texture3D>(resourcePath);
            if (volume != null) { RegisterResourcePath(volume, resourcePath); return volume; }
        }

        // Environment's cubemap/volume fields
        // (EnvironmentMap/SkyMap/ColorCorrectionCube/FogOfWarCorrectionCube)
        // showed no preview even with a configured game install, while
        // Imported terrain's flat Diffuse/Normal textures resolved fine from
        // the exact same archive — the asymmetry: flat resolution
        // (LoadTextureCore/TryLoadFromGameArchive) tries the configured
        // archive directly by path; this function only ever fell through to
        // the pre-built SC2TextureCatalog's leaf-name match, which never
        // gets populated for engine textures the catalog scan didn't cover.
        // Mirrors TryLoadFromGameArchive's own candidate generation, just
        // keeping a Cubemap/Texture3D result instead of discarding it.
        var archiveCubeOrVolume = TryLoadCubeOrVolumeFromGameArchive(basePath);
        if (archiveCubeOrVolume != null) return archiveCubeOrVolume;

        // Substring match (not just exact-or-"+.win"), in both directions —
        // info.bdf's own stored path convention for these fields isn't
        // fully pinned down (e.g. it may omit a "ccc"/"win"-style infix a
        // real file's leaf name carries, or vice versa), so a lenient
        // "one leaf contains the other" check is more robust than requiring
        // an exact match while still being scoped to cube/volume entries
        // only (never matches an unrelated flat texture).
        var leaf = SC2.MapEditor.Textures.SC2TextureCatalogEntry.Leaf(basePath).ToLowerInvariant();
        foreach (var entry in SC2TextureCatalog.Instance.Entries)
        {
            if (!entry.IsCubemap && !entry.IsVolume) continue;
            if (string.IsNullOrEmpty(leaf)) continue;
            var entryLeaf = (entry.Name ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(entryLeaf)) continue;
            if (entryLeaf != leaf && !entryLeaf.Contains(leaf) && !leaf.Contains(entryLeaf)) continue;
            if (!string.IsNullOrEmpty(entry.GamePath))
            {
                if (!SC2CustomTextureImporter.TryLoadAny(entry.GamePath, out var any, out _)) continue;
                RegisterResourcePath(any, !string.IsNullOrEmpty(entry.ResourcePath) ? entry.ResourcePath : "file:" + entry.GamePath);
                return any;
            }
            if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && SC2GameArchiveRegistry.TryGetReader(out var archiveReader))
            {
                if (!SC2CustomTextureImporter.TryLoadFromArchive(archiveReader, entry.ArchiveEntryPath, out var archiveTex, out _)) continue;
                RegisterResourcePath(archiveTex, "archive:" + entry.ArchiveEntryPath);
                return archiveTex;
            }
        }

        if (!optional)
            Debug.LogWarning($"[SC2Terrain] cubemap/volume texture not found: '{normalized}'.");
        return null;
    }

    private static void TrySetNormal(Material material, string property, string? path)
    {
        if (path is null) return;
        var tex = LoadTexture(path, optional: false, isLinear: true);
        if (tex != null && material.HasProperty(property)) material.SetTexture(property, tex);
    }

    /// <summary>
    /// Loads a texture by its terrain.bdf path, recording the result into
    /// _captureBuffer when BeginDiagnosticCapture is active — the actual
    /// search logic lives in LoadTextureCore, to avoid touching its many
    /// return paths.
    /// </summary>
    private static Texture2D? LoadTexture(
        string bdfPath,
        bool optional = false,
        bool isLinear = false,
        bool forceDefaultType = false,
        bool disableMipmaps = false)
    {
        var tex = LoadTextureCore(bdfPath, optional, isLinear, forceDefaultType, disableMipmaps);
        if (_captureBuffer != null && !string.IsNullOrEmpty(bdfPath))
        {
            // Prefer the real resolved path (StreamingAssets/Resources/
            // AssetDatabase — whichever actually matched), not just
            // tex.name: this project's own texture library has thousands of
            // leaf-name collisions across different subfolders (numbered
            // sets like "1_d"/"2_d" repeat under many different material
            // folders, confirmed genuinely different content) — tex.name
            // alone can't tell two same-named-but-different files apart, so
            // it was never actually useful for confirming "which real file
            // did this resolve to."
            var resolvedFrom = tex != null ? (TryGetResourcePath(tex) ?? tex.name) : null;
            _captureBuffer.Add(new TextureLoadAttempt
            {
                RequestedPath = bdfPath,
                Found = tex != null,
                ResolvedFrom = resolvedFrom
            });
        }
        return tex;
    }

    /// <summary>LoadTextureCore result cache, keyed by the normalized bdf
    /// path (e.g. "textures/Terrain/Water/waves_001.dds"). Callers that
    /// rebuild a material far more often than its inputs actually change
    /// (water preview, terrain material factory) used to pay the FULL
    /// candidate-path search — and, in a Build, a fresh Resources.Load +
    /// "texture loaded" log line — on every single call, even for
    /// texture paths that are constant literals never depending on
    /// map/environment state (e.g. water's foam/wave textures). Confirmed
    /// in the field: a single real play session's Player.log showed each of
    /// the 7 water textures individually re-resolved and re-logged over
    /// 550 000 times, ballooning the log past 800MB. A "resolved once, read
    /// many" cache here — not per-texture like _resourcePathByTexture's
    /// reverse mapping above, but per-input-path — makes every repeat
    /// resolution O(1) and log-free regardless of call frequency. Failed
    /// lookups are cached too (null-valued entries), via TryGetValue's
    /// distinction between "key present with null value" and "key absent",
    /// so an optional texture that's persistently missing doesn't re-walk
    /// every candidate/extension on every call either.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _loadedTextureCache = new();

    /// <summary>
    /// Loads a texture by its terrain.bdf path.
    /// <paramref name="bdfPath"/> can be:
    ///  - with an extension: 'Textures/Name.dds' -> looks for Name.win.dds and Name.dds
    ///  - without an extension (RNM masks): 'maps/.../lighting/mask01_0' -> looks for mask01_0.win.dds and mask01_0.dds
    /// </summary>
    private static Texture2D? LoadTextureCore(
        string bdfPath,
        bool optional = false,
        bool isLinear = false,
        bool forceDefaultType = false,
        bool disableMipmaps = false)
    {
        if (string.IsNullOrEmpty(bdfPath)) return null;

        var normalized = NormalizePath(bdfPath); // 'Textures/Name.dds' or 'textures/Engine/Name.dds'
        var cacheKey = $"{normalized}|linear:{isLinear}|default:{forceDefaultType}|mip:{disableMipmaps}";
        if (_loadedTextureCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var resolved = LoadTextureUncached(normalized, optional, isLinear, forceDefaultType, disableMipmaps);
        _loadedTextureCache[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>isLinear: true for normal maps and RNM basis/blend masks —
    /// raw vector/blend data, not color. Confirmed the real root cause of
    /// imported terrain rendering as full-frame RGB static/noise: these
    /// files are freshly copied into the project by SC2TerrainBdfImporter's
    /// plain File.Copy (no AssetPostprocessor anywhere in this codebase to
    /// override it), so they land with Unity's *default* sRGB="Color
    /// Texture" import setting — gamma-decoding raw linear bytes corrupts
    /// every value, and RNM's basis-blend shader math turns that into
    /// per-pixel noise rather than a subtle tint shift. EnsureLinearImport
    /// (Editor-only, matching this whole auto-copy workflow's existing
    /// Editor-only-effective scope) forces the correct setting the first
    /// time each such asset is resolved.</summary>
    private static Texture2D? LoadTextureUncached(
        string normalized,
        bool optional,
        bool isLinear = false,
        bool forceDefaultType = false,
        bool disableMipmaps = false)
    {
        var basePath = StripExtension(normalized); // 'Textures/Name'

        // Tried FIRST, ahead of every AssetDatabase/Resources.Load path
        // below: Unity's own native DDS importer is confirmed (via its own
        // Texture Inspector mip-level preview on a real vanilla non-power-
        // of-two compressed texture) to decode embedded mip levels below
        // mip 0 as pure block noise for this class of real vanilla asset —
        // not fixable via any TextureImporter setting found so far. This
        // reads the exact same real .dds bytes via this project's own
        // decoder (SC2CustomTextureImporter, already proven correct for
        // per-level mip byte layout) from a real loose-file copy under
        // StreamingAssets, which — unlike Resources/, packed into the
        // Player's data files at build time — stays File.ReadAllBytes-
        // accessible both in the Editor and in a real Standalone Build.
        // Falls through to the existing Unity-native paths below only when
        // no StreamingAssets copy exists yet (e.g. before a re-copy/re-
        // import picks it up).
        var streamingTex = TryLoadFromStreamingAssets(basePath, isLinear, forceDefaultType, disableMipmaps);
        if (streamingTex != null) return streamingTex;

#if UNITY_EDITOR
        // Iterate extensions and first-folder case variants.
        var extensions = new[] { ".win.dds", ".dds", ".png", ".tga" };

        // Search roots:
        // 1. SC2TerrainMaterialFactory.TextureRoot — the standard one (for imported maps)
        // 2. "Assets" — for masks generated alongside SC2MapRawData
        //    (a path like /maps/<name>/lighting/mask_a_b -> Assets/maps/<name>/lighting/...)
        var searchRoots = new[] { TextureRoot, "Assets" };
        var basePaths   = new[] { basePath, CapitalizeFirstSegment(basePath) };

        foreach (var root in searchRoots)
        foreach (var bp in basePaths)
        foreach (var ext in extensions)
        {
            var assetPath = $"{root}/{bp}{ext}";
            var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex != null)
            {
                // A reimport (if EnsureLinearImport actually changed the
                // setting) invalidates the Texture2D object just loaded
                // above — Unity creates a new instance — so re-fetch rather
                // than returning the now-stale reference.
                if ((isLinear || disableMipmaps) && EnsureLinearImport(assetPath, forceDefaultType, disableMipmaps))
                    tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                return tex;
            }
        }

        // Fallback: textures auto-copied by SC2TerrainBdfImporter land under
        // Resources/{ResourcesTextureRoot}/... (see its
        // EnsureTexturesAvailable), not under a plain "Assets/SC2Textures"
        // the AssetDatabase search above looks at — Resources.Load works in
        // the Editor too, so this picks those up without needing a third
        // AssetDatabase search root.
        //
        // Resources.Load only strips the FINAL extension off a file name —
        // for a real file named "CA_U03_blueice_001_d.win.dds" its Resources
        // key is "CA_U03_blueice_001_d.win", not "CA_U03_blueice_001_d".
        // basePath already has BOTH ".dds" and ".win" stripped
        // (StripExtension), so try it both with and without ".win" re-added.
        // Also needs the same case-variant handling as the AssetDatabase
        // search just above (Resources.Load's internal lookup is
        // case-sensitive regardless of the OS filesystem) — confirmed via
        // the Texture Browser/SC2TextureCatalog resolving a real water
        // texture at "SC2Textures/Textures/Terrain/Water/foam_001.win"
        // (capital T) while WaterTexturePaths' bdf-convention paths are
        // lowercase ("/textures/Terrain/Water/foam_001.dds") — this branch
        // previously only tried the literal lowercase basePath and so never
        // matched real water textures at all.
        // This is the branch SC2TerrainBdfImporter's auto-copied textures
        // (including every Normal/RNM-mask file for an imported terrain.bdf)
        // actually resolve through in practice — the AssetDatabase loop
        // above searches TextureRoot ("Assets/SC2Textures"), but
        // EnsureTexturesAvailable copies into "Assets/SC2/Resources/
        // SC2Textures" instead, so this Resources.Load fallback is where
        // the isLinear fix needs to land for that real workflow.
        var resourceCandidates = basePaths.SelectMany(bp => new[] { bp, bp + ".win" }).ToArray();
        foreach (var candidate in resourceCandidates)
        {
            var resourcePath = $"{ResourcesTextureRoot}/{candidate}";
            var resourceFallback = Resources.Load<Texture2D>(resourcePath);
            if (resourceFallback != null)
            {
                if (isLinear || disableMipmaps)
                {
                    var assetPath = UnityEditor.AssetDatabase.GetAssetPath(resourceFallback);
                    if (!string.IsNullOrEmpty(assetPath) && EnsureLinearImport(assetPath, forceDefaultType, disableMipmaps))
                        resourceFallback = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                }
                _resourcePathByTexture[resourceFallback] = resourcePath;
                return resourceFallback;
            }
        }

        // Last resort, before giving up: a map opened straight from a
        // configured game install's archives (SC2GameArchiveRegistry) never
        // gets its textures copied into Assets/StreamingAssets at all (the
        // whole point of the archive-first pipeline) — resolve directly
        // from the archive bytes in memory instead.
        var archiveTex = TryLoadFromGameArchive(basePath, isLinear, disableMipmaps);
        if (archiveTex != null) return archiveTex;

        if (!optional)
        {
            var triedList = string.Join(", ", searchRoots.SelectMany(root => basePaths.SelectMany(bp => extensions.Select(ext => $"{root}/{bp}{ext}"))));
            var resourceTriedList = string.Join(", ", resourceCandidates.Select(c => $"Resources.Load(\"{ResourcesTextureRoot}/{c}\")"));
            Debug.LogWarning($"[SC2Terrain] texture not found: '{normalized}'. Tried paths: {triedList}, " +
                              $"also {resourceTriedList}, also the configured game install (SC2GameArchiveRegistry).");
        }
        return null;
#else
        // Resources.Load paths are relative to whatever folder is literally
        // named "Resources" anywhere under Assets — they must NOT include
        // "Assets/" themselves (that's an AssetDatabase-path convention, used
        // only by TextureRoot/the Editor branch above — mixing the two was
        // the actual bug here: a project with textures physically at
        // ".../Resources/SC2Textures/..." never matched because this branch
        // used to build "Assets/SC2Textures/..." — an extra, wrong "Assets/"
        // segment). ResourcesTextureRoot ("SC2Textures", no "Assets/" prefix)
        // is the correct root for every Resources.Load call.
        //
        // Two more variations, both confirmed needed against a real project's
        // texture library: (1) try both the path as-is and with its first
        // segment capitalized ('textures/Engine/...' vs 'Textures/Engine/...'
        // — Resources.Load doesn't normalize case); (2) Resources.Load only
        // strips the FINAL extension off a file name, not a whole
        // ".win.dds" suffix — a real file named
        // "CA_U03_blueice_001_d.win.dds" has Resources key
        // "CA_U03_blueice_001_d.win", not "CA_U03_blueice_001_d". basePath
        // already has both ".dds" and ".win" stripped (StripExtension), so
        // every case variant is tried both with and without ".win" re-added.
        var basePaths = new[] { basePath, CapitalizeFirstSegment(basePath) };
        var candidates = basePaths.SelectMany(bp => new[] { bp, bp + ".win" }).ToArray();
        Texture2D tex = null;
        string loadedFrom = null;
        foreach (var candidate in candidates)
        {
            var resourcePath = $"{ResourcesTextureRoot}/{candidate}";
            tex = Resources.Load<Texture2D>(resourcePath);
            if (tex != null) { loadedFrom = resourcePath; break; }
        }

        if (tex == null)
        {
            // Same last-resort archive fallback as the Editor branch above —
            // a map opened straight from a configured game install's
            // archives never gets its textures copied into Resources at
            // all, so this is the only path that can ever resolve them in
            // a real Build.
            tex = TryLoadFromGameArchive(basePath, isLinear, disableMipmaps);
            if (tex != null) loadedFrom = TryGetResourcePath(tex) ?? "archive";
        }

        if (tex != null)
        {
            _resourcePathByTexture[tex] = loadedFrom;
            Debug.Log($"[SC2Terrain] texture loaded: '{normalized}' <- Resources.Load(\"{loadedFrom}\") " +
                      $"(physically: .../Resources/{loadedFrom}.<ext>)");
        }
        else if (!optional)
        {
            var triedList = string.Join(", ", candidates.Select(c => $"Resources.Load(\"{ResourcesTextureRoot}/{c}\")"));
            Debug.LogWarning($"[SC2Terrain] texture not found: '{normalized}'. Tried: {triedList} -> null, " +
                              $"also the configured game install (SC2GameArchiveRegistry). " +
                              $"The file must be at '.../Resources/{ResourcesTextureRoot}/{basePath}[.win].<ext>' " +
                              $"(any folder named 'Resources' anywhere under Assets, " +
                              $"then exactly this relative path and extension .dds/.png/.tga).");
        }
        return tex;
#endif
    }

    /// <summary>Resolves basePath ("Textures/Name", already extension- and
    /// case-variant-expanded by the caller) directly against
    /// SC2GameArchiveRegistry's configured game install — the last resort
    /// once every Assets/StreamingAssets/Resources path above has failed.
    /// A map opened straight from a configured game install's archives
    /// (Import Map's "vanilla/archive map" flow) never gets its textures
    /// physically copied anywhere at all, so this is the only way those
    /// textures ever resolve. No cache-key collision risk from adding this:
    /// the registry is a single process-wide reader for the one currently
    /// configured install, so a given normalized path always resolves to
    /// the same entry regardless of which caller asks — same guarantee
    /// _loadedTextureCache already relies on for every other branch here.</summary>
    private static Texture2D TryLoadFromGameArchive(string basePath, bool isLinear, bool disableMipmaps = false)
    {
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null;

        var basePaths = new[] { basePath, CapitalizeFirstSegment(basePath) };
        var extensions = new[] { ".win.dds", ".dds", ".png", ".tga" };
        foreach (var bp in basePaths)
        foreach (var ext in extensions)
        {
            var entryPath = $"{bp}{ext}";
            if (!SC2CustomTextureImporter.TryLoadFromArchive(reader, entryPath, out var texture, out _, isLinear, disableMipmaps))
                continue;
            if (texture is Texture2D tex2D)
            {
                _resourcePathByTexture[tex2D] = "archive:" + entryPath;
                return tex2D;
            }
            // Terrain-material texture slots (diffuse/normal/mask/RNM) are
            // never cubemaps/volumes — an archive entry resolving to one at
            // this path would mean something else entirely collided with
            // it; discard rather than return a wrong-shaped asset.
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
        return null;
    }

    /// <summary>Cubemap/Texture3D counterpart to TryLoadFromGameArchive above
    /// — same configured-archive lookup by exact candidate path, just
    /// keeping a Cubemap/Texture3D result (discarding a flat Texture2D
    /// instead, the inverse of that method's own filter) since
    /// LoadTexturePublicAny already tried flat resolution first. Used only
    /// by LoadTexturePublicAny (info.bdf's EnvironmentMap/SkyMap/
    /// ColorCorrectionCube/FogOfWarCorrectionCube) — without this, those
    /// fields could only ever resolve via the pre-built SC2TextureCatalog's
    /// leaf-name match, which never gets populated for engine textures the
    /// catalog scan didn't specifically cover, even when the exact same
    /// configured game archive genuinely contains the file.</summary>
    private static Texture TryLoadCubeOrVolumeFromGameArchive(string basePath)
    {
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null;

        var basePaths = new[] { basePath, CapitalizeFirstSegment(basePath) };
        var extensions = new[] { ".win.dds", ".dds" };
        foreach (var bp in basePaths)
        foreach (var ext in extensions)
        {
            var entryPath = $"{bp}{ext}";
            if (!SC2CustomTextureImporter.TryLoadFromArchive(reader, entryPath, out var texture, out _))
                continue;
            if (texture is Cubemap || texture is Texture3D)
            {
                // No SC2TextureCatalogEntry exists for this file (that's the
                // whole point of this bypass) — SC2TextureBrowser.DrawPreview
                // builds a preview for it lazily, on demand, the first time
                // this exact texture instance is actually drawn (see its own
                // doc comment for why eager preview generation here caused a
                // real reported bug: SC2TerrainMaterialFactory.Create also
                // resolves EnvironmentMap once per material via this same
                // function, on every rebuild — thousands of short-lived
                // instances that aren't ever drawn in any picker box, which
                // thrashed a small eager cache and evicted the one instance
                // that actually needed its preview kept).
                RegisterResourcePath(texture, "archive:" + entryPath);
                return texture;
            }
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
        return null;
    }

    /// <summary>Reads a real .dds file directly off disk from
    /// StreamingAssets and decodes it via SC2CustomTextureImporter — this
    /// project's own decoder, not Unity's native DDS importer. Identical
    /// code path in the Editor and in a real Standalone Build, since both
    /// read the same real loose files under Application.streamingAssetsPath
    /// (unlike Resources/, which only exists as loose files in the Editor).
    /// Returns null (never logs/warns) whenever no StreamingAssets copy
    /// exists yet, or the candidate isn't a confirmed-NPOT .dds file, so
    /// callers fall through to the existing AssetDatabase/Resources.Load
    /// paths unaffected.
    ///
    /// DELIBERATELY NARROW SCOPE (this is the second attempt at this bypass
    /// — the first, reverted attempt routed EVERY texture with a
    /// StreamingAssets copy through this decoder unconditionally, which
    /// introduced an unrelated hard-edge intra-material blend regression on
    /// files that were never broken to begin with, e.g. playsurface_1.win.dds
    /// / RNM Mask 1 for mat_cliff01_g01 — 1024x1024, already power-of-two,
    /// 1 mip level, imports fine natively). Two targeted attempts to make
    /// this decoder behave identically to Unity's native import for those
    /// unrelated files (explicit filterMode/wrapMode; always-generate mip
    /// chain) both failed to fix the regression, meaning some other, still-
    /// unknown difference remains. Rather than keep guessing at that
    /// difference, this version narrows the decoder's blast radius to
    /// EXACTLY the confirmed-broken case — real vanilla NPOT DDS files with
    /// deep mip chains (dirt_01_d, sand_01_d) — via a cheap header-only
    /// power-of-two check (SC2CustomTextureImporter.IsNpotDds) before ever
    /// reading/decoding the full file. Every POT DDS and every non-DDS
    /// format (.png/.tga — Unity already imports those correctly) keeps
    /// using Unity's native import path completely unchanged below, so
    /// nothing that already worked can regress through this code path.</summary>
    private static Texture2D TryLoadFromStreamingAssets(
        string basePath,
        bool isLinear,
        bool forceCustomDecode = false,
        bool disableMipmaps = false)
    {
        var basePaths = new[] { basePath, CapitalizeFirstSegment(basePath) };
        var candidates = basePaths.SelectMany(bp => new[] { bp, bp + ".win" }).ToArray();
        foreach (var candidate in candidates)
        {
            var fullPath = Path.Combine(Application.streamingAssetsPath, StreamingAssetsTextureRoot,
                candidate.Replace('/', Path.DirectorySeparatorChar) + ".dds");
            if (!File.Exists(fullPath)) continue;
            // Most textures stay on Unity's native path unless they are in
            // the confirmed broken NPOT class. Explicit raw-data callers are
            // the exception: Unity may bake POT wave DDS files as Normal Map
            // assets and swizzle their RGB channels, while SC2 water.hlsl
            // samples the original bytes directly. disableMipmaps callers
            // (RNM masks) are NOT added to this gate — the common POT RNM
            // case is deliberately left on Unity's native import path below
            // (fixed there via EnsureLinearImport's mipmapEnabled=false),
            // not routed through this narrowly-scoped decoder, to avoid
            // reintroducing the hard-edge blend regression documented above.
            if (!forceCustomDecode && !SC2CustomTextureImporter.IsNpotDds(fullPath)) continue;
            if (!SC2CustomTextureImporter.TryLoad(fullPath, out var tex, out _, isLinear, disableMipmaps) || tex == null) continue;
            // A Texture2D built via SC2CustomTextureImporter's own decode
            // never has .name set (plain `new Texture2D(...)`, defaults to
            // "") unlike a real AssetDatabase-imported asset (named after
            // its source file automatically) — set it explicitly so
            // TextureLoadAttempt.ResolvedFrom's tex.name fallback is
            // actually meaningful if the resource-path dictionary lookup
            // below ever misses for any reason, instead of silently
            // rendering as an empty string in the diagnostic panel.
            var resourcePath = $"{StreamingAssetsTextureRoot}/{candidate}";
            tex.name = resourcePath;
            _resourcePathByTexture[tex] = resourcePath;
            return tex;
        }
        return null;
    }

#if UNITY_EDITOR
    /// <summary>Forces sRGB off (Linear) for a texture asset that encodes
    /// raw vector/blend data rather than color — normal maps and RNM
    /// basis/blend masks — and, when disableMipmaps is set, also forces
    /// "Generate Mip Maps" off (RNM masks specifically: Unity's default
    /// box-filtered mip chain averages raw basis-vector/blend data
    /// incorrectly, producing visible striping on the real rendered
    /// terrain). Returns true only when it
    /// actually changed and reimported the asset (meaning any already-loaded
    /// Texture2D reference for it is now stale and must be re-fetched via
    /// LoadAssetAtPath). Editor-only, matching SC2TerrainBdfImporter's own
    /// texture auto-copy workflow, which only takes effect in the
    /// Editor/Play mode to begin with (see that class's doc comment) — a
    /// real Build's Resources content is baked at build time, so this can't
    /// retroactively fix anything there; the source asset must already have
    /// the correct import settings before the project is built (i.e. it must
    /// be resolved at least once in the Editor first).</summary>
    private static bool EnsureLinearImport(string assetPath, bool forceDefaultType = false, bool disableMipmaps = false)
    {
        if (UnityEditor.AssetImporter.GetAtPath(assetPath) is not UnityEditor.TextureImporter importer) return false;
        var changed = false;
        if (importer.sRGBTexture)
        {
            importer.sRGBTexture = false;
            changed = true;
        }
        if (forceDefaultType && importer.textureType != UnityEditor.TextureImporterType.Default)
        {
            importer.textureType = UnityEditor.TextureImporterType.Default;
            changed = true;
        }
        if (disableMipmaps && importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            changed = true;
        }
        if (!changed) return false;
        importer.SaveAndReimport();
        return true;
    }
#endif

    private static string CapitalizeFirstSegment(string path)
    {
        var slash = path.IndexOf('/');
        if (slash <= 0) return path;
        var first = path[..slash];
        var rest  = path[slash..];
        if (first.Length == 0) return path;
        return char.ToUpperInvariant(first[0]) + first[1..] + rest;
    }
}
