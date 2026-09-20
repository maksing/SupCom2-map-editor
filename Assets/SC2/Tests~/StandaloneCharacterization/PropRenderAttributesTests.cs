using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>A prop's shadow flag (bit 0 of Flags is set when it casts NO shadow) and render priority (mapobjs.bdf "Unknown9", 0..1).</summary>
public class PropRenderAttributesTests
{
    [Fact]
    public void CastsShadow_IsTheAbsenceOfBitZero_AndKeepsTheOtherBits()
    {
        // Flags 0 = the prop casts a shadow, Flags 1 = it does not
        Assert.True(new SC2ObjProp { Flags = 0 }.CastsShadow);
        Assert.False(new SC2ObjProp { Flags = 1 }.CastsShadow);
        var prop = new SC2ObjProp { Flags = 0b110 };
        Assert.True(prop.CastsShadow);
        prop.CastsShadow = false;
        Assert.Equal(0b111u, prop.Flags);
        prop.CastsShadow = true;
        Assert.Equal(0b110u, prop.Flags);
        Assert.Equal(1u, new SC2ObjProp { CastsShadow = false }.Flags);
    }

    [Fact]
    public void NewProps_CastAShadow_AndHaveFullPriority()
    {
        var prop = new SC2ObjProp();
        Assert.True(prop.CastsShadow);
        Assert.Equal(0u, prop.Flags);
        Assert.Equal(1f, prop.RenderPriority);
    }

    [Fact]
    public void Serializer_RoundTripsThePriorityAndTheFlag()
    {
        var doc = new SC2MapDocument();
        doc.Objects.Props.Add(new SC2ObjProp { Id = 1, BlueprintPath = "/props/tree.bp", RenderPriority = 0.625f, CastsShadow = false });
        doc.Objects.Props.Add(new SC2ObjProp { Id = 2, BlueprintPath = "/props/rock.bp" });
        var restored = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));
        var tree = restored.Objects.Props.First(p => p.Id == 1);
        var rock = restored.Objects.Props.First(p => p.Id == 2);
        Assert.Equal(0.625f, tree.RenderPriority, 4);
        Assert.False(tree.CastsShadow);
        Assert.Equal(1u, tree.Flags);
        Assert.Equal(1f, rock.RenderPriority);
        Assert.True(rock.CastsShadow);
    }

    [Fact]
    public void Serializer_ReadsAnOlderDocumentWithoutPriorityAsFullPriority()
    {
        var doc = new SC2MapDocument();
        doc.Objects.Props.Add(new SC2ObjProp { Id = 1, RenderPriority = 0.3f });
        var json = SC2MapDocumentSerializer.Serialize(doc);
        Assert.Contains("renderPriority", json);
        var withoutKey = System.Text.RegularExpressions.Regex.Replace(json, "\"renderPriority\"\\s*:\\s*[-0-9.eE]+,?", "");
        var restored = SC2MapDocumentSerializer.Deserialize(withoutKey);
        Assert.Equal(1f, restored.Objects.Props.First().RenderPriority);
    }
}
