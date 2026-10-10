using System.Collections.Generic;
using UnityEngine;

public sealed class SC2TerrainBuiltLayer
{
    public int PairIndex;
    public int SourceTriangleCount;
    public SC2TerrainSimplifier.SimplifiedMesh Mesh;
}

public sealed class SC2TerrainBuildResult
{
    public readonly List<SC2TerrainBuiltLayer> Layers = new List<SC2TerrainBuiltLayer>();
    public int SourceTriangleCount;
    public int BuiltTriangleCount;
    public int BuiltVertexCount;
    public int LockedVertexCount;
    public int ComponentCount;
    public int SkippedComponentCount;
    public int ProblemTriangleCount;
    public int[] PairTriangleCounts = System.Array.Empty<int>();
}

public static class SC2TerrainLayerBuilder
{
    private static float PairRatio(SC2TerrainData data, float[] perPair, int pairIndex) =>
        perPair != null && pairIndex < perPair.Length ? perPair[pairIndex] : data.Pairs[pairIndex].SimplifyRatio;

    public static SC2TerrainBuildResult Build(SC2TerrainData data, bool sortByTriangleCount)
        => Build(data, sortByTriangleCount, null, null);

    public static SC2TerrainBuildResult Build(
        SC2TerrainData data,
        bool sortByTriangleCount,
        System.Action<string, float> progress)
        => Build(data, sortByTriangleCount, progress, null);

