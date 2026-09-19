using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Textures;

namespace SC2CharacterizationTests;

/// <summary>
/// The texture browser's SOURCE (Vanilla / Community / Imported) and USAGE
/// (Environment folds in Sky; Other = Rnm/Heightmap/Unknown) filters, as
/// evaluated by SC2TextureCatalogQueryEngine.
/// </summary>
public class TextureBrowserFilterTests
{
    private static SC2TextureCatalogEntry E(string name, SC2TextureSource source, SC2TextureUsage usage, bool community = false) =>
        new() { Id = source + ":" + name, Name = name, Source = source, Usage = usage, IsCommunity = community };

    private static readonly SC2TextureCatalogEntry[] Entries =
    {
        E("diffuse", SC2TextureSource.Vanilla, SC2TextureUsage.Diffuse),
        E("env", SC2TextureSource.Vanilla, SC2TextureUsage.Environment),
        E("sky", SC2TextureSource.Vanilla, SC2TextureUsage.Sky),
        E("lut", SC2TextureSource.Vanilla, SC2TextureUsage.ColorCorrectionCube),
        E("rnm", SC2TextureSource.Vanilla, SC2TextureUsage.Rnm),
        E("height", SC2TextureSource.Vanilla, SC2TextureUsage.Heightmap),
        E("mystery", SC2TextureSource.Vanilla, SC2TextureUsage.Unknown),
        E("community_diffuse", SC2TextureSource.Vanilla, SC2TextureUsage.Diffuse, community: true),
        E("imported_diffuse", SC2TextureSource.Custom, SC2TextureUsage.Diffuse),
    };

    private static List<string> Run(SC2TextureQuery query)
    {
        var engine = new SC2TextureCatalogQueryEngine();
        engine.SetEntries(Entries, 1);
        return engine.Execute(query).Select(i => Entries[i].Name).ToList();
    }

    [Theory]
    [InlineData(SC2TextureUsage.Sky, SC2TextureUsage.Environment)]
    [InlineData(SC2TextureUsage.Environment, SC2TextureUsage.Environment)]
    [InlineData(SC2TextureUsage.Rnm, SC2TextureUsage.Unknown)]
    [InlineData(SC2TextureUsage.Heightmap, SC2TextureUsage.Unknown)]
    [InlineData(SC2TextureUsage.Unknown, SC2TextureUsage.Unknown)]
    [InlineData(SC2TextureUsage.Diffuse, SC2TextureUsage.Diffuse)]
    [InlineData(SC2TextureUsage.ColorCorrectionCube, SC2TextureUsage.ColorCorrectionCube)]
    public void Normalize_FoldsSkyIntoEnvironmentAndUnbuttonedUsagesIntoOther(SC2TextureUsage input, SC2TextureUsage expected) =>
        Assert.Equal(expected, SC2TextureUsageFilter.Normalize(input));

    [Fact]
    public void EnvironmentFilter_IncludesSky_ButNotColorCorrectionCube()
    {
        var names = Run(new SC2TextureQuery { Usage = SC2TextureUsage.Environment });
        Assert.Equal(new[] { "env", "sky" }, names.OrderBy(n => n));
    }

    [Fact]
    public void OtherFilter_CollectsEverythingWithoutAButton()
    {
        var names = Run(new SC2TextureQuery { Usage = SC2TextureUsage.Unknown });
        Assert.Equal(new[] { "height", "mystery", "rnm" }, names.OrderBy(n => n));
    }

    [Fact]
    public void APickerAskingForSkyOrRnm_LandsOnTheSameBucketAsTheButtons()
    {
        Assert.Equal(Run(new SC2TextureQuery { Usage = SC2TextureUsage.Environment }).OrderBy(n => n),
                     Run(new SC2TextureQuery { Usage = SC2TextureUsage.Sky }).OrderBy(n => n));
        Assert.Equal(Run(new SC2TextureQuery { Usage = SC2TextureUsage.Unknown }).OrderBy(n => n),
                     Run(new SC2TextureQuery { Usage = SC2TextureUsage.Rnm }).OrderBy(n => n));
    }

