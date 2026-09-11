using BdfIO.Core;
using System.Collections.Generic;

namespace BdfIO.Formats.MapObjs
{
/// <summary>MapProp (44 bytes): quaternion + position + scale + blueprintPath + unknown9 + flags.</summary>
public sealed class MapProp
{
    public float[] Quat = new float[] { 1f, 0f, 0f, 0f };
    public float[] Position = new float[3];
    public float Scale = 1f;
    public string BlueprintPath = "";
    public float Unknown9 = 1f;
    public uint Flags;
}

/// <summary>MapMarker (36 bytes): name + alwaysOne + 3×unk + position + markerType.</summary>
public sealed class MapMarker
{
    public string Name = "";
    public float AlwaysOne = 1f;
    public int Unk2, Unk3, Unk4;
    public float[] Position = new float[3];
    public string MarkerType = "";
}

/// <summary>MapGraphHeader (12 bytes) + GraphEntry[] (8 bytes each, pairs of marker indices).</summary>
public sealed class MapGraph
{
    public string Name = "";
    public List<(int X, int Y)> Entries { get; } = new();
}

/// <summary>
/// mapobjs.bdf: MapObjectsHeader (28 bytes) + MapProp[] + a blueprintPath
/// pool + MapMarker[] + a name/markerType pool + MapGraphHeader[] +
/// GraphEntry[] + a graphName pool. All 3 pools are deduplicated and their
/// strings are 4-byte aligned (the same pattern terrain.bdf uses).
/// </summary>
public sealed class MapObjsModel
{
    public int FileVersion = 4;
    public List<MapProp> Props { get; } = new();
    public List<MapMarker> Markers { get; } = new();
    public List<MapGraph> Graphs { get; } = new();
}

public static class MapObjsBdf
{
    private const int HeaderSize = 28;
    private const int PropSize = 44;
    private const int MarkerSize = 36;
    private const int GraphHeaderSize = 12;
    private const int GraphEntrySize = 8;

    // Confirmed against 56 real vanilla mapobjs.bdf files: Align32 (pad=0
    // when the offset table already lands on a 32-byte boundary) fails to
    // locate the zlib stream on 5/56 files — the real format always pads a
    // full 32 bytes before the compressed block, even when already aligned
    // (BdfPadding.MinPad32). Align32 only "worked" for the other 51 files
    // because Align32 and MinPad32 are identical whenever the offset table
    // doesn't land exactly on a 32-byte boundary; they differ only at that
    // boundary, which is exactly where the 5 failures were.
    public static BdfPadding Padding => BdfPadding.MinPad32;

    public static MapObjsModel Parse(BdfContainer container)
    {
        var r = new BdfReader(container.Data, container.Platform);
        var model = new MapObjsModel { FileVersion = r.PeekInt32At(0) };

        var propCount = r.PeekInt32At(4);
        var propListOffset = (int)r.PeekUInt32At(8);
        var markerCount = r.PeekInt32At(12);
        var markerListOffset = (int)r.PeekUInt32At(16);
        var graphCount = r.PeekInt32At(20);
        var graphListOffset = (int)r.PeekUInt32At(24);

        for (var i = 0; i < propCount; i++)
        {
            var off = propListOffset + i * PropSize;
            model.Props.Add(new MapProp
            {
                Quat = new float[] { r.PeekFloatAt(off + 0), r.PeekFloatAt(off + 4), r.PeekFloatAt(off + 8), r.PeekFloatAt(off + 12) },
                Position = new float[] { r.PeekFloatAt(off + 16), r.PeekFloatAt(off + 20), r.PeekFloatAt(off + 24) },
                Scale = r.PeekFloatAt(off + 28),
                BlueprintPath = r.ReadCStringAt(r.PeekUInt32At(off + 32)),
                Unknown9 = r.PeekFloatAt(off + 36),
                Flags = r.PeekUInt32At(off + 40)
            });
        }

        for (var i = 0; i < markerCount; i++)
        {
            var off = markerListOffset + i * MarkerSize;
            model.Markers.Add(new MapMarker
            {
                Name = r.ReadCStringAt(r.PeekUInt32At(off + 0)),
                AlwaysOne = r.PeekFloatAt(off + 4),
                Unk2 = r.PeekInt32At(off + 8),
                Unk3 = r.PeekInt32At(off + 12),
                Unk4 = r.PeekInt32At(off + 16),
                Position = new float[] { r.PeekFloatAt(off + 20), r.PeekFloatAt(off + 24), r.PeekFloatAt(off + 28) },
                MarkerType = r.ReadCStringAt(r.PeekUInt32At(off + 32))
            });
        }

        for (var i = 0; i < graphCount; i++)
        {
            var off = graphListOffset + i * GraphHeaderSize;
            var name = r.ReadCStringAt(r.PeekUInt32At(off + 0));
            var entryCount = r.PeekInt32At(off + 4);
            var entriesOffset = (int)r.PeekUInt32At(off + 8);

            var graph = new MapGraph { Name = name };
            for (var j = 0; j < entryCount; j++)
            {
                var eoff = entriesOffset + j * GraphEntrySize;
                graph.Entries.Add((r.PeekInt32At(eoff + 0), r.PeekInt32At(eoff + 4)));
            }
            model.Graphs.Add(graph);
        }

        return model;
    }

