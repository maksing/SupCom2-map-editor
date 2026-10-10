using Xunit;

public class SymmetrySlotYawTests
{
    private static float Wrap(float degrees)
    {
        var d = degrees % 360f;
        if (d > 180f) d -= 360f;
        if (d <= -180f) d += 360f;
        return d;
    }

    [Fact]
    public void Slot_zero_keeps_its_own_yaw() =>
        Assert.Equal(40f, SC2SymmetryMath.GetSlotYaw(SymmetryMode.X, 2, 0, 40f), 3);

    [Theory]
    [InlineData(30f, -30f)]
    [InlineData(100f, -100f)]
    public void An_X_mirror_negates_the_yaw(float yaw, float expected) =>
        Assert.Equal(expected, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.X, 2, 1, yaw)), 3);

    [Theory]
    [InlineData(30f, 150f)]   // facing +Z-ish becomes facing -Z-ish
    [InlineData(0f, 180f)]
    public void A_Y_mirror_turns_the_yaw_about_the_X_axis(float yaw, float expected) =>
        Assert.Equal(expected, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.Y, 2, 1, yaw)), 3);

    [Theory]
    [InlineData(0f, 90f)]      // facing +Z across the diagonal faces +X
    [InlineData(90f, 0f)]
    [InlineData(30f, 60f)]
    public void The_TLBR_diagonal_swaps_the_axes(float yaw, float expected) =>
        Assert.Equal(expected, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.DiagonalTLBR, 2, 1, yaw)), 3);

    [Theory]
    [InlineData(0f, -90f)]
    [InlineData(90f, 180f)]
    public void The_TRBL_diagonal_swaps_and_flips_the_axes(float yaw, float expected) =>
        Assert.Equal(expected, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.DiagonalTRBL, 2, 1, yaw)), 3);

    [Fact]
    public void Both_diagonals_give_two_mirrors_and_a_half_turn()
    {
        Assert.Equal(90f, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.DiagonalBoth, 2, 1, 0f)), 3);
        Assert.Equal(-90f, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.DiagonalBoth, 2, 2, 0f)), 3);
        Assert.Equal(180f, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.DiagonalBoth, 2, 3, 0f)), 3);
    }

    [Fact]
    public void A_radial_copy_turns_the_other_way_round_to_its_position()
    {
        // 4-way: the copy in slot 1 sits 90 degrees on from slot 0 (its position turns from +X towards +Z), and faces turn with it.
        Assert.Equal(-90f, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.Radial, 4, 1, 0f)), 3);
        Assert.Equal(180f, Wrap(SC2SymmetryMath.GetSlotYaw(SymmetryMode.Radial, 4, 2, 0f)), 3);
    }
}
