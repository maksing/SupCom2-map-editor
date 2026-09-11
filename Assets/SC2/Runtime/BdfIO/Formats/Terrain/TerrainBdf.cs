using BdfIO.Core;
using System;
using System.Collections.Generic;

namespace BdfIO.Formats.Terrain
{
/// <summary>
/// Parse/Serialize terrain.bdf.
///
/// Serialize guarantees:
///  - <c>OffsetTable</c> matches the original POSITIONALLY (length and every
///    entry) — FileHeader/EDH/forest/Materials/ShaderSetup/GeometryHeader
///    positions don't depend on the string pool's order (see below).
///  - <c>Data.Length</c> matches the original (same string set in the pool,
///    with no deduplication — the total size doesn't depend on order).
///  - Bytes [0, materialOffset) and [geometryHeaderStart, end) match the
///    original byte-for-byte (shape/BSP/geometry are unchanged).
///  - Bytes inside [materialOffset, geometryHeaderStart) may differ from
///    the original (a different string order in the pool → different
///    pointer values), but re-parsing them produces a SEMANTICALLY
///    identical <see cref="TerrainModel"/> (the same strings).
/// </summary>
public static class TerrainBdf
{
    public enum StringRefKind
    {
        MaterialName,
        ShaderFile,
        ShaderPrefix,
        State,
        TexturePath,
        ShaderSetupName,
        ShaderSetupValue
    }

    public readonly struct StringRef
    {
        public StringRef(StringRefKind kind, int materialIndex, int subIndex = 0)
        {
            Kind = kind;
            MaterialIndex = materialIndex;
            SubIndex = subIndex;
        }

        public StringRefKind Kind { get; }
        public int MaterialIndex { get; }
        public int SubIndex { get; }
    }

    /// <summary>
    /// Align32Min8: guarantees at least 8 bytes of padding after the
    /// OffsetTable — exactly what TrailingData (GeometryHeaderStart, see
    /// SerializeToContainer) needs. Plain Align32 could produce 0..7 bytes,
    /// leaving nowhere to physically write those 8 required bytes — Parse
    /// would then be unable to find the start of geometry for procedurally
    /// generated models (where OffsetTable.Length differs from existing
    /// maps, and the remainder mod 32 can be anything).
    /// </summary>
    public static BdfPadding Padding => BdfPadding.Align32Min8;

