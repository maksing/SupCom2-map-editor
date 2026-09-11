// SC2WaterMaterialFactory.cs
//
// Builds a Material("SC2/Water") from a TerrainMaterial("Water") (textures,
// TexturePaths) plus an InfoModel (global constants from info.bdf).
//
// The field->register mapping below was corrected against a real
// disassembly-grounded audit of SC2-NO-ENC.EXE — several of this file's
// previous "best-effort default" guesses turned out to be real, confirmed
// fields that InfoBdf.cs's CURRENT C# property names simply mislabel (the
// parser itself is deliberately left unrenamed pending a separate review —
// so every mismatch between an offset's real meaning and its current C#
// name is called out explicitly at its use site below).
//
// TexturePaths slots for the "Water" material (verified byte-for-byte,
// mat[10]): tex[0]=foam_001 (Diffuse1), tex[1]=foam_002
// (Diffuse2), tex[2..5]=waves_001..004 (Normal1-4), tex[6]=waterDepth
// (Mask1, map-specific), tex[8]=wave_animation_001 (Ramp — not tex[7],
// verified byte-for-byte despite water.hlsl declaring RampMap right after
// Mask1Map — the real slot layout isn't strictly positional).

using UnityEngine;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Info;

public static class SC2WaterMaterialFactory
{
    /// <summary>Original entry point — needs a live SC2Environment
    /// MonoBehaviour (only ever spawned by the legacy Editor/ScdImportWindow.cs
    /// scene workflow). Delegates to the InfoModel overload below, which
    /// needs no scene component at all - prefer that one for anything in
    /// the unified procedural workflow (SC2RnmBakeService's own doc comment
    /// already flags this same "needs a live SC2Environment that doesn't
    /// exist there" gap for RNM baking; the water plane hits
    /// the identical problem and fixes it the same way).</summary>
    public static Material Create(TerrainModel model, SC2Environment env)
    {
        var info = new InfoModel { Platform = env.Platform };
        System.Array.Copy(env.Header, info.Header, System.Math.Min(env.Header.Length, info.Header.Length));
        return Create(model, info);
    }

    /// <summary>Same material, built straight from a parsed/derived
    /// InfoModel (e.g. SC2TerrainExporter.BuildInfoModel(data, session)) -
    /// no live scene MonoBehaviour required.</summary>
    public static Material Create(TerrainModel model, InfoModel info)
    {
        var shader = Shader.Find("SC2/Water");
        var material = new Material(shader) { name = "mat_Water" };
        ApplyTo(material, model, info);
        return material;
    }

