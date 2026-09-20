using System;
using System.IO;
using System.Linq;
using BdfIO.Core;
using BdfIO.Formats.Costs;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Info;

namespace SC2CharacterizationTests;

/// <summary>The vanilla-measured navmesh rules: cross growth, the 64/16 wall
/// ring, hard vs water walls, and the water gradient; plus checks against the
/// real vanilla costs.bdf corpus (skipped when the corpus is absent).</summary>
public class CostsNavmeshRulesTests
{
    private static bool[] Mask(int w, int h, params (int x, int y)[] cells)
    {
        var m = new bool[w * h];
        foreach (var (x, y) in cells) m[y * w + x] = true;
        return m;
    }

    [Fact]
    public void DilateCross_GrowsByDiamond_NotSquare()
    {
        var grown = SC2CostsNavmeshRules.DilateCross(Mask(9, 9, (4, 4)), 9, 9, 2);
        Assert.True(grown[4 * 9 + 6]);   // L1 = 2
        Assert.True(grown[5 * 9 + 5]);   // L1 = 2 (diagonal)
        Assert.False(grown[6 * 9 + 6]);  // L1 = 4 (square corner)
        Assert.False(grown[4 * 9 + 7]);  // L1 = 3
    }

    [Fact]
    public void Ring_WallAtDistanceOne_Is64_AtDistanceTwo_Is16()
    {
        var nav = SC2CostsNavmeshRules.Build(Mask(9, 9, (4, 4)), null, null, null, 9, 9);
        Assert.Equal(255, nav[4 * 9 + 4]);
        Assert.Equal(64, nav[4 * 9 + 5]);
        Assert.Equal(16, nav[4 * 9 + 6]);
        Assert.Equal(1, nav[4 * 9 + 7]);
    }

    [Fact]
    public void Ring_DiagonalAloneGivesNothing_AxisDirectionsAdd_NearestPerDirectionOnly()
    {
        var nav = SC2CostsNavmeshRules.Build(Mask(9, 9, (4, 4)), null, null, null, 9, 9);
        Assert.Equal(1, nav[5 * 9 + 5]);   // diagonal neighbour of the wall
        Assert.Equal(1, nav[5 * 9 + 6]);   // knight move: no axis-aligned wall within 2

        var two = SC2CostsNavmeshRules.Build(Mask(5, 1, (0, 0), (2, 0)), null, null, null, 5, 1);
        Assert.Equal(128, two[1]);         // between two walls: 64 + 64

        var same = SC2CostsNavmeshRules.Build(Mask(5, 1, (2, 0), (3, 0)), null, null, null, 5, 1);
        Assert.Equal(64, same[1]);         // wall at 1 and 2 in one direction counts once
    }

    [Fact]
    public void Ring_IsCappedAt254()
    {
        var nav = SC2CostsNavmeshRules.Build(Mask(3, 3, (1, 0), (0, 1), (2, 1), (1, 2)), null, null, null, 3, 3);
        Assert.Equal(254, nav[1 * 3 + 1]);
    }

    [Fact]
    public void Ring_SkipsWaterWalls_ExceptInTheSeafloorBlock()
    {
        const int w = 7;
        var walls = Mask(w, 1, (3, 0));
        var water = Mask(w, 1, (3, 0));
        Assert.Equal(1, SC2CostsNavmeshRules.BuildBlock(0, 5, walls, walls, water, w, 1)[2]);
        Assert.Equal(64, SC2CostsNavmeshRules.BuildBlock(3, 5, walls, walls, water, w, 1)[2]);
    }

    [Fact]
    public void LandOnlyCascade_IsOneThenThreeCrossSteps()
    {
        const int n = 15;
        var walls = SC2CostsNavmeshRules.DeriveLandOnlyWalls(Mask(n, n, (7, 7)), n, n);
        Assert.True(walls[1][7 * n + 8]);
        Assert.False(walls[1][7 * n + 9]);
        Assert.True(walls[2][7 * n + 11]);   // 1 + 3 = 4
        Assert.False(walls[2][7 * n + 12]);
        Assert.False(walls[2][10 * n + 10]); // square corner is outside the diamond
    }

