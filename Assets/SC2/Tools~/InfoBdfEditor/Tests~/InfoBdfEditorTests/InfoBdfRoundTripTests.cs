using System.IO;
using BdfIO.Core;
using BdfIO.Formats.Info;

namespace InfoBdfEditorTests;

/// <summary>
/// Data-layer correctness: InfoBdf.Parse/Serialize must round-trip every
/// real vanilla info.bdf byte-for-byte on the header (the actual editable
/// payload). This is what the standalone app's Open/Save menu items rely
/// on — if this breaks, the app silently corrupts every file it touches
/// regardless of what the UI on top of it looks like.
///
/// Skip-not-fail when the corpus isn't present, same convention as
/// Tests~/StandaloneCharacterization (see TestCorpus.cs) — this is the
/// user's own legally-owned game data, never checked into source control.
/// </summary>
public sealed class InfoBdfRoundTripTests
{
    public static IEnumerable<object[]> VanillaMaps() =>
        TestCorpus.IsAvailable
            ? TestCorpus.MapNames.Select(n => new object[] { n })
            : Array.Empty<object[]>();

    [Theory]
    [MemberData(nameof(VanillaMaps))]
    public void HeaderRoundTripsByteForByte(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.info.win.bdf");
        if (!File.Exists(path)) return; // not every map has an info.bdf

        var raw = File.ReadAllBytes(path);
        var parsed = InfoBdf.Parse(BdfContainer.Read(raw, InfoBdf.Padding));

        var (data, offsetTable) = InfoBdf.Serialize(parsed, parsed.Platform);
        var container = new BdfContainer { Platform = parsed.Platform, Data = data, OffsetTable = offsetTable };
        var rewritten = container.Write(InfoBdf.Padding);

        var reparsed = InfoBdf.Parse(BdfContainer.Read(rewritten, InfoBdf.Padding));

        // Bytes [240..260) are the 5 string-pool POINTER slots
        // (EnvironmentMap/SkyMap/ReservedTexturePath/ColorCorrectionCube/
        // FogOfWarCorrectionCube, @240/244/248/252/256) — their numeric
        // VALUES legitimately depend on wherever StringPoolWriter placed
        // each string this time around (dedup order isn't guaranteed
        // stable across a fresh Serialize call), so they're excluded from
        // the raw byte comparison. What actually matters — the strings
        // those pointers RESOLVE to — is asserted explicitly below instead,
        // and every other byte in the header (every real numeric field)
        // must still match exactly.
        Assert.Equal(parsed.Header[..240], reparsed.Header[..240]);
        Assert.Equal(parsed.Header[260..], reparsed.Header[260..]);
        Assert.Equal(parsed.EnvironmentMap, reparsed.EnvironmentMap);
        Assert.Equal(parsed.SkyMap, reparsed.SkyMap);
        Assert.Equal(parsed.ReservedTexturePath, reparsed.ReservedTexturePath);
        Assert.Equal(parsed.ColorCorrectionCube, reparsed.ColorCorrectionCube);
        Assert.Equal(parsed.FogOfWarCorrectionCube, reparsed.FogOfWarCorrectionCube);
    }

    [Theory]
    [MemberData(nameof(VanillaMaps))]
    public void PropertyGettersDoNotThrowForAnyRealFile(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.info.win.bdf");
        if (!File.Exists(path)) return;

        var raw = File.ReadAllBytes(path);
        var m = InfoBdf.Parse(BdfContainer.Read(raw, InfoBdf.Padding));

        // Touch every single numeric property once — this is exactly what
        // populating the UI does on Open, and it's the property surface a
        // wrong offset/type would corrupt silently (NaN, huge magnitude)
        // rather than throw, so this test only catches genuine exceptions
        // (e.g. an out-of-range Header access); InfoFieldLimitsTests and
        // the corpus sampling that produced InfoFieldLimits.cs already
        // cover plausible-value-range checks separately.
        _ = m.Version;
        _ = (m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ);
        _ = (m.SunColorR, m.SunColorG, m.SunColorB, m.SunIntensity);
        _ = (m.ShadowColorR, m.ShadowColorG, m.ShadowColorB, m.ShadowColorA);
        _ = m.WaterEnabled;
        _ = (m.MaxWaterDepth, m.WaterElevation, m.WaterRenderElevationOverride);
        _ = (m.WaterColorR, m.WaterColorG, m.WaterColorB, m.WaterColorA);
        _ = (m.GlowEnabled, m.BloomEnabled, m.DepthOfFieldEnabled);
        _ = (m.RenderPassACEnabled, m.RenderPassB0Enabled, m.RenderPassB4Enabled);
        _ = (m.AirElevation, m.WindDirectionX, m.WindDirectionZ, m.WindSpeed);
        _ = (m.EnvironmentMap, m.SkyMap, m.ReservedTexturePath, m.ColorCorrectionCube, m.FogOfWarCorrectionCube);
    }

    [Fact]
    public void CorpusIsAvailableOrTestsAreExplicitlySkipped()
    {
        // Not a real assertion — just makes the skip condition visible in
        // test output instead of silently returning zero cases.
        if (!TestCorpus.IsAvailable)
            Console.WriteLine(TestCorpus.SkipReason);
    }
}
