using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>Bakes the two point-cloud volumes (pcloud_up, pcloud_down) that light units from the map itself: a
/// Res x Res x Depth grid over a box in the map, read by unit.hlsl. Terrain only (props do not occlude here).
///
/// Per voxel (centre = origin + ((i+.5)/Res * size.x, (k+.5)/Depth * size.y, (j+.5)/Res * size.z)):
///  - inside the ground (terrain surface above the voxel): down.a = 0; otherwise 255, or 0 when the terrain
///    shadows the point from the sun (hard ray along ToSun);
///  - up.rgb = sky colour x sky visibility (cos^2 of the horizon angle, averaged over azimuths); ground voxels
///    keep the plain sky colour so trilinear filtering never darkens units standing on the ground;
///  - down.rgb = GroundReflection x (top-down ground light blurred with sigma = BlurTexels x slice index)
///    + SkyInGround x up. The ground light is the map's baked RNM lighting (mean of the three basis maps) when
///    given, a flat mid value otherwise.
///
/// The model follows what the vanilla maps' point clouds show (see the project notes); the sky colour and the
/// reflection scales are per-map inputs.</summary>
public static class SC2PointCloudBaker
{
    public const int Res = 256;
    public const int Depth = 8;

    public sealed class Settings
    {
        public Vector3 Origin, Size;
        /// <summary>Direction TO the sun (Y up), need not be normalised.</summary>
        public Vector3 ToSun = new Vector3(0.3f, 0.8f, 0.3f);
        /// <summary>Sky light colour with its intensity already applied (0..1 per channel).</summary>
        public Color SkyColor = new Color(0.375f, 0.44f, 0.5f);
        public float GroundReflection = 0.5f;
        public float SkyInGround = 0.35f;
        public float BlurTexels = 1f;
        public bool SunShadow = true;
        /// <summary>How far (world units) the sun ray and the sky horizon look for terrain.</summary>
        public float SunRange = 400f;
        public float SkyRange = 120f;
        public int SkyAzimuths = 16;
    }

    public sealed class Result
    {
        /// <summary>Per slice, Res*Res*3 bytes (RGB, row = z, column = x).</summary>
        public byte[][] UpRgb, DownRgb;
        /// <summary>Per slice, Res*Res bytes: the sun visibility (0 = shadow or inside the ground).</summary>
        public byte[][] DownAlpha;
        /// <summary>Fraction of voxels inside the ground / in the sun's shadow (for the status line).</summary>
        public float SolidFraction, ShadowFraction;
    }

    /// <summary>World y of slice <paramref name="k"/>'s voxel centres.</summary>
    public static float SliceHeight(Settings s, int k) => s.Origin.y + (k + 0.5f) * s.Size.y / Depth;

