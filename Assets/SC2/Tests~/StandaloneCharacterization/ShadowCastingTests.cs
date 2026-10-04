using System.Collections.Generic;
using SC2.MapEditor.Document;
using Xunit;

namespace SC2CharacterizationTests;

public class ShadowCastingTests
{
    private const string Core = SC2ShadowCasting.CoreId;
    private const string Background = SC2ShadowCasting.BackgroundId;

    [Fact]
    public void EveryObjectShadowsEveryObjectByDefault()
    {
        var none = new List<string>();
        Assert.True(SC2ShadowCasting.Casts(none, "a", Core));
        Assert.True(SC2ShadowCasting.Casts(none, Core, "a"));
        Assert.True(SC2ShadowCasting.Casts(none, "a", "a"));
        Assert.True(SC2ShadowCasting.Casts(null, "a", "b"));
    }

    [Fact]
    public void SwitchingOnePairOffLeavesTheReverseAndTheOthersAlone()
    {
        var rules = new List<string>();
        Assert.True(SC2ShadowCasting.SetCasts(rules, "two", Core, false));

        Assert.False(SC2ShadowCasting.Casts(rules, "two", Core));
        Assert.True(SC2ShadowCasting.Casts(rules, Core, "two"));
        Assert.True(SC2ShadowCasting.Casts(rules, "two", "three"));
        Assert.True(SC2ShadowCasting.Casts(rules, "one", Core));
    }

    [Fact]
    public void SettingTheSameStateTwiceChangesNothing()
    {
        var rules = new List<string>();
        Assert.True(SC2ShadowCasting.SetCasts(rules, "two", Core, false));
        Assert.False(SC2ShadowCasting.SetCasts(rules, "two", Core, false));
        Assert.Single(rules);
        Assert.True(SC2ShadowCasting.SetCasts(rules, "two", Core, true));
        Assert.False(SC2ShadowCasting.SetCasts(rules, "two", Core, true));
        Assert.Empty(rules);
    }

    [Fact]
    public void ObjectOneShadowsTwoWhileTwoShadowsNeitherTheCoreNorThree()
    {
        var rules = new List<string>();
        SC2ShadowCasting.SetCasts(rules, "two", Core, false);
        SC2ShadowCasting.SetCasts(rules, "two", "three", false);

        var all = new[] { Core, "one", "two", "three" };
        Assert.Equal(new[] { Core, "one", "two", "three" }, SC2ShadowCasting.CastersOnto(all, rules, "two"));
        Assert.Equal(new[] { Core, "one", "three" }, SC2ShadowCasting.CastersOnto(all, rules, Core));
        Assert.Equal(new[] { Core, "one", "three" }, SC2ShadowCasting.CastersOnto(all, rules, "three"));
    }

    [Fact]
    public void ACasterCanBeSwitchedOffForEveryReceiverAtOnce()
    {
        var rules = new List<string>();
        var receivers = new[] { Core, Background, "one", "two" };
        Assert.True(SC2ShadowCasting.SetCastsOnAll(rules, "two", receivers, false));
        foreach (var receiver in receivers) Assert.False(SC2ShadowCasting.Casts(rules, "two", receiver));
        Assert.True(SC2ShadowCasting.Casts(rules, "one", "two"));

        Assert.True(SC2ShadowCasting.SetCastsOnAll(rules, "two", receivers, true));
        Assert.Empty(rules);
    }

    [Fact]
    public void TheRulesSurviveSavingAndLoadingTheDocument()
    {
        var doc = new SC2MapDocument();
        doc.Terrain.RnmShadowExclusions.Add(SC2ShadowCasting.Key("two", Core));
        doc.Terrain.RnmShadowExclusions.Add(SC2ShadowCasting.Key("two", "three"));

        var read = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        Assert.Equal(doc.Terrain.RnmShadowExclusions, read.Terrain.RnmShadowExclusions);
        Assert.False(SC2ShadowCasting.Casts(read.Terrain.RnmShadowExclusions, "two", Core));
        Assert.True(SC2ShadowCasting.Casts(read.Terrain.RnmShadowExclusions, "one", Core));
    }

    [Fact]
    public void ADocumentWithoutRulesLoadsWithNoRules()
    {
        var read = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(new SC2MapDocument()));
        Assert.Empty(read.Terrain.RnmShadowExclusions);
    }

    [Fact]
    public void ReceiversShadowedByTheSameCastersGetTheSameSetKey()
    {
        var a = SC2ShadowCasting.SetKey(new[] { "one", Core });
        var b = SC2ShadowCasting.SetKey(new[] { Core, "one" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, SC2ShadowCasting.SetKey(new[] { Core }));
        Assert.Equal("", SC2ShadowCasting.SetKey(new string[0]));
    }
}
