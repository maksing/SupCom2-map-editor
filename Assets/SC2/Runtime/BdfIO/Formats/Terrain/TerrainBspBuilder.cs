using System.Collections.Generic;
using System.Linq;

namespace BdfIO.Formats.Terrain
{
/// <summary>
/// Core BSP/EDB tree builder, ported 1:1 from the reference Blender add-on
/// SupCom2-TerrainExporter (_build_bsp_tree_recursive +
/// _flatten_bsp_tree_and_order_geometry) and verified byte-for-byte against
/// a real vanilla terrain.bdf.
///
/// KEY PRINCIPLE (unlike a naive "BSP over existing indices" approach): the
/// build order is REVERSED —
///   1. First build the BSP tree over the INPUT triangles (before
///      reindexing). Polygons that cross the split plane are DUPLICATED into
///      both branches (front and back).
///   2. Then walk the tree (post-order: children before parents), and for
///      each LEAF repack its geometry (vertices+triangles) into a new local
///      list — deduplicating vertices by a rounded key (pos+uv1+uv2+normal,
///      7 significant digits). Only from this new list are
///      StartVertex/EndVertex/StartPoly/PolyCount taken.
///   3. Internal nodes get StartVertex/EndVertex/StartPoly/PolyCount as the
///      union (min start, max end) of both children's ranges — AFTER the
///      children have already been counted.
///
/// Because polygons are duplicated at split boundaries and vertices are
/// deduplicated, the final vertex/triangle count of a rebuilt block can
/// differ from what it was before — that's expected and matches the
/// reference exporter's own behavior.
///
/// Layer-agnostic on purpose: terrain.bdf has one tree per material layer,
/// collision2.bdf has exactly one tree for the whole mesh — both are just
/// "one call to <see cref="EdbTreeBuilder.Build"/>" with a different target
/// vertex/triangle range and a different place to store the resulting node
/// list.
///
/// Split selection: real vanilla collision2.bdf files (56 maps' worth
/// checked) do NOT match a simple "always split at the count-median index
/// on the longest bbox axis" rule — average child balance ratio ~0.7 (not
/// ~1.0), axis choice correlates only ~35-54% with the longest bbox axis
/// (barely above the ~33% random baseline for 3 axes), and ~73-97% of real
/// splits exactly match SOME (axis, index) pair found by brute-force
/// testing every possible centroid-sorted split point — i.e. it IS a
/// centroid-sort + contiguous-partition algorithm (matching this class's
/// own no-duplication invariant), just not with a fixed median index or a
/// fixed "longest axis" rule. A full sweep-SAH cost search (evaluate every
/// candidate split index on all 3 axes, pick the one minimizing
/// triangleCount*boundingSurfaceArea on each side) reproduces this shape
/// much more closely and is the standard, well-understood technique for
/// exactly this kind of decision — used here for BOTH terrain.bdf and
/// collision2.bdf so they share one real algorithm instead of two
/// divergent approximations. Benchmarked at ~2-3x the cost of the old
/// single-axis median split, independent of triangle count (same
/// O(N log N) complexity class, just 3 sorts instead of 1 plus a cheap
/// linear sweep).
/// </summary>
public static class EdbTreeBuilder
{
    private const int MaxDepth = 30;
    public const int DefaultMinPolysPerLeaf = 200;

    /// <summary>Result of building one tree: reindexed vertices/triangles
    /// (deduplicated per this tree) plus the flat EDB node list (index 0 =
    /// root).</summary>
    public sealed class Result
    {
        public List<TerrainVertex>   Vertices = new();
        public List<TerrainTriangle> Triangles = new();
        public List<EdbNode>         Nodes     = new();
    }

