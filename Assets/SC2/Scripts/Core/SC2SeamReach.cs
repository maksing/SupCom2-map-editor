using UnityEngine;

/// <summary>
/// Which of the two terrain surfaces a round brush reaches. The background surrounds the core and has the core's rectangle carved
/// out of it, so a brush well inside the core touches only the core, one on the seam touches both, and one outside the core touches
/// only the background. Pure, so the rules can be tested.
/// </summary>
public static class SC2SeamReach
{
    /// <summary>True when a brush of <paramref name="radius"/> at (x, z) touches the core's rectangle [0, coreW] x [0, coreD]
    /// (widened by <paramref name="margin"/>).</summary>
    public static bool ReachesCore(float x, float z, float coreW, float coreD, float radius, float margin)
    {
        var gapX = Mathf.Max(0f, Mathf.Max(-x, x - coreW));
        var gapZ = Mathf.Max(0f, Mathf.Max(-z, z - coreD));
        return Mathf.Sqrt(gapX * gapX + gapZ * gapZ) <= radius + margin;
    }

    /// <summary>True when the brush touches any of the background, i.e. anything of its rectangle outside the core's. A brush
    /// centred inside the core reaches it only once it gets within reach of the core's edge (what lies inside the carved hole is
    /// not rendered and is left alone); one centred outside the core reaches it when it touches the background's rectangle
    /// (starting at (bgMinX, bgMinZ), bgW x bgD in size).</summary>
    public static bool ReachesBackground(float x, float z, float coreW, float coreD,
        float bgMinX, float bgMinZ, float bgW, float bgD, float radius, float margin)
    {
        var insideCore = x >= 0f && x <= coreW && z >= 0f && z <= coreD;
        if (insideCore)
        {
            var edgeGap = Mathf.Min(Mathf.Min(x, coreW - x), Mathf.Min(z, coreD - z));
            return edgeGap <= radius + margin;
        }
        var gapX = Mathf.Max(0f, Mathf.Max(bgMinX - x, x - (bgMinX + bgW)));
        var gapZ = Mathf.Max(0f, Mathf.Max(bgMinZ - z, z - (bgMinZ + bgD)));
        return Mathf.Sqrt(gapX * gapX + gapZ * gapZ) <= radius + margin;
    }
}
