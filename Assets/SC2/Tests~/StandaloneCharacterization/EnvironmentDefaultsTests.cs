using BdfIO.Formats.Info;
using Xunit;

/// <summary>What a new map's Environment (info.bdf) header starts with: the Marine_Barrier header, with water on at elevation 0.</summary>
public class EnvironmentDefaultsTests
{
    private static InfoModel DefaultModel()
    {
        var model = new InfoModel();
        System.Array.Copy(SC2EnvironmentDefaults.CloneDefaultHeader(), model.Header, 508);
        return model;
    }

    [Fact]
    public void DefaultHeader_IsAFullInfoHeader()
    {
        Assert.Equal(508, SC2EnvironmentDefaults.CloneDefaultHeader().Length);
    }

    [Fact]
    public void DefaultHeader_HasWaterOnAtElevationZero()
    {
        var model = DefaultModel();
        Assert.Equal(1, model.WaterEnabled);
        Assert.Equal(0f, model.WaterElevation);
    }

    [Fact]
    public void DefaultHeader_KeepsTheMarineBarrierSun()
    {
        var model = DefaultModel();
        // The values of the source file, not the generic defaults the previous header carried.
        Assert.Equal(1.3f, model.SunIntensity, 3);
        Assert.InRange(model.SunDirectionY, 0.49f, 0.50f);
    }

    [Fact]
    public void CloneDefaultHeader_ReturnsACopy()
    {
        var first = SC2EnvironmentDefaults.CloneDefaultHeader();
        first[0] ^= 0xFF;
        Assert.NotEqual(first[0], SC2EnvironmentDefaults.CloneDefaultHeader()[0]);
    }
}
