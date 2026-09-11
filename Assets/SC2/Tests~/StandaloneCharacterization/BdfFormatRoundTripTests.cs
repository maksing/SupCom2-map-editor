using System.Collections.Generic;
using System.IO;
using System.Linq;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Costs;
using BdfIO.Formats.Collision2;
using BdfIO.Formats.Info;
using BdfIO.Formats.MapObjs;
using BdfIO.Formats.Decals;

/// <summary>
/// Parses
/// every known BDF format against the real 58-map vanilla corpus and asserts
/// the CURRENT structural round-trip behavior: parse succeeds, and
/// re-serializing + re-parsing produces the same summary counts. This is a
/// characterization test, not a correctness test — it exists to catch any
/// regression a future SC2MapDocument migration might introduce in the BDF
/// layer, not to judge whether the current behavior is "right".
///
/// When the vanilla corpus isn't present on the machine running these tests
/// (see TestCorpus.cs), Maps() yields zero cases, so xUnit reports these as
/// "no test data" rather than failing — the corpus is the user's own game
/// data and is never checked into source control.
/// </summary>
public class BdfFormatRoundTripTests
{
    public static IEnumerable<object[]> Maps() =>
        TestCorpus.MapNames.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Maps))]
    public void Terrain_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.terrain.win.bdf");
        if (!File.Exists(path)) return; // not every map ships every file — recorded separately by the golden baseline

        var container = BdfContainer.Read(File.ReadAllBytes(path), TerrainBdf.Padding);
        var model = TerrainBdf.Parse(container);

        Assert.True(model.Materials.Count >= 0);
        Assert.True(model.Vertices.Count > 0, $"{mapName}: expected at least one vertex");
        Assert.True(model.Triangles.Count > 0, $"{mapName}: expected at least one triangle");

        var (data, offsetTable) = TerrainBdf.Serialize(model, container.Platform);
        var reContainer = new BdfContainer { Platform = container.Platform, Data = data, OffsetTable = offsetTable, NumFiles = 1 };
        var reModel = TerrainBdf.Parse(reContainer);

        Assert.Equal(model.Materials.Count, reModel.Materials.Count);
        Assert.Equal(model.Layers.Count, reModel.Layers.Count);
        Assert.Equal(model.Vertices.Count, reModel.Vertices.Count);
        Assert.Equal(model.Triangles.Count, reModel.Triangles.Count);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Hfield_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.hfield.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), HfieldBdf.Padding);
        var model = HfieldBdf.Parse(container);

        Assert.True(model.MapX > 0);
        Assert.True(model.MapY > 0);
        Assert.Equal((int)(model.MapX * model.MapY), model.Heightmap.Length);

        var (data, offsetTable) = HfieldBdf.Serialize(model, container.Platform);
        var reModel = HfieldBdf.Parse(new BdfContainer { Platform = container.Platform, Data = data, OffsetTable = offsetTable, NumFiles = 1 });

        Assert.Equal(model.MapX, reModel.MapX);
        Assert.Equal(model.MapY, reModel.MapY);
        Assert.Equal(model.Heightmap.Length, reModel.Heightmap.Length);
        Assert.Equal(model.TerrainTypesElementSize, reModel.TerrainTypesElementSize);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Costs_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.costs.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), CostsBdf.Padding);
        var model = CostsBdf.Parse(container);
        Assert.True(model.Blocks.Count > 0, $"{mapName}: expected at least one costs block");

        var (data, offsetTable) = CostsBdf.Serialize(model, container.Platform);
        var reModel = CostsBdf.Parse(new BdfContainer { Platform = container.Platform, Data = data, OffsetTable = offsetTable, NumFiles = 1 });
        Assert.Equal(model.Blocks.Count, reModel.Blocks.Count);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Collision2_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.collision2.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), Collision2Bdf.Padding);
        var model = Collision2Bdf.Parse(container);
        Assert.True(model.Vertices.Count > 0, $"{mapName}: expected at least one collision vertex");

        var (data, offsetTable) = Collision2Bdf.Serialize(model, container.Platform);
        var reModel = Collision2Bdf.Parse(new BdfContainer { Platform = container.Platform, Data = data, OffsetTable = offsetTable, NumFiles = 1 });
        Assert.Equal(model.Vertices.Count, reModel.Vertices.Count);
        Assert.Equal(model.Triangles.Count, reModel.Triangles.Count);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Info_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.info.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), InfoBdf.Padding);
        var model = InfoBdf.Parse(container);
        Assert.Equal(508, model.Header.Length);

        var (data, offsetTable) = InfoBdf.Serialize(model, container.Platform);
        var reModel = InfoBdf.Parse(new BdfContainer { Platform = container.Platform, Data = data, OffsetTable = offsetTable, NumFiles = 1 });
        Assert.Equal(model.WaterElevation, reModel.WaterElevation);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void MapObjs_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.mapobjs.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), MapObjsBdf.Padding);
        var model = MapObjsBdf.Parse(container);

        var (data, offsetTable) = MapObjsBdf.Serialize(model, container.Platform);
        var reModel = MapObjsBdf.Parse(new BdfContainer { Platform = container.Platform, Data = data, OffsetTable = offsetTable, NumFiles = 1 });

        Assert.Equal(model.Props.Count, reModel.Props.Count);
        Assert.Equal(model.Markers.Count, reModel.Markers.Count);
        Assert.Equal(model.Graphs.Count, reModel.Graphs.Count);
        for (var i = 0; i < model.Graphs.Count; i++)
            Assert.Equal(model.Graphs[i].Entries.Count, reModel.Graphs[i].Entries.Count);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Decals_ParsesWithoutThrowing(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.decals.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), DecalsBdf.Padding);
        var model = DecalsBdf.Parse(container);
        Assert.Equal(model.Vertices.Count > 0, model.HasDecals);
    }

    /// <summary>
    /// Unlike every other format above, DecalsBdf.Serialize's own geometry
    /// sub-file location scheme relies on BdfContainer.TrailingData (see
    /// DecalsBdf.cs's class doc comment) — its Serialize returns a 4-tuple
    /// (Data, OffsetTable, NumFiles, TrailingData), not the 2-tuple every
    /// other format's Serialize returns. Omitting TrailingData/NumFiles from
    /// the re-Parse container (an easy mistake, since every OTHER format's
    /// round-trip test here only needs Platform/Data/OffsetTable) would
    /// silently re-parse an empty/wrong model instead of failing loudly, so
    /// this is the one format in this file where getting the container
    /// reconstruction right actually matters for the test to mean anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void Decals_ParsesAndRoundTripsStructurally(string mapName)
    {
        var path = Path.Combine(TestCorpus.MapDir(mapName), $"{mapName}.decals.win.bdf");
        if (!File.Exists(path)) return;

        var container = BdfContainer.Read(File.ReadAllBytes(path), DecalsBdf.Padding);
        var model = DecalsBdf.Parse(container);

        var (data, offsetTable, numFiles, trailingData) = DecalsBdf.Serialize(model);
        var reContainer = new BdfContainer
        {
            Platform = container.Platform,
            Data = data,
            OffsetTable = offsetTable,
            NumFiles = numFiles,
            TrailingData = trailingData,
        };
        var reModel = DecalsBdf.Parse(reContainer);

        Assert.Equal(model.DiffuseAtlasPath, reModel.DiffuseAtlasPath);
        Assert.Equal(model.NormalAtlasPath, reModel.NormalAtlasPath);
        Assert.Equal(model.HasDecals, reModel.HasDecals);
        Assert.Equal(model.Vertices.Count, reModel.Vertices.Count);
        Assert.Equal(model.Indices, reModel.Indices);
        // The opaque BSP/spatial-accel block must survive byte-for-byte —
        // it's never decoded, only preserved (see DecalsBdf.cs's own doc
        // comment), so a mismatch here would mean the "don't touch it"
        // contract is actually broken.
        Assert.Equal(model.ExtraDataRaw, reModel.ExtraDataRaw);

        if (model.Vertices.Count > 0)
        {
            // Spot-check the first and last vertex survive byte-for-byte —
            // floats round-trip exactly through WriteFloat/PeekFloatAt (same
            // IEEE 754 bit pattern in and out), so an exact equality check
            // (not an epsilon-tolerant one) is the right assertion here.
            Assert.Equal(model.Vertices[0].Position, reModel.Vertices[0].Position);
            Assert.Equal(model.Vertices[0].ScaleOffset, reModel.Vertices[0].ScaleOffset);
            var last = model.Vertices.Count - 1;
            Assert.Equal(model.Vertices[last].Position, reModel.Vertices[last].Position);
            Assert.Equal(model.Vertices[last].TangentXform2, reModel.Vertices[last].TangentXform2);
        }
    }
}
