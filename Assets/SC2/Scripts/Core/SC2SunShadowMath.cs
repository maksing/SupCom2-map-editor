using UnityEngine;

/// <summary>The geometry behind the Environment > Sun shadow preview: where an object lit by the map's sun casts its
/// shadow on flat ground. SunDirection is the direction TO the sun (Y up), so the shadow falls opposite to its
/// horizontal part and is 1 / tan(elevation) times as long as the object is tall. World axes as SC2 has them:
/// X grows to the right and Z grows DOWN the map (so "map up" is -Z).</summary>
public static class SC2SunShadowMath
{
    /// <summary>Below this height of the normalised sun vector the sun counts as on the horizon: the shadow
    /// would be practically endless.</summary>
    public const float MinSunY = 0.001f;

    public struct Result
    {
        /// <summary>False when the direction is unusable or the sun is at or below the horizon.</summary>
        public bool HasShadow;
        /// <summary>Angle of the sun above the horizon, degrees (negative below it).</summary>
        public float ElevationDeg;
        /// <summary>Compass angle of the sun's horizontal position, degrees clockwise from map up (-Z) toward +X;
        /// 0..360. Zero when the sun is straight overhead.</summary>
        public float AzimuthDeg;
        /// <summary>Shadow length for an object one unit tall.</summary>
        public float LengthPerUnit;
        /// <summary>Ground offset (world x, world z) of the top of a one-unit-tall object's shadow from its foot.</summary>
        public Vector2 OffsetPerUnit;
    }

    public static Result Compute(Vector3 sunDirection)
    {
        var result = new Result();
        if (sunDirection.sqrMagnitude < 1e-12f) return result;
        var d = sunDirection.normalized;
        result.ElevationDeg = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;

        var horizontal = new Vector2(d.x, d.z);
        if (horizontal.sqrMagnitude > 1e-12f)
        {
            var azimuth = Mathf.Atan2(horizontal.x, -horizontal.y) * Mathf.Rad2Deg;
            result.AzimuthDeg = (azimuth + 360f) % 360f;
        }

        if (d.y <= MinSunY) return result;
        result.HasShadow = true;
        result.OffsetPerUnit = -horizontal / d.y;
        result.LengthPerUnit = result.OffsetPerUnit.magnitude;
        return result;
    }

    /// <summary>True when <paramref name="p"/> lies in the ground area covered by an axis-aligned square (centred on
    /// the origin, side <paramref name="side"/>) or by any copy of it moved along <paramref name="offset"/> by
    /// 0..1 times — the top view of a cube and its shadow.</summary>
    public static bool InSweptSquare(Vector2 p, float side, Vector2 offset)
    {
        var half = side * 0.5f;
        var lo = 0f;
        var hi = 1f;
        return ClipAxis(p.x, offset.x, half, ref lo, ref hi) && ClipAxis(p.y, offset.y, half, ref lo, ref hi);
    }

    // The t values for which p - t * offset stays within [-half, half] on one axis, intersected into [lo, hi].
    private static bool ClipAxis(float p, float offset, float half, ref float lo, ref float hi)
    {
        if (Mathf.Abs(offset) < 1e-6f)
            return Mathf.Abs(p) <= half;
        var t1 = (p - half) / offset;
        var t2 = (p + half) / offset;
        if (t1 > t2) { var swap = t1; t1 = t2; t2 = swap; }
        lo = Mathf.Max(lo, t1);
        hi = Mathf.Min(hi, t2);
        return lo <= hi;
    }
}
