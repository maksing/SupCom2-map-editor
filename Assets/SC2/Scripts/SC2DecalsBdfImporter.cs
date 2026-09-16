using System.IO;
using System.Linq;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Decals;
using SC2.MapEditor.Document;

/// <summary>
/// Imports a standalone decals.win.bdf directly as one merged, shaded mesh —
/// same "loose .bdf, no .scd needed" pattern as SC2TerrainBdfImporter, and
/// for the same reason: DecalsBdf/BdfIO have no UnityEditor dependency, so
/// this works identically in the Editor and in a standalone Build &amp; Run
/// player (only the file-picking dialog differs — see
/// SC2GameViewEditorUI.OpenDecalsBdfImport).
///
/// The mesh built here carries the FULL vertex layout DecalsBdf.cs verified
/// against real vanilla data, packed into Unity's built-in Mesh channels
/// (there's no native "TexCoord3/ScaleOffset4/Binormal" channel, so it's
/// spread across UV0/UV1/UV2 — see BuildMesh's comment for the exact
/// mapping, which Resources/SC2Decal.shader's appdata struct must match
/// exactly or the decal will render as garbage/black).
/// </summary>
public static class SC2DecalsBdfImporter
{
    public sealed class ImportResult
    {
        public GameObject Root;
        public DecalsModel Model;
        public bool DiffuseTextureFound;
        public bool NormalTextureFound;
    }

    public static ImportResult Import(string bdfPath) => Import(bdfPath, null);

    /// <summary>archiveReader: same composite reader the unified "Import Map"
    /// orchestrator passes to SC2TerrainBdfImporter.Import — a real game
    /// install ships decal atlas textures in a separate textures.scd archive
    /// from the one carrying the map's own BDFs, so a loose-file-only search
    /// next to bdfPath can't find them there. Null (the default, via the
    /// overload above) is fine for a genuinely standalone decals.bdf with a
    /// sibling 'Textures' folder.</summary>
    public static ImportResult Import(string bdfPath, ISC2SourceContainerReader archiveReader)
    {
        var bytes = File.ReadAllBytes(bdfPath);
        var container = BdfContainer.Read(bytes, DecalsBdf.Padding);
        var model = DecalsBdf.Parse(container);

        var go = new GameObject(Path.GetFileNameWithoutExtension(bdfPath));
        var result = new ImportResult { Root = go, Model = model };

        if (!model.HasDecals)
            return result; // empty map — caller decides how to report this; nothing to render

        // Same real bug class SC2TerrainBdfImporter's own EnsureTexturesAvailable
        // fixes for terrain materials: a bare Resources.Load lookup (the old
        // behavior here) only ever finds a texture that's already a Unity
        // asset — it never looks next to the .bdf or inside a scanned
        // archive, so a real map's decal atlas rendered flat/untextured in
        // the editor even though the same atlas resolves fine in the real
        // game. Copy the two atlas textures in first, same resolution chain
        // terrain materials already use.
        var atlasPaths = new[] { model.DiffuseAtlasPath, model.NormalAtlasPath }
            .Where(p => !string.IsNullOrEmpty(p));
        SC2TerrainBdfImporter.CopyReferencedTextures(atlasPaths, bdfPath, archiveReader);

        var mesh = BuildMesh(model);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();

        var shader = Resources.Load<Shader>("SC2Decal") ?? SC2FallbackShader.Get();
        var mat = new Material(shader) { name = "SC2Decal" };

        var diffuse = LoadAtlasTexture(model.DiffuseAtlasPath);
        var normal = LoadAtlasTexture(model.NormalAtlasPath);
        result.DiffuseTextureFound = diffuse != null;
        result.NormalTextureFound = normal != null;
        if (diffuse != null) mat.SetTexture("_DiffuseMap", diffuse);
        if (normal != null)
        {
            mat.SetTexture("_NormalMap", normal);
            mat.SetFloat("_HasNormalMap", 1f);
        }

        mr.sharedMaterial = mat;
        return result;
    }

    /// <summary>
    /// Vertex channel mapping (must match Resources/SC2Decal.shader's
    /// appdata struct exactly):
    ///   Position        -> Mesh.vertices
    ///   TangentXform2    -> Mesh.normals            (NORMAL, per decal.hlsl)
    ///   TangentXform0    -> Mesh.tangents.xyz (w=1)  (TANGENT, per decal.hlsl)
    ///   TexCoord (3)     -> UV0 (Vector3)            (TEXCOORD0, per decal.hlsl)
    ///   ScaleOffset (4)  -> UV1 (Vector4)            (TEXCOORD1, per decal.hlsl)
    ///   TangentXform1    -> UV2 (Vector3)            (BINORMAL — no built-in
    ///                                                  Mesh channel for it)
    /// </summary>
    public static Mesh BuildMesh(DecalsModel model)
    {
        var count = model.Vertices.Count;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var tangents = new Vector4[count];
        var texCoord0 = new Vector3[count];
        var scaleOffset = new Vector4[count];
        var binormals = new Vector3[count];

        for (var i = 0; i < count; i++)
        {
            var v = model.Vertices[i];
            positions[i] = new Vector3(v.PositionX, v.PositionY, v.PositionZ);
            texCoord0[i] = new Vector3(v.TexCoordX, v.TexCoordY, v.TexCoordZ);
            scaleOffset[i] = new Vector4(v.ScaleOffsetX, v.ScaleOffsetY, v.ScaleOffsetZ, v.ScaleOffsetW);
            tangents[i] = new Vector4(v.TangentXform0X, v.TangentXform0Y, v.TangentXform0Z, 1f);
            binormals[i] = new Vector3(v.TangentXform1X, v.TangentXform1Y, v.TangentXform1Z);
            normals[i] = new Vector3(v.TangentXform2X, v.TangentXform2Y, v.TangentXform2Z);
        }

        var mesh = new Mesh
        {
            name = "SC2Decal",
            indexFormat = count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.vertices = positions;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.SetUVs(0, texCoord0);
        mesh.SetUVs(1, scaleOffset);
        mesh.SetUVs(2, binormals);
        mesh.triangles = model.Indices.ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Resolves an atlas texture via SC2TerrainMaterialFactory's own
    /// public multi-root search (Assets/SC2Textures in the Editor, Resources/
    /// SC2Textures in a Build) — same lookup terrain materials use, so a
    /// texture CopyReferencedTextures just copied in above (or one already
    /// sitting there from an earlier terrain import) actually resolves,
    /// instead of the old bare Resources.Load-only lookup this replaced.</summary>
    private static Texture2D LoadAtlasTexture(string bdfTexturePath) =>
        SC2TerrainMaterialFactory.LoadTexturePublic(bdfTexturePath, optional: true);
}
