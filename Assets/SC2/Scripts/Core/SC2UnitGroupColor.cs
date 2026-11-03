using System.Collections.Generic;

/// <summary>
/// The colour of a unit in the viewport (its dot and, for a building, its footprint): one colour per group, so the units of a group
/// read as a set. The group is the one the unit sits in directly (the last group of its branch); a group can be given a colour by
/// hand (<see cref="SC2ObjUnitNode.ColorHex"/>), otherwise the colour comes from the group's id (<see cref="SC2ObjectPalette"/>).
/// A unit that sits in no group is coloured by its army instead. Plain floats, so the rule can be tested without Unity.
/// </summary>
public static class SC2UnitGroupColor
{
    /// <summary>The group a node belongs to: the node itself when it is a group, else the group at its parent index, else null.</summary>
    public static SC2ObjUnitNode GroupOf(IList<SC2ObjUnitNode> nodes, SC2ObjUnitNode node)
    {
        if (node == null) return null;
        if (node.IsGroup) return node;
        if (nodes == null || node.ParentIndex < 0 || node.ParentIndex >= nodes.Count) return null;
        var parent = nodes[node.ParentIndex];
        return parent != null && parent.IsGroup ? parent : null;
    }

    public static void Rgb(IList<SC2ObjUnitNode> nodes, SC2ObjUnitNode node, out float r, out float g, out float b) =>
        RgbOfGroup(GroupOf(nodes, node), node?.ArmyName, out r, out g, out b);

    /// <summary>The colour of a group; with no group (null) the colour of the army the unit belongs to.</summary>
    public static void RgbOfGroup(SC2ObjUnitNode group, string armyName, out float r, out float g, out float b)
    {
        if (group != null) SC2ObjectPalette.Resolve(group.ColorHex, group.Id, out r, out g, out b);
        else SC2ObjectPalette.Resolve("", ArmyKey(armyName), out r, out g, out b);
    }

    /// <summary>A stable negative key per army name (the ids of real groups are positive, so the two never meet). FNV-1a, because the
    /// hash code of a string differs between runtimes.</summary>
    public static int ArmyKey(string armyName)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in (armyName ?? "").ToUpperInvariant())
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return -1 - (int)(hash % 100000u);
        }
    }
}
