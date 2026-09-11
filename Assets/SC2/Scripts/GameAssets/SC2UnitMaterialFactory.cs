using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Creates one SC2/Unit material for each material entry in a blueprint material set.
/// SCM material ranges and blueprint materials use their declared order as the mapping.
/// </summary>
public static class SC2UnitMaterialFactory
{
    public const string ShaderName = "SC2/Unit";
    private const string ShaderResourcesName = "SC2Unit"; // Resources/SC2Unit.shader

    public static Material[] CreateMaterials(
        SC2GameAssetMaterialSet materialSet,
        ISC2UnitTextureResolver textureResolver,
        Color mainColor,
        Color additionalColor)
    {
        if (materialSet == null) throw new ArgumentNullException(nameof(materialSet));
        if (textureResolver == null) throw new ArgumentNullException(nameof(textureResolver));

        var result = new Material[materialSet.Materials.Count];
        for (int index = 0; index < result.Length; index++)
            result[index] = CreateMaterial(materialSet.Materials[index], textureResolver, mainColor, additionalColor);
        return result;
    }

    public static Material CreateMaterial(
        SC2GameAssetMaterial blueprintMaterial,
        ISC2UnitTextureResolver textureResolver,
        Color mainColor,
        Color additionalColor)
    {
        if (blueprintMaterial == null) throw new ArgumentNullException(nameof(blueprintMaterial));
        if (textureResolver == null) throw new ArgumentNullException(nameof(textureResolver));

        // Resources.Load first, Shader.Find as a fallback - NOT the other
        // way around. This is the exact same bug already fixed for SC2/
        // Terrain, SC2/Water and SC2/CostsOverlay (see
        // SC2TerrainMaterialFactory.ResolveShader's own doc comment for the
        // full story): SC2Unit.shader lives under Resources/ but was still
        // being looked up purely via Shader.Find(ShaderName), which finds
        // ANY compiled shader in the Editor (nothing gets stripped there)
        // but is not reliable in a standalone Build - a build can still
        // strip a shader that no Material ASSET anywhere references, even
        // if the .shader file itself sits in a Resources folder, since
        // these materials are only ever created procedurally at runtime via
        // `new Material(shader)`. Confirmed the actual reported symptom:
        // works correctly in the Unity Editor's Game View, renders nothing
        // at all in a real Build & Run - exactly this failure mode.
        // Resources.Load<Shader> uses the normal Resources asset-loading
        // path (guaranteed to include everything under Resources/ in the
        // build), so it finds the shader reliably regardless of stripping.
        var shader = Resources.Load<Shader>(ShaderResourcesName) ?? Shader.Find(ShaderName);
        if (shader == null) throw new InvalidOperationException("Shader '" + ShaderName + "' was not found (neither Resources/" + ShaderResourcesName + " nor Shader.Find). Ensure SC2Unit.shader is included in the build.");

        var material = new Material(shader)
        {
            name = "SC2 Unit " + (blueprintMaterial.ShaderMacros ?? "Material"),
            // Required for Graphics.DrawMeshInstanced (SC2GameAssetScenePreview's
            // batched Units/Props rendering) - a Material created at runtime
            // defaults to false regardless of the shader's own instancing
            // support (#pragma multi_compile_instancing, SC2Unit.shader).
            enableInstancing = true
        };
        material.SetColor("_MainColor", mainColor);
        material.SetColor("_AdditionalColor", additionalColor);

        SetTexture(material, "_Diffuse", blueprintMaterial, "DiffuseName", textureResolver);
        SetTexture(material, "_Normal", blueprintMaterial, "NormalName", textureResolver);
        SetTexture(material, "_Mask", blueprintMaterial, "MaskName", textureResolver);
        bool hasTeamTexture = SetTexture(material, "_Team", blueprintMaterial, "TeamName", textureResolver);
        material.SetFloat("_TeamEnabled", hasTeamTexture ? 1f : 0f);
        return material;
    }

    private static bool SetTexture(Material target, string propertyName, SC2GameAssetMaterial source, string blueprintField, ISC2UnitTextureResolver resolver)
    {
        string path;
        if (!source.ResolvedTextures.TryGetValue(blueprintField, out path))
            source.Textures.TryGetValue(blueprintField, out path);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var texture = resolver.Resolve(path);
        if (texture == null) return false;
        target.SetTexture(propertyName, texture);
        return true;
    }
}

/// <summary>
/// Resolves the slash-prefixed texture paths stored in blueprints to bundled
/// Resources textures. It does no filesystem scanning in a player build.
/// </summary>
public interface ISC2UnitTextureResolver
{
    Texture2D Resolve(string blueprintPath);
}

public sealed class SC2UnitTextureResolver : ISC2UnitTextureResolver
{
    private readonly string[] _resourceRoots;
    private readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

    public SC2UnitTextureResolver(params string[] resourceRoots)
    {
        _resourceRoots = resourceRoots == null || resourceRoots.Length == 0
            ? new[] { "SC2Textures" }
            : resourceRoots;
    }

    public Texture2D Resolve(string blueprintPath)
    {
        if (string.IsNullOrWhiteSpace(blueprintPath)) return null;
        string normalized = Normalize(blueprintPath);
        Texture2D cached;
        if (_cache.TryGetValue(normalized, out cached)) return cached;

        foreach (string root in _resourceRoots)
        {
            foreach (string candidate in Candidates(root, normalized))
            {
                var texture = Resources.Load<Texture2D>(candidate);
                if (texture == null) continue;
                _cache[normalized] = texture;
                return texture;
            }
        }
        _cache[normalized] = null;
        return null;
    }

    private static IEnumerable<string> Candidates(string root, string normalized)
    {
        string prefix = string.IsNullOrWhiteSpace(root) ? string.Empty : root.TrimEnd('/', '\\') + "/";
        yield return prefix + normalized;
        if (normalized.EndsWith(".win", StringComparison.OrdinalIgnoreCase))
            yield return prefix + normalized.Substring(0, normalized.Length - 4);
        else
            yield return prefix + normalized + ".win";
    }

    private static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        return Path.ChangeExtension(normalized, null).Replace('\\', '/');
    }
}
