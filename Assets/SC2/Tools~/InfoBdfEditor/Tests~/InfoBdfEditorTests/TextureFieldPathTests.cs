using System.Reflection;
using SC2InfoBdfEditorApp;

namespace InfoBdfEditorTests;

/// <summary>
/// InfoBdfEditorUi.ComputeBdfTexturePath (private static) converts an
/// absolute path picked via the "Browse..." file dialog on the Textures
/// tab into the game's real relative-path convention. Reflection-driven
/// since the method is intentionally private (pure internal helper, not
/// part of the class's public surface).
/// </summary>
public sealed class TextureFieldPathTests
{
    private static string Compute(string absolutePath)
    {
        var method = typeof(InfoBdfEditorUi).GetMethod("ComputeBdfTexturePath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { absolutePath })!;
    }

    [Fact]
    public void StripsWinMarkerAndTakesEverythingAfterTheScdFolder()
    {
        var result = Compute(@"C:\Users\user\Desktop\Новая папка (9)\gamedata\gamedata.scd\textures\environment\C01_001_env.win.dds");
        Assert.Equal("/textures/environment/C01_001_env.dds", result);
    }

    [Fact]
    public void LeavesPathUnchangedWhenSourceFileHasNoWinMarker()
    {
        var result = Compute(@"C:\game\gamedata.scd\textures\environment\C01_001_env.dds");
        Assert.Equal("/textures/environment/C01_001_env.dds", result);
    }

    [Fact]
    public void HandlesNestedSubfoldersUnderTheScdFolder()
    {
        var result = Compute(@"D:\extracted\gamedata.scd\textures\environment\sky\clouds01.win.dds");
        Assert.Equal("/textures/environment/sky/clouds01.dds", result);
    }

    [Fact]
    public void FallsBackToJustTheFileNameWhenNoScdFolderIsInThePath()
    {
        var result = Compute(@"C:\random\place\my_texture.win.dds");
        Assert.Equal("/my_texture.dds", result);
    }

    [Fact]
    public void IsCaseInsensitiveForBothScdAndWinMarkers()
    {
        var result = Compute(@"C:\game\GameData.SCD\textures\env\tex.WIN.dds");
        Assert.Equal("/textures/env/tex.dds", result);
    }
}
