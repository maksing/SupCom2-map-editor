using System.Linq;
using UnityEngine;
using Xunit;

namespace SC2CharacterizationTests;

public class StampStrokeTests
{
    [Fact]
    public void TheFirstDabIsWhereTheStrokeStarts()
    {
        var stroke = new SC2StampStroke();
        var dabs = stroke.Begin(new Vector3(10f, 0f, 20f));
        Assert.Single(dabs);
        Assert.Equal(new Vector3(10f, 0f, 20f), dabs[0]);
    }

    [Fact]
    public void NothingIsPlacedUntilTheMouseHasMovedOneSpacing()
    {
        var stroke = new SC2StampStroke();
        stroke.Begin(Vector3.zero);
        Assert.Empty(stroke.Move(new Vector3(3f, 0f, 0f), 10f));
        Assert.Empty(stroke.Move(new Vector3(9.9f, 0f, 0f), 10f));
        var dabs = stroke.Move(new Vector3(10.5f, 0f, 0f), 10f);
        Assert.Single(dabs);
        Assert.Equal(10f, dabs[0].x, 3);
    }

    [Fact]
    public void ADragPlacesADabEverySpacingNoMatterHowTheMovesAreCut()
    {
        // The same path walked in tiny steps and in one jump gives the same dabs.
        var small = new SC2StampStroke(); small.Begin(Vector3.zero);
        var smallDabs = new System.Collections.Generic.List<Vector3>();
        for (var x = 1f; x <= 100f; x += 1f) smallDabs.AddRange(small.Move(new Vector3(x, 0f, 0f), 10f));

        var big = new SC2StampStroke(); big.Begin(Vector3.zero);
        var bigDabs = big.Move(new Vector3(100f, 0f, 0f), 10f);

        Assert.Equal(10, smallDabs.Count);
        Assert.Equal(10, bigDabs.Count);
        for (var i = 0; i < 10; i++) Assert.Equal((i + 1) * 10f, bigDabs[i].x, 2);
        for (var i = 0; i < 10; i++) Assert.Equal(bigDabs[i].x, smallDabs[i].x, 2);
    }

    [Fact]
    public void TheDistanceCarriesOverBetweenEvents()
    {
        var stroke = new SC2StampStroke(); stroke.Begin(Vector3.zero);
        Assert.Empty(stroke.Move(new Vector3(6f, 0f, 0f), 10f));
        var dabs = stroke.Move(new Vector3(12f, 3f, 0f), 10f);   // 6 + 6.7 along the path: the dab is on the line to the target
        Assert.Single(dabs);
        Assert.InRange(dabs[0].x, 9.5f, 10.5f);
    }

    [Fact]
    public void AVeryLongJumpIsCutShortAndTheStrokeResumesFromThere()
    {
        var stroke = new SC2StampStroke(); stroke.Begin(Vector3.zero);
        var dabs = stroke.Move(new Vector3(100000f, 0f, 0f), 1f);
        Assert.Equal(SC2StampStroke.MaxDabsPerEvent, dabs.Count);
        // Resumes at the target: the next small move is measured from it.
        Assert.Empty(stroke.Move(new Vector3(100000.5f, 0f, 0f), 1f));
        Assert.Single(stroke.Move(new Vector3(100001.2f, 0f, 0f), 1f));
    }

    [Fact]
    public void MovingBeforeBeginStartsTheStroke()
    {
        var stroke = new SC2StampStroke();
        var dabs = stroke.Move(new Vector3(5f, 0f, 5f), 10f);
        Assert.Single(dabs);
        Assert.Equal(new Vector3(5f, 0f, 5f), dabs[0]);
    }

    [Theory]
    [InlineData(20f, 0.4f, 2f, 16f)]    // 40 % of the diameter
    [InlineData(1f, 0.1f, 4f, 2f)]      // never finer than half a cell
    public void SpacingIsAFractionOfTheDiameterButNotBelowHalfACell(float radius, float fraction, float cell, float expected)
        => Assert.Equal(expected, SC2StampStroke.Spacing(radius, fraction, cell), 3);

    [Fact]
    public void TheEnvelopeOnlyGrowsAndAddsWhatItGrew()
    {
        var max = 0f;
        Assert.Equal(0.3f, SC2StampStroke.EnvelopeGain(ref max, 0.3f), 5);
        Assert.Equal(0f, SC2StampStroke.EnvelopeGain(ref max, 0.3f), 5);   // the same amplitude again adds nothing
        Assert.Equal(0f, SC2StampStroke.EnvelopeGain(ref max, 0.1f), 5);   // a weaker one neither
        Assert.Equal(0.4f, SC2StampStroke.EnvelopeGain(ref max, 0.7f), 5);
        Assert.Equal(0.7f, max, 5);
    }

    [Fact]
    public void ManyDabsAtTheSameSpotHeightGainNoMoreThanOneDab()
    {
        var max = 0f; var height = 0f;
        for (var i = 0; i < 500; i++) height += SC2StampStroke.EnvelopeGain(ref max, 0.8f);
        Assert.Equal(0.8f, height, 4);
    }

    [Fact]
    public void ADabsEnvelopeGainPerPixelFollowsTheStampsPatternNotTheNumberOfDabs()
    {
        // A pixel the stamp reaches at 0.25 on one dab and at 0.9 on another ends at 0.9, in whatever order and however often.
        float Run(params float[] amplitudes) { var m = 0f; var h = 0f; foreach (var a in amplitudes) h += SC2StampStroke.EnvelopeGain(ref m, a); return h; }
        Assert.Equal(0.9f, Run(0.25f, 0.9f), 5);
        Assert.Equal(0.9f, Run(0.9f, 0.25f, 0.9f, 0.25f, 0.5f), 5);
    }

    [Fact]
    public void LerpingByTheRemainingShareStepsAddUpToOneLerpByTheFinalEnvelope()
    {
        var amplitudes = new[] { 0.1f, 0.35f, 0.2f, 0.6f, 0.6f, 0.45f, 0.8f, 0.3f };
        float original = 0.2f, target = 1f;
        var value = original; var max = 0f;
        foreach (var amplitude in amplitudes)
        {
            var previous = max;
            var gain = SC2StampStroke.EnvelopeGain(ref max, amplitude);
            var share = SC2StampStroke.RemainingShare(gain, previous);
            value += (target - value) * share;
        }
        Assert.Equal(original + (target - original) * 0.8f, value, 4);
    }

    [Fact]
    public void RemainingShareIsZeroWithoutGainAndSafeAtAFullEnvelope()
    {
        Assert.Equal(0f, SC2StampStroke.RemainingShare(0f, 0.5f));
        Assert.Equal(0.5f, SC2StampStroke.RemainingShare(0.25f, 0.5f), 5);
        Assert.False(float.IsNaN(SC2StampStroke.RemainingShare(0.1f, 1f)));
    }
}