    /// <summary>
    /// Rebuilds the BSP forest for the given layer AFTER its geometry
    /// changes (e.g. TransformLayer). The algorithm matches the reference
    /// Blender add-on SupCom2-TerrainExporter: first the BSP tree is built
    /// from the layer's source triangles, THEN the layer's geometry is
    /// repacked by tree leaf (deduplicating vertices). So the layer's
    /// vertex/triangle count can change after this call — every subsequent
    /// layer (its CumulativeVertices/CumulativeIndices and the vertex
    /// indices of its own triangles) SHIFTS accordingly. Call before Serialize.
    /// </summary>
    public static void RebuildLayerBsp(TerrainModel model, int layerIndex,
        int minPolysPerLeaf = EdbTreeBuilder.DefaultMinPolysPerLeaf,
        bool useVanillaCandidateSplit = false)
    {
        if (layerIndex < 0 || layerIndex >= model.Layers.Count) return;
        var layer = model.Layers[layerIndex];

        int vertStart = layer.CumulativeVertices;
        int triStart  = layer.CumulativeIndices / 3;
        int vertEnd = layerIndex + 1 < model.Layers.Count
            ? model.Layers[layerIndex + 1].CumulativeVertices
            : model.Vertices.Count;
        int triEnd = layerIndex + 1 < model.Layers.Count
            ? model.Layers[layerIndex + 1].CumulativeIndices / 3
            : model.Triangles.Count;

        int oldVertCount = vertEnd - vertStart;
        int oldTriCount  = triEnd  - triStart;

        // Remove exact-duplicate triangles (same 3 world positions, any
        // winding/vertex-index) BEFORE building the BSP tree — see
        // DeduplicateLayerTriangles's doc comment for why. Must happen here,
        // per-layer/per-material and pre-split: confirmed in-game (construction-
        // effect banding on SC2_D3_Eclipse) that duplicate geometry is harmless
        // on its own (a single-leaf-per-layer rebuild keeps every duplicate and
        // shows no artifact), but becomes visible whenever a duplicate pair's
        // two copies land in two DIFFERENT BSP leaves — which happens
        // unavoidably once a layer with duplicates gets subdivided into more
        // than one leaf. Deduplicating here removes the precondition entirely,
        // independent of which split algorithm/leaf count is subsequently used.
        var srcTris = model.Triangles.GetRange(triStart, oldTriCount);
        var dedupedTris = DeduplicateLayerTriangles(srcTris, model.Vertices, vertStart);
        if (dedupedTris.Count != srcTris.Count)
        {
            model.Triangles.RemoveRange(triStart, oldTriCount);
            model.Triangles.InsertRange(triStart, dedupedTris);
            int dtRemoved = (dedupedTris.Count - srcTris.Count) * 3;
            for (var i = layerIndex + 1; i < model.Layers.Count; i++)
                model.Layers[i].CumulativeIndices += dtRemoved;
            oldTriCount = dedupedTris.Count;
        }

        var result = TerrainBspBuilder.Rebuild(
            layer, model.Vertices, model.Triangles,
            vertStart, triStart, oldVertCount, oldTriCount, minPolysPerLeaf, useVanillaCandidateSplit);

        int newVertCount = result.Vertices.Count;
        int newTriCount  = result.Triangles.Count;
        int dv = newVertCount - oldVertCount;
        int dt = (newTriCount - oldTriCount) * 3;

        // Replace the layer's geometry with the newly reordered version.
        model.Vertices.RemoveRange(vertStart, oldVertCount);
        model.Vertices.InsertRange(vertStart, result.Vertices);
        model.Triangles.RemoveRange(triStart, oldTriCount);
        model.Triangles.InsertRange(triStart, result.Triangles);

        // Shift CumulativeVertices/Indices of every SUBSEQUENT layer.
        for (var i = layerIndex + 1; i < model.Layers.Count; i++)
        {
            model.Layers[i].CumulativeVertices += dv;
            model.Layers[i].CumulativeIndices  += dt;
        }
        // Indices inside the BSP nodes themselves are already layer-local
        // (0-based from the layer's start) — no conversion into other
        // layers is needed, since each layer's triangles use their OWN
        // local vertex indices.
    }

    /// <summary>
    /// Rebuilds the BSP for every layer of the model in order. Since each
    /// RebuildLayerBsp call can change a layer's vertex/triangle count (and
    /// thereby shift the CumulativeVertices/Indices of later layers), it's
    /// important to go strictly in increasing index order — the next layer
    /// is always processed with already-updated (shifted) offsets.
    /// </summary>
    /// <param name="minPolysPerLeaf">Subdivision stops once a node holds this
    /// many triangles or fewer. Pass <see cref="SingleLeafPerLayer"/> to emit
    /// exactly one leaf per layer (no subdivision at all).</param>
    /// <param name="useVanillaCandidateSplit">EXPERIMENTAL — see
    /// TerrainBspBuilder.FindBestVanillaCandidateSplit's doc comment for
    /// exactly what this does and does not reproduce from the real vanilla
    /// algorithm. Ignored (no effect) when minPolysPerLeaf is
    /// SingleLeafPerLayer, since there is nothing left to split.</param>
    public static void RebuildAllBsp(TerrainModel model,
        int minPolysPerLeaf = EdbTreeBuilder.DefaultMinPolysPerLeaf,
        bool useVanillaCandidateSplit = false)
    {
        for (var i = 0; i < model.Layers.Count; i++)
            RebuildLayerBsp(model, i, minPolysPerLeaf, useVanillaCandidateSplit);
    }

    /// <summary>Sentinel for <see cref="RebuildAllBsp"/>: no node ever holds
    /// more triangles than this, so the root is always a leaf and each layer
    /// ends up with exactly one EDB node.</summary>
    public const int SingleLeafPerLayer = int.MaxValue;

