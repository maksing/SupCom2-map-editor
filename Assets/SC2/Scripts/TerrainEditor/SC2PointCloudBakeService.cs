using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;

/// <summary>Lighting Bake > Bake Point Cloud: runs SC2PointCloudBaker over the terrain (core, background, imported
/// terrain.bdf objects; no props) and stores the two volumes as DXT5 volume DDS files on SC2TerrainData. The
/// ground light comes from the map's baked RNM lighting when Bake Lighting has run, a flat mid value otherwise.
/// Export writes the files; the bake never runs on export.</summary>
public static class SC2PointCloudBakeService
{
    private const float HeightRawScale = 128f;   // SC2HfieldProjectionService.Project's raw height encoding
    private const float BoxMargin = 12f;
    private const float DefaultBoxHeight = 60f;

    /// <summary>The default box: the whole map minus a 12-unit margin (as on the vanilla 1K maps), from the lowest
    /// terrain in it up 60 units.</summary>
    public static void FitBox(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, out Vector3 origin, out Vector3 size)
    {
        var spanX = Mathf.Max(1f, data.GridWidth * data.CellSize);
        var spanZ = Mathf.Max(1f, data.GridHeight * data.CellSize);
        var margin = Mathf.Min(BoxMargin, Mathf.Min(spanX, spanZ) * 0.25f);
        var boxX = spanX - 2f * margin;
        var boxZ = spanZ - 2f * margin;

        var heightAt = BuildHeightSampler(data, importedTerrains);
        var lowest = float.PositiveInfinity;
        const int probes = 48;
        for (var j = 0; j < probes; j++)
            for (var i = 0; i < probes; i++)
            {
                var h = heightAt(margin + (i + 0.5f) / probes * boxX, margin + (j + 0.5f) / probes * boxZ);
                if (!float.IsNegativeInfinity(h) && h < lowest) lowest = h;
            }
        if (float.IsPositiveInfinity(lowest)) lowest = 0f;

        origin = new Vector3(margin, Mathf.Floor(lowest), margin);
        size = new Vector3(boxX, DefaultBoxHeight, boxZ);
    }

    /// <summary>Bakes both volumes into <paramref name="data"/> (PointCloudUpDds / PointCloudDownDds). Sets the box
    /// to the fitted one first when none is set. Returns a status line for the panel.</summary>
    public static string Bake(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session)
    {
        if (data == null || !data.IsInitialized) return "No active terrain to bake.";

        if (data.PointCloudBoxSize.x < 1f || data.PointCloudBoxSize.y < 1f || data.PointCloudBoxSize.z < 1f)
        {
            FitBox(data, importedTerrains, out var origin, out var size);
            data.PointCloudBoxOrigin = origin;
            data.PointCloudBoxSize = size;
        }

        var info = SC2TerrainExporter.BuildInfoModel(data, session, quiet: true);
        Vector3 toSun;
        if (data.RnmBakeSyncSunDirection)
        {
            var (sx, sy, sz) = info.SunDirection;
            toSun = new Vector3(sx, sy, sz);
        }
        else toSun = data.RnmBakeOverrideSunDirection;

        var settings = new SC2PointCloudBaker.Settings
        {
            Origin = data.PointCloudBoxOrigin,
            Size = data.PointCloudBoxSize,
            ToSun = toSun,
            SkyColor = new Color(
                Mathf.Clamp01(data.PointCloudSkyColor.r * data.PointCloudSkyIntensity),
                Mathf.Clamp01(data.PointCloudSkyColor.g * data.PointCloudSkyIntensity),
                Mathf.Clamp01(data.PointCloudSkyColor.b * data.PointCloudSkyIntensity)),
            GroundReflection = data.PointCloudGroundReflection,
            SkyInGround = data.PointCloudSkyInGround,
            BlurTexels = data.PointCloudBlurTexels,
            SunShadow = data.PointCloudSunShadow,
        };

        var groundLight = BuildGroundLight(data, settings.Origin, settings.Size);
        var result = SC2PointCloudBaker.Bake(BuildHeightSampler(data, importedTerrains), groundLight, settings);

        var res = SC2PointCloudBaker.Res;
        var depth = SC2PointCloudBaker.Depth;
        var opaque = new byte[res * res];
        for (var i = 0; i < opaque.Length; i++) opaque[i] = 255;
        var opaqueSlices = new byte[depth][];
        for (var k = 0; k < depth; k++) opaqueSlices[k] = opaque;
        data.PointCloudUpDds = SC2DdsEncoder.BuildDxt5Volume(res, res, depth, result.UpRgb, opaqueSlices);
        data.PointCloudDownDds = SC2DdsEncoder.BuildDxt5Volume(res, res, depth, result.DownRgb, result.DownAlpha);

        return $"Baked the point cloud ({res}x{res}x{depth}): {result.SolidFraction * 100f:0}% of the voxels are inside the ground, " +
               $"{result.ShadowFraction * 100f:0}% in the sun's shadow; ground light from " +
               (groundLight != null ? "the baked RNM lighting." : "a flat value (run Bake Lighting first for the real one).");
    }

