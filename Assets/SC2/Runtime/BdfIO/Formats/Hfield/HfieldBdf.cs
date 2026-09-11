using BdfIO.Core;
using System;

namespace BdfIO.Formats.Hfield
{
/// <summary>
/// hfield.bdf: HFieldHeader (28 bytes) + heightmap (uint16[mapX*mapY]) +
/// terrainTypes ((uint8 or uint16)[(mapX-1)*(mapY-1)]). No string pool.
/// Padding is MinPad32 (a minimum of 32 bytes even if the position is
/// already a multiple of 32).
/// </summary>
public sealed class HfieldModel
{
    public uint Version = 2;
    public uint MapX;
    public uint MapY;

    /// <summary>uint16[MapX*MapY], byte order per Platform — raw height values.</summary>
    public ushort[] Heightmap { get; set; } = Array.Empty<ushort>();

    /// <summary>[(MapX-1)*(MapY-1)], one surface-type ID per cell.</summary>
    public ushort[] TerrainTypes { get; set; } = Array.Empty<ushort>();

    /// <summary>
    /// Element size of terrainTypes in the source hfield.bdf: 1 = u8 raw
    /// image, 2 = legacy/compat u16. Serialization defaults to writing u16.
    /// </summary>
    public int TerrainTypesElementSize { get; set; } = 2;
}

public static class HfieldBdf
{
    private const int HeaderSize = 28;

    public static BdfPadding Padding => BdfPadding.MinPad32;

    /// <summary>Serializes an HfieldModel into a BdfContainer (a convenience wrapper for RegenerateHfield).</summary>
    public static BdfContainer Create(HfieldModel model, BdfPlatform platform)
    {
        var (data, ot) = Serialize(model, platform);
        return new BdfContainer { Platform = platform, Data = data, OffsetTable = ot, NumFiles = 1 };
    }

    public static HfieldModel Parse(BdfContainer container)
    {
        var r = new BdfReader(container.Data, container.Platform);

        var version = r.PeekUInt32At(0);
        var mapX = r.PeekUInt32At(4);
        var mapY = r.PeekUInt32At(8);
        var heightmapSize = r.PeekUInt32At(12);
        var heightmapOffset = (int)r.PeekUInt32At(16);
        var terrainTypesSize = r.PeekUInt32At(20);
        var terrainTypesOffset = (int)r.PeekUInt32At(24);

        var heightmap = new ushort[heightmapSize];
        for (var i = 0; i < heightmapSize; i++)
            heightmap[i] = r.PeekUInt16At(heightmapOffset + i * 2);

        var terrainTypes = new ushort[terrainTypesSize];
        var terrainTypesBytesAvailable = container.Data.Length - terrainTypesOffset;
        var terrainTypesElementSize = terrainTypesBytesAvailable == terrainTypesSize ? 1 : 2;

        if (terrainTypesElementSize == 1)
        {
            for (var i = 0; i < terrainTypesSize; i++)
                terrainTypes[i] = container.Data[terrainTypesOffset + i];
        }
        else
        {
            for (var i = 0; i < terrainTypesSize; i++)
                terrainTypes[i] = r.PeekUInt16At(terrainTypesOffset + i * 2);
        }

        return new HfieldModel
        {
            Version = version,
            MapX = mapX,
            MapY = mapY,
            Heightmap = heightmap,
            TerrainTypes = terrainTypes,
            TerrainTypesElementSize = terrainTypesElementSize
        };
    }

    /// <summary>OffsetTable order (2 entries): heightmapOffset_pos(=16), terrainTypesOffset_pos(=24).</summary>
    public static (byte[] Data, uint[] OffsetTable) Serialize(HfieldModel model, BdfPlatform platform)
    {
        if (model.Heightmap.Length != (int)(model.MapX * model.MapY))
            throw new ArgumentException("Heightmap.Length must equal MapX*MapY");
        if (model.TerrainTypes.Length != (int)((model.MapX - 1) * (model.MapY - 1)))
            throw new ArgumentException("TerrainTypes.Length must equal (MapX-1)*(MapY-1)");

        var w = new BdfWriter(platform);

        var heightmapOffset = HeaderSize;
        var terrainTypesElementSize = model.TerrainTypesElementSize == 1 && TerrainTypesFitByte(model.TerrainTypes)
            ? 1
            : 2;
        var terrainTypesOffset = heightmapOffset + model.Heightmap.Length * 2;

        w.WriteUInt32(model.Version);
        w.WriteUInt32(model.MapX);
        w.WriteUInt32(model.MapY);
        w.WriteUInt32((uint)model.Heightmap.Length);
        w.WriteUInt32((uint)heightmapOffset);
        w.WriteUInt32((uint)model.TerrainTypes.Length);
        w.WriteUInt32((uint)terrainTypesOffset);

        foreach (var h in model.Heightmap) w.WriteUInt16(h);
        if (terrainTypesElementSize == 1)
        {
            foreach (var t in model.TerrainTypes) w.WriteByte((byte)t);
        }
        else
        {
            foreach (var t in model.TerrainTypes) w.WriteUInt16(t);
        }

        var data = w.ToArray();
        var offsetTable = new[] { 16u, 24u };
        return (data, offsetTable);
    }

    private static bool TerrainTypesFitByte(ushort[] terrainTypes)
    {
        foreach (var t in terrainTypes)
            if (t > byte.MaxValue)
                return false;
        return true;
    }
}
}
