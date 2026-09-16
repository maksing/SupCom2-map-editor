using System;
using System.IO;
using System.Linq;
using BdfIO.Core;
using BdfIO.Formats.Decals;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class DecalsDocumentBridgeTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2decalsblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    private static byte[] BuildSyntheticDecalsBdf(bool withDecals)
    {
        var model = new DecalsModel
        {
            DiffuseAtlasPath = "/env/common/decals/atlas_diffuse.dds",
            NormalAtlasPath = "/env/common/decals/atlas_normal.dds",
        };
        if (withDecals)
        {
            model.Vertices.Add(new DecalVertex
            {
                PositionX = 1f, PositionY = 2f, PositionZ = 3f,
                TexCoordX = 0.1f, TexCoordY = 0.2f, TexCoordZ = 0.3f,
                ScaleOffsetX = 0.5f, ScaleOffsetY = 0.5f, ScaleOffsetZ = 0.25f, ScaleOffsetW = 0.25f,
                TangentXform0X = 1f, TangentXform0Y = 0f, TangentXform0Z = 0f,
                TangentXform1X = 0f, TangentXform1Y = 1f, TangentXform1Z = 0f,
                TangentXform2X = 0f, TangentXform2Y = 0f, TangentXform2Z = 1f,
            });
            model.Vertices.Add(new DecalVertex
            {
                PositionX = 4f, PositionY = 5f, PositionZ = 6f,
                TexCoordX = 0.4f, TexCoordY = 0.5f, TexCoordZ = 0.6f,
                ScaleOffsetX = 0.5f, ScaleOffsetY = 0.5f, ScaleOffsetZ = 0.75f, ScaleOffsetW = 0.75f,
                TangentXform0X = 1f, TangentXform0Y = 0f, TangentXform0Z = 0f,
                TangentXform1X = 0f, TangentXform1Y = 1f, TangentXform1Z = 0f,
                TangentXform2X = 0f, TangentXform2Y = 0f, TangentXform2Z = 1f,
            });
            model.Vertices.Add(new DecalVertex
            {
                PositionX = 7f, PositionY = 8f, PositionZ = 9f,
                TexCoordX = 0.7f, TexCoordY = 0.8f, TexCoordZ = 0.9f,
                ScaleOffsetX = 0.5f, ScaleOffsetY = 0.5f, ScaleOffsetZ = 0f, ScaleOffsetW = 0f,
                TangentXform0X = 1f, TangentXform0Y = 0f, TangentXform0Z = 0f,
                TangentXform1X = 0f, TangentXform1Y = 1f, TangentXform1Z = 0f,
                TangentXform2X = 0f, TangentXform2Y = 0f, TangentXform2Z = 1f,
            });
            model.Indices.AddRange(new[] { 0, 1, 2 });
            model.ExtraDataRaw = new byte[] { 1, 2, 3, 4 };
        }

        var (data, offsetTable, numFiles, trailingData) = DecalsBdf.Serialize(model);
        var container = new BdfContainer
        {
            Platform = BdfPlatform.Pc,
            Data = data,
            OffsetTable = offsetTable,
            NumFiles = numFiles,
            TrailingData = trailingData,
        };
        return container.Write(DecalsBdf.Padding);
    }

    [Fact]
    public void BuildFromLegacy_ReadsSummaryFieldsFromSyntheticDecalsBdf()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2DecalsLegacyBridge.BuildFromLegacy(BuildSyntheticDecalsBdf(withDecals: true), store);

            Assert.Equal(SC2DerivedState.Current, doc.State);
            Assert.True(doc.HasDecals);
            Assert.Equal(3, doc.VertexCount);
            Assert.Equal(3, doc.IndexCount);
            Assert.Equal("/env/common/decals/atlas_diffuse.dds", doc.DiffuseAtlasPath);
            Assert.Equal("/env/common/decals/atlas_normal.dds", doc.NormalAtlasPath);
            Assert.False(doc.IsEditable);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ReportsNoDecalsWhenMapHasNone()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2DecalsLegacyBridge.BuildFromLegacy(BuildSyntheticDecalsBdf(withDecals: false), store);

            Assert.Equal(SC2DerivedState.Current, doc.State);
            Assert.False(doc.HasDecals);
            Assert.Equal(0, doc.VertexCount);
            Assert.Equal(0, doc.IndexCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_StoresOriginalBytesAsPassthroughBlob()
    {
        var store = NewStore(out var root);
        try
        {
            var bytes = BuildSyntheticDecalsBdf(withDecals: true);
            var doc = SC2DecalsLegacyBridge.BuildFromLegacy(bytes, store);

            Assert.False(doc.OriginalBdf.IsEmpty);
            using var stream = store.OpenRead(doc.OriginalBdf.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Assert.Equal(bytes, ms.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_MissingBytesLeavesStateMissing()
    {
        var store = NewStore(out var root);
        try
        {
            var doc = SC2DecalsLegacyBridge.BuildFromLegacy(null, store);
            Assert.Equal(SC2DerivedState.Missing, doc.State);
            Assert.True(doc.OriginalBdf.IsEmpty);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BuildFromLegacy_ThrowsOnNullBlobStore()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SC2DecalsLegacyBridge.BuildFromLegacy(BuildSyntheticDecalsBdf(withDecals: true), null));
    }

    // Real vanilla corpus (gamedata.scd/maps) — skipped automatically if the
    // corpus isn't present on this machine, matching every other real-corpus
    // test in this project.
    public static System.Collections.Generic.IEnumerable<object[]> Maps() =>
        TestCorpus.MapNames.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Maps))]
    public void BuildFromLegacy_MatchesFreshParseForRealVanillaDecalsBdf(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.decals.win.bdf");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        var expected = DecalsBdf.Parse(BdfContainer.Read(bytes, DecalsBdf.Padding));

        var store = NewStore(out var root);
        try
        {
            var doc = SC2DecalsLegacyBridge.BuildFromLegacy(bytes, store);

            Assert.Equal(SC2DerivedState.Current, doc.State);
            Assert.Equal(expected.HasDecals, doc.HasDecals);
            Assert.Equal(expected.Vertices.Count, doc.VertexCount);
            Assert.Equal(expected.Indices.Count, doc.IndexCount);
            Assert.Equal(expected.DiffuseAtlasPath, doc.DiffuseAtlasPath);
            Assert.Equal(expected.NormalAtlasPath, doc.NormalAtlasPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
