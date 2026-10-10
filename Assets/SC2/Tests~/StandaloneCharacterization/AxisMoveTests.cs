using UnityEngine;
using Xunit;

public class AxisMoveTests
{
    [Fact]
    public void A_ray_straight_down_reads_where_it_crosses_the_x_axis()
    {
        Assert.True(SC2AxisMove.TryParam(Vector3.zero, new Vector3(1, 0, 0), new Vector3(5, 10, 0), new Vector3(0, -1, 0), out var distance));
        Assert.Equal(5f, distance, 3);
    }

    [Fact]
    public void The_distance_is_measured_from_the_axis_origin()
    {
        Assert.True(SC2AxisMove.TryParam(new Vector3(2, 0, 0), new Vector3(1, 0, 0), new Vector3(5, 10, 0), new Vector3(0, -1, 0), out var distance));
        Assert.Equal(3f, distance, 3);
    }

    [Fact]
    public void An_oblique_ray_reads_the_closest_point_on_the_vertical_axis()
    {
        // The camera at (0, 0, -10) looks at (0, 4, 0)
        var direction = new Vector3(0, 4, 10).normalized;
        Assert.True(SC2AxisMove.TryParam(Vector3.zero, new Vector3(0, 1, 0), new Vector3(0, 0, -10), direction, out var distance));
        Assert.Equal(4f, distance, 3);
    }

    [Fact]
    public void A_ray_parallel_to_the_axis_cannot_say_how_far_along_it_is() =>
        Assert.False(SC2AxisMove.TryParam(Vector3.zero, new Vector3(0, 1, 0), new Vector3(1, 10, 0), new Vector3(0, -1, 0), out _));

    [Fact]
    public void Pressing_an_axis_limits_the_move_and_pressing_it_again_frees_it()
    {
        var x = SC2AxisMove.Press(SC2MoveAxis.None, SC2MoveAxis.X);
        Assert.Equal(SC2MoveAxis.X, x);
        Assert.Equal(SC2MoveAxis.Z, SC2AxisMove.Press(x, SC2MoveAxis.Z));
        Assert.Equal(SC2MoveAxis.None, SC2AxisMove.Press(x, SC2MoveAxis.X));
    }
}
