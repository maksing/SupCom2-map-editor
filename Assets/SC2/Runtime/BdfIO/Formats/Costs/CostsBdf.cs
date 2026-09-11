using BdfIO.Core;
using System;
using System.Collections.Generic;

namespace BdfIO.Formats.Costs
{
/// <summary>One costs.bdf block (navmesh + island map + an arbitrary trailing longInts tail).</summary>
public sealed class CostsBlock
{
    public uint BlockIndex;

    /// <summary>8-bit grayscale; size = navmesh.Length = islandmap.Length (usually width×height).</summary>
    public byte[] Navmesh { get; set; } = Array.Empty<byte>();

    /// <summary>8-bit; connected components of navmesh, indices 0..254.</summary>
    public byte[] Islandmap { get; set; } = Array.Empty<byte>();

    /// <summary>Arbitrary count of trailing uint32 values per block (measured on real vanilla data: 28,28,28,24,4).</summary>
    public uint[] LongInts { get; set; } = Array.Empty<uint>();
}

/// <summary>
/// costs.bdf: CostsHeader (12 bytes) + CostsBlockHeader[numBlocks] (28 bytes
/// each) + each block's data IN SEQUENCE (navmesh -> islandmap -> longInts),
/// no string pool. Padding is MinPad32.
/// </summary>
public sealed class CostsModel
{
    public uint Version = 2;
    public List<CostsBlock> Blocks { get; } = new();
}

public static class CostsBdf
{
    private const int HeaderSize = 12;
    private const int BlockHeaderSize = 28;

    public static BdfPadding Padding => BdfPadding.MinPad32;

    public static CostsModel Parse(BdfContainer container)
    {
        var r = new BdfReader(container.Data, container.Platform);

        var model = new CostsModel { Version = r.PeekUInt32At(0) };
        var numBlocks = (int)r.PeekUInt32At(4);
        var blocksOffset = (int)r.PeekUInt32At(8);

        for (var i = 0; i < numBlocks; i++)
        {
            var bh = blocksOffset + i * BlockHeaderSize;
            var blockIndex = r.PeekUInt32At(bh + 0);
            var navmeshSize = (int)r.PeekUInt32At(bh + 4);
            var navmeshOffset = (int)r.PeekUInt32At(bh + 8);
            var islandmapSize = (int)r.PeekUInt32At(bh + 12);
            var islandmapOffset = (int)r.PeekUInt32At(bh + 16);
            var numLongInts = (int)r.PeekUInt32At(bh + 20);
            var longIntsOffset = (int)r.PeekUInt32At(bh + 24);

            var navmesh = new byte[navmeshSize];
            Array.Copy(container.Data, navmeshOffset, navmesh, 0, navmeshSize);

            var islandmap = new byte[islandmapSize];
            Array.Copy(container.Data, islandmapOffset, islandmap, 0, islandmapSize);

            var longInts = new uint[numLongInts];
            for (var j = 0; j < numLongInts; j++)
                longInts[j] = r.PeekUInt32At(longIntsOffset + j * 4);

            model.Blocks.Add(new CostsBlock
            {
                BlockIndex = blockIndex,
                Navmesh = navmesh,
                Islandmap = islandmap,
                LongInts = longInts
            });
        }

        return model;
    }

    /// <summary>
    /// OffsetTable order (numBlocks*3+1 entries, confirmed byte-for-byte):
    /// for block i, navmeshOffset_pos, islandmapOffset_pos,
    /// longIntsOffset_pos in a row (CostsBlockHeader fields in declaration
    /// order), then one final entry after every block = CostsHeader.
    /// blocksOffset_pos (=8).
    /// </summary>
    public static (byte[] Data, uint[] OffsetTable) Serialize(CostsModel model, BdfPlatform platform)
    {
        var numBlocks = model.Blocks.Count;
        var blocksOffset = HeaderSize;

        // Lay out block data (navmesh -> island -> longInts, sequentially).
        var dataStart = blocksOffset + numBlocks * BlockHeaderSize;
        var navmeshOffsets = new int[numBlocks];
        var islandmapOffsets = new int[numBlocks];
        var longIntsOffsets = new int[numBlocks];

        var cursor = dataStart;
        for (var i = 0; i < numBlocks; i++)
        {
            var b = model.Blocks[i];
            navmeshOffsets[i] = cursor;
            cursor += b.Navmesh.Length;
            islandmapOffsets[i] = cursor;
            cursor += b.Islandmap.Length;
            longIntsOffsets[i] = cursor;
            cursor += b.LongInts.Length * 4;
        }

        var w = new BdfWriter(platform);

        // CostsHeader
        w.WriteUInt32(model.Version);
        w.WriteUInt32((uint)numBlocks);
        w.WriteUInt32((uint)blocksOffset);

        // CostsBlockHeader[numBlocks]
        for (var i = 0; i < numBlocks; i++)
        {
            var b = model.Blocks[i];
            w.WriteUInt32(b.BlockIndex);
            w.WriteUInt32((uint)b.Navmesh.Length);
            w.WriteUInt32((uint)navmeshOffsets[i]);
            w.WriteUInt32((uint)b.Islandmap.Length);
            w.WriteUInt32((uint)islandmapOffsets[i]);
            w.WriteUInt32((uint)b.LongInts.Length);
            w.WriteUInt32((uint)longIntsOffsets[i]);
        }

        // Block data
        for (var i = 0; i < numBlocks; i++)
        {
            var b = model.Blocks[i];
            w.WriteBytes(b.Navmesh);
            w.WriteBytes(b.Islandmap);
            foreach (var v in b.LongInts) w.WriteUInt32(v);
        }

        var data = w.ToArray();

        var table = new List<uint>();
        for (var i = 0; i < numBlocks; i++)
        {
            var bh = blocksOffset + i * BlockHeaderSize;
            table.Add((uint)(bh + 8));  // navmeshOffset
            table.Add((uint)(bh + 16)); // islandmapOffset
            table.Add((uint)(bh + 24)); // longIntsOffset
        }
        table.Add(8); // CostsHeader.blocksOffset

        return (data, table.ToArray());
    }
}
}
