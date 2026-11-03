using System.Collections.Generic;

public sealed class ImportedMapPathsTests
{
    // The fields of a real vanilla scenario (SC2_MP_001), in the order the game's own files have them.
    private const string Scenario =
        "version = 3\r\n" +
        "ScenarioInfo = {\r\n" +
        "    devname = 'MP_001: 4v4 LAND/NAVAL',\r\n" +
        "    name = 'Old Map',\r\n" +
        "    description = 'SC2_MP_001',\r\n" +
        "    type = 'skirmish',\r\n" +
        "    preview = '',\r\n" +
        "    reverbPreset = 'SC2_MP_001',\r\n" +
        "    size = {1024, 1024},\r\n" +
        "    map = '/maps/SC2_MP_001/SC2_MP_001.scmap',\r\n" +
        "    save = '/maps/SC2_MP_001/SC2_MP_001_save.lua',\r\n" +
        "    script = '/maps/SC2_MP_001/SC2_MP_001_script.lua',\r\n" +
        "    mappack = 4,\r\n" +
        "}\r\n";

    [Fact]
    public void RewriteScenarioPaths_PointsMapSaveAndScriptAtTheNewMap()
    {
        var result = SC2ImportedMapPaths.RewriteScenarioPaths(Scenario, "My_Map");

        Assert.Contains("map = '/maps/My_Map/My_Map.scmap',", result);
        Assert.Contains("save = '/maps/My_Map/My_Map_save.lua',", result);
        Assert.Contains("script = '/maps/My_Map/My_Map_script.lua',", result);
        Assert.DoesNotContain("/maps/SC2_MP_001/", result);
    }

    [Fact]
    public void RewriteScenarioPaths_KeepsEverythingElseByteForByte()
    {
        var result = SC2ImportedMapPaths.RewriteScenarioPaths(Scenario, "My_Map");

        // reverbPreset is a key into the game's audio data and description is text: neither follows the folder name.
        Assert.Contains("reverbPreset = 'SC2_MP_001',", result);
        Assert.Contains("description = 'SC2_MP_001',", result);
        Assert.Contains("mappack = 4,", result);
        Assert.Contains("devname = 'MP_001: 4v4 LAND/NAVAL',", result);
        var before = Scenario.Split("\r\n");
        var after = result.Split("\r\n");
        Assert.Equal(before.Length, after.Length);
        var changed = new List<int>();
        for (var i = 0; i < before.Length; i++)
            if (before[i] != after[i]) changed.Add(i);
        // Exactly the map, save and script lines differ.
        Assert.Equal(3, changed.Count);
        Assert.All(changed, i => Assert.Matches(@"^\s+(map|save|script) = ", before[i]));
    }

    [Fact]
    public void RewriteScenarioPaths_IsAFixedPointWhenTheNameIsUnchanged()
    {
        Assert.Equal(Scenario, SC2ImportedMapPaths.RewriteScenarioPaths(Scenario, "SC2_MP_001"));
    }

    [Fact]
    public void RewriteScenarioPaths_IsIdempotent()
    {
        var once = SC2ImportedMapPaths.RewriteScenarioPaths(Scenario, "My_Map");
        Assert.Equal(once, SC2ImportedMapPaths.RewriteScenarioPaths(once, "My_Map"));
    }

    [Fact]
    public void RewriteScenarioPaths_DoesNotAddFieldsThatAreAbsent()
    {
        var withoutScript = "ScenarioInfo = {\r\n    map = '/maps/A/A.scmap',\r\n}\r\n";
        var result = SC2ImportedMapPaths.RewriteScenarioPaths(withoutScript, "B");

        Assert.Contains("map = '/maps/B/B.scmap',", result);
        Assert.DoesNotContain("script", result);
        Assert.DoesNotContain("save", result);
    }

    [Fact]
    public void RewriteScenarioPaths_AcceptsDoubleQuotes()
    {
        var text = "ScenarioInfo = {\r\n    map = \"/maps/A/A.scmap\",\r\n}\r\n";
        Assert.Contains("map = \"/maps/B/B.scmap\",", SC2ImportedMapPaths.RewriteScenarioPaths(text, "B"));
    }

