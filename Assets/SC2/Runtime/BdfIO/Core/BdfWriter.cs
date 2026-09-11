using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace BdfIO.Core
{
/// <summary>
/// Writes a decompressed .bdf's content honoring <see cref="BdfPlatform"/>,
/// plus a built-in pointer-table builder: <see cref="Mark"/> records the
/// CURRENT position as the next OffsetTable entry — call it before writing
/// a pointer field's value regardless of whether that value is valid
/// (<c>0xFFFFFFFF</c> is marked too).
///
/// The order <see cref="Mark"/> is called in must exactly match the order
/// each format's own layout requires — nothing restores that order
/// automatically.
/// </summary>
public sealed class BdfWriter
{
    private readonly List<byte> _buf = new();
    private readonly List<uint> _pointerTable = new();

    public BdfWriter(BdfPlatform platform)
    {
        Platform = platform;
    }

    public BdfPlatform Platform { get; }
    public int Position => _buf.Count;
    public uint[] PointerTable => _pointerTable.ToArray();

    /// <summary>Records the current position as an OffsetTable entry.</summary>
    public void Mark() => _pointerTable.Add((uint)Position);

    public void WriteUInt32(uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        else BinaryPrimitives.WriteUInt32BigEndian(tmp, value);
        _buf.AddRange(tmp.ToArray());
    }

    public void WriteInt32(int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
        else BinaryPrimitives.WriteInt32BigEndian(tmp, value);
        _buf.AddRange(tmp.ToArray());
    }

    public void WriteFloat(float value) => WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    public void WriteUInt16(ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
        else BinaryPrimitives.WriteUInt16BigEndian(tmp, value);
        _buf.AddRange(tmp.ToArray());
    }

    public void WriteInt16(short value)
    {
        Span<byte> tmp = stackalloc byte[2];
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteInt16LittleEndian(tmp, value);
        else BinaryPrimitives.WriteInt16BigEndian(tmp, value);
        _buf.AddRange(tmp.ToArray());
    }

    /// <summary>16-bit half-float (Vertex.uv1).</summary>
    public void WriteHalf(float value)
    {
        var bits = HalfConvert.FromFloat(value);
        WriteUInt16(bits);
    }

    public void WriteByte(byte value) => _buf.Add(value);

    public void WriteBytes(byte[] bytes) => _buf.AddRange(bytes);

    /// <summary>
    /// <c>Mark()</c> + <c>WriteUInt32(value)</c> — for a pointer field whose
    /// absolute value is already known (or is <c>0xFFFFFFFF</c>).
    /// </summary>
    public void WritePointer(uint value)
    {
        Mark();
        WriteUInt32(value);
    }

    /// <summary>Zero-pads up to a multiple of <paramref name="alignment"/>.</summary>
    public void PadTo(int alignment)
    {
        var rem = Position % alignment;
        if (rem == 0) return;
        for (var i = 0; i < alignment - rem; i++) _buf.Add(0);
    }

    public void PadBytes(int count)
    {
        for (var i = 0; i < count; i++) _buf.Add(0);
    }

    public byte[] ToArray() => _buf.ToArray();
}

/// <summary>
/// String pool: null-terminated ASCII strings, with no deduplication by
/// default (identical texture paths get written multiple times, each
/// pointer referencing its own copy — that's how terrain.bdf actually does
/// it). Deduplication is opt-in via a flag, for formats where it's safe.
/// </summary>
public sealed class StringPoolWriter
{
    private readonly bool _dedup;
    private readonly List<byte> _bytes = new();
    private readonly Dictionary<string, uint> _seen = new();

    public StringPoolWriter(bool dedup)
    {
        _dedup = dedup;
    }

    public int Length => _bytes.Count;

    /// <summary>
    /// Adds a string, returns its offset WITHIN the pool (0-based) — the
    /// caller adds the pool's own base offset in the shared buffer. Each
    /// entry is null-terminated and then zero-padded to a 4-byte boundary
    /// (confirmed byte-for-byte against real vanilla terrain.bdf data: 0-3
    /// zero bytes always separate one entry's '\0' from the next entry's
    /// start).
    /// </summary>
    public uint Add(string value)
    {
        if (_dedup && _seen.TryGetValue(value, out var existing))
            return existing;

        var offset = (uint)_bytes.Count;
        _bytes.AddRange(Encoding.ASCII.GetBytes(value));
        _bytes.Add(0);
        while (_bytes.Count % 4 != 0)
            _bytes.Add(0);

        if (_dedup) _seen[value] = offset;
        return offset;
    }

    public byte[] ToArray() => _bytes.ToArray();
}
}
