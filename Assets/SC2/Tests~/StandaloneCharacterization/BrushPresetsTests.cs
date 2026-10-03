using Xunit;

namespace SC2CharacterizationTests;

public class BrushPresetsTests
{
    [Fact]
    public void ABuiltInBrushPathRoundTrips()
    {
        var path = SC2BrushPresets.ToPath("ridge_spine");
        Assert.Equal("brush:ridge_spine", path);
        Assert.True(SC2BrushPresets.TryGetName(path, out var name));
        Assert.Equal("ridge_spine", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/textures/environment/grass.dds")]
    [InlineData("file:C:/brushes/a.png")]
    [InlineData("brush:")]
    [InlineData("brush:   ")]
    public void AnythingElseIsNotABuiltInBrush(string path)
    {
        Assert.False(SC2BrushPresets.TryGetName(path, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void ThePrefixIsMatchedWithoutRegardToCase()
    {
        Assert.True(SC2BrushPresets.TryGetName("Brush:Hill_round", out var name));
        Assert.Equal("Hill_round", name);
    }

    [Theory]
    [InlineData("ridge_spine_b", "Ridge spine b")]
    [InlineData("hill_round", "Hill round")]
    [InlineData("a", "A")]
    [InlineData("", "")]
    public void DisplayNamesAreReadable(string name, string expected) => Assert.Equal(expected, SC2BrushPresets.DisplayName(name));
}
