using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

/// <summary>
/// Full "Import Heightmap" round-trip for SC2GeometryMode, entirely
/// independent of Unity's own Texture2D/AssetDatabase/ImageConversion
/// pipeline — both directions read/write real PNG bytes directly.
///
/// The original export used a TextureFormat.R16 Texture2D +
/// Texture2D.EncodeToPNG() — confirmed (real exported file inspected) to
/// silently write 8-bit regardless of the source format. Rewritten to write
/// PNG bytes by hand instead (self-contained, single-channel/16-bit/no-
/// interlace encoder: raw scanlines, System.IO.Compression.DeflateStream for
/// DEFLATE wrapped in a hand-written zlib header/Adler-32 trailer per RFC
/// 1950, PNG chunks with a standard CRC-32 per RFC 2083) — verified
/// independently outside Unity (a completely separate decoder confirmed real
/// IHDR bitdepth=16 and exact pixel round-trip).
///
/// A follow-up finding: reimporting that file into Unity
/// as an ordinary Texture2D asset still read back as 8-bit — Unity's default
/// Texture Importer settings quantize a plain dropped-in PNG to 8-bit
/// regardless of the source file's real depth, and can additionally resize
/// non-power-of-two images (Data.Heights' real dimensions, GridWidth+1 x
/// GridHeight+1, are essentially never a power of two). A per-folder
/// AssetPostprocessor fix was tried and reverted, since a free choice of
/// export folder AND import file is wanted, not a
/// dedicated watched folder. The only way to give both AND keep full
/// precision is to never touch Unity's Texture2D pipeline for this feature
/// at all: TryParsePng below is a real, general (not just self-round-trip)
/// PNG grayscale decoder — chunk parsing (including multi-IDAT concatenation
/// per the spec), zlib inflate via DeflateStream, and all 5 standard PNG
/// filter types (None/Sub/Up/Average/Paeth) — so it reads any real grayscale
/// PNG (an external tool's 16-bit export included), not only files this
/// class itself wrote.
///
/// Encoding/decoding convention: pixel intensity is height normalized across
/// [SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight], one pixel per
/// height GRID VERTEX on export ((GridWidth+1) x (GridHeight+1), Data.
/// Heights' own row-major layout, index = gz*(GridWidth+1)+gx — see
/// SC2TerrainRuntime's _stride) with no resampling — a lossless (modulo
/// quantization) dump of exactly what's on the terrain. On import, the
/// source image can be ANY resolution — ImportHeights resamples it onto the
/// current grid via the exact same nearest-pixel formula SC2TerrainRuntime.
/// ImportHeightmap already used (so a source image that isn't the terrain's
/// own exported size still behaves exactly like it always has).
///
/// Lives in Scripts/ (not Editor/) so it compiles and runs the same way in
/// both the Editor panel and a standalone Build &amp; Run player — nothing
/// here is Editor-only.
/// </summary>
public static class SC2HeightmapExporter
{
    public static byte[] BuildPng(float[] heights, int gridWidth, int gridHeight)
    {
        var w = gridWidth + 1;
        var h = gridHeight + 1;
        if (heights == null || heights.Length != w * h) return null;

        // Raw scanline data, PNG's own pre-compression layout: one filter-
        // type byte (0 = None) per row, followed by that row's pixels as
        // big-endian 16-bit samples (network byte order, per the PNG spec —
        // NOT little-endian like most in-memory ushort storage).
        var stride = w * 2;
        var raw = new byte[h * (1 + stride)];
        var ri = 0;
        for (var y = 0; y < h; y++)
        {
            raw[ri++] = 0; // filter type: None
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var t = Mathf.InverseLerp(SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight, heights[rowBase + x]);
                var v = (ushort)Mathf.RoundToInt(Mathf.Clamp01(t) * 65535f);
                raw[ri++] = (byte)(v >> 8);   // high byte first (big-endian)
                raw[ri++] = (byte)(v & 0xFF);
            }
        }

