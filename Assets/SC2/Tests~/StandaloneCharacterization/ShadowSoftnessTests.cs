using System;
using Xunit;

namespace SC2CharacterizationTests;

/// <summary>The penumbra of a baked shadow keeps its width in world units however coarsely a layer is packed into its mask, and
/// follows the Shadow Sharpness setting smoothly.</summary>
public class ShadowSoftnessTests
{
    [Fact]
    public void ATexelThatCoversTheReferenceAreaKeepsTheRadius()
    {
        Assert.Equal(6f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 0.5f), 4);
    }

    [Fact]
    public void ACoarseTexelIsBlurredOverFewerTexels()
    {
        // 6 texels of 0.5 world units = 3 units; a texel of 4 units gets three quarters of a texel of blur.
        Assert.Equal(0.75f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 4f), 4);
        Assert.Equal(0.3f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 10f), 4);
        Assert.Equal(3f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 1f), 4);
    }

    [Fact]
    public void AFineTexelIsBlurredOverMoreTexelsUpToFourTimesTheRadius()
    {
        Assert.Equal(12f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 0.25f), 4);
        Assert.Equal(24f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 0.01f), 4);
    }

    [Fact]
    public void TheRadiusOfACoarseTexelFollowsTheSettingInSmallSteps()
    {
        // A floor texel of 4 world units against a core texel of 0.5: every step of the setting moves the radius.
        var previous = -1f;
        for (var setting = 1; setting <= 24; setting++)
        {
            var scaled = SC2RnmMaskBaker.ScaleRadius(setting, 0.5f, 4f);
            Assert.True(scaled > previous);
            previous = scaled;
        }
    }

    [Fact]
    public void WithoutAReferenceOrAMeasuredTexelTheRadiusIsUnchanged()
    {
        Assert.Equal(6f, SC2RnmMaskBaker.ScaleRadius(6, 0f, 4f), 4);
        Assert.Equal(6f, SC2RnmMaskBaker.ScaleRadius(6, 0.5f, 0f), 4);
        Assert.Equal(0f, SC2RnmMaskBaker.ScaleRadius(0, 0.5f, 4f), 4);
    }

    private static float[] HardEdge(int w, int h, int edgeX)
    {
        var raw = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) raw[y * w + x] = x >= edgeX ? 1f : 0f;
        return raw;
    }

    private static bool[] AllWritten(int n)
    {
        var written = new bool[n];
        Array.Fill(written, true);
        return written;
    }

    // The blurred width of the hard edge at x = 20, in the sense of the sum over the row (a box blur keeps the total, so the
    // sum of a row's values measures how far the edge has been smeared as a number that grows smoothly with the radius).
    private static float Spread(float[] blurred, int w, int row)
    {
        var inside = 0f;
        for (var x = 8; x < 20; x++) inside += blurred[row * w + x]; // lit side bleeding into the dark half
        return inside;
    }

    [Fact]
    public void ARadiusPerTexelOfTheSameValueGivesTheSameBlurAsOneRadius()
    {
        const int w = 40, h = 8;
        var raw = HardEdge(w, h, 20);
        var written = AllWritten(w * h);

        var scalar = SC2RnmMaskBaker.BoxBlurOcclusion(raw, written, w, h, 3);
        var perTexel = SC2RnmMaskBaker.BoxBlurOcclusion(raw, written, w, h, 3, _ => 3f);

        Assert.Equal(scalar, perTexel);
    }

    [Fact]
    public void ARadiusOfZeroLeavesTheEdgeHard()
    {
        const int w = 40, h = 8;
        var raw = HardEdge(w, h, 20);

        var blurred = SC2RnmMaskBaker.BoxBlurOcclusion(raw, AllWritten(w * h), w, h, 6, _ => 0f);

        Assert.Equal(raw, blurred);
    }

    [Fact]
    public void AFractionalRadiusSitsBetweenTheWholeOnes()
    {
        const int w = 40, h = 8;
        var raw = HardEdge(w, h, 20);
        var written = AllWritten(w * h);

        float SpreadAt(float radius) => Spread(SC2RnmMaskBaker.BoxBlurOcclusion(raw, written, w, h, 0, _ => radius), w, 3);

        var none = SpreadAt(0f);
        var one = SpreadAt(1f);
        var three = SpreadAt(0.75f);
        var two = SpreadAt(2f);
        Assert.True(none < three && three < one && one < two);
    }

    [Fact]
    public void TheTexelsOfACoarseAreaAreBlurredLessThanTheOnesOfAFineArea()
    {
        const int w = 60, h = 8;
        var raw = HardEdge(w, h, 15);
        // The same edge once more, at x = 45.
        for (var y = 0; y < h; y++)
            for (var x = 30; x < w; x++) raw[y * w + x] = x >= 45 ? 1f : 0f;
        // Left half: radius 1; right half: radius 5.
        var blurred = SC2RnmMaskBaker.BoxBlurOcclusion(raw, AllWritten(w * h), w, h, 6, idx => idx % w < 30 ? 1f : 5f);

        float Width(int edge)
        {
            var count = 0;
            for (var x = edge - 8; x < edge + 8; x++)
            {
                var v = blurred[3 * w + x];
                if (v > 0.001f && v < 0.999f) count++;
            }
            return count;
        }

        Assert.True(Width(15) < Width(45));
    }
}
