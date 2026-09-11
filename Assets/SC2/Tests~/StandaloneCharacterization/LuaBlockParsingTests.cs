using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Characterizes the
/// pure-text Lua parsing layer (SC2LuaBlocks/SC2SaveLuaParser) against the
/// real uncompiled-source corpus, independent of anything Unity/ScriptableObject
/// related. This is the same parsing machinery SC2ObjectsImport/SC2ObjectsExport
/// build on, so its behavior needs to be pinned down before those (which DO
/// need a live SC2TerrainData) can be migrated.
/// </summary>
public class LuaBlockParsingTests
{
    public static IEnumerable<object[]> Maps() =>
        TestCorpusLua.MapNames.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Maps))]
    public void SaveLua_MarkersBlockIsFound(string mapName)
    {
        var path = TestCorpusLua.SaveLuaPath(mapName);
        if (!File.Exists(path)) return;

        var text = File.ReadAllText(path);
        var (markers, open, close) = SC2SaveLuaParser.Parse(text);

        Assert.True(open >= 0, $"{mapName}: expected a Markers block to be found");
        Assert.True(close > open);
        // Every real vanilla map has had at least one marker in every prior
        // survey pass this project has done — a count of zero here would be
        // a parser regression, not a legitimately marker-less map.
        Assert.True(markers.Count > 0, $"{mapName}: expected at least one parsed marker");
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void SaveLua_AreasAndChainsBlocksAreLocatable(string mapName)
    {
        var path = TestCorpusLua.SaveLuaPath(mapName);
        if (!File.Exists(path)) return;

        var text = File.ReadAllText(path);
        var (areasOpen, areasClose) = SC2LuaBlocks.FindBlock(text, "Areas = ");
        var (chainsOpen, chainsClose) = SC2LuaBlocks.FindBlock(text, "Chains = ");
        var (armiesOpen, armiesClose) = SC2LuaBlocks.FindBlock(text, "Armies = ");

        // These blocks are always present in the real file structure (even
        // if empty), per the earlier per-map survey — record if that ever
        // stops being true rather than assuming it always will be.
        Assert.True(areasOpen >= 0, $"{mapName}: expected an Areas block");
        Assert.True(chainsOpen >= 0, $"{mapName}: expected a Chains block");
        Assert.True(armiesOpen >= 0, $"{mapName}: expected an Armies block");
        Assert.True(areasClose > areasOpen);
        Assert.True(chainsClose > chainsOpen);
        Assert.True(armiesClose > armiesOpen);
    }

    /// <summary>
    /// Confirmed on real files: campaign maps (SC2_CA_*) do NOT have a
    /// StartPositions block in _scenario.lua at all (their Configurations
    /// block covers single-player/AI setup instead) — only multiplayer
    /// (SC2_MP_*) and DLC1 (SC2_D1_*) maps have one. An earlier version of
    /// this test wrongly assumed every map has the block and failed on all
    /// 21 campaign maps; this is the corrected characterization: the SPLIT
    /// itself is the fact worth recording; SC2ObjectsExport.RegenerateScenarioLua's
    /// FindBlock-returns-text-unchanged-if-absent fallback already handles
    /// campaign maps as a silent no-op, which this test now confirms is the
    /// actual situation it's silently handling.
    /// </summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void ScenarioLua_StartPositionsBlockPresenceMatchesMapKind(string mapName)
    {
        var path = TestCorpusLua.ScenarioLuaPath(mapName);
        if (!File.Exists(path)) return;

        var text = File.ReadAllText(path);
        var (open, close) = SC2LuaBlocks.FindBlock(text, "StartPositions = ");
        // Confirmed on real files: campaign missions (SC2_CA_*) and the
        // single-scenario tech demo (SC2_GC_DEMO) have no StartPositions
        // block at all; only real multiplayer maps (SC2_MP_*, SC2_D1_*) do.
        var expectsStartPositions = !mapName.StartsWith("SC2_CA_") && mapName != "SC2_GC_DEMO";

        if (!expectsStartPositions)
        {
            Assert.True(open < 0, $"{mapName}: expected no StartPositions block (campaign/demo map) — characterization assumption changed, investigate.");
        }
        else
        {
            Assert.True(open >= 0, $"{mapName}: expected a StartPositions block in _scenario.lua (multiplayer map)");
            Assert.True(close > open);
        }
    }
}
