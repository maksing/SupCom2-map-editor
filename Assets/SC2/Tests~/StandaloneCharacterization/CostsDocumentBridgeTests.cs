using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class CostsDocumentBridgeTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2costsblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    private static List<SC2CostsBlockData> PlayableWaterBlocks() => new()
    {
        new SC2CostsBlockData { Name = "Land_Small", Navmesh = new byte[] { 1, 1, 1 } },
        new SC2CostsBlockData { Name = "Land_Medium", Navmesh = new byte[] { 1, 5, 1 } },
        new SC2CostsBlockData { Name = "Land_Large", Navmesh = new byte[] { 255, 1, 1 } },
        new SC2CostsBlockData { Name = "Water_Small", Navmesh = new byte[] { 1, 1, 255 } },
        new SC2CostsBlockData { Name = "Water_Large", Navmesh = new byte[] { 5, 5, 5 } },
    };

    [Fact]
    public void BuildFromLegacy_ProjectsFinalGridBytesForEveryBlock()
    {
        var store = NewStore(out var root);
        try
        {
            var blocks = PlayableWaterBlocks();
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(blocks, store);

            Assert.Equal(5, doc.Layers.Count);
            foreach (var block in blocks)
            {
                var layer = doc.Find(block.Name);
                Assert.NotNull(layer);
                using var stream = store.OpenRead(layer.FinalGrid.ContentHash);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                Assert.Equal(block.Navmesh, ms.ToArray());
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_DerivesLayerIdAndFieldNameFromBlockName()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(PlayableWaterBlocks(), store);

            var layer = doc.Find("Land_Small");
            Assert.NotNull(layer);
            Assert.Equal("Land_Small", layer.Id);
            Assert.Equal("Land_Small", layer.FieldName);
            Assert.Equal("Land_Small", layer.DisplayName);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_DetectsPlayableWaterProfile()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(PlayableWaterBlocks(), store);
            Assert.Equal(SC2CostsProfile.PlayableWater, doc.Profile);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_DetectsLandOnlyProfile()
    {
        var store = NewStore(out var root);
        try
        {
            var blocks = new List<SC2CostsBlockData>
            {
                new() { Name = "Land_Small", Navmesh = new byte[] { 1 } },
                new() { Name = "Land_Medium", Navmesh = new byte[] { 1 } },
                new() { Name = "Land_Large", Navmesh = new byte[] { 1 } },
            };
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(blocks, store);
            Assert.Equal(SC2CostsProfile.LandOnly, doc.Profile);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_DetectsImportedCustomProfileForAnyOtherNameSet()
    {
        var store = NewStore(out var root);
        try
        {
            var blocks = new List<SC2CostsBlockData>
            {
                new() { Name = "Some_Custom_Block", Navmesh = new byte[] { 1 } },
                new() { Name = "Another_Block", Navmesh = new byte[] { 1 } },
            };
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(blocks, store);
            Assert.Equal(SC2CostsProfile.ImportedCustom, doc.Profile);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_LeavesBaseGeneratedGridAndPaintOverridesEmpty()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(PlayableWaterBlocks(), store);
            foreach (var layer in doc.Layers)
            {
                Assert.True(layer.BaseGeneratedGrid.IsEmpty);
                Assert.True(layer.PaintOverrides.IsEmpty);
                Assert.False(layer.FinalGrid.IsEmpty);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_StateIsCurrent()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2CostsLegacyBridge.BuildFromLegacy(PlayableWaterBlocks(), store);
            Assert.Equal(SC2DerivedState.Current, doc.State);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ThrowsOnNullArguments()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => SC2CostsLegacyBridge.BuildFromLegacy(null, store));
            Assert.Throws<ArgumentNullException>(() => SC2CostsLegacyBridge.BuildFromLegacy(PlayableWaterBlocks(), null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
