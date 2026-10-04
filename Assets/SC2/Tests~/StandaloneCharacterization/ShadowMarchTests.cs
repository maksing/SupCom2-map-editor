using System;
using BdfIO.Formats.Hfield;
using UnityEngine;
using Xunit;

namespace SC2CharacterizationTests;

/// <summary>The sun test of the lighting bake leaps over stretches of the heightmap the ray is above; it has to give exactly the
/// answer of testing every step.</summary>
public class ShadowMarchTests
{
    private const float HeightRawScale = 128f;

    private static HfieldModel RandomTerrain(int size, int seed)
    {
        var random = new System.Random(seed);
        var heights = new ushort[size * size];
        // Rolling ground with a few ridges and spikes, so rays are blocked at all sorts of distances.
        for (var z = 0; z < size; z++)
            for (var x = 0; x < size; x++)
            {
                var ground = 60f + 25f * Mathf.Sin(x * 0.045f) * Mathf.Cos(z * 0.03f) + 10f * Mathf.Sin((x + z) * 0.11f);
                heights[z * size + x] = (ushort)Mathf.RoundToInt(ground * HeightRawScale);
            }
        for (var i = 0; i < 40; i++)
        {
            var cx = random.Next(size); var cz = random.Next(size);
            var radius = 1 + random.Next(9);
            var top = 90f + random.Next(120);
            for (var z = Math.Max(0, cz - radius); z < Math.Min(size, cz + radius); z++)
                for (var x = Math.Max(0, cx - radius); x < Math.Min(size, cx + radius); x++)
                    heights[z * size + x] = (ushort)Mathf.RoundToInt(top * HeightRawScale);
        }
        return new HfieldModel { MapX = (uint)size, MapY = (uint)size, Heightmap = heights };
    }

    // The march as it was before the leaps: sample every step.
    private static bool EveryStep(HfieldModel hfield, float worldX, float worldY, float worldZ, Vector3 toSun)
    {
        var horizontal = new Vector2(toSun.x, toSun.z);
        var horizontalLen = horizontal.magnitude;
        if (toSun.y <= 0f) return true;
        if (horizontalLen < 1e-5f) return false;
        horizontal /= horizontalLen;
        var slope = toSun.y / horizontalLen;
        var steps = Mathf.CeilToInt(SC2RnmMaskBaker.ShadowMaxDistance / 4f);
        for (var i = 1; i <= steps; i++)
        {
            var d = i * 4f;
            var sampleX = worldX + horizontal.x * d;
            var sampleZ = worldZ + horizontal.y * d;
            if (sampleX < 0f || sampleX >= hfield.MapX - 1 || sampleZ < 0f || sampleZ >= hfield.MapY - 1) return false;
            var rayHeight = worldY + d * slope;
            if (Bilinear(hfield, sampleX, sampleZ) > rayHeight) return true;
        }
        return false;
    }

    private static float Bilinear(HfieldModel hfield, float x, float z)
    {
        var x0 = Mathf.FloorToInt(x);
        var z0 = Mathf.FloorToInt(z);
        var x1 = Mathf.Min(x0 + 1, (int)hfield.MapX - 1);
        var z1 = Mathf.Min(z0 + 1, (int)hfield.MapY - 1);
        var fx = x - x0;
        var fz = z - z0;
        float H(int gx, int gz) => hfield.Heightmap[gz * (int)hfield.MapX + gx] / HeightRawScale;
        return Mathf.Lerp(Mathf.Lerp(H(x0, z0), H(x1, z0), fx), Mathf.Lerp(H(x0, z1), H(x1, z1), fx), fz);
    }

    [Theory]
    [InlineData(1, 1f, 1f, -1f)]
    [InlineData(2, 0.3f, 0.8f, 0.3f)]
    [InlineData(3, -1f, 0.4f, 0.2f)]
    [InlineData(4, 0.05f, 0.25f, -1f)]
    [InlineData(5, 1f, 3f, 0.001f)]
    [InlineData(6, 0f, 1f, 0.2f)]
    public void LeapingGivesTheAnswerOfTestingEveryStep(int seed, float sx, float sy, float sz)
    {
        const int size = 700;
        var hfield = RandomTerrain(size, seed);
        var grid = new SC2RnmMaskBaker.ShadowGrid(hfield);
        var toSun = new Vector3(sx, sy, sz).normalized;
        var random = new System.Random(seed * 31);

        var shadowed = 0; var lit = 0;
        for (var i = 0; i < 4000; i++)
        {
            var x = (float)random.NextDouble() * (size - 1);
            var z = (float)random.NextDouble() * (size - 1);
            var y = Bilinear(hfield, x, z) + (float)random.NextDouble() * 3f;
            var expected = EveryStep(hfield, x, y, z, toSun);
            Assert.Equal(expected, SC2RnmMaskBaker.IsShadowed(x, y, z, toSun, grid));
            if (expected) shadowed++; else lit++;
        }
        // The terrain is rough enough that both outcomes occur (the low sun at seed 4 mostly shadows, the high ones mostly light).
        Assert.True(shadowed + lit == 4000);
        Assert.True(seed == 6 || shadowed > 0);
    }

    [Fact]
    public void RaysFromTheEdgesOfAMapThatIsNotAMultipleOfTheBlockSize()
    {
        const int size = 213; // not a multiple of 16 or 64
        var hfield = RandomTerrain(size, 9);
        var grid = new SC2RnmMaskBaker.ShadowGrid(hfield);
        var toSun = new Vector3(1f, 0.6f, 1f).normalized;
        var random = new System.Random(5);
        for (var i = 0; i < 3000; i++)
        {
            var edge = random.Next(4);
            var along = (float)random.NextDouble() * (size - 1.01f);
            var x = edge == 0 ? 0f : edge == 1 ? size - 1.01f : along;
            var z = edge == 2 ? 0f : edge == 3 ? size - 1.01f : along;
            var y = Bilinear(hfield, x, z) + 0.5f;
            Assert.Equal(EveryStep(hfield, x, y, z, toSun), SC2RnmMaskBaker.IsShadowed(x, y, z, toSun, grid));
        }
    }
}
