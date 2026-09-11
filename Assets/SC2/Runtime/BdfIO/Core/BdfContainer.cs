using System;
using System.IO;
using System.Linq;

namespace BdfIO.Core
{
/// <summary>
/// The universal container shared by all 7 .bdf formats: a 32-byte header +
/// OffsetTable[uint32] + padding + zlib(data) + padding.
///
/// Knows nothing about the concrete structures inside <see cref="Data"/> —
/// that's the job of the format-specific classes in BdfIO.Formats.
/// </summary>
public sealed class BdfContainer
{
    private const int HeaderSize = 32;

    public BdfPlatform Platform { get; set; }
    public uint MajorVersion { get; set; } = 7;
    public uint MinorVersion { get; set; } = 2;
    public uint Reserved { get; set; } = 0;

    /// <summary>The decompressed content — format-specific data.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>Positions inside <see cref="Data"/> marking pointer fields.</summary>
    public uint[] OffsetTable { get; set; } = Array.Empty<uint>();

    /// <summary>Number of logical sub-blocks (1 for most formats, 2 for terrain.bdf).</summary>
    public uint NumFiles { get; set; } = 1;

    /// <summary>
    /// Optional data written into the padding between the OffsetTable and
    /// the compressed block. terrain.bdf stores 2 uint32 values there:
    /// [0, GeometryHeaderStart]. Every other format leaves this null
    /// (padding is plain zeros).
    /// </summary>
    public byte[]? TrailingData { get; set; }

    public static BdfContainer Read(byte[] raw, BdfPadding padding)
    {
        var platform = BdfMagic.Detect(raw.AsSpan(0, 4));
        var r = new BdfReader(raw, platform) { Position = 4 };

        var majorVersion = r.ReadUInt32();
        var minorVersion = r.ReadUInt32();
        var reserved = r.ReadUInt32();
        var compressedSize = r.ReadUInt32();
        var decompressedSize = r.ReadUInt32();
        var offsetTableCount = r.ReadUInt32();
        var numFiles = r.ReadUInt32();

        if (majorVersion != 7 || minorVersion != 2 || reserved != 0)
            throw new InvalidDataException(
                $"Unexpected BDF header: major={majorVersion} minor={minorVersion} reserved={reserved} " +
                "(expected major=7 minor=2 reserved=0)");

        var offsetTable = new uint[offsetTableCount];
        for (var i = 0; i < offsetTableCount; i++)
            offsetTable[i] = r.ReadUInt32();

        var afterTable = HeaderSize + (int)offsetTableCount * 4;
        var prePad = padding.ComputePad(afterTable);
        var compStart = afterTable + prePad;

        // Read trailing data out of the padding (may hold format-specific
        // uint32 values).
        byte[]? trailingData = null;
        if (prePad > 0)
        {
            trailingData = raw[afterTable..compStart];
            if (trailingData.All(b => b == 0)) trailingData = null; // Plain zero padding
        }

        if (compStart + compressedSize > raw.Length)
            throw new InvalidDataException(
                $"Compressed block [{compStart}, {compStart + compressedSize}) exceeds file length {raw.Length}");

        var zlibBlock = raw.AsSpan(compStart, (int)compressedSize);
        var data = ZlibCodec.Decompress(zlibBlock, (int)decompressedSize);

        return new BdfContainer
        {
            Platform = platform,
            MajorVersion = majorVersion,
            MinorVersion = minorVersion,
            Reserved = reserved,
            Data = data,
            OffsetTable = offsetTable,
            NumFiles = numFiles,
            TrailingData = trailingData
        };
    }

    /// <summary>
    /// Assembles the whole file. Note: the compressed block is not
    /// guaranteed to match the original byte-for-byte (see
    /// <see cref="ZlibCodec.Compress"/>) — a round-trip test should compare
    /// <see cref="Data"/>/<see cref="OffsetTable"/> after a fresh
    /// <see cref="Read"/>, not the file's raw bytes.
    /// </summary>
    public byte[] Write(BdfPadding padding)
    {
        var zlibBlock = ZlibCodec.Compress(Data);

        var afterTable = HeaderSize + OffsetTable.Length * 4;
        var prePad = padding.ComputePad(afterTable);
        var compStart = afterTable + prePad;
        var afterZlib = compStart + zlibBlock.Length;
        var postPad = padding.ComputePad(afterZlib);
        var totalSize = afterZlib + postPad;

        var w = new BdfWriter(Platform);

        // magic (4 bytes, not affected by platform byte order — plain ASCII)
        w.WriteBytes(System.Text.Encoding.ASCII.GetBytes(BdfMagic.For(Platform)));
        w.WriteUInt32(MajorVersion);
        w.WriteUInt32(MinorVersion);
        w.WriteUInt32(Reserved);
        w.WriteUInt32((uint)zlibBlock.Length);
        w.WriteUInt32((uint)Data.Length);
        w.WriteUInt32((uint)OffsetTable.Length);
        w.WriteUInt32(NumFiles);

        foreach (var entry in OffsetTable)
            w.WriteUInt32(entry);

        // Write TrailingData if present (plus zeros up to the full pad),
        // otherwise plain zeros.
        if (TrailingData != null && TrailingData.Length <= prePad)
        {
            w.WriteBytes(TrailingData);
            w.PadBytes(prePad - TrailingData.Length);
        }
        else
        {
            w.PadBytes(prePad);
        }
        w.WriteBytes(zlibBlock);
        w.PadBytes(postPad);

        var result = w.ToArray();
        if (result.Length != totalSize)
            throw new InvalidOperationException(
                $"Internal error: wrote {result.Length} bytes, expected {totalSize}");

        return result;
    }
}
}
