using System.Collections.Generic;

namespace SC2CharacterizationTests;

public class UnitGroupColorTests
{
    private static (float, float, float) Rgb(IList<SC2ObjUnitNode> nodes, SC2ObjUnitNode node)
    {
        SC2UnitGroupColor.Rgb(nodes, node, out var r, out var g, out var b);
        return (r, g, b);
    }

    private static List<SC2ObjUnitNode> Tree()
    {
        return new List<SC2ObjUnitNode>
        {
            new() { Id = 10, ArmyName = "ARMY_1", Name = "INITIAL", IsGroup = true, ParentIndex = -1 },      // 0
            new() { Id = 11, ArmyName = "ARMY_1", Name = "BASE", IsGroup = true, ParentIndex = 0 },          // 1
            new() { Id = 12, ArmyName = "ARMY_1", Name = "u1", ParentIndex = 1 },                            // 2
            new() { Id = 13, ArmyName = "ARMY_1", Name = "u2", ParentIndex = 1 },                            // 3
            new() { Id = 14, ArmyName = "ARMY_1", Name = "u3", ParentIndex = 0 },                            // 4
            new() { Id = 15, ArmyName = "ARMY_1", Name = "loose", ParentIndex = -1 },                        // 5
        };
    }

    [Fact]
    public void UnitsOfTheSameGroupShareAColour_AndDifferentGroupsDiffer()
    {
        var nodes = Tree();

        Assert.Equal(Rgb(nodes, nodes[2]), Rgb(nodes, nodes[3]));
        Assert.NotEqual(Rgb(nodes, nodes[2]), Rgb(nodes, nodes[4]));
    }

    [Fact]
    public void AUnitTakesTheColourOfTheGroupItSitsInDirectly()
    {
        var nodes = Tree();

        // u1 is in BASE (id 11), not in INITIAL (id 10), which holds BASE
        SC2ObjectPalette.Resolve("", 11, out var r, out var g, out var b);
        Assert.Equal((r, g, b), Rgb(nodes, nodes[2]));
        Assert.Equal((r, g, b), Rgb(nodes, nodes[1]));
    }

    [Fact]
    public void AColourChosenForAGroupReachesItsUnits()
    {
        var nodes = Tree();
        nodes[1].ColorHex = "ff0000";

        Assert.Equal((1f, 0f, 0f), Rgb(nodes, nodes[2]));
        Assert.Equal((1f, 0f, 0f), Rgb(nodes, nodes[3]));
        Assert.NotEqual((1f, 0f, 0f), Rgb(nodes, nodes[4]));
    }

    [Fact]
    public void AUnitInNoGroupTakesTheColourOfItsArmy()
    {
        var nodes = Tree();
        var other = new SC2ObjUnitNode { Id = 20, ArmyName = "ARMY_2", ParentIndex = -1 };
        nodes.Add(other);

        Assert.Equal(Rgb(nodes, nodes[5]), Rgb(nodes, nodes[5]));
        Assert.NotEqual(Rgb(nodes, nodes[5]), Rgb(nodes, other));
        SC2UnitGroupColor.RgbOfGroup(null, "ARMY_1", out var r, out var g, out var b);
        Assert.Equal((r, g, b), Rgb(nodes, nodes[5]));
    }

    [Fact]
    public void ABrokenParentIndexDoesNotThrow()
    {
        var nodes = Tree();
        nodes.Add(new SC2ObjUnitNode { Id = 30, ArmyName = "ARMY_1", ParentIndex = 99 });
        nodes.Add(new SC2ObjUnitNode { Id = 31, ArmyName = "ARMY_1", ParentIndex = 2 }); // parent is a unit, not a group

        Assert.Equal(Rgb(nodes, nodes[6]), Rgb(nodes, nodes[5]));
        Assert.Equal(Rgb(nodes, nodes[7]), Rgb(nodes, nodes[5]));
    }

    [Fact]
    public void ArmyKey_IsStableAndNegative()
    {
        Assert.Equal(SC2UnitGroupColor.ArmyKey("ARMY_1"), SC2UnitGroupColor.ArmyKey("army_1"));
        Assert.NotEqual(SC2UnitGroupColor.ArmyKey("ARMY_1"), SC2UnitGroupColor.ArmyKey("ARMY_2"));
        Assert.True(SC2UnitGroupColor.ArmyKey("ARMY_1") < 0);
        Assert.True(SC2UnitGroupColor.ArmyKey("") < 0);
    }
}
