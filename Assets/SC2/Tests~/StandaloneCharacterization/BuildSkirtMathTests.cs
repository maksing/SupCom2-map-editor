using System;
using SC2.MapEditor.Document;
using Xunit;

public class BuildSkirtMathTests
{
    private const int Size = 24;

    private static ushort Raw(float height) => (ushort)Math.Round(height * SC2BuildSkirtMath.HeightScale);

    private static ushort[] Flat(float height)
    {
        var heights = new ushort[Size * Size];
        Array.Fill(heights, Raw(height));
        return heights;
    }

    private static void Set(ushort[] heights, int x, int z, float height) => heights[z * Size + x] = Raw(height);

    [Fact]
    public void The_skirt_is_the_footprint_centred_on_the_position_and_the_upper_end_is_included()
    {
        var even = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f);
        Assert.Equal((8, 12, 8, 12), (even.X0, even.X1, even.Z0, even.Z1));

        var odd = SC2BuildSkirtMath.SkirtAround(10f, 10f, 5f, 3f);
        Assert.Equal((7, 12, 8, 11), (odd.X0, odd.X1, odd.Z0, odd.Z1));
    }

    [Fact]
    public void A_flat_map_can_be_built_on_in_both_modes()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f);
        Assert.True(SC2BuildSkirtMath.CanBuild(Flat(7f), Size, Size, rect, true, 1f));
        Assert.True(SC2BuildSkirtMath.CanBuild(Flat(7f), Size, Size, rect, false, 1f));
    }

    [Fact]
    public void A_skirt_that_needs_a_vertex_outside_the_grid_cannot_be_built_on()
    {
        var heights = Flat(7f);
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(1f, 10f, 4f, 4f), true, 1f));
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(1f, 10f, 4f, 4f), false, 1f));
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(22f, 10f, 4f, 4f), true, 1f));
    }

    [Fact]
    public void Levelled_mode_only_looks_at_the_border_of_the_skirt()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f); // 8..12
        var heights = Flat(7f);
        Set(heights, 10, 10, 30f); // inside the skirt: levelled away by the game
        Set(heights, 9, 9, 30f);
        Set(heights, 7, 10, 30f);  // one cell outside the skirt: not looked at
        Set(heights, 13, 10, 30f);
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1f));

        Set(heights, 8, 10, 9f);   // on the border
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1f));
    }

    [Fact]
    public void Levelled_mode_needs_only_the_skirt_itself_to_be_inside_the_grid()
    {
        var heights = Flat(7f);
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(2f, 10f, 4f, 4f), true, 1f));   // x0 = 0
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(1f, 10f, 4f, 4f), true, 1f));  // x0 = -1
    }

    [Fact]
    public void Levelled_mode_compares_the_ring_with_the_last_sample_rounded_up()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f); // border x 8..12, z 8..12, last sample (12, 12)
        var heights = Flat(10f);

        // The last sample is a little above 10, so the pivot is 11: a border vertex that is only 0.1 below the others is
        // now 1.1 under the pivot.
        heights[12 * Size + 12] = (ushort)(10 * 128 + 1);
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1f));
        heights[8 * Size + 9] = (ushort)(10 * 128 - 13);
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1f));
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1.2f));
    }

    [Fact]
    public void Levelled_mode_accepts_a_deviation_equal_to_the_tolerance()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f);
        var heights = Flat(10f);
        Set(heights, 9, 8, 11f); // exactly 1.0 above the pivot of 10
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1f));
        Set(heights, 9, 8, 11f + 1f / 128f);
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, true, 1f));
    }

    [Fact]
    public void Area_mode_limits_the_whole_spread_of_the_skirt()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f);
        var heights = Flat(7f);
        Set(heights, 10, 10, 8f);
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, false, 1f)); // spread 1.0 = tolerance
        Set(heights, 12, 12, 6.5f);
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, false, 1f)); // spread 1.5
    }

    [Fact]
    public void Area_mode_includes_the_upper_ends_and_ignores_the_ring_outside()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f); // 8..12
        var heights = Flat(7f);
        Set(heights, 7, 10, 20f);  // just outside
        Set(heights, 13, 10, 20f);
        Assert.True(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, false, 1f));
        Set(heights, 12, 12, 20f); // the far corner, inside
        Assert.False(SC2BuildSkirtMath.CanBuild(heights, Size, Size, rect, false, 1f));
    }

    [Theory]
    [InlineData(true, 4f, 4f, 1f)]
    [InlineData(true, 5f, 3f, 1f)]
    [InlineData(false, 4f, 4f, 1f)]
    [InlineData(false, 5f, 3f, 1f)]
    [InlineData(false, 1f, 1f, 0.5f)]
    public void The_whole_map_mask_agrees_with_checking_each_vertex(bool flatten, float sizeX, float sizeZ, float tolerance)
    {
        var random = new Random(1234);
        var heights = new ushort[Size * Size];
        for (var i = 0; i < heights.Length; i++) heights[i] = (ushort)(1000 + random.Next(0, 160));

        var mask = SC2BuildSkirtMath.BuildMask(heights, Size, Size, sizeX, sizeZ, flatten, tolerance);

        var buildable = 0;
        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                var expected = SC2BuildSkirtMath.CanBuild(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(x, z, sizeX, sizeZ), flatten, tolerance);
                Assert.Equal(expected ? (byte)1 : (byte)0, mask[z * Size + x]);
                if (expected) buildable++;
            }
        }
        Assert.InRange(buildable, 1, Size * Size - 1); // both answers occur, so the comparison above means something
    }

    [Fact]
    public void The_buildable_flag_of_a_reference_unit_survives_the_document_round_trip()
    {
        var doc = new SC2MapDocument();
        doc.Objects.ReferenceUnits.Add(new SC2ObjReferenceUnit { Id = 1, BlueprintId = "ueb0101", ShowBuildable = true });
        doc.Objects.ReferenceUnits.Add(new SC2ObjReferenceUnit { Id = 2, BlueprintId = "ueb0102" });

        var back = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));

        Assert.True(back.Objects.ReferenceUnits[0].ShowBuildable);
        Assert.False(back.Objects.ReferenceUnits[1].ShowBuildable);
    }

    [Fact]
    public void The_report_gives_the_numbers_the_levelled_check_was_decided_by()
    {
        var rect = SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f);
        var heights = Flat(10f);
        Set(heights, 9, 8, 12.5f);   // on the border
        Set(heights, 12, 12, 10.5f); // the last sample: pivot is 11

        var report = SC2BuildSkirtMath.Evaluate(heights, Size, Size, rect, true, 1f);

        Assert.True(report.Levelled);
        Assert.True(report.InsideGrid);
        Assert.Equal(10f, report.MinHeight);
        Assert.Equal(12.5f, report.MaxHeight);
        Assert.Equal(11f, report.Pivot);
        Assert.Equal(1.5f, report.Deviation);
        Assert.False(report.Buildable);
    }

    [Fact]
    public void The_report_gives_the_spread_for_the_plain_check_and_flags_a_skirt_outside_the_grid()
    {
        var heights = Flat(7f);
        Set(heights, 10, 10, 8.25f);
        var report = SC2BuildSkirtMath.Evaluate(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(10f, 10f, 4f, 4f), false, 1f);
        Assert.False(report.Levelled);
        Assert.Equal(1.25f, report.Deviation);
        Assert.False(report.Buildable);

        var outside = SC2BuildSkirtMath.Evaluate(heights, Size, Size, SC2BuildSkirtMath.SkirtAround(1f, 10f, 4f, 4f), true, 1f);
        Assert.False(outside.InsideGrid);
        Assert.False(outside.Buildable);
    }

    [Fact]
    public void Footprints_that_share_area_overlap_and_ones_that_only_touch_do_not()
    {
        var other = new SC2PlacedFootprint(10f, 10f, 4f, 4f, 0f); // 8..12
        Assert.True(SC2BuildSkirtMath.Overlaps(13f, 10f, 4f, 4f, other));  // 11..15 shares 11..12
        Assert.False(SC2BuildSkirtMath.Overlaps(14f, 10f, 4f, 4f, other)); // 12..16 touches the edge at 12
        Assert.False(SC2BuildSkirtMath.Overlaps(20f, 10f, 4f, 4f, other));
        Assert.True(SC2BuildSkirtMath.Overlaps(10f, 10f, 1f, 1f, other));  // inside it
    }

    [Fact]
    public void A_turned_footprint_is_a_turned_rectangle_not_its_bounding_box()
    {
        // 6 x 2, turned a quarter: it is 2 wide along X and 6 long along Z.
        var turned = new SC2PlacedFootprint(10f, 10f, 6f, 2f, 90f);
        Assert.True(SC2BuildSkirtMath.Overlaps(10f, 12.5f, 2f, 2f, turned));
        Assert.False(SC2BuildSkirtMath.Overlaps(12.5f, 10f, 2f, 2f, turned));

        // Turned by 45 degrees a 6 x 2 bar leaves the corner of its bounding box empty.
        var diagonal = new SC2PlacedFootprint(10f, 10f, 6f, 2f, 45f);
        Assert.False(SC2BuildSkirtMath.Overlaps(12.5f, 12.5f, 1f, 1f, diagonal)); // in the box corner, away from the bar
        Assert.True(SC2BuildSkirtMath.Overlaps(10f, 10f, 1f, 1f, diagonal));
    }

    [Fact]
    public void The_turn_follows_Unity_yaw_so_a_quarter_turn_sends_X_towards_minus_Z()
    {
        // A bar long along its own X, turned +45: its +X end points towards (+x, -z).
        var bar = new SC2PlacedFootprint(10f, 10f, 8f, 1f, 45f);
        Assert.True(SC2BuildSkirtMath.Overlaps(12.5f, 7.5f, 1f, 1f, bar));
        Assert.False(SC2BuildSkirtMath.Overlaps(12.5f, 12.5f, 1f, 1f, bar));
    }

    [Fact]
    public void Blocking_clears_exactly_the_vertices_that_would_overlap()
    {
        var mask = new byte[Size * Size];
        Array.Fill(mask, (byte)1);
        var others = new[] { new SC2PlacedFootprint(10f, 10f, 4f, 4f, 0f) };

        var blocked = SC2BuildSkirtMath.BlockOverlapping(mask, Size, Size, 4f, 4f, others);

        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                var overlaps = Math.Abs(x - 10) < 4 && Math.Abs(z - 10) < 4;
                Assert.Equal(overlaps ? (byte)0 : (byte)1, blocked[z * Size + x]);
            }
        }
        Assert.All(mask, value => Assert.Equal((byte)1, value)); // the mask passed in is untouched
    }

    [Fact]
    public void Blocking_a_turned_footprint_agrees_with_checking_each_vertex()
    {
        var mask = new byte[Size * Size];
        Array.Fill(mask, (byte)1);
        var others = new[] { new SC2PlacedFootprint(11.3f, 9.6f, 7f, 3f, 33f), new SC2PlacedFootprint(4f, 18f, 5f, 5f, 0f) };

        var blocked = SC2BuildSkirtMath.BlockOverlapping(mask, Size, Size, 4f, 2f, others);

        for (var z = 0; z < Size; z++)
            for (var x = 0; x < Size; x++)
                Assert.Equal(SC2BuildSkirtMath.FindOverlap(x, z, 4f, 2f, others) >= 0 ? (byte)0 : (byte)1, blocked[z * Size + x]);
    }

    [Theory]
    [InlineData(4f, 8, 11)]  // centred on 10: cells 8..11
    [InlineData(5f, 7, 12)]  // 7.5 .. 12.5 touches cells 7..12
    [InlineData(1f, 9, 10)]   // 9.5 .. 10.5 touches cells 9 and 10
    public void One_valid_placement_covers_the_cells_of_its_footprint(float size, int firstCell, int lastCell)
    {
        var centres = new byte[Size * Size];
        centres[10 * Size + 10] = 1;

        var covered = SC2BuildSkirtMath.CoverageMask(centres, Size, Size, size, size);

        for (var z = 0; z < Size; z++)
            for (var x = 0; x < Size; x++)
                Assert.Equal(x >= firstCell && x <= lastCell && z >= firstCell && z <= lastCell ? (byte)1 : (byte)0, covered[z * Size + x]);
    }

    [Fact]
    public void Next_to_another_building_exactly_its_footprint_stays_uncovered()
    {
        var all = new byte[Size * Size];
        Array.Fill(all, (byte)1);
        var others = new[] { new SC2PlacedFootprint(12f, 12f, 6f, 6f, 0f) }; // cells 9..14

        var centres = SC2BuildSkirtMath.BlockOverlapping(all, Size, Size, 4f, 4f, others);
        var covered = SC2BuildSkirtMath.CoverageMask(centres, Size, Size, 4f, 4f);

        for (var z = 0; z < Size - 1; z++)
            for (var x = 0; x < Size - 1; x++)
                Assert.Equal(x >= 9 && x <= 14 && z >= 9 && z <= 14 ? (byte)0 : (byte)1, covered[z * Size + x]);
    }

    [Fact]
    public void The_coverage_mask_agrees_with_covering_by_brute_force()
    {
        var random = new Random(77);
        var centres = new byte[Size * Size];
        for (var i = 0; i < centres.Length; i++) centres[i] = (byte)(random.Next(0, 12) == 0 ? 1 : 0);

        var covered = SC2BuildSkirtMath.CoverageMask(centres, Size, Size, 5f, 3f);

        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                var expected = false;
                for (var cz = 0; cz < Size && !expected; cz++)
                    for (var cx = 0; cx < Size && !expected; cx++)
                        if (centres[cz * Size + cx] != 0
                            && x + 1 > cx - 2.5f && x < cx + 2.5f && z + 1 > cz - 1.5f && z < cz + 1.5f)
                            expected = true;
                Assert.Equal(expected ? (byte)1 : (byte)0, covered[z * Size + x]);
            }
        }
    }

    [Theory]
    // hasCaps, land, seabed, water, mapHasWaterBlocks, expected
    [InlineData(false, false, false, false, true, SC2BuildCostsRule.None)]
    [InlineData(true, true, false, false, true, SC2BuildCostsRule.LandMedium)]
    [InlineData(true, true, false, false, false, SC2BuildCostsRule.LandMedium)]
    [InlineData(true, true, true, false, true, SC2BuildCostsRule.LandMediumWaterSurface)]
    [InlineData(true, true, true, false, false, SC2BuildCostsRule.LandMedium)]
    [InlineData(true, false, false, true, true, SC2BuildCostsRule.WaterSurfaceOnly)]
    [InlineData(true, false, false, true, false, SC2BuildCostsRule.Impossible)]
    [InlineData(true, true, false, true, true, SC2BuildCostsRule.WaterSurfaceOnly)]
    [InlineData(true, false, false, false, true, SC2BuildCostsRule.None)]
    public void The_costs_block_follows_the_layers_a_building_can_be_built_on(bool hasCaps, bool land, bool seabed, bool water, bool mapHasWaterBlocks, SC2BuildCostsRule expected)
    {
        Assert.Equal(expected, SC2BuildSkirtMath.SelectCostsRule(hasCaps, land, seabed, water, mapHasWaterBlocks));
    }

    [Fact]
    public void Water_cells_are_those_whose_corners_are_on_average_under_the_surface()
    {
        var heights = Flat(5f);
        Set(heights, 3, 3, 9f); // one high corner among four: mean (5 + 5 + 5 + 9) / 4 = 6
        var cells = SC2BuildSkirtMath.WaterCells(heights, Size, Size, 5.5f);
        var width = Size - 1;
        Assert.Equal((byte)1, cells[0]);
        Assert.Equal((byte)0, cells[3 * width + 3]); // the cell above and right of (3, 3): its corner (3, 3) is high
        Assert.Equal((byte)0, cells[2 * width + 2]); // the cell whose corner (3, 3) is the high one
        Assert.Equal((byte)1, cells[10 * width + 10]);
        Assert.All(SC2BuildSkirtMath.WaterCells(heights, Size, Size, 4f), cell => Assert.Equal((byte)0, cell));
    }

    [Fact]
    public void A_centre_is_cleared_when_its_footprint_covers_a_cell_that_is_not_allowed()
    {
        var width = Size - 1;
        var allowed = new byte[width * width];
        Array.Fill(allowed, (byte)1);
        allowed[10 * width + 10] = 0; // one forbidden cell

        var centres = new byte[Size * Size];
        Array.Fill(centres, (byte)1);
        var restricted = SC2BuildSkirtMath.RestrictToAllowedCells(centres, Size, Size, 4f, 4f, allowed);

        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                // A 4 x 4 footprint on vertex c covers cells c - 2 .. c + 1.
                var covers = x - 2 <= 10 && 10 <= x + 1 && z - 2 <= 10 && 10 <= z + 1;
                var inside = x - 2 >= 0 && x + 1 < width && z - 2 >= 0 && z + 1 < width;
                Assert.Equal(inside && !covers ? (byte)1 : (byte)0, restricted[z * Size + x]);
            }
        }
        Assert.All(centres, value => Assert.Equal((byte)1, value)); // the mask passed in is untouched
    }

    [Fact]
    public void The_footprint_check_for_one_position_agrees_with_the_mask()
    {
        var width = Size - 1;
        var random = new Random(5);
        var allowed = new byte[width * width];
        for (var i = 0; i < allowed.Length; i++) allowed[i] = (byte)(random.Next(0, 9) == 0 ? 0 : 1);
        var centres = new byte[Size * Size];
        Array.Fill(centres, (byte)1);

        var restricted = SC2BuildSkirtMath.RestrictToAllowedCells(centres, Size, Size, 3f, 5f, allowed);

        for (var z = 0; z < Size; z++)
            for (var x = 0; x < Size; x++)
                Assert.Equal(SC2BuildSkirtMath.FootprintCellsAllowed(allowed, width, width, x, z, 3f, 5f) ? (byte)1 : (byte)0, restricted[z * Size + x]);
    }
}
