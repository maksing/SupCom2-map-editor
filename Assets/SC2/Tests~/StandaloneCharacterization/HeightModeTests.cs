using Xunit;

public class HeightModeTests
{
    [Theory]
    [InlineData(true, false, SC2HeightMode.Heightmap)]
    [InlineData(true, true, SC2HeightMode.Model)]
    [InlineData(false, false, SC2HeightMode.Free)]
    // The model flag means nothing without the anchor.
    [InlineData(false, true, SC2HeightMode.Free)]
    public void The_two_stored_flags_give_the_mode(bool anchor, bool toModel, SC2HeightMode expected) =>
        Assert.Equal(expected, SC2HeightModes.From(anchor, toModel));

    [Theory]
    [InlineData(SC2HeightMode.Heightmap, true, false)]
    [InlineData(SC2HeightMode.Model, true, true)]
    [InlineData(SC2HeightMode.Free, false, false)]
    public void A_mode_sets_the_two_stored_flags(SC2HeightMode mode, bool anchor, bool toModel)
    {
        SC2HeightModes.Apply(mode, out var a, out var m);
        Assert.Equal(anchor, a);
        Assert.Equal(toModel, m);
        Assert.Equal(mode, SC2HeightModes.From(a, m));
    }

    [Fact]
    public void Heightmap_mode_uses_the_terrain_height_plus_the_offset() =>
        Assert.Equal(12.5f, SC2HeightModes.AnchoredY(SC2HeightMode.Heightmap, modelHeight: 40f, terrainHeight: 10f, currentY: 99f, offset: 2.5f));

    [Fact]
    public void Model_mode_uses_the_model_surface_plus_the_offset_and_ignores_the_heightmap() =>
        Assert.Equal(42.5f, SC2HeightModes.AnchoredY(SC2HeightMode.Model, modelHeight: 40f, terrainHeight: 10f, currentY: 99f, offset: 2.5f));

    [Fact]
    public void Model_mode_keeps_the_current_height_where_no_model_is_under_the_object() =>
        Assert.Equal(99f, SC2HeightModes.AnchoredY(SC2HeightMode.Model, modelHeight: null, terrainHeight: 10f, currentY: 99f, offset: 2.5f));

    [Fact]
    public void Free_mode_keeps_the_current_height() =>
        Assert.Equal(99f, SC2HeightModes.AnchoredY(SC2HeightMode.Free, modelHeight: 40f, terrainHeight: 10f, currentY: 99f, offset: 2.5f));

    [Fact]
    public void The_nearest_surface_wins_so_an_object_stays_on_its_own_layer()
    {
        // A bridge deck at 12 over the ground at 2.
        var surfaces = new[] { 12f, 2f };
        Assert.Equal(2f, SC2HeightModes.NearestSurface(surfaces, referenceY: 3f));
        Assert.Equal(12f, SC2HeightModes.NearestSurface(surfaces, referenceY: 11f));
    }

    [Fact]
    public void No_surface_gives_nothing() =>
        Assert.Null(SC2HeightModes.NearestSurface(new float[0], referenceY: 5f));
}
