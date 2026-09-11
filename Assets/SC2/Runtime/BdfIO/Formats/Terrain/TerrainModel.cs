using System.Collections.Generic;

namespace BdfIO.Formats.Terrain
{
/// <summary>
/// Full terrain.bdf model: materials + geometry + a BSP forest per layer.
/// Used both for round-trip and as the data source for visualization/the
/// material inspector in Unity.
/// </summary>
public sealed class TerrainModel
{
    /// <summary>FileHeader_ExtraMaterials.version, usually = 6.</summary>
    public int Version = 6;

    public List<TerrainLayer> Layers { get; } = new();
    public List<TerrainMaterial> Materials { get; } = new();

    /// <summary>Shared vertex buffer, common to all layers (see cumulativeVertices in ExtraDataHeader).</summary>
    public List<TerrainVertex> Vertices { get; } = new();

    /// <summary>Shared triangle buffer.</summary>
    public List<TerrainTriangle> Triangles { get; } = new();
}

/// <summary>
/// One material/geometry layer (ExtraDataHeader + a BSP forest of ExtraDataBlock[]).
/// </summary>
public sealed class TerrainLayer
{
    // ExtraDataHeader constants — identical across every layer examined.
    public int BspVersion1 = 200;
    public int BspVersion2 = 30;
    public float Hc = 200.0f;

    /// <summary>Sum of vertex counts of all previous layers.</summary>
    public int CumulativeVertices;

    /// <summary>Sum of index counts of all previous layers.</summary>
    public int CumulativeIndices;

    public float[] MapCenter = new float[3];

    /// <summary>
    /// BSP/kD-tree nodes in file order (index 0 = root; rootBlockOffset =
    /// this array's start in the file).
    /// </summary>
    public List<EdbNode> Nodes { get; } = new();
}

/// <summary>
/// EdbNode (56 bytes). Semantics confirmed against the reference Blender
/// exporter (SupCom2-TerrainExporter, pack_edb) and a byte-for-byte check
/// against a real game file:
///   [+0..11]  BboxMin (3×float32)
///   [+12..23] BboxMax (3×float32)
///   [+24]     IsLeaf      (uint32: 0=internal, 1=leaf)
///   [+28]     IsLeafCopy  (uint32: duplicates IsLeaf)
///   [+32]     StartVertex (uint32: local first vertex index)
///   [+36]     EndVertex-1 (uint32: the LAST valid vertex index in the
///                          file; the model stores the already-decoded
///                          EndVertex = file_value + 1)
///   [+40]     StartPoly*3 (uint32: first triangle index × 3 in the file;
///                          the model stores the already-decoded StartPoly
///                          = file_value / 3)
///   [+44]     PolyCount   (uint32: triangle count)
///   [+48]     Child1 (uint32: absolute offset in dec, 0xFFFFFFFF=none)
///   [+52]     Child2 (uint32: absolute offset in dec, 0xFFFFFFFF=none)
/// (De)coding the -1/÷3 happens in TerrainBdf.Parse/Serialize — the model
/// itself always stores StartPoly/EndVertex as plain "unpacked" values.
/// </summary>
public sealed class EdbNode
{
    public float[] BboxMin     = new float[3];
    public float[] BboxMax     = new float[3];
    public bool    IsLeaf;
    public uint    StartVertex;    // [+32]
    public uint    StartPoly;      // decoded from the file's [+40] (/3)
    public uint    EndVertex;      // decoded from the file's [+36] (+1)
    public uint    PolyCount;      // [+44]

    public int? Child1Index;
    public int? Child2Index;

    [System.Obsolete("Use StartPoly instead")] public uint StartPolygon { get => StartPoly; set => StartPoly = value; }
    [System.Obsolete("Use PolyCount instead")]  public uint PolygonCount { get => PolyCount; set => PolyCount = value; }
}

/// <summary>
/// MaterialData (84 bytes) + an associated ShaderSetup. Strings are
/// nullable; null = a 0xFFFFFFFF pointer (the field is absent).
/// </summary>
public sealed class TerrainMaterial
{
    public string Name = "";
    public string? ShaderFile;
    public string? ShaderPrefix;
    public string? State;
    public float[] TextureScroll = new float[2];

