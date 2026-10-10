using System.Collections.Generic;
using Xunit;

public class PropFacingTests
{
    // The corners of a 4 x 4 square, centred on the origin.
    private static List<float> Square() => new List<float> { -2, 0, -2, 2, 0, -2, 2, 0, 2, -2, 0, 2 };

    [Fact]
    public void A_centred_model_has_no_facing() =>
        Assert.Equal(0f, SC2PropFacing.FromPositions(Square()), 3);

    [Fact]
    public void No_vertices_have_no_facing() =>
        Assert.Equal(0f, SC2PropFacing.FromPositions(new List<float>()), 3);

    [Fact]
    public void A_ramp_on_the_plus_X_side_makes_the_model_face_plus_X()
    {
        var model = Square();
        model.AddRange(new float[] { 4, 0, -1, 4, 0, 1, 5, 0, 0 }); // a stub out to +X
        Assert.Equal(90f, SC2PropFacing.FromPositions(model), 0);
    }

    [Fact]
    public void A_ramp_on_the_minus_Z_side_makes_the_model_face_minus_Z()
    {
        var model = Square();
        model.AddRange(new float[] { -1, 0, -4, 1, 0, -4, 0, 0, -5 });
        Assert.Equal(180f, System.Math.Abs(SC2PropFacing.FromPositions(model)), 0);
    }

    [Fact]
    public void The_facing_is_what_a_mirror_needs_to_carry_the_side_across()
    {
        // A ramp pointing +X on the W building of a double diagonal: the building north of it, mirrored across the x = z diagonal,
        // has its ramp pointing +Z, whatever yaw the model itself has. yaw + facing is the world direction of the ramp.
        var facing = SC2PropFacing.FromPositions(new List<float> { -2, 0, -2, 2, 0, -2, 2, 0, 2, -2, 0, 2, 4, 0, -1, 4, 0, 1, 5, 0, 0 });
        var yaw = 0f;
        var mirrored = SC2SymmetryMath.GetSlotYaw(SymmetryMode.DiagonalBoth, 2, 1, yaw + facing) - facing;
        var rampDirection = mirrored + facing;
        var wrapped = (rampDirection % 360f + 360f) % 360f;
        Assert.Equal(0f, wrapped > 180f ? wrapped - 360f : wrapped, 0); // +Z
    }
}

public class PropFacingBodyTests
{
    // A ring of 24 vertices of radius 5 (a round building) with a ramp stub reaching out to 8 on the given side and some extra
    // vertices of the body that lean the same way the ramp does (a plain average of the vertices would be pulled towards them).
    private static List<float> BuildingWithRamp(float rampYawDegrees)
    {
        var positions = new List<float>();
        for (var i = 0; i < 24; i++)
        {
            var a = i * 15f * System.MathF.PI / 180f;
            positions.AddRange(new[] { 5f * System.MathF.Sin(a), 0f, 5f * System.MathF.Cos(a) });
        }
        // Dense detail inside the body, on one side only (stairs, a wall): it pulls the plain average off the ramp's direction.
        var lean = (rampYawDegrees + 40f) * System.MathF.PI / 180f;
        for (var i = 0; i < 60; i++)
            positions.AddRange(new[] { 2.5f * System.MathF.Sin(lean) + 0.02f * i, 0f, 2.5f * System.MathF.Cos(lean) });
        var ramp = rampYawDegrees * System.MathF.PI / 180f;
        for (var k = 0; k < 3; k++)
        {
            var side = (k - 1) * 0.6f;
            positions.AddRange(new[] { 8f * System.MathF.Sin(ramp) + side * System.MathF.Cos(ramp), 0f, 8f * System.MathF.Cos(ramp) - side * System.MathF.Sin(ramp) });
        }
        return positions;
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(-135f)]
    [InlineData(30f)]
    public void The_side_that_sticks_out_decides_the_facing(float rampYaw)
    {
        var facing = SC2PropFacing.FromPositions(BuildingWithRamp(rampYaw));
        var difference = ((facing - rampYaw) % 360f + 540f) % 360f - 180f;
        Assert.InRange(System.Math.Abs(difference), 0f, 12f);
    }

    [Fact]
    public void A_round_body_without_a_ramp_has_no_facing()
    {
        var positions = new List<float>();
        for (var i = 0; i < 24; i++)
        {
            var a = i * 15f * System.MathF.PI / 180f;
            positions.AddRange(new[] { 5f * System.MathF.Sin(a), 0f, 5f * System.MathF.Cos(a) });
        }
        Assert.Equal(0f, SC2PropFacing.FromPositions(positions), 3);
    }
}