    // The terrain surface height at a world (x, z), bilinear over the unified shadow heightfield (core + background +
    // imported terrain objects); -infinity outside it.
    private static System.Func<float, float, float> BuildHeightSampler(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains)
    {
        var hfield = SC2RnmBakeService.BuildShadowHfield(data, importedTerrains, out var offset, out _, out _);
        return (x, z) => SampleHeight(hfield, x - offset.x, z - offset.z);
    }

    private static float SampleHeight(HfieldModel hfield, float x, float z)
    {
        var mapX = (int)hfield.MapX; var mapY = (int)hfield.MapY;
        if (x < 0f || z < 0f || x >= mapX - 1 || z >= mapY - 1) return float.NegativeInfinity;
        var x0 = (int)x; var z0 = (int)z;
        var fx = x - x0; var fz = z - z0;
        float H(int gx, int gz) => hfield.Heightmap[gz * mapX + gx] / HeightRawScale;
        var top = Mathf.Lerp(H(x0, z0), H(x0 + 1, z0), fx);
        var bottom = Mathf.Lerp(H(x0, z0 + 1), H(x0 + 1, z0 + 1), fx);
        return Mathf.Lerp(top, bottom, fz);
    }

    // The map's baked RNM lighting (mean of the three basis maps) seen from above over the box, per raster texel; null
    // when no pair has a bake. The mask's UV2 is the position over the core footprint.
    private static float[] BuildGroundLight(SC2TerrainData data, Vector3 origin, Vector3 size)
    {
        var res = SC2PointCloudBaker.Res;
        var spanX = Mathf.Max(1f, data.GridWidth * data.CellSize);
        var spanZ = Mathf.Max(1f, data.GridHeight * data.CellSize);
        var light = new float[res * res * 3];
        var any = false;
        for (var j = 0; j < res; j++)
        {
            var z = origin.z + (j + 0.5f) / res * size.z;
            for (var i = 0; i < res; i++)
            {
                var x = origin.x + (i + 0.5f) / res * size.x;
                var idx = (j * res + i) * 3;
                light[idx] = light[idx + 1] = light[idx + 2] = 0.5f;
                if (x < 0f || z < 0f || x >= spanX || z >= spanZ) continue;

                var cell = Mathf.Clamp((int)(z / data.CellSize), 0, data.GridHeight - 1) * data.GridWidth
                           + Mathf.Clamp((int)(x / data.CellSize), 0, data.GridWidth - 1);
                var pairIndex = TrianglePair(data, cell * 2);
                if (pairIndex < 0) pairIndex = TrianglePair(data, cell * 2 + 1);
                if (pairIndex < 0 || pairIndex >= data.Pairs.Count) continue;
                var pair = data.Pairs[pairIndex];
                var mw = pair.MaskWidth; var mh = pair.MaskHeight;
                var n = mw * mh * 3;
                if (mw <= 0 || mh <= 0 || pair.RnmBakedRgb0 == null || pair.RnmBakedRgb0.Length != n
                    || pair.RnmBakedRgb1 == null || pair.RnmBakedRgb1.Length != n
                    || pair.RnmBakedRgb2 == null || pair.RnmBakedRgb2.Length != n) continue;

                var mx = Mathf.Clamp((int)(x / spanX * mw), 0, mw - 1);
                var my = Mathf.Clamp((int)(z / spanZ * mh), 0, mh - 1);
                var m = (my * mw + mx) * 3;
                for (var c = 0; c < 3; c++)
                    light[idx + c] = (pair.RnmBakedRgb0[m + c] + pair.RnmBakedRgb1[m + c] + pair.RnmBakedRgb2[m + c]) / (3f * 255f);
                any = true;
            }
        }
        return any ? light : null;
    }

    private static int TrianglePair(SC2TerrainData data, int triangle) =>
        data.TrianglePairIndex != null && (uint)triangle < (uint)data.TrianglePairIndex.Length ? data.TrianglePairIndex[triangle] : -1;
}
