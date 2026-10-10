using System;
using System.Collections.Generic;

/// <summary>
/// Which way a prop model faces. A prop has no front of its own, but a building with a ramp, or any model that is lopsided, has
/// a side that stands out: where the model's vertices are on average, away from its origin. Mirroring such a prop in a symmetry
/// copy has to carry that side across the mirror, which a plain "yaw -> -yaw" cannot do unless the side happens to be the
/// model's +Z.
/// </summary>
public static class SC2PropFacing
{
    /// <summary>A model whose far vertices average closer to its origin than this fraction of its radius is treated as symmetric.</summary>
    public const float MinOffsetFraction = 0.04f;

    /// <summary>The yaw (degrees, Unity's: a facing of +Z is 0 and of +X is 90) of the side the model leans towards, from its
    /// vertex positions as x, y, z triples in the model's own space; 0 for a model that is centred on its origin. The direction
    /// is that of the vertices weighted by the 32nd power of their distance from the origin (in effect the tips that reach farthest): a body that is the same all
    /// round (a ring of walls and towers) cancels out, and what stands out beyond it - a ramp, a wing - decides, where a plain
    /// average of all the vertices was pulled off its direction by whatever else was on that side (on the teleporter, by 13 degrees).</summary>
    public static float FromPositions(IReadOnlyList<float> positions)
    {
        var count = positions == null ? 0 : positions.Count / 3;
        if (count == 0) return 0f;
        double radius = 0;
        for (var i = 0; i < count; i++)
        {
            double x = positions[i * 3], z = positions[i * 3 + 2];
            radius = Math.Max(radius, Math.Sqrt(x * x + z * z));
        }
        if (radius < 1e-6) return 0f;
        double sumX = 0, sumZ = 0, sumWeight = 0;
        for (var i = 0; i < count; i++)
        {
            double x = positions[i * 3], z = positions[i * 3 + 2];
            var r = Math.Sqrt(x * x + z * z) / radius; // 0..1
            var weight = r * r;                         // r^2
            weight *= weight; weight *= weight;         // r^8
            weight *= weight; weight *= weight;         // r^32
            sumX += x * weight; sumZ += z * weight; sumWeight += weight;
        }
        if (sumWeight < 1e-12) return 0f;
        var meanX = sumX / sumWeight;
        var meanZ = sumZ / sumWeight;
        if (Math.Sqrt(meanX * meanX + meanZ * meanZ) < MinOffsetFraction * radius) return 0f;
        return (float)(Math.Atan2(meanX, meanZ) * 180.0 / Math.PI);
    }
}
