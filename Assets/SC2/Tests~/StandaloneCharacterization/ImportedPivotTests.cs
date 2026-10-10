using System.Collections.Generic;
using UnityEngine;
using Xunit;

public class ImportedPivotTests
{
    [Fact]
    public void A_file_with_no_origin_keeps_the_world_origin() =>
        Assert.Equal(Vector3.zero, SC2ImportedPivot.FromLayerCenters(new List<float[]> { new float[3], new float[3] }));

    [Fact]
    public void The_first_layer_with_an_origin_gives_it()
    {
        var layers = new List<float[]> { new float[3], new[] { 300f, 12.5f, 200f }, new[] { 1f, 2f, 3f } };
        Assert.Equal(new Vector3(300f, 12.5f, 200f), SC2ImportedPivot.FromLayerCenters(layers));
    }

    [Fact]
    public void No_layers_have_no_origin() =>
        Assert.Equal(Vector3.zero, SC2ImportedPivot.FromLayerCenters(new List<float[]>()));

    [Fact]
    public void An_old_placement_keeps_every_vertex_where_it_was()
    {
        // World = position + rotation * (scale * vertex) before; position' + rotation * (scale * (vertex - pivot)) after.
        var position = new Vector3(5f, 1f, -3f);
        var rotation = new Quaternion(0f, 0.70710678f, 0f, 0.70710678f); // a quarter turn about Y
        var scale = new Vector3(2f, 1f, 3f);
        var pivot = new Vector3(300f, 10f, 200f);
        var vertex = new Vector3(310f, 12f, 190f);

        var before = position + rotation * Vector3.Scale(scale, vertex);
        var converted = SC2ImportedPivot.ConvertLegacyPosition(position, rotation, scale, pivot);
        var after = converted + rotation * Vector3.Scale(scale, vertex - pivot);

        Assert.Equal(before.x, after.x, 3);
        Assert.Equal(before.y, after.y, 3);
        Assert.Equal(before.z, after.z, 3);
    }
}
