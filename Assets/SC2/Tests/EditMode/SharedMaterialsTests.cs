using NUnit.Framework;
using UnityEngine;

/// <summary>The core and the background use one material list: SC2SharedMaterials.Sync keeps the background's list on the core's
/// objects and its pairs on the materials their ids name.</summary>
public class SharedMaterialsTests
{
    private SC2TerrainData _core;
    private SC2TerrainData _background;

    [SetUp]
    public void SetUp()
    {
        _core = ScriptableObject.CreateInstance<SC2TerrainData>();
        _core.InitializeFlatPlane(2, 2, 8f);
        _core.Materials.Clear();
        _core.Pairs.Clear();
        _core.Materials.Add(new SC2Material { Name = "Grass" });
        _core.Materials.Add(new SC2Material { Name = "Rock" });
        _core.Pairs.Add(new SC2MaterialPair { Name = "Core", Mat1Index = 0, Mat2Index = 1 });

        _background = ScriptableObject.CreateInstance<SC2TerrainData>();
        _background.Materials.Add(new SC2Material { Name = "Far ground" });
        _background.Materials.Add(new SC2Material { Name = "Far rock" });
        _background.Pairs.Add(new SC2MaterialPair { Name = "Background", Mat1Index = 0, Mat2Index = 1 });
        _core.BackgroundTerrainData = _background;
        _core.EnsureStableIds();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_background);
        Object.DestroyImmediate(_core);
    }

    private static string Name(SC2TerrainData data, int index) => index >= 0 && index < data.Materials.Count ? data.Materials[index].Name : null;

    [Test]
    public void Sync_AddsTheBackgroundsOwnMaterialsToTheCoreAndKeepsItsPairsOnThem()
    {
        Assert.IsTrue(SC2SharedMaterials.Sync(_core));

        Assert.AreEqual(4, _core.Materials.Count);
        Assert.AreEqual(4, _background.Materials.Count);
        for (var i = 0; i < 4; i++) Assert.AreSame(_core.Materials[i], _background.Materials[i]);
        Assert.AreEqual("Far ground", Name(_background, _background.Pairs[0].Mat1Index));
        Assert.AreEqual("Far rock", Name(_background, _background.Pairs[0].Mat2Index));
        Assert.AreEqual("Grass", Name(_core, _core.Pairs[0].Mat1Index));
        Assert.IsFalse(SC2SharedMaterials.Sync(_core), "a second call has nothing left to do");
    }

    [Test]
    public void APairOfEitherSurfaceCanTakeAMaterialOfTheOther_AndAnEditShowsOnBoth()
    {
        SC2SharedMaterials.Sync(_core);
        var grass = _core.Materials[0];
        var farRock = _core.Materials[3];

        var backgroundPair = _background.Pairs[0];
        backgroundPair.Mat1Id = grass.Id;
        backgroundPair.Mat1Index = SC2MaterialPairLinker.ResolveMaterialIndex(_background, grass.Id);
        var corePair = _core.Pairs[0];
        corePair.Mat2Id = farRock.Id;
        corePair.Mat2Index = SC2MaterialPairLinker.ResolveMaterialIndex(_core, farRock.Id);

        Assert.IsFalse(SC2SharedMaterials.Sync(_core));
        grass.Name = "Renamed";
        Assert.AreEqual("Renamed", Name(_background, backgroundPair.Mat1Index));
        Assert.AreEqual("Far rock", Name(_core, corePair.Mat2Index));
    }

    [Test]
    public void DeletingACoreMaterial_UnassignsItInTheBackgroundAndLeavesTheOtherPairsAlone()
    {
        SC2SharedMaterials.Sync(_core);
        var backgroundPair = _background.Pairs[0];
        backgroundPair.Mat1Id = _core.Materials[0].Id;
        backgroundPair.Mat1Index = 0;

        _core.Materials.RemoveAt(0);
        SC2MaterialPairLinker.RelinkPairsToMaterials(_core);
        Assert.IsTrue(SC2SharedMaterials.Sync(_core));

        Assert.AreEqual(3, _background.Materials.Count);
        Assert.AreEqual(-1, backgroundPair.Mat1Index);
        Assert.AreEqual("Far rock", Name(_background, backgroundPair.Mat2Index));
    }

    [Test]
    public void AfterTheBackgroundsListIsReplacedByCopies_SyncPutsItBackOnTheCoresObjects()
    {
        SC2SharedMaterials.Sync(_core);
        _background.Materials = _background.Materials.ConvertAll(m => JsonUtility.FromJson<SC2Material>(JsonUtility.ToJson(m)));

        Assert.IsTrue(SC2SharedMaterials.Sync(_core));

        for (var i = 0; i < _core.Materials.Count; i++) Assert.AreSame(_core.Materials[i], _background.Materials[i]);
        Assert.AreEqual("Far ground", Name(_background, _background.Pairs[0].Mat1Index));
    }

    [Test]
    public void WithoutABackground_SyncDoesNothing()
    {
        _core.BackgroundTerrainData = null;
        Assert.IsFalse(SC2SharedMaterials.Sync(_core));
    }
}