    /// <summary>Same property writes as Create, applied to an EXISTING
    /// material instead of allocating a new one — lets a caller that needs
    /// to refresh a live preview material on a real cadence (e.g.
    /// SC2WaterPreview.Ensure, ticked once per frame) update it in place
    /// instead of paying a fresh Material allocation + shader-variant
    /// lookup + DestroyImmediate of the old one on every refresh. All the
    /// property writes below are plain SetTexture/SetColor/SetVector calls,
    /// which are cheap and don't themselves log anything, so calling this
    /// more often than strictly necessary is now harmless (unlike the old
    /// allocate-a-new-Material pattern).</summary>
    public static void ApplyTo(Material material, TerrainModel model, InfoModel info)
    {
        var waterMat = model.Materials.Find(SC2HfieldProjectionService.IsWaterMaterial);
        SC2WaterRenderController.Ensure(info.WaterElevation);

        var hasMask1Tex = false;
        if (waterMat != null)
        {
            TrySetTexture(material, "_Diffuse1Tex", waterMat.TexturePaths[0]); // foam_001
            TrySetTexture(material, "_Diffuse2Tex", waterMat.TexturePaths[1]); // foam_002
            TrySetDataTexture(material, "_Normal1Tex", waterMat.TexturePaths[2]); // waves_001
            TrySetDataTexture(material, "_Normal2Tex", waterMat.TexturePaths[3]); // waves_002
            TrySetDataTexture(material, "_Normal3Tex", waterMat.TexturePaths[4]); // waves_003
            TrySetDataTexture(material, "_Normal4Tex", waterMat.TexturePaths[5]); // waves_004
            hasMask1Tex = TrySetDataTexture(material, "_Mask1Tex", waterMat.TexturePaths[6]); // waterDepth
            TrySetDataTexture(material, "_RampTex",    waterMat.TexturePaths[8]); // wave_animation_001
        }
        // _Mask1Tex ("waterDepth") is a genuinely per-map
        // texture, only shipped by real vanilla maps that actually have
        // real water (27 of 56 in the corpus) - a from-scratch procedural
        // map's synthetic SC2WaterPlane never has one. When the real
        // texture is missing, TrySetDataTexture's own `if (tex != null)`
        // guard silently leaves the property unset, so Unity falls back to
        // the shader's declared default ("white" {} - see SC2Water.shader's
        // own Properties block) uniformly across the whole surface. Since
        // depthScalar is derived entirely from this mask's alpha channel,
        // a uniform white mask collapses depthScalar to one CONSTANT value
        // everywhere instead of a real per-position shore/depth signal -
        // shoreColor (the shore-only foam this session's earlier fix relies
        // on exclusively) then loses its actual spatial shore gating and
        // paints foam uniformly across the whole surface instead, driven
        // only by whatever the Ramp texture's alpha happens to be at that
        // one constant depthScalar — foam kept covering the whole surface
        // even after the open-water whitecap term was already deleted.
        // Fixed by telling the shader explicitly whether it has a
        // REAL depth mask to work with (mirrors the same
        // has-a-real-texture pattern _HasEnvironmentTex used before it was
        // removed) - shoreColor is now gated off entirely when there's no
        // real mask, rather than guessing wrong from a degenerate default.
        //
        // A real, correctly-loaded Mask1Tex still
        // wasn't enough on its own - this also occurs on a real IMPORTED map whose
        // Water material genuinely has a real waterDepth texture. Root
        // cause: _WaterDepthArea (the world-space rect Mask1Tex is projected
        // onto, computed below from info.WaterDepthOriginX/Y/
        // WaterDepthBoundsSizeX/Y) comes from `info` - the ACTIVE terrain's
        // own Environment data (session document, or SC2TerrainData.
        // EnvironmentHeader), NOT from the imported object's own original
        // info.bdf. "Import terrain.bdf" only imports geometry/materials/
        // textures (confirmed by the status bar's own "Textures: 88/88
        // loaded" wording) - it never touches the active Environment tab at
        // all, so a fresh/blank active environment leaves
        // WaterDepthBoundsSizeX/Y at their all-zero default. The shader
        // divides by these directly (1/BoundsSize, matching the real game's
        // own division instruction, per that code's own comment on
        // preserving IEEE-754 semantics) - 1/0 is +Infinity, and
        // saturate((posWS.xz - origin) * Infinity) collapses to a step
        // function of the SIGN of (posWS.xz - origin) - i.e. one single UV
        // corner of the mask sampled for the ENTIRE map regardless of real
        // position, reproducing the same uniform-foam symptom (now driven
        // by whatever's at that one corner of a real, correctly-loaded
        // mask instead of a missing one). Guard
        // _HasMask1Tex on the bounds actually being real/nonzero too, not
        // just the texture being bound - a degenerate 0-sized projection
        // rect is exactly as unusable as no texture at all.
        var hasRealDepthArea = info.WaterDepthBoundsSizeX != 0f && info.WaterDepthBoundsSizeY != 0f;
        material.SetFloat("_HasMask1Tex", hasMask1Tex && hasRealDepthArea ? 1f : 0f);

        // Guard against a degenerate WaterColor.a making the whole plane
        // invisible even with WaterEnabled=1 and a real WaterElevation:
        // SC2Water.shader blends with "Blend SrcAlpha OneMinusSrcAlpha" and
        // uses _WaterColor.a directly as the final fragment alpha, so any
        // alpha <= 0 renders fully transparent — "water disappears" — with
        // nothing else about the setup wrong. Confirmed in the field via
        // SC2WaterPreview.Ensure's diagnostic log: a real session showed
        // waterColorA sitting at 0 (and even -5, a clearly out-of-range
        // value) for many consecutive ticks while every other reported
        // value (elevation, mesh bounds, renderer state) was completely
        // normal. The FIRST version of this guard only caught a fully
        // all-zero RGBA (see this file's own earlier history) — too narrow,
        // since it missed exactly this case where only alpha was degenerate
        // while R/G/B were real, non-zero values from a partially-configured
        // header (e.g. a still-open "Environment defaults are
        // all-zero" gap, or simply an out-of-range value dragged into the
        // Water tab's alpha field, which has no input clamp of its own).
        // The compatibility guard described above is intentionally disabled:
        // the game path preserves all four authored info.bdf components.
        material.SetColor("_WaterColor", new Color(
            info.WaterColorR, info.WaterColorG, info.WaterColorB, info.WaterColorA));
        material.SetFloat("_MaxWaterDepth", info.MaxWaterDepth);

        // Diffuse1/2Movement.xy = WaterDiffuse1/2MovementX/Y (@52/56/60/64).
        // .z (tiling scale) = best-effort: WaterFoamRepeatRateX/Y is a
        // 2-float pair sitting immediately after WaterColor in the byte
        // layout, matching the shape of "one tiling scalar per foam map"
        // the real WaterDiffuse1/2Movement.z registers need (not a single
        // 2D repeat-rate for one map, despite the field's own name).
        material.SetVector("_Diffuse1Movement", new Vector4(info.WaterDiffuse1MovementX, info.WaterDiffuse1MovementY, info.WaterFoamRepeatRateX, 0));
        material.SetVector("_Diffuse2Movement", new Vector4(info.WaterDiffuse2MovementX, info.WaterDiffuse2MovementY, info.WaterFoamRepeatRateY, 0));

        // Normal1-4Movement.xy = confirmed (WaterNormal1-4MovementX/Y).
        // .z (tiling scale) = best-effort, same reasoning as above:
        // WaterNormalRepeatRateX/Y/Z/W (4 floats, immediately following
        // WaterFoamRepeatRate in the byte layout) as one tiling scalar per
        // normal map, matching water.hlsl's 4 independent
        // WaterNormal{1-4}Movement.z registers.
        material.SetVector("_Normal1Movement", new Vector4(info.WaterNormal1MovementX, info.WaterNormal1MovementY, info.WaterNormalRepeatRateX, 0));
        material.SetVector("_Normal2Movement", new Vector4(info.WaterNormal2MovementX, info.WaterNormal2MovementY, info.WaterNormalRepeatRateY, 0));
        material.SetVector("_Normal3Movement", new Vector4(info.WaterNormal3MovementX, info.WaterNormal3MovementY, info.WaterNormalRepeatRateZ, 0));
        material.SetVector("_Normal4Movement", new Vector4(info.WaterNormal4MovementX, info.WaterNormal4MovementY, info.WaterNormalRepeatRateW, 0));

        // Matches CWldTerrainRes @0xA95842..0xA95880 exactly:
        // WaterDepth = (originX, originY, 1 / boundsX, 1 / boundsY).
        // Do not clamp the authored sizes. The game uses direct divss and
        // therefore preserves IEEE-754 infinity/NaN for zero-sized bounds.
        material.SetVector("_WaterDepthArea", new Vector4(
            info.WaterDepthOriginX, info.WaterDepthOriginY,
            1f / info.WaterDepthBoundsSizeX,
            1f / info.WaterDepthBoundsSizeY));

        // x = WaterRefractionScale (@140; currently unused by the shader —
        //   no real screen refraction implemented here).
        // y = WaterReflectionScale (@144).
        // z = WaterDepthScale (@156) — repurposed here as the depth->color
        //   blend factor (see SC2Water.shader's own comment on why real
        //   screen refraction was replaced with a depth lerp).
        // w = WaterChromaticScale (@160; currently unused by the shader —
        //   no per-channel refraction implemented here).
        // Current shader binding: x=refraction UV scale, y=planar reflection
        // offset, z=depth-buffer scale, w=per-channel chromatic offset.
        material.SetVector("_WaterScales", new Vector4(
            info.WaterRefractionScale,
            info.WaterReflectionScale,
            info.WaterDepthScale,
            info.WaterChromaticScale));

        // x = WaterFresnelBias (@148).
        // y = WaterFresnelPower (@152).
        // z = WaterSpecPower (@164).
        // w = WaterBloomFactor (@168).
        material.SetVector("_WaterLighting", new Vector4(
            info.WaterFresnelBias,
            info.WaterFresnelPower,
            info.WaterSpecPower,
            info.WaterBloomFactor));

        // Matches CWldTerrainRes @0xA95888..0xA958B8 exactly:
        // WaterWhiteCap.z = WaterFoamHeightOffset / MaxWaterDepth.
        //
        // WaterWhiteCapCutoff is deliberately authored close to the
        // theoretical max of normalSum.z (sum of 4 real wave-normal maps'
        // raw .b channel, each in [0,1], so max 4.0) - e.g. 3.94 on a real
        // vanilla map, against wave textures whose .b averages ~3.8 -
        // confirmed by direct byte inspection of a real map's textures/
        // info.bdf. This makes "Add whitecap foam"
        // (saturate(normalSum.z - Cutoff) * Brightness) fire only at
        // scattered wave-crest peaks, not across the whole surface. If foam
        // instead blankets the entire water plane, the most likely cause is
        // THIS vector not actually reaching the shader with the real
        // authored Cutoff (e.g. falling back to the shader Properties
        // block's own preview default of 0.5, which saturates almost
        // everywhere against the same texture data) - logged once below so
        // that failure mode is directly visible instead of requiring a live
        // GPU/material inspector session to catch.
        LogVectorOnce(material, "_WaterWhiteCap", info.WaterWhiteCapCutoff, info.WaterWhiteCapBrightness);
        material.SetVector("_WaterWhiteCap", new Vector4(
            info.WaterWhiteCapCutoff,
            info.WaterWhiteCapBrightness,
            info.WaterFoamHeightOffset / info.MaxWaterDepth,
            0f));

        // Matches CWldTerrainRes @0xA958C0..0xA958FF exactly. The second
        // division uses the computed ratio, including its non-finite edge
        // cases; it must not be replaced by an epsilon-safe reciprocal.
        var waveStartFactor = info.WaterWaveStartDepth / info.MaxWaterDepth;
        material.SetVector("_WaterWaves", new Vector4(
            waveStartFactor,
            1f / waveStartFactor,
            info.WaterWaveTiming,
            info.WaterWaveModulus));

        var (sdx, sdy, sdz) = info.SunDirection;
        material.SetVector("_SunDirection", new Vector4(sdx, sdy, sdz, 0));
        var (scr, scg, scb) = info.SunColor;
        material.SetColor("_SunColor", new Color(
            scr * info.SunIntensity,
            scg * info.SunIntensity,
            scb * info.SunIntensity,
            1f));
        material.SetColor("_SC2FogColor", new Color(
            info.CoreFogColorR, info.CoreFogColorG, info.CoreFogColorB, info.CoreFogColorA));
        material.SetVector("_SC2FogAmounts", new Vector4(
            info.CoreFogAmount1, info.CoreFogAmount2, info.CoreFogAmount3, 0f));
        material.SetVector("_SC2FogDistances", new Vector4(
            info.CoreFogDist1,
            SafeReciprocal(info.CoreFogDist1),
            SafeReciprocal(info.CoreFogDist2 - info.CoreFogDist1),
            0f));
    }

