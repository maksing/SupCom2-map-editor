using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

/// <summary>One world-space surface path for imported MeshCollider terrain
/// and Unity runtime Terrain. Decal tools never branch by map origin.</summary>
public sealed class SC2DecalSurfaceProvider
{
    private readonly RaycastHit[] _hits = new RaycastHit[128];

    // Object.FindObjectsByType is a full scene scan — cheap once, but both
    // Raycast (every mouse-move while placing/moving a decal) and Collect
    // called it fresh every single time. Real cost: dragging a decal fires
    // Raycast on every mouse-move event, and a fast drag can generate more
    // of those per second than this scan (plus, for Raycast, a full
    // unaccelerated per-triangle test against every collider-less
    // SC2DecalProjectionSurface mesh found) could keep up with, making the
    // decal visibly lag behind the cursor. Scene structure (which colliders/
    // projection surfaces exist) essentially never changes mid-drag, so a
    // short time-based cache is safe — bounded to SceneCacheSeconds stale
    // at worst, well within normal editing feel.
    private MeshCollider[] _cachedColliders;
    private SC2DecalProjectionSurface[] _cachedSurfaces;
    private double _sceneCacheExpiry;
    private const double SceneCacheSeconds = 0.5;

    /// <summary>Forgets the cached scene search: the next Collect / Raycast looks for the colliders and projection surfaces again. Needed
    /// when terrain objects were just created (the imported terrains restored after a load), which a cache younger than
    /// <see cref="SceneCacheSeconds"/> would not contain yet.</summary>
    public void InvalidateSceneCache()
    {
        _cachedColliders = null;
        _cachedSurfaces = null;
        _sceneCacheExpiry = 0;
    }

