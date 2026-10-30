using System.Collections.Generic;

/// <summary>
/// The core and the background terrain use one list of materials. The core's list is the one that is edited (Materials tab, undo);
/// the background's own list holds the very same <see cref="SC2Material"/> objects, so a pair of either surface can take any
/// material, and a change to a material shows on both. Every reader of the background (preview, export, save) keeps resolving a
/// pair's material through its own list and index, which <see cref="Sync"/> keeps pointing at the same material the pair's
/// stable id names.
/// </summary>
public static class SC2SharedMaterials
{
    /// <summary>Brings the background's material list in line with the core's: the first time for a background (a map saved before the
    /// lists were shared, or a freshly generated one) its own materials the core does not know are added to the core's list; after
    /// that the background's list is a copy of the core's list holding the same objects, and its pairs are re-pointed by material id.
    /// Cheap enough to call every frame. Returns true when anything changed.</summary>
    public static bool Sync(SC2TerrainData core)
    {
        var background = core != null ? core.BackgroundTerrainData : null;
        if (background == null) return false;
        if (core.Materials == null) core.Materials = new List<SC2Material>();
        if (background.Materials == null) background.Materials = new List<SC2Material>();

        // Ids first: the pairs' ids are filled in from their current indices, which still mean the background's old list.
        core.EnsureStableIds();
        background.EnsureStableIds();

        var changed = false;
        if (!background.MaterialsSharedWithCore)
        {
            foreach (var material in background.Materials)
                if (material != null && SC2MaterialPairLinker.ResolveMaterialIndex(core, material.Id) < 0)
                {
                    core.Materials.Add(material);
                    changed = true;
                }
            background.MaterialsSharedWithCore = true;
        }

        if (!SameObjects(core.Materials, background.Materials))
        {
            background.Materials = new List<SC2Material>(core.Materials);
            changed = true;
        }

        foreach (var pair in background.Pairs)
        {
            var first = SC2MaterialPairLinker.ResolveMaterialIndex(background, pair.Mat1Id);
            var second = SC2MaterialPairLinker.ResolveMaterialIndex(background, pair.Mat2Id);
            if (first == pair.Mat1Index && second == pair.Mat2Index) continue;
            pair.Mat1Index = first;
            pair.Mat2Index = second;
            changed = true;
        }
        return changed;
    }

    private static bool SameObjects(List<SC2Material> first, List<SC2Material> second)
    {
        if (first.Count != second.Count) return false;
        for (var i = 0; i < first.Count; i++)
            if (!ReferenceEquals(first[i], second[i])) return false;
        return true;
    }
}