    /// <summary>
    /// 13 texture slots (Diffuse1/2, Normal1-4, Mask1/2, Ramp, Texture6,
    /// RNM Volume, Environment, Texture8). null = the slot is unused.
    /// </summary>
    public string?[] TexturePaths = new string?[13];

    public List<ShaderSetupEntry> ShaderSetup { get; } = new();
}

/// <summary>One ShaderSetupItem (8 bytes): a name/flag-value pair.</summary>
public readonly struct ShaderSetupEntry
{
    public ShaderSetupEntry(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

/// <summary>
/// Vertex terrain.bdf (32 bytes):
///   [+0]  PosX, PosY, PosZ — world coords (float32 × 3, 0..1024)
///   [+12] Uv1U, Uv1V      — UV1 as a HALF-FLOAT pair (2×uint16), large values (tiling)
///   [+16] Uv2U, Uv2V      — UV2 as INT16, scaled ×32767 (i.e. value/32767f = 0..1)
///   [+20] Normal          — a packed vector, 1×uint32 (see below)
///   [+24] Tangent         — a packed vector, 1×uint32
///   [+28] Binormal        — a packed vector, 1×uint32
///
/// [+20..32) packs three signed 10-bit fields per uint32 (bits[0..9],
/// [10..19], [20..29], divisor 51.1 — see TerrainLayout.UnpackTbn), not
/// three float32 normals. Confirmed by direct analysis of all 56 vanilla
/// maps (9,073,570 vertices): reading the field as float32 gives zero
/// negative values across 27 million components, an average |v| of 0.457,
/// and only 8 distinct high-byte values — statistically impossible for
/// real float normals. Reading it as packed 10:10:10 instead produces
/// three unit-length (|N|=1.0014, |T|=1.0013, |B|=1.0013) and mutually
/// orthogonal (|N·T|=0.045, |N·B|=0.066, |T·B|=0.008) vectors, and Normal
/// matches an independently computed winding-based normal to a median
/// error of 0.51°, Tangent matches a UV1-derived tangent to 0.43°.
///
/// These are exactly the three attributes (NORMAL/TANGENT/BINORMAL)
/// declared in the game shader's (effects/terrain.hlsl) VS_INPUT — they
/// fit into 12 bytes only because they're packed. Writing plain float32
/// here instead produces a garbage TBN basis in-game: RNM diffuse is
/// unaffected (it samples tsnormal from the normal map), but specular/
/// environment reflections — computed from reflView, which derives from
/// this basis — get random, incorrect highlights.
/// </summary>
public struct TerrainVertex
{
    public float PosX, PosY, PosZ;
    public float Uv1U, Uv1V;       // half-float
    public float Uv2U, Uv2V;       // int16 / 32767

    // Decoded from the packed 10:10:10 fields at [+20..32) - see TerrainLayout.
    public float NormalX, NormalY, NormalZ;
    public float TangentX, TangentY, TangentZ;
    public float BinormalX, BinormalY, BinormalZ;

    /// <summary>The three raw uint32 words exactly as they appeared in the
    /// file, kept so re-serializing a parsed map is byte-identical (quantizing
    /// back from the decoded floats would not be). Only meaningful when
    /// <see cref="HasPackedTbn"/> is true.</summary>
    public uint PackedNormal, PackedTangent, PackedBinormal;

    /// <summary>True for vertices read from a real file (raw words preserved).
    /// False for procedurally built vertices, whose words get packed from
    /// Normal/Tangent/Binormal at serialize time.</summary>
    public bool HasPackedTbn;
}

/// <summary>Triangle (12 bytes): 3 uint32 indices into <see cref="TerrainModel.Vertices"/>.</summary>
public struct TerrainTriangle
{
    public uint V0, V1, V2;
    // Compatibility aliases.
    public uint A { get => V0; set => V0 = value; }
    public uint B { get => V1; set => V1 = value; }
    public uint C { get => V2; set => V2 = value; }
}
}
