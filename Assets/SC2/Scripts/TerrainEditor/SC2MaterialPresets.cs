using System.Collections.Generic;
using UnityEngine;

/// <summary>Real material-authoring presets, ported 1:1 from a reference
/// table the user supplied (from the real Blender/tooling ecosystem for
/// this game — texture paths and material names cross-check exactly
/// against this session's own 56-map corpus survey, e.g. SC2_C06_planet_d/
/// SC2_C06_sun matching SC2_CA_C06's real Planet01/Clouds01-class
/// materials). Scoped to PAIR-LEVEL fields only (ShaderFile/ShaderPrefix/
/// State/ShaderFlags/TextureScroll) — applying a
/// preset never touches a linked material's texture paths, so a user's
/// chosen Diffuse/Normal/Mask textures are never silently overwritten by
/// picking a preset. The original table's per-slot texture template is
/// kept only as this file's own documentation, not applied anywhere.
///
/// "vertex_optimize" from the source table is intentionally not ported —
/// every single preset in the reference table sets it to the same value
/// (true), so it carries no discriminating information for this preset
/// set; SC2MaterialPair.SimplifyRatio is a separate, already-existing,
/// user-controlled field and is left untouched by presets.</summary>
public sealed class SC2MaterialPreset
{
    public string Name = "";
    public string Description = "";
    public string ShaderFile = "";
    public string ShaderPrefix = "";
    /// <summary>"" means None (matches SC2MaterialPair.State's own "" =
    /// null-on-export convention).</summary>
    public string State = "";
    /// <summary>All 12 real ShaderSetup flag names always present as keys
    /// (matching SC2PairsMode.kShaderFlags' own fixed key set) — applying a
    /// preset sets every flag's Value explicitly (true or false), never
    /// adds/removes list entries, so SC2MaterialPair.ShaderFlags always
    /// stays at exactly 12 entries (see the duplicate-flag safety note in
    /// this feature's own plan).</summary>
    public Dictionary<string, bool> Flags = new();
    /// <summary>Only meaningful when Flags["ALPHA_TEST"] is true.</summary>
    public float AlphaTestThreshold = 16f;
    public Vector2 TextureScroll = Vector2.zero;
    /// <summary>Ramp / Environment textures the preset fills into a pair's still-empty slots ("" = leave alone).</summary>
    public string RampPath = "";
    public string EnvironmentPath = "";

    private static SC2MaterialPreset Build(string name, string description, string state,
        string[] trueFlags, string shaderFile = "", string shaderPrefix = "",
        float alphaTestThreshold = 16f, float scrollX = 0f, float scrollY = 0f,
        string rampPath = "", string environmentPath = "")
    {
        var preset = new SC2MaterialPreset
        {
            Name = name,
            Description = description,
            ShaderFile = shaderFile,
            ShaderPrefix = shaderPrefix,
            State = state,
            AlphaTestThreshold = alphaTestThreshold,
            TextureScroll = new Vector2(scrollX, scrollY),
            RampPath = rampPath,
            EnvironmentPath = environmentPath,
        };
        // All 12 real flags default false; only the preset's own list is set true.
        foreach (var key in new[]
                 {
                     "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING", "RNM_LIGHTING",
                     "BLENDING", "FOG", "DYNAMIC_SHADOWS", "REFLECT", "CHROMATIC_DISPERSION",
                     "GLOW", "ENVIRONMENT_MAPPING", "INFINITE_DISTANCE", "ALPHA_TEST",
                 })
            preset.Flags[key] = false;
        foreach (var flag in trueFlags) preset.Flags[flag] = true;
        return preset;
    }

    public static readonly List<SC2MaterialPreset> All = new()
    {
        Build("Standard", "Standard Preset", "",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING", "RNM_LIGHTING",
                     "BLENDING", "FOG", "DYNAMIC_SHADOWS", "REFLECT", "CHROMATIC_DISPERSION" }),

        Build("METALLIC", "Preset for metal surfaces", "",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING", "RNM_LIGHTING",
                     "GLOW", "BLENDING", "FOG", "DYNAMIC_SHADOWS", "REFLECT", "CHROMATIC_DISPERSION" }),

        Build("WATER", "Preset for water surfaces", "WaterState",
            System.Array.Empty<string>(), shaderFile: "/effects/water.hlsl", shaderPrefix: "water"),

        Build("Environment mapping", "Environment mapping from corvana chasm", "",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING", "GLOW",
                     "ENVIRONMENT_MAPPING", "RNM_LIGHTING", "BLENDING", "FOG", "DYNAMIC_SHADOWS",
                     "REFLECT", "CHROMATIC_DISPERSION" },
            // What the vanilla materials with this flag set use most (Metal ramp + the default environment cube).
            rampPath: "/textures/Terrain/Ramps/Metal_001_Ramps.dds",
            environmentPath: "/textures/environment/DefaultEnvCube.dds"),

        Build("Clouds", "clouds from corvana chasm", "TerrainAlphaBlendState",
            new[] { "INFINITE_DISTANCE", "FOG", "DYNAMIC_SHADOWS", "REFLECT", "CHROMATIC_DISPERSION" }),

        Build("Sun from Shiva Prime", "Sun from Shiva Prime", "TerrainAlphaBlendState",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING",
                     "INFINITE_DISTANCE", "REFLECT", "CHROMATIC_DISPERSION" }),

        Build("Planet from Shiva Prime", "Planet from Shiva Prime", "",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING",
                     "INFINITE_DISTANCE", "REFLECT", "CHROMATIC_DISPERSION" },
            scrollX: 0.2f),

        Build("Atmosphere from Shiva Prime", "Atmosphere from Shiva Prime", "TerrainAlphaBlendState",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING", "ALPHA_TEST",
                     "INFINITE_DISTANCE", "REFLECT", "CHROMATIC_DISPERSION" },
            alphaTestThreshold: 16f, scrollX: 0.2f),

        Build("Nebula from Shiva Prime", "Nebula from Shiva Prime", "TerrainAlphaBlendState",
            new[] { "AMBIENT_LIGHTING", "DIFFUSE_LIGHTING", "SPECULAR_LIGHTING",
                     "INFINITE_DISTANCE", "REFLECT", "CHROMATIC_DISPERSION" }),

        Build("Space rim from Shiva Prime", "Space rim from Shiva Prime", "TerrainAdditiveState",
            new[] { "GLOW", "INFINITE_DISTANCE", "REFLECT", "CHROMATIC_DISPERSION" }),
    };
}
