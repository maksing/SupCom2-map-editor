using UnityEngine;

/// <summary>The Collision2 floor plane: the lowest height a vertex of the generated collision mesh may have.</summary>
public static class SC2Collision2Floor
{
    /// <summary>Raises every height of a Collision2 heightmap (raw units, 1/128 world unit) that is lower than
    /// <paramref name="floorHeight"/> up to it, so the ground below the floor becomes a flat plane at that height.</summary>
    public static void Apply(ushort[] heightmap, float floorHeight)
    {
        var floorRaw = (ushort)Mathf.Clamp(Mathf.RoundToInt(floorHeight * 128f), 0, 65535);
        for (var i = 0; i < heightmap.Length; i++)
            if (heightmap[i] < floorRaw) heightmap[i] = floorRaw;
    }
}