    [Fact]
    public void SourceTabs_SplitVanillaCommunityAndImported()
    {
        var vanilla = Run(new SC2TextureQuery { Source = SC2TextureSource.Vanilla, Community = false });
        var community = Run(new SC2TextureQuery { Source = SC2TextureSource.Vanilla, Community = true });
        var imported = Run(new SC2TextureQuery { Source = SC2TextureSource.Custom });

        Assert.DoesNotContain("community_diffuse", vanilla);
        Assert.DoesNotContain("imported_diffuse", vanilla);
        Assert.Equal(new[] { "community_diffuse" }, community);
        Assert.Equal(new[] { "imported_diffuse" }, imported);
        Assert.Equal(Entries.Length, Run(new SC2TextureQuery()).Count);
    }

    [Theory]
    [InlineData("textures/Engine/FullGlow_m.win.dds")]
    [InlineData("textures/Engine/Gray_d.win.dds")]
    [InlineData("textures/Engine/Red_d.win.dds")]
    [InlineData("textures/Engine/Spec00_m.win.dds")]
    [InlineData("textures/Engine/Spec50_m.win.dds")]
    [InlineData("textures/Engine/Spec100_m.win.dds")]
    [InlineData("TEXTURES/ENGINE/SPEC50_M.WIN.DDS")]
    [InlineData("/textures/engine/spec50_m.dds")]
    public void EngineWhitelist_AcceptsTheSixListedTextures(string path) =>
        Assert.True(SC2TextureCatalogEntry.IsEngineTexturePath(path));

    [Theory]
    [InlineData("textures/Engine/Other_m.win.dds")]
    [InlineData("textures/Engine/Spec25_m.win.dds")]
    [InlineData("textures/Engine/sub/Gray_d.win.dds")]
    [InlineData("textures/Terrain/Gray_d.win.dds")]
    [InlineData("")]
    public void EngineWhitelist_RejectsEverythingElse(string path) =>
        Assert.False(SC2TextureCatalogEntry.IsEngineTexturePath(path));

    [Fact]
    public void EngineTexture_IsFiledUnderTheEngineCategory()
    {
        var entry = new SC2TextureCatalogEntry
        {
            Source = SC2TextureSource.Vanilla,
            ArchiveEntryPath = "textures/Engine/Gray_d.win.dds",
        };
        entry.Normalize();
        Assert.Equal(SC2TextureCatalogEntry.EngineCategory, entry.Category);
    }

    [Fact]
    public void EngineCategoryFilter_SelectsOnlyEngineEntries()
    {
        var engine = new SC2TextureCatalogEntry
        {
            Id = "vanilla:textures/engine/spec00_m.win.dds", Source = SC2TextureSource.Vanilla,
            ArchiveEntryPath = "textures/Engine/Spec00_m.win.dds", Usage = SC2TextureUsage.Mask,
        };
        var terrain = new SC2TextureCatalogEntry
        {
            Id = "vanilla:textures/terrain/a_m.win.dds", Source = SC2TextureSource.Vanilla,
            ArchiveEntryPath = "textures/Terrain/a_m.win.dds", Usage = SC2TextureUsage.Mask,
        };
        engine.Normalize();
        terrain.Normalize();
        var entries = new[] { engine, terrain };
        var query = new SC2TextureCatalogQueryEngine();
        query.SetEntries(entries, 1);
        var hits = query.Execute(new SC2TextureQuery { Category = SC2TextureCatalogEntry.EngineCategory })
            .Select(i => entries[i].Id).ToList();
        Assert.Equal(new[] { engine.Id }, hits);
    }

    [Fact]
    public void CacheKey_DistinguishesCommunityFromVanilla()
    {
        Assert.NotEqual(
            new SC2TextureQuery { Source = SC2TextureSource.Vanilla, Community = true }.CacheKey,
            new SC2TextureQuery { Source = SC2TextureSource.Vanilla, Community = false }.CacheKey);
    }
}
