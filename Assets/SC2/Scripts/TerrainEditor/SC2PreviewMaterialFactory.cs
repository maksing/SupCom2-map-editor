using UnityEngine;

public static class SC2PreviewMaterialFactory
{
    public static Material MakePairColorMaterial(SC2TerrainData data, int pairIdx, string prefix)
    {
        // Shader.Find("Unlit/Color")/("Standard")/("Diffuse") all reliably
        // return null in a standalone build (Unity strips any shader
        // nothing references — none of these are referenced anywhere in
        // this project), which used to throw "Value cannot be null.
        // Parameter name: shader" on `new Material(...)` here — the same
        // crash already fixed for SC2/Terrain (see
        // SC2TerrainMaterialFactory.ResolveShader). SC2FallbackShader.Get()
        // is loaded via Resources.Load, guaranteed present in any build.
        var shader = SC2FallbackShader.Get();
        var name = data != null && pairIdx >= 0 && pairIdx < data.Pairs.Count
            ? data.Pairs[pairIdx].Name
            : "Pair";
        var mat = new Material(shader) { name = $"{prefix}_{name}" };
        var color = data != null ? data.PairColor(pairIdx) : Color.grey;
        ApplyColor(mat, shader, color);
        return mat;
    }

    public static Material MakeGrey(string name)
    {
        // Shader.Find("Unlit/Color")/("Standard")/("Diffuse") all reliably
        // return null in a standalone build (Unity strips any shader
        // nothing references — none of these are referenced anywhere in
        // this project), which used to throw "Value cannot be null.
        // Parameter name: shader" on `new Material(...)` here — the same
        // crash already fixed for SC2/Terrain (see
        // SC2TerrainMaterialFactory.ResolveShader). SC2FallbackShader.Get()
        // is loaded via Resources.Load, guaranteed present in any build.
        var shader = SC2FallbackShader.Get();
        var mat = new Material(shader) { name = name };
        ApplyColor(mat, shader, new Color(0.5f, 0.5f, 0.5f));
        return mat;
    }

    /// <summary>General-purpose version of MakeGrey/MakeDebugRed for any
    /// caller that just needs a flat unlit-ish color (e.g. a wireframe
    /// overlay) — same guaranteed-present-in-a-build shader resolution.</summary>
    public static Material MakeColor(string name, Color color)
    {
        var shader = SC2FallbackShader.Get();
        var mat = new Material(shader) { name = name };
        ApplyColor(mat, shader, color);
        return mat;
    }

    public static Material MakeDebugRed(string name)
    {
        // Shader.Find("Unlit/Color")/("Standard")/("Diffuse") all reliably
        // return null in a standalone build (Unity strips any shader
        // nothing references — none of these are referenced anywhere in
        // this project), which used to throw "Value cannot be null.
        // Parameter name: shader" on `new Material(...)` here — the same
        // crash already fixed for SC2/Terrain (see
        // SC2TerrainMaterialFactory.ResolveShader). SC2FallbackShader.Get()
        // is loaded via Resources.Load, guaranteed present in any build.
        var shader = SC2FallbackShader.Get();
        var mat = new Material(shader) { name = name };
        ApplyColor(mat, shader, new Color(1f, 0.08f, 0.02f, 1f));
        return mat;
    }

    public static void Destroy(Material[] materials)
    {
        if (materials == null) return;
        for (var i = 0; i < materials.Length; i++)
            if (materials[i] != null) Object.DestroyImmediate(materials[i]);
    }

    private static void ApplyColor(Material mat, Shader shader, Color color)
    {
        if (shader != null && shader.name == "Unlit/Color") mat.SetColor("_Color", color);
        else mat.color = color;
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
    }
}
