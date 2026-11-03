using System;
using System.Collections.Generic;

namespace SC2CharacterizationTests;

public class ChainPaletteTests
{
    private static (float r, float g, float b) Of(int id)
    {
        SC2ChainPalette.Rgb(id, out var r, out var g, out var b);
        return (r, g, b);
    }

    [Fact]
    public void Rgb_SameIdGivesTheSameColour()
    {
        Assert.Equal(Of(17), Of(17));
        Assert.Equal(Of(100000), Of(100000));
    }

    [Fact]
    public void Rgb_StaysInsideTheColourRange()
    {
        for (var id = -5; id < 2000; id++)
        {
            var (r, g, b) = Of(id);
            Assert.InRange(r, 0f, 1f);
            Assert.InRange(g, 0f, 1f);
            Assert.InRange(b, 0f, 1f);
            // never dark enough to vanish against the terrain
            Assert.True(Math.Max(r, Math.Max(g, b)) >= 0.7f, "id " + id);
        }
    }

    [Fact]
    public void Rgb_NoTwoOfSeveralHundredChainsShareAColour()
    {
        const int count = 600;
        var colours = new List<(float r, float g, float b)>();
        for (var id = 1; id <= count; id++) colours.Add(Of(id));

        var closest = double.MaxValue;
        for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
            {
                var d = Math.Sqrt(Math.Pow(colours[i].r - colours[j].r, 2) + Math.Pow(colours[i].g - colours[j].g, 2) + Math.Pow(colours[i].b - colours[j].b, 2));
                if (d < closest) closest = d;
            }

        // 600 colours inside the RGB cube cannot all be far apart, but none may be a repeat or a near copy
        Assert.True(closest > 0.01, "closest pair is " + closest);
    }

    [Fact]
    public void TryParseHex_ReadsSixDigitsWithOrWithoutHash()
    {
        Assert.True(SC2ChainPalette.TryParseHex("ff8000", out var r, out var g, out var b));
        Assert.Equal(1f, r);
        Assert.Equal(128f / 255f, g, 4);
        Assert.Equal(0f, b);
        Assert.True(SC2ChainPalette.TryParseHex("#00FF00", out r, out g, out b));
        Assert.Equal((0f, 1f, 0f), (r, g, b));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ff80")]
    [InlineData("ff8000aa")]
    [InlineData("gg0000")]
    public void TryParseHex_RefusesAnythingElse(string hex)
    {
        Assert.False(SC2ChainPalette.TryParseHex(hex, out _, out _, out _));
    }

    [Fact]
    public void ToHex_RoundTripsThroughTryParseHex()
    {
        for (var id = 1; id <= 200; id++)
        {
            SC2ChainPalette.Rgb(id, out var r, out var g, out var b);
            var hex = SC2ChainPalette.ToHex(r, g, b);
            Assert.True(SC2ChainPalette.TryParseHex(hex, out var r2, out var g2, out var b2));
            Assert.Equal(hex, SC2ChainPalette.ToHex(r2, g2, b2));
        }
    }

    [Fact]
    public void Rgb_ConsecutiveIdsLookDifferent()
    {
        // chains made one after another (ids next to each other) are the ones drawn next to each other: they must be easy to tell apart
        for (var id = 1; id < 400; id++)
        {
            var (r1, g1, b1) = Of(id);
            var (r2, g2, b2) = Of(id + 1);
            var d = Math.Sqrt(Math.Pow(r1 - r2, 2) + Math.Pow(g1 - g2, 2) + Math.Pow(b1 - b2, 2));
            Assert.True(d > 0.12, "ids " + id + " and " + (id + 1) + " are only " + d + " apart");
        }
    }
}