    [Fact]
    public void WaterProfile_LandWallsGrowOnePlusFour_B1BlocksAllWater_BandGrowsByTwo()
    {
        const int n = 25;
        var water = new bool[n * n];
        for (var y = 0; y < n; y++) for (var x = 15; x < n; x++) water[y * n + x] = true;
        var walls0 = Mask(n, n, (3, 12), (15, 12));
        var w = SC2CostsNavmeshRules.DeriveWaterProfileWalls(walls0, water, null, null, n, n);

        foreach (var b in new[] { 1, 4 }) { Assert.True(w[b][12 * n + 4]); Assert.False(w[b][12 * n + 5]); }
        foreach (var b in new[] { 2, 3 }) { Assert.True(w[b][12 * n + 7]); Assert.False(w[b][12 * n + 8]); }

        Assert.True(w[1][0 * n + 24]);     // b1: every water cell
        Assert.False(w[0][0 * n + 24]);
        Assert.False(w[4][0 * n + 24]);

        Assert.True(w[2][12 * n + 17]);    // shoreline band wall grows by 2 in water
        Assert.False(w[2][12 * n + 18]);
    }

    [Fact]
    public void WaterProfile_PinnedCellOverridesB4AndCarriesIntoB3()
    {
        const int n = 15;
        var water = new bool[n * n];
        var pinMask = new byte[n * n];
        var pinnedWall = new bool[n * n];
        pinMask[7 * n + 8] = 1; // pinned passable where b4 would grow a wall
        var w = SC2CostsNavmeshRules.DeriveWaterProfileWalls(Mask(n, n, (7, 7)), water, pinMask, pinnedWall, n, n);
        Assert.False(w[4][7 * n + 8]);
        Assert.True(w[1][7 * n + 8]);
        Assert.True(w[4][7 * n + 7]);
    }

    [Fact]
    public void WaterProfile_GrowthOnlyStripNextToPinnedPassableCellOpens_RealWallsStay()
    {
        const int n = 15;
        var water = new bool[n * n];
        // b0 walls: (7,7) and (8,7); growth of b4 adds (6,7), (9,7), (7,6), ...
        var walls0 = Mask(n, n, (7, 7), (8, 7));
        var pinMask = new byte[n * n];
        var pinnedWall = new bool[n * n];
        pinMask[7 * n + 7] = 1;            // Shore opened the wall cell (7,7)
        var w = SC2CostsNavmeshRules.DeriveWaterProfileWalls(walls0, water, pinMask, pinnedWall, n, n);
        Assert.False(w[4][7 * n + 7]);     // pinned, passable
        Assert.False(w[4][7 * n + 6]);     // growth-only neighbour opens
        Assert.False(w[4][6 * n + 7]);
        Assert.True(w[4][7 * n + 8]);      // real b0 wall stays
        Assert.True(w[4][7 * n + 9]);      // growth cell not touching the pin stays
    }

    [Fact]
    public void ShoreZone_HasNoRingOnTheLandSide_InB0B1B2_ButKeepsItElsewhere()
    {
        const int n = 25;
        var water = new bool[n * n];
        var walls0 = Mask(n, n, (12, 12), (3, 3));
        var pinMask = new byte[n * n];
        var pinnedWall = new bool[n * n];
        pinMask[12 * n + 12] = 1;   // Shore opened the wall at (12,12); (3,3) stays an ordinary wall
        var w = SC2CostsNavmeshRules.DeriveWaterProfileWalls(walls0, water, pinMask, pinnedWall, n, n, out _);
        var zone = SC2CostsNavmeshRules.ShoreZone(w[0], w[1], w[4], water, pinMask);
        Assert.True(zone[12 * n + 12]);
        Assert.True(zone[12 * n + 13]);   // growth-only cell opened with it
        Assert.False(zone[3 * n + 3]);

        // land-side cell just outside each block's wall around (12,12): wall radius 0 / 1 / 4
        var outside = new[] { 12 * n + 11, 12 * n + 10, 12 * n + 7 };
        foreach (var b in new[] { 0, 1, 2 })
        {
            var without = SC2CostsNavmeshRules.BuildBlock(b, 5, w[b], w[4], water, n, n);
            var withZone = SC2CostsNavmeshRules.BuildBlock(b, 5, w[b], w[4], water, n, n, zone);
            Assert.Equal(64, without[outside[b]]);
            Assert.Equal(1, withZone[outside[b]]);
            // the ordinary wall at (3,3) keeps its ring (wall radius 0 / 1 / 4)
            Assert.Equal(64, withZone[3 * n + new[] { 4, 5, 8 }[b]]);
        }
        // b3 is not touched by the shore zone
        Assert.Equal(
            SC2CostsNavmeshRules.BuildBlock(3, 5, w[3], w[4], water, n, n),
            SC2CostsNavmeshRules.BuildBlock(3, 5, w[3], w[4], water, n, n, zone));
    }

