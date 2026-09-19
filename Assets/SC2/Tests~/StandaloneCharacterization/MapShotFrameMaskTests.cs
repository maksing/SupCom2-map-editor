using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

/// <summary>
/// The alpha frame stamped on _PC_mapshot.dds, extracted from the real vanilla
/// SC2_MP_001_PC_mapshot.dds (448x206). Expected values below were read
/// straight out of that file's alpha channel.
/// </summary>
public class MapShotFrameMaskTests
{
    private const int W = 448, H = 206;

    [Theory]
    [InlineData(0, 100, 4)]
    [InlineData(1, 100, 12)]
    [InlineData(2, 100, 24)]
    [InlineData(3, 100, 40)]
    [InlineData(4, 100, 60)]
    [InlineData(5, 100, 84)]
    [InlineData(6, 100, 112)]
    [InlineData(7, 100, 255)]
    [InlineData(200, 0, 4)]
    [InlineData(200, 6, 112)]
    [InlineData(200, 7, 255)]
    public void EdgeProfile_MatchesVanilla(int x, int y, int expected) =>
        Assert.Equal(expected, SC2MapShotFrameMask.AlphaAt(x, y, W, H));

    [Theory]
    [InlineData(447, 100, 4)]
    [InlineData(441, 100, 112)]
    [InlineData(440, 100, 255)]
    [InlineData(200, 205, 4)]
    [InlineData(200, 199, 112)]
    [InlineData(200, 198, 255)]
    public void FarEdges_MirrorTheNearEdges(int x, int y, int expected) =>
        Assert.Equal(expected, SC2MapShotFrameMask.AlphaAt(x, y, W, H));

    [Fact]
    public void RoundedCorners_AreFadedAndMirrored()
    {
        // Top-left values read from the vanilla file.
        Assert.Equal(0, SC2MapShotFrameMask.AlphaAt(0, 0, W, H));
        Assert.Equal(49, SC2MapShotFrameMask.AlphaAt(6, 6, W, H));
        Assert.Equal(255, SC2MapShotFrameMask.AlphaAt(7, 7, W, H));
        Assert.Equal(106, SC2MapShotFrameMask.AlphaAt(12, 6, W, H));
        for (var y = 0; y < 14; y++)
        for (var x = 0; x < 14; x++)
        {
            var tl = SC2MapShotFrameMask.AlphaAt(x, y, W, H);
            Assert.Equal(tl, SC2MapShotFrameMask.AlphaAt(W - 1 - x, y, W, H));
            Assert.Equal(tl, SC2MapShotFrameMask.AlphaAt(W - 1 - x, H - 1 - y, W, H));
        }
    }

    [Fact]
    public void BottomLeft_IsADiagonalCut_UnlikeTheOtherCorners()
    {
        Assert.Equal(0, SC2MapShotFrameMask.AlphaAt(0, H - 1, W, H));
        Assert.Equal(0, SC2MapShotFrameMask.AlphaAt(10, H - 1, W, H));
        Assert.Equal(1, SC2MapShotFrameMask.AlphaAt(19, H - 1, W, H));
        // Same distance from the corner: the cut is transparent where the
        // bottom-right rounded corner is already partly visible.
        Assert.True(SC2MapShotFrameMask.AlphaAt(3, H - 4, W, H) < SC2MapShotFrameMask.AlphaAt(W - 4, H - 4, W, H));
        Assert.Equal(255, SC2MapShotFrameMask.AlphaAt(30, H - 30, W, H));
    }

    [Fact]
    public void InteriorIsFullyOpaque()
    {
        for (var y = 30; y < H - 30; y += 7)
        for (var x = 30; x < W - 30; x += 7)
            Assert.Equal(255, SC2MapShotFrameMask.AlphaAt(x, y, W, H));
    }

    [Fact]
    public void ApplyToBgra_BlacksOutTheFrame_AndRespectsRowOrder()
    {
        var pixels = new byte[W * H * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 10; pixels[i + 1] = 20; pixels[i + 2] = 30; pixels[i + 3] = 99; }

        // Unity buffers have the bottom row first: the cut sits in row 0.
        SC2MapShotFrameMask.ApplyToBgra(pixels, W, H, bottomRowFirst: true);
        // Vanilla's frame pixels are black, not a faded copy of the image.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels[0..4]);
        var leftEdge = (100 * W + 6) * 4; // 6 px in from the left edge: alpha 112, still black
        Assert.Equal(new byte[] { 0, 0, 0, 112 }, pixels[leftEdge..(leftEdge + 4)]);
        Assert.Equal(SC2MapShotFrameMask.AlphaAt(17, H - 1, W, H), pixels[17 * 4 + 3]);
        // Fully opaque pixels keep their colour.
        var interior = ((H / 2) * W + W / 2) * 4;
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, pixels[interior..(interior + 4)]);

        // DDS order (top row first) puts it in the last row instead.
        var dds = new byte[W * H * 4];
        SC2MapShotFrameMask.ApplyToBgra(dds, W, H, bottomRowFirst: false);
        Assert.Equal(SC2MapShotFrameMask.AlphaAt(17, H - 1, W, H), dds[((H - 1) * W + 17) * 4 + 3]);
        Assert.Equal(SC2MapShotFrameMask.AlphaAt(0, 0, W, H), dds[3]);

        var again = (byte[])pixels.Clone();
        SC2MapShotFrameMask.ApplyToBgra(again, W, H, bottomRowFirst: true);
        Assert.Equal(pixels, again);
    }

    [Fact]
    public void WrongBufferSize_IsLeftUntouched()
    {
        var pixels = new byte[16];
        SC2MapShotFrameMask.ApplyToBgra(pixels, W, H, bottomRowFirst: true);
        Assert.All(pixels, b => Assert.Equal(0, b));
    }
}
