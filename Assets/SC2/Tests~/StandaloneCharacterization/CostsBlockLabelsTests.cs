using Xunit;

namespace SC2CharacterizationTests;

public class CostsBlockLabelsTests
{
    [Fact]
    public void ALandOnlySetIsNamedByTheUnitsItIsFor()
    {
        Assert.Equal("Tanks", SC2CostsBlockLabels.Role(0, 3, "Land_Small"));
        Assert.Equal("Buildings, engineers", SC2CostsBlockLabels.Role(1, 3, "Land_Medium"));
        Assert.Equal("ACU, experimentals", SC2CostsBlockLabels.Role(2, 3, "Land_Large"));
    }

    [Fact]
    public void APlayableWaterSetIsNamedByTheUnitsItIsFor()
    {
        var names = new[] { "Tanks", "Buildings", "Megaliths", "ACU, Fatboy", "Aeon tanks" };
        for (var i = 0; i < names.Length; i++)
            Assert.Equal(names[i], SC2CostsBlockLabels.Role(i, 5, "file" + i));
    }

    [Fact]
    public void ASetOfAnyOtherSizeKeepsTheFileNames()
    {
        Assert.Equal("Land_Small", SC2CostsBlockLabels.Role(0, 4, "Land_Small"));
        Assert.Equal("x", SC2CostsBlockLabels.Role(7, 5, "x"));
        Assert.Equal("Land_Small", SC2CostsBlockLabels.WithFileName(0, 4, "Land_Small"));
    }

    [Fact]
    public void TheFileNameCanFollowTheLabel()
        => Assert.Equal("Tanks (Land_Small)", SC2CostsBlockLabels.WithFileName(0, 3, "Land_Small"));
}
