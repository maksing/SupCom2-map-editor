using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Characterizes the single-shot resolver
/// SC2ScdEntryPointResolver.Resolve, run against every real vanilla map
/// folder. Records which resolution path ("scenario.lua" vs "heuristic")
/// actually fires today.
/// </summary>
public class ScdEntryPointResolverTests
{
    public static IEnumerable<object[]> Maps() =>
        TestCorpus.MapNames.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Maps))]
    public void Resolve_FindsATerrainBdfStem(string mapName)
    {
        var result = SC2ScdEntryPointResolver.Resolve(TestCorpus.MapDir(mapName));
        Assert.False(string.IsNullOrEmpty(result.BdfStem));
        Assert.False(string.IsNullOrEmpty(result.BdfDirectory));
    }

    /// <summary>
    /// This is the key characterization finding: FindScenarioLuaPath looks
    /// for a file literally named "scenario.lua", but every real vanilla map
    /// folder in gamedata.scd/maps/&lt;name&gt;/ names it "&lt;name&gt;_scenario.lua"
    /// (confirmed directly on disk: SC2_CA_C01/SC2_CA_C01_scenario.lua, not
    /// SC2_CA_C01/scenario.lua). So the "scenario.lua-driven" resolution
    /// path this resolver was specifically built to prefer over guessing
    /// never actually triggers against this corpus layout — every map falls
    /// through to the "heuristic (first *.terrain.win.bdf found)" fallback
    /// instead. This test doesn't assert a specific outcome (that would bake
    /// in "current = correct"); it just records what happens today so a
    /// SC2MapDocument migration can see if this changed on purpose or by
    /// accident.
    /// </summary>
    [Fact]
    public void Resolve_RecordsWhichResolutionPathFiresAcrossTheCorpus()
    {
        if (!TestCorpus.IsAvailable) return;

        var byMethod = TestCorpus.MapNames
            .Select(n => SC2ScdEntryPointResolver.Resolve(TestCorpus.MapDir(n)))
            .GroupBy(r => r.ResolutionMethod)
            .ToDictionary(g => g.Key ?? "(null)", g => g.Count());

        var summary = string.Join(", ", byMethod.Select(kv => $"{kv.Key}={kv.Value}"));
        // Not a pass/fail assertion — this is a recorded characterization,
        // surfaced via the test output so it's visible in any test run.
        System.Console.WriteLine($"[characterization] Resolution method counts across corpus: {summary}");
        Assert.True(byMethod.Values.Sum() == TestCorpus.MapNames.Count());
    }
}
