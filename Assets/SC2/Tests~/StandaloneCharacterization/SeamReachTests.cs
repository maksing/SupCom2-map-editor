using Xunit;

namespace SC2CharacterizationTests;

public class SeamReachTests
{
    // A 1000 x 1000 core with a background 3000 across, so the background starts at -1000.
    private const float W = 1000f, D = 1000f, BgMin = -1000f, BgSize = 3000f, Margin = 4f;

    private static bool Core(float x, float z, float r) => SC2SeamReach.ReachesCore(x, z, W, D, r, Margin);
    private static bool Bg(float x, float z, float r) => SC2SeamReach.ReachesBackground(x, z, W, D, BgMin, BgMin, BgSize, BgSize, r, Margin);

    [Fact]
    public void ABrushDeepInsideTheCoreTouchesOnlyTheCore()
    {
        Assert.True(Core(500f, 500f, 50f));
        Assert.False(Bg(500f, 500f, 50f));
    }

    [Fact]
    public void ABrushOnTheSeamTouchesBoth()
    {
        Assert.True(Core(20f, 500f, 50f));
        Assert.True(Bg(20f, 500f, 50f));
        // centred in the background, close to the core
        Assert.True(Core(-20f, 500f, 50f));
        Assert.True(Bg(-20f, 500f, 50f));
    }

    [Fact]
    public void ABrushOutsideTheCoreTouchesOnlyTheBackground()
    {
        Assert.False(Core(-500f, 500f, 50f));
        Assert.True(Bg(-500f, 500f, 50f));
    }

    [Fact]
    public void TheReachIsTheRadiusPlusTheMargin()
    {
        Assert.True(Bg(500f, 54f, 50f));    // 54 from the edge = radius + margin
        Assert.False(Bg(500f, 55f, 50f));
        Assert.True(Core(-54f, 500f, 50f));
        Assert.False(Core(-55f, 500f, 50f));
    }

    [Fact]
    public void ACornerIsMeasuredDiagonally()
    {
        // 40 past the corner on both axes is about 56.6 away: out of reach for 50 + 4, in reach for 55 + 4.
        Assert.False(Core(-40f, -40f, 50f));
        Assert.True(Core(-40f, -40f, 55f));
    }

    [Fact]
    public void ABrushBeyondTheBackgroundTouchesNothingOfIt()
    {
        Assert.False(Bg(-1100f, 500f, 50f));
        Assert.True(Bg(-1040f, 500f, 50f));
    }
}
