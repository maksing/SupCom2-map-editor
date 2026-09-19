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

    [Fact]
    public void CacheKey_DistinguishesCommunityFromVanilla()
    {
        Assert.NotEqual(
            new SC2TextureQuery { Source = SC2TextureSource.Vanilla, Community = true }.CacheKey,
            new SC2TextureQuery { Source = SC2TextureSource.Vanilla, Community = false }.CacheKey);
    }
}