    /// <summary>
    /// Builds one BSP/EDB tree over triangles [triStart, triStart+triCount)
    /// of <paramref name="allTriangles"/>, whose vertex indices are assumed
    /// local to [vertStart, vertStart+vertCount) of <paramref name="allVertices"/>
    /// (i.e. the same "local index range per block" convention terrain.bdf
    /// layers already use).
    /// </summary>
    public static Result Build(
        IReadOnlyList<TerrainVertex> allVertices,
        IReadOnlyList<TerrainTriangle> allTriangles,
        int vertStart,
        int triStart,
        int vertCount,
        int triCount,
        int minPolysPerLeaf = DefaultMinPolysPerLeaf,
        bool useVanillaCandidateSplit = false,
        float hc = 200f)
    {
        var result = new Result();
        if (triCount == 0 || vertCount == 0) return result;

        var localVerts = new TerrainVertex[vertCount];
        for (var i = 0; i < vertCount; i++)
            localVerts[i] = allVertices[vertStart + i];

        var bmin = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var bmax = new float[] { float.MinValue, float.MinValue, float.MinValue };
        foreach (var v in localVerts)
        {
            if (v.PosX < bmin[0]) bmin[0] = v.PosX;
            if (v.PosY < bmin[1]) bmin[1] = v.PosY;
            if (v.PosZ < bmin[2]) bmin[2] = v.PosZ;
            if (v.PosX > bmax[0]) bmax[0] = v.PosX;
            if (v.PosY > bmax[1]) bmax[1] = v.PosY;
            if (v.PosZ > bmax[2]) bmax[2] = v.PosZ;
        }

        var srcTris = new (TerrainTriangle Tri, int OrigIdx)[triCount];
        for (var i = 0; i < triCount; i++)
            srcTris[i] = (allTriangles[triStart + i], i);

        // Step 1: build the BSP tree (polygons duplicated at split boundaries).
        var root = BuildTreeRecursive(srcTris.ToList(), localVerts, bmin, bmax, 0,
            minPolysPerLeaf, useVanillaCandidateSplit, hc);

        // Step 2: walk the tree (post-order), repack geometry per leaf.
        // IMPORTANT: post-order publishes root LAST into result.Nodes, but the
        // file format requires rootBlockOffset == ForestStart (root must be
        // the FIRST element of the node forest — confirmed byte-for-byte on
        // the reference map). So after building we move root to the front of
        // the list and remap all Child1Index/Child2Index accordingly.
        var dedup = new Dictionary<VKey, int>();
        FlattenAndReorder(root, localVerts, srcTris, result.Nodes, result, dedup);
        MoveRootToFront(result.Nodes, root.EdbIndex);

        // ---- Root invariant guard ----
        // After MoveRootToFront, root must be result.Nodes[0] (confirmed
        // byte-for-byte on the reference map: the EDH's rootBlockOffset
        // points at the FIRST node of the block's forest). Root's StartPoly
        // must be 0 (first triangle of the repacked block), EndVertex must
        // be > 0 when PolyCount > 0.
        if (result.Nodes.Count > 0)
        {
            var rootEdb = result.Nodes[0];
            if (rootEdb.PolyCount > 0)
            {
                if (rootEdb.StartPoly != 0)
                    throw new System.InvalidOperationException(
                        $"BSP root invariant violated: Nodes[0].StartPoly={rootEdb.StartPoly} (expected 0). " +
                        $"PolyCount={rootEdb.PolyCount}, StartVertex={rootEdb.StartVertex}, EndVertex={rootEdb.EndVertex}.");
                if (rootEdb.EndVertex == 0)
                    throw new System.InvalidOperationException(
                        $"BSP root invariant violated: Nodes[0].EndVertex=0 with PolyCount={rootEdb.PolyCount} > 0. " +
                        $"StartVertex={rootEdb.StartVertex}, StartPoly={rootEdb.StartPoly}.");
                if (rootEdb.PolyCount != triCount)
                    throw new System.InvalidOperationException(
                        $"BSP root invariant violated: Nodes[0].PolyCount={rootEdb.PolyCount} != triCount={triCount}.");
            }
        }

        return result;
    }