        var idatData = ZlibCompress(raw);

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8); // PNG signature

        // IHDR: width(4) height(4) bitDepth(1)=16 colorType(1)=0(grayscale)
        // compression(1)=0 filter(1)=0 interlace(1)=0 — 13 bytes total.
        var ihdr = new byte[13];
        WriteUInt32BE(ihdr, 0, (uint)w);
        WriteUInt32BE(ihdr, 4, (uint)h);
        ihdr[8] = 16;  // bit depth
        ihdr[9] = 0;   // color type: grayscale
        ihdr[10] = 0;  // compression method
        ihdr[11] = 0;  // filter method
        ihdr[12] = 0;  // interlace method
        WriteChunk(ms, "IHDR", ihdr);

        WriteChunk(ms, "IDAT", idatData);
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        return ms.ToArray();
    }

    /// <summary>Reads an arbitrary PNG file's bytes and returns the terrain
    /// heights ready to apply, resampled onto the CURRENT grid
    /// ((gridWidth+1) x (gridHeight+1)) exactly the way SC2TerrainRuntime.
    /// ImportHeightmap always resampled a source Texture2D — same nearest-
    /// pixel formula, same [MinHeight,MaxHeight]-clamped scale multiply.
    /// Returns null (with error set) if the file isn't a decodable grayscale
    /// PNG.</summary>
    public static float[] ImportHeights(byte[] pngBytes, int gridWidth, int gridHeight, float scale, out string error)
    {
        if (!TryParsePng(pngBytes, out var normalized, out var srcW, out var srcH, out error))
            return null;

        var vw = gridWidth + 1;
        var vh = gridHeight + 1;
        var result = new float[vw * vh];
        for (var gz = 0; gz < vh; gz++)
        {
            var py = Mathf.Clamp(Mathf.RoundToInt((float)gz / gridHeight * (srcH - 1)), 0, srcH - 1);
            for (var gx = 0; gx < vw; gx++)
            {
                var px = Mathf.Clamp(Mathf.RoundToInt((float)gx / gridWidth * (srcW - 1)), 0, srcW - 1);
                result[gz * vw + gx] = SC2TerrainData.ClampHeight(normalized[py * srcW + px] * scale);
            }
        }
        return result;
    }

    /// <summary>General-purpose grayscale PNG decoder — not limited to files
    /// BuildPng itself wrote. Supports 8-bit and 16-bit color type 0
    /// (grayscale), multiple IDAT chunks (concatenated per the spec before
    /// inflating), and all 5 standard scanline filter types. Returns
    /// normalized [0,1] intensity per pixel, row-major (index = y*width+x),
    /// matching what Texture2D.GetPixels()[...].grayscale would have given
    /// for the same image.</summary>
    public static bool TryParsePng(byte[] data, out float[] normalized, out int width, out int height, out string error)
    {
        normalized = null; width = 0; height = 0; error = null;

        if (data == null || data.Length < 8 ||
            data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47 ||
            data[4] != 0x0D || data[5] != 0x0A || data[6] != 0x1A || data[7] != 0x0A)
        {
            error = "Not a PNG file (bad signature).";
            return false;
        }

        byte[] ihdr = null;
        var idatChunks = new List<byte[]>();
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var len = ReadUInt32BE(data, pos);
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var chunkDataStart = pos + 8;
            if (chunkDataStart + len + 4 > data.Length) break; // truncated/corrupt — stop, work with what we have
            if (type == "IHDR")
            {
                ihdr = new byte[len];
                Array.Copy(data, chunkDataStart, ihdr, 0, (int)len);
            }
            else if (type == "IDAT")
            {
                var chunk = new byte[len];
                Array.Copy(data, chunkDataStart, chunk, 0, (int)len);
                idatChunks.Add(chunk);
            }
            else if (type == "IEND")
            {
                break;
            }
            pos = chunkDataStart + (int)len + 4; // skip data + CRC
        }

        if (ihdr == null || ihdr.Length < 13)
        {
            error = "PNG is missing a valid IHDR chunk.";
            return false;
        }
        if (idatChunks.Count == 0)
        {
            error = "PNG has no image data (no IDAT chunk).";
            return false;
        }

        width = (int)ReadUInt32BE(ihdr, 0);
        height = (int)ReadUInt32BE(ihdr, 4);
        var bitDepth = ihdr[8];
        var colorType = ihdr[9];
        var interlace = ihdr[12];
        if (colorType != 0)
        {
            error = "Only grayscale PNGs are supported for heightmap import (this file has color/alpha channels) — re-export as grayscale.";
            return false;
        }
        if (bitDepth != 8 && bitDepth != 16)
        {
            error = $"Unsupported PNG bit depth ({bitDepth}) — only 8-bit and 16-bit grayscale are supported.";
            return false;
        }
        if (interlace != 0)
        {
            error = "Interlaced PNGs are not supported for heightmap import — re-export without interlacing.";
            return false;
        }
        if (width <= 0 || height <= 0)
        {
            error = "PNG has an invalid size.";
            return false;
        }

        var idatTotal = 0;
        foreach (var c in idatChunks) idatTotal += c.Length;
        var idat = new byte[idatTotal];
        var off = 0;
        foreach (var c in idatChunks) { Array.Copy(c, 0, idat, off, c.Length); off += c.Length; }

        byte[] raw;
        try
        {
            raw = ZlibDecompress(idat);
        }
        catch (Exception ex)
        {
            error = "Could not decompress PNG image data: " + ex.Message;
            return false;
        }

        var bytesPerPixel = bitDepth == 16 ? 2 : 1;
        var stride = width * bytesPerPixel;
        var expected = height * (1 + stride);
        if (raw.Length < expected)
        {
            error = "PNG image data is shorter than expected (corrupt file?).";
            return false;
        }

        if (!Unfilter(raw, width, height, bytesPerPixel, out var pixelBytes))
        {
            error = "PNG uses an unrecognized scanline filter type (corrupt file?).";
            return false;
        }

        normalized = new float[width * height];
        var maxVal = bitDepth == 16 ? 65535f : 255f;
        var pi = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (bitDepth == 16)
            {
                var v = (pixelBytes[pi] << 8) | pixelBytes[pi + 1];
                normalized[i] = v / maxVal;
                pi += 2;
            }
            else
            {
                normalized[i] = pixelBytes[pi] / maxVal;
                pi += 1;
            }
        }
        return true;
    }

    /// <summary>Standard PNG scanline reconstruction (RFC 2083 section 6) — one
    /// filter-type byte per row, Sub/Up/Average/Paeth all reference already-
    /// RECONSTRUCTED bytes (not the raw filtered ones), computed left-to-
    /// right, top-to-bottom. Returns the unfiltered pixel bytes with the
    /// per-row filter-type bytes stripped out.</summary>
    private static bool Unfilter(byte[] raw, int width, int height, int bpp, out byte[] pixels)
    {
        var stride = width * bpp;
        pixels = new byte[height * stride];
        var rawPos = 0;
        for (var y = 0; y < height; y++)
        {
            var filterType = raw[rawPos++];
            var rowStart = y * stride;
            var prevRowStart = rowStart - stride;
            for (var x = 0; x < stride; x++)
            {
                var filt = raw[rawPos++];
                int a = x >= bpp ? pixels[rowStart + x - bpp] : 0;
                int b = y > 0 ? pixels[prevRowStart + x] : 0;
                int c = (x >= bpp && y > 0) ? pixels[prevRowStart + x - bpp] : 0;
                int recon;
                switch (filterType)
                {
                    case 0: recon = filt; break;
                    case 1: recon = filt + a; break;
                    case 2: recon = filt + b; break;
                    case 3: recon = filt + (a + b) / 2; break;
                    case 4: recon = filt + Paeth(a, b, c); break;
                    default: pixels = null; return false;
                }
                pixels[rowStart + x] = (byte)recon;
            }
        }
        return true;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>RFC 1950 zlib stream: 2-byte header, raw DEFLATE payload
    /// (System.IO.Compression only exposes raw DEFLATE via DeflateStream,
    /// never a zlib-wrapped one), 4-byte big-endian Adler-32 of the
    /// UNCOMPRESSED data. 0x78 0x9C is a standard, always-valid header for
    /// "32K window, default compression level" (FCHECK makes CMF*256+FLG a
    /// multiple of 31 for this exact byte pair) — real decoders only care
    /// that the header is valid, not which compression level produced it.</summary>
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        using var result = new MemoryStream();
        result.WriteByte(0x78);
        result.WriteByte(0x9C);
        compressed.Position = 0;
        compressed.CopyTo(result);
        var adler = Adler32(raw);
        var trailer = new byte[4];
        WriteUInt32BE(trailer, 0, adler);
        result.Write(trailer, 0, 4);
        return result.ToArray();
    }

    /// <summary>Inverse of ZlibCompress — skips the 2-byte zlib header and
    /// trailing 4-byte Adler-32 (not verified; a corrupt stream will already
    /// fail loudly inside DeflateStream), inflating the raw DEFLATE payload
    /// in between via DeflateStream's decompress mode.</summary>
    private static byte[] ZlibDecompress(byte[] zlibData)
    {
        if (zlibData.Length < 6) throw new InvalidDataException("zlib stream too short");
        using var src = new MemoryStream(zlibData, 2, zlibData.Length - 2 - 4, writable: false);
        using var deflate = new DeflateStream(src, CompressionMode.Decompress);
        using var result = new MemoryStream();
        deflate.CopyTo(result);
        return result.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint modAdler = 65521;
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % modAdler;
            b = (b + a) % modAdler;
        }
        return (b << 16) | a;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var lenBytes = new byte[4];
        WriteUInt32BE(lenBytes, 0, (uint)data.Length);
        s.Write(lenBytes, 0, 4);

        var typeBytes = new byte[4];
        typeBytes[0] = (byte)type[0]; typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2]; typeBytes[3] = (byte)type[3];
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteUInt32BE(crcBytes, 0, crc);
        s.Write(crcBytes, 0, 4);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Standard PNG CRC-32 (RFC 2083 Annex D) over a chunk's type
    /// bytes followed by its data bytes.</summary>
    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var t in typeBytes) c = CrcTable[(c ^ t) & 0xFF] ^ (c >> 8);
        foreach (var t in data) c = CrcTable[(c ^ t) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteUInt32BE(byte[] dst, int offset, uint value)
    {
        dst[offset] = (byte)(value >> 24);
        dst[offset + 1] = (byte)(value >> 16);
        dst[offset + 2] = (byte)(value >> 8);
        dst[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32BE(byte[] src, int offset) =>
        ((uint)src[offset] << 24) | ((uint)src[offset + 1] << 16) | ((uint)src[offset + 2] << 8) | src[offset + 3];
}
