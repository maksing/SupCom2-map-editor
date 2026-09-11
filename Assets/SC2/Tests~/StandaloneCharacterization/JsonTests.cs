using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class JsonTests
{
    [Fact]
    public void RoundTrips_ScalarKinds()
    {
        var root = SC2JsonValue.NewObject();
        root.Set("s", SC2JsonValue.Of("hello"));
        root.Set("n", SC2JsonValue.Of(42L));
        root.Set("d", SC2JsonValue.Of(3.5));
        root.Set("b", SC2JsonValue.Of(true));
        root.Set("nil", SC2JsonValue.Null);

        var parsed = SC2JsonValue.Parse(root.Write());

        Assert.Equal("hello", parsed.Get("s").AsString());
        Assert.Equal(42, parsed.Get("n").AsLong());
        Assert.Equal(3.5, parsed.Get("d").AsDouble());
        Assert.True(parsed.Get("b").AsBool());
        Assert.Equal(SC2JsonKind.Null, parsed.Get("nil").Kind);
    }

    [Fact]
    public void RoundTrips_NestedObjectsAndArrays()
    {
        var root = SC2JsonValue.NewObject();
        var arr = SC2JsonValue.NewArray();
        arr.Add(SC2JsonValue.Of("a"));
        arr.Add(SC2JsonValue.Of("b"));
        var nested = SC2JsonValue.NewObject();
        nested.Set("inner", SC2JsonValue.Of(7L));
        arr.Add(nested);
        root.Set("items", arr);

        var parsed = SC2JsonValue.Parse(root.Write());
        var items = parsed.Get("items");
        Assert.Equal(SC2JsonKind.Array, items.Kind);
        Assert.Equal(3, items.AsArrayItems.Count);
        Assert.Equal("a", items.AsArrayItems[0].AsString());
        Assert.Equal(7, items.AsArrayItems[2].Get("inner").AsLong());
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("has \"quotes\"")]
    [InlineData("has\\backslash")]
    [InlineData("has\nnewline\tand\rtab")]
    [InlineData("unicode: é中")]
    public void RoundTrips_StringEscaping(string value)
    {
        var root = SC2JsonValue.NewObject();
        root.Set("v", SC2JsonValue.Of(value));
        var parsed = SC2JsonValue.Parse(root.Write());
        Assert.Equal(value, parsed.Get("v").AsString());
    }

    [Fact]
    public void MissingKey_ReturnsNullValueNotException()
    {
        var root = SC2JsonValue.NewObject();
        var missing = root.Get("does-not-exist");
        Assert.Equal(SC2JsonKind.Null, missing.Kind);
        Assert.Null(missing.AsString());
        Assert.Equal("fallback", missing.AsString("fallback"));
    }

    [Fact]
    public void Write_CompactMode_ProducesNoWhitespace()
    {
        var root = SC2JsonValue.NewObject();
        root.Set("a", SC2JsonValue.Of(1L));
        var compact = root.Write(indent: false);
        Assert.DoesNotContain("\n", compact);
        Assert.Equal(1, SC2JsonValue.Parse(compact).Get("a").AsLong());
    }

    [Fact]
    public void EmptyObjectAndArray_RoundTrip()
    {
        var root = SC2JsonValue.NewObject();
        root.Set("emptyObj", SC2JsonValue.NewObject());
        root.Set("emptyArr", SC2JsonValue.NewArray());

        var parsed = SC2JsonValue.Parse(root.Write());
        Assert.Empty(parsed.Get("emptyObj").Members());
        Assert.Empty(parsed.Get("emptyArr").Items());
    }

    [Fact]
    public void Set_OverwritesExistingKeyInPlace()
    {
        var root = SC2JsonValue.NewObject();
        root.Set("k", SC2JsonValue.Of("first"));
        root.Set("k", SC2JsonValue.Of("second"));

        Assert.Equal("second", root.Get("k").AsString());
        Assert.Single(root.Members());
    }
}