    /// <summary>
    /// Drops triangles that are exact geometric duplicates of an
    /// earlier-appearing triangle in the same layer — same 3 world vertex
    /// positions (rounded to 1e-3 units), regardless of winding order or
    /// which vertex indices/UV/TBN they reference. Confirmed root cause on
    /// SC2_D3_Eclipse (a fully Blender-authored import): the reference
    /// Blender exporter's <c>_build_bsp_tree_recursive</c> duplicates any
    /// triangle straddling a split plane into BOTH branches rather than
    /// clipping it, so a from-Blender terrain.bdf can carry thousands of
    /// such pairs per material layer. <paramref name="layerLocalTris"/> uses
    /// layer-local vertex indices (0-based from <paramref name="vertStart"/>),
    /// matching every other triangle list in this file.
    /// </summary>
    private static List<TerrainTriangle> DeduplicateLayerTriangles(
        List<TerrainTriangle> layerLocalTris,
        List<TerrainVertex> allVertices,
        int vertStart)
    {
        var seen = new HashSet<TriKey>();
        var result = new List<TerrainTriangle>(layerLocalTris.Count);
        foreach (var t in layerLocalTris)
        {
            var p0 = allVertices[vertStart + (int)t.V0];
            var p1 = allVertices[vertStart + (int)t.V1];
            var p2 = allVertices[vertStart + (int)t.V2];
            if (seen.Add(TriKey.Canonical(p0, p1, p2)))
                result.Add(t);
        }
        return result;
    }