    private static float SafeReciprocal(float value) =>
        Mathf.Abs(value) > 0.000001f ? 1f / value : 0f;

    /// <summary>Per-material, per-property last-logged value so this only
    /// prints when the value is first seen or genuinely changes (ApplyTo
    /// runs every tick via SC2WaterPreview.Ensure), not every frame.</summary>
    private static readonly System.Collections.Generic.Dictionary<(Material, string), string> _lastLoggedVectors = new();

    private static void LogVectorOnce(Material material, string property, float x, float y)
    {
        var key = (material, property);
        var value = $"{x:0.###},{y:0.###}";
        if (_lastLoggedVectors.TryGetValue(key, out var last) && last == value) return;
        _lastLoggedVectors[key] = value;
        Debug.Log($"[SC2Water] {property} <- ({value}) on material '{material.name}'.");
    }

    /// <summary>Routes through SC2TerrainMaterialFactory.LoadTexturePublic —
    /// the same multi-root search (Assets/SC2Textures in the Editor,
    /// Resources/SC2Textures in a Build) every other terrain texture in
    /// this codebase already uses, instead of this class's own narrower
    /// duplicate (single root, no .win-suffix retry, no case-folding
    /// fallback). Real textures DO resolve here whenever they're actually
    /// present locally — e.g. a real map .scd import already copies
    /// water's own foam/wave/depth .dds files into this same root
    /// (Editor/ScdImportWindow.cs's CopyTexturesFromScd) — this was never
    /// guaranteed to fail, just under-searched. optional: true (quiet on a
    /// miss) since a from-scratch map's water plane genuinely often has no
    /// local copy of these to find yet.</summary>
    /// <summary>Every water texture slot that's had a load attempted this
    /// session, so LogTextureIssueOnce below fires at most once per
    /// property+path pair instead of every frame (ApplyTo is called
    /// per-tick by SC2WaterPreview.Ensure).</summary>
    private static readonly System.Collections.Generic.HashSet<string> _loggedTextureIssues = new();