    [Fact]
    public void Outline_LandOnly_MarkedCellsOpen_RealWallsAndUnmarkedStay()
    {
        const int n = 15;
        var walls0 = Mask(n, n, (7, 7));
        var masks = new byte[3][];
        masks[1] = new byte[n * n];
        masks[1][7 * n + 8] = 1;   // outline cell (walls0 + 1)
        masks[1][7 * n + 7] = 1;   // a real wall of b0: marking it does nothing
        masks[1][2 * n + 2] = 1;   // not a wall at all: nothing
        var profile = SC2CostsNavmeshRules.DeriveLandOnlyProfile(walls0, masks, n, n);

        Assert.True(profile.Outline[1][7 * n + 8]);
        Assert.False(profile.Outline[1][7 * n + 7]);
        Assert.False(profile.Walls[1][7 * n + 8]);   // opened
        Assert.True(profile.Walls[1][7 * n + 7]);    // real wall stays
        Assert.True(profile.Walls[1][7 * n + 6]);    // unmarked outline stays
        Assert.False(profile.Walls[1][2 * n + 2]);
        // ring follows what remains: the opened cell now sits next to the real wall
        var nav = SC2CostsNavmeshRules.BuildBlock(1, 3, profile.Walls[1], null, null, n, n);
        Assert.Equal(64, nav[7 * n + 8]);
        Assert.Equal(255, nav[7 * n + 6]);
        // b2 has its own mask and is unaffected by b1's
        Assert.True(profile.Walls[2][7 * n + 8]);
    }

    [Fact]
    public void Outline_WaterProfile_B4MarksShrinkB3_B1WaterIsNotOutline()
    {
        const int n = 25;
        var water = new bool[n * n];
        for (var y = 0; y < n; y++) for (var x = 20; x < n; x++) water[y * n + x] = true;
        var walls0 = Mask(n, n, (5, 12));
        var masks = new byte[5][];
        masks[4] = new byte[n * n];
        masks[4][12 * n + 6] = 1;      // b4 growth cell
        masks[1] = new byte[n * n];
        masks[1][0 * n + 22] = 1;      // open water in b1: not outline, cannot be opened
        var plain = SC2CostsNavmeshRules.DeriveWaterProfile(walls0, water, null, null, null, n, n);
        var marked = SC2CostsNavmeshRules.DeriveWaterProfile(walls0, water, null, null, masks, n, n);

        Assert.True(plain.Walls[4][12 * n + 6]);
        Assert.False(marked.Walls[4][12 * n + 6]);
        Assert.True(marked.Walls[1][12 * n + 6]);            // b1 keeps its own outline
        // b3 grows from the opened b4: it no longer reaches as far
        Assert.True(plain.Walls[3][12 * n + 9]);
        Assert.False(marked.Walls[3][12 * n + 9]);
        Assert.True(marked.Walls[1][0 * n + 22]);
        Assert.False(marked.Outline[1][0 * n + 22]);
    }

    [Fact]
    public void Outline_WaterProfile_MarkedGrowthCellIsNotAShore()
    {
        const int n = 15;
        var water = new bool[n * n];
        var walls0 = Mask(n, n, (7, 7));
        var masks = new byte[5][];
        masks[4] = new byte[n * n];
        masks[4][7 * n + 8] = 1;
        var profile = SC2CostsNavmeshRules.DeriveWaterProfile(walls0, water, null, null, masks, n, n);
        var zone = SC2CostsNavmeshRules.ShoreZone(profile.Walls[0], profile.Walls[1], profile.Walls[4], water, null, masks[4]);
        Assert.Null(zone);   // no pins, and the opened cell is the user's mark, not a shore
        Assert.NotNull(SC2CostsNavmeshRules.ShoreZone(profile.Walls[0], profile.Walls[1], profile.Walls[4], water, null, null));
    }

    [Fact]
    public void Gradient_FallsOffFromAWaterWall_AndIsAbsentFarAway()
    {
        const int n = 40;
        var water = Enumerable.Repeat(true, n * n).ToArray();
        var walls = Mask(n, n, (20, 20));
        var nav = SC2CostsNavmeshRules.Build(walls, new bool[n * n], water, walls, n, n);
        var near = nav[20 * n + 21];
        var mid = nav[20 * n + 23];
        Assert.True(near > mid && mid >= 1);
        Assert.Equal(1, nav[20 * n + 30]);
    }

