// TerrainMeshBuilder.cs
//
// Converts terrain.bdf geometry into a Unity Mesh, split by material.
// Uses BdfIO.Formats.Terrain.TerrainModel.
//
// Lives in the Unity project as an ordinary (not Editor-only) script — mesh
// building needs no UnityEditor API. Import tooling (.scd extraction,
// EditorWindow) is a separate concern; this only builds geometry from an
// already-parsed TerrainModel.
//
// IMPORTANT about coordinate system/winding: SC2 and Unity can differ in
// handedness. Options.FlipTriangleWinding below defaults to false — see its
// own doc comment for why. After a first import, check visually (normals
// facing outward, mesh not "inside-out") and flip Options.FlipTriangleWinding
// and/or Options.FlipZ if needed.

using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Terrain;

public static class TerrainMeshBuilder
{
    public sealed class Options
    {
        // Uv2Scale was removed: Uv2U/V are already normalized in
        // TerrainBdf.Parse (int16/32767 is applied on read, see TerrainVertex).

        /// <summary>
        /// Swaps the B/C indices of every triangle.
        ///
        /// SC2 stores triangles CCW relative to vertex normals (verified
        /// byte-for-byte: dot(cross(AB,AC), avgNormal) > 0 for 100% of
        /// triangles). SC2 and Unity use the same axis orientation (Y-up,
        /// +Z = down on the map/forward in Unity) — coordinates map 1:1,
        /// no FlipZ needed. So winding doesn't need changing: false.
        /// </summary>
        public bool FlipTriangleWinding = false;

        /// <summary>
        /// Creates placeholder materials (color keyed by index).
        /// false + UseSC2TerrainShader=true -> SC2TerrainMaterialFactory.
        /// </summary>
        public bool CreatePlaceholderMaterials = true;

        /// <summary>Use SC2TerrainMaterialFactory (the "SC2/Terrain" shader).</summary>
        public bool UseSC2TerrainShader = false;
    }

    /// <summary>
    /// Builds a SINGLE Mesh containing every terrain.bdf vertex, with one
    /// submesh per material (sliced by TerrainLayer.CumulativeIndices), plus
    /// a GameObject with MeshFilter/MeshRenderer
    /// (sharedMaterials.Length == model.Materials.Count).
    /// </summary>
    public static GameObject Build(TerrainModel model, Transform? parent = null, Options? options = null)
    {
        options ??= new Options();

        var mesh = BuildMesh(model, options);

        var go = new GameObject("terrain");
        if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = options.UseSC2TerrainShader
            ? BuildSC2TerrainMaterials(model)
            : options.CreatePlaceholderMaterials
                ? BuildPlaceholderMaterials(model)
                : new Material[model.Materials.Count];

        return go;
    }

    /// <summary>
    /// Mesh only (no GameObject) — useful when an importer needs its own
    /// placement/material logic.
    /// </summary>
    public static Mesh BuildMesh(TerrainModel model, Options? options = null)
    {
        options ??= new Options();

        var vertexCount = model.Vertices.Count;
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uv1 = new Vector2[vertexCount];
        var uv2 = new Vector2[vertexCount];

        for (var i = 0; i < vertexCount; i++)
        {
            var v = model.Vertices[i];
            positions[i] = new Vector3(v.PosX, v.PosY, v.PosZ);
            normals[i]   = Vector3.up; // the file's stored normals are a garbage constant — recomputed below
            uv1[i] = new Vector2(v.Uv1U, 1f - v.Uv1V);
            uv2[i] = new Vector2(v.Uv2U, 1f - v.Uv2V); // already normalized (int16/32767)
        }

        var mesh = new Mesh
        {
            name = "terrain",
            // 106k+ vertices exceeds 65535 -> a 32-bit index is mandatory.
            indexFormat = vertexCount > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };

        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv1);
        mesh.SetUVs(1, uv2);

        mesh.subMeshCount = model.Layers.Count;

