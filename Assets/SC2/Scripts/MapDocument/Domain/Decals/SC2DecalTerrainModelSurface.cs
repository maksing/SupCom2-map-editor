using System;
using System.Collections.Generic;
using BdfIO.Formats.Terrain;

namespace SC2.MapEditor.Document
{
    /// <summary>The ground a decal drapes over at export: the FINAL terrain
    /// model's own triangles - the exact mesh the game renders, live plane and
    /// imported terrain.bdf alike, after Simplify. Clipping a decal against the
    /// real triangles (rather than against a resampled height grid) is what
    /// makes it sit exactly on the surface: no second approximate layer, no
    /// dipping under it, no clipping.</summary>
    public static class SC2DecalTerrainModelSurface
    {
        public readonly struct Box
        {
            public readonly float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
            { MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ; }
        }

        /// <param name="skipLayer">Layers to leave out, typically water (a decal
        /// must not also stick to the water surface above a lake floor).</param>
        /// <param name="wanted">World boxes of the placements; a triangle that
        /// touches none of them is dropped early.</param>
        public static List<SC2DecalSurfaceTriangle> Collect(TerrainModel model, Func<TerrainMaterial, bool> skipLayer, IReadOnlyList<Box> wanted)
        {
            var result = new List<SC2DecalSurfaceTriangle>();
            if (model == null || wanted == null || wanted.Count == 0) return result;

            for (var li = 0; li < model.Layers.Count; li++)
            {
                var material = li < model.Materials.Count ? model.Materials[li] : null;
                if (skipLayer != null && skipLayer(material)) continue;

                var vertexBase = model.Layers[li].CumulativeVertices;
                var triangleStart = model.Layers[li].CumulativeIndices / 3;
                var triangleEnd = li + 1 < model.Layers.Count ? model.Layers[li + 1].CumulativeIndices / 3 : model.Triangles.Count;

                for (var t = triangleStart; t < triangleEnd; t++)
                {
                    var tri = model.Triangles[t];
                    var a = model.Vertices[vertexBase + (int)tri.V0];
                    var b = model.Vertices[vertexBase + (int)tri.V1];
                    var c = model.Vertices[vertexBase + (int)tri.V2];
                    if (!TouchesAny(wanted, a, b, c)) continue;
                    result.Add(ToSurfaceTriangle(a, b, c));
                }
            }
            return result;
        }

        private static bool TouchesAny(IReadOnlyList<Box> wanted, TerrainVertex a, TerrainVertex b, TerrainVertex c)
        {
            var minX = Math.Min(a.PosX, Math.Min(b.PosX, c.PosX)); var maxX = Math.Max(a.PosX, Math.Max(b.PosX, c.PosX));
            var minY = Math.Min(a.PosY, Math.Min(b.PosY, c.PosY)); var maxY = Math.Max(a.PosY, Math.Max(b.PosY, c.PosY));
            var minZ = Math.Min(a.PosZ, Math.Min(b.PosZ, c.PosZ)); var maxZ = Math.Max(a.PosZ, Math.Max(b.PosZ, c.PosZ));
            for (var i = 0; i < wanted.Count; i++)
            {
                var w = wanted[i];
                if (maxX < w.MinX || minX > w.MaxX || maxY < w.MinY || minY > w.MaxY || maxZ < w.MinZ || minZ > w.MaxZ) continue;
                return true;
            }
            return false;
        }

        /// <summary>The builder expects Cross(B-A, C-A) to point out of the
        /// surface (toward the decal) - the winding of the decal mesh it emits
        /// comes straight from the source triangle. The model's own winding
        /// convention is not what the builder assumes, so each triangle is
        /// oriented by the vertex normals instead.</summary>
        private static bool HasVector(float x, float y, float z) => x * x + y * y + z * z > 1e-6f;

        private static SC2DecalSurfaceTriangle ToSurfaceTriangle(TerrainVertex a, TerrainVertex b, TerrainVertex c)
        {
            var nx = a.NormalX + b.NormalX + c.NormalX;
            var ny = a.NormalY + b.NormalY + c.NormalY;
            var nz = a.NormalZ + b.NormalZ + c.NormalZ;
            var ux = b.PosX - a.PosX; var uy = b.PosY - a.PosY; var uz = b.PosZ - a.PosZ;
            var wx = c.PosX - a.PosX; var wy = c.PosY - a.PosY; var wz = c.PosZ - a.PosZ;
            var cx = uy * wz - uz * wy; var cy = uz * wx - ux * wz; var cz = ux * wy - uy * wx;

            var normalLength = nx * nx + ny * ny + nz * nz;
            // No usable vertex normals: fall back to "the surface faces up".
            var reference = normalLength > 1e-6f ? cx * nx + cy * ny + cz * nz : cy;
            if (reference < 0f) { var swap = b; b = c; c = swap; }

            var hasFrame = normalLength > 1e-6f && HasVector(a.TangentX, a.TangentY, a.TangentZ) && HasVector(b.TangentX, b.TangentY, b.TangentZ)
                && HasVector(c.TangentX, c.TangentY, c.TangentZ) && HasVector(a.BinormalX, a.BinormalY, a.BinormalZ)
                && HasVector(b.BinormalX, b.BinormalY, b.BinormalZ) && HasVector(c.BinormalX, c.BinormalY, c.BinormalZ);
            return new SC2DecalSurfaceTriangle
            {
                AX = a.PosX, AY = a.PosY, AZ = a.PosZ,
                BX = b.PosX, BY = b.PosY, BZ = b.PosZ,
                CX = c.PosX, CY = c.PosY, CZ = c.PosZ,
                HasNormals = normalLength > 1e-6f,
                NAX = a.NormalX, NAY = a.NormalY, NAZ = a.NormalZ,
                NBX = b.NormalX, NBY = b.NormalY, NBZ = b.NormalZ,
                NCX = c.NormalX, NCY = c.NormalY, NCZ = c.NormalZ,
                HasTangentFrame = hasFrame,
                TgAX = a.TangentX, TgAY = a.TangentY, TgAZ = a.TangentZ,
                TgBX = b.TangentX, TgBY = b.TangentY, TgBZ = b.TangentZ,
                TgCX = c.TangentX, TgCY = c.TangentY, TgCZ = c.TangentZ,
                BnAX = a.BinormalX, BnAY = a.BinormalY, BnAZ = a.BinormalZ,
                BnBX = b.BinormalX, BnBY = b.BinormalY, BnBZ = b.BinormalZ,
                BnCX = c.BinormalX, BnCY = c.BinormalY, BnCZ = c.BinormalZ
            };
        }
    }
}