    /// <summary>
    /// OffsetTable order (628+1+412+412+1+8+1=1463 entries on a real
    /// vanilla map examined, confirmed byte-for-byte):
    ///  [0..627]    MapProp[i].blueprintPath_pos
    ///  [628]       header.propListOffset_pos (=8)
    ///  [629..1040] MapMarker[i].name_pos
    ///  [1041..1452] MapMarker[i].markerType_pos
    ///  [1453]      header.markerListOffset_pos (=16)
    ///  [1454..1461] for graph i=0..3: graphName_pos, entries_pos
    ///  [1462]      header.graphListOffset_pos (=24)
    /// </summary>
    public static (byte[] Data, uint[] OffsetTable) Serialize(MapObjsModel model, BdfPlatform platform)
    {
        var propCount = model.Props.Count;
        var markerCount = model.Markers.Count;
        var graphCount = model.Graphs.Count;

        var propListOffset = HeaderSize;
        var bpPoolStart = propListOffset + propCount * PropSize;

        // blueprintPath pool (deduplicated + 4-byte aligned, like terrain.bdf)
        var bpPool = new StringPoolWriter(dedup: true);
        var bpOffsets = new uint[propCount];
        for (var i = 0; i < propCount; i++)
            bpOffsets[i] = (uint)bpPoolStart + bpPool.Add(model.Props[i].BlueprintPath);
        var bpPoolBytes = bpPool.ToArray();

        var markerListOffset = bpPoolStart + bpPoolBytes.Length;
        var markerPoolStart = markerListOffset + markerCount * MarkerSize;

        // name+markerType pool (one shared dedup pool for markers)
        var markerPool = new StringPoolWriter(dedup: true);
        var nameOffsets = new uint[markerCount];
        var typeOffsets = new uint[markerCount];
        for (var i = 0; i < markerCount; i++)
            nameOffsets[i] = (uint)markerPoolStart + markerPool.Add(model.Markers[i].Name);
        for (var i = 0; i < markerCount; i++)
            typeOffsets[i] = (uint)markerPoolStart + markerPool.Add(model.Markers[i].MarkerType);
        var markerPoolBytes = markerPool.ToArray();

        var graphListOffset = markerPoolStart + markerPoolBytes.Length;
        var entriesStart = graphListOffset + graphCount * GraphHeaderSize;

        var entryOffsets = new int[graphCount];
        var cursor = entriesStart;
        for (var i = 0; i < graphCount; i++)
        {
            entryOffsets[i] = cursor;
            cursor += model.Graphs[i].Entries.Count * GraphEntrySize;
        }
        var graphPoolStart = cursor;

        var graphPool = new StringPoolWriter(dedup: true);
        var graphNameOffsets = new uint[graphCount];
        for (var i = 0; i < graphCount; i++)
            graphNameOffsets[i] = (uint)graphPoolStart + graphPool.Add(model.Graphs[i].Name);
        var graphPoolBytes = graphPool.ToArray();

        // ---- write ----
        var w = new BdfWriter(platform);

        w.WriteInt32(model.FileVersion);
        w.WriteInt32(propCount);
        w.WriteUInt32((uint)propListOffset);
        w.WriteInt32(markerCount);
        w.WriteUInt32((uint)markerListOffset);
        w.WriteInt32(graphCount);
        w.WriteUInt32((uint)graphListOffset);

        for (var i = 0; i < propCount; i++)
        {
            var p = model.Props[i];
            w.WriteFloat(p.Quat[0]); w.WriteFloat(p.Quat[1]); w.WriteFloat(p.Quat[2]); w.WriteFloat(p.Quat[3]);
            w.WriteFloat(p.Position[0]); w.WriteFloat(p.Position[1]); w.WriteFloat(p.Position[2]);
            w.WriteFloat(p.Scale);
            w.WriteUInt32(bpOffsets[i]);
            w.WriteFloat(p.Unknown9);
            w.WriteUInt32(p.Flags);
        }

        w.WriteBytes(bpPoolBytes);

        for (var i = 0; i < markerCount; i++)
        {
            var m = model.Markers[i];
            w.WriteUInt32(nameOffsets[i]);
            w.WriteFloat(m.AlwaysOne);
            w.WriteInt32(m.Unk2); w.WriteInt32(m.Unk3); w.WriteInt32(m.Unk4);
            w.WriteFloat(m.Position[0]); w.WriteFloat(m.Position[1]); w.WriteFloat(m.Position[2]);
            w.WriteUInt32(typeOffsets[i]);
        }

        w.WriteBytes(markerPoolBytes);

        for (var i = 0; i < graphCount; i++)
        {
            w.WriteUInt32(graphNameOffsets[i]);
            w.WriteInt32(model.Graphs[i].Entries.Count);
            w.WriteUInt32((uint)entryOffsets[i]);
        }

        for (var i = 0; i < graphCount; i++)
            foreach (var (x, y) in model.Graphs[i].Entries)
            {
                w.WriteInt32(x);
                w.WriteInt32(y);
            }

        w.WriteBytes(graphPoolBytes);

        var data = w.ToArray();

        // ---- OffsetTable ----
        var table = new List<uint>();
        for (var i = 0; i < propCount; i++)
            table.Add((uint)(propListOffset + i * PropSize + 32));
        table.Add(8);

        for (var i = 0; i < markerCount; i++)
            table.Add((uint)(markerListOffset + i * MarkerSize + 0));
        for (var i = 0; i < markerCount; i++)
            table.Add((uint)(markerListOffset + i * MarkerSize + 32));
        table.Add(16);

        for (var i = 0; i < graphCount; i++)
        {
            table.Add((uint)(graphListOffset + i * GraphHeaderSize + 0));
            table.Add((uint)(graphListOffset + i * GraphHeaderSize + 8));
        }
        table.Add(24);

        return (data, table.ToArray());
    }
}
}
