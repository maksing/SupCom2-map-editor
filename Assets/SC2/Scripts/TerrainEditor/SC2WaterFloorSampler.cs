using System;
using UnityEngine;
using BdfIO.Formats.Terrain;

/// <summary>
/// The height the water depth texture classifies land and water by, read from the 3D model itself instead of the one-height-per-cell
/// top surface of the heightfield. The top surface of a column that holds a bridge over a river is the bridge deck, so the river under
/// it counted as land and got no water (and shore foam around the bridge). Here every column keeps two heights: the highest surface of
/// any layer (what the heightfield sees) and the highest surface that is not above the water. Where a surface at or below the water
/// exists, that one is used - the water floor under a bridge or any other overhang - and otherwise the top surface, so a plain
/// terrain, with one surface per column, reads exactly as before.
///
/// Surfaces are the triangles of the final terrain model (core terrain, imported terrain objects, the background); the water plane
/// itself is skipped. A column no triangle covers has no value: <see cref="Sample"/> then asks the fallback sampler.
/// </summary>
public sealed class SC2WaterFloorSampler
{
    private readonly int _width, _height;
    private readonly float[] _effective;
    private readonly float[] _top;
    private readonly bool[] _covered;

    private SC2WaterFloorSampler(int width, int height, float[] effective, float[] top, bool[] covered)
    {
        _width = width; _height = height; _effective = effective; _top = top; _covered = covered;
    }

    /// <param name="mapX">Grid points along X (cells + 1); the grid is one world unit per cell, from world (0, 0).</param>
    /// <param name="isWaterMaterial">True for the layers that are the water surface (not ground).</param>
    public static SC2WaterFloorSampler Build(TerrainModel model, int mapX, int mapY, float waterElevation, Func<TerrainMaterial, bool> isWaterMaterial)
    {
        var top = new float[mapX * mapY];
        var floor = new float[mapX * mapY];
        var hasTop = new bool[mapX * mapY];
        var hasFloor = new bool[mapX * mapY];

        for (var li = 0; li < model.Layers.Count; li++)
        {
            var layer = model.Layers[li];
            var material = li < model.Materials.Count ? model.Materials[li] : null;
            if (isWaterMaterial != null && isWaterMaterial(material)) continue;

            var vertBase = layer.CumulativeVertices;
            var triStart = layer.CumulativeIndices / 3;
            var triEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeIndices / 3 : model.Triangles.Count;
            for (var t = triStart; t < triEnd; t++)
            {
                var tri = model.Triangles[t];
                Rasterize(model.Vertices[vertBase + (int)tri.V0], model.Vertices[vertBase + (int)tri.V1], model.Vertices[vertBase + (int)tri.V2],
                    top, hasTop, floor, hasFloor, mapX, mapY, waterElevation);
            }
        }

        var effective = new float[mapX * mapY];
        for (var i = 0; i < effective.Length; i++)
        {
            if (!hasTop[i]) continue;
            // A surface at or below the water wins over a higher one (a deck above it); with only a surface above the water, that one.
            effective[i] = hasFloor[i] ? floor[i] : top[i];
        }
        return new SC2WaterFloorSampler(mapX, mapY, effective, top, hasTop);
    }

