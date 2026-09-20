using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>The point-cloud volume bake (pcloud_up / pcloud_down) on small synthetic terrains.</summary>
public class PointCloudBakerTests
{
    private static SC2PointCloudBaker.Settings Box(float originY = 10f, float sizeY = 40f) => new()
    {
        Origin = new Vector3(0, originY, 0),
        Size = new Vector3(256, sizeY, 256),   // one texel = one unit
        ToSun = new Vector3(0f, 1f, 0f),       // sun straight overhead unless a test tilts it
        SkyColor = new Color(0.4f, 0.5f, 0.6f),
        GroundReflection = 0.5f, SkyInGround = 0f, BlurTexels = 0f,
    };

    private static int Idx(int i, int j) => j * SC2PointCloudBaker.Res + i;

    [Fact]
    public void FlatGround_VoxelsBelowAreSolid_AboveAreOpenSkyAndLit()
    {
        var s = Box(originY: 0f, sizeY: 80f);            // slice height = (k + .5) * 10
        var r = SC2PointCloudBaker.Bake((x, z) => 25f, null, s);
        // slices 0,1,2,... are at y = 5, 15, 25, ... -> the ground (25) is above 5 and 15 only
        Assert.Equal(0, r.DownAlpha[0][Idx(100, 100)]);
        Assert.Equal(0, r.DownAlpha[1][Idx(100, 100)]);
        Assert.Equal(255, r.DownAlpha[3][Idx(100, 100)]);
        // open air over flat ground sees the whole sky: the plain sky colour
        Assert.InRange((int)r.UpRgb[5][Idx(100, 100) * 3], 102 - 1, 102 + 1);
        Assert.InRange((int)r.UpRgb[5][Idx(100, 100) * 3 + 2], 153 - 1, 153 + 1);
        // ground voxels keep the sky colour too (no dark bleed into units standing on the ground)
        Assert.InRange((int)r.UpRgb[0][Idx(100, 100) * 3], 102 - 1, 102 + 1);
        Assert.InRange(r.SolidFraction, 0.24f, 0.26f);      // two of the eight slices
    }

    [Fact]
    public void TallWall_ShadowsTheGroundBehindIt_AlongTheSun()
    {
        var s = Box(originY: 10f, sizeY: 40f);           // slice 0 at y = 12.5
        s.ToSun = new Vector3(1f, 1f, 0f);               // sun up and toward +x: shadows fall toward -x
        // ground at 0, a wall at x in [150,160) of height 30
        var r = SC2PointCloudBaker.Bake((x, z) => x >= 150f && x < 160f ? 30f : 0f, null, s);
        // 20 units of wall above the slice height at 45 degrees -> shadow reaches ~20 units toward -x
        Assert.Equal(0, r.DownAlpha[0][Idx(140, 100)]);
        Assert.Equal(255, r.DownAlpha[0][Idx(100, 100)]);   // far from the wall
        Assert.Equal(255, r.DownAlpha[0][Idx(200, 100)]);   // on the sun side of the wall
        // switching the sun shadow off leaves only the solid wall dark
        s.SunShadow = false;
        var noShadow = SC2PointCloudBaker.Bake((x, z) => x >= 150f && x < 160f ? 30f : 0f, null, s);
        Assert.Equal(255, noShadow.DownAlpha[0][Idx(140, 100)]);
        Assert.Equal(0, noShadow.DownAlpha[0][Idx(155, 100)]);
    }

    [Fact]
    public void SkyDarkensNextToACliff_AndStaysFullInTheOpen()
    {
        var s = Box(originY: 10f, sizeY: 40f);
        var r = SC2PointCloudBaker.Bake((x, z) => x >= 150f ? 40f : 0f, null, s);
        var nearWall = r.UpRgb[0][Idx(148, 100) * 3];
        var farAway = r.UpRgb[0][Idx(20, 100) * 3];
        Assert.True(nearWall < farAway - 15, $"near {nearWall}, far {farAway}");
        Assert.InRange((int)farAway, 98, 106);
    }

    [Fact]
    public void GroundLight_IsBlurredMoreOnHigherSlices()
    {
        var s = Box(originY: 100f, sizeY: 80f);           // everything above a flat ground at 0
        s.BlurTexels = 3f;
        var light = new float[SC2PointCloudBaker.Res * SC2PointCloudBaker.Res * 3];
        for (var j = 0; j < SC2PointCloudBaker.Res; j++)
            for (var i = 0; i < SC2PointCloudBaker.Res; i++)
                for (var c = 0; c < 3; c++)
                    light[(j * SC2PointCloudBaker.Res + i) * 3 + c] = i >= 128 ? 1f : 0f;   // a sharp vertical edge
        var r = SC2PointCloudBaker.Bake((x, z) => 0f, light, s);
        // slice 0 is unblurred: the edge stays a step
        Assert.Equal(0, r.DownRgb[0][Idx(126, 100) * 3]);
        Assert.InRange((int)r.DownRgb[0][Idx(129, 100) * 3], 128 - 2, 128 + 2);
        // slice 7 is blurred with sigma 21 texels: the light has spread well to the dark side
        Assert.True(r.DownRgb[7][Idx(118, 100) * 3] > 20);
        Assert.True(r.DownRgb[7][Idx(138, 100) * 3] < 118);
    }

