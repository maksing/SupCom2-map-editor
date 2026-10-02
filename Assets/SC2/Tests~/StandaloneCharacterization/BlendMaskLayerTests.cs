using System.Linq;

public sealed class BlendMaskLayerTests
{
    private const int Pixels = 4;

    private static SC2BlendMaskLayer Layer(string name, params (byte value, byte coverage)[] pixels)
    {
        var data = new byte[Pixels * 2];
        for (var i = 0; i < pixels.Length; i++) { data[i * 2] = pixels[i].value; data[i * 2 + 1] = pixels[i].coverage; }
        return new SC2BlendMaskLayer { Name = name, Data = data };
    }

    private static byte[] Flatten(params SC2BlendMaskLayer[] layers)
    {
        var alpha = new byte[Pixels];
        SC2BlendMaskLayerMath.Composite(layers, Pixels, alpha);
        return alpha;
    }

    [Fact]
    public void Composite_BaseFromOldMask_ReproducesTheMaskExactly()
    {
        var rgba = new byte[Pixels * 4];
        byte[] alphas = { 0, 17, 128, 255 };
        for (var i = 0; i < Pixels; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 128; rgba[i * 4 + 3] = alphas[i]; }

        var baseLayer = SC2BlendMaskLayerMath.CreateBaseFromRgbaMask(rgba, Pixels);

        Assert.Equal(alphas, Flatten(baseLayer));
    }

    [Fact]
    public void CreateBaseFromRgbaMask_WithMissingOrWrongSizedMask_GivesAFullyCoveringZeroLayer()
    {
        Assert.Equal(new byte[Pixels], Flatten(SC2BlendMaskLayerMath.CreateBaseFromRgbaMask(null, Pixels)));
        Assert.Equal(new byte[Pixels], Flatten(SC2BlendMaskLayerMath.CreateBaseFromRgbaMask(new byte[3], Pixels)));
    }

    [Fact]
    public void Composite_UsesCoverageAndOpacityOfTheLayerOnTop()
    {
        var baseLayer = Layer("base", (0, 255), (0, 255), (0, 255), (0, 255));
        var top = Layer("top", (255, 255), (255, 128), (255, 0), (100, 255));
        top.Opacity = 1f;

        var flat = Flatten(baseLayer, top);

        Assert.Equal(255, flat[0]);            // full coverage: the top value wins
        Assert.InRange(flat[1], 127, 129);     // half coverage: halfway to the top value
        Assert.Equal(0, flat[2]);              // no coverage: the base shows through
        Assert.Equal(100, flat[3]);

        top.Opacity = 0.5f;
        var half = Flatten(baseLayer, top);
        Assert.InRange(half[0], 127, 129);
        Assert.InRange(half[1], 63, 65);
    }

    [Fact]
    public void Composite_SkipsHiddenAndWrongSizedLayers()
    {
        var baseLayer = Layer("base", (10, 255), (10, 255), (10, 255), (10, 255));
        var hidden = Layer("hidden", (255, 255), (255, 255), (255, 255), (255, 255));
        hidden.Visible = false;
        var broken = new SC2BlendMaskLayer { Name = "broken", Data = new byte[3] };

        Assert.Equal(new byte[] { 10, 10, 10, 10 }, Flatten(baseLayer, hidden, broken));
    }

    [Fact]
    public void Composite_RespectsLayerOrder()
    {
        var a = Layer("a", (200, 255), (200, 255), (200, 255), (200, 255));
        var b = Layer("b", (50, 255), (50, 255), (50, 255), (50, 255));
        Assert.Equal(50, Flatten(a, b)[0]);
        Assert.Equal(200, Flatten(b, a)[0]);
    }

    [Fact]
    public void MergeDown_LooksTheSameAsTheTwoLayersOverAnyBackground()
    {
        var lower = Layer("lower", (40, 255), (200, 90), (255, 0), (10, 255));
        var upper = Layer("upper", (220, 100), (30, 200), (140, 60), (0, 0));
        upper.Opacity = 0.75f;
        lower.Opacity = 0.9f;
        var background = Layer("bg", (60, 255), (60, 255), (60, 255), (60, 255));

        var before = Flatten(background, lower, upper);
        var merged = SC2BlendMaskLayerMath.MergeDown(upper, lower, Pixels);
        var after = Flatten(background, merged);

        for (var i = 0; i < Pixels; i++) Assert.InRange(after[i], before[i] - 2, before[i] + 2);
        Assert.Equal("lower", merged.Name);
        Assert.Equal(lower.Id, merged.Id);
        Assert.Equal(1f, merged.Opacity);
    }

    [Fact]
    public void Stack_AddDuplicateMoveRemoveAndMerge_KeepTheActiveLayerSensible()
    {
        var stack = new SC2BlendMaskLayerStack { Width = 2, Height = 2 };
        stack.Layers.Add(SC2BlendMaskLayerMath.CreateEmptyBaseLayer(stack.PixelCount));
        stack.ActiveIndex = 0;

        var top = stack.AddLayerAboveActive();
        Assert.Equal(1, stack.ActiveIndex);
        Assert.Same(top, stack.Active);
        Assert.Equal("Layer 2", top.Name);

        var copy = stack.DuplicateLayer(1);
        Assert.Equal(2, stack.ActiveIndex);
        Assert.NotEqual(top.Id, copy.Id);
        Assert.Equal(3, stack.Layers.Count);

        Assert.True(stack.MoveLayer(2, -1));
        Assert.Equal(1, stack.ActiveIndex);
        Assert.Same(copy, stack.Layers[1]);
        Assert.False(stack.MoveLayer(2, 1)); // already on top

        Assert.True(stack.RemoveLayer(1));
        Assert.Equal(0, stack.ActiveIndex);
        Assert.Equal(2, stack.Layers.Count);

        Assert.True(stack.MergeDown(1));
        Assert.Single(stack.Layers);
        Assert.False(stack.RemoveLayer(0)); // the last layer stays
        Assert.False(stack.MergeDown(0));   // nothing below the bottom layer
    }

    [Fact]
    public void Normalize_RepairsAnEmptyOrBrokenStack()
    {
        var stack = new SC2BlendMaskLayerStack { Width = 2, Height = 2, ActiveIndex = 9 };
        stack.Layers.Add(null);
        stack.Normalize();
        Assert.Single(stack.Layers);
        Assert.Equal(0, stack.ActiveIndex);

        var duplicateIds = new SC2BlendMaskLayerStack { Width = 2, Height = 2 };
        duplicateIds.Layers.Add(new SC2BlendMaskLayer { Id = "x", Opacity = 7f });
        duplicateIds.Layers.Add(new SC2BlendMaskLayer { Id = "x" });
        duplicateIds.Normalize();
        Assert.Equal(2, duplicateIds.Layers.Select(layer => layer.Id).Distinct().Count());
        Assert.Equal(1f, duplicateIds.Layers[0].Opacity);
    }

    [Fact]
    public void Clone_SharesDataArraysButNotTheLayerList()
    {
        var stack = new SC2BlendMaskLayerStack { Width = 2, Height = 2 };
        stack.Layers.Add(SC2BlendMaskLayerMath.CreateEmptyBaseLayer(stack.PixelCount));
        var copy = stack.Clone();

        Assert.Same(stack.Layers[0].Data, copy.Layers[0].Data);
        copy.Layers[0].Name = "renamed";
        copy.Layers.Add(new SC2BlendMaskLayer());
        Assert.Equal("Base", stack.Layers[0].Name);
        Assert.Single(stack.Layers);
    }
}
