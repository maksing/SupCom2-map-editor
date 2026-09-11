using BdfIO.Core;
using BdfIO.Formats.Terrain;
using System;
using System.Collections.Generic;

namespace BdfIO.Formats.Collision2
{
/// <summary>
/// collision2.bdf: CollisionHeader (24 bytes) + CollisionVertex[]
/// (pos-only, 12 bytes) + Triangle[] (12 bytes) + ONE ExtraDataHeader (108
/// bytes) + a forest of ExtraDataBlock[] (56 bytes, the same format as
/// terrain.bdf — reuses <see cref="EdbNode"/>/<see cref="TerrainTriangle"/>)
/// + a 28-byte tail (all zeros on every vanilla map examined; its purpose
/// is unknown). No string pool. Padding is Align32.
/// </summary>
public sealed class Collision2Model
{
    public uint Version = 1;

    /// <summary>Position only — no UV/normal.</summary>
    public List<(float X, float Y, float Z)> Vertices { get; } = new();

    public List<TerrainTriangle> Triangles { get; } = new();

    // ExtraDataHeader (one per whole map) — same constants as terrain.bdf.
    public int BspVersion1 = 200;
    public int BspVersion2 = 30;
    public float Hc = 200.0f;
    public float[] MapCenter = new float[3];

    /// <summary>BSP/BVH nodes in file order (index 0 = root).</summary>
    public List<EdbNode> Nodes { get; } = new();

    /// <summary>28-byte tail after the forest (all zeros on every vanilla map examined).</summary>
    public byte[] Tail { get; set; } = new byte[28];
}

public static class Collision2Bdf
{
    private const int HeaderSize = 24;
    private const int VertexSize = 12;
    private const int EdhSize = 108;
    private const int TailSize = 28;

    public static BdfPadding Padding => BdfPadding.Align32;

    public static Collision2Model Parse(BdfContainer container)
    {
        var r = new BdfReader(container.Data, container.Platform);

        var version = r.PeekUInt32At(0);
        var totalVertices = (int)r.PeekUInt32At(4);
        var vertOffset = (int)r.PeekUInt32At(8);
        var totalIndices = (int)r.PeekUInt32At(12);
        var indexOffset = (int)r.PeekUInt32At(16);
        var extraOffset = (int)r.PeekUInt32At(20);

        var model = new Collision2Model { Version = version };

        for (var i = 0; i < totalVertices; i++)
        {
            var off = vertOffset + i * VertexSize;
            model.Vertices.Add((r.PeekFloatAt(off), r.PeekFloatAt(off + 4), r.PeekFloatAt(off + 8)));
        }

        for (var i = 0; i < totalIndices / 3; i++)
        {
            var off = indexOffset + i * TerrainLayout.TriangleSize;
            model.Triangles.Add(new TerrainTriangle
            {
                A = r.PeekUInt32At(off + 0),
                B = r.PeekUInt32At(off + 4),
                C = r.PeekUInt32At(off + 8)
            });
        }

        // ExtraDataHeader (108 bytes, same layout as terrain.bdf)
        model.BspVersion1 = r.PeekInt32At(extraOffset + 0);
        model.BspVersion2 = r.PeekInt32At(extraOffset + 4);
        model.Hc = r.PeekFloatAt(extraOffset + 8);
        model.MapCenter = new float[] { r.PeekFloatAt(extraOffset + 96), r.PeekFloatAt(extraOffset + 100), r.PeekFloatAt(extraOffset + 104) };
        var root = (int)r.PeekUInt32At(extraOffset + 32);

        // NOT (container.Data.Length - TailSize - root) / EdbSize: that
        // formula silently drops the LAST node on 51/56 real vanilla
        // collision2.win.bdf files (measured directly against the full
        // corpus) - Data.Length includes trailing padding/rounding that
        // doesn't divide evenly by EdbSize once TailSize is subtracted, so
        // the truncating integer division comes up one short. The
        // OffsetTable-relocation count is exact on all 56 files: the format
        // emits exactly 2 relocations per EDB node (Child1/Child2, the only
        // pointer-valued fields in the 56-byte record) plus 13 fixed
        // relocations elsewhere in the file, so this is invertible with no
        // rounding step at all.
        var nodeCount = (container.OffsetTable.Length - 13) / 2;
        for (var k = 0; k < nodeCount; k++)
        {
            var off = root + k * TerrainLayout.EdbSize;
            // Same -1/×3 encoding as TerrainBdf.Parse for fields [+36]/[+40].
            var rawEndVertexMinus1 = r.PeekUInt32At(off + 36);
            var rawStartPolyTimes3 = r.PeekUInt32At(off + 40);
            model.Nodes.Add(new EdbNode
            {
                BboxMin     = new float[] { r.PeekFloatAt(off + 0), r.PeekFloatAt(off + 4), r.PeekFloatAt(off + 8) },
                BboxMax     = new float[] { r.PeekFloatAt(off + 12), r.PeekFloatAt(off + 16), r.PeekFloatAt(off + 20) },
                IsLeaf      = r.PeekUInt32At(off + 24) != 0,
                StartVertex = r.PeekUInt32At(off + 32),
                EndVertex   = rawEndVertexMinus1 + 1,
                StartPoly   = rawStartPolyTimes3 / 3,
                PolyCount   = r.PeekUInt32At(off + 44),
                Child1Index = ChildIndex(r.PeekUInt32At(off + 48), root),
                Child2Index = ChildIndex(r.PeekUInt32At(off + 52), root)
            });
        }

        model.Tail = container.Data[^TailSize..];

        return model;
    }