    [Fact]
    public void FlatFallbackLight_GivesTheReflectionShare_AndTheSkyShareAdds()
    {
        var s = Box(originY: 10f, sizeY: 40f);
        s.SkyInGround = 0.5f;
        var r = SC2PointCloudBaker.Bake((x, z) => 0f, null, s);
        // 0.5 * 0.5 (flat light) + 0.5 * 0.4 (sky red) = 0.45
        Assert.InRange((int)r.DownRgb[3][Idx(100, 100) * 3], Mathf.RoundToInt(0.45f * 255f) - 1, Mathf.RoundToInt(0.45f * 255f) + 1);
    }

    [Fact]
    public void Dxt5Volume_HasTheVanillaHeaderLayout()
    {
        var res = SC2PointCloudBaker.Res; var depth = SC2PointCloudBaker.Depth;
        var rgb = new byte[depth][]; var a = new byte[depth][];
        for (var k = 0; k < depth; k++) { rgb[k] = new byte[res * res * 3]; a[k] = new byte[res * res]; }
        var dds = SC2DdsEncoder.BuildDxt5Volume(res, res, depth, rgb, a);
        Assert.Equal(128 + 65536 * depth, dds.Length);                       // 524416 like the vanilla files
        Assert.Equal((byte)'D', dds[0]);
        Assert.Equal(0x00881007u, System.BitConverter.ToUInt32(dds, 8));
        Assert.Equal((uint)res, System.BitConverter.ToUInt32(dds, 12));
        Assert.Equal(65536u, System.BitConverter.ToUInt32(dds, 20));
        Assert.Equal((uint)depth, System.BitConverter.ToUInt32(dds, 24));
        Assert.Equal("DXT5", System.Text.Encoding.ASCII.GetString(dds, 84, 4));
        Assert.Equal(0x1000u, System.BitConverter.ToUInt32(dds, 108));
        Assert.Equal(0x200000u, System.BitConverter.ToUInt32(dds, 112));
    }
}

/// <summary>The point-cloud settings and the baked volumes are stored in the map document.</summary>
public class PointCloudPersistenceTests
{
    [Fact]
    public void Serializer_RoundTripsTheSettingsBoxAndVolumeReferences()
    {
        var doc = new SC2.MapEditor.Document.SC2MapDocument();
        var t = doc.Terrain;
        t.PointCloudSkyColor = new Color(0.2f, 0.4f, 0.6f, 1f);
        t.PointCloudSkyIntensity = 0.8f; t.PointCloudGroundReflection = 0.7f; t.PointCloudSkyInGround = 0.2f;
        t.PointCloudBlurTexels = 2.5f; t.PointCloudSunShadow = false;
        t.PointCloudBoxOrigin = new Vector3(12, 30, 12); t.PointCloudBoxSize = new Vector3(1000, 60, 900);
        t.PointCloudUp = new SC2.MapEditor.Document.SC2BlobReference("aa11", 524416);
        t.PointCloudDown = new SC2.MapEditor.Document.SC2BlobReference("bb22", 524416);

        var back = SC2.MapEditor.Document.SC2MapDocumentSerializer.Deserialize(SC2.MapEditor.Document.SC2MapDocumentSerializer.Serialize(doc)).Terrain;
        Assert.Equal(0.4f, back.PointCloudSkyColor.g, 3);
        Assert.Equal(0.8f, back.PointCloudSkyIntensity, 3);
        Assert.Equal(0.7f, back.PointCloudGroundReflection, 3);
        Assert.Equal(0.2f, back.PointCloudSkyInGround, 3);
        Assert.Equal(2.5f, back.PointCloudBlurTexels, 3);
        Assert.False(back.PointCloudSunShadow);
        Assert.Equal(new Vector3(12, 30, 12), back.PointCloudBoxOrigin);
        Assert.Equal(new Vector3(1000, 60, 900), back.PointCloudBoxSize);
        Assert.Equal("aa11", back.PointCloudUp.ContentHash);
        Assert.Equal("bb22", back.PointCloudDown.ContentHash);
    }

    [Fact]
    public void Serializer_ReadsAnOlderDocumentAsNotBakedWithDefaults()
    {
        var back = SC2.MapEditor.Document.SC2MapDocumentSerializer.Deserialize(
            SC2.MapEditor.Document.SC2MapDocumentSerializer.Serialize(new SC2.MapEditor.Document.SC2MapDocument())).Terrain;
        Assert.True(back.PointCloudUp.IsEmpty);
        Assert.True(back.PointCloudDown.IsEmpty);
        Assert.Equal(0.5f, back.PointCloudSkyIntensity, 3);
        Assert.True(back.PointCloudSunShadow);
        Assert.Equal(Vector3.zero, back.PointCloudBoxSize);
    }
}