    private void EnsureSceneCache()
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (_cachedColliders != null && now < _sceneCacheExpiry) return;
        _cachedColliders = Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None);
        _cachedSurfaces = Object.FindObjectsByType<SC2DecalProjectionSurface>(FindObjectsSortMode.None);
        _sceneCacheExpiry = now + SceneCacheSeconds;
    }

    /// <summary>Optional — wired by the caller to the live playable core's
    /// own analytic height source (SC2TerrainShell.TryRaycastLiveHeightGrid,
    /// backed by SC2LiveHeightGrid), the SAME projection Costs/Objects
    /// already trust for "what's the ground here" on live procedural +
    /// imported terrain. Needed because SC2TerrainRuntime's chunk
    /// GameObjects carry no real Collider at all (MeshFilter+MeshRenderer
    /// only — see SC2TerrainRuntime.CreateChunks) — Physics.Raycast below
    /// can never hit them, and the SC2DecalProjectionSurface/collider-less-
    /// MeshFilter fallback further down is an unaccelerated full-mesh
    /// triangle scan that's both slow on a real map and silently empty
    /// whenever the chunk root happens to be inactive (Simplified preview
    /// mode, ImportOnlyMode, Terrain visibility off — Object.FindObjectsByType
    /// excludes inactive objects), which used to leave decals unable to
    /// place on runtime terrain. Returns null when nothing is under the ray
    /// within the live core's own grid bounds (e.g. the ray is entirely
    /// over the decorative background instead).</summary>
    public System.Func<Ray, Vector3?> LiveTerrainRaycast;

    /// <summary>Paired with LiveTerrainRaycast above - same live height
    /// source, used to derive a surface normal at a raycast hit (via finite
    /// difference). Only placement clicks use it; the decal geometry itself
    /// is always clipped against real triangles (see Collect).</summary>
    public System.Func<float, float, float> LiveTerrainSampleHeight;

    /// <summary>The live runtime terrain's own chunk meshes (core and
    /// background) with their transforms - wired by SC2DecalsShell from
    /// SC2TerrainRuntime.VisibleChunkMeshes. Decals clip against these real
    /// triangles; a scene search for the chunk GameObjects is not needed.</summary>
    public System.Func<IEnumerable<(Mesh Mesh, Matrix4x4 LocalToWorld)>> RuntimeSurfaceMeshes;

    /// <summary>What the last Collect gathered from where - shown in the
    /// preview rebuild log so an empty result says which source came up empty.</summary>
    public string LastCollectSummary { get; private set; } = "";

    /// <summary>A decal placement click
    /// should be able to pass straight through a real water-material
    /// terrain surface and land on the actual lake/sea floor beneath it —
    /// real imported terrain.bdf meshes genuinely carry separate water and
    /// floor geometry within the same MeshCollider (unlike the purely
    /// decorative, collider-less SC2WaterPlane overlay), so the water
    /// surface was winning the raycast every time it happened to be the
    /// closest hit, anchoring the decal there instead of on the floor the
    /// user actually clicked past. <paramref name="skipWater"/> lets the
    /// caller choose: true (the new default) walks past any hit whose
    /// rendered material uses the "SC2/Water" shader (the same shader both
    /// SC2WaterMaterialFactory and SC2TerrainMaterialFactory's own water
    /// detection already assign — see IsWaterHit) to keep looking for the
    /// next-closest real surface; false keeps the original "closest hit
    /// wins, water included" behavior, useful for a decal meant to sit
    /// right at the water's own surface plane (e.g. foam/ripple decals).</summary>
    public bool Raycast(Ray ray, bool skipWater, out RaycastHit best)
    {
        best = default;
        var count = Physics.RaycastNonAlloc(ray, _hits, 100000f, ~0, QueryTriggerInteraction.Ignore);
        var distance = float.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var hit = _hits[i];
            if (hit.collider == null || !IsProjectionSurface(hit.collider.gameObject) || hit.distance >= distance) continue;
            if (skipWater && IsWaterHit(hit)) continue;
            best = hit; distance = hit.distance;
        }
        EnsureSceneCache();
        foreach (var surface in _cachedSurfaces)
        {
            if (surface == null) continue;
            foreach (var filter in surface.GetComponentsInChildren<MeshFilter>(false))
            {
                if (filter == null || filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;
                var filterRenderer = filter.GetComponent<MeshRenderer>();
                var filterMaterials = filterRenderer != null ? filterRenderer.sharedMaterials : null;
                if (!TryRaycastMesh(ray, filter.sharedMesh, filterMaterials, skipWater, filter.transform.localToWorldMatrix, distance, out var point, out var normal, out var hitDistance)) continue;
                distance = hitDistance;
                best.point = point; best.normal = normal; best.distance = hitDistance;
            }
        }
        if (LiveTerrainRaycast != null)
        {
            var liveHit = LiveTerrainRaycast(ray);
            if (liveHit.HasValue)
            {
                var liveDistance = Vector3.Distance(ray.origin, liveHit.Value);
                if (liveDistance < distance)
                {
                    distance = liveDistance;
                    best.point = liveHit.Value;
                    best.normal = LiveTerrainNormal(liveHit.Value.x, liveHit.Value.z);
                    best.distance = liveDistance;
                }
            }
        }
        return distance < float.MaxValue;
    }

    private Vector3 LiveTerrainNormal(float x, float z)
    {
        if (LiveTerrainSampleHeight == null) return Vector3.up;
        const float eps = 0.5f;
        var hL = LiveTerrainSampleHeight(x - eps, z);
        var hR = LiveTerrainSampleHeight(x + eps, z);
        var hD = LiveTerrainSampleHeight(x, z - eps);
        var hU = LiveTerrainSampleHeight(x, z + eps);
        return new Vector3(hL - hR, 2f * eps, hD - hU).normalized;
    }

    /// <summary>Real triangles only, everywhere: an imported terrain.bdf
    /// (MeshCollider), the live runtime terrain's chunk meshes (collider-less,
    /// marked with SC2DecalProjectionSurface) and Unity's native Terrain. Water
    /// submeshes are skipped so a decal on a lake floor does not also stick to
    /// the water surface above it. Used by the editor preview; the export
    /// clips against the final exported model instead (CollectFromTerrainModel).</summary>
    public List<SC2DecalSurfaceTriangle> Collect(SC2DecalsDocument document)
    {
        var result = new List<SC2DecalSurfaceTriangle>();
        if (document == null || document.Placements.Count == 0) return result;
        // Per-placement bounds, not one combined box wrapping every
        // placement - a map with decals spread far apart previously forced
        // a full triangle scan of every collider whose bounds intersected
        // that whole loose combined region, even for colliders nowhere near
        // any individual decal. Checking against each placement's own
        // (much tighter) bounds keeps the collider-level early-out useful
        // and skips per-triangle work for geometry no placement actually
        // touches.
        var boundsList = PlacementBoundsList(document);
        if (boundsList.Count == 0) return result;

        EnsureSceneCache();
        var colliderMeshes = 0;
        foreach (var collider in _cachedColliders)
        {
            if (collider == null || collider.sharedMesh == null || !collider.enabled || !IsProjectionSurface(collider.gameObject)) continue;
            // The bounds come from the mesh and the transform, not from Collider.bounds: a collider created (or moved) this frame - the
            // imported terrains a load has just restored - reports the physics scene's stale bounds until the next physics sync, so it
            // was skipped and the decals were clipped against the core terrain only, under the imported model.
            if (!IntersectsAny(boundsList, WorldBounds(collider.sharedMesh.bounds, collider.transform.localToWorldMatrix))) continue;
            var renderer = collider.GetComponent<MeshRenderer>();
            AddMesh(result, collider.sharedMesh, collider.transform.localToWorldMatrix, boundsList, renderer != null ? renderer.sharedMaterials : null);
            colliderMeshes++;
        }

        // The live runtime terrain (core + background chunks). Its chunks are
        // separate meshes, so the ones no decal touches are skipped by their
        // own mesh bounds instead of scanning the whole grid.
        var runtimeMeshes = 0;
        var covered = new HashSet<Mesh>();
        if (RuntimeSurfaceMeshes != null)
            foreach (var (mesh, matrix) in RuntimeSurfaceMeshes())
            {
                covered.Add(mesh);
                if (!IntersectsAny(boundsList, WorldBounds(mesh.bounds, matrix))) continue;
                AddMesh(result, mesh, matrix, boundsList, null);
                runtimeMeshes++;
            }

        // Any other collider-less SC2DecalProjectionSurface mesh in the scene.
        var sceneMeshes = 0;
        foreach (var surface in _cachedSurfaces)
        {
            if (surface == null) continue;
            foreach (var filter in surface.GetComponentsInChildren<MeshFilter>(false))
            {
                if (filter == null || filter.sharedMesh == null || filter.GetComponent<Collider>() != null || covered.Contains(filter.sharedMesh)) continue;
                var matrix = filter.transform.localToWorldMatrix;
                if (!IntersectsAny(boundsList, WorldBounds(filter.sharedMesh.bounds, matrix))) continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                AddMesh(result, filter.sharedMesh, matrix, boundsList, renderer != null ? renderer.sharedMaterials : null);
                sceneMeshes++;
            }
        }
        LastCollectSummary = $"colliders={colliderMeshes}/{_cachedColliders.Length} runtimeChunks={runtimeMeshes}/{covered.Count} sceneMeshes={sceneMeshes}";

        foreach (var terrain in Terrain.activeTerrains)
        {
            var terrainBounds = terrain != null && terrain.terrainData != null
                ? terrain.terrainData.bounds.Transform(terrain.transform.localToWorldMatrix) : default;
            if (terrain != null && terrain.terrainData != null && IntersectsAny(boundsList, terrainBounds))
                AddTerrain(result, terrain, document);
        }
        return result;
    }

    /// <summary>The export's ground: the final terrain model's own triangles
    /// (live plane + imported terrain, after Simplify), water layers left out -
    /// exactly what the game renders, so a decal clipped against it lies on the
    /// real surface instead of an approximation of it.</summary>
    public List<SC2DecalSurfaceTriangle> CollectFromTerrainModel(SC2DecalsDocument document, TerrainModel model)
    {
        if (document == null || document.Placements.Count == 0) return new List<SC2DecalSurfaceTriangle>();
        var wanted = new List<SC2DecalTerrainModelSurface.Box>();
        foreach (var b in PlacementBoundsList(document))
            wanted.Add(new SC2DecalTerrainModelSurface.Box(b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z));
        return SC2DecalTerrainModelSurface.Collect(model, SC2HfieldProjectionService.IsWaterMaterial, wanted);
    }

    private static void SetNormals(ref SC2DecalSurfaceTriangle t, Vector3 na, Vector3 nb, Vector3 nc)
    {
        t.HasNormals = true;
        t.NAX = na.x; t.NAY = na.y; t.NAZ = na.z;
        t.NBX = nb.x; t.NBY = nb.y; t.NBZ = nb.z;
        t.NCX = nc.x; t.NCY = nc.y; t.NCZ = nc.z;
    }

    private static void AddMesh(List<SC2DecalSurfaceTriangle> output, Mesh mesh, Matrix4x4 matrix, List<Bounds> wantedList, Material[] materials)
    {
        var vertices = mesh.vertices;
        // Real per-vertex normals (already smooth - averaged across every
        // face sharing that vertex, exactly what the mesh's own shading
        // uses) - see SC2DecalGeometryBuilder.BuildPlacement's own comment
        // for why passing these through (instead of letting that class fall
        // back to a flat per-triangle normal) fixes visible decal faceting
        // on any non-perfectly-flat surface. Normals transform by the
        // matrix's rotation only, never translated/scaled like a position.
        var normals = mesh.normals;
        var hasNormals = normals != null && normals.Length == vertices.Length;
        for (var sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (IsWaterSubMesh(materials, sub)) continue;
            var indices = mesh.GetTriangles(sub);
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var ia = indices[i]; var ib = indices[i + 1]; var ic = indices[i + 2];
                var a = matrix.MultiplyPoint3x4(vertices[ia]); var b = matrix.MultiplyPoint3x4(vertices[ib]); var c = matrix.MultiplyPoint3x4(vertices[ic]);
                var triangleBounds = new Bounds(a, Vector3.zero);
                triangleBounds.Encapsulate(b); triangleBounds.Encapsulate(c);
                if (!IntersectsAny(wantedList, triangleBounds)) continue;
                var triangle = Triangle(a, b, c);
                if (hasNormals)
                    SetNormals(ref triangle, matrix.MultiplyVector(normals[ia]), matrix.MultiplyVector(normals[ib]), matrix.MultiplyVector(normals[ic]));
                output.Add(triangle);
            }
        }
    }

    private static bool IsWaterSubMesh(Material[] materials, int subMesh) =>
        materials != null && subMesh < materials.Length && materials[subMesh] != null &&
        materials[subMesh].shader != null && materials[subMesh].shader.name == "SC2/Water";

    private static Bounds WorldBounds(Bounds local, Matrix4x4 matrix)
    {
        var world = new Bounds(matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3(-1, -1, -1))), Vector3.zero);
        for (var i = 1; i < 8; i++)
        {
            var sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            world.Encapsulate(matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, sign)));
        }
        return world;
    }

    private static bool IntersectsAny(List<Bounds> boundsList, Bounds triangleBounds)
    {
        for (var i = 0; i < boundsList.Count; i++)
            if (boundsList[i].Intersects(triangleBounds)) return true;
        return false;
    }

    private static void AddTerrain(List<SC2DecalSurfaceTriangle> output, Terrain terrain, SC2DecalsDocument document)
    {
        foreach (var p in document.Placements)
        {
            if (p == null || !p.Enabled || p.Position == null || p.Position.Length < 3) continue;
            // Same rotation bug as PlacementBoundsList above, different
            // symptom: this generates the terrain SOURCE geometry itself
            // (for the Unity-native Terrain path), always exactly
            // Width x Length and axis-aligned - SC2DecalGeometryBuilder's
            // own clip correctly accounts for p.Rotation, but a rotated
            // decal's true footprint corners then have no terrain geometry
            // at all to clip against (this grid never reached that far),
            // same visible "clipped at rotation" result. Sized off the
            // diagonal (a safe upper bound for any rotation angle) instead.
            var side = Mathf.Sqrt(p.Width * p.Width + p.Length * p.Length);
            var cellsX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, side)), 4, 96);
            var cellsZ = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, side)), 4, 96);
            var origin = new Vector3(p.Position[0] - side * .5f, 0, p.Position[2] - side * .5f);
            for (var z = 0; z < cellsZ; z++) for (var x = 0; x < cellsX; x++)
            {
                var x0 = origin.x + side * x / cellsX; var x1 = origin.x + side * (x + 1) / cellsX;
                var z0 = origin.z + side * z / cellsZ; var z1 = origin.z + side * (z + 1) / cellsZ;
                var a = Point(terrain, x0, z0); var b = Point(terrain, x1, z0); var c = Point(terrain, x1, z1); var d = Point(terrain, x0, z1);
                // GetInterpolatedNormal — the same smooth (bicubic-blended)
                // normal Unity's own terrain shading already uses, not a
                // flat cross-product of this manually-tessellated quad's own
                // 4 corners — see SC2DecalGeometryBuilder.BuildPlacement's
                // comment for why a flat normal here would facet visibly.
                var na = NormalAt(terrain, x0, z0); var nb = NormalAt(terrain, x1, z0); var nc = NormalAt(terrain, x1, z1); var nd = NormalAt(terrain, x0, z1);
                // Winding matches SC2TerrainRuntime.BuildChunkIndices - a
                // reversed decal mesh backface-culls in the real game
                // ("exports fine, invisible"). Tagged with this placement's own
                // Id (see OwnerPlacementId): this tessellates ONE placement's
                // own footprint per call, so two nearby placements' grids of
                // the same ground must not be mixed into each other's clip.
                var t1 = Triangle(a, c, b); SetNormals(ref t1, na, nc, nb); t1.OwnerPlacementId = p.Id; output.Add(t1);
                var t2 = Triangle(a, d, c); SetNormals(ref t2, na, nd, nc); t2.OwnerPlacementId = p.Id; output.Add(t2);
            }
        }
    }

    private static Vector3 Point(Terrain t, float x, float z) => new(x, t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y, z);

    private static Vector3 NormalAt(Terrain t, float worldX, float worldZ)
    {
        var size = t.terrainData.size;
        var localX = worldX - t.transform.position.x; var localZ = worldZ - t.transform.position.z;
        var x01 = size.x > 0.0001f ? Mathf.Clamp01(localX / size.x) : 0f;
        var z01 = size.z > 0.0001f ? Mathf.Clamp01(localZ / size.z) : 0f;
        return t.terrainData.GetInterpolatedNormal(x01, z01);
    }
    private static SC2DecalSurfaceTriangle Triangle(Vector3 a, Vector3 b, Vector3 c) => new()
    {
        AX = a.x, AY = a.y, AZ = a.z,
        BX = b.x, BY = b.y, BZ = b.z,
        CX = c.x, CY = c.y, CZ = c.z
    };
    private static bool IsProjectionSurface(GameObject go) => go != null &&
        (go.GetComponentInParent<SC2DecalProjectionSurface>() != null || go.GetComponentInParent<Terrain>() != null);

    /// <summary>True when the hit triangle's own rendered material is the
    /// real water shader — see Raycast's own doc comment on skipWater for
    /// why this is the reliable, already-correct-by-construction signal
    /// (both material factories that can ever produce a water material
    /// always name its shader exactly "SC2/Water").</summary>
    private static bool IsWaterHit(RaycastHit hit)
    {
        if (hit.collider is not MeshCollider meshCollider || meshCollider.sharedMesh == null) return false;
        var renderer = hit.collider.GetComponent<MeshRenderer>();
        return renderer != null && IsWaterTriangle(meshCollider.sharedMesh, renderer.sharedMaterials, hit.triangleIndex);
    }

    private static bool IsWaterTriangle(Mesh mesh, Material[] materials, int triangleIndex)
    {
        if (mesh == null || materials == null || triangleIndex < 0) return false;
        var indexPos = triangleIndex * 3;
        for (var sm = 0; sm < mesh.subMeshCount && sm < materials.Length; sm++)
        {
            var desc = mesh.GetSubMesh(sm);
            if (indexPos < desc.indexStart || indexPos >= desc.indexStart + desc.indexCount) continue;
            var mat = materials[sm];
            return mat != null && mat.shader != null && mat.shader.name == "SC2/Water";
        }
        return false;
    }

    private static bool TryRaycastMesh(Ray ray, Mesh mesh, Material[] materials, bool skipWater, Matrix4x4 matrix, float maxDistance,
        out Vector3 point, out Vector3 normal, out float distance)
    {
        point = default; normal = Vector3.up; distance = maxDistance;
        var vertices = mesh.vertices; var indices = mesh.triangles; var found = false;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            if (skipWater && IsWaterTriangle(mesh, materials, i / 3)) continue;
            var a = matrix.MultiplyPoint3x4(vertices[indices[i]]);
            var b = matrix.MultiplyPoint3x4(vertices[indices[i + 1]]);
            var c = matrix.MultiplyPoint3x4(vertices[indices[i + 2]]);
            if (!IntersectTriangle(ray, a, b, c, out var candidate) || candidate >= distance) continue;
            distance = candidate; point = ray.GetPoint(candidate);
            normal = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(normal, ray.direction) > 0f) normal = -normal;
            found = true;
        }
        return found;
    }

    private static bool IntersectTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;
        var edge1 = b - a; var edge2 = c - a;
        var p = Vector3.Cross(ray.direction, edge2); var determinant = Vector3.Dot(edge1, p);
        if (Mathf.Abs(determinant) < 1e-7f) return false;
        var inverse = 1f / determinant; var t = ray.origin - a;
        var u = Vector3.Dot(t, p) * inverse; if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(t, edge1); var v = Vector3.Dot(ray.direction, q) * inverse;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f;
    }

    private static List<Bounds> PlacementBoundsList(SC2DecalsDocument d)
    {
        var result = new List<Bounds>(d.Placements.Count);
        foreach (var p in d.Placements)
        {
            if (p?.Position == null || p.Position.Length < 3) continue;
            // This box is only a coarse "which triangles are even
            // worth considering" pre-filter — the real, precise clip happens
            // later in SC2DecalGeometryBuilder — but it must not use p.Width/p.Length
            // directly as an AXIS-ALIGNED box size, ignoring p.Rotation
            // entirely. A rotated rectangle's true world-space footprint
            // extends past that unrotated box at its corners (maximally at
            // 45°, where the axis-aligned bound of a square is ~41% too
            // small each way), so triangles under those corners - genuinely
            // inside the decal - were silently rejected here before the real
            // clip test ever saw them. Using the rectangle's own diagonal for
            // both horizontal extents is a safe upper bound for ANY rotation
            // angle (never too small, so never incorrectly excludes a real
            // triangle) - only makes this pre-filter box a bit less tight,
            // which only costs a little extra triangle-bounds-intersection
            // work, never correctness.
            var diag = Mathf.Sqrt(p.Width * p.Width + p.Length * p.Length);
            result.Add(new Bounds(new Vector3(p.Position[0], p.Position[1], p.Position[2]), new Vector3(diag, p.ProjectionDepth, diag)));
        }
        return result;
    }
}

internal static class SC2BoundsExtensions
{
    public static Bounds Transform(this Bounds bounds, Matrix4x4 matrix)
    {
        var center = matrix.MultiplyPoint3x4(bounds.center); var e = bounds.extents;
        var ax = matrix.MultiplyVector(new Vector3(e.x, 0, 0)); var ay = matrix.MultiplyVector(new Vector3(0, e.y, 0)); var az = matrix.MultiplyVector(new Vector3(0, 0, e.z));
        e = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x), Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y), Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
        return new Bounds(center, e * 2f);
    }
}
