using System;
using System.IO;
using System.IO.Compression;

namespace BdfIO.Core
{
/// <summary>
/// zlib (RFC 1950) on top of <see cref="DeflateStream"/> (raw deflate, RFC 1951).
///
/// <see cref="System.IO.Compression.ZLibStream"/> only exists in .NET 6+ and
/// may not be available in Unity's runtime, so the zlib wrapper is built by
/// hand: a 2-byte header + raw deflate + a 4-byte Adler32 trailer (BE).
///
/// Every .bdf file examined uses the header <c>78 9C</c> (CMF=0x78 —
/// deflate/32K window, FLG=0x9C — default compression level, a valid
/// FCHECK for that pair).
/// </summary>
public static class ZlibCodec
{
    /// <summary>Standard 2-byte zlib header for the default compression level.</summary>
    public static readonly byte[] DefaultHeader = new byte[] { 0x78, 0x9C };

    public static byte[] Decompress(ReadOnlySpan<byte> zlibBlock, int expectedDecompressedSize)
    {
        if (zlibBlock.Length < 6)
            throw new InvalidDataException("zlib block too small (need at least 2-byte header + 4-byte trailer)");

        var header = zlibBlock[..2];
        if (header[0] != 0x78)
            throw new InvalidDataException(
                $"Unexpected zlib CMF byte 0x{header[0]:X2} (expected 0x78 = deflate/32K window)");

        var deflateBody = zlibBlock[2..^4];
        var trailer = zlibBlock[^4..];

        var result = new byte[expectedDecompressedSize];
        using (var ms = new MemoryStream(deflateBody.ToArray()))
        using (var inflate = new DeflateStream(ms, CompressionMode.Decompress))
        {
            var totalRead = 0;
            while (totalRead < expectedDecompressedSize)
            {
                var read = inflate.Read(result, totalRead, expectedDecompressedSize - totalRead);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead != expectedDecompressedSize)
                throw new InvalidDataException(
                    $"Decompressed {totalRead} bytes, expected {expectedDecompressedSize}");
        }

        // The Adler32 trailer is big-endian (RFC 1950), unlike the
        // little-endian content of PC maps.
        var expectedAdler = (uint)((trailer[0] << 24) | (trailer[1] << 16) | (trailer[2] << 8) | trailer[3]);
        var actualAdler = Adler32.Compute(result);
        if (expectedAdler != actualAdler)
            throw new InvalidDataException(
                $"Adler32 mismatch: expected 0x{expectedAdler:X8}, got 0x{actualAdler:X8}");

        return result;
    }

    /// <summary>
    /// Compresses <paramref name="data"/> into a full zlib block (header +
    /// deflate + Adler32). Note: byte-for-byte agreement with an original
    /// compressed block is NOT guaranteed (different deflate implementations
    /// or levels produce different output for the same input) — a
    /// round-trip test must compare the <i>decompressed</i> data and
    /// OffsetTable, not the file's raw bytes.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        byte[] deflateBody;
        using (var ms = new MemoryStream())
        {
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data);
            deflateBody = ms.ToArray();
        }

        var adler = Adler32.Compute(data);
        var result = new byte[2 + deflateBody.Length + 4];
        DefaultHeader.CopyTo(result, 0);
        deflateBody.CopyTo(result, 2);
        var trailerOffset = 2 + deflateBody.Length;
        result[trailerOffset + 0] = (byte)(adler >> 24);
        result[trailerOffset + 1] = (byte)(adler >> 16);
        result[trailerOffset + 2] = (byte)(adler >> 8);
        result[trailerOffset + 3] = (byte)adler;
        return result;
    }
}
}