    [Fact]
    public void RewriteScenarioPaths_DoesNotTouchKeysThatOnlyStartWithTheFieldName()
    {
        var text = "ScenarioInfo = {\r\n    mappack = 4,\r\n    savegame = 'x',\r\n    scripted = 'y',\r\n}\r\n";
        Assert.Equal(text, SC2ImportedMapPaths.RewriteScenarioPaths(text, "B"));
    }

    [Fact]
    public void RewriteScenarioPaths_OnlyChangesTheFirstOccurrenceOfAField()
    {
        var text = "ScenarioInfo = {\r\n    map = '/maps/A/A.scmap',\r\n    Configurations = {\r\n        map = 'other',\r\n    },\r\n}\r\n";
        var result = SC2ImportedMapPaths.RewriteScenarioPaths(text, "B");

        Assert.Contains("map = '/maps/B/B.scmap',", result);
        Assert.Contains("map = 'other',", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RewriteScenarioPaths_WithNoNameReturnsTheTextAsItIs(string name)
    {
        Assert.Equal(Scenario, SC2ImportedMapPaths.RewriteScenarioPaths(Scenario, name));
    }

    [Fact]
    public void RewriteScenarioPaths_WithNoTextReturnsIt()
    {
        Assert.Null(SC2ImportedMapPaths.RewriteScenarioPaths(null, "B"));
        Assert.Equal("", SC2ImportedMapPaths.RewriteScenarioPaths("", "B"));
    }

    [Theory]
    [InlineData("/Textures/My_Map/grass.dds", true)]
    [InlineData("Textures/My_Map/grass.dds", true)]
    [InlineData("\\Textures\\my_map\\grass.dds", true)]
    [InlineData("/Textures/My_Map/sub/grass.dds", false)]
    [InlineData("/Textures/Other_Map/grass.dds", false)]
    [InlineData("/maps/My_Map/grass.dds", false)]
    [InlineData("", false)]
    public void IsInOwnTextureFolder_ReportsOnlyFilesDirectlyInTheMapsFolder(string path, bool expected)
    {
        Assert.Equal(expected, SC2ImportedMapPaths.IsInOwnTextureFolder(path, "My_Map"));
    }

    [Theory]
    // not shipped by the game, not ours yet: copy
    [InlineData("/maps/OldMap/textures/grass.dds", false, false, true)]
    // shipped by the game: referenced, not copied
    [InlineData("/textures/Terrain/grass.dds", true, false, false)]
    // shipped by the game but the slot asks for a copy
    [InlineData("/textures/Terrain/grass.dds", true, true, true)]
    // already in the exported map's own folder: never again, even when asked
    [InlineData("/Textures/My_Map/grass.dds", false, true, false)]
    [InlineData("/Textures/My_Map/grass.dds", false, false, false)]
    // nothing to copy
    [InlineData("", false, false, false)]
    [InlineData(null, false, true, false)]
    public void NeedsCopy_CopiesWhatTheGameDoesNotShipAndTheMapDoesNotHaveYet(string path, bool shipped, bool force, bool expected)
    {
        Assert.Equal(expected, SC2ImportedMapPaths.NeedsCopy(path, "My_Map", shipped, force));
    }

    [Theory]
    [InlineData("/maps/OldMap/textures/grass_d.dds", "grass_d")]
    [InlineData("maps/OldMap/textures/grass_d.win.dds", "grass_d")]
    [InlineData("\\maps\\OldMap\\rock normal.dds", "rock_normal")]
    [InlineData("/lighting/mask01_0.win.dds", "mask01_0")]
    [InlineData("/maps/x/", "texture")]
    [InlineData("", "texture")]
    [InlineData(null, "texture")]
    public void FileStem_IsTheSafeLeafWithoutExtensionAndPlatformInfix(string path, string expected)
    {
        Assert.Equal(expected, SC2ImportedMapPaths.FileStem(path));
    }

    [Fact]
    public void UniqueStem_KeepsATextureThatIsNotTakenAndNumbersTheOthers()
    {
        var taken = new HashSet<string> { "grass", "grass_2" };

        Assert.Equal("rock", SC2ImportedMapPaths.UniqueStem("rock", taken.Contains));
        Assert.Equal("grass_3", SC2ImportedMapPaths.UniqueStem("grass", taken.Contains));
        Assert.Equal("grass", SC2ImportedMapPaths.UniqueStem("grass", null));
    }
}
