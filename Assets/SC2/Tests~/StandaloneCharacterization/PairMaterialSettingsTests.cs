using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;

namespace SC2CharacterizationTests;

/// <summary>A pair's export-only material settings (State, Texture Scroll, ALPHA_TEST threshold, shader file and prefix)
/// and its Ramp / Environment textures survive the document package (forward bridge, JSON, reverse bridge), and the
/// texture browser files ramp textures under one category in a fixed order.</summary>
public class PairMaterialSettingsTests
{
    private static SC2MaterialPair FullPair() => new SC2MaterialPair
    {
        Name = "Metal",
        State = "TerrainAlphaBlendState",
        TextureScroll = new Vector2(0.25f, -1.5f),
        AlphaTestThreshold = 77f,
        ShaderFile = "/effects/water.hlsl",
        ShaderPrefix = "water",
        RampPath = "/textures/Terrain/Ramps/Metal_001_Ramps.dds",
        RampSourceTag = "archive:textures/Terrain/Ramps/Metal_001_Ramps.dds",
        RampForceExport = true,
        EnvironmentPath = "/textures/environment/DefaultEnvCube.dds",
        EnvironmentSourceTag = "",
        EnvironmentForceExport = false,
    };

    private static void AssertFull(SC2MaterialPair p)
    {
        Assert.Equal("TerrainAlphaBlendState", p.State);
        Assert.Equal(0.25f, p.TextureScroll.x, 4);
        Assert.Equal(-1.5f, p.TextureScroll.y, 4);
        Assert.Equal(77f, p.AlphaTestThreshold, 3);
        Assert.Equal("/effects/water.hlsl", p.ShaderFile);
        Assert.Equal("water", p.ShaderPrefix);
        Assert.Equal("/textures/Terrain/Ramps/Metal_001_Ramps.dds", p.RampPath);
        Assert.Equal("archive:textures/Terrain/Ramps/Metal_001_Ramps.dds", p.RampSourceTag);
        Assert.True(p.RampForceExport);
        Assert.Equal("/textures/environment/DefaultEnvCube.dds", p.EnvironmentPath);
        Assert.Equal("", p.EnvironmentSourceTag);
        Assert.False(p.EnvironmentForceExport);
    }

    [Fact]
    public void ForwardJsonAndReverseBridge_RoundTripThePairSettings()
    {
        var root = Directory.CreateTempSubdirectory("sc2pairsettings_").FullName;
        try
        {
            var store = new SC2DirectoryBlobStore(root);
            var doc = SC2TerrainLegacyBridge.BuildFromLegacy(
                new List<SC2Material>(), new List<SC2MaterialPair> { FullPair() },
                Array.Empty<float>(), Array.Empty<short>(),
                gridWidth: 2, gridHeight: 2, cellSize: 1f,
                symmetry: SymmetryMode.None, symmetryCenter: Vector3.zero,
                blobStore: store, isInitialized: true, dataVersion: 1, radialCount: 2,
                importOnlyMode: false, singleBspNodePerLayer: false, vanillaCandidateBspSplit: false,
                playableAreaName: "");

            // through the manifest JSON, as a save and a load do
            var map = new SC2MapDocument { Terrain = doc };
            var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(map));
            var result = SC2MapDocumentToLegacyBridge.ReconstructTerrain(restored.Terrain, store);

            Assert.Single(result.Pairs);
            AssertFull(result.Pairs[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Serializer_ReadsAnOlderPairWithoutTheKeysAsDefaults()
    {
        var map = new SC2MapDocument();
        map.Terrain.MaterialPairs.Add(new SC2MaterialPairDocument { Id = "p1", Name = "Old" });
        var json = SC2MapDocumentSerializer.Serialize(map);
        foreach (var key in new[] { "state", "textureScrollX", "textureScrollY", "alphaTestThreshold", "shaderFile", "shaderPrefix",
                     "rampPath", "rampSourceTag", "rampForceExport", "environmentPath", "environmentSourceTag", "environmentForceExport" })
            json = System.Text.RegularExpressions.Regex.Replace(json, ",\\s*\"" + key + "\"\\s*:\\s*(\"[^\"]*\"|[-0-9.eE]+|true|false)", "");

        var pair = SC2MapDocumentSerializer.Deserialize(json).Terrain.MaterialPairs[0];
        Assert.Equal("", pair.State);
        Assert.Equal(Vector2.zero, pair.TextureScroll);
        Assert.Equal(16f, pair.AlphaTestThreshold);
        Assert.Equal("", pair.RampPath);
        Assert.Equal("", pair.EnvironmentPath);
        Assert.False(pair.RampForceExport);
    }

    // ---- ramp textures in the browser ----

    [Theory]
    [InlineData("textures/Terrain/Ramps/Metal_001_Ramps.dds", 0)]
    [InlineData("textures/Terrain/Ramps/Default_001_Ramps.win.dds", 0)]
    [InlineData("textures/lights/Default_Light_Ramp.dds", 0)]
    [InlineData("textures/Engine/waterramp_desert.dds", 1)]
    [InlineData("textures/Engine/waterramp.dds", 1)]
    [InlineData("textures/particles/ramp_blue_01.win.dds", 2)]
    [InlineData("textures/particles/ramp/ra_0035_white.win.dds", 2)]
    [InlineData("textures/particles/testramp.win.dds", 2)]
    [InlineData("textures/Terrain/Water/wave_animation_001.win.dds", 3)]
    [InlineData("textures/Terrain/Water/wave_animation_002.win.dds", 3)]
    public void RampGroup_ClassifiesTheRampTextures(string path, int group)
    {
        Assert.Equal(group, SC2TextureCatalogEntry.RampGroup(path));
    }

    [Theory]
    [InlineData("textures/Engine/black_d.win.dds")]
    [InlineData("textures/Engine/Gray_d.win.dds")]
    [InlineData("textures/Terrain/CA_C05/C05_Ramp.win.dds")]        // an ordinary terrain texture that just has "ramp" in its name
    [InlineData("textures/Terrain/D1_101/d1_101_sandRamp_d.win.dds")]
    [InlineData("textures/Terrain/Water/foam_001.win.dds")]
    [InlineData("textures/particles/fire_01.win.dds")]
    [InlineData("textures/Engine/sub/waterramp.dds")]
    public void RampGroup_IgnoresEverythingElse(string path)
    {
        Assert.Equal(-1, SC2TextureCatalogEntry.RampGroup(path));
    }

    [Fact]
    public void Ramp_TexturesAreFiledUnderTheRampCategory()
    {
        var entry = new SC2TextureCatalogEntry { ArchiveEntryPath = "textures/Engine/waterramp_lava.dds", Source = SC2TextureSource.Vanilla };
        entry.Normalize();
        Assert.Equal(SC2TextureCatalogEntry.RampCategory, entry.Category);

        var wave = new SC2TextureCatalogEntry { ArchiveEntryPath = "textures/Terrain/Water/wave_animation_002.win.dds", Source = SC2TextureSource.Vanilla };
        wave.Normalize();
        Assert.Equal(SC2TextureCatalogEntry.RampCategory, wave.Category);

        var ordinary = new SC2TextureCatalogEntry { ArchiveEntryPath = "textures/Terrain/Water/foam_001.win.dds", Source = SC2TextureSource.Vanilla };
        ordinary.Normalize();
        Assert.NotEqual(SC2TextureCatalogEntry.RampCategory, ordinary.Category);
    }
}