    /// <summary>
    /// Moves the node at index <paramref name="rootIdx"/> to the front of
    /// <paramref name="nodes"/> and remaps every Child1Index/Child2Index for
    /// the resulting index shift.
    /// </summary>
    private static void MoveRootToFront(List<EdbNode> nodes, int rootIdx)
    {
        if (rootIdx == 0 || nodes.Count == 0) return;

        var oldToNew = new int[nodes.Count];
        oldToNew[rootIdx] = 0;
        var cursor = 1;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i == rootIdx) continue;
            oldToNew[i] = cursor++;
        }

        var reordered = new EdbNode[nodes.Count];
        for (var oldIdx = 0; oldIdx < nodes.Count; oldIdx++)
        {
            var node = nodes[oldIdx];
            if (node.Child1Index.HasValue) node.Child1Index = oldToNew[node.Child1Index.Value];
            if (node.Child2Index.HasValue) node.Child2Index = oldToNew[node.Child2Index.Value];
            reordered[oldToNew[oldIdx]] = node;
        }

        nodes.Clear();
        nodes.AddRange(reordered);
    }

    // ----------------------------------------------------------------
    // Internal tree-node representation used during construction
    // ----------------------------------------------------------------
    private sealed class BuildNode
    {
        public float[] BboxMin = new float[3];
        public float[] BboxMax = new float[3];
        public bool IsLeaf;
        public List<int> OriginalPolyIndices = new(); // indices into srcTris
        public BuildNode? Front;
        public BuildNode? Back;

        // Filled in step 2 (after reorder)
        public int StartVertex, EndVertex, StartPoly, PolyCount;
        public int EdbIndex = -1; // index in the target node list after publishing
    }

    private static BuildNode BuildTreeRecursive(
        List<(TerrainTriangle Tri, int OrigIdx)> polys,
        TerrainVertex[] localVerts,
        float[] bboxMin, float[] bboxMax,
        int depth,
        int minPolysPerLeaf,
        bool useVanillaCandidateSplit = false,
        float hc = 200f)
    {
        var node = new BuildNode { BboxMin = bboxMin, BboxMax = bboxMax };
        node.OriginalPolyIndices = polys.Select(p => p.OrigIdx).ToList();

        // Stop rule differs deliberately by mode, not just the split search:
        //  - default (SAH) mode keeps its original count-only rule exactly as
        //    shipped, so turning this parameter on never changes default
        //    export output.
        //  - vanilla-candidate mode adds the bbox-diagonal condition:
        //    every internal node in the real vanilla files satisfies
        //    count >= bspVersion1(200) || diagonal > hc(200) — i.e. a node
        //    only stays a leaf when BOTH count < 200 AND diagonal <= hc.
        var stop = depth >= MaxDepth || polys.Count == 0;
        if (!stop)
            stop = useVanillaCandidateSplit
                ? polys.Count < minPolysPerLeaf && Diagonal(bboxMin, bboxMax) <= hc
                : polys.Count <= minPolysPerLeaf;

        var front = new List<(TerrainTriangle Tri, int OrigIdx)>();
        var back = new List<(TerrainTriangle Tri, int OrigIdx)>();
        var split = false;
        if (!stop)
        {
            split = useVanillaCandidateSplit
                ? FindBestVanillaCandidateSplit(polys, localVerts, bboxMin, bboxMax, out front, out back)
                : FindBestSahSplit(polys, localVerts, out front, out back);
        }

        if (!split)
        {
            node.IsLeaf = true;
            return node;
        }

        // Each branch's bbox is the REAL bbox of its contained vertices (not
        // a geometric truncation of the parent bbox by the split plane),
        // since without triangle duplication at the boundary, vertices of
        // each branch can extend past the split coordinate by up to one
        // triangle's size — the real bbox reflects a node's actual content
        // more accurately and produces fewer bbox-validation false positives.
        ComputeBBoxForList(localVerts, front, out var frontMin, out var frontMax);
        ComputeBBoxForList(localVerts, back,  out var backMin,  out var backMax);

        node.IsLeaf = false;
        node.Front = BuildTreeRecursive(front, localVerts, frontMin, frontMax, depth + 1, minPolysPerLeaf, useVanillaCandidateSplit, hc);
        node.Back  = BuildTreeRecursive(back,  localVerts, backMin,  backMax,  depth + 1, minPolysPerLeaf, useVanillaCandidateSplit, hc);
        return node;
    }

    /// <summary>
    /// Full-sweep SAH split search: each triangle goes STRICTLY into one
    /// branch — no duplication at the split plane (confirmed byte-for-byte
    /// against real game files: parent.PolyCount == child1.PolyCount +
    /// child2.PolyCount EXACTLY, with no exception, for both terrain.bdf and
    /// collision2.bdf). For each of the 3 axes, sorts by triangle centroid,
    /// then sweeps once (via incremental prefix/suffix bounding boxes) to
    /// evaluate cost(splitIndex) = leftCount*SurfaceArea(leftBbox) +
    /// rightCount*SurfaceArea(rightBbox) at every possible contiguous split
    /// point, picking whichever (axis, index) minimizes that cost. This
    /// reproduces real vanilla BSP shape far more closely than a fixed
    /// "median index on the longest axis" rule (see class doc comment for
    /// the byte-level evidence) while keeping the same O(N log N) per-node
    /// complexity (3 sorts instead of 1, plus a cheap linear sweep — ~2-3x
    /// slower in practice, not asymptotically worse).
    /// </summary>
    private static bool FindBestSahSplit(
        List<(TerrainTriangle Tri, int OrigIdx)> polys,
        TerrainVertex[] localVerts,
        out List<(TerrainTriangle Tri, int OrigIdx)> front,
        out List<(TerrainTriangle Tri, int OrigIdx)> back)
    {
        front = new List<(TerrainTriangle Tri, int OrigIdx)>();
        back = new List<(TerrainTriangle Tri, int OrigIdx)>();

        var n = polys.Count;
        if (n < 2) return false;

        List<(TerrainTriangle Tri, int OrigIdx)>? bestSorted = null;
        var bestSplitIdx = -1;
        var bestCost = float.MaxValue;

        for (var axis = 0; axis < 3; axis++)
        {
            var sorted = polys
                .OrderBy(p => TriCentroid(localVerts, p.Tri, axis))
                .ToList();

            var prefixMin = new float[n][]; var prefixMax = new float[n][];
            var suffixMin = new float[n][]; var suffixMax = new float[n][];

            var curMin = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var curMax = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (var i = 0; i < n; i++)
            {
                ExpandBBox(localVerts, sorted[i].Tri, curMin, curMax);
                prefixMin[i] = (float[])curMin.Clone();
                prefixMax[i] = (float[])curMax.Clone();
            }
            curMin = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            curMax = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (var i = n - 1; i >= 0; i--)
            {
                ExpandBBox(localVerts, sorted[i].Tri, curMin, curMax);
                suffixMin[i] = (float[])curMin.Clone();
                suffixMax[i] = (float[])curMax.Clone();
            }

            for (var i = 1; i < n; i++)
            {
                var leftCost = i * SurfaceArea(prefixMin[i - 1], prefixMax[i - 1]);
                var rightCost = (n - i) * SurfaceArea(suffixMin[i], suffixMax[i]);
                var cost = leftCost + rightCost;
                if (cost < bestCost)
                {
                    // Remember only the winning (axis, index) as a reference
                    // + integer — materializing the front/back lists here on
                    // every improvement (rather than once at the end) would
                    // turn this into an accidental O(N^2) sweep.
                    bestCost = cost;
                    bestSorted = sorted;
                    bestSplitIdx = i;
                }
            }
        }

        if (bestSorted == null) return false;

        front = bestSorted.Take(bestSplitIdx).ToList();
        back = bestSorted.Skip(bestSplitIdx).ToList();

        // Degenerate case guard (all centroids equal on every axis) — stop
        // splitting if a branch would be empty.
        return front.Count > 0 && back.Count > 0;
    }

    // Nine fixed fractions of the node's own bbox along each axis, in this
    // exact order — decoded from SC2-NO-ENC.EXE's real split-candidate loop
    // (0x004923B0/0x00492740). Vanilla does NOT search every possible split
    // point (that's what FindBestSahSplit above does, and that shape only
    // matches real trees ~36% of the time) — it always evaluates exactly
    // these 9 candidates per node.
    private static readonly float[] CandidateFractions = { 0.50f, 0.25f, 0.75f };

    /// <summary>
    /// EXPERIMENTAL, NOT bit-exact — implements only the parts of vanilla's
    /// split selection confirmed via EXE disassembly:
    ///  - exactly 9 candidates: {X,Y,Z} x {0.50, 0.25, 0.75} of the node's own
    ///    bbox span on that axis (not every triangle's centroid/min, and not
    ///    every possible split point);
    ///  - score = abs(lowCount - highCount), keeping the FIRST candidate with
    ///    a strictly lower score (a tie keeps the earlier one) and exiting
    ///    the search early once bestScore &lt;= 1;
    ///  - triangle classification by comparing its AABB MINIMUM corner on the
    ///    candidate axis against the split coordinate — confirmed to give
    ///    the SAME visible topology as the real full SAT triangle/box
    ///    overlap test on real data (partition=min vs partition=sat), so
    ///    this is a measured simplification, not a guess — but it is not a
    ///    verified-identical substitute for every possible map.
    ///
    /// Explicitly NOT reproduced here — these remain genuinely unresolved
    /// even with EXE access, not just unported:
    ///  - the exact child-order swap formula (only the visible SPLIT
    ///    membership is reproduced here, not which child is serialized
    ///    first — see TerrainBdf/EdbTreeBuilder's existing "root must be
    ///    Nodes[0]" handling, unaffected by this);
    ///  - the degenerate-triangle front-end filter;
    ///  - the leaf-local output/vertex-cache permutation function
    ///    (0x00492920) — this build still repacks a leaf's own geometry via
    ///    FlattenAndReorder's existing dedup, not vanilla's exact order.
    ///
    /// Consequently this does NOT produce a bit-exact vanilla EDB tree.
    ///
    /// The construction-effect banding this was originally offered as a
    /// second lever against turned out to have a different, unconditional
    /// fix (exact-duplicate-triangle removal per material layer before
    /// either splitter runs — see TerrainBdf.RebuildLayerBsp's
    /// DeduplicateLayerTriangles). Independent of that, this splitter is now
    /// SC2TerrainData.VanillaCandidateBspSplit's DEFAULT (true), on
    /// separate, corpus-measured grounds: rebuilding all 56 real vanilla
    /// terrain.bdf files' own geometry through this splitter at the default
    /// 200/200 thresholds reproduces each file's shipped node/leaf count
    /// almost exactly (mean ratio 1.00 across the corpus) and matches its
    /// SAH-weighted-surface-area cost (mean ratio 0.98), while
    /// <see cref="FindBestSahSplit"/> at the same nominal threshold stops
    /// subdividing far too early (mean node ratio 0.14, mean weighted-SA
    /// ratio 23.9 — 24x WORSE by the very metric SAH optimizes) because its
    /// stop rule only checks triangle count, never bbox extent. This
    /// splitter is also ~3.9x faster to build across the same corpus. See
    /// SC2TerrainData.VanillaCandidateBspSplit's doc comment for the full
    /// per-map numbers.
    /// </summary>
    private static bool FindBestVanillaCandidateSplit(
        List<(TerrainTriangle Tri, int OrigIdx)> polys,
        TerrainVertex[] localVerts,
        float[] bboxMin, float[] bboxMax,
        out List<(TerrainTriangle Tri, int OrigIdx)> front,
        out List<(TerrainTriangle Tri, int OrigIdx)> back)
    {
        front = new List<(TerrainTriangle Tri, int OrigIdx)>();
        back = new List<(TerrainTriangle Tri, int OrigIdx)>();

        var n = polys.Count;
        if (n < 2) return false;

        // Each triangle's AABB-minimum corner, computed once.
        var triMin = new float[n][];
        for (var i = 0; i < n; i++)
        {
            var bm = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var bx = new[] { float.MinValue, float.MinValue, float.MinValue };
            ExpandBBox(localVerts, polys[i].Tri, bm, bx);
            triMin[i] = bm;
        }

        List<(TerrainTriangle Tri, int OrigIdx)>? bestFront = null;
        List<(TerrainTriangle Tri, int OrigIdx)>? bestBack = null;
        var bestScore = int.MaxValue;

        for (var axis = 0; axis < 3 && bestScore > 1; axis++)
        {
            var span = bboxMax[axis] - bboxMin[axis];
            if (span <= 0f) continue;

            foreach (var frac in CandidateFractions)
            {
                if (bestScore <= 1) break; // early exit, matches 0x004923B0's `cmp edi,1 / jbe`

                var coord = bboxMin[axis] + frac * span;
                var candFront = new List<(TerrainTriangle Tri, int OrigIdx)>();
                var candBack = new List<(TerrainTriangle Tri, int OrigIdx)>();
                for (var i = 0; i < n; i++)
                {
                    if (triMin[i][axis] <= coord) candFront.Add(polys[i]);
                    else candBack.Add(polys[i]);
                }
                if (candFront.Count == 0 || candBack.Count == 0) continue;

                var score = System.Math.Abs(candFront.Count - candBack.Count);
                if (score < bestScore) // strictly less — a tie keeps the earlier candidate
                {
                    bestScore = score;
                    bestFront = candFront;
                    bestBack = candBack;
                }
            }
        }

        if (bestFront == null || bestBack == null) return false;
        front = bestFront;
        back = bestBack;
        return true;
    }

    private static double Diagonal(float[] bmin, float[] bmax)
    {
        double dx = bmax[0] - bmin[0], dy = bmax[1] - bmin[1], dz = bmax[2] - bmin[2];
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static void ExpandBBox(TerrainVertex[] localVerts, TerrainTriangle tri, float[] bmin, float[] bmax)
    {
        foreach (var lvi in new[] { (int)tri.V0, (int)tri.V1, (int)tri.V2 })
        {
            var v = localVerts[lvi];
            if (v.PosX < bmin[0]) bmin[0] = v.PosX;
            if (v.PosY < bmin[1]) bmin[1] = v.PosY;
            if (v.PosZ < bmin[2]) bmin[2] = v.PosZ;
            if (v.PosX > bmax[0]) bmax[0] = v.PosX;
            if (v.PosY > bmax[1]) bmax[1] = v.PosY;
            if (v.PosZ > bmax[2]) bmax[2] = v.PosZ;
        }
    }

    private static float SurfaceArea(float[] bmin, float[] bmax)
    {
        var dx = bmax[0] - bmin[0]; var dy = bmax[1] - bmin[1]; var dz = bmax[2] - bmin[2];
        return 2f * (dx * dy + dy * dz + dz * dx);
    }

    private static float Coord(TerrainVertex v, int axis) =>
        axis == 0 ? v.PosX : axis == 1 ? v.PosY : v.PosZ;

    private static float TriCentroid(TerrainVertex[] localVerts, TerrainTriangle tri, int axis)
    {
        var v0 = localVerts[(int)tri.V0];
        var v1 = localVerts[(int)tri.V1];
        var v2 = localVerts[(int)tri.V2];
        return (Coord(v0, axis) + Coord(v1, axis) + Coord(v2, axis)) / 3f;
    }

    private static void ComputeBBoxForList(
        TerrainVertex[] localVerts,
        List<(TerrainTriangle Tri, int OrigIdx)> polys,
        out float[] bmin, out float[] bmax)
    {
        bmin = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        bmax = new[] { float.MinValue, float.MinValue, float.MinValue };
        foreach (var (tri, _) in polys)
            ExpandBBox(localVerts, tri, bmin, bmax);
    }

    // ----------------------------------------------------------------
    // Step 2: post-order walk + geometry repack + publish into node list
    // ----------------------------------------------------------------
    private readonly struct VKey : System.IEquatable<VKey>
    {
        private readonly long _px, _py, _pz, _u1, _v1, _u2, _v2, _nx, _ny, _nz;
        public VKey(TerrainVertex v)
        {
            const float p = 1e7f; // 7 significant digits
            _px = (long)System.Math.Round(v.PosX * p); _py = (long)System.Math.Round(v.PosY * p); _pz = (long)System.Math.Round(v.PosZ * p);
            _u1 = (long)System.Math.Round(v.Uv1U * p); _v1 = (long)System.Math.Round(v.Uv1V * p);
            _u2 = (long)System.Math.Round(v.Uv2U * p); _v2 = (long)System.Math.Round(v.Uv2V * p);
            _nx = (long)System.Math.Round(v.NormalX * p); _ny = (long)System.Math.Round(v.NormalY * p); _nz = (long)System.Math.Round(v.NormalZ * p);
        }
        public bool Equals(VKey o) => _px==o._px&&_py==o._py&&_pz==o._pz&&_u1==o._u1&&_v1==o._v1&&_u2==o._u2&&_v2==o._v2&&_nx==o._nx&&_ny==o._ny&&_nz==o._nz;
        public override bool Equals(object? o) => o is VKey k && Equals(k);
        public override int GetHashCode()
        {
            var h = new System.HashCode();
            h.Add(_px); h.Add(_py); h.Add(_pz); h.Add(_u1); h.Add(_v1); h.Add(_u2); h.Add(_v2); h.Add(_nx); h.Add(_ny); h.Add(_nz);
            return h.ToHashCode();
        }
    }

    private static void FlattenAndReorder(
        BuildNode node,
        TerrainVertex[] origVerts,
        (TerrainTriangle Tri, int OrigIdx)[] srcTris,
        List<EdbNode> targetNodes,
        Result result,
        Dictionary<VKey, int> dedup)
    {
        if (node.IsLeaf)
        {
            node.StartPoly = result.Triangles.Count;

            // StartVertex/EndVertex must cover the min..max index of USED
            // vertices (including ones reused via dedup from previous leaves
            // of the same tree) — NOT just a "before/after" result.Vertices.Count
            // counter, since with full reuse of old vertices no new vertices
            // may be added at all, and the range [count, count) would be
            // empty even though real triangle vertices lie earlier in the array.
            var minVi = int.MaxValue; var maxVi = int.MinValue;

            foreach (var origIdx in node.OriginalPolyIndices)
            {
                var tri = srcTris[origIdx].Tri;
                var newIdx = new int[3];
                var lvis = new[] { (int)tri.V0, (int)tri.V1, (int)tri.V2 };
                for (var k = 0; k < 3; k++)
                {
                    var vData = origVerts[lvis[k]];
                    var key = new VKey(vData);
                    if (!dedup.TryGetValue(key, out var newVi))
                    {
                        newVi = result.Vertices.Count;
                        result.Vertices.Add(vData);
                        dedup[key] = newVi;
                    }
                    newIdx[k] = newVi;
                    if (newVi < minVi) minVi = newVi;
                    if (newVi > maxVi) maxVi = newVi;
                }
                result.Triangles.Add(new TerrainTriangle { V0 = (uint)newIdx[0], V1 = (uint)newIdx[1], V2 = (uint)newIdx[2] });
            }

            node.StartVertex = node.OriginalPolyIndices.Count > 0 ? minVi : 0;
            node.EndVertex   = node.OriginalPolyIndices.Count > 0 ? maxVi + 1 : 0;
            node.PolyCount   = result.Triangles.Count - node.StartPoly;
        }
        else
        {
            // Dedup is reused across the whole tree of ONE block — matches
            // the reference (one shared mesh_reordered_vertices per mesh).
            if (node.Front != null) FlattenAndReorder(node.Front, origVerts, srcTris, targetNodes, result, dedup);
            if (node.Back  != null) FlattenAndReorder(node.Back,  origVerts, srcTris, targetNodes, result, dedup);

            var minVS = int.MaxValue; var maxVE = 0;
            var minPS = int.MaxValue; var maxPE = 0;
            if (node.Front != null)
            {
                minVS = System.Math.Min(minVS, node.Front.StartVertex);
                maxVE = System.Math.Max(maxVE, node.Front.EndVertex);
                minPS = System.Math.Min(minPS, node.Front.StartPoly);
                maxPE = System.Math.Max(maxPE, node.Front.StartPoly + node.Front.PolyCount);
            }
            if (node.Back != null)
            {
                minVS = System.Math.Min(minVS, node.Back.StartVertex);
                maxVE = System.Math.Max(maxVE, node.Back.EndVertex);
                minPS = System.Math.Min(minPS, node.Back.StartPoly);
                maxPE = System.Math.Max(maxPE, node.Back.StartPoly + node.Back.PolyCount);
            }
            node.StartVertex = minVS == int.MaxValue ? 0 : minVS;
            node.EndVertex   = maxVE;
            node.StartPoly   = minPS == int.MaxValue ? 0 : minPS;
            node.PolyCount   = maxPE - node.StartPoly;
        }

        // Publish the EdbNode into the flat target list (post-order: children
        // published before parent, so child indices are already known when
        // the parent is published).
        var edb = new EdbNode
        {
            BboxMin     = node.BboxMin,
            BboxMax     = node.BboxMax,
            IsLeaf      = node.IsLeaf,
            StartVertex = (uint)node.StartVertex,
            StartPoly   = (uint)node.StartPoly,
            EndVertex   = (uint)node.EndVertex,
            PolyCount   = (uint)node.PolyCount,
        };

        if (!node.IsLeaf)
        {
            edb.Child1Index = node.Front?.EdbIndex;
            edb.Child2Index = node.Back?.EdbIndex;
        }

        targetNodes.Add(edb);
        node.EdbIndex = targetNodes.Count - 1;
    }
}

