using System;

namespace BdfIO.Core
{
/// <summary>
/// Adler-32 (RFC 1950 §9) — checksum in a zlib block's 4-byte trailer
/// (big-endian, unlike the little-endian content of PC maps).
/// </summary>
public static class Adler32
{
    private const uint Mod = 65521;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        // Not block-optimized — fine at map-file sizes (tens/hundreds of KB
        // of decompressed data).
        foreach (var value in data)
        {
            a += value;
            if (a >= Mod) a -= Mod;
            b += a;
            if (b >= Mod) b -= Mod;
        }

        return (b << 16) | a;
    }
}
}

