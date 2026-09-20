using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>Editor markers and machine-local Windows paths must never be written into a BDF.</summary>
public class TexturePathSafetyTests
{
    [Theory]
    [InlineData("file:C:/Users/ragin/AppData/LocalLow/DefaultCompany/SupCom2 map editor/TextureLibrary/a.dds")]
    [InlineData("FILE:C:\\Users\\ragin\\a.dds")]
    [InlineData("archive:textures/Terrain/Dirt_d.dds")]
    [InlineData("C:/Users/ragin/a.dds")]
    [InlineData("d:\\maps\\a.dds")]
    [InlineData("\\\\server\\share\\a.dds")]
    public void MarkersAndLocalPaths_AreUnexportable(string path) =>
        Assert.True(SC2TexturePathSafety.IsUnexportable(path));

    [Theory]
    [InlineData("/textures/Terrain/Dirt_d.dds")]
    [InlineData("/maps/SC2_MP_001/lighting/mask01.dds")]
    [InlineData("textures/Terrain/Dirt_d.dds")]
    [InlineData("/Textures/custom_d.dds")]
    [InlineData("")]
    [InlineData(null)]
    public void GamePathsAndEmpty_AreFine(string path) =>
        Assert.False(SC2TexturePathSafety.IsUnexportable(path));
}
