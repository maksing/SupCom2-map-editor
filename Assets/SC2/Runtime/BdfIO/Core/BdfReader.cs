using System;
using System.Buffers.Binary;
using System.Text;

namespace BdfIO.Core
{
/// <summary>
/// Reads numbers/strings from a decompressed .bdf's content
/// (<see cref="BdfContainer.Data"/>), honoring <see cref="BdfPlatform"/>:
/// PC = little-endian, Xbox = big-endian.
///
/// Both positional access (Read*, moves <see cref="Position"/>) and random
/// access (Peek*At) are needed: positional for sequentially parsing
/// structures, Peek for resolving pointers/strings at an offset elsewhere
/// in the file.
/// </summary>
public sealed class BdfReader
{
    public BdfReader(byte[] data, BdfPlatform platform)
    {
        Data = data;
        Platform = platform;
    }

    public byte[] Data { get; }
    public BdfPlatform Platform { get; }
    public int Position { get; set; }

    public int Length => Data.Length;

    private ReadOnlySpan<byte> SpanAt(int pos, int size) => Data.AsSpan(pos, size);

    public uint ReadUInt32()
    {
        var v = PeekUInt32At(Position);
        Position += 4;
        return v;
    }

    public int ReadInt32()
    {
        var v = PeekInt32At(Position);
        Position += 4;
        return v;
    }

    public float ReadFloat()
    {
        var v = PeekFloatAt(Position);
        Position += 4;
        return v;
    }

    public ushort ReadUInt16()
    {
        var v = PeekUInt16At(Position);
        Position += 2;
        return v;
    }

    public short ReadInt16()
    {
        var v = PeekInt16At(Position);
        Position += 2;
        return v;
    }

    /// <summary>16-bit half-float (Vertex.uv1).</summary>
    public float ReadHalf()
    {
        var v = PeekHalfAt(Position);
        Position += 2;
        return v;
    }

    /// <summary>
    /// 16-bit half-float at an absolute offset. <see cref="PeekUInt16At"/>
    /// already accounts for <see cref="Platform"/>, so no extra byte-order
    /// reversal is needed here — the bits are always in logical order once
    /// read.
    /// </summary>
    public float PeekHalfAt(int pos) => HalfConvert.ToFloat(PeekUInt16At(pos));

    public byte ReadByte() => Data[Position++];

    public void Skip(int bytes) => Position += bytes;

    // ----- random access (does not change Position) -----

    public uint PeekUInt32At(int pos) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadUInt32LittleEndian(SpanAt(pos, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(SpanAt(pos, 4));

    public int PeekInt32At(int pos) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadInt32LittleEndian(SpanAt(pos, 4))
        : BinaryPrimitives.ReadInt32BigEndian(SpanAt(pos, 4));

    public ushort PeekUInt16At(int pos) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadUInt16LittleEndian(SpanAt(pos, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(SpanAt(pos, 2));

    public short PeekInt16At(int pos) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadInt16LittleEndian(SpanAt(pos, 2))
        : BinaryPrimitives.ReadInt16BigEndian(SpanAt(pos, 2));

    public float PeekFloatAt(int pos)
    {
        var bits = PeekUInt32At(pos);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    /// <summary>
    /// Null-terminated C string at an absolute offset into <see cref="Data"/>.
    /// <c>0xFFFFFFFF</c> and an empty string (a single '\0') are for the
    /// caller to interpret per that specific format's own rules — this only
    /// does the raw read.
    /// </summary>
    public string ReadCStringAt(uint offset)
    {
        var start = (int)offset;
        var end = start;
        while (end < Data.Length && Data[end] != 0) end++;
        return Encoding.ASCII.GetString(Data, start, end - start);
    }
}
}
