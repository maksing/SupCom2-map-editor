using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// The geometry of the props that are included in the heightmap (SC2ObjProp.IncludeInHeightmap), as terrain-model solids that
/// SC2HfieldProjectionService.Project rasterizes next to the terrain - so a building a unit must walk around, or over, is part of
/// the hfield.bdf (and the costs and Collision2 built from it) without ever being written into terrain.bdf.
/// </summary>
public static class SC2PropHeightmapGeometry
{
    /// <summary>Finds the catalog asset of a prop blueprint path ("/props/..."); set by the Objects shell once the blueprint index
    /// exists. Without it (or while the index is still loading) no prop contributes geometry.</summary>
    public static Func<string, SC2GameAsset> AssetResolver;

    private static readonly Dictionary<string, (float[] Positions, ushort[] Indices, float Scale)?> MeshCache =
        new Dictionary<string, (float[] Positions, ushort[] Indices, float Scale)?>(StringComparer.OrdinalIgnoreCase);

    private static readonly List<int> IncludedIds = new List<int>();

    /// <summary>The ids of the props marked "include in heightmap" (the list is reused: copy it to keep it).</summary>
    public static List<int> IncludedPropIds(SC2TerrainData data)
    {
        IncludedIds.Clear();
        if (data?.ObjProps == null) return IncludedIds;
        foreach (var p in data.ObjProps)
            if (p != null && p.IncludeInHeightmap) IncludedIds.Add(p.Id);
        return IncludedIds;
    }

    /// <summary>Forgets the meshes read so far (the game data root changed).</summary>
    public static void ClearCache() { MeshCache.Clear(); FacingCache.Clear(); }

    private static readonly Dictionary<string, float> FacingCache = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The yaw (degrees) of the side a prop model leans towards (see SC2PropFacing), 0 for a centred model or while the
    /// blueprint index / game archives are not available (then not kept, so the next call tries again).</summary>
    public static float FacingYaw(string blueprintPath)
    {
        if (string.IsNullOrEmpty(blueprintPath)) return 0f;
        if (FacingCache.TryGetValue(blueprintPath, out var cached)) return cached;
        if (AssetResolver == null || !SC2GameArchiveRegistry.TryGetReader(out var reader) || reader == null) return 0f;
        var asset = AssetResolver(blueprintPath);
        if (asset == null) return 0f;
        if (!MeshCache.TryGetValue(blueprintPath, out var mesh))
            MeshCache[blueprintPath] = mesh = TryReadMesh(asset, reader);
        var yaw = mesh.HasValue ? SC2PropFacing.FromPositions(mesh.Value.Positions) : 0f;
        FacingCache[blueprintPath] = yaw;
        return yaw;
    }

    /// <summary>One solid for every included prop, or an empty list when none is marked or the game archives are not available.
    /// Props whose blueprint or SCM cannot be found are skipped.</summary>
    public static List<TerrainModel> BuildSolids(SC2TerrainData data, IEnumerable<SC2ObjProp> props = null)
    {
        var solids = new List<TerrainModel>();
        var source = props ?? data?.ObjProps;
        if (data == null || source == null) return solids;
        var included = source.Where(p => p != null && p.IncludeInHeightmap && !string.IsNullOrEmpty(p.BlueprintPath)).ToList();
        if (included.Count == 0 || AssetResolver == null) return solids;
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader) || reader == null) return solids;

        // A blueprint repeated over many props (a row of the same building) is read once, and kept for the next rebuild: the live
        // height grid asks again after every edit. An asset that was not found (the index still loading) is not kept.
        var meshes = MeshCache;
        foreach (var prop in included)
        {
            if (!meshes.TryGetValue(prop.BlueprintPath, out var mesh))
            {
                var asset = AssetResolver(prop.BlueprintPath);
                if (asset == null) continue;
                meshes[prop.BlueprintPath] = mesh = TryReadMesh(asset, reader);
            }
            if (!mesh.HasValue) continue;
            solids.Add(SC2HfieldProjectionService.BuildSolid(mesh.Value.Positions, mesh.Value.Indices, prop.Position, prop.Rotation, prop.Scale * mesh.Value.Scale));
        }
        return solids;
    }

    private static (float[] Positions, ushort[] Indices, float Scale)? TryReadMesh(SC2GameAsset asset, ISC2SourceContainerReader reader)
    {
        if (asset == null) return null;
        var lod = asset.Lods.FirstOrDefault(candidate => candidate.ModelPaths != null && candidate.ModelPaths.Count > 0);
        if (lod == null) return null;
        foreach (var path in lod.ModelPaths)
        {
            try
            {
                SC2ScmModel model;
                using (var stream = reader.OpenEntry(path))
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    buffer.Position = 0;
                    model = SC2ScmModelReader.Read(buffer, path);
                }
                if (model.Vertices.Count == 0 || model.Indices.Count < 3) continue;
                var positions = new float[model.Vertices.Count * 3];
                for (var i = 0; i < model.Vertices.Count; i++)
                {
                    var p = model.Vertices[i].Position;
                    if (p == null || p.Length < 3) continue;
                    positions[i * 3] = p[0]; positions[i * 3 + 1] = p[1]; positions[i * 3 + 2] = p[2];
                }
                return (positions, model.Indices.ToArray(), Mathf.Max(0.0001f, asset.UniformScale));
            }
            catch (Exception)
            {
                // The next candidate path (or no solid for this prop).
            }
        }
        return null;
    }
}
