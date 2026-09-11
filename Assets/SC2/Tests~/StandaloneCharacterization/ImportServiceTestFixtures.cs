using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SC2CharacterizationTests;

/// <summary>Builds a small synthetic multi-map .scd fixture (as a real zip
/// file on disk, or as a real directory tree) for SC2MapImportService tests.
/// Not a real vanilla archive — the BDF payloads are just a valid 4-byte
/// platform magic plus padding, enough to exercise container reading,
/// scenario probing/classification and platform detection without needing
/// the actual gamedata.scd corpus (which ships COMPILED scenario.lua and
/// so can't exercise the plain-text-parsing path this service supports).</summary>
internal static class ImportServiceTestFixtures
{
    public const string ValidStem = "SC2_TEST_001";
    public const string MissingHfieldStem = "SC2_OTHER_002";
    public const string CompiledStem = "SC2_COMPILED_003";

    public static readonly byte[] LuaBytecodeSignature = { 0x1B, (byte)'L', (byte)'u', (byte)'a', (byte)'Q' };

    public static Dictionary<string, byte[]> BuildFixtureEntries()
    {
        var entries = new Dictionary<string, byte[]>();

        // A fully valid, importable map.
        var validDir = $"maps/{ValidStem}";
        entries[$"{validDir}/{ValidStem}_scenario.lua"] = Encoding.UTF8.GetBytes(PlainScenarioLua(
            name: "Test Map One",
            map: $"/maps/{ValidStem}/{ValidStem}.scmap",
            save: $"/maps/{ValidStem}/{ValidStem}_save.lua",
            script: $"/maps/{ValidStem}/{ValidStem}_script.lua"));
        entries[$"{validDir}/{ValidStem}.scmap"] = new byte[] { 1, 2, 3, 4 };
        entries[$"{validDir}/{ValidStem}_save.lua"] = Encoding.UTF8.GetBytes("-- save --");
        entries[$"{validDir}/{ValidStem}_script.lua"] = Encoding.UTF8.GetBytes("-- script --");
        entries[$"{validDir}/{ValidStem}.terrain.win.bdf"] = FakeBdfBytes("MFDB");
        entries[$"{validDir}/{ValidStem}.hfield.win.bdf"] = FakeBdfBytes("MFDB");
        entries[$"{validDir}/lighting/mask01.dds"] = new byte[] { 9, 9, 9 };

        // A second map missing its required hfield.bdf -- exercises the
        // "missing-hfield-bdf" diagnostic and the CanImport=false path.
        var otherDir = $"maps/{MissingHfieldStem}";
        entries[$"{otherDir}/{MissingHfieldStem}_scenario.lua"] = Encoding.UTF8.GetBytes(PlainScenarioLua(
            name: "Other Map",
            map: $"/maps/{MissingHfieldStem}/{MissingHfieldStem}.scmap",
            save: $"/maps/{MissingHfieldStem}/{MissingHfieldStem}_save.lua",
            script: $"/maps/{MissingHfieldStem}/{MissingHfieldStem}_script.lua"));
        entries[$"{otherDir}/{MissingHfieldStem}.terrain.win.bdf"] = FakeBdfBytes("MFDB");

        // A third map whose scenario.lua is compiled bytecode -- exercises
        // the compiled-lua detection path.
        var compiledDir = $"maps/{CompiledStem}";
        entries[$"{compiledDir}/{CompiledStem}_scenario.lua"] = LuaBytecodeSignature;

        return entries;
    }

    private static string PlainScenarioLua(string name, string map, string save, string script) =>
        "version = 3\n" +
        "ScenarioInfo = {\n" +
        $"    name = '{name}',\n" +
        $"    map = '{map}',\n" +
        $"    save = '{save}',\n" +
        $"    script = '{script}',\n" +
        "}\n";

    private static byte[] FakeBdfBytes(string magic)
    {
        var bytes = new byte[64];
        var magicBytes = Encoding.ASCII.GetBytes(magic);
        Array.Copy(magicBytes, bytes, magicBytes.Length);
        return bytes;
    }

    public static string WriteZipFixture(Dictionary<string, byte[]> entries)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"sc2importfixture_{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (path, bytes) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return zipPath;
    }

    public static string WriteDirectoryFixture(Dictionary<string, byte[]> entries)
    {
        var root = Directory.CreateTempSubdirectory("sc2importfixture_").FullName;
        foreach (var (path, bytes) in entries)
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
        }
        return root;
    }

    public static void AddEntryToZip(string zipPath, string entryPath, byte[] bytes)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(entryPath);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}