    /// <summary>Position-only canonical key for a triangle: its 3 vertex
    /// world positions, rounded to 1e-3 units and sorted, so the same
    /// physical triangle hashes identically regardless of winding order or
    /// which of its 3 vertex records (UV/normal/tangent can differ between
    /// the front/back copies made by the Blender exporter's straddle
    /// duplication) it was built from.</summary>
    private readonly struct TriKey : IEquatable<TriKey>
    {
        private readonly long _a0, _a1, _a2, _b0, _b1, _b2, _c0, _c1, _c2;

        private TriKey(long a0, long a1, long a2, long b0, long b1, long b2, long c0, long c1, long c2)
        {
            _a0 = a0; _a1 = a1; _a2 = a2;
            _b0 = b0; _b1 = b1; _b2 = b2;
            _c0 = c0; _c1 = c1; _c2 = c2;
        }

        public static TriKey Canonical(TerrainVertex p0, TerrainVertex p1, TerrainVertex p2)
        {
            const float scale = 1000f; // round to 1e-3 world units
            var pts = new (long x, long y, long z)[3]
            {
                (RoundL(p0.PosX, scale), RoundL(p0.PosY, scale), RoundL(p0.PosZ, scale)),
                (RoundL(p1.PosX, scale), RoundL(p1.PosY, scale), RoundL(p1.PosZ, scale)),
                (RoundL(p2.PosX, scale), RoundL(p2.PosY, scale), RoundL(p2.PosZ, scale)),
            };
            Array.Sort(pts, (a, b) =>
            {
                var c = a.x.CompareTo(b.x);
                if (c != 0) return c;
                c = a.y.CompareTo(b.y);
                return c != 0 ? c : a.z.CompareTo(b.z);
            });
            return new TriKey(
                pts[0].x, pts[0].y, pts[0].z,
                pts[1].x, pts[1].y, pts[1].z,
                pts[2].x, pts[2].y, pts[2].z);
        }

        private static long RoundL(float v, float scale) => (long)Math.Round(v * scale);

        public bool Equals(TriKey o) =>
            _a0 == o._a0 && _a1 == o._a1 && _a2 == o._a2 &&
            _b0 == o._b0 && _b1 == o._b1 && _b2 == o._b2 &&
            _c0 == o._c0 && _c1 == o._c1 && _c2 == o._c2;

        public override bool Equals(object? o) => o is TriKey k && Equals(k);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(_a0); h.Add(_a1); h.Add(_a2);
            h.Add(_b0); h.Add(_b1); h.Add(_b2);
            h.Add(_c0); h.Add(_c1); h.Add(_c2);
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// Material-only patch: Parse -> patch -> Serialize. Returns the full
    /// BdfContainer (including TrailingData with GeometryHeaderStart).
    /// </summary>
    public static BdfContainer PatchMaterials(
        BdfContainer container, Action<List<TerrainMaterial>> patch)
    {
        var model = Parse(container);
        patch(model.Materials);
        return SerializeToContainer(model, container.Platform);
    }

    // ----------------------------------------------------------------
    // Parse
    // ----------------------------------------------------------------

    public static TerrainModel Parse(BdfContainer container)
    {
        var r = new BdfReader(container.Data, container.Platform);
        var model = new TerrainModel
        {
            Version = r.PeekInt32At(0)
        };

        var layerCount = r.PeekInt32At(4);
        var edhOffset = (int)r.PeekUInt32At(8);
        var materialCount = (int)r.PeekUInt32At(12);
        var materialOffset = (int)r.PeekUInt32At(16);

        var roots = new int[layerCount];
        for (var i = 0; i < layerCount; i++)
        {
            var off = edhOffset + i * TerrainLayout.EdhSize;
            var layer = new TerrainLayer
            {
                BspVersion1 = r.PeekInt32At(off + 0),
                BspVersion2 = r.PeekInt32At(off + 4),
                Hc = r.PeekFloatAt(off + 8),
                CumulativeVertices = r.PeekInt32At(off + 12),
                CumulativeIndices = r.PeekInt32At(off + 16),
                MapCenter = new float[] { r.PeekFloatAt(off + 96), r.PeekFloatAt(off + 100), r.PeekFloatAt(off + 104) }
            };
            roots[i] = (int)r.PeekUInt32At(off + 32);
            model.Layers.Add(layer);
        }

        for (var i = 0; i < layerCount; i++)
        {
            var forestEnd = i + 1 < layerCount ? roots[i + 1] : materialOffset;
            var nodeCount = (forestEnd - roots[i]) / TerrainLayout.EdbSize;

            for (var k = 0; k < nodeCount; k++)
            {
                var off = roots[i] + k * TerrainLayout.EdbSize;
                // The file stores: [+36] = EndVertex-1, [+40] = StartPoly*3
                // (confirmed against the reference exporter and byte-for-byte
                // against a real map: for layer 0's root, [+36]=20315,
                // [+40]=0 -> EndVertex=20316 exactly matches the next
                // layer's CumulativeVertices, and StartPoly=0 makes sense
                // for the root).
                var rawEndVertexMinus1 = r.PeekUInt32At(off + 36);
                var rawStartPolyTimes3 = r.PeekUInt32At(off + 40);
                var node = new EdbNode
                {
                    BboxMin     = new float[] { r.PeekFloatAt(off + 0),  r.PeekFloatAt(off + 4),  r.PeekFloatAt(off + 8) },
                    BboxMax     = new float[] { r.PeekFloatAt(off + 12), r.PeekFloatAt(off + 16), r.PeekFloatAt(off + 20) },
                    IsLeaf      = r.PeekUInt32At(off + 24) != 0,
                    StartVertex = r.PeekUInt32At(off + 32),  // [+32]
                    EndVertex   = rawEndVertexMinus1 + 1,    // decoded from [+36]
                    StartPoly   = rawStartPolyTimes3 / 3,    // decoded from [+40]
                    PolyCount   = r.PeekUInt32At(off + 44),  // [+44]
                    Child1Index = ChildIndex(r.PeekUInt32At(off + 48), roots[i]),
                    Child2Index = ChildIndex(r.PeekUInt32At(off + 52), roots[i])
                };
                model.Layers[i].Nodes.Add(node);
            }
        }

        for (var i = 0; i < materialCount; i++)
        {
            var off = materialOffset + i * TerrainLayout.MaterialSize;
            var mat = new TerrainMaterial
            {
                Name = ReadString(r, r.PeekUInt32At(off + 0)) ?? "",
                ShaderFile = ReadOptionalString(r, r.PeekUInt32At(off + 4)),
                ShaderPrefix = ReadOptionalString(r, r.PeekUInt32At(off + 8)),
                State = ReadOptionalString(r, r.PeekUInt32At(off + 20)),
                TextureScroll = new float[] { r.PeekFloatAt(off + 24), r.PeekFloatAt(off + 28) }
            };

            for (var j = 0; j < 13; j++)
                mat.TexturePaths[j] = ReadOptionalString(r, r.PeekUInt32At(off + 32 + 4 * j));

            var ssCount = (int)r.PeekUInt32At(off + 12);
            var ssOffset = (int)r.PeekUInt32At(off + 16);
            for (var j = 0; j < ssCount; j++)
            {
                var itemOff = ssOffset + j * TerrainLayout.ShaderSetupItemSize;
                mat.ShaderSetup.Add(new ShaderSetupEntry(
                    ReadString(r, r.PeekUInt32At(itemOff + 0)) ?? "",
                    ReadString(r, r.PeekUInt32At(itemOff + 4)) ?? ""));
            }

            model.Materials.Add(mat);
        }

        // GeometryHeader: OffsetTable[-2] = GeomHeader+8, OffsetTable[-1] = GeomHeader+16
        // => geometryHeaderStart = OffsetTable[-1] - 16
        var geometryHeaderStart = (int)container.OffsetTable[^1] - 16;

        // GeometryHeader (20 bytes = 5 × uint32), confirmed byte-for-byte:
        //   [+0]  uint32 = 3            — unknown (a version?)
        //   [+4]  uint32 = vertCount    — vertex count
        //   [+8]  uint32 = 20           — header size = vertex data offset from geomStart
        //   [+12] uint32 = idxCount     — index count
        //   [+16] uint32 = indexOffset  — index data offset from geomStart (a large value)
        var totalVertices = (int)r.PeekUInt32At(geometryHeaderStart + 4);
        var vertexRelOff  = (int)r.PeekUInt32At(geometryHeaderStart + 8);   // = 20
        var totalIndices  = (int)r.PeekUInt32At(geometryHeaderStart + 12);
        var indexRelOff   = (int)r.PeekUInt32At(geometryHeaderStart + 16);  // = large offset

        var vertexStart = geometryHeaderStart + vertexRelOff;
        var indexStart  = geometryHeaderStart + indexRelOff;

        for (var i = 0; i < totalVertices; i++)
        {
            var off = vertexStart + i * TerrainLayout.VertexSize;

            // [+20..32) — three packed vectors (Normal/Tangent/Binormal),
            // not three float32 values. See TerrainVertex's own doc comment
            // for the reasoning and measurements. The raw words are kept
            // alongside the decoded values so re-serializing a parsed map
            // stays byte-identical.
            var packedNormal   = r.PeekUInt32At(off + 20);
            var packedTangent  = r.PeekUInt32At(off + 24);
            var packedBinormal = r.PeekUInt32At(off + 28);
            TerrainLayout.UnpackTbn(packedNormal, out var nx, out var ny, out var nz);
            TerrainLayout.UnpackTbn(packedTangent, out var tx, out var ty, out var tz);
            TerrainLayout.UnpackTbn(packedBinormal, out var bx, out var by, out var bz);

            model.Vertices.Add(new TerrainVertex
            {
                PosX     = r.PeekFloatAt(off + 0),
                PosY     = r.PeekFloatAt(off + 4),
                PosZ     = r.PeekFloatAt(off + 8),
                Uv1U     = r.PeekHalfAt(off + 12),                    // half-float
                Uv1V     = r.PeekHalfAt(off + 14),
                Uv2U     = r.PeekInt16At(off + 16) / 32767f,          // int16 / 32767
                Uv2V     = r.PeekInt16At(off + 18) / 32767f,
                NormalX  = nx, NormalY  = ny, NormalZ  = nz,
                TangentX = tx, TangentY = ty, TangentZ = tz,
                BinormalX = bx, BinormalY = by, BinormalZ = bz,
                PackedNormal = packedNormal,
                PackedTangent = packedTangent,
                PackedBinormal = packedBinormal,
                HasPackedTbn = true,
            });
        }

        for (var i = 0; i < totalIndices / 3; i++)
        {
            var off = indexStart + i * TerrainLayout.TriangleSize;
            model.Triangles.Add(new TerrainTriangle
            {
                A = r.PeekUInt32At(off + 0),
                B = r.PeekUInt32At(off + 4),
                C = r.PeekUInt32At(off + 8)
            });
        }

        return model;
    }

    private static int? ChildIndex(uint rawOffset, int forestStart) =>
        rawOffset == 0xFFFFFFFF ? null : (int)((rawOffset - forestStart) / TerrainLayout.EdbSize);

    private static string? ReadOptionalString(BdfReader r, uint ptr) =>
        ptr == 0xFFFFFFFF ? null : r.ReadCStringAt(ptr);

    private static string? ReadString(BdfReader r, uint ptr) =>
        ptr == 0xFFFFFFFF ? null : r.ReadCStringAt(ptr);

    // ----------------------------------------------------------------
    // Serialize
    // ----------------------------------------------------------------

    public static (byte[] Data, uint[] OffsetTable) Serialize(TerrainModel model, BdfPlatform platform)
    {
        var layout = TerrainLayout.Compute(model);
        var w = new BdfWriter(platform);

        // FileHeader_ExtraMaterials (20 bytes)
        w.WriteInt32(model.Version);
        w.WriteInt32(model.Layers.Count);
        w.WriteUInt32((uint)layout.EdhOffset);
        w.WriteUInt32((uint)model.Materials.Count);
        w.WriteUInt32((uint)layout.MaterialOffset);

        // ExtraDataHeader[layerCount] (108 bytes each)
        for (var i = 0; i < model.Layers.Count; i++)
        {
            var layer = model.Layers[i];
            w.WriteInt32(layer.BspVersion1);
            w.WriteInt32(layer.BspVersion2);
            w.WriteFloat(layer.Hc);
            w.WriteInt32(layer.CumulativeVertices);
            w.WriteInt32(layer.CumulativeIndices);
            w.WriteInt32(0); w.WriteInt32(0); w.WriteInt32(0); // reserved[3]
            w.WriteUInt32((uint)layout.ForestStart[i]); // rootBlockOffset
            w.WriteInt32(0); w.WriteInt32(0); // reserved2[2]
            w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); // filler1[3]
            w.WriteInt32(0); w.WriteInt32(0); // reserved3[2]
            w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); // filler2[3]
            w.WriteInt32(0); w.WriteInt32(0); // reserved4[2]
            w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); w.WriteUInt32(0xFFFFFFFF); // filler3[3]
            w.WriteFloat(layer.MapCenter[0]); w.WriteFloat(layer.MapCenter[1]); w.WriteFloat(layer.MapCenter[2]);
        }

