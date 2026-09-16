using System;
using System.Collections.Generic;
using System.Text;
using BdfIO.Core;
using BdfIO.Formats.Terrain;

namespace BdfIO.Formats.Decals
{
/// <summary>
/// Rebuilds the decals EDH/EDB spatial block and the geometry permutation it
/// describes. Decals use the same 108-byte EDH and 56-byte EDB node layout as
/// terrain.bdf; only their render vertex layout differs.
/// </summary>
public static class DecalsSpatialBuilder
{
    private const int EdhSize = TerrainLayout.EdhSize;
    private const int EdbSize = TerrainLayout.EdbSize;
    private const int MinPolysPerLeaf = EdbTreeBuilder.DefaultMinPolysPerLeaf;
    private const int BspVersion1 = 200;
    private const int BspVersion2 = 30;
    private const float Hc = 200f;

    public static void Rebuild(DecalsModel model, bool useVanillaCandidateSplit = true)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (model.Vertices.Count == 0 || model.Indices.Count == 0)
        {
            model.Vertices.Clear();
            model.Indices.Clear();
            model.ExtraDataRaw = Array.Empty<byte>();
            model.ExtraDataOffsetTable = Array.Empty<uint>();
            return;
        }
        if (model.Indices.Count % 3 != 0)
            throw new InvalidOperationException($"Decal index count {model.Indices.Count} is not divisible by three.");

        var canonicalVertices = new List<DecalVertex>();
        var canonicalByValue = new Dictionary<DecalVertexKey, int>();
        var sourceToCanonical = new int[model.Vertices.Count];
        for (var i = 0; i < model.Vertices.Count; i++)
        {
            var vertex = model.Vertices[i];
            var key = new DecalVertexKey(vertex);
            if (!canonicalByValue.TryGetValue(key, out var canonicalIndex))
            {
                canonicalIndex = canonicalVertices.Count;
                canonicalByValue.Add(key, canonicalIndex);
                canonicalVertices.Add(vertex);
            }
            sourceToCanonical[i] = canonicalIndex;
        }

        var proxyVertices = new TerrainVertex[canonicalVertices.Count];
        for (var i = 0; i < canonicalVertices.Count; i++)
        {
            var source = canonicalVertices[i];
            proxyVertices[i] = new TerrainVertex
            {
                PosX = source.PositionX,
                PosY = source.PositionY,
                PosZ = source.PositionZ,
                // EdbTreeBuilder deduplicates terrain vertices by selected
                // semantic fields. A canonical decal-vertex ID in UV1 keeps
                // all 19 decal floats distinct while partitioning by position.
                Uv1U = i,
                Uv1V = 0f,
                Uv2U = 0f,
                Uv2V = 0f,
                NormalX = 0f,
                NormalY = 1f,
                NormalZ = 0f
            };
        }

        var proxyTriangles = new TerrainTriangle[model.Indices.Count / 3];
        for (var triangle = 0; triangle < proxyTriangles.Length; triangle++)
        {
            var a = ValidateIndex(model.Indices[triangle * 3], sourceToCanonical.Length, triangle, 0);
            var b = ValidateIndex(model.Indices[triangle * 3 + 1], sourceToCanonical.Length, triangle, 1);
            var c = ValidateIndex(model.Indices[triangle * 3 + 2], sourceToCanonical.Length, triangle, 2);
            proxyTriangles[triangle] = new TerrainTriangle
            {
                V0 = (uint)sourceToCanonical[a],
                V1 = (uint)sourceToCanonical[b],
                V2 = (uint)sourceToCanonical[c]
            };
        }

        var built = EdbTreeBuilder.Build(
            proxyVertices,
            proxyTriangles,
            0,
            0,
            proxyVertices.Length,
            proxyTriangles.Length,
            MinPolysPerLeaf,
            useVanillaCandidateSplit,
            Hc);

        model.Vertices.Clear();
        foreach (var proxy in built.Vertices)
        {
            var canonicalIndex = checked((int)Math.Round(proxy.Uv1U));
            if ((uint)canonicalIndex >= (uint)canonicalVertices.Count)
                throw new InvalidOperationException($"BSP builder returned invalid decal vertex ID {canonicalIndex}.");
            model.Vertices.Add(canonicalVertices[canonicalIndex]);
        }

        model.Indices.Clear();
        foreach (var triangle in built.Triangles)
        {
            model.Indices.Add(checked((int)triangle.V0));
            model.Indices.Add(checked((int)triangle.V1));
            model.Indices.Add(checked((int)triangle.V2));
        }

        BuildSpatialBlock(model, built.Nodes);
    }

