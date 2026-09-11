using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>Same idea as TestCorpusLua, but points at the actual shipped
/// gamedata.scd/maps folder (compiled Lua 5.1 bytecode for 55/56 sampled
/// *_scenario.lua / *_save.lua files, confirmed by header-byte inspection
/// during the KopiLua port — see
/// Assets/SC2/Runtime/KopiLua/CHANGES_FROM_UPSTREAM.md), used by
/// ScenarioDocumentBridgeTests to exercise SC2ScenarioLegacyBridge against
/// real vanilla bytecode, not just the readable uncompiled mirror.</summary>
public static class TestCorpusLuaCompiled
{
    private static readonly string[] CandidatePaths =
    {
        System.Environment.GetEnvironmentVariable("SC2_VANILLA_COMPILED_MAPS_DIR"),
        @"C:\Users\user\Desktop\Новая папка (9)\gamedata\gamedata.scd\maps",
    };

    public static string MapsRoot { get; } = CandidatePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
    public static bool IsAvailable => MapsRoot != null;

    public static IEnumerable<string> MapNames =>
        IsAvailable
            ? Directory.EnumerateDirectories(MapsRoot).Select(Path.GetFileName).OrderBy(n => n)
            : Enumerable.Empty<string>();

    public static string ScenarioLuaPath(string mapName) => Path.Combine(MapsRoot, mapName, $"{mapName}_scenario.lua");
    public static string SaveLuaPath(string mapName) => Path.Combine(MapsRoot, mapName, $"{mapName}_save.lua");
    public static string ScriptLuaPath(string mapName) => Path.Combine(MapsRoot, mapName, $"{mapName}_script.lua");
}