        // Forests of ExtraDataBlock[] in sequence by layer
        for (var i = 0; i < model.Layers.Count; i++)
        {
            var layer = model.Layers[i];
            foreach (var node in layer.Nodes)
            {
                // [+0..11]  BboxMin
                w.WriteFloat(node.BboxMin[0]); w.WriteFloat(node.BboxMin[1]); w.WriteFloat(node.BboxMin[2]);
                // [+12..23] BboxMax
                w.WriteFloat(node.BboxMax[0]); w.WriteFloat(node.BboxMax[1]); w.WriteFloat(node.BboxMax[2]);
                // [+24] IsLeaf
                w.WriteUInt32(node.IsLeaf ? 1u : 0u);
                // [+28] IsLeafCopy — duplicates IsLeaf (confirmed by the
                // reference exporter: is_leaf_val is written twice in a row)
                w.WriteUInt32(node.IsLeaf ? 1u : 0u);
                // [+32] StartVertex
                w.WriteUInt32(node.StartVertex);
                // [+36] EndVertex - 1 (the file stores the last valid index,
                // not an exclusive end — confirmed by the reference exporter
                // and byte-for-byte against a real map)
                w.WriteUInt32(node.EndVertex > 0 ? node.EndVertex - 1 : 0);
                // [+40] StartPoly * 3 (the file stores the index as an
                // offset into the int32 index buffer, not a triangle index)
                w.WriteUInt32(node.StartPoly * 3);
                // [+44] PolyCount
                w.WriteUInt32(node.PolyCount);
                // [+48] Child1, [+52] Child2 (absolute offsets in dec)
                w.WriteUInt32(ResolveChild(node.Child1Index, layout.ForestStart[i]));
                w.WriteUInt32(ResolveChild(node.Child2Index, layout.ForestStart[i]));
            }
        }