    /// <param name="heightAt">Terrain surface height at a world (x, z); -infinity where there is none.</param>
    /// <param name="groundLight">Res*Res*3 floats (0..1), row = z, column = x over the box; null = flat mid light.</param>
    public static Result Bake(Func<float, float, float> heightAt, float[] groundLight, Settings s)
    {
        var tx = s.Size.x / Res;
        var tz = s.Size.z / Res;
        var toSun = s.ToSun.sqrMagnitude > 1e-8f ? s.ToSun.normalized : new Vector3(0.3f, 0.8f, 0.3f).normalized;

        // Height grid over the box plus a margin, so rays and horizon scans can leave the box.
        var range = Mathf.Max(s.SunRange, s.SkyRange);
        var mx = Mathf.Clamp(Mathf.CeilToInt(range / Mathf.Max(tx, 1e-3f)), 1, 384);
        var mz = Mathf.Clamp(Mathf.CeilToInt(range / Mathf.Max(tz, 1e-3f)), 1, 384);
        var gw = Res + 2 * mx;
        var gh = Res + 2 * mz;
        var grid = new float[gw * gh];
        var maxHeight = float.NegativeInfinity;
        Parallel.For(0, gh, gj =>
        {
            var z = s.Origin.z + (gj - mz + 0.5f) * tz;
            for (var gi = 0; gi < gw; gi++)
            {
                var h = heightAt(s.Origin.x + (gi - mx + 0.5f) * tx, z);
                grid[gj * gw + gi] = float.IsNaN(h) ? float.NegativeInfinity : h;
            }
        });
        foreach (var h in grid) if (h > maxHeight) maxHeight = h;

        var sunMax = ComputeSunOcclusion(grid, gw, mx, mz, tx, tz, toSun, s, maxHeight);
        var light = groundLight != null && groundLight.Length == Res * Res * 3 ? groundLight : FlatLight();

        var result = new Result
        {
            UpRgb = new byte[Depth][], DownRgb = new byte[Depth][], DownAlpha = new byte[Depth][],
        };
        var blurred = BlurStack(light, s.BlurTexels);
        var azimuths = Mathf.Clamp(s.SkyAzimuths, 4, 64);
        var skyOffsets = BuildHorizonOffsets(azimuths, tx, tz, s.SkyRange);
        long solidCount = 0, shadowCount = 0;

        for (var k = 0; k < Depth; k++)
        {
            var y = SliceHeight(s, k);
            var up = new byte[Res * Res * 3];
            var down = new byte[Res * Res * 3];
            var alpha = new byte[Res * Res];
            var solidRow = new long[Res];
            var shadowRow = new long[Res];
            var blur = blurred[k];
            Parallel.For(0, Res, j =>
            {
                for (var i = 0; i < Res; i++)
                {
                    var idx = j * Res + i;
                    var groundH = grid[(j + mz) * gw + (i + mx)];
                    var solid = groundH > y;
                    var visibility = solid ? 1f : SkyVisibility(grid, gw, gh, i + mx, j + mz, y, skyOffsets);
                    var lit = !solid && !(s.SunShadow && sunMax[idx] > y);
                    if (solid) solidRow[j]++;
                    else if (!lit) shadowRow[j]++;

                    var upR = s.SkyColor.r * visibility;
                    var upG = s.SkyColor.g * visibility;
                    var upB = s.SkyColor.b * visibility;
                    up[idx * 3] = ToByte(upR); up[idx * 3 + 1] = ToByte(upG); up[idx * 3 + 2] = ToByte(upB);
                    down[idx * 3] = ToByte(s.GroundReflection * blur[idx * 3] + s.SkyInGround * upR);
                    down[idx * 3 + 1] = ToByte(s.GroundReflection * blur[idx * 3 + 1] + s.SkyInGround * upG);
                    down[idx * 3 + 2] = ToByte(s.GroundReflection * blur[idx * 3 + 2] + s.SkyInGround * upB);
                    alpha[idx] = lit ? (byte)255 : (byte)0;
                }
            });
            foreach (var c in solidRow) solidCount += c;
            foreach (var c in shadowRow) shadowCount += c;
            result.UpRgb[k] = up; result.DownRgb[k] = down; result.DownAlpha[k] = alpha;
        }
        var total = (float)Depth * Res * Res;
        result.SolidFraction = solidCount / total;
        result.ShadowFraction = shadowCount / total;
        return result;
    }

    private static float[] FlatLight()
    {
        var light = new float[Res * Res * 3];
        for (var i = 0; i < light.Length; i++) light[i] = 0.5f;
        return light;
    }

    private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

    // For every column, the largest (terrain height - t * toSun.y) along the horizontal ray toward the sun: a voxel
    // at height y is in shadow when that value exceeds y.
    private static float[] ComputeSunOcclusion(float[] grid, int gw, int mx, int mz, float tx, float tz, Vector3 toSun, Settings s, float maxHeight)
    {
        var occlusion = new float[Res * Res];
        if (toSun.y <= 1e-3f)
        {
            for (var i = 0; i < occlusion.Length; i++) occlusion[i] = float.PositiveInfinity;
            return occlusion;
        }
        // No terrain can shadow a point once the ray is above the tallest surface.
        var tMax = Mathf.Min(s.SunRange / Mathf.Max(Mathf.Sqrt(toSun.x * toSun.x + toSun.z * toSun.z), 1e-3f),
            Mathf.Max(0f, (maxHeight - s.Origin.y) / toSun.y));
        var step = Mathf.Max(0.5f * Mathf.Min(tx, tz), 0.25f);
        var count = Mathf.Max(1, Mathf.CeilToInt(tMax / step));
        var di = new int[count]; var dj = new int[count]; var drop = new float[count];
        for (var n = 0; n < count; n++)
        {
            var t = (n + 1) * step;
            di[n] = Mathf.RoundToInt(toSun.x * t / tx);
            dj[n] = Mathf.RoundToInt(toSun.z * t / tz);
            drop[n] = toSun.y * t;
        }
        var gh = grid.Length / gw;
        Parallel.For(0, Res, j =>
        {
            for (var i = 0; i < Res; i++)
            {
                var best = float.NegativeInfinity;
                for (var n = 0; n < count; n++)
                {
                    var gi = Mathf.Clamp(i + mx + di[n], 0, gw - 1);
                    var gj = Mathf.Clamp(j + mz + dj[n], 0, gh - 1);
                    var v = grid[gj * gw + gi] - drop[n];
                    if (v > best) best = v;
                }
                occlusion[j * Res + i] = best;
            }
        });
        return occlusion;
    }