    private static void BuildSpatialBlock(DecalsModel model, IReadOnlyList<EdbNode> nodes)
    {
        if (nodes.Count == 0)
            throw new InvalidOperationException("BSP builder returned no EDB nodes for non-empty decal geometry.");

        var extraStart = 16
            + Encoding.ASCII.GetByteCount(model.DiffuseAtlasPath ?? string.Empty) + 1
            + Encoding.ASCII.GetByteCount(model.NormalAtlasPath ?? string.Empty) + 1;
        var rootAbsolute = checked(extraStart + EdhSize);
        var payloadLength = checked(EdhSize + nodes.Count * EdbSize);
        var padding = (32 - ((extraStart + payloadLength) & 31)) & 31;
        var writer = new BdfWriter(BdfPlatform.Pc);

        writer.WriteInt32(BspVersion1);
        writer.WriteInt32(BspVersion2);
        writer.WriteFloat(Hc);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        writer.WriteUInt32((uint)rootAbsolute);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        WriteAbsentVector(writer);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        WriteAbsentVector(writer);
        writer.WriteInt32(0);
        writer.WriteInt32(0);
        WriteAbsentVector(writer);

        var root = nodes[0];
        writer.WriteFloat((root.BboxMin[0] + root.BboxMax[0]) * 0.5f);
        writer.WriteFloat((root.BboxMin[1] + root.BboxMax[1]) * 0.5f);
        writer.WriteFloat((root.BboxMin[2] + root.BboxMax[2]) * 0.5f);

        foreach (var node in nodes)
        {
            WriteVector(writer, node.BboxMin);
            WriteVector(writer, node.BboxMax);
            writer.WriteUInt32(node.IsLeaf ? 1u : 0u);
            writer.WriteUInt32(node.IsLeaf ? 1u : 0u);
            writer.WriteUInt32(node.StartVertex);
            writer.WriteUInt32(node.EndVertex > 0 ? node.EndVertex - 1 : 0);
            writer.WriteUInt32(node.StartPoly * 3);
            writer.WriteUInt32(node.PolyCount);
            writer.WriteUInt32(ResolveChild(node.Child1Index, rootAbsolute, nodes.Count));
            writer.WriteUInt32(ResolveChild(node.Child2Index, rootAbsolute, nodes.Count));
        }
        for (var i = 0; i < padding; i++) writer.WriteByte(0);

        var relocations = new List<uint>(nodes.Count * 2 + 10);
        for (var i = 0; i < nodes.Count; i++)
        {
            var nodeRelative = EdhSize + i * EdbSize;
            relocations.Add((uint)(nodeRelative + 48));
            relocations.Add((uint)(nodeRelative + 52));
        }
        relocations.Add(32);
        relocations.Add(44); relocations.Add(48); relocations.Add(52);
        relocations.Add(64); relocations.Add(68); relocations.Add(72);
        relocations.Add(84); relocations.Add(88); relocations.Add(92);

        model.ExtraDataRaw = writer.ToArray();
        model.ExtraDataOriginalStart = extraStart;
        model.ExtraDataOffsetTable = relocations.ToArray();
    }

    private static int ValidateIndex(int index, int count, int triangle, int corner)
    {
        if ((uint)index >= (uint)count)
            throw new InvalidOperationException($"Decal triangle {triangle}, corner {corner} references vertex {index}, count {count}.");
        return index;
    }

    private static uint ResolveChild(int? childIndex, int rootAbsolute, int nodeCount)
    {
        if (!childIndex.HasValue) return uint.MaxValue;
        if ((uint)childIndex.Value >= (uint)nodeCount)
            throw new InvalidOperationException($"EDB child index {childIndex.Value} is outside node count {nodeCount}.");
        return checked((uint)(rootAbsolute + childIndex.Value * EdbSize));
    }

    private static float Component(float[] values, int index) => values != null && index < values.Length ? values[index] : 0f;

    private static void WriteAbsentVector(BdfWriter writer)
    {
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteUInt32(uint.MaxValue);
        writer.WriteUInt32(uint.MaxValue);
    }

    private static void WriteVector(BdfWriter writer, float[] values)
    {
        writer.WriteFloat(Component(values, 0));
        writer.WriteFloat(Component(values, 1));
        writer.WriteFloat(Component(values, 2));
    }

    private readonly struct DecalVertexKey : IEquatable<DecalVertexKey>
    {
        private readonly int[] _bits;

        public DecalVertexKey(DecalVertex vertex)
        {
            _bits = new int[19];
            var cursor = 0;
            Add(vertex.PositionX, _bits, ref cursor); Add(vertex.PositionY, _bits, ref cursor); Add(vertex.PositionZ, _bits, ref cursor);
            Add(vertex.TexCoordX, _bits, ref cursor); Add(vertex.TexCoordY, _bits, ref cursor); Add(vertex.TexCoordZ, _bits, ref cursor);
            Add(vertex.ScaleOffsetX, _bits, ref cursor); Add(vertex.ScaleOffsetY, _bits, ref cursor); Add(vertex.ScaleOffsetZ, _bits, ref cursor); Add(vertex.ScaleOffsetW, _bits, ref cursor);
            Add(vertex.TangentXform0X, _bits, ref cursor); Add(vertex.TangentXform0Y, _bits, ref cursor); Add(vertex.TangentXform0Z, _bits, ref cursor);
            Add(vertex.TangentXform1X, _bits, ref cursor); Add(vertex.TangentXform1Y, _bits, ref cursor); Add(vertex.TangentXform1Z, _bits, ref cursor);
            Add(vertex.TangentXform2X, _bits, ref cursor); Add(vertex.TangentXform2Y, _bits, ref cursor); Add(vertex.TangentXform2Z, _bits, ref cursor);
        }

        public bool Equals(DecalVertexKey other)
        {
            if (_bits == null || other._bits == null) return _bits == other._bits;
            for (var i = 0; i < _bits.Length; i++)
                if (_bits[i] != other._bits[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is DecalVertexKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                for (var i = 0; i < _bits.Length; i++) hash = hash * 31 + _bits[i];
                return hash;
            }
        }

        private static void Add(float value, int[] destination, ref int cursor) => destination[cursor++] = BitConverter.SingleToInt32Bits(value);
    }
}
}
