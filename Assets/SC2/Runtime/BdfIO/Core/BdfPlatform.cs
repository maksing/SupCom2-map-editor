using System;
using System.IO;

namespace BdfIO.Core
{
/// <summary>
/// The source .bdf's platform — determines the container's magic string and
/// the byte order of every numeric field (both the header/offset table and
/// the decompressed structures). The reference map is PC; Xbox is
/// theoretically supported (same layout/field order, different byte order)
/// but unverified against real files.
/// </summary>
public enum BdfPlatform
{
    /// <summary>magic "MFDB", little-endian.</summary>
    Pc,

    /// <summary>magic "BDFM", big-endian.</summary>
    Xbox
}

public static class BdfMagic
{
    public const string Pc = "MFDB";
    public const string Xbox = "BDFM";

    /// <summary>Determines the platform from a file's first 4 bytes.</summary>
    public static BdfPlatform Detect(ReadOnlySpan<byte> first4Bytes)
    {
        if (first4Bytes.Length < 4)
            throw new ArgumentException("Need at least 4 bytes to detect magic", nameof(first4Bytes));

        if (first4Bytes[0] == (byte)'M' && first4Bytes[1] == (byte)'F'
            && first4Bytes[2] == (byte)'D' && first4Bytes[3] == (byte)'B')
            return BdfPlatform.Pc;

        if (first4Bytes[0] == (byte)'B' && first4Bytes[1] == (byte)'D'
            && first4Bytes[2] == (byte)'F' && first4Bytes[3] == (byte)'M')
            return BdfPlatform.Xbox;

        throw new InvalidDataException(
            $"Unknown BDF magic: {BitConverter.ToString(first4Bytes[..4].ToArray())}");
    }

    public static string For(BdfPlatform platform) => platform switch
    {
        BdfPlatform.Pc => Pc,
        BdfPlatform.Xbox => Xbox,
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };
}
}
