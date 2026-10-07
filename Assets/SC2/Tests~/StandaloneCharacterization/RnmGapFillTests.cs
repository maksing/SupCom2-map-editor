using Xunit;

namespace SC2CharacterizationTests;

/// <summary>The fill of texels no triangle covers in a baked RNM mask (SC2RnmMaskBaker.FillGapsRgb).</summary>
public class RnmGapFillTests
{
    [Fact]
    public void FillsAGapWiderThanAnyFixedPassCountFromBothSides()
    {
        const int w = 200, h = 4;
        var rgb0 = new byte[w * h * 3];
        var rgb1 = new byte[w * h * 3];
        var rgb2 = new byte[w * h * 3];
        var alpha = new byte[w * h];
        var written = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (x >= 10 && x < w - 10) continue; // a 180 texel wide hole in the middle
            var i = y * w + x;
            written[i] = true;
            var value = (byte)(x < 10 ? 200 : 100);
            rgb0[i * 3] = rgb0[i * 3 + 1] = rgb0[i * 3 + 2] = value;
            rgb1[i * 3] = rgb1[i * 3 + 1] = rgb1[i * 3 + 2] = value;
            rgb2[i * 3] = rgb2[i * 3 + 1] = rgb2[i * 3 + 2] = value;
            alpha[i] = 255;
        }

        SC2RnmMaskBaker.FillGapsRgb(rgb0, rgb1, rgb2, alpha, written, w, h);

        for (var i = 0; i < w * h; i++)
        {
            Assert.True(written[i]);
            Assert.InRange((int)rgb0[i * 3], 100, 200);
            Assert.Equal(255, alpha[i]);
        }
        // The hole's texels next to each side carry that side's value, not a default.
        Assert.Equal(200, rgb0[(0 * w + 10) * 3]);
        Assert.Equal(100, rgb0[(0 * w + w - 11) * 3]);
    }

    [Fact]
    public void LeavesCoveredTexelsUntouched()
    {
        const int w = 8, h = 8;
        var rgb0 = new byte[w * h * 3];
        var rgb1 = new byte[w * h * 3];
        var rgb2 = new byte[w * h * 3];
        var alpha = new byte[w * h];
        var written = new bool[w * h];
        written[3 * w + 3] = true;
        rgb0[(3 * w + 3) * 3] = 77;
        alpha[3 * w + 3] = 9;

        SC2RnmMaskBaker.FillGapsRgb(rgb0, rgb1, rgb2, alpha, written, w, h);

        Assert.Equal(77, rgb0[(3 * w + 3) * 3]);
        Assert.Equal(9, alpha[3 * w + 3]);
        Assert.Equal(77, rgb0[(7 * w + 7) * 3]);
    }
}