    private struct HorizonStep { public int Di, Dj; public float Dist; public int Azimuth; }

    private static HorizonStep[] BuildHorizonOffsets(int azimuths, float tx, float tz, float range)
    {
        var steps = new System.Collections.Generic.List<HorizonStep>();
        var first = Mathf.Max(Mathf.Min(tx, tz), 0.5f);
        for (var a = 0; a < azimuths; a++)
        {
            var angle = 2f * Mathf.PI * a / azimuths;
            var dx = Mathf.Cos(angle); var dz = Mathf.Sin(angle);
            var lastI = int.MinValue; var lastJ = int.MinValue;
            for (var r = first; r <= range; r *= 1.18f)
            {
                var di = Mathf.RoundToInt(dx * r / tx); var dj = Mathf.RoundToInt(dz * r / tz);
                if (di == lastI && dj == lastJ) continue;
                lastI = di; lastJ = dj;
                steps.Add(new HorizonStep { Di = di, Dj = dj, Dist = Mathf.Sqrt((di * tx) * (di * tx) + (dj * tz) * (dj * tz)), Azimuth = a });
            }
        }
        return steps.ToArray();
    }

    // Mean over azimuths of cos^2(horizon elevation) = 1 / (1 + tan^2): the cosine-weighted share of the sky
    // that stays open above the terrain around the point.
    private static float SkyVisibility(float[] grid, int gw, int gh, int gi0, int gj0, float y, HorizonStep[] steps)
    {
        if (steps.Length == 0) return 1f;
        var azimuths = steps[steps.Length - 1].Azimuth + 1;
        Span<float> tanMax = stackalloc float[64];
        for (var a = 0; a < azimuths; a++) tanMax[a] = 0f;
        foreach (var st in steps)
        {
            var gi = gi0 + st.Di; var gj = gj0 + st.Dj;
            if (gi < 0 || gj < 0 || gi >= gw || gj >= gh) continue;
            var t = (grid[gj * gw + gi] - y) / st.Dist;
            if (t > tanMax[st.Azimuth]) tanMax[st.Azimuth] = t;
        }
        var sum = 0f;
        for (var a = 0; a < azimuths; a++) sum += 1f / (1f + tanMax[a] * tanMax[a]);
        return sum / azimuths;
    }

    // blurred[k] = the light raster blurred with sigma = blurTexels * k (slice 0 stays sharp).
    private static float[][] BlurStack(float[] light, float blurTexels)
    {
        var stack = new float[Depth][];
        for (var k = 0; k < Depth; k++)
        {
            var sigma = blurTexels * k;
            stack[k] = sigma < 0.05f ? light : GaussianBlur(light, sigma);
        }
        return stack;
    }

    private static float[] GaussianBlur(float[] src, float sigma)
    {
        var radius = Mathf.Clamp(Mathf.CeilToInt(sigma * 3f), 1, Res / 2);
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++) { kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma)); sum += kernel[i + radius]; }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, Res, j =>
        {
            for (var i = 0; i < Res; i++)
                for (var c = 0; c < 3; c++)
                {
                    var acc = 0f;
                    for (var t = -radius; t <= radius; t++)
                        acc += kernel[t + radius] * src[(j * Res + Mathf.Clamp(i + t, 0, Res - 1)) * 3 + c];
                    tmp[(j * Res + i) * 3 + c] = acc;
                }
        });
        Parallel.For(0, Res, j =>
        {
            for (var i = 0; i < Res; i++)
                for (var c = 0; c < 3; c++)
                {
                    var acc = 0f;
                    for (var t = -radius; t <= radius; t++)
                        acc += kernel[t + radius] * tmp[(Mathf.Clamp(j + t, 0, Res - 1) * Res + i) * 3 + c];
                    dst[(j * Res + i) * 3 + c] = acc;
                }
        });
        return dst;
    }
}
