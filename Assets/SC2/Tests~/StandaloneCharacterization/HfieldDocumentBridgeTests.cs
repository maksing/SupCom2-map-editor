using System;
using System.IO;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class HfieldDocumentBridgeTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2hfieldblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void BuildFromLegacy_ConvertsUshortArrayToByteBlob()
    {
        var store = NewStore(out var root);
        try
        {
            var terrainTypes = new ushort[] { 2, 2, 3, 2, 3 }; // real vanilla values: Default=2, Chasm_Bottom=3

            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(terrainTypes, gridSize: 1024, store);

            using var stream = store.OpenRead(doc.TerrainTypesUInt8.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var decoded = ms.ToArray();

            Assert.Equal(new byte[] { 2, 2, 3, 2, 3 }, decoded);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ThrowsWhenValueDoesNotFitInByte()
    {
        var store = NewStore(out var root);
        try
        {
            var terrainTypes = new ushort[] { 2, 256, 3 };
            Assert.Throws<InvalidOperationException>(() =>
                SC2HfieldLegacyBridge.BuildFromLegacy(terrainTypes, 1024, store));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_AcceptsTheMaxByteValueExactly()
    {
        var store = NewStore(out var root);
        try
        {
            var terrainTypes = new ushort[] { 255 };
            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(terrainTypes, 1024, store);

            using var stream = store.OpenRead(doc.TerrainTypesUInt8.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(new byte[] { 255 }, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ProjectsGridSizeIntoMapXAndMapY()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(new ushort[] { 2 }, gridSize: 1024, store);
            Assert.Equal(1024, doc.MapX);
            Assert.Equal(1024, doc.MapY);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_LeavesHeightmapUnpopulated()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(new ushort[] { 2 }, 1024, store);
            Assert.True(doc.HeightmapUInt16.IsEmpty);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_StateIsMissingEvenThoughTerrainTypesIsPopulated()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(new ushort[] { 2 }, 1024, store);
            Assert.Equal(SC2DerivedState.Missing, doc.State);
            Assert.False(doc.TerrainTypesUInt8.IsEmpty);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_EmptyArrayProducesEmptyBlobReference()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2HfieldLegacyBridge.BuildFromLegacy(Array.Empty<ushort>(), 1024, store);
            Assert.True(doc.TerrainTypesUInt8.IsEmpty);
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
            Assert.Throws<ArgumentNullException>(() => SC2HfieldLegacyBridge.BuildFromLegacy(null, 1024, store));
            Assert.Throws<ArgumentNullException>(() => SC2HfieldLegacyBridge.BuildFromLegacy(new ushort[] { 2 }, 1024, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
