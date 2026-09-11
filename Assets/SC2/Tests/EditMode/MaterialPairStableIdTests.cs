using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Proves the actual live
/// bug fix: deleting a material must not silently re-point OTHER pairs at
/// the wrong material. Exercises the exact sequence SC2MaterialsMode's
/// delete-material button now runs (Materials.RemoveAt +
/// SC2MaterialPairLinker.RelinkPairsToMaterials — SC2MaterialsMode itself
/// is just a thin UI wrapper over the same call, not re-tested here since
/// it needs an IMGUI context to click the button).
/// </summary>
public class MaterialPairStableIdTests
{
    private SC2TerrainData _data;

    [SetUp]
    public void SetUp()
    {
        _data = ScriptableObject.CreateInstance<SC2TerrainData>();
        _data.InitializeFlatPlane(2, 2, 8f);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_data);
    }

    [Test]
    public void DeletingMiddleMaterial_DoesNotResetOtherPairsMaterialLinks()
    {
        _data.Materials.Add(new SC2Material { Name = "Grass" });
        _data.Materials.Add(new SC2Material { Name = "Rock" });
        _data.Materials.Add(new SC2Material { Name = "Sand" });
        _data.EnsureStableIds();

        var grassId = _data.Materials[0].Id;
        var rockId = _data.Materials[1].Id;
        var sandId = _data.Materials[2].Id;

        // Pair A: Grass+Rock (indices 0,1). Pair B: Rock+Sand (indices 1,2).
        var pairA = new SC2MaterialPair { Name = "GrassRock", Mat1Index = 0, Mat2Index = 1 };
        var pairB = new SC2MaterialPair { Name = "RockSand", Mat1Index = 1, Mat2Index = 2 };
        _data.Pairs.Add(pairA);
        _data.Pairs.Add(pairB);
        _data.EnsureStableIds(); // backfills Mat1Id/Mat2Id from the indices above

        Assert.AreEqual(grassId, pairA.Mat1Id);
        Assert.AreEqual(rockId, pairA.Mat2Id);
        Assert.AreEqual(rockId, pairB.Mat1Id);
        Assert.AreEqual(sandId, pairB.Mat2Id);

        // Delete "Rock" (index 1, the MIDDLE material) -- exactly the
        // sequence SC2MaterialsMode.DrawMaterialBlock's delete button runs.
        _data.Materials.RemoveAt(1);
        SC2MaterialPairLinker.RelinkPairsToMaterials(_data);

        // Pair A's Mat1 (Grass) must still resolve to Grass, not silently
        // become whatever material happens to now sit at index 0.
        Assert.AreEqual(0, pairA.Mat1Index);
        Assert.AreEqual(grassId, _data.Materials[pairA.Mat1Index].Id);

        // Pair A's Mat2 referenced the deleted material (Rock) -- must
        // become unassigned (-1), never silently repoint at Sand.
        Assert.AreEqual(-1, pairA.Mat2Index);

        // Pair B's Mat1 also referenced the deleted material -- unassigned.
        Assert.AreEqual(-1, pairB.Mat1Index);

        // Pair B's Mat2 (Sand) must still resolve to Sand at its new index
        // (1, since Rock at old index 1 was removed and Sand shifted down),
        // not to whatever remaining material now happens to sit at the OLD
        // index 2.
        Assert.AreEqual(1, pairB.Mat2Index);
        Assert.AreEqual(sandId, _data.Materials[pairB.Mat2Index].Id);
    }

    [Test]
    public void DeletingUnreferencedMaterial_LeavesAllPairLinksUntouched()
    {
        _data.Materials.Add(new SC2Material { Name = "Grass" });
        _data.Materials.Add(new SC2Material { Name = "Rock" });
        _data.Materials.Add(new SC2Material { Name = "Unused" });
        _data.EnsureStableIds();

        var grassId = _data.Materials[0].Id;
        var rockId = _data.Materials[1].Id;

        var pair = new SC2MaterialPair { Name = "GrassRock", Mat1Index = 0, Mat2Index = 1 };
        _data.Pairs.Add(pair);
        _data.EnsureStableIds();

        _data.Materials.RemoveAt(2); // delete "Unused" -- referenced by nothing
        SC2MaterialPairLinker.RelinkPairsToMaterials(_data);

        Assert.AreEqual(grassId, _data.Materials[pair.Mat1Index].Id);
        Assert.AreEqual(rockId, _data.Materials[pair.Mat2Index].Id);
    }

    [Test]
    public void EnsureStableIds_BackfillsMat1IdMat2IdFromExistingIndices_ForOldSavedData()
    {
        _data.Materials.Add(new SC2Material { Name = "Grass" });
        _data.Materials.Add(new SC2Material { Name = "Rock" });

        // Simulates an asset saved before Mat1Id/Mat2Id existed: index set,
        // Id fields still empty.
        var pair = new SC2MaterialPair { Mat1Index = 0, Mat2Index = 1 };
        _data.Pairs.Add(pair);

        Assert.IsTrue(string.IsNullOrEmpty(pair.Mat1Id));

        _data.EnsureStableIds();

        Assert.AreEqual(_data.Materials[0].Id, pair.Mat1Id);
        Assert.AreEqual(_data.Materials[1].Id, pair.Mat2Id);
    }
}
