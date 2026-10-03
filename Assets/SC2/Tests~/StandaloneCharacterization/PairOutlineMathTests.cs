public sealed class PairOutlineMathTests
{
    // A 4x4 cell grid whose left two columns belong to pair 1 and the rest to pair 2.
    private static short[] TwoPairGrid()
    {
        var triangles = new short[4 * 4 * 2];
        for (var z = 0; z < 4; z++)
        for (var x = 0; x < 4; x++)
        {
            var pair = (short)(x < 2 ? 1 : 2);
            triangles[(z * 4 + x) * 2] = pair;
            triangles[(z * 4 + x) * 2 + 1] = pair;
        }
        return triangles;
    }

    [Fact]
    public void RasterizePairCells_MapsMaskPixelsToTheCellUnderTheirCentre()
    {
        // 8x8 mask over the 4x4 grid: each cell is 2x2 pixels, pair 1 fills the left half.
        var inside = SC2PairOutlineMath.RasterizePairCells(TwoPairGrid(), 4, 4, 1, 8, 8);

        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            Assert.Equal(x < 4 ? 1 : 0, inside[y * 8 + x]);
    }

    [Fact]
    public void RasterizePairCells_CountsACellWhenEitherTriangleHasThePair()
    {
        var triangles = new short[1 * 1 * 2];
        triangles[0] = -1;
        triangles[1] = 3;
        Assert.Equal(1, SC2PairOutlineMath.RasterizePairCells(triangles, 1, 1, 3, 2, 2)[0]);
        Assert.Equal(0, SC2PairOutlineMath.RasterizePairCells(triangles, 1, 1, 4, 2, 2)[0]);
    }

    [Fact]
    public void BuildBand_IsFullAtTheBorderAndFadesInwardOnlyInsideTheRegion()
    {
        var inside = SC2PairOutlineMath.RasterizePairCells(TwoPairGrid(), 4, 4, 1, 8, 8);

        var band = SC2PairOutlineMath.BuildBand(inside, 8, 8, widthPixels: 3f, softness: 1f);

        var row = 3 * 8;
        Assert.True(band[row + 3] > band[row + 2], "the pixel next to the border is stronger than the one inside it");
        Assert.True(band[row + 2] > band[row + 1]);
        Assert.Equal(0, band[row + 0]);                     // 3 pixels from the border: out of the band
        for (var y = 0; y < 8; y++)
            for (var x = 4; x < 8; x++) Assert.Equal(0, band[y * 8 + x]); // never outside the region
    }

    [Fact]
    public void BuildBand_HardEdgeIsFullStrengthAcrossTheWholeWidth()
    {
        var inside = SC2PairOutlineMath.RasterizePairCells(TwoPairGrid(), 4, 4, 1, 8, 8);

        var band = SC2PairOutlineMath.BuildBand(inside, 8, 8, widthPixels: 2.5f, softness: 0f);

        Assert.Equal(255, band[3 * 8 + 3]);
        Assert.Equal(255, band[3 * 8 + 2]);
        Assert.Equal(0, band[3 * 8 + 1]);
    }

    [Fact]
    public void BuildBand_DoesNotOutlineTheMapEdgeOrAWholeMapRegion()
    {
        var all = new byte[8 * 8];
        for (var i = 0; i < all.Length; i++) all[i] = 1;
        Assert.All(SC2PairOutlineMath.BuildBand(all, 8, 8, 4f, 0.5f), b => Assert.Equal(0, b));

        // Pair 1 touches the map's left, top and bottom edges: only its right border is outlined.
        var inside = SC2PairOutlineMath.RasterizePairCells(TwoPairGrid(), 4, 4, 1, 8, 8);
        var band = SC2PairOutlineMath.BuildBand(inside, 8, 8, 2f, 0f);
        Assert.Equal(0, band[0 * 8 + 0]);
        Assert.Equal(255, band[0 * 8 + 3]);
    }

    [Fact]
    public void BuildBand_ZeroWidthGivesAnEmptyBand()
    {
        var inside = SC2PairOutlineMath.RasterizePairCells(TwoPairGrid(), 4, 4, 1, 8, 8);
        Assert.All(SC2PairOutlineMath.BuildBand(inside, 8, 8, 0f, 0.5f), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ApplyBand_LeavesTheInputUntouchedAndAppliesEachPixelByItsStrength()
    {
        var layer = new byte[4 * 2];
        layer[0] = 100; layer[1] = 0;      // transparent pixel
        layer[2] = 100; layer[3] = 255;    // opaque pixel, value 100
        layer[4] = 50; layer[5] = 40;      // pixel outside the band
        layer[6] = 0; layer[7] = 128;      // half covered, value 0
        var before = (byte[])layer.Clone();
        byte[] band = { 255, 128, 0, 255 };

        var result = SC2BlendMaskLayerMath.ApplyBand(layer, band, 1f, 4);

        Assert.Equal(before, layer);
        Assert.Equal(255, result[0]);                       // band stronger than coverage: target value at band strength
        Assert.Equal(255, result[1]);
        Assert.InRange(result[2], 127, 129);                // opaque layer: value pulled up to target*strength (max(100, 128))
        Assert.Equal(255, result[3]);                       // coverage untouched
        Assert.Equal(50, result[4]);                        // outside the band: unchanged
        Assert.Equal(40, result[5]);
        Assert.Equal(255, result[6]);                       // band stronger than the half coverage
        Assert.Equal(255, result[7]);
    }

    [Fact]
    public void ApplyBand_AppliedTwiceGivesTheSameResultAsOnce()
    {
        var layer = new byte[6 * 2];
        for (var i = 0; i < 6; i++) { layer[i * 2] = (byte)(i * 40); layer[i * 2 + 1] = (byte)(i * 50); }
        byte[] band = { 255, 200, 128, 60, 10, 0 };

        foreach (var target in new[] { 1f, 0f, 0.6f })
        {
            var once = SC2BlendMaskLayerMath.ApplyBand(layer, band, target, 6);
            var twice = SC2BlendMaskLayerMath.ApplyBand(once, band, target, 6);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void ApplyBand_PullsAnOpaqueLayerTowardTheTargetFromEitherSideButNeverPastIt()
    {
        var layer = new byte[2 * 2];
        layer[0] = 200; layer[1] = 255;    // value above a low target
        layer[2] = 20; layer[3] = 255;     // value below a high target

        var down = SC2BlendMaskLayerMath.ApplyBand(layer, new byte[] { 255, 0 }, 0.25f, 2);
        Assert.Equal(64, down[0]);         // fully inside the band: reaches the target 0.25 (= 64)
        Assert.Equal(20, down[2]);

        var up = SC2BlendMaskLayerMath.ApplyBand(layer, new byte[] { 0, 128 }, 1f, 2);
        Assert.Equal(200, up[0]);
        Assert.InRange(up[2], 127, 129);   // max(20, 1 * 0.5)
    }

    [Fact]
    public void ApplyBand_TreatsMissingLayerDataAsTransparent()
    {
        var result = SC2BlendMaskLayerMath.ApplyBand(null, new byte[] { 255, 0 }, 0.5f, 2);
        Assert.InRange(result[0], 127, 128);
        Assert.Equal(255, result[1]);
        Assert.Equal(0, result[2]);
        Assert.Equal(0, result[3]);
    }

    // A region whose border is a staircase of 4-pixel steps, like a pair painted over whole grid cells.
    private static byte[] StairRegion(int size)
    {
        var inside = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            if (x < 10 + (y / 4) * 3) inside[y * size + x] = 1;
        return inside;
    }

    // How jagged the inner edge of the band is: for every row, the leftmost x where the band reaches half strength (the band
    // hugs the region's right-hand border, so that is where its inner edge lies); the sum of the second differences of that
    // position down the rows is large for a stepped edge and small for a smooth one.
    private static float InnerEdgeRoughness(byte[] band, int size)
    {
        var edge = new float[size];
        for (var y = 0; y < size; y++)
        {
            edge[y] = size;
            for (var x = 0; x < size; x++)
                if (band[y * size + x] >= 128) { edge[y] = x; break; }
        }
        var sum = 0f;
        for (var y = 4; y < size - 4; y++) sum += System.Math.Abs(edge[y + 1] - 2f * edge[y] + edge[y - 1]);
        return sum;
    }

    [Fact]
    public void BuildBand_SmoothingMakesTheBandOfAStaircaseBorderLessJagged()
    {
        const int size = 40;
        var inside = StairRegion(size);

        // A narrow, fully soft band: its inner edge lies about two pixels from the border, closer than the 3-pixel steps are deep.
        var sharp = SC2PairOutlineMath.BuildBand(inside, size, size, 4f, 1f, 0f, 3f);
        var smooth = SC2PairOutlineMath.BuildBand(inside, size, size, 4f, 1f, 3f, 3f);

        Assert.True(InnerEdgeRoughness(smooth, size) < InnerEdgeRoughness(sharp, size) * 0.7f,
            "smoothed " + InnerEdgeRoughness(smooth, size) + " vs sharp " + InnerEdgeRoughness(sharp, size));
        // The band still lands on the border.
        Assert.True(smooth[20 * size + (10 + (20 / 4) * 3) - 1] > 0);
    }

    [Theory]
    [InlineData(3f, 10f)]
    [InlineData(6f, 10f)]
    [InlineData(2f, 4f)]
    public void BuildBand_IsTheSameOnBothSidesOfTheStaircaseBorder(float smoothingPixels, float spreadPixels)
    {
        // The band is measured from the smoothed outline, not from the staircase, and is one function of the position: across
        // the real border (which does not follow the smooth outline) neighbouring pixels differ by no more than the slope of
        // the fade, so the two pairs meet at the same blend value and the border between them does not show.
        const int size = 64;
        var inside = new byte[size * size];
        for (var y = 8; y < 56; y++)
            for (var x = 8 + (y / 6) % 3 * 4; x < 52 - (y / 5) % 3 * 4; x++) inside[y * size + x] = 1;

        var band = SC2PairOutlineMath.BuildBand(inside, size, size, 10f, 0.5f, smoothingPixels, spreadPixels);

        var compared = 0;
        var worstStep = 0;
        for (var y = 1; y < size - 1; y++)
        for (var x = 1; x < size - 2; x++)
        {
            var i = y * size + x;
            if (inside[i] == inside[i + 1]) continue;   // not a border between two horizontal neighbours
            worstStep = System.Math.Max(worstStep, System.Math.Abs(band[i] - band[i + 1]));
            compared++;
        }
        Assert.True(compared > 40);
        // The steepest fade here is 255 / (half of the narrowest span), plus a little for the pixel grid.
        Assert.True(worstStep <= 255f / (0.5f * System.Math.Min(10f, spreadPixels)) + 12f, "largest jump across the border: " + worstStep);
    }

    [Fact]
    public void BuildBand_SmoothingRoundsTheRegionOffWithoutMovingItFarOrOutliningTheMapEdge()
    {
        const int size = 40;
        var inside = StairRegion(size);

        var smooth = SC2PairOutlineMath.BuildBand(inside, size, size, 6f, 0.5f, 3f, 6f);

        // The smoothed outline lies within about a smoothing radius of the real border: nothing further out than width/spread
        // plus that is outlined.
        var border = 10 + (20 / 4) * 3;   // first pixel outside the region in row 20
        Assert.True(smooth[20 * size + border - 1] > 100);
        Assert.True(smooth[20 * size + border] > 100);
        Assert.Equal(0, smooth[20 * size + border + 12]);
        Assert.True(smooth[0] < 8);                          // the region touches the map's left edge: no outline there
        Assert.True(smooth[(size - 1) * size] < 8);
    }

    [Fact]
    public void BuildBand_StrongSmoothingStaysWithinTheMaxShiftOfTheRealBorder()
    {
        const int size = 64;
        var inside = new byte[size * size];
        // A blob with a ragged outline: a disc plus large bumps that a strong smoothing would cut off.
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - 32; var dy = y - 32;
            var radius = 14 + ((x / 8 + y / 8) % 2 == 0 ? 9 : 0);
            if (dx * dx + dy * dy < radius * radius) inside[y * size + x] = 1;
        }

        var free = SC2PairOutlineMath.BuildBand(inside, size, size, 3f, 0f, 16f, 3f);
        var limited = SC2PairOutlineMath.BuildBand(inside, size, size, 3f, 0f, 16f, 3f, 3f);

        // Distance of each pixel to the real border: the band reaches at most width/spread (3) plus the shift (3) plus slack.
        var outside = new byte[inside.Length];
        for (var i = 0; i < outside.Length; i++) outside[i] = inside[i] == 0 ? (byte)1 : (byte)0;
        var depth = DistanceToBorder(inside, outside, size);
        var farthestFree = 0f; var farthestLimited = 0f; var covered = 0;
        for (var i = 0; i < inside.Length; i++)
        {
            if (free[i] != 0) farthestFree = System.Math.Max(farthestFree, depth[i]);
            if (limited[i] != 0) { farthestLimited = System.Math.Max(farthestLimited, depth[i]); covered++; }
        }
        Assert.True(covered > 100);
        Assert.True(farthestLimited <= 3f + 3f + 8f, "limited reaches " + farthestLimited);   // shift + width/spread, plus the faint tail of the edge blur
        Assert.True(farthestFree > farthestLimited, "unlimited " + farthestFree + " vs limited " + farthestLimited);
    }

    // Euclidean distance of every pixel to the nearest pixel of the other side of the region's border.
    private static float[] DistanceToBorder(byte[] inside, byte[] outside, int size)
    {
        var result = new float[inside.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var best = float.MaxValue;
            var mine = inside[y * size + x];
            for (var yy = System.Math.Max(0, y - 14); yy < System.Math.Min(size, y + 15); yy++)
            for (var xx = System.Math.Max(0, x - 14); xx < System.Math.Min(size, x + 15); xx++)
                if (inside[yy * size + xx] != mine)
                    best = System.Math.Min(best, (float)System.Math.Sqrt((x - xx) * (x - xx) + (y - yy) * (y - yy)));
            result[y * size + x] = best == float.MaxValue ? 99f : best;
        }
        return result;
    }

    [Fact]
    public void BuildBand_ZeroSmoothingIsTheSameAsBefore()
    {
        const int size = 40;
        var inside = StairRegion(size);
        Assert.Equal(SC2PairOutlineMath.BuildBand(inside, size, size, 6f, 0.5f), SC2PairOutlineMath.BuildBand(inside, size, size, 6f, 0.5f, 0f));
    }

    [Fact]
    public void BuildBand_SpreadContinuesTheFadeOntoTheNeighboursAndNothingBeyondIt()
    {
        var inside = SC2PairOutlineMath.RasterizePairCells(TwoPairGrid(), 4, 4, 1, 8, 8); // pixels x < 4

        var inwardOnly = SC2PairOutlineMath.BuildBand(inside, 8, 8, 2f, 1f);
        var spread = SC2PairOutlineMath.BuildBand(inside, 8, 8, 2f, 1f, 0f, 3f);

        var row = 3 * 8;
        for (var x = 4; x < 8; x++) Assert.Equal(0, inwardOnly[row + x]);              // without spread nothing lands outside
        Assert.True(spread[row + 4] > spread[row + 5], "strongest right next to the border");
        Assert.True(spread[row + 5] > spread[row + 6]);
        Assert.Equal(0, spread[row + 7]);                                               // 3 pixels out is past the spread
        for (var x = 0; x < 4; x++) Assert.Equal(inwardOnly[row + x], spread[row + x]); // the inside band is unchanged
    }

    [Fact]
    public void BorderCells_AreTheCellsNextToADifferentPair()
    {
        // TwoPairGrid: columns 0..1 are pair 1, columns 2..3 pair 2; the cells beside the border are columns 1 and 2.
        var cells = SC2PairOutlineMath.BorderCells(TwoPairGrid(), 4, 4);

        var expected = new[] { 1, 2, 5, 6, 9, 10, 13, 14 };
        Assert.Equal(expected, cells.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void BorderCells_UnassignedCellsCountAsAnotherPairAndTheMapEdgeIsNoBorder()
    {
        var triangles = new short[2 * 1 * 2];
        triangles[0] = triangles[1] = 0;
        triangles[2] = triangles[3] = -1;
        Assert.Equal(new[] { 0, 1 }, SC2PairOutlineMath.BorderCells(triangles, 2, 1).OrderBy(i => i).ToArray());

        var single = new short[3 * 3 * 2];
        Assert.Empty(SC2PairOutlineMath.BorderCells(single, 3, 3));
    }

    [Fact]
    public void DilateMask_IncludesEveryPixelWithinTheReach()
    {
        var mask = new byte[9 * 9];
        mask[4 * 9 + 4] = 1;
        var dilated = SC2PairOutlineMath.DilateMask(mask, 9, 9, 2f);

        Assert.Equal(1, dilated[4 * 9 + 4]);
        Assert.Equal(1, dilated[4 * 9 + 6]);   // 2 away
        Assert.Equal(1, dilated[5 * 9 + 5]);   // 1.41 away
        Assert.Equal(0, dilated[4 * 9 + 7]);   // 3 away
        Assert.Equal(0, dilated[0]);
    }

    [Fact]
    public void ClearOutsideZone_DropsTheOutlineWhereThereIsNoLongerABorderAndKeepsTheRest()
    {
        var layer = new byte[4 * 2];
        for (var i = 0; i < 4; i++) { layer[i * 2] = 255; layer[i * 2 + 1] = (byte)(100 + i); }
        byte[] zone = { 1, 0, 1, 0 };

        var cleared = SC2BlendMaskLayerMath.ClearOutsideZone(layer, zone, 4);

        Assert.Equal(new byte[] { 255, 100, 0, 0, 255, 102, 0, 0 }, cleared);
        Assert.Equal(255, layer[2]);   // the input is untouched
        Assert.All(SC2BlendMaskLayerMath.ClearOutsideZone(null, zone, 4), b => Assert.Equal(0, b));
    }

    [Fact]
    public void AnOutlineThatIsNoLongerAlongABorderIsRemovedWhileTheCurrentBorderKeepsItsOutline()
    {
        // Pair 1 first owns columns 0..1 of the 4x4 grid (8x8 mask), outlined along its right border (x 3/4). It then grows
        // over column 2: the old border is gone, the new one is at x 5/6. With the outline kept only near current borders the
        // old outline vanishes (here the reach is 0: just the cells beside the new border).
        var firstGrid = TwoPairGrid();
        var firstRegion = SC2PairOutlineMath.RasterizePairCells(firstGrid, 4, 4, 1, 8, 8);
        var outline = SC2BlendMaskLayerMath.ApplyBand(null, SC2PairOutlineMath.BuildBand(firstRegion, 8, 8, 2f, 0f, 0f, 0f), 1f, 64);
        Assert.Equal(255, outline[(3 * 8 + 3) * 2 + 1]);

        var grownGrid = TwoPairGrid();
        for (var z = 0; z < 4; z++) { grownGrid[(z * 4 + 2) * 2] = 1; grownGrid[(z * 4 + 2) * 2 + 1] = 1; }
        var borderPixels = SC2PairOutlineMath.RasterizeCells(SC2PairOutlineMath.BorderCells(grownGrid, 4, 4), 4, 4, 8, 8);
        var zone = SC2PairOutlineMath.DilateMask(borderPixels, 8, 8, 0f);

        var pruned = SC2BlendMaskLayerMath.ClearOutsideZone(outline, zone, 64);

        Assert.Equal(0, pruned[(3 * 8 + 3) * 2 + 1]);   // the old border, now inside the pair
        Assert.Equal(0, pruned[(3 * 8 + 2) * 2 + 1]);
    }

    [Fact]
    public void StableBorderCells_LeaveOutBordersNextToReassignedCells()
    {
        var before = TwoPairGrid();
        var after = TwoPairGrid();
        for (var z = 0; z < 4; z++) { after[(z * 4 + 2) * 2] = 1; after[(z * 4 + 2) * 2 + 1] = 1; }

        // Column 2 changed, so the borders on both sides of it (columns 1/2 now inside, 2/3 new) are not stable.
        Assert.Empty(SC2PairOutlineMath.StableBorderCells(after, before, 4, 4, 0, 0, 4, 4));
        Assert.NotEmpty(SC2PairOutlineMath.BorderCells(after, 4, 4));

        // Nothing changed: the same cells as BorderCells.
        Assert.Equal(SC2PairOutlineMath.BorderCells(before, 4, 4).OrderBy(i => i).ToArray(),
            SC2PairOutlineMath.StableBorderCells(before, before, 4, 4, 0, 0, 4, 4).OrderBy(i => i).ToArray());
        Assert.Equal(SC2PairOutlineMath.BorderCells(before, 4, 4).OrderBy(i => i).ToArray(),
            SC2PairOutlineMath.StableBorderCells(before, null, 4, 4, 0, 0, 4, 4).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void AMovedBordersOldOutlineIsRemovedEvenWhenTheNewBorderIsWithinReachOfIt()
    {
        var before = TwoPairGrid();
        var firstRegion = SC2PairOutlineMath.RasterizePairCells(before, 4, 4, 1, 8, 8);
        var outline = SC2BlendMaskLayerMath.ApplyBand(null, SC2PairOutlineMath.BuildBand(firstRegion, 8, 8, 2f, 0f, 0f, 0f), 1f, 64);

        var after = TwoPairGrid();
        for (var z = 0; z < 4; z++) { after[(z * 4 + 2) * 2] = 1; after[(z * 4 + 2) * 2 + 1] = 1; }
        var stable = SC2PairOutlineMath.RasterizeCells(SC2PairOutlineMath.StableBorderCells(after, before, 4, 4, 0, 0, 4, 4), 4, 4, 8, 8);
        var zone = SC2PairOutlineMath.DilateMask(stable, 8, 8, 4f);   // reach wider than the move

        var pruned = SC2BlendMaskLayerMath.ClearOutsideZone(outline, zone, 64);

        Assert.Equal(0, pruned[(3 * 8 + 3) * 2 + 1]);
        Assert.Equal(0, pruned[(3 * 8 + 2) * 2 + 1]);
    }

    [Fact]
    public void RectVersions_MatchTheMatchingPartOfTheWholeMaskVersions()
    {
        var grid = TwoPairGrid();
        var whole = SC2PairOutlineMath.RasterizePairCells(grid, 4, 4, 1, 16, 16);
        var rect = new SC2PairOutlineMath.MaskRect(5, 3, 9, 7);
        var part = SC2PairOutlineMath.RasterizePairCells(grid, 4, 4, 1, 16, 16, rect);

        for (var y = 0; y < rect.Height; y++)
            for (var x = 0; x < rect.Width; x++)
                Assert.Equal(whole[(rect.Y + y) * 16 + rect.X + x], part[y * rect.Width + x]);

        var wholeCells = SC2PairOutlineMath.RasterizeCells(new[] { 5, 6, 9 }, 4, 4, 16, 16);
        var partCells = SC2PairOutlineMath.RasterizeCells(new[] { 5, 6, 9 }, 4, 4, 16, 16, rect);
        for (var y = 0; y < rect.Height; y++)
            for (var x = 0; x < rect.Width; x++)
                Assert.Equal(wholeCells[(rect.Y + y) * 16 + rect.X + x], partCells[y * rect.Width + x]);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => SC2PairOutlineMath.RasterizePairCells(grid, 4, 4, 1, 16, 16, new SC2PairOutlineMath.MaskRect(10, 10, 9, 9)));
    }

    [Fact]
    public void MaskRectOfCells_CoversTheCellsRoundedOutwardAndClamped()
    {
        var rect = SC2PairOutlineMath.MaskRectOfCells(1, 1, 3, 2, 4, 4, 10, 10);
        Assert.Equal(2, rect.X);      // floor(1 * 10 / 4)
        Assert.Equal(8, rect.Right);  // ceil(3 * 10 / 4)
        Assert.Equal(2, rect.Y);
        Assert.Equal(5, rect.Bottom); // ceil(2 * 10 / 4)

        var clamped = SC2PairOutlineMath.MaskRectOfCells(-3, -3, 9, 9, 4, 4, 10, 10);
        Assert.Equal(0, clamped.X);
        Assert.Equal(10, clamped.Width);
        Assert.Equal(10, clamped.Height);
    }

    [Fact]
    public void BorderCells_InACellRangeAreTheSameAsTheWholeMapsOnesInThatRange()
    {
        var grid = TwoPairGrid();
        var all = SC2PairOutlineMath.BorderCells(grid, 4, 4);
        var inRange = SC2PairOutlineMath.BorderCells(grid, 4, 4, 1, 1, 3, 3);
        Assert.Equal(new[] { 1 * 4 + 1, 1 * 4 + 2, 2 * 4 + 1, 2 * 4 + 2 }, inRange.OrderBy(i => i).ToArray());
        foreach (var cell in inRange) Assert.Contains(cell, all);
    }

    [Fact]
    public void InPlaceEditsOnARectangleGiveTheSameLayerAsTheWholeMaskVersions()
    {
        const int size = 8;
        var layer = new byte[size * size * 2];
        for (var i = 0; i < size * size; i++) { layer[i * 2] = (byte)(i * 3); layer[i * 2 + 1] = (byte)(i * 5); }
        var rect = new SC2PairOutlineMath.MaskRect(2, 3, 4, 3);
        var band = new byte[rect.Width * rect.Height];
        for (var i = 0; i < band.Length; i++) band[i] = (byte)(i * 20);

        // The same band as a whole-mask band that is zero outside the rectangle.
        var wholeBand = new byte[size * size];
        for (var y = 0; y < rect.Height; y++)
            for (var x = 0; x < rect.Width; x++) wholeBand[(rect.Y + y) * size + rect.X + x] = band[y * rect.Width + x];
        var expected = SC2BlendMaskLayerMath.ApplyBand(layer, wholeBand, 1f, size * size);

        var copy = (byte[])layer.Clone();
        var changed = SC2BlendMaskLayerMath.ApplyBandInPlace(copy, size, rect, band, 1f);

        Assert.True(changed);
        Assert.Equal(expected, copy);
        Assert.False(SC2BlendMaskLayerMath.ApplyBandInPlace(copy, size, rect, band, 1f)); // idempotent: the second time changes nothing

        // Clearing outside a zone, only inside the apply rectangle.
        var zone = new byte[rect.Width * rect.Height];   // all 0: nothing is a border any more
        var applyRect = new SC2PairOutlineMath.MaskRect(3, 3, 2, 2);
        var cleared = SC2BlendMaskLayerMath.ClearOutsideZoneInPlace(copy, size, rect, zone, applyRect);
        Assert.True(cleared);
        Assert.Equal(0, copy[(3 * size + 3) * 2 + 1]);                 // inside the apply rectangle: cleared
        Assert.NotEqual(0, copy[(5 * size + 5) * 2 + 1]);              // outside the rectangle: untouched
        Assert.False(SC2BlendMaskLayerMath.ClearOutsideZoneInPlace(copy, size, rect, zone, applyRect));
    }
}
