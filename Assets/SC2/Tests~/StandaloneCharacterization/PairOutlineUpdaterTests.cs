using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SC2CharacterizationTests;

public class PairOutlineUpdaterTests
{
    // 32x32 cells of 8 units over a 256x256 mask: 1 pixel per unit, 8 pixels per cell.
    private static readonly SC2PairOutlineUpdater.Geometry G = new SC2PairOutlineUpdater.Geometry(256, 256, 32, 32, 8f);
    private static readonly SC2PairOutlineUpdater.Settings S = new SC2PairOutlineUpdater.Settings(8f, 4f, 1f, 0.5f, 1f);

    private static short[] Grid(short fill = -1)
    {
        var grid = new short[32 * 32 * 2];
        for (var i = 0; i < grid.Length; i++) grid[i] = fill;
        return grid;
    }

    private static short[] Paint(short[] grid, short pair, int x0, int z0, int x1, int z1)
    {
        var result = (short[])grid.Clone();
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++) { result[(z * 32 + x) * 2] = pair; result[(z * 32 + x) * 2 + 1] = pair; }
        return result;
    }

    private static byte Coverage(byte[] layer, int x, int y) => layer[(y * 256 + x) * 2 + 1];

    private static int CoveredPixels(byte[] layer)
    {
        var n = 0;
        for (var i = 1; i < layer.Length; i += 2) if (layer[i] != 0) n++;
        return n;
    }

    private static byte[] NewLayer() => new byte[256 * 256 * 2];

    private static bool Step(byte[] layer, short[] before, short[] after, int pair)
        => SC2PairOutlineUpdater.Update(layer, G, before, after, pair, S, S);

    /// <summary>Largest distance (in pixels) from any outline pixel to the nearest cell that lies on a pair border.</summary>
    private static double FarthestOutlineFromBorder(byte[] layer, short[] assignments)
    {
        var border = SC2PairOutlineMath.BorderCells(assignments, 32, 32);
        var px = new List<(int x, int y)>();
        foreach (var cell in border) px.Add((cell % 32, cell / 32));
        double worst = 0;
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            if (Coverage(layer, x, y) == 0) continue;
            var best = double.MaxValue;
            foreach (var (cx, cz) in px)
            {
                var dx = Math.Max(0, Math.Max(cx * 8 - x, x - (cx * 8 + 7)));
                var dy = Math.Max(0, Math.Max(cz * 8 - y, y - (cz * 8 + 7)));
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
            }
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    [Fact]
    public void PaintingTheBorderWritesAnOutlineNextToTheNewCells()
    {
        var before = Grid();
        var after = Paint(before, 0, 8, 8, 15, 15);
        var layer = NewLayer();

        Assert.True(Step(layer, before, after, 0));

        Assert.True(CoveredPixels(layer) > 0);
        Assert.Equal(0, Coverage(layer, 100, 100));   // the middle of the square is not outlined
    }

    [Fact]
    public void ARegionPaintedOverTheWholeOldOneRemovesTheOldOutline()
    {
        var empty = Grid();
        var first = Paint(empty, 0, 8, 8, 15, 15);
        var layer = NewLayer();
        Step(layer, empty, first, 0);
        Assert.True(Coverage(layer, 68, 100) != 0 || Coverage(layer, 66, 100) != 0);   // inside the old left border

        var second = Paint(first, 1, 6, 6, 17, 17);
        Step(layer, first, second, 1);

        // 20 px in from the new border: only the old pair's outline could have been here.
        Assert.Equal(0, Coverage(layer, 68, 100));
        Assert.Equal(0, Coverage(layer, 66, 100));
        Assert.Equal(0, Coverage(layer, 100, 68));
        Assert.True(FarthestOutlineFromBorder(layer, second) <= 16);
    }

    [Fact]
    public void AGrownRegionLeavesNoOutlineAtItsOldBorder()
    {
        var empty = Grid();
        var first = Paint(empty, 0, 8, 8, 15, 15);
        var layer = NewLayer();
        Step(layer, empty, first, 0);

        var grown = Paint(first, 0, 8, 8, 19, 15);   // 4 more columns on the right
        Step(layer, first, grown, 0);

        // The old right border was at x = 128 (cells 15|16); it is now 32 px inside the region.
        for (var x = 112; x <= 135; x++) Assert.Equal(0, Coverage(layer, x, 100));
        Assert.True(FarthestOutlineFromBorder(layer, grown) <= 16);
    }

    [Fact]
    public void ANeighbourPaintedOverAnOutlinedPairRemovesTheSpreadOntoIt()
    {
        // Pair 0 outlined next to pair 1: the outline spreads onto pair 1's cells. Pair 1 then takes the cells of pair 0.
        var start = Paint(Grid(), 1, 0, 0, 31, 31);
        var withZero = Paint(start, 0, 8, 8, 15, 15);
        var layer = NewLayer();
        Step(layer, start, withZero, 0);
        Assert.True(CoveredPixels(layer) > 0);

        var back = Paint(withZero, 1, 4, 4, 20, 20);   // pair 1 covers pair 0 completely
        Step(layer, withZero, back, 1);

        Assert.Equal(0, CoveredPixels(layer));   // no borders at all any more
    }

    [Fact]
    public void ClearingEverythingRemovesAllOutline()
    {
        var empty = Grid();
        var first = Paint(empty, 0, 8, 8, 15, 15);
        var layer = NewLayer();
        Step(layer, empty, first, 0);

        Step(layer, first, empty, -1);

        Assert.Equal(0, CoveredPixels(layer));
    }

    [Fact]
    public void OutlineNextToAnUnrelatedUnchangedBorderStays()
    {
        var empty = Grid();
        var squareA = Paint(empty, 0, 2, 2, 7, 7);
        var layer = NewLayer();
        Step(layer, empty, squareA, 0);
        var both = Paint(squareA, 1, 20, 20, 25, 25);
        Step(layer, squareA, both, 1);
        var before = CoveredPixels(layer);
        var aOutline = Coverage(layer, 18, 40);

        // Another stroke far away: A's outline must not change.
        var third = Paint(both, 2, 12, 2, 14, 4);
        Step(layer, both, third, 2);

        Assert.Equal(aOutline, Coverage(layer, 18, 40));
        Assert.True(CoveredPixels(layer) >= before);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AfterAnySequenceOfStrokesEveryOutlinePixelIsNextToACurrentBorder(int seed)
    {
        var random = new Random(seed);
        var grid = Grid();
        var layer = NewLayer();
        var reach = SC2PairOutlineUpdater.SupportReachPixels(G, S.Width, S.Spread, S.Smoothing);
        for (var step = 0; step < 14; step++)
        {
            var pair = (short)random.Next(-1, 3);   // -1 erases
            var x0 = random.Next(0, 28); var z0 = random.Next(0, 28);
            var next = Paint(grid, pair, x0, z0, x0 + random.Next(0, 6), z0 + random.Next(0, 6));
            Step(layer, grid, next, pair);
            grid = next;

            var lines = SC2PairOutlineMath.StableBorderLines(grid, null, 32, 32, 0, 0, 32, 32, 256, 256, new SC2PairOutlineMath.MaskRect(0, 0, 256, 256));
            var near = SC2PairOutlineMath.DilateMask(lines, 256, 256, reach);
            for (var i = 0; i < 256 * 256; i++)
                Assert.False(layer[i * 2 + 1] != 0 && near[i] == 0, $"seed {seed} step {step}: outline at ({i % 256},{i / 256}) is away from every border");
        }
    }

    [Theory]
    [InlineData(512, 128, 2f, 8f, 4f, 1f, 11)]
    [InlineData(512, 256, 1f, 8f, 4f, 1f, 12)]
    [InlineData(512, 256, 1f, 24f, 12f, 3f, 13)]
    [InlineData(1024, 128, 4f, 8f, 4f, 0f, 14)]
    [InlineData(512, 64, 4f, 16f, 0f, 2f, 15)]
    [InlineData(512, 256, 2f, 8f, 4f, 1f, 16)]
    public void TheInvariantAlsoHoldsAtOtherMapAndMaskScales(int mask, int grid, float cellSize, float width, float spread, float smoothing, int seed)
    {
        var g = new SC2PairOutlineUpdater.Geometry(mask, mask, grid, grid, cellSize);
        var s = new SC2PairOutlineUpdater.Settings(width, spread, smoothing, 0.5f, 1f);
        var random = new Random(seed);
        var assignments = new short[grid * grid * 2];
        for (var i = 0; i < assignments.Length; i++) assignments[i] = -1;
        var layer = new byte[mask * mask * 2];
        var reach = SC2PairOutlineUpdater.SupportReachPixels(g, width, spread, smoothing);
        var all = new SC2PairOutlineMath.MaskRect(0, 0, mask, mask);
        for (var step = 0; step < 12; step++)
        {
            var pair = (short)random.Next(-1, 3);
            var next = (short[])assignments.Clone();
            var x0 = random.Next(0, grid - 4); var z0 = random.Next(0, grid - 4);
            var w = random.Next(1, Math.Max(2, grid / 6)); var h = random.Next(1, Math.Max(2, grid / 6));
            for (var z = z0; z < Math.Min(grid, z0 + h); z++)
            for (var x = x0; x < Math.Min(grid, x0 + w); x++) { next[(z * grid + x) * 2] = pair; next[(z * grid + x) * 2 + 1] = pair; }
            SC2PairOutlineUpdater.Update(layer, g, assignments, next, pair, s, s);
            assignments = next;

            var lines = SC2PairOutlineMath.StableBorderLines(assignments, null, grid, grid, 0, 0, grid, grid, mask, mask, all);
            var near = SC2PairOutlineMath.DilateMask(lines, mask, mask, reach);
            for (var i = 0; i < mask * mask; i++)
                Assert.False(layer[i * 2 + 1] != 0 && near[i] == 0, $"step {step}: outline at ({i % mask},{i / mask}) is away from every border");
        }
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    public void ARegionRepaintedInStepsEndsWithExactlyTheOutlineOfItsFinalShape(int seed)
    {
        // One pair, strokes that overlap or touch (so it stays one closed region): after every stroke the layer must equal the
        // outline drawn once for the region as it is now.
        var random = new Random(seed);
        var grid = Grid();
        var layer = NewLayer();
        var x = 12; var z = 12;
        for (var step = 0; step < 10; step++)
        {
            x = Math.Max(2, Math.Min(26, x + random.Next(-4, 5)));
            z = Math.Max(2, Math.Min(26, z + random.Next(-4, 5)));
            var next = Paint(grid, 0, x, z, x + random.Next(1, 5), z + random.Next(1, 5));
            Step(layer, grid, next, 0);
            grid = next;

            var fresh = NewLayer();
            SC2PairOutlineUpdater.Update(fresh, G, Grid(), grid, 0, S, S);
            // The smoothing sees a different work rectangle, so the shape can differ by a pixel or two: every strongly covered
            // pixel of one has a strongly covered pixel within two pixels in the other.
            AssertSameShape(layer, fresh, $"seed {seed} step {step}: stepwise outline vs fresh outline");
            AssertSameShape(fresh, layer, $"seed {seed} step {step}: fresh outline vs stepwise outline");
        }
    }

    private static void AssertSameShape(byte[] a, byte[] b, string message)
    {
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            if (Coverage(a, x, y) < 64) continue;
            var found = false;
            for (var dy = -2; dy <= 2 && !found; dy++)
            for (var dx = -2; dx <= 2 && !found; dx++)
            {
                var xx = x + dx; var yy = y + dy;
                if (xx >= 0 && yy >= 0 && xx < 256 && yy < 256 && Coverage(b, xx, yy) >= 64) found = true;
            }
            Assert.True(found, $"{message}: pixel ({x},{y}) is covered at {Coverage(a, x, y)} but nothing within two pixels in the other");
        }
    }

    [Fact]
    public void RepaintingWithANarrowerWidthReplacesTheWiderRing()
    {
        var wide = new SC2PairOutlineUpdater.Settings(16f, 8f, 1f, 0.5f, 1f);
        var narrow = new SC2PairOutlineUpdater.Settings(4f, 2f, 1f, 0.5f, 1f);
        var empty = Grid();
        var first = Paint(empty, 0, 8, 8, 20, 20);
        var layer = NewLayer();
        SC2PairOutlineUpdater.Update(layer, G, empty, first, 0, wide, wide);
        Assert.NotEqual(0, Coverage(layer, 64 + 12, 120));   // 12 px inside the left border: in the wide ring

        var more = Paint(first, 0, 21, 8, 21, 20);   // one more column touches the region
        SC2PairOutlineUpdater.Update(layer, G, first, more, 0, narrow, wide);

        Assert.Equal(0, Coverage(layer, 64 + 12, 120));
        Assert.NotEqual(0, Coverage(layer, 64 + 1, 120));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(3f)]
    public void ASingleCellRegionGetsAnOutline(float smoothing)
    {
        var settings = new SC2PairOutlineUpdater.Settings(8f, 4f, smoothing, 0.5f, 1f);
        var empty = Grid();
        var one = Paint(empty, 0, 15, 15, 15, 15);
        var layer = NewLayer();

        SC2PairOutlineUpdater.Update(layer, G, empty, one, 0, settings, settings);

        // Cell (15, 15) covers pixels 120..127: the outline must be on its border, and nothing in the middle of the empty map.
        Assert.NotEqual(0, Coverage(layer, 119, 124));   // just outside
        Assert.NotEqual(0, Coverage(layer, 121, 124));   // just inside
        Assert.Equal(0, Coverage(layer, 20, 20));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(3f)]
    public void WhatIsLeftOfAnOutlinedPairPaintedOverStillHasItsOutline(float smoothing)
    {
        var settings = new SC2PairOutlineUpdater.Settings(8f, 4f, smoothing, 0.5f, 1f);
        var empty = Grid();
        var first = Paint(empty, 0, 8, 8, 20, 20);
        var layer = NewLayer();
        SC2PairOutlineUpdater.Update(layer, G, empty, first, 0, settings, settings);

        // Pair 1 takes the whole area except one cell in the corner (cells 8,8).
        var second = Paint(first, 1, 9, 8, 20, 20);
        second = Paint(second, 1, 8, 9, 20, 20);
        SC2PairOutlineUpdater.Update(layer, G, first, second, 1, settings, settings);

        // The cell (8, 8), pixels 64..71, is all that is left of pair 0: it keeps an outline of its own...
        Assert.NotEqual(0, Coverage(layer, 66, 66));
        Assert.NotEqual(0, Coverage(layer, 63, 66));
        // ...and the old pair-0 outline across the middle of what pair 1 now owns is gone.
        Assert.Equal(0, Coverage(layer, 120, 120));
        Assert.Equal(0, Coverage(layer, 100, 130));
    }

    [Fact]
    public void AnUnoutlinedNeighbourIsNotOutlinedBecauseOfAStrokeNextToIt()
    {
        var empty = Grid();
        var neighbour = Paint(empty, 2, 2, 2, 8, 8);   // never outlined
        var layer = NewLayer();
        var withPainted = Paint(neighbour, 0, 10, 2, 14, 8);
        SC2PairOutlineUpdater.Update(layer, G, neighbour, withPainted, 0, S, S);

        Assert.NotEqual(0, Coverage(layer, 10 * 8 + 4, 5 * 8));     // pair 0 itself is outlined
        Assert.Equal(0, Coverage(layer, 2 * 8 + 3, 5 * 8));         // pair 2, far side of its region: no outline
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1.17f, 1f)]
    [InlineData(3f, 1f)]
    [InlineData(3f, 0f)]
    public void AOneCellGapBetweenTwoPartsOfARegionKeepsTheOutlineAlongItsBorders(float smoothing, float softness)
    {
        // Two diagonal staircase bands of one pair with a diagonal strip of unassigned cells (x - z = 4) between them: the
        // smoothing must not bridge the strip and take the outline along it away.
        var settings = new SC2PairOutlineUpdater.Settings(16f, 8f, smoothing, softness, 1f);
        var empty = Grid();
        var both = (short[])empty.Clone();
        for (var z = 0; z < 32; z++)
        for (var x = 0; x < 32; x++)
        {
            var d = x - z;
            if (x < 3 || x > 28 || !(Math.Abs(d) <= 3 || d >= 5 && d <= 7)) continue;
            both[(z * 32 + x) * 2] = 0; both[(z * 32 + x) * 2 + 1] = 0;
        }
        var layer = NewLayer();

        SC2PairOutlineUpdater.Update(layer, G, empty, both, 0, settings, settings);

        // Cell (16, 12) (x - z = 4) is a gap cell; its left neighbour (15, 12) is region. The pixels of the gap cell next to
        // that border, and the ones of the region cell, carry the outline.
        var gapCellLeftEdge = 16 * 8;
        Assert.NotEqual(0, Coverage(layer, gapCellLeftEdge, 12 * 8 + 4));
        Assert.NotEqual(0, Coverage(layer, gapCellLeftEdge - 1, 12 * 8 + 4));
        // The same on the other side of the gap, cell (17, 12)'s right neighbour... (x - z = 5 is region).
        Assert.NotEqual(0, Coverage(layer, 17 * 8 + 7, 12 * 8 + 4));
    }

    [Theory]
    [InlineData(1.17f, 1f)]
    [InlineData(3f, 1f)]
    [InlineData(3f, 0f)]
    public void TheTipsOfTheStaircaseAreOutlinedToo(float smoothing, float softness)
    {
        // A block with a one-cell bump on its right side and a diagonal band ending in a tip: the smoothing cuts the bump and
        // the tip off, but the pixels along their real borders still carry the outline.
        var settings = new SC2PairOutlineUpdater.Settings(2f, 1f, smoothing, softness, 1f);
        var empty = Grid();
        var shape = (short[])empty.Clone();
        for (var z = 0; z < 32; z++)
        for (var x = 0; x < 32; x++)
        {
            var block = x >= 8 && x <= 19 && z >= 8 && z <= 19;
            var bump = x == 20 && z == 13;
            if (!block && !bump) continue;
            shape[(z * 32 + x) * 2] = 0; shape[(z * 32 + x) * 2 + 1] = 0;
        }
        var layer = NewLayer();

        SC2PairOutlineUpdater.Update(layer, G, empty, shape, 0, settings, settings);

        // The bump is cell (20, 13): pixels x 160..167, y 104..111. Just outside its far edge and above/below it:
        Assert.NotEqual(0, Coverage(layer, 168, 108));
        Assert.NotEqual(0, Coverage(layer, 164, 103));
        Assert.NotEqual(0, Coverage(layer, 164, 112));
        // and just inside its far edge:
        Assert.NotEqual(0, Coverage(layer, 167, 108));

    }

    [Fact]
    public void Minimum_IsTwoMaskPixelsForWidthAndSpreadAndOnePixelForSmoothing()
    {
        // G: 1 pixel per unit, 8 pixels per cell.
        var minimum = SC2PairOutlineUpdater.Minimum(G);
        Assert.Equal(2f, minimum.Width, 3);
        Assert.Equal(2f, minimum.Spread, 3);
        Assert.Equal(1f / 8f, minimum.Smoothing, 3);

        // A finer mask (2048 px over 512 cells of 2 units: 2 px per unit, 4 px per cell).
        var fine = new SC2PairOutlineUpdater.Geometry(2048, 2048, 512, 512, 2f);
        var fineMinimum = SC2PairOutlineUpdater.Minimum(fine);
        Assert.Equal(1f, fineMinimum.Width, 3);
        Assert.Equal(0.25f, fineMinimum.Smoothing, 3);
    }

    [Fact]
    public void Clamp_RaisesTooSmallValuesAndKeepsZeroSpreadAndSmoothingOff()
    {
        var clamped = SC2PairOutlineUpdater.Clamp(new SC2PairOutlineUpdater.Settings(0.1f, 0.3f, 0.01f, 0.4f, 0.8f), G);
        Assert.Equal(2f, clamped.Width, 3);
        Assert.Equal(2f, clamped.Spread, 3);
        Assert.Equal(0.125f, clamped.Smoothing, 3);
        Assert.Equal(0.4f, clamped.Softness, 3);
        Assert.Equal(0.8f, clamped.Blend, 3);

        Assert.Equal(SC2PairOutlineUpdater.MaximumSmoothingCells, SC2PairOutlineUpdater.Clamp(new SC2PairOutlineUpdater.Settings(5f, 1f, 3f, 0f, 1f), G).Smoothing, 3);

        var off = SC2PairOutlineUpdater.Clamp(new SC2PairOutlineUpdater.Settings(5f, 0f, 0f, 0f, 1f), G);
        Assert.Equal(5f, off.Width, 3);
        Assert.Equal(0f, off.Spread, 3);
        Assert.Equal(0f, off.Smoothing, 3);
    }

    // Diagonal bands, a staircase block with a bump, a single cell and a one-cell gap: the shapes the outline has to survive.
    private static short[] MinimumTestShapes()
    {
        var grid = Grid();
        for (var z = 0; z < 32; z++)
        for (var x = 0; x < 32; x++)
        {
            var d = x - z;
            var band = x > 2 && x < 14 && (Math.Abs(d) <= 2 || d >= 4 && d <= 5);   // diagonal band, one-cell gap, second band
            var block = x >= 18 && x <= 26 && z >= 3 && z <= 10 || x == 27 && z == 6;   // block with a bump
            var single = x == 24 && z == 22;
            var antiDiagonal = Math.Abs(x + z - 40) <= 1 && x >= 10 && x <= 22 && z >= 18;
            if (band || block || single || antiDiagonal) { grid[(z * 32 + x) * 2] = 0; grid[(z * 32 + x) * 2 + 1] = 0; }
        }
        return grid;
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(1f, 0f, 1f)]
    public void AtTheMinimumValuesEveryPixelAlongEveryBorderIsStillOutlined(float softness, float spreadOn, float smoothingOn)
    {
        var minimum = SC2PairOutlineUpdater.Minimum(G);
        var settings = new SC2PairOutlineUpdater.Settings(minimum.Width, minimum.Spread * spreadOn, minimum.Smoothing * smoothingOn, softness, 1f);
        var empty = Grid();
        var shapes = MinimumTestShapes();
        var layer = NewLayer();

        SC2PairOutlineUpdater.Update(layer, G, empty, shapes, 0, settings, settings);

        var lines = SC2PairOutlineMath.StableBorderLines(shapes, null, 32, 32, 0, 0, 32, 32, 256, 256, new SC2PairOutlineMath.MaskRect(0, 0, 256, 256));
        var total = 0; var covered = 0;
        for (var y = 0; y < 256; y++)
        for (var x = 0; x < 256; x++)
        {
            if (lines[y * 256 + x] == 0) continue;
            // The inside pixels always get the band (Width is always on); the outside ones only with a Spread.
            var inside = shapes[((y / 8) * 32 + x / 8) * 2] == 0;
            if (!inside && spreadOn == 0f) continue;
            total++;
            if (Coverage(layer, x, y) >= 128) covered++;
        }
        Assert.True(total > 400);
        Assert.Equal(total, covered);
    }

    [Fact]
    public void BelowTheMinimumTheOutlineIsRaisedToTheMinimumNotDropped()
    {
        var tiny = new SC2PairOutlineUpdater.Settings(0.01f, 0.01f, 0.01f, 0f, 1f);
        var minimum = SC2PairOutlineUpdater.Minimum(G);
        var atMinimum = new SC2PairOutlineUpdater.Settings(minimum.Width, minimum.Spread, minimum.Smoothing, 0f, 1f);
        var shapes = MinimumTestShapes();
        var a = NewLayer(); var b = NewLayer();

        SC2PairOutlineUpdater.Update(a, G, Grid(), shapes, 0, tiny, tiny);
        SC2PairOutlineUpdater.Update(b, G, Grid(), shapes, 0, atMinimum, atMinimum);

        Assert.True(CoveredPixels(a) > 400);
        Assert.Equal(b, a);
    }

    private static long TotalCoverage(byte[] layer)
    {
        long sum = 0;
        for (var i = 1; i < layer.Length; i += 2) sum += layer[i];
        return sum;
    }

    [Theory]
    [InlineData(1.17f, 0.5f)]
    [InlineData(1.5f, 0.5f)]
    [InlineData(1.17f, 1f)]
    [InlineData(1.5f, 0f)]
    public void SmoothingAddsNoThicknessAlongFlatBorders(float smoothing, float softness)
    {
        // A rectangle and a disc-less block: straight borders only. Smoothing may round the corners but must not thicken the
        // outline along the sides.
        var empty = Grid();
        var shapes = Paint(empty, 0, 6, 6, 24, 20);
        var plainSettings = new SC2PairOutlineUpdater.Settings(6f, 3f, 0f, softness, 1f);
        var smoothSettings = new SC2PairOutlineUpdater.Settings(6f, 3f, smoothing, softness, 1f);
        var plain = NewLayer(); var smooth = NewLayer();

        SC2PairOutlineUpdater.Update(plain, G, empty, shapes, 0, plainSettings, plainSettings);
        SC2PairOutlineUpdater.Update(smooth, G, empty, shapes, 0, smoothSettings, smoothSettings);

        // The middle of the left side (x = 48), rows 80..120: the same coverage with and without smoothing.
        for (var y = 80; y <= 120; y += 8)
            for (var x = 40; x <= 56; x++)
                Assert.InRange(Math.Abs(Coverage(smooth, x, y) - Coverage(plain, x, y)), 0, 6);
        var ratio = (double)TotalCoverage(smooth) / TotalCoverage(plain);
        Assert.True(ratio < 1.3, "smoothed outline covers " + ratio.ToString("0.00") + " times the area of the unsmoothed one");
    }

    [Fact]
    public void StrokesFarApartAreUpdatedOnTheirOwnAndGiveTheSameOutlineAsOneByOne()
    {
        // Two far-apart places changed at once (as with a symmetric stroke) must give the same layer as two separate updates.
        var empty = Grid();
        var left = Paint(empty, 0, 3, 14, 7, 18);
        var both = Paint(left, 0, 24, 14, 28, 18);
        var together = NewLayer();
        SC2PairOutlineUpdater.Update(together, G, empty, both, 0, S, S);

        var oneByOne = NewLayer();
        SC2PairOutlineUpdater.Update(oneByOne, G, empty, left, 0, S, S);
        SC2PairOutlineUpdater.Update(oneByOne, G, left, both, 0, S, S);

        AssertSameShape(together, oneByOne, "all at once vs one by one");
        AssertSameShape(oneByOne, together, "one by one vs all at once");
        Assert.NotEqual(0, Coverage(together, 24 * 8 - 1, 16 * 8));   // the right-hand place is outlined
        Assert.NotEqual(0, Coverage(together, 3 * 8, 16 * 8));        // and the left-hand one
        Assert.Equal(0, Coverage(together, 16 * 8, 16 * 8));          // nothing between them
    }

    [Fact]
    public void ABorderThatMovedAFewCellsLeavesNoOutlineAtTheOldPosition()
    {
        // A long vertical border, then the pair grows by a single column: the old border is 8 px away from the new one.
        var empty = Grid();
        var first = Paint(empty, 0, 4, 4, 15, 27);
        var layer = NewLayer();
        Step(layer, empty, first, 0);

        var grown = Paint(first, 0, 4, 4, 16, 27);
        Step(layer, first, grown, 0);

        // Whatever is there now must be along the new border (x = 136): nothing may remain that only fits the old one, on
        // the far side of the old border (x = 128): well inside the pair, deeper than the band reaches from the new border.
        for (var y = 80; y < 160; y++) Assert.Equal(0, Coverage(layer, 118, y));
    }
}
