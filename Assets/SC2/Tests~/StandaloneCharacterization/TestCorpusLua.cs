using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>Same idea as TestCorpus, but for the plain-text lua source
/// corpus (gamedata.scd/uncompiled/maps — the shipped maps/ folder holds
/// compiled Lua 5.1 bytecode for _save.lua on 55/56 maps; this uncompiled
/// mirror is the readable source, confirmed present for all 56+ maps).</summary>
public static class TestCorpusLua
{
    private static readonly string[] CandidatePaths =
    {
        System.Environment.GetEnvironmentVariable("SC2_VANILLA_UNCOMPILED_MAPS_DIR"),
        @"C:\Users\user\Desktop\Новая папка (9)\gamedata\gamedata.scd\uncompiled\maps",
    };

    public static string MapsRoot { get; } = CandidatePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
    public static bool IsAvailable => MapsRoot != null;

    public static IEnumerable<string> MapNames =>
        IsAvailable
            ? Directory.EnumerateDirectories(MapsRoot).Select(Path.GetFileName).OrderBy(n => n)
            : Enumerable.Empty<string>();

    public static string SaveLuaPath(string mapName) => Path.Combine(MapsRoot, mapName, $"{mapName}_save.lua");
    public static string ScenarioLuaPath(string mapName) => Path.Combine(MapsRoot, mapName, $"{mapName}_scenario.lua");
}