    /// <summary>A silently-missing water texture (empty path, or a real
    /// path that failed to resolve) falls back to this shader's own default
    /// Property values - most consequentially, _Normal1Tex.._Normal4Tex
    /// default to Unity's flat "bump" texture, whose raw .z channel reads
    /// as 1.0 (see DEFAULT_PACKED_NORMAL_DATA in the real global_constants.
    /// hlsl - the game's own equivalent default). Four flat normals summed
    /// give normalSum.z == 4, the maximum possible value, which saturates
    /// the "Add whitecap foam" term (water.hlsl: saturate(normalSum.z -
    /// WaterWhiteCap.x)) to 1.0 across the ENTIRE water surface regardless
    /// of any real wave/depth data - reproducing "foam covers the whole
    /// water surface" exactly, with no shader-logic bug required. This was
    /// previously silent (TrySetTexture's own `if (tex != null)` guard just
    /// no-ops), so there was no way to tell "texture genuinely absent for
    /// this map" apart from "shader math is wrong" without live debugging -
    /// logging it here closes that gap.</summary>
    private static void LogTextureIssueOnce(string property, string? bdfPath, bool loaded)
    {
        if (loaded) return;
        var key = property + "|" + bdfPath;
        if (!_loggedTextureIssues.Add(key)) return;
        Debug.LogWarning(string.IsNullOrEmpty(bdfPath)
            ? $"[SC2Water] {property} has no texture path on this map's Water material - rendering with the shader's default fallback texture."
            : $"[SC2Water] {property} path '{bdfPath}' failed to resolve to a real texture - rendering with the shader's default fallback texture instead.");
    }

    private static void TrySetTexture(Material material, string property, string? bdfPath)
    {
        var tex = SC2TerrainMaterialFactory.LoadTexturePublic(bdfPath, optional: true);
        LogTextureIssueOnce(property, bdfPath, tex != null);
        TrySetTexture(material, property, tex);
    }

    private static bool TrySetDataTexture(Material material, string property, string? bdfPath)
    {
        var tex = SC2TerrainMaterialFactory.LoadTexturePublicRawData(bdfPath, optional: true);
        LogTextureIssueOnce(property, bdfPath, tex != null);
        TrySetTexture(material, property, tex);
        return tex != null;
    }

    private static void TrySetTexture(Material material, string property, Texture2D? tex)
    {
        if (tex != null && material.HasProperty(property)) material.SetTexture(property, tex);
    }
}
