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
                Position = new[] { 1f, 2f, 3f },
                TexCoord = new[] { 0.1f, 0.2f, 0.3f },
                ScaleOffset = new[] { 0.5f, 0.5f, 0.25f, 0.25f },
                TangentXform0 = new[] { 1f, 0f, 0f },
                TangentXform1 = new[] { 0f, 1f, 0f },
                TangentXform2 = new[] { 0f, 0f, 1f },
            });
            model.Vertices.Add(new DecalVertex
            {
                Position = new[] { 4f, 5f, 6f },
                TexCoord = new[] { 0.4f, 0.5f, 0.6f },
                ScaleOffset = new[] { 0.5f, 0.5f, 0.75f, 0.75f },
                TangentXform0 = new[] { 1f, 0f, 0f },
                TangentXform1 = new[] { 0f, 1f, 0f },
                TangentXform2 = new[] { 0f, 0f, 1f },
            });
            model.Vertices.Add(new DecalVertex
            {
                Position = new[] { 7f, 8f, 9f },
                TexCoord = new[] { 0.7f, 0.8f, 0.9f },
                ScaleOffset = new[] { 0.5f, 0.5f, 0f, 0f },
                TangentXform0 = new[] { 1f, 0f, 0f },
                TangentXform1 = new[] { 0f, 1f, 0f },
                TangentXform2 = new[] { 0f, 0f, 1f },
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
