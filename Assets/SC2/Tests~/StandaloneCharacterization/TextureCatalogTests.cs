using SC2.MapEditor.Textures;

public sealed class TextureCatalogTests
{
    [Fact]
    public void StableId_UsesFullNormalizedPath_NotAmbiguousLeafName()
    {
        var grass = SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Vanilla, @"SC2Textures\Grass\1_d");
        var rock = SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Vanilla, "SC2Textures/Rock/1_d");
        Assert.NotEqual(grass, rock);
        Assert.Equal("vanilla:sc2textures/grass/1_d", grass);
    }

    [Fact]
    public void Query_CombinesTokensUsageSourceAndCategory()
    {
        var entries = new[]
        {
            Entry("grass green", "Nature", SC2TextureSource.Vanilla, SC2TextureUsage.Diffuse),
            Entry("grass normal", "Nature", SC2TextureSource.Vanilla, SC2TextureUsage.Normal),
            Entry("grass custom", "Imported", SC2TextureSource.Custom, SC2TextureUsage.Diffuse),
        };
        var engine = new SC2TextureCatalogQueryEngine();
        engine.SetEntries(entries, 1);
        var result = engine.Execute(new SC2TextureQuery { Search = "grass green", Source = SC2TextureSource.Vanilla, Usage = SC2TextureUsage.Diffuse, Category = "Nature" });
        Assert.Equal(new[] { 0 }, result);
    }

    [Fact]
    public void QueryCache_InvalidatesWhenCatalogVersionChanges()
    {
        var engine = new SC2TextureCatalogQueryEngine();
        engine.SetEntries(new[] { Entry("one", "A", SC2TextureSource.Vanilla, SC2TextureUsage.Diffuse) }, 1);
        Assert.Single(engine.Execute(new SC2TextureQuery()));
        engine.SetEntries(new[] { Entry("one", "A", SC2TextureSource.Vanilla, SC2TextureUsage.Diffuse), Entry("two", "A", SC2TextureSource.Vanilla, SC2TextureUsage.Diffuse) }, 2);
        Assert.Equal(2, engine.Execute(new SC2TextureQuery()).Count);
    }

    [Fact]
    public void VirtualGrid_ReturnsOnlyVisibleRowsWithOverscan()
    {
        var range = SC2TextureVirtualGrid.Calculate(10_000, 500f, 1_000f, 400f, 100f, 120f, 1);
        Assert.Equal(5, range.Columns);
        Assert.True(range.FirstIndex > 0);
        Assert.True(range.LastIndexExclusive - range.FirstIndex <= 30);
        Assert.Equal(2_000, range.TotalRows);
        Assert.Equal(240_000f, range.TotalHeight);
    }

    private static SC2TextureCatalogEntry Entry(string name, string category, SC2TextureSource source, SC2TextureUsage usage)
    {
        var entry = new SC2TextureCatalogEntry { Id = source + ":" + name, Name = name, ResourcePath = category + "/" + name, Category = category, Source = source, Usage = usage };
        entry.Normalize();
        return entry;
    }
}
