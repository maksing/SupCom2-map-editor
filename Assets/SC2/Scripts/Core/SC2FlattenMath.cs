using System;
using UnityEngine;

/// <summary>A plane through <see cref="Center"/> with the (upward) unit normal <see cref="Normal"/>, as the
/// Flatten brush samples it from the terrain under the brush.</summary>
public struct SC2FlattenPlane
{
    public bool Valid;
    public Vector3 Center;
    public Vector3 Normal;

    /// <summary>Height of the plane at world (x, z).</summary>
    public float HeightAt(float x, float z) =>
        Center.y - (Normal.x * (x - Center.x) + Normal.z * (z - Center.z)) / Normal.y;
}

/// <summary>Blender's Flatten brush for a heightfield: the brush samples an "area plane" from the vertices
/// in the middle of the brush (their average position and surface normal) and pulls every vertex under it toward that
/// plane, up or down. A tilted slope therefore flattens into a tilted plane, not into a horizontal one.</summary>
public static class SC2FlattenMath
{
    /// <summary>Blender samples the plane from the middle of the brush only (its "area radius", half the
    /// brush radius by default) and averages that area without any falloff. The whole brush radius is
    /// only used to move vertices. A plane fitted over the whole brush disc, weighted toward its centre,
    /// changes with every dab as the brush slides over the terrain it has just changed, and the ground
    /// seems to flow after the brush.</summary>
    public const float AreaRadiusFactor = 0.5f;

    /// <summary>Blender's stroke spacing (10% of the brush diameter): a dab is placed every this many
    /// world units along the stroke, whatever the mouse speed or event rate.</summary>
    public const float SpacingRadiusFraction = 0.2f;

    /// <summary>Blender's default "Smooth" brush falloff: 1 across the inner <paramref name="hardness"/>
    /// of the radius, then a smoothstep down to 0 at the edge. <paramref name="t"/> is the distance
    /// from the centre as a fraction of the radius.</summary>
    public static float SmoothFalloff(float t, float hardness)
    {
        if (t >= 1f) return 0f;
        hardness = Mathf.Clamp01(hardness);
        if (hardness >= 0.999f || t <= hardness) return 1f;
        var f = 1f - (t - hardness) / Mathf.Max(1f - hardness, 0.001f);
        return f * f * (3f - 2f * f);
    }

    /// <summary>The dab positions for a drag from <paramref name="from"/> to <paramref name="to"/> (X/Z),
    /// one every <paramref name="spacing"/> units, the first one <paramref name="spacing"/> from
    /// <paramref name="from"/>. At most <paramref name="maxDabs"/>; when that cap cuts a long jump
    /// short, <paramref name="reachedEnd"/> is true (the stroke simply resumes from <paramref name="to"/>).</summary>
    public static System.Collections.Generic.List<Vector3> SpacedDabs(Vector3 from, Vector3 to, float spacing, int maxDabs, out bool reachedEnd)
    {
        var dabs = new System.Collections.Generic.List<Vector3>();
        reachedEnd = false;
        var dx = to.x - from.x; var dz = to.z - from.z;
        var dist = Mathf.Sqrt(dx * dx + dz * dz);
        if (spacing <= 0f || dist < spacing) return dabs;
        var count = Mathf.FloorToInt(dist / spacing);
        if (count > maxDabs) { count = maxDabs; reachedEnd = true; }
        for (var k = 1; k <= count; k++)
        {
            var t = k * spacing / dist;
            dabs.Add(new Vector3(from.x + dx * t, Mathf.Lerp(from.y, to.y, t), from.z + dz * t));
        }
        return dabs;
    }

    /// <summary>Sampling weight for <see cref="ComputeAreaPlane"/>: 1 inside the area radius, 0 outside.</summary>
    public static float AreaWeight(float brushRadius, float dx, float dz)
    {
        var r = brushRadius * AreaRadiusFactor;
        return dx * dx + dz * dz <= r * r ? 1f : 0f;
    }

