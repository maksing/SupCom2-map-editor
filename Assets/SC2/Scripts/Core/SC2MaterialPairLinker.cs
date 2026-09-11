// Split out of SC2MaterialsMode (SC2MaterialsPairsMode.cs): that file needs
// UnityEditor.dll (EditorGUILayout/EditorUtility/EditorGUI, used directly,
// not through the runtime shim) and references SC2TerrainShell (the "god
// object" — pulling it in would explode the dependency closure). This
// class has none of that: just SC2TerrainData/SC2Material/SC2MaterialPair,
// so it can live in SC2.Core.asmdef and be exercised by a real EditMode test
// without needing SC2TerrainShell or a UnityEditor reference at all.

/// <summary>
/// Resolves SC2MaterialPair.Mat1Index/Mat2Index from the pair's stable
/// Mat1Id/Mat2Id, instead of manual index-shift arithmetic (which caused a
/// confirmed cross-pair reference-reset bug). The Id is the source of
/// truth; the index is a derived cache every existing reader (preview
/// materials, BDF export) still resolves against. Call after any operation
/// that changes SC2TerrainData.Materials' list shape (currently:
/// SC2MaterialsMode's "delete material" button).
/// </summary>
public static class SC2MaterialPairLinker
{
    public static void RelinkPairsToMaterials(SC2TerrainData data)
    {
        foreach (var p in data.Pairs)
        {
            p.Mat1Index = ResolveMaterialIndex(data, p.Mat1Id);
            p.Mat2Index = ResolveMaterialIndex(data, p.Mat2Id);
        }
    }

    public static int ResolveMaterialIndex(SC2TerrainData data, string materialId)
    {
        if (string.IsNullOrEmpty(materialId)) return -1;
        for (var i = 0; i < data.Materials.Count; i++)
            if (data.Materials[i].Id == materialId)
                return i;
        return -1; // materialId no longer exists (its material was deleted)
    }
}