        // MaterialData[materialCount] (84 bytes each)
        for (var i = 0; i < model.Materials.Count; i++)
        {
            var mat = model.Materials[i];
            w.WriteUInt32(layout.StringOffsets[new StringRef(StringRefKind.MaterialName, i)]);
            w.WriteUInt32(ResolveOptionalString(layout, StringRefKind.ShaderFile, i, mat.ShaderFile));
            w.WriteUInt32(ResolveOptionalString(layout, StringRefKind.ShaderPrefix, i, mat.ShaderPrefix));
            w.WriteUInt32((uint)mat.ShaderSetup.Count);
            w.WriteUInt32(mat.ShaderSetup.Count > 0 ? (uint)layout.ShaderSetupStart[i] : 0xFFFFFFFF);
            w.WriteUInt32(ResolveOptionalString(layout, StringRefKind.State, i, mat.State));
            w.WriteFloat(mat.TextureScroll[0]);
            w.WriteFloat(mat.TextureScroll[1]);
            for (var j = 0; j < 13; j++)
                w.WriteUInt32(ResolveOptionalString(layout, StringRefKind.TexturePath, i, mat.TexturePaths[j], j));
        }

        // ShaderSetupItem[] by material, in sequence (8 bytes each)
        for (var i = 0; i < model.Materials.Count; i++)
        {
            var mat = model.Materials[i];
            for (var j = 0; j < mat.ShaderSetup.Count; j++)
            {
                w.WriteUInt32(layout.StringOffsets[new StringRef(StringRefKind.ShaderSetupName, i, j)]);
                w.WriteUInt32(layout.StringOffsets[new StringRef(StringRefKind.ShaderSetupValue, i, j)]);
            }
        }