/// <summary>
/// Thin terrain.bdf-specific wrapper over <see cref="EdbTreeBuilder"/>:
/// rebuilds one material layer's BSP tree and writes the resulting node
/// list into <see cref="TerrainLayer.Nodes"/>. All the actual tree-building
/// logic lives in EdbTreeBuilder so collision2.bdf (a single tree for the
/// whole mesh, no material layers) can reuse it without duplicating the
/// algorithm.
/// </summary>
public static class TerrainBspBuilder
{
    /// <summary>Result of rebuilding one layer: new layer vertices/triangles
    /// (replace the old ones in TerrainModel) — the ready node list is
    /// written directly into <see cref="TerrainLayer.Nodes"/>.</summary>
    public sealed class RebuildResult
    {
        public List<TerrainVertex>   Vertices  = new();
        public List<TerrainTriangle> Triangles = new();
    }

    /// <summary>
    /// Fully rebuilds one layer: builds the BSP over the original geometry,
    /// repacks vertices/triangles per leaf, fills layer.Nodes, and returns
    /// the new layer Vertices/Triangles.
    /// </summary>
    public static RebuildResult Rebuild(
        TerrainLayer layer,
        IReadOnlyList<TerrainVertex> allVertices,
        IReadOnlyList<TerrainTriangle> allTriangles,
        int layerVertStart,
        int layerTriStart,
        int layerVertCount,
        int layerTriCount,
        int minPolysPerLeaf = EdbTreeBuilder.DefaultMinPolysPerLeaf,
        bool useVanillaCandidateSplit = false)
    {
        layer.Nodes.Clear();

        // layer.Hc is already the same 200f default every real vanilla layer
        // header carries (SC2TerrainMaterialModel/TerrainLayer's own default),
        // so the vanilla-candidate mode's stop-rule threshold reads the
        // layer's own value instead of a separate parameter.
        var built = EdbTreeBuilder.Build(allVertices, allTriangles, layerVertStart, layerTriStart, layerVertCount, layerTriCount,
            minPolysPerLeaf, useVanillaCandidateSplit, layer.Hc);
        layer.Nodes.AddRange(built.Nodes);

        return new RebuildResult { Vertices = built.Vertices, Triangles = built.Triangles };
    }
}
}