    private static int? ChildIndex(uint rawOffset, int root) =>
        rawOffset == 0xFFFFFFFF ? null : (int)((rawOffset - root) / TerrainLayout.EdbSize);

    private static uint ResolveChild(int? childIndex, int root) =>
        childIndex is { } idx ? (uint)(root + idx * TerrainLayout.EdbSize) : 0xFFFFFFFF;

    /// <summary>
    /// OffsetTable order (2103 entries on a real vanilla map examined,
    /// confirmed byte-for-byte):
    ///  A) vertOffset_pos(=8), indexOffset_pos(=16)
    ///  B) for each node k=0..N-1 (root+k*56): child1pos, child2pos
    ///  C) EDH.rootBlockOffset_pos, filler1[0..2], filler2[0..2], filler3[0..2] (10)
    ///  D) extraOffset_pos(=20)
    /// </summary>
    public static (byte[] Data, uint[] OffsetTable) Serialize(Collision2Model model, BdfPlatform platform)
    {
        var vertOffset = HeaderSize;
        var indexOffset = vertOffset + model.Vertices.Count * VertexSize;
        var extraOffset = indexOffset + model.Triangles.Count * TerrainLayout.TriangleSize;
        var root = extraOffset + EdhSize;

        var w = new BdfWriter(platform);

        // CollisionHeader
        w.WriteUInt32(model.Version);
        w.WriteUInt32((uint)model.Vertices.Count);
        w.WriteUInt32((uint)vertOffset);
        w.WriteUInt32((uint)(model.Triangles.Count * 3));
        w.WriteUInt32((uint)indexOffset);
        w.WriteUInt32((uint)extraOffset);

        // CollisionVertex[]
        foreach (var (x, y, z) in model.Vertices)
        {
            w.WriteFloat(x); w.WriteFloat(y); w.WriteFloat(z);
        }

        // Triangle[]
        foreach (var t in model.Triangles)
        {
            w.WriteUInt32(t.A); w.WriteUInt32(t.B); w.WriteUInt32(t.C);
        }

        // ExtraDataHeader (108 bytes)
        w.WriteInt32(model.BspVersion1);
        w.WriteInt32(model.BspVersion2);
        w.WriteFloat(model.Hc);
        w.WriteInt32(0); w.WriteInt32(0); // cumulativeVertices/Indices — unused for collision2
        w.WriteInt32(0); w.WriteInt32(0); w.WriteInt32(0); // reserved[3]
        w.WriteUInt32((uint)root); // rootBlockOffset
        w.WriteInt32(0); w.WriteInt32(0); // reserved2[2]
        w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); // filler1[3]
        w.WriteInt32(0); w.WriteInt32(0); // reserved3[2]
        w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); // filler2[3]
        w.WriteInt32(0); w.WriteInt32(0); // reserved4[2]
        w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); // filler3[3]
        w.WriteFloat(model.MapCenter[0]); w.WriteFloat(model.MapCenter[1]); w.WriteFloat(model.MapCenter[2]);

        // Forest of ExtraDataBlock[]
        foreach (var node in model.Nodes)
        {
            w.WriteFloat(node.BboxMin[0]); w.WriteFloat(node.BboxMin[1]); w.WriteFloat(node.BboxMin[2]);
            w.WriteFloat(node.BboxMax[0]); w.WriteFloat(node.BboxMax[1]); w.WriteFloat(node.BboxMax[2]);
            w.WriteUInt32(node.IsLeaf ? 1u : 0u);   // [+24]
            w.WriteUInt32(node.IsLeaf ? 1u : 0u);   // [+28] IsLeafCopy
            w.WriteUInt32(node.StartVertex);         // [+32]
            w.WriteUInt32(node.EndVertex > 0 ? node.EndVertex - 1 : 0); // [+36]
            w.WriteUInt32(node.StartPoly * 3);       // [+40]
            w.WriteUInt32(node.PolyCount);           // [+44]
            w.WriteUInt32(ResolveChild(node.Child1Index, root));
            w.WriteUInt32(ResolveChild(node.Child2Index, root));
        }

        // 28-byte tail
        if (model.Tail.Length != TailSize)
            throw new ArgumentException($"Tail must be {TailSize} bytes");
        w.WriteBytes(model.Tail);

        var data = w.ToArray();

        // OffsetTable
        var table = new List<uint> { 8u, 16u };
        for (var k = 0; k < model.Nodes.Count; k++)
        {
            var off = root + k * TerrainLayout.EdbSize;
            table.Add((uint)(off + 48));
            table.Add((uint)(off + 52));
        }
        table.Add((uint)(extraOffset + 32)); // rootBlockOffset
        table.Add((uint)(extraOffset + 44)); table.Add((uint)(extraOffset + 48)); table.Add((uint)(extraOffset + 52)); // filler1
        table.Add((uint)(extraOffset + 64)); table.Add((uint)(extraOffset + 68)); table.Add((uint)(extraOffset + 72)); // filler2
        table.Add((uint)(extraOffset + 84)); table.Add((uint)(extraOffset + 88)); table.Add((uint)(extraOffset + 92)); // filler3
        table.Add(20u); // extraOffset

        return (data, table.ToArray());
    }
}
}