        // String pool
        w.WriteBytes(layout.PoolBytes);

        // GeometryHeader (20 bytes = 5 × uint32):
        //   [+0]  uint32 = 3                   — unknown
        //   [+4]  uint32 = vertCount
        //   [+8]  uint32 = GeometryHeaderSize  — header size = vertex data offset from geomStart
        //   [+12] uint32 = idxCount
        //   [+16] uint32 = indexRelOff         — index data offset from geomStart
        var indexRelOff = (uint)(TerrainLayout.GeometryHeaderSize + model.Vertices.Count * TerrainLayout.VertexSize);
        w.WriteUInt32(3u);
        w.WriteUInt32((uint)model.Vertices.Count);
        w.WriteUInt32((uint)TerrainLayout.GeometryHeaderSize);  // = 20
        w.WriteUInt32((uint)(model.Triangles.Count * 3));
        w.WriteUInt32(indexRelOff);

        // Vertex[totalVertices] (32 bytes): pos(3f) + uv1(2×half) + uv2(2×int16/32767)
        //                                   + packed Normal/Tangent/Binormal (3×uint32)
        foreach (var v in model.Vertices)
        {
            w.WriteFloat(v.PosX); w.WriteFloat(v.PosY); w.WriteFloat(v.PosZ);
            w.WriteHalf(v.Uv1U); w.WriteHalf(v.Uv1V);
            w.WriteInt16((short)System.Math.Round(v.Uv2U * 32767f));
            w.WriteInt16((short)System.Math.Round(v.Uv2V * 32767f));

            // A parsed vertex hands back its RAW words, so re-serializing
            // an imported map stays byte-exact (re-quantizing the decoded
            // floats would introduce drift). A procedurally built vertex
            // gets packed from its own vectors instead.
            if (v.HasPackedTbn)
            {
                w.WriteUInt32(v.PackedNormal);
                w.WriteUInt32(v.PackedTangent);
                w.WriteUInt32(v.PackedBinormal);
            }
            else
            {
                w.WriteUInt32(TerrainLayout.PackTbn(v.NormalX, v.NormalY, v.NormalZ));
                w.WriteUInt32(TerrainLayout.PackTbn(v.TangentX, v.TangentY, v.TangentZ));
                w.WriteUInt32(TerrainLayout.PackTbn(v.BinormalX, v.BinormalY, v.BinormalZ));
            }
        }

        // Index data starts immediately after vertex data (no padding)

        // Triangle[] (12 bytes each)
        foreach (var t in model.Triangles)
        {
            w.WriteUInt32(t.A); w.WriteUInt32(t.B); w.WriteUInt32(t.C);
        }

        var data2 = w.ToArray();
        if (data2.Length != layout.TotalSize)
            throw new InvalidOperationException(
                $"Internal layout mismatch: wrote {data2.Length} bytes, layout predicted {layout.TotalSize}");

