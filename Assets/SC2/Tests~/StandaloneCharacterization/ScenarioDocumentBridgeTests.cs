using System;
using System.IO;
using System.Linq;
using System.Text;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class ScenarioDocumentBridgeTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2scenarioblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    // Minimal, hand-written, real Lua TEXT source (not bytecode) matching the
    // exact shape confirmed on real vanilla *_scenario.lua files — self
    // contained, doesn't depend on the external vanilla corpus being present
    // on the machine running the test.
    private const string SyntheticScenarioLua = @"
version = 3
ScenarioInfo = {
    name = 'SC2_TEST_MAP',
    campaignID = 'Cybran',
    description = 'A synthetic test map',
    type = 'campaign',
    starts = true,
    preview = '',
    reverbPreset = 'SC2_TEST_MAP',
    size = {512, 256},
    map = '/maps/SC2_TEST_MAP/SC2_TEST_MAP.scmap',
    save = '/maps/SC2_TEST_MAP/SC2_TEST_MAP_save.lua',
    script = '/maps/SC2_TEST_MAP/SC2_TEST_MAP_script.lua',
    UIDeactivate = true,
    norushradius = 12.5,
    norushoffsetX_ARMY_PLAYER = 0.000000,
    Configurations = {
        ['standard'] = {
            teams = { { name = 'FFA', armies = {'ARMY_PLAYER','ARMY_ENEM01',} } },
            customprops = {},
        },
    },
}
";

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void BuildFromLegacy_ReadsKnownScalarFieldsFromSyntheticTextSource()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(Ascii(SyntheticScenarioLua), null, null, store);

            Assert.Equal(SC2DerivedState.Current, doc.State);
            Assert.Equal("SC2_TEST_MAP", doc.KnownFields.Name);
            Assert.Equal("Cybran", doc.KnownFields.CampaignId);
            Assert.Equal("A synthetic test map", doc.KnownFields.Description);
            Assert.Equal("campaign", doc.KnownFields.Type);
            Assert.True(doc.KnownFields.Starts);
            Assert.Equal("SC2_TEST_MAP", doc.KnownFields.ReverbPreset);
            Assert.Equal(512, doc.KnownFields.SizeX);
            Assert.Equal(256, doc.KnownFields.SizeY);
            Assert.Equal("/maps/SC2_TEST_MAP/SC2_TEST_MAP.scmap", doc.KnownFields.MapPath);
            Assert.Equal("/maps/SC2_TEST_MAP/SC2_TEST_MAP_save.lua", doc.KnownFields.SavePath);
            Assert.Equal("/maps/SC2_TEST_MAP/SC2_TEST_MAP_script.lua", doc.KnownFields.ScriptPath);
            Assert.True(doc.KnownFields.UiDeactivate);
            Assert.Equal(12.5f, doc.KnownFields.NoRushRadius, 3);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_PutsUnpromotedFieldsIntoExtraFields()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(Ascii(SyntheticScenarioLua), null, null, store);

            var extraKeys = doc.KnownFields.ExtraFields.Select(f => f.Key).ToList();
            Assert.Contains("norushoffsetX_ARMY_PLAYER", extraKeys);
            Assert.Contains("Configurations", extraKeys);
            // Promoted fields must NOT be duplicated into ExtraFields.
            Assert.DoesNotContain("name", extraKeys);
            Assert.DoesNotContain("size", extraKeys);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_StoresSaveAndScriptLuaAsPassthroughBlobsWithoutParsing()
    {
        var store = NewStore(out var root);
        try
        {
            var saveBytes = Ascii("-- not real lua, just needs to round-trip as bytes --");
            var scriptBytes = Ascii("local x = import('/lua/sim/ScenarioFramework.lua')");

            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(Ascii(SyntheticScenarioLua), saveBytes, scriptBytes, store);

            Assert.False(doc.OriginalSaveLua.IsEmpty);
            Assert.False(doc.OriginalScriptLua.IsEmpty);
            Assert.Equal(SC2LuaPatchMode.OriginalPassthrough, doc.SaveMode);
            Assert.Equal(SC2LuaPatchMode.OriginalPassthrough, doc.ScriptMode);

            using var saveStream = store.OpenRead(doc.OriginalSaveLua.ContentHash);
            using var saveMs = new MemoryStream();
            saveStream.CopyTo(saveMs);
            Assert.Equal(saveBytes, saveMs.ToArray());

            using var scriptStream = store.OpenRead(doc.OriginalScriptLua.ContentHash);
            using var scriptMs = new MemoryStream();
            scriptStream.CopyTo(scriptMs);
            Assert.Equal(scriptBytes, scriptMs.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_MissingScenarioLuaLeavesStateMissing()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(null, null, null, store);
            Assert.Equal(SC2DerivedState.Missing, doc.State);
            Assert.Equal("", doc.KnownFields.Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_MalformedLuaMarksInvalidInsteadOfThrowing()
    {
        var store = NewStore(out var root);
        try
        {
            var malformed = Ascii("ScenarioInfo = { name = 'unterminated string");
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(malformed, null, null, store);

            Assert.Equal(SC2DerivedState.Invalid, doc.State);
            Assert.Contains(doc.KnownFields.ExtraFields, f => f.Key == "__parseError");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ThrowsOnNullBlobStore()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SC2ScenarioLegacyBridge.BuildFromLegacy(Ascii(SyntheticScenarioLua), null, null, null));
    }

    // Real vanilla corpus (compiled Lua 5.1 bytecode, gamedata.scd/maps) —
    // skipped automatically if the corpus isn't present on this machine,
    // matching every other real-corpus test in this project.
    public static System.Collections.Generic.IEnumerable<object[]> CompiledMaps() =>
        TestCorpusLuaCompiled.MapNames.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(CompiledMaps))]
    public void BuildFromLegacy_ReadsRealVanillaCompiledScenarioLua(string mapName)
    {
        var path = TestCorpusLuaCompiled.ScenarioLuaPath(mapName);
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        var isBytecode = bytes.Length > 0 && bytes[0] == 0x1B;

        var store = NewStore(out var root);
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(bytes, null, null, store);

            Assert.Equal(SC2DerivedState.Current, doc.State);
            // Only campaign (SC2_CA_*) maps use the map stem as `name`
            // directly — MP/D1 maps use a localization key + human display
            // name instead (e.g. "<LOC SC2_MAPNAME_0036>[6] Van Horne Core"),
            // confirmed across the real corpus. Assert the stronger check
            // only where it's actually true.
            if (mapName.StartsWith("SC2_CA_"))
                Assert.Equal(mapName, doc.KnownFields.Name);
            else
                Assert.False(string.IsNullOrEmpty(doc.KnownFields.Name), $"{mapName}: expected a non-empty name");
            Assert.False(string.IsNullOrEmpty(doc.KnownFields.Type));
            Assert.True(doc.KnownFields.SizeX > 0 && doc.KnownFields.SizeY > 0, $"{mapName}: expected a positive map size, got {doc.KnownFields.SizeX}x{doc.KnownFields.SizeY}");
            Assert.False(string.IsNullOrEmpty(doc.KnownFields.MapPath));
            Assert.False(string.IsNullOrEmpty(doc.KnownFields.SavePath));
            Assert.False(string.IsNullOrEmpty(doc.KnownFields.ScriptPath));
            // Sanity: this test is only meaningful if it actually exercised
            // the bytecode path for the maps confirmed compiled.
            if (isBytecode)
                Assert.False(doc.OriginalScenarioLua.IsEmpty);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_MatchesKnownGroundTruthForCA_C01()
    {
        if (!TestCorpusLuaCompiled.IsAvailable) return;
        var path = TestCorpusLuaCompiled.ScenarioLuaPath("SC2_CA_C01");
        if (!File.Exists(path)) return;

        var store = NewStore(out var root);
        try
        {
            var doc = SC2ScenarioLegacyBridge.BuildFromLegacy(File.ReadAllBytes(path), null, null, store);

            // Ground truth read directly from the uncompiled source during
            // this session's corpus analysis (see
            // SC2MapDocument_Etap4_Scenario_KopiLuaPort_Status.md).
            Assert.Equal(SC2DerivedState.Current, doc.State);
            Assert.Equal("SC2_CA_C01", doc.KnownFields.Name);
            Assert.Equal("Cybran", doc.KnownFields.CampaignId);
            Assert.Equal("campaign", doc.KnownFields.Type);
            Assert.True(doc.KnownFields.Starts);
            Assert.Equal("SC2_CA_C01", doc.KnownFields.ReverbPreset);
            Assert.Equal(1024, doc.KnownFields.SizeX);
            Assert.Equal(1024, doc.KnownFields.SizeY);
            Assert.Equal("/maps/SC2_CA_C01/SC2_CA_C01.scmap", doc.KnownFields.MapPath);
            Assert.Equal("/maps/SC2_CA_C01/SC2_CA_C01_save.lua", doc.KnownFields.SavePath);
            Assert.Equal("/maps/SC2_CA_C01/SC2_CA_C01_script.lua", doc.KnownFields.ScriptPath);
            Assert.True(doc.KnownFields.UiDeactivate);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
