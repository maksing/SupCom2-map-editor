namespace SC2CharacterizationTests;

/// <summary>Fitting an imported picture to the fixed minimap / map-preview size by covering and cropping.</summary>
public class ImageResampleTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255; }
        return px;
    }

    [Fact]
    public void CoverRect_CropsTheWiderSideEvenly()
    {
        // 400x100 source into a square: the middle 100x100 is used
        var (x, y, w, h) = SC2ImageResample.CoverRect(400, 100, 100, 100);
        Assert.Equal(150f, x, 3); Assert.Equal(0f, y, 3); Assert.Equal(100f, w, 3); Assert.Equal(100f, h, 3);
        // a tall source into a wide target crops top and bottom
        var (x2, y2, w2, h2) = SC2ImageResample.CoverRect(100, 400, 200, 100);
        Assert.Equal(0f, x2, 3); Assert.Equal(w2, 100f, 3); Assert.Equal(50f, h2, 3); Assert.Equal(175f, y2, 3);
        // same aspect: everything
        var (x3, y3, w3, h3) = SC2ImageResample.CoverRect(896, 412, 448, 206);
        Assert.Equal(0f, x3, 3); Assert.Equal(0f, y3, 3); Assert.Equal(896f, w3, 3); Assert.Equal(412f, h3, 3);
    }

    [Fact]
    public void CoverResize_KeepsASolidColour_AndTheRequestedSize()
    {
        var result = SC2ImageResample.CoverResizeRgba(Solid(37, 91, 10, 200, 30), 37, 91, 448, 206);
        Assert.Equal(448 * 206 * 4, result.Length);
        Assert.All(new[] { 0, 1000, result.Length / 4 - 1 }, i =>
        {
            Assert.Equal(10, result[i * 4]); Assert.Equal(200, result[i * 4 + 1]);
            Assert.Equal(30, result[i * 4 + 2]); Assert.Equal(255, result[i * 4 + 3]);
        });
    }

    [Fact]
    public void CoverResize_DropsTheCroppedSidesAndKeepsTheRowOrder()
    {
        // 6x2 source: left third red, middle third green, right third blue; row 0 white, row 1 black in the middle
        var src = new byte[6 * 2 * 4];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 6; x++)
            {
                var i = (y * 6 + x) * 4;
                if (x < 2) { src[i] = 255; }
                else if (x < 4) { src[i + 1] = y == 0 ? (byte)255 : (byte)100; }
                else { src[i + 2] = 255; }
                src[i + 3] = 255;
            }
        // into a 2x2 target: only the middle 2x2 (the green block) is used, red and blue are cropped away
        var result = SC2ImageResample.CoverResizeRgba(src, 6, 2, 2, 2);
        for (var i = 0; i < 4; i++) { Assert.Equal(0, result[i * 4]); Assert.Equal(0, result[i * 4 + 2]); }
        Assert.True(result[1] > result[2 * 4 + 1] - 1 && result[1] >= 200);          // row 0 stays the bright row
        Assert.InRange(result[2 * 4 + 1], 90, 110);                                  // row 1 stays the dim row
    }
}