    /// <summary>Samples the area plane under the brush. <paramref name="weightAt"/>(dx, dz) is the brush
    /// falloff (0 outside the brush) at an offset from the brush centre. Heights are indexed
    /// [gz * (gridWidth + 1) + gx]. Returns an invalid plane when no vertex carries weight.</summary>
    public static SC2FlattenPlane ComputeAreaPlane(float[] heights, int gridWidth, int gridHeight, float cellSize,
        Vector3 worldPos, float radius, Func<float, float, float> weightAt)
    {
        var stride = gridWidth + 1;
        var x0 = Mathf.Max(0, Mathf.FloorToInt((worldPos.x - radius) / cellSize));
        var x1 = Mathf.Min(gridWidth, Mathf.CeilToInt((worldPos.x + radius) / cellSize));
        var z0 = Mathf.Max(0, Mathf.FloorToInt((worldPos.z - radius) / cellSize));
        var z1 = Mathf.Min(gridHeight, Mathf.CeilToInt((worldPos.z + radius) / cellSize));

        double sumW = 0, sumX = 0, sumH = 0, sumZ = 0, nx = 0, ny = 0, nz = 0;
        for (var gz = z0; gz <= z1; gz++)
        for (var gx = x0; gx <= x1; gx++)
        {
            var wx = gx * cellSize; var wz = gz * cellSize;
            var w = weightAt(wx - worldPos.x, wz - worldPos.z);
            if (w <= 0f) continue;

            var h = heights[gz * stride + gx];
            // Surface normal from central differences (one-sided at the grid border).
            var hl = heights[gz * stride + Mathf.Max(gx - 1, 0)];
            var hr = heights[gz * stride + Mathf.Min(gx + 1, gridWidth)];
            var hd = heights[Mathf.Max(gz - 1, 0) * stride + gx];
            var hu = heights[Mathf.Min(gz + 1, gridHeight) * stride + gx];
            var spanX = (Mathf.Min(gx + 1, gridWidth) - Mathf.Max(gx - 1, 0)) * cellSize;
            var spanZ = (Mathf.Min(gz + 1, gridHeight) - Mathf.Max(gz - 1, 0)) * cellSize;
            var slopeX = spanX > 0f ? (hr - hl) / spanX : 0f;
            var slopeZ = spanZ > 0f ? (hu - hd) / spanZ : 0f;
            var len = Mathf.Sqrt(slopeX * slopeX + 1f + slopeZ * slopeZ);

            sumW += w; sumX += w * wx; sumH += w * h; sumZ += w * wz;
            nx += w * (-slopeX / len); ny += w / len; nz += w * (-slopeZ / len);
        }
        if (sumW <= 0) return default;

        var normal = new Vector3((float)nx, (float)ny, (float)nz);
        if (normal.y < 0.05f) return default; // no usable "up" — a near-vertical average is not a heightfield plane
        return new SC2FlattenPlane
        {
            Valid = true,
            Center = new Vector3((float)(sumX / sumW), (float)(sumH / sumW), (float)(sumZ / sumW)),
            Normal = normal.normalized,
        };
    }

    /// <summary>Pulls the vertices under the brush toward <paramref name="plane"/>: each one moves by
    /// <c>falloff * amount</c> of its vertical distance to the plane (amount is clamped to 0..1).
    /// Returns false when the plane is invalid.</summary>
    public static bool ApplyToHeights(float[] heights, int gridWidth, int gridHeight, float cellSize,
        Vector3 worldPos, float radius, float amount, SC2FlattenPlane plane, Func<float, float, float> weightAt,
        float minHeight, float maxHeight)
    {
        if (!plane.Valid) return false;
        var stride = gridWidth + 1;
        var x0 = Mathf.Max(0, Mathf.FloorToInt((worldPos.x - radius) / cellSize));
        var x1 = Mathf.Min(gridWidth, Mathf.CeilToInt((worldPos.x + radius) / cellSize));
        var z0 = Mathf.Max(0, Mathf.FloorToInt((worldPos.z - radius) / cellSize));
        var z1 = Mathf.Min(gridHeight, Mathf.CeilToInt((worldPos.z + radius) / cellSize));
        for (var gz = z0; gz <= z1; gz++)
        for (var gx = x0; gx <= x1; gx++)
        {
            var wx = gx * cellSize; var wz = gz * cellSize;
            var w = weightAt(wx - worldPos.x, wz - worldPos.z);
            if (w <= 0f) continue;
            var idx = gz * stride + gx;
            var blended = Mathf.Lerp(heights[idx], plane.HeightAt(wx, wz), Mathf.Clamp01(w * amount));
            heights[idx] = Mathf.Clamp(blended, minHeight, maxHeight);
        }
        return true;
    }
}
