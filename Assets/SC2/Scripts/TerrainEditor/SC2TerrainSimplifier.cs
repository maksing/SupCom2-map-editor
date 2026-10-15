using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fast terrain simplifier for SC2 export.
/// Uses vertex clustering instead of iterative edge-collapse so export time
/// stays linear on large heightfield-like layers.
/// </summary>
public static class SC2TerrainSimplifier
{
    private const int kSmallComponentTriThreshold = 64;
    private const int kComponentSplitTriLimit = 120_000;
    private const int kPerimeterAreaTriLimit = 40_000;

    public struct SimplifiedMesh
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public int[]     Triangles;
        public int[]     OrigTriMap;
        public int       ComponentCount;
        public int       SkippedComponentCount;
        public int       ProblemTriangleCount;
        public int[]     ProblemOrigTriMap;
    }

    public static SimplifiedMesh Simplify(
        Vector3[]    allVerts,
        Vector3[]    globalNormals,
        int[]        pairTris,
        int[]        origIdx,
        HashSet<int> boundary,
        float        ratio,
        System.Func<float, float, int> clusterRegion = null,
        SC2ShoreDetail shore = null)
    {
        if (ratio <= 0f || pairTris.Length < 6)
            return Repack(allVerts, globalNormals, pairTris, origIdx);

        // The old heap edge-collapse path has good local quality, but it can
        // spend minutes on large SC2 terrain layers because stale/duplicate
        // heap entries grow faster than the useful collapses. Terrain export is
        // dominated by broad heightfield-like surfaces, so vertex clustering is
        // a much better default: O(V+T), deterministic, and no unbounded heap.
        return SimplifyByComponents(allVerts, globalNormals, pairTris, origIdx, boundary, ratio, clusterRegion, shore);

    }

    private static SimplifiedMesh SimplifyByComponents(
        Vector3[]    allVerts,
        Vector3[]    globalNormals,
        int[]        pairTris,
        int[]        origIdx,
        HashSet<int> boundary,
        float        ratio,
        System.Func<float, float, int> clusterRegion,
        SC2ShoreDetail shore)
    {
        var triCount = pairTris.Length / 3;
        if (triCount < 96)
            return WithComponentStats(Repack(allVerts, globalNormals, pairTris, origIdx), 1, 1, 0);

        // Component splitting is useful for small islands and ragged borders,
        // but sorting millions of component edges on Unity's main thread can
        // look like a hard editor freeze during export. Large painted regions
        // are normally contiguous heightfield sheets, so use the fast clustering
        // path directly and rely on locked boundary/ring vertices there.
        if (triCount > kComponentSplitTriLimit)
        {
            if (ShouldKeepComponentRaw(allVerts, pairTris, boundary))
                return WithComponentStats(Repack(allVerts, globalNormals, pairTris, origIdx), 1, 1, 0);
            var large = SimplifyByVertexClustering(allVerts, globalNormals, pairTris, origIdx, boundary, ratio, clusterRegion, shore);
            large.ComponentCount = 1;
            return large;
        }

        var components = FindTriangleComponents(pairTris);
        if (components.Count <= 1)
        {
            if (ShouldKeepComponentRaw(allVerts, pairTris, boundary))
                return WithComponentStats(Repack(allVerts, globalNormals, pairTris, origIdx), 1, 1, 0);
            var one = SimplifyByVertexClustering(allVerts, globalNormals, pairTris, origIdx, boundary, ratio, clusterRegion, shore);
            one.ComponentCount = 1;
            return one;
        }

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>();
        var orig = new List<int>();
        var problemOrig = new List<int>();
        var skipped = 0;
        var problems = 0;

        foreach (var comp in components)
        {
            var compTris = new int[comp.Count * 3];
            var compOrig = new int[comp.Count];
            for (var i = 0; i < comp.Count; i++)
            {
                var t = comp[i];
                compTris[i * 3] = pairTris[t * 3];
                compTris[i * 3 + 1] = pairTris[t * 3 + 1];
                compTris[i * 3 + 2] = pairTris[t * 3 + 2];
                compOrig[i] = t < origIdx.Length ? origIdx[t] : -1;
            }

            var keepRaw = ShouldKeepComponentRaw(allVerts, compTris, boundary);
            if (keepRaw) skipped++;
            var mesh = keepRaw
                ? Repack(allVerts, globalNormals, compTris, compOrig)
                : SimplifyByVertexClustering(allVerts, globalNormals, compTris, compOrig, boundary, ratio, clusterRegion, shore);

            if (mesh.Triangles == null || mesh.Triangles.Length == 0)
                continue;

            var offset = verts.Count;
            verts.AddRange(mesh.Vertices);
            normals.AddRange(mesh.Normals);
            for (var i = 0; i < mesh.Triangles.Length; i++)
                tris.Add(mesh.Triangles[i] + offset);
            orig.AddRange(mesh.OrigTriMap);
            if (mesh.ProblemOrigTriMap != null) problemOrig.AddRange(mesh.ProblemOrigTriMap);
            problems += mesh.ProblemTriangleCount;
        }

        return new SimplifiedMesh
        {
            Vertices = verts.ToArray(),
            Normals = normals.ToArray(),
            Triangles = tris.ToArray(),
            OrigTriMap = orig.ToArray(),
            ProblemOrigTriMap = problemOrig.ToArray(),
            ComponentCount = components.Count,
            SkippedComponentCount = skipped,
            ProblemTriangleCount = problems
        };
    }

    private static bool ShouldKeepComponentRaw(Vector3[] allVerts, int[] pairTris, HashSet<int> boundary)
    {
        var triCount = pairTris.Length / 3;
        if (triCount < kSmallComponentTriThreshold) return true;

        var minX = float.PositiveInfinity; var maxX = float.NegativeInfinity;
        var minZ = float.PositiveInfinity; var maxZ = float.NegativeInfinity;
        var boundaryHits = 0;
        var seen = new HashSet<int>();
        for (var i = 0; i < pairTris.Length; i++)
        {
            var gi = pairTris[i];
            if (seen.Add(gi) && boundary != null && boundary.Contains(gi)) boundaryHits++;
            var p = allVerts[gi];
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
        }

        var width = Mathf.Max(0.001f, maxX - minX);
        var depth = Mathf.Max(0.001f, maxZ - minZ);
        // thin/boundaryHeavy are shape heuristics meant to protect small,
        // decorative features (a painted road, a thin coastal transition
        // strip) from vertex-clustering distortion - below kPerimeterAreaTriLimit,
        // same size ceiling perimeterAreaHeavy already uses. Un-gated, a
        // large, substantial pair that simply borders many other pairs
        // across a big map (e.g. a widespread Sand/Rock transition covering
        // most of the terrain) legitimately has >35% of its own vertices on
        // a shared boundary without being anything like a thin strip -
        // confirmed via a real map where a 128k-triangle pair spanning the
        // whole map (1012x1012, i.e. NOT thin) stayed at 0% reduction
        // regardless of its SimplifyRatio purely because of this. Above the
        // ceiling, SimplifyByVertexClustering already locks/protects real
        // boundary vertices during clustering, so raw-vertex-count-based
        // shape heuristics aren't needed to keep those seams intact.
        var thin = triCount <= kPerimeterAreaTriLimit &&
                   Math.Min(width, depth) <= Mathf.Max(0.001f, Mathf.Max(width, depth) * 0.035f);
        var boundaryHeavy = triCount <= kPerimeterAreaTriLimit &&
                             seen.Count > 0 && boundaryHits / (float)seen.Count > 0.35f;
        var perimeterAreaHeavy = triCount <= kPerimeterAreaTriLimit && HasHighPerimeterAreaRatio(allVerts, pairTris);
        return thin || boundaryHeavy || perimeterAreaHeavy;
    }

    private static bool HasHighPerimeterAreaRatio(Vector3[] allVerts, int[] pairTris)
    {
        var edges = new Dictionary<long, int>(pairTris.Length);
        var area = 0f;
        for (var t = 0; t < pairTris.Length; t += 3)
        {
            var a = pairTris[t];
            var b = pairTris[t + 1];
            var c = pairTris[t + 2];
            AddEdge(edges, a, b);
            AddEdge(edges, b, c);
            AddEdge(edges, c, a);
            area += Math.Abs(SignedAreaXZ(allVerts[a], allVerts[b], allVerts[c]));
        }

        if (area <= 0.0001f) return true;

        var perimeter = 0f;
        foreach (var kv in edges)
        {
            if (kv.Value != 1) continue;
            var a = (int)(kv.Key >> 32);
            var b = (int)(kv.Key & 0xFFFFFFFF);
            var pa = allVerts[a];
            var pb = allVerts[b];
            var dx = pa.x - pb.x;
            var dz = pa.z - pb.z;
            perimeter += Mathf.Sqrt(dx * dx + dz * dz);
        }

        var ratio = (perimeter * perimeter) / area;
        return ratio > 220f;
    }

    private static void AddEdge(Dictionary<long, int> edges, int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        var key = ((long)a << 32) | (uint)b;
        edges.TryGetValue(key, out var count);
        edges[key] = count + 1;
    }

    private static float SignedAreaXZ(Vector3 a, Vector3 b, Vector3 c)
    {
        return ((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
    }

    private static List<List<int>> FindTriangleComponents(int[] pairTris)
    {
        var triCount = pairTris.Length / 3;
        var dsu = new DisjointSet(triCount);
        var edges = new ComponentEdge[triCount * 3];
        var n = 0;
        for (var t = 0; t < triCount; t++)
        {
            var a = pairTris[t * 3];
            var b = pairTris[t * 3 + 1];
            var c = pairTris[t * 3 + 2];
            edges[n++] = new ComponentEdge(a, b, t);
            edges[n++] = new ComponentEdge(b, c, t);
            edges[n++] = new ComponentEdge(c, a, t);
        }

        Array.Sort(edges, ComponentEdgeComparer.Instance);
        var start = 0;
        while (start < edges.Length)
        {
            var end = start + 1;
            var key = edges[start].Key;
            var firstTri = edges[start].Tri;
            while (end < edges.Length && edges[end].Key == key)
            {
                dsu.Union(firstTri, edges[end].Tri);
                end++;
            }
            start = end;
        }

        var map = new Dictionary<int, List<int>>();
        for (var t = 0; t < triCount; t++)
        {
            var root = dsu.Find(t);
            if (!map.TryGetValue(root, out var list)) map[root] = list = new List<int>();
            list.Add(t);
        }

        var result = new List<List<int>>(map.Values);
        result.Sort((a, b) => b.Count.CompareTo(a.Count));
        return result;
    }

    private static SimplifiedMesh SimplifyByVertexClustering(
        Vector3[]    allVerts,
        Vector3[]    globalNormals,
        int[]        pairTris,
        int[]        origIdx,
        HashSet<int> boundary,
        float        ratio,
        System.Func<float, float, int> clusterRegion,
        SC2ShoreDetail shore)
    {
        var (lv, lt, _, l2g) = BuildLocal(allVerts, pairTris);
        var triCount = lt.Length / 3;
        var targetT = Mathf.Max(2, Mathf.RoundToInt(triCount * (1f - ratio)));
        if (targetT >= triCount) return Repack(allVerts, globalNormals, pairTris, origIdx);

        SimplifiedMesh best = default;
        var bestCount = int.MaxValue;

        // Retry with a slightly coarser grid if the first estimate keeps too
        // many triangles in irregular pair islands.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var effectiveTarget = Mathf.Max(2, Mathf.RoundToInt(targetT * (1f - attempt * 0.18f)));
            var candidate = ClusterOnce(lv, globalNormals, lt, l2g, origIdx, boundary, effectiveTarget, clusterRegion, shore);
            var candidateCount = candidate.Triangles.Length / 3;

            if (candidateCount > 0 && candidateCount < bestCount)
            {
                best = candidate;
                bestCount = candidateCount;
            }

            if (candidateCount > 0 && candidateCount <= targetT)
                return candidate;
        }

        return bestCount == int.MaxValue ? Repack(allVerts, globalNormals, pairTris, origIdx) : best;
    }

    private static SimplifiedMesh ClusterOnce(
        Vector3[]    lv,
        Vector3[]    globalNormals,
        int[]        lt,
        List<int>    l2g,
        int[]        origIdx,
        HashSet<int> boundary,
        int          targetT,
        System.Func<float, float, int> clusterRegion,
        SC2ShoreDetail shore)
    {
        var minX = float.PositiveInfinity; var maxX = float.NegativeInfinity;
        var minZ = float.PositiveInfinity; var maxZ = float.NegativeInfinity;
        for (var i = 0; i < lv.Length; i++)
        {
            var p = lv[i];
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
        }

        var width = Mathf.Max(0.001f, maxX - minX);
        var depth = Mathf.Max(0.001f, maxZ - minZ);
        var aspect = Mathf.Clamp(width / depth, 0.125f, 8f);
        var cellsX = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(targetT * aspect * 0.5f)));
        var cellsZ = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(targetT / aspect * 0.5f)));
        var invX = cellsX / width;
        var invZ = cellsZ / depth;

        var map = new Dictionary<long, int>(lv.Length);
        var remap = new int[lv.Length];
        var sums = new List<Vector3>(lv.Length);
        var counts = new List<int>(lv.Length);
        // Normal sums accumulated in parallel with position sums, each from
        // the GLOBAL (whole-terrain, not per-pair) per-original-vertex
        // normal — see ComputeGlobalNormals. A clustered vertex's normal is
        // the (re-normalized) average of the global normals of every
        // original vertex merged into it, so it stays consistent with
        // whatever normal a neighboring pair's mesh assigns to the same
        // shared original vertices, instead of each pair's clustering
        // deriving its own normal from only its own decimated triangles.
        var normalSums = new List<Vector3>(lv.Length);

        for (var i = 0; i < lv.Length; i++)
        {
            long key;
            // The junction with water is not simplified at all: its vertices stay as they are, like the vertices of a pair border.
            if ((boundary != null && boundary.Contains(l2g[i])) || (shore != null && shore.IsShore(lv[i].y)))
            {
                // Boundary vertices stay unique if a future caller provides a
                // real boundary set. The current exporter passes an empty set.
                key = long.MinValue + i;
            }
            else
            {
                var ix = Mathf.Clamp(Mathf.RoundToInt((lv[i].x - minX) * invX), 0, cellsX);
                var iz = Mathf.Clamp(Mathf.RoundToInt((lv[i].z - minZ) * invZ), 0, cellsZ);
                key = ((long)ix << 32) | (uint)iz;
                // Vertices of different regions never merge, even in the same grid cell (see Build's clusterRegion).
                if (clusterRegion != null) key ^= (long)clusterRegion(lv[i].x, lv[i].z) << 56;
            }

            if (!map.TryGetValue(key, out var ci))
            {
                ci = sums.Count;
                map[key] = ci;
                sums.Add(Vector3.zero);
                counts.Add(0);
                normalSums.Add(Vector3.zero);
            }

            remap[i] = ci;
            sums[ci] += lv[i];
            counts[ci] = counts[ci] + 1;
            normalSums[ci] += globalNormals[l2g[i]];
        }

        var verts = new Vector3[sums.Count];
        for (var i = 0; i < verts.Length; i++)
            verts[i] = sums[i] / counts[i];
        var clusterNormals = new Vector3[normalSums.Count];
        for (var i = 0; i < clusterNormals.Length; i++)
            clusterNormals[i] = normalSums[i].normalized;

        var tris = new List<int>(lt.Length);
        var orig = new List<int>(lt.Length / 3);
        var problemOrig = new List<int>();
        var seen = new HashSet<long>();

        for (var t = 0; t < lt.Length / 3; t++)
        {
            var a = remap[lt[t * 3]];
            var b = remap[lt[t * 3 + 1]];
            var c = remap[lt[t * 3 + 2]];
            if (a == b || b == c || a == c) continue;
            if (!TriangleLooksContainedXZ(verts[a], verts[b], verts[c], minX, maxX, minZ, maxZ))
            {
                problemOrig.Add(t < origIdx.Length ? origIdx[t] : -1);
                continue;
            }

            var key = TriKey(a, b, c);
            if (!seen.Add(key)) continue;

            tris.Add(a); tris.Add(b); tris.Add(c);
            orig.Add(t < origIdx.Length ? origIdx[t] : -1);
        }

        var result = CompactResult(verts, clusterNormals, tris.ToArray(), orig.ToArray());
        result.ProblemOrigTriMap = problemOrig.ToArray();
        result.ProblemTriangleCount = problemOrig.Count;
        return result;
    }

    private static bool TriangleLooksContainedXZ(Vector3 a, Vector3 b, Vector3 c, float minX, float maxX, float minZ, float maxZ)
    {
        var cx = (a.x + b.x + c.x) / 3f;
        var cz = (a.z + b.z + c.z) / 3f;
        var padX = Mathf.Max(0.001f, (maxX - minX) * 0.001f);
        var padZ = Mathf.Max(0.001f, (maxZ - minZ) * 0.001f);
        return cx >= minX - padX && cx <= maxX + padX && cz >= minZ - padZ && cz <= maxZ + padZ;
    }

    private static long TriKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return ((long)(a & 0x1FFFFF) << 42) |
               ((long)(b & 0x1FFFFF) << 21) |
               (uint)(c & 0x1FFFFF);
    }

    private static SimplifiedMesh CompactResult(Vector3[] verts, Vector3[] normals, int[] tris, int[] orig)
    {
        var used = new bool[verts.Length];
        for (var i = 0; i < tris.Length; i++) used[tris[i]] = true;

        var remap = new int[verts.Length];
        var packed = new List<Vector3>(verts.Length);
        var packedNormals = new List<Vector3>(verts.Length);
        for (var i = 0; i < verts.Length; i++)
        {
            if (!used[i]) { remap[i] = -1; continue; }
            remap[i] = packed.Count;
            packed.Add(verts[i]);
            packedNormals.Add(normals[i]);
        }

        for (var i = 0; i < tris.Length; i++)
            tris[i] = remap[tris[i]];

        var va = packed.ToArray();
        return new SimplifiedMesh
        {
            Vertices = va,
            Normals = packedNormals.ToArray(),
            Triangles = tris,
            OrigTriMap = orig,
            ProblemOrigTriMap = System.Array.Empty<int>(),
            ComponentCount = 1
        };
    }


    // ── Helpers ──────────────────────────────────────────────

    private static SimplifiedMesh Repack(Vector3[] av, Vector3[] globalNormals, int[] pt, int[] orig)
    {
        var (lv,lt,_,l2g) = BuildLocal(av,pt);
        var ln = new Vector3[lv.Length];
        for (var i = 0; i < lv.Length; i++) ln[i] = globalNormals[l2g[i]];
        return new SimplifiedMesh{Vertices=lv,Normals=ln,Triangles=lt,OrigTriMap=orig,ProblemOrigTriMap=System.Array.Empty<int>(),ComponentCount=1};
    }

    private static SimplifiedMesh WithComponentStats(SimplifiedMesh mesh, int components, int skipped, int problems)
    {
        mesh.ComponentCount = components;
        mesh.SkippedComponentCount = skipped;
        mesh.ProblemTriangleCount = problems;
        return mesh;
    }

    private static (Vector3[] lv, int[] lt, Dictionary<int,int> g2l, List<int> l2g)
        BuildLocal(Vector3[] av, int[] pt)
    {
        var g2l=new Dictionary<int,int>(pt.Length); var l2g=new List<int>(pt.Length);
        var lt=new int[pt.Length];
        for (var i=0;i<pt.Length;i++)
        {
            var g=pt[i];
            if (!g2l.TryGetValue(g,out var l)) { l=l2g.Count; l2g.Add(g); g2l[g]=l; }
            lt[i]=l;
        }
        var lv=new Vector3[l2g.Count];
        for (var i=0;i<l2g.Count;i++) lv[i]=av[l2g[i]];
        return (lv,lt,g2l,l2g);
    }


    private static Vector3[] MkNormals(Vector3[] v, int[] t)
    {
        var n=new Vector3[v.Length];
        for (var i=0;i<t.Length;i+=3)
        {
            var x=Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]);
            n[t[i]]+=x; n[t[i+1]]+=x; n[t[i+2]]+=x;
        }
        for (var i=0;i<n.Length;i++) n[i]=n[i].normalized;
        return n;
    }

    /// <summary>Global per-ORIGINAL-vertex normals, accumulated once from
    /// every exportable triangle across ALL pairs (bounded to the first
    /// triLimit entries of allTris, same as SC2TerrainLayerBuilder.Build's
    /// own triLimit) — NOT per pair. Every pair's SimplifiedMesh below looks
    /// these up (Repack) or averages them (ClusterOnce, when several
    /// original vertices merge into one clustered vertex) instead of each
    /// independently recomputing a normal from only its OWN triangle subset
    /// via MkNormals. Recomputing per pair used to give a vertex sitting
    /// exactly on a shared pair-boundary edge a genuinely different normal
    /// in each neighboring pair's mesh (since MkNormals only ever saw that
    /// one pair's own triangles around it) — a real per-pixel lighting seam
    /// running along every texture-pair boundary, visible in-game as
    /// grid-aligned bright stripes wherever a shader (e.g. a construction/
    /// build effect) is sensitive to normal discontinuities.</summary>
    public static Vector3[] ComputeGlobalNormals(Vector3[] allVerts, int[] allTris, int triLimit)
    {
        var triVertCount = Mathf.Min(triLimit * 3, allTris.Length);
        if (triVertCount == allTris.Length) return MkNormals(allVerts, allTris);
        var bounded = new int[triVertCount];
        Array.Copy(allTris, bounded, triVertCount);
        return MkNormals(allVerts, bounded);
    }

    /// <param name="protectRingAroundUnassigned">Keeping the first triangle ring around a border (below) is for borders between two
    /// pairs. A border against UNASSIGNED triangles (a carved hole: the background's cut-out for the core) is still locked itself,
    /// but with this off no extra ring is kept around it - that ring stays at full density next to the hole, a strip of small
    /// quads along the whole seam.</param>
    public static HashSet<int> FindBoundaryVertices(SC2TerrainData data, bool protectRingAroundUnassigned = true)
    {
        var boundary = new HashSet<int>();
        var ringSeeds = new HashSet<int>();
        if (data == null || data.TrianglePairIndex == null || data.TrianglePairIndex.Length == 0)
            return boundary;

        var tris = data.Triangles;
        var triCount = Math.Min(data.TrianglePairIndex.Length, tris.Length / 3);
        if (triCount == 0) return boundary;

        var edges = new EdgeRec[triCount * 3];
        var n = 0;
        for (var t = 0; t < triCount; t++)
        {
            var a = tris[t * 3];
            var b = tris[t * 3 + 1];
            var c = tris[t * 3 + 2];
            var pair = data.TrianglePairIndex[t];
            edges[n++] = new EdgeRec(a, b, pair);
            edges[n++] = new EdgeRec(b, c, pair);
            edges[n++] = new EdgeRec(c, a, pair);
        }

        Array.Sort(edges, EdgeRecComparer.Instance);

        var groupStart = 0;
        while (groupStart < edges.Length)
        {
            var groupEnd = groupStart + 1;
            var key = edges[groupStart].Key;
            var firstPair = edges[groupStart].Pair;
            var pairDiffers = false;
            var touchesUnassigned = firstPair < 0;

            while (groupEnd < edges.Length && edges[groupEnd].Key == key)
            {
                if (edges[groupEnd].Pair != firstPair)
                    pairDiffers = true;
                if (edges[groupEnd].Pair < 0)
                    touchesUnassigned = true;
                groupEnd++;
            }

            if (pairDiffers)
            {
                boundary.Add(edges[groupStart].A);
                boundary.Add(edges[groupStart].B);
                if (protectRingAroundUnassigned || !touchesUnassigned)
                {
                    ringSeeds.Add(edges[groupStart].A);
                    ringSeeds.Add(edges[groupStart].B);
                }
            }

            groupStart = groupEnd;
        }

        if (boundary.Count == 0) return boundary;

        // Vertex clustering is done independently per material pair. Locking
        // only the exact shared edge prevents cracks, but triangles adjacent to
        // concave pair borders can still be pulled across that border and
        // overlap the neighbour. Keep the first triangle ring around pair
        // boundaries intact; deeper interior vertices remain simplifiable.
        var protectedVerts = new HashSet<int>(boundary);
        for (var t = 0; t < triCount; t++)
        {
            var a = tris[t * 3];
            var b = tris[t * 3 + 1];
            var c = tris[t * 3 + 2];
            if (ringSeeds.Contains(a) || ringSeeds.Contains(b) || ringSeeds.Contains(c))
            {
                protectedVerts.Add(a);
                protectedVerts.Add(b);
                protectedVerts.Add(c);
            }
        }

        return protectedVerts;
    }

    /// <summary>The terrain's true outer map-edge vertices (grid border,
    /// gx==0||gx==GridWidth||gz==0||gz==GridHeight) — distinct from
    /// FindBoundaryVertices above, which only locks PAINTED PAIR-TO-PAIR
    /// boundaries and never the map's own perimeter (an edge used by only
    /// one triangle never appears twice with a differing pair, so
    /// pairDiffers never fires for it). Used by the background-terrain
    /// seam (see SC2BackgroundTerrain) to guarantee the core's edge ring
    /// survives simplification even when the bordering pair has its own
    /// SimplifyRatio > 0 — the background's boundary-ring height copy is
    /// taken from exactly this ring, so it must never move.</summary>
    public static HashSet<int> FindOuterPerimeterVertices(SC2TerrainData data)
    {
        var result = new HashSet<int>();
        if (data == null || !data.IsInitialized) return result;
        var vw = data.GridWidth + 1;
        var vh = data.GridHeight + 1;
        for (var x = 0; x < vw; x++) { result.Add(x); result.Add((vh - 1) * vw + x); }
        for (var z = 0; z < vh; z++) { result.Add(z * vw); result.Add(z * vw + vw - 1); }
        return result;
    }

    private readonly struct EdgeRec
    {
        public readonly int A;
        public readonly int B;
        public readonly short Pair;
        public readonly long Key;

        public EdgeRec(int a, int b, short pair)
        {
            if (a <= b) { A = a; B = b; }
            else { A = b; B = a; }
            Pair = pair;
            Key = ((long)A << 32) | (uint)B;
        }
    }

    private sealed class EdgeRecComparer : IComparer<EdgeRec>
    {
        public static readonly EdgeRecComparer Instance = new EdgeRecComparer();
        public int Compare(EdgeRec x, EdgeRec y) => x.Key.CompareTo(y.Key);
    }

    private readonly struct ComponentEdge
    {
        public readonly long Key;
        public readonly int Tri;

        public ComponentEdge(int a, int b, int tri)
        {
            if (a > b) (a, b) = (b, a);
            Key = ((long)a << 32) | (uint)b;
            Tri = tri;
        }
    }

    private sealed class ComponentEdgeComparer : IComparer<ComponentEdge>
    {
        public static readonly ComponentEdgeComparer Instance = new ComponentEdgeComparer();
        public int Compare(ComponentEdge x, ComponentEdge y) => x.Key.CompareTo(y.Key);
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public DisjointSet(int count)
        {
            _parent = new int[count];
            _rank = new byte[count];
            for (var i = 0; i < count; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            if (_rank[ra] < _rank[rb]) _parent[ra] = rb;
            else if (_rank[ra] > _rank[rb]) _parent[rb] = ra;
            else { _parent[rb] = ra; _rank[ra]++; }
        }
    }
}