    /// <summary>additionalLockedVertices: extra vertex indices that must
    /// never be merged away by simplification, unioned into the normal
    /// painted-pair-boundary lock — used by the background-terrain feature
    /// to lock the core's true outer perimeter and the background's own
    /// boundary ring (see SC2TerrainSimplifier.FindOuterPerimeterVertices
    /// and SC2BackgroundTerrain). Null/empty for every pre-existing
    /// caller, so behavior is unchanged unless a caller opts in.
    /// skipSimplification: forces every pair through Simplify's own
    /// ratio&lt;=0 fast path (a plain repack, no decimation) regardless of
    /// the pair's own configured SimplifyRatio, and skips computing real
    /// per-vertex normals (a throwaway zero-filled array instead) — for a
    /// caller that only wants accurate, full-resolution triangle POSITIONS
    /// (SC2LiveHeightGrid's rebuild, via SC2HfieldProjectionService.Project,
    /// which rasterizes positions only and never reads normals) rather than
    /// a reduced export-shaped mesh. SimplifyRatio/normals only exist to
    /// serve the shipped game mesh's own triangle budget and shading — real
    /// export/Collision2/RNM-bake callers must keep getting the genuine
    /// simplified+shaded result, so this defaults to false and only
    /// SC2LiveHeightGrid opts in.
    /// simplifyRatioPerPair: when set, pair i is simplified by simplifyRatioPerPair[i] instead of its own SimplifyRatio (the
    /// background, whose ratios come from one triangle budget, SC2BackgroundTerrain.ExportSimplifyRatios).
    /// protectRingAroundUnassigned: see SC2TerrainSimplifier.FindBoundaryVertices.
    /// clusterRegion: maps a vertex position (x, z) to a region id; the simplifier never merges vertices of different regions. For
    /// a surface with a hole, regions that are each convex and clear of the hole keep a merged vertex out of it.
    /// shoreDetail: when set, the ground near the water surface is simplified on a finer grid than the rest (see SC2ShoreDetail).</summary>
    public static SC2TerrainBuildResult Build(
        SC2TerrainData data,
        bool sortByTriangleCount,
        System.Action<string, float> progress,
        HashSet<int> additionalLockedVertices,
        bool skipSimplification = false,
        float[] simplifyRatioPerPair = null,
        bool protectRingAroundUnassigned = true,
        System.Func<float, float, int> clusterRegion = null,
        SC2ShoreDetail shoreDetail = null)
    {
        var result = new SC2TerrainBuildResult();
        // ImportOnlyMode: the live plane must contribute
        // NOTHING to any merged model built from this — every caller
        // (SC2TerrainExporter.BuildExportArtifacts, SC2RnmBakeService.
        // BuildBakeGeometry) folds imported terrain.bdf objects in separately,
        // after this returns, so an empty result here is exactly "only the
        // import counts," not "nothing exports."
        if (data == null || !data.IsInitialized || data.ImportOnlyMode || data.TrianglePairIndex == null || data.TrianglePairIndex.Length == 0)
            return result;

        progress?.Invoke("Invalidating geometry cache", 0.01f);
        data.InvalidateGeometryCache();
        var allVerts = data.Vertices;
        var allTris = data.Triangles;
        var triLimit = Mathf.Min(data.TrianglePairIndex.Length, allTris.Length / 3);
        result.SourceTriangleCount = triLimit;

        var hasAdditionalLocks = additionalLockedVertices != null && additionalLockedVertices.Count > 0;
        // Boundary-locking only matters to the real decimation path
        // (SimplifyByComponents/SimplifyByVertexClustering) — Repack (what
        // every pair takes when skipSimplification forces ratio 0) never
        // reads it, so skip finding it at all in that mode.
        var needsBoundary = !skipSimplification && hasAdditionalLocks;
        if (!skipSimplification)
            for (var i = 0; i < data.Pairs.Count; i++)
                if (PairRatio(data, simplifyRatioPerPair, i) > 0f) { needsBoundary = true; break; }
        progress?.Invoke(needsBoundary ? "Finding locked pair boundaries" : "Counting pair triangles", 0.03f);
        var boundary = needsBoundary ? SC2TerrainSimplifier.FindBoundaryVertices(data, protectRingAroundUnassigned) : new HashSet<int>();
        if (needsBoundary && hasAdditionalLocks)
            foreach (var v in additionalLockedVertices)
                boundary.Add(v);
        result.LockedVertexCount = boundary.Count;

        // Computed ONCE from every exportable triangle across ALL pairs, not
        // per pair — every pair's SimplifiedMesh below looks these up
        // instead of recomputing a normal from only its own triangle subset.
        // Without this, a vertex shared between two pairs (e.g. a "grass"
        // pair and a "rock" pair meeting at their painted boundary) got a
        // genuinely different normal in each pair's own exported mesh, since
        // each pair's local normal computation only ever saw that one pair's
        // own triangles around the shared vertex. That's a real per-pixel
        // lighting seam running along every texture-pair boundary line —
        // reported in-game as grid-aligned bright stripes, visible wherever
        // a shader (e.g. the construction/build effect) is sensitive to
        // normal discontinuities. Skipped for skipSimplification callers —
        // see this method's own doc comment on why they never read normals.
        var globalNormals = skipSimplification ? new Vector3[allVerts.Length] : SC2TerrainSimplifier.ComputeGlobalNormals(allVerts, allTris, triLimit);

        progress?.Invoke("Counting pair triangles", 0.08f);
        var pairTriCount = new int[data.Pairs.Count];
        for (var t = 0; t < triLimit; t++)
        {
            var v = data.TrianglePairIndex[t];
            if (v >= 0 && v < data.Pairs.Count) pairTriCount[v]++;
        }
        result.PairTriangleCounts = pairTriCount;

        var order = new List<int>();
        for (var i = 0; i < pairTriCount.Length; i++)
            if (pairTriCount[i] > 0) order.Add(i);
        if (sortByTriangleCount)
            order.Sort((a, b) => pairTriCount[b].CompareTo(pairTriCount[a]));

        progress?.Invoke("Collecting pair triangle buffers", 0.12f);
        var pairTris = new int[data.Pairs.Count][];
        var pairOrig = new int[data.Pairs.Count][];
        var writeTri = new int[data.Pairs.Count];
        var writeOrig = new int[data.Pairs.Count];
        for (var i = 0; i < data.Pairs.Count; i++)
        {
            if (pairTriCount[i] <= 0) continue;
            pairTris[i] = new int[pairTriCount[i] * 3];
            pairOrig[i] = new int[pairTriCount[i]];
        }

        for (var t = 0; t < triLimit; t++)
        {
            var pi = data.TrianglePairIndex[t];
            if (pi < 0 || pi >= data.Pairs.Count || pairTris[pi] == null) continue;
            var cursor = writeTri[pi];
            pairTris[pi][cursor++] = allTris[t * 3];
            pairTris[pi][cursor++] = allTris[t * 3 + 1];
            pairTris[pi][cursor++] = allTris[t * 3 + 2];
            writeTri[pi] = cursor;
            pairOrig[pi][writeOrig[pi]++] = t;
        }

        for (var oi = 0; oi < order.Count; oi++)
        {
            var pi = order[oi];
            var count = pairTriCount[pi];
            progress?.Invoke($"Simplifying pair {oi + 1}/{order.Count}: {data.Pairs[pi].Name}", 0.45f + 0.45f * oi / Mathf.Max(1, order.Count));
            var mesh = SC2TerrainSimplifier.Simplify(allVerts, globalNormals, pairTris[pi], pairOrig[pi], boundary,
                skipSimplification ? 0f : PairRatio(data, simplifyRatioPerPair, pi), clusterRegion, shoreDetail);
            result.Layers.Add(new SC2TerrainBuiltLayer
            {
                PairIndex = pi,
                SourceTriangleCount = count,
                Mesh = mesh
            });
            result.BuiltTriangleCount += mesh.Triangles != null ? mesh.Triangles.Length / 3 : 0;
            result.BuiltVertexCount += mesh.Vertices != null ? mesh.Vertices.Length : 0;
            result.ComponentCount += Mathf.Max(1, mesh.ComponentCount);
            result.SkippedComponentCount += mesh.SkippedComponentCount;
            result.ProblemTriangleCount += mesh.ProblemTriangleCount;
        }

        return result;
    }
}