        for (var i = 0; i < model.Layers.Count; i++)
        {
            var triStart = model.Layers[i].CumulativeIndices / 3;
            var triEndIndices = i + 1 < model.Layers.Count
                ? model.Layers[i + 1].CumulativeIndices
                : model.Triangles.Count * 3;
            var triEnd = triEndIndices / 3;
            var triCount = triEnd - triStart;
            var vertexBase = model.Layers[i].CumulativeVertices;

            var indices = new int[triCount * 3];
            for (var t = 0; t < triCount; t++)
            {
                var tri = model.Triangles[triStart + t];
                // Triangle indices are LOCAL to each material (0..vCount_i-1,
                // verified byte-for-byte on a real vanilla map: layer i's
                // index range exactly matches its vertex count) -> offset by
                // CumulativeVertices to get indices into the shared vertex buffer.
                var a = (int)tri.A + vertexBase;
                var b = (int)tri.B + vertexBase;
                var c = (int)tri.C + vertexBase;

                // SC2 stores CCW winding (verified: dot(cross(AB,AC), avgNormal) > 0
                // for 100% of triangles on a real vanilla map). Coordinates map
                // 1:1, no FlipZ — winding doesn't change, no flip needed.
                if (options.FlipTriangleWinding)
                    (b, c) = (c, b);

                indices[t * 3 + 0] = a;
                indices[t * 3 + 1] = b;
                indices[t * 3 + 2] = c;
            }

            mesh.SetIndices(indices, MeshTopology.Triangles, i);
        }

        mesh.RecalculateBounds();
        mesh.RecalculateNormals(); // the file doesn't store real normals — computed by Unity instead

        // Diagnostic only (not gated behind any toggle — cheap, always runs):
        // per-layer UV2 min/max range. UV2 (unlike normals, above) is
        // trusted verbatim from the .bdf file with no validation at all —
        // if a Blender-exported file's UV2 turns out to be degenerate the
        // same way its normals/tangents are known to be (see the "Recompute
        // normals + tangents" toggle elsewhere in this codebase), RNM mask
        // sampling (which reads UV2) would silently collapse to a near-
        // constant or discontinuous value per material, producing a hard-
        // edged blend/lighting result despite genuinely smooth source mask
        // data. A near-zero range here confirms that failure mode; a
        // real, reasonably-sized range rules it out.
        for (var i = 0; i < model.Layers.Count; i++)
        {
            var vStart = model.Layers[i].CumulativeVertices;
            var vEnd = i + 1 < model.Layers.Count ? model.Layers[i + 1].CumulativeVertices : vertexCount;
            if (vEnd <= vStart) continue;
            var min = uv2[vStart];
            var max = uv2[vStart];
            for (var vi = vStart + 1; vi < vEnd; vi++)
            {
                var uv = uv2[vi];
                if (uv.x < min.x) min.x = uv.x; if (uv.y < min.y) min.y = uv.y;
                if (uv.x > max.x) max.x = uv.x; if (uv.y > max.y) max.y = uv.y;
            }
            var matName = i < model.Materials.Count ? model.Materials[i].Name : $"layer{i}";
            Debug.Log($"[TerrainMeshBuilder] UV2 range for material '{matName}' (layer {i}, {vEnd - vStart} verts): " +
                      $"U [{min.x:F4}..{max.x:F4}] V [{min.y:F4}..{max.y:F4}]");
        }

        return mesh;
    }

    /// <summary>Materials via SC2TerrainMaterialFactory (the "SC2/Terrain" shader, RNM+blending).</summary>
    private static Material[] BuildSC2TerrainMaterials(TerrainModel model)
    {
        var result = new Material[model.Materials.Count];
        for (var i = 0; i < model.Materials.Count; i++)
            result[i] = SC2TerrainMaterialFactory.Create(model.Materials[i]);
        return result;
    }

    /// <summary>
    /// Simple opaque materials for an initial preview without textures:
    /// each material gets its own color (keyed by index, HSV), named after
    /// TerrainMaterial.Name (including "Water" — the real water shader is a
    /// separate path).
    /// </summary>
    private static Material[] BuildPlaceholderMaterials(TerrainModel model)
    {
        var shader = SC2FallbackShader.Get();
        var result = new Material[model.Materials.Count];

        for (var i = 0; i < model.Materials.Count; i++)
        {
            var mat = new Material(shader) { name = $"mat_{model.Materials[i].Name}" };
            var hue = (float)i / Mathf.Max(1, model.Materials.Count);
            mat.color = Color.HSVToRGB(hue, 0.5f, 0.85f);
            result[i] = mat;
        }

        return result;
    }
}