    private static void Rasterize(TerrainVertex v0, TerrainVertex v1, TerrainVertex v2,
        float[] top, bool[] hasTop, float[] floor, bool[] hasFloor, int mapW, int mapH, float ceiling)
    {
        var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(v0.PosX, Mathf.Min(v1.PosX, v2.PosX))));
        var maxX = Mathf.Min(mapW - 1, Mathf.CeilToInt(Mathf.Max(v0.PosX, Mathf.Max(v1.PosX, v2.PosX))));
        var minZ = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(v0.PosZ, Mathf.Min(v1.PosZ, v2.PosZ))));
        var maxZ = Mathf.Min(mapH - 1, Mathf.CeilToInt(Mathf.Max(v0.PosZ, Mathf.Max(v1.PosZ, v2.PosZ))));
        if (minX > maxX || minZ > maxZ) return;

        var denom = (v1.PosZ - v2.PosZ) * (v0.PosX - v2.PosX) + (v2.PosX - v1.PosX) * (v0.PosZ - v2.PosZ);
        if (Mathf.Abs(denom) < 1e-8f) return;

        for (var gz = minZ; gz <= maxZ; gz++)
        for (var gx = minX; gx <= maxX; gx++)
        {
            var w0 = ((v1.PosZ - v2.PosZ) * (gx - v2.PosX) + (v2.PosX - v1.PosX) * (gz - v2.PosZ)) / denom;
            var w1 = ((v2.PosZ - v0.PosZ) * (gx - v2.PosX) + (v0.PosX - v2.PosX) * (gz - v2.PosZ)) / denom;
            var w2 = 1f - w0 - w1;
            const float eps = 1e-4f;
            if (w0 < -eps || w1 < -eps || w2 < -eps) continue;

            var height = w0 * v0.PosY + w1 * v1.PosY + w2 * v2.PosY;
            var idx = gz * mapW + gx;
            if (!hasTop[idx] || height > top[idx]) { top[idx] = height; hasTop[idx] = true; }
            if (height <= ceiling && (!hasFloor[idx] || height > floor[idx])) { floor[idx] = height; hasFloor[idx] = true; }
        }
    }

    /// <summary>The height of the highest surface of the model (what the heightfield sees) at world (<paramref name="x"/>,
    /// <paramref name="z"/>): bilinear between the four grid points around it. False when any of them is not covered by a triangle.</summary>
    public bool TrySampleTop(float x, float z, out float height)
    {
        height = 0f;
        var gx = Mathf.Clamp(x, 0f, _width - 1);
        var gz = Mathf.Clamp(z, 0f, _height - 1);
        var x0 = Mathf.FloorToInt(gx); var z0 = Mathf.FloorToInt(gz);
        var x1 = Mathf.Min(_width - 1, x0 + 1); var z1 = Mathf.Min(_height - 1, z0 + 1);
        var i00 = z0 * _width + x0; var i10 = z0 * _width + x1; var i01 = z1 * _width + x0; var i11 = z1 * _width + x1;
        if (!_covered[i00] || !_covered[i10] || !_covered[i01] || !_covered[i11]) return false;
        var tx = gx - x0; var tz = gz - z0;
        height = Mathf.Lerp(Mathf.Lerp(_top[i00], _top[i10], tx), Mathf.Lerp(_top[i01], _top[i11], tx), tz);
        return true;
    }

    /// <summary>The height at world (<paramref name="x"/>, <paramref name="z"/>): bilinear between the four grid points around it, or
    /// whatever <paramref name="fallback"/> says when any of them is not covered by a triangle.</summary>
    public float Sample(float x, float z, Func<float, float, float> fallback)
    {
        var gx = Mathf.Clamp(x, 0f, _width - 1);
        var gz = Mathf.Clamp(z, 0f, _height - 1);
        var x0 = Mathf.FloorToInt(gx); var z0 = Mathf.FloorToInt(gz);
        var x1 = Mathf.Min(_width - 1, x0 + 1); var z1 = Mathf.Min(_height - 1, z0 + 1);
        var i00 = z0 * _width + x0; var i10 = z0 * _width + x1; var i01 = z1 * _width + x0; var i11 = z1 * _width + x1;
        if (!_covered[i00] || !_covered[i10] || !_covered[i01] || !_covered[i11]) return fallback(x, z);
        var tx = gx - x0; var tz = gz - z0;
        return Mathf.Lerp(Mathf.Lerp(_effective[i00], _effective[i10], tx), Mathf.Lerp(_effective[i01], _effective[i11], tx), tz);
    }
}