        return (data2, BuildOffsetTable(model, layout));
    }

    // Internal helper for PatchMaterials — returns the full container including TrailingData.
    public static BdfContainer SerializeToContainer(TerrainModel model, BdfPlatform platform)
    {
        var (data2, offsetTable) = Serialize(model, platform);
        var layout = TerrainLayout.Compute(model);

        // TrailingData: 8 bytes in the padding between OffsetTable and the
        // zlib block. Confirmed byte-for-byte: [+0]=0x00000000,
        // [+4]=GeometryHeaderStart (LE/BE per platform)
        var trailingData = new byte[8];
        if (platform == BdfPlatform.Pc)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailingData.AsSpan(4, 4), (uint)layout.GeometryHeaderStart);
        else
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(trailingData.AsSpan(4, 4), (uint)layout.GeometryHeaderStart);

        return new BdfContainer
        {
            Platform = platform,
            Data = data2,
            OffsetTable = offsetTable,
            NumFiles = 2,
            TrailingData = trailingData
        };
    }

    private static uint ResolveChild(int? childIndex, int forestStart) =>
        childIndex is { } idx ? (uint)(forestStart + idx * TerrainLayout.EdbSize) : 0xFFFFFFFF;

    private static uint ResolveOptionalString(TerrainLayout layout, StringRefKind kind, int materialIndex, string? value, int subIndex = 0) =>
        value is null ? 0xFFFFFFFF : layout.StringOffsets[new StringRef(kind, materialIndex, subIndex)];

    /// <summary>
    /// Order of strings in the pool (deterministic, see the note on
    /// <see cref="TerrainLayout"/>): by material — name/shaderFile/
    /// shaderPrefix/state/texturePtr[0..12] (non-null only), then by
    /// material — every ShaderSetup name/value pair.
    /// </summary>
    public static IEnumerable<(StringRef Key, string Value)> BuildStringRequests(TerrainModel model)
    {
        for (var i = 0; i < model.Materials.Count; i++)
        {
            var mat = model.Materials[i];
            yield return (new StringRef(StringRefKind.MaterialName, i), mat.Name);
            if (mat.ShaderFile is not null) yield return (new StringRef(StringRefKind.ShaderFile, i), mat.ShaderFile);
            if (mat.ShaderPrefix is not null) yield return (new StringRef(StringRefKind.ShaderPrefix, i), mat.ShaderPrefix);
            if (mat.State is not null) yield return (new StringRef(StringRefKind.State, i), mat.State);
            for (var j = 0; j < 13; j++)
                if (mat.TexturePaths[j] is { } path)
                    yield return (new StringRef(StringRefKind.TexturePath, i, j), path);
        }

        for (var i = 0; i < model.Materials.Count; i++)
        {
            var mat = model.Materials[i];
            for (var j = 0; j < mat.ShaderSetup.Count; j++)
            {
                yield return (new StringRef(StringRefKind.ShaderSetupName, i, j), mat.ShaderSetup[j].Name);
                yield return (new StringRef(StringRefKind.ShaderSetupValue, i, j), mat.ShaderSetup[j].Value);
            }
        }
    }

    /// <summary>Order of OffsetTable entries, confirmed byte-for-byte against a real vanilla map.</summary>
    public static uint[] BuildOffsetTable(TerrainModel model, TerrainLayout layout)
    {
        var table = new List<uint>();

        // A: every forest in sequence, 2 entries per node (child1/child2)
        for (var i = 0; i < model.Layers.Count; i++)
        {
            for (var k = 0; k < model.Layers[i].Nodes.Count; k++)
            {
                var nodeOff = layout.ForestStart[i] + k * TerrainLayout.EdbSize;
                table.Add((uint)(nodeOff + 48));
                table.Add((uint)(nodeOff + 52));
            }
        }

        // B: pointer fields of every ExtraDataHeader in sequence (10 per layer)
        for (var i = 0; i < model.Layers.Count; i++)
        {
            var edh = layout.EdhOffset + i * TerrainLayout.EdhSize;
            table.Add((uint)(edh + 32));
            table.Add((uint)(edh + 44)); table.Add((uint)(edh + 48)); table.Add((uint)(edh + 52));
            table.Add((uint)(edh + 64)); table.Add((uint)(edh + 68)); table.Add((uint)(edh + 72));
            table.Add((uint)(edh + 84)); table.Add((uint)(edh + 88)); table.Add((uint)(edh + 92));
        }

        // C: FileHeader.edhOffset
        table.Add(8);

        // D: by material
        for (var i = 0; i < model.Materials.Count; i++)
        {
            var moff = layout.MaterialOffset + i * TerrainLayout.MaterialSize;
            table.Add((uint)(moff + 0)); table.Add((uint)(moff + 4)); table.Add((uint)(moff + 8));
            table.Add((uint)(moff + 16)); table.Add((uint)(moff + 20));
            for (var j = 0; j < 13; j++) table.Add((uint)(moff + 32 + 4 * j));

            var ssCount = model.Materials[i].ShaderSetup.Count;
            if (ssCount > 0)
            {
                var ssStart = layout.ShaderSetupStart[i];
                for (var j = 0; j < ssCount; j++)
                {
                    table.Add((uint)(ssStart + j * TerrainLayout.ShaderSetupItemSize));
                    table.Add((uint)(ssStart + j * TerrainLayout.ShaderSetupItemSize + 4));
                }
            }
        }

        // E: FileHeader.materialOffset
        table.Add(16);

        // F: pointers to GeomHeader[+8] and GeomHeader[+16] (used by Parse
        // to compute geometryHeaderStart)
        table.Add((uint)(layout.GeometryHeaderStart + 8));   // vertexRelOff field
        table.Add((uint)(layout.GeometryHeaderStart + 16));  // indexRelOff field

        return table.ToArray();
    }
}
}