    // ---- vanilla corpus

    private static (bool[][] walls, byte[][] nav, bool[] water, int n) Load(string map)
    {
        var dir = TestCorpus.MapDir(map);
        var costs = CostsBdf.Parse(BdfContainer.Read(File.ReadAllBytes(Path.Combine(dir, map + ".costs.win.bdf")), CostsBdf.Padding));
        var n = (int)Math.Sqrt(costs.Blocks[0].Navmesh.Length);
        var nav = costs.Blocks.Select(b => b.Navmesh).ToArray();
        var walls = nav.Select(a => a.Select(v => v == 255).ToArray()).ToArray();
        bool[] water = null;
        if (nav.Length == 5)
        {
            var hf = HfieldBdf.Parse(BdfContainer.Read(File.ReadAllBytes(Path.Combine(dir, map + ".hfield.win.bdf")), HfieldBdf.Padding));
            var info = InfoBdf.Parse(BdfContainer.Read(File.ReadAllBytes(Path.Combine(dir, map + ".info.win.bdf")), InfoBdf.Padding));
            var mapX = (int)hf.MapX;
            water = new bool[n * n];
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
                water[z * n + x] = hf.Heightmap[z * mapX + x] / 128f < info.WaterElevation;
        }
        return (walls, nav, water, n);
    }

    private static double Match(byte[] a, byte[] b, bool[] skip)
    {
        long same = 0, total = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (skip[i]) continue;
            total++;
            if (a[i] == b[i]) same++;
        }
        return total == 0 ? 1 : (double)same / total;
    }

    [Theory]
    [InlineData("SC2_D1_001")]
    [InlineData("SC2_D1_002")]
    [InlineData("SC2_D1_102")]
    public void Corpus_LandOnlyRing_MatchesVanillaOnPassableCells(string map)
    {
        if (!TestCorpus.IsAvailable || !Directory.Exists(TestCorpus.MapDir(map))) return;
        var (walls, nav, _, n) = Load(map);
        if (nav.Length != 3) return;
        for (var b = 0; b < 3; b++)
        {
            var built = SC2CostsNavmeshRules.BuildBlock(b, 3, walls[b], null, null, n, n);
            Assert.True(Match(built, nav[b], walls[b]) >= 0.995, $"{map} block {b}");
        }
    }

    [Theory]
    [InlineData("SC2_D1_102")]
    [InlineData("SC2_D1_101_1K")]
    public void Corpus_LandOnlyGrowth_B1AndB2FollowCrossSteps(string map)
    {
        if (!TestCorpus.IsAvailable || !Directory.Exists(TestCorpus.MapDir(map))) return;
        var (walls, nav, _, n) = Load(map);
        if (nav.Length != 3) return;
        var derived = SC2CostsNavmeshRules.DeriveLandOnlyWalls(walls[0], n, n);
        var diff1 = Enumerable.Range(0, n * n).Count(i => derived[1][i] != walls[1][i]);
        var diff2 = Enumerable.Range(0, n * n).Count(i => derived[2][i] != walls[2][i]);
        Assert.True(diff1 <= n * n / 200, $"{map} b1 differs in {diff1} cells");
        Assert.True(diff2 <= n * n / 100, $"{map} b2 differs in {diff2} cells");
    }

    [Theory]
    [InlineData("SC2_D1_202")]
    [InlineData("SC2_D1_003")]
    public void Corpus_WaterProfile_RingMatchesVanillaOnLand(string map)
    {
        if (!TestCorpus.IsAvailable || !Directory.Exists(TestCorpus.MapDir(map))) return;
        var (walls, nav, water, n) = Load(map);
        if (nav.Length != 5) return;
        for (var b = 0; b < 5; b++)
        {
            var built = SC2CostsNavmeshRules.BuildBlock(b, 5, walls[b], walls[4], water, n, n);
            var skip = new bool[n * n];
            for (var i = 0; i < skip.Length; i++) skip[i] = walls[b][i] || water[i];
            var expected = b == 3 ? 0.85 : 0.93;
            Assert.True(Match(built, nav[b], skip) >= expected, $"{map} block {b} land ring");
        }
    }
}
