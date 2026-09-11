using BdfIO.Core;
using System;
using System.Collections.Generic;

namespace BdfIO.Formats.Terrain
{
/// <summary>
/// Lays out terrain.bdf's sections at absolute offsets inside the
/// decompressed data. Computed ONCE from a <see cref="TerrainModel"/> (from
/// sizes alone — a different string layout still lands FileHeader/EDH/
/// forest/Materials/GeometryHeader/Vertices/Triangles at the SAME
/// positions, see the explanation in TerrainBdf.Serialize), and used both
/// by <see cref="TerrainBdf.BuildOffsetTable"/> and for writing the actual
/// bytes.
/// </summary>
public sealed class TerrainLayout
{
    public const int FileHeaderSize = 20;
    public const int EdhSize = 108;
    public const int EdbSize = 56;
    public const int MaterialSize = 84;
    public const int ShaderSetupItemSize = 8;
    public const int GeometryHeaderSize = 20; // 5 × uint32
    public const int VertexSize = 32;              // confirmed byte-for-byte
    // No IndexDataPadding constant — indexStart is stored explicitly in
    // GeomHeader[+16] instead of being derived from padding.
    public const int TriangleSize = 12; // 3 * uint32

    /// <summary>
    /// Divisor for the packed 10-bit TBN components. Measured across all 56
    /// vanilla maps (9,073,570 vertices): the average raw vector length is
    /// 51.068, giving |N|=|T|=|B|=1.000. The signed 10-bit field holds
    /// ±511, so the original tool only used about 1/10 of that range — but
    /// the in-game shader normalizes the result anyway, so the exact scale
    /// doesn't affect rendering; this value is kept to match vanilla data
    /// as closely as possible.
    /// </summary>
    public const float TbnPackScale = 51.1f;

    /// <summary>Unpacks a vector from a uint32: three signed 10-bit fields in
    /// bits[0..9] / [10..19] / [20..29] (bits[30..31] unused).</summary>
    public static void UnpackTbn(uint packed, out float x, out float y, out float z)
    {
        x = Signed10((int)(packed & 0x3FF)) / TbnPackScale;
        y = Signed10((int)((packed >> 10) & 0x3FF)) / TbnPackScale;
        z = Signed10((int)((packed >> 20) & 0x3FF)) / TbnPackScale;
    }

    /// <summary>Packs a vector into a uint32 the same way. Components are
    /// clamped to the valid ±511 range so overflow never wraps into the
    /// opposite sign.</summary>
    public static uint PackTbn(float x, float y, float z) =>
        (uint)(Quantize10(x) | (Quantize10(y) << 10) | (Quantize10(z) << 20));

    private static int Signed10(int raw) => raw >= 512 ? raw - 1024 : raw;

    private static int Quantize10(float v)
    {
        var q = (int)MathF.Round(v * TbnPackScale);
        if (q > 511) q = 511;
        if (q < -511) q = -511;
        return q & 0x3FF;
    }

    public int EdhOffset { get; set; } // = FileHeaderSize, always 20
    public int[] ForestStart { get; set; } = Array.Empty<int>(); // per layer
    public int MaterialOffset { get; set; }
    public int[] ShaderSetupStart { get; set; } = Array.Empty<int>(); // per material (-1 if ssCount==0)
    public int StringPoolStart { get; set; }
    public int StringPoolSize { get; set; }
    public int GeometryHeaderStart { get; set; }
    public int VertexStart { get; set; }
    public int IndexStart { get; set; }
    public int TotalSize { get; set; }

    /// <summary>Resolved string offsets (absolute Data coordinates), in write order (see <see cref="TerrainBdf.BuildStringRequests"/>).</summary>
    public Dictionary<TerrainBdf.StringRef, uint> StringOffsets { get; set; } = new Dictionary<TerrainBdf.StringRef, uint>();

    /// <summary>Raw string-pool bytes (written into Data verbatim).</summary>
    public byte[] PoolBytes { get; set; } = Array.Empty<byte>();

    public static TerrainLayout Compute(TerrainModel model)
    {
        const int edhOffset = FileHeaderSize;
        var edhTotal = model.Layers.Count * EdhSize;

        var forestStart = new int[model.Layers.Count];
        var cursor = edhOffset + edhTotal;
        for (var i = 0; i < model.Layers.Count; i++)
        {
            forestStart[i] = cursor;
            cursor += model.Layers[i].Nodes.Count * EdbSize;
        }

        var materialOffset = cursor;
        cursor += model.Materials.Count * MaterialSize;

        var shaderSetupStart = new int[model.Materials.Count];
        for (var i = 0; i < model.Materials.Count; i++)
        {
            var ss = model.Materials[i].ShaderSetup;
            if (ss.Count == 0)
            {
                shaderSetupStart[i] = -1;
                continue;
            }

            shaderSetupStart[i] = cursor;
            cursor += ss.Count * ShaderSetupItemSize;
        }

        var stringPoolStart = cursor;

        // String pool: deduplicated here (identical strings share one
        // entry). Order is deterministic (see BuildStringRequests) but
        // doesn't necessarily match an original file's own string order;
        // the total pool size still matches (same string set either way),
        // so every later section (GeometryHeader/Vertices/Triangles) lands
        // at the same offsets an original file would use.
        var pool = new StringPoolWriter(dedup: true);
        var stringOffsets = new Dictionary<TerrainBdf.StringRef, uint>();
        foreach (var (key, value) in TerrainBdf.BuildStringRequests(model))
            stringOffsets[key] = (uint)stringPoolStart + pool.Add(value);

        var stringPoolSize = pool.Length;
        var poolBytes = pool.ToArray();
        cursor += stringPoolSize;

        // 32-byte alignment before GeometryHeader (confirmed against real vanilla map data).
        var align32Pad = (32 - cursor % 32) % 32;
        cursor += align32Pad;
        // Add the padding into poolBytes.
        if (align32Pad > 0)
        {
            var padded = new byte[poolBytes.Length + align32Pad];
            poolBytes.CopyTo(padded, 0);
            poolBytes = padded;
        }

        var geometryHeaderStart = cursor;
        var vertexStart = geometryHeaderStart + GeometryHeaderSize;
        cursor = vertexStart + model.Vertices.Count * VertexSize;
        var indexStart = cursor;
        cursor += model.Triangles.Count * TriangleSize;

        return new TerrainLayout
        {
            EdhOffset = edhOffset,
            ForestStart = forestStart,
            MaterialOffset = materialOffset,
            ShaderSetupStart = shaderSetupStart,
            StringPoolStart = stringPoolStart,
            StringPoolSize = stringPoolSize,
            GeometryHeaderStart = geometryHeaderStart,
            VertexStart = vertexStart,
            IndexStart = indexStart,
            TotalSize = cursor,
            StringOffsets = stringOffsets,
            PoolBytes = poolBytes
        };
    }
}
}
