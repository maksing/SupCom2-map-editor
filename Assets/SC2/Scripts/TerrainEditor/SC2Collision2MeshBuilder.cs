using System;
using System.Collections.Generic;

/// <summary>
/// Builds a low-poly collision mesh from a computed hfield heightmap, for
/// collision2.bdf, via greedy-insertion Delaunay TIN (Triangulated Irregular
/// Network) refinement — NOT a quadtree. There is no requirement that the
/// mesh be built from same-size (or even axis-aligned power-of-two) quads;
/// that was an earlier, incorrect self-imposed constraint of this class. A
/// quadtree only refines at the granularity of whole square cells and
/// evaluates error via a coarse interior sample stride, so it can miss a
/// small but real height feature that doesn't happen to trip a large cell's
/// sampled error — exactly the failure mode a collision mesh, which camera
/// zoom and air unit height limits depend on everywhere on the map, cannot
/// afford.
///
/// Algorithm: start from 2 triangles covering the whole map (split along one
/// diagonal). Track, per triangle, which not-yet-inserted grid points fall
/// inside it and the true height error of each against that triangle's own
/// 3-corner plane. Repeatedly insert the single point with the globally
/// worst error (wherever on the map it is) via Bowyer-Watson incremental
/// Delaunay insertion — find every triangle whose circumcircle contains the
/// new point (the "cavity"), remove them, and re-fan the cavity boundary
/// around the new point — until the triangle budget (2500) is used up. This
/// judges every individual grid point on its own merit (nothing is skipped
/// by construction, unlike a fixed quad grid) AND keeps the mesh Delaunay
/// (maximizing the minimum angle across the triangulation), which avoids the
/// long sliver triangles a plain "insert and fan, never revisit" approach
/// produces once points far apart in the original 2-triangle split are never
/// reconnected to anything better.
///
/// History: an earlier version of this class used plain fan-insertion with a
/// separate, hand-rolled Lawson edge-flip legalization pass. That approach
/// had two real, found-via-testing bugs — forcing per-triangle winding
/// instead of checking global edge-direction consistency, and flipping
/// edges without checking whether the resulting quad was even convex (which
/// silently produced inverted/self-overlapping triangles). Bowyer-Watson is
/// used here instead because it maintains the Delaunay property as an
/// invariant after every insertion via a single uniform operation (no
/// separate flip step, no special-casing for a point landing exactly on an
/// edge — that case is handled automatically since both adjacent triangles
/// end up in the same cavity).
///
/// This only affects Collision2 generation. It is unrelated to
/// SC2TerrainSimplifier (visual terrain.bdf mesh simplification), which is
/// untouched.
///
/// Diagnostic note (checked against 3 real vanilla collision2.bdf
/// files): real files store their EDB "forest" as exactly ONE flat leaf node
/// spanning the whole mesh — there is no real BSP tree in collision2.bdf in
/// practice. So mesh generation here does not need to feed into a BSP
/// builder; the caller only needs to wrap the result in a single EdbNode.
/// See SC2TerrainExporter's collision2 assembly for that.
/// </summary>
public static class SC2Collision2MeshBuilder
{
    public struct RawMesh
    {
        public List<(float X, float Y, float Z)> Vertices;
        public List<(int A, int B, int C)> Triangles;
    }

    /// <param name="heightmap">HfieldModel.Heightmap — ushort = round(worldY * 128), row-major gz*mapX+gx.</param>
    /// <param name="mapX">HfieldModel.MapX (e.g. 1025 = 1024 grid cells + 1).</param>
    /// <param name="mapY">HfieldModel.MapY.</param>
    /// <param name="maxTriangles">Hard format limit (2500 for collision2.bdf).</param>
    /// <param name="heightOffset">Uniform vertical shift added to every
    /// output vertex's Y (e.g. SC2TerrainData.Collision2HeightOffset). Added
    /// ONLY at final vertex emission — error-driven point selection uses the
    /// raw heightmap, since a uniform additive shift changes no point's
    /// error relative to any other and so cannot affect which points get
    /// chosen.</param>
    public static RawMesh Build(ushort[] heightmap, int mapX, int mapY, int maxTriangles = 2500, float heightOffset = 0f)
        => new Builder(heightmap, mapX, mapY).Run(maxTriangles, heightOffset);

    private sealed class Builder
    {
        private const float Eps = 1e-4f;

        private readonly ushort[] _heightmap;
        private readonly int _mapX;
        private readonly int _gridSize;
        private readonly float _cellSize;

        // Inserted mesh points (grid-index space + cached exact height).
        private readonly List<int> _px = new();
        private readonly List<int> _pz = new();
        private readonly List<float> _ph = new();

        private sealed class Tri
        {
            public int A, B, C; // indices into _px/_pz/_ph, fixed CCW order (see class doc comment)
            public bool Alive = true;
            // Not-yet-inserted grid points inside this tri, packed as gx | gz << 16, in the order they were assigned.
            public List<int> Candidates = new();
            public int BestCandidate = -1; // packed like the candidates
            public float BestError = -1f;
        }

        private readonly List<Tri> _tris = new();
        // Directed edge (u,v) [point indices] -> the triangle whose CCW
        // boundary traverses u->v. Lets us find "the triangle on the other
        // side of edge (u,v)" via _edgeOwner[(v,u)] in O(1).
        private readonly Dictionary<(int, int), Tri> _edgeOwner = new();

        public Builder(ushort[] heightmap, int mapX, int mapY)
        {
            if (mapX - 1 != mapY - 1)
                throw new ArgumentException($"SC2Collision2MeshBuilder expects a square grid; got {mapX - 1}x{mapY - 1}.");
            _heightmap = heightmap;
            _mapX = mapX;
            _gridSize = mapX - 1;
            // Was hardcoded to "1024f / _gridSize", silently
            // assuming every map is exactly 1024 world units regardless of
            // its real size - only correct by coincidence for a 1024
            // CostsGridSize map, and WRONG (e.g. half scale on a 2048 map)
            // for any other size. World units = grid cells 1:1 is the
            // established convention everywhere else this project samples
            // a heightmap (SC2HfieldProjectionService, SC2LiveHeightGrid,
            // SC2CostsShell) - no separate cell-size scaling belongs here.
            _cellSize = 1f;
        }

        private float H(int gx, int gz) => _heightmap[gz * _mapX + gx] / 128f;

        private int AddPoint(int gx, int gz)
        {
            _px.Add(gx); _pz.Add(gz); _ph.Add(H(gx, gz));
            return _px.Count - 1;
        }

        private void SetEdge(Tri t)
        {
            _edgeOwner[(t.A, t.B)] = t;
            _edgeOwner[(t.B, t.C)] = t;
            _edgeOwner[(t.C, t.A)] = t;
        }

        private void ClearEdges(Tri t)
        {
            _edgeOwner.Remove((t.A, t.B));
            _edgeOwner.Remove((t.B, t.C));
            _edgeOwner.Remove((t.C, t.A));
        }

        private const int PackShift = 16;
        private const int PackMask = (1 << PackShift) - 1;

        /// <summary>What testing a point against one triangle needs, worked out once from its three corners. The arithmetic is the same
        /// expression the point test always used (barycentric weights in the XZ plane), only with the corner terms computed up front.</summary>
        private struct TriGeom
        {
            public float Cx, Cz, K1, K2, K3, K4, Denom, Ha, Hb, Hc;
            public bool Degenerate;
            public int MinX, MaxX, MinZ, MaxZ; // integer bounding box of the corners
        }

        private TriGeom MakeGeom(Tri t)
        {
            float ax = _px[t.A], az = _pz[t.A];
            float bx = _px[t.B], bz = _pz[t.B];
            float cx = _px[t.C], cz = _pz[t.C];
            var g = new TriGeom
            {
                Cx = cx, Cz = cz,
                K1 = bz - cz, K2 = cx - bx, K3 = cz - az, K4 = ax - cx,
                Denom = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz),
                Ha = _ph[t.A], Hb = _ph[t.B], Hc = _ph[t.C],
                MinX = Math.Min(_px[t.A], Math.Min(_px[t.B], _px[t.C])),
                MaxX = Math.Max(_px[t.A], Math.Max(_px[t.B], _px[t.C])),
                MinZ = Math.Min(_pz[t.A], Math.Min(_pz[t.B], _pz[t.C])),
                MaxZ = Math.Max(_pz[t.A], Math.Max(_pz[t.B], _pz[t.C])),
            };
            g.Degenerate = MathF.Abs(g.Denom) < 1e-9f;
            return g;
        }

        // Below this many points a distribution is not worth handing to other threads.
        private const int ParallelMinPoints = 12000;
        private const int ParallelChunkPoints = 4096;

        /// <summary>Gives every point of <paramref name="points"/> (packed gx | gz << 16) to the first of the triangles whose footprint
        /// holds it — barycentric weights in the XZ plane only, height never enters that test — and measures its height error against
        /// that triangle's own 3-corner plane, keeping the worst per triangle (the last of equally bad ones, which is the one a
        /// backwards scan with a strict comparison always picked). The integer bounding box is a pure shortcut: a grid point outside it
        /// is further than the Eps tolerance from the triangle, so the barycentric test would reject it too.
        /// The result does not depend on how the work is split: a point only looks at the triangles, and the per-triangle lists and
        /// best candidates are put together in the order of the points. So large sets are cut into runs, done on several threads and
        /// joined in order.</summary>
        private void Distribute(int[] points, int count, int skipPacked, Tri[] tris, TriGeom[] geom)
        {
            var n = tris.Length;
            var chunkCount = count >= ParallelMinPoints ? Math.Min(Environment.ProcessorCount, count / ParallelChunkPoints) : 1;
            if (chunkCount <= 1)
            {
                var lists = new List<int>[n];
                var bestErr = new float[n];
                var bestPos = new int[n];
                for (var k = 0; k < n; k++) { lists[k] = tris[k].Candidates; bestErr[k] = tris[k].BestError; bestPos[k] = tris[k].BestCandidate; }
                DistributeRange(points, 0, count, skipPacked, geom, lists, bestErr, bestPos);
                for (var k = 0; k < n; k++) { tris[k].BestError = bestErr[k]; tris[k].BestCandidate = bestPos[k]; }
                return;
            }

            var chunkLists = new List<int>[chunkCount][];
            var chunkErr = new float[chunkCount][];
            var chunkPos = new int[chunkCount][];
            var span = (count + chunkCount - 1) / chunkCount;
            System.Threading.Tasks.Parallel.For(0, chunkCount, c =>
            {
                var lists = new List<int>[n];
                var bestErr = new float[n];
                var bestPos = new int[n];
                for (var k = 0; k < n; k++) { lists[k] = new List<int>(); bestErr[k] = -1f; bestPos[k] = -1; }
                var from = c * span;
                DistributeRange(points, from, Math.Min(count, from + span), skipPacked, geom, lists, bestErr, bestPos);
                chunkLists[c] = lists; chunkErr[c] = bestErr; chunkPos[c] = bestPos;
            });

            for (var k = 0; k < n; k++)
            {
                var total = tris[k].Candidates.Count;
                for (var c = 0; c < chunkCount; c++) total += chunkLists[c][k].Count;
                tris[k].Candidates.Capacity = Math.Max(tris[k].Candidates.Capacity, total);
                for (var c = 0; c < chunkCount; c++)
                {
                    tris[k].Candidates.AddRange(chunkLists[c][k]);
                    if (chunkPos[c][k] >= 0 && chunkErr[c][k] >= tris[k].BestError)
                    {
                        tris[k].BestError = chunkErr[c][k];
                        tris[k].BestCandidate = chunkPos[c][k];
                    }
                }
            }
        }

        private void DistributeRange(int[] points, int from, int to, int skipPacked, TriGeom[] geom, List<int>[] lists, float[] bestErr, int[] bestPos)
        {
            var n = geom.Length;
            for (var i = from; i < to; i++)
            {
                var packed = points[i];
                if (packed == skipPacked) continue;
                var gx = packed & PackMask;
                var gz = packed >> PackShift;
                for (var k = 0; k < n; k++)
                {
                    ref readonly var g = ref geom[k];
                    if (gx < g.MinX || gx > g.MaxX || gz < g.MinZ || gz > g.MaxZ || g.Degenerate) continue;
                    var dx = gx - g.Cx;
                    var dz = gz - g.Cz;
                    var wa = (g.K1 * dx + g.K2 * dz) / g.Denom;
                    var wb = (g.K3 * dx + g.K4 * dz) / g.Denom;
                    var wc = 1f - wa - wb;
                    if (wa < -Eps || wb < -Eps || wc < -Eps) continue;
                    lists[k].Add(packed);
                    var plane = wa * g.Ha + wb * g.Hb + wc * g.Hc;
                    var err = MathF.Abs(H(gx, gz) - plane);
                    if (err >= bestErr[k]) { bestErr[k] = err; bestPos[k] = packed; }
                    break;
                }
            }
        }

        /// <summary>Standard incircle predicate (map-space XZ only — the
        /// cavity decision is about triangle shape quality in plan view,
        /// not height): returns &gt; 0 iff (dx,dz) lies strictly inside the
        /// circumcircle of the triangle (ax,az)-(bx,bz)-(cx,cz), PROVIDED
        /// (a,b,c) is wound counter-clockwise in the STANDARD mathematical
        /// sense. This class's own fixed winding convention (ny = uz*vx -
        /// ux*vz &gt; 0, verified elsewhere against real terrain data) is
        /// the NEGATION of that standard cross product — i.e. every
        /// triangle here is clockwise in the standard sense — so callers
        /// must pass points in the REVERSE of this class's own (A,B,C)
        /// order (see FindCavity) or this predicate's sign is inverted.
        /// Double precision to keep the determinant stable for map
        /// coordinates up to ~1024.</summary>
        private static double InCircle(double ax, double az, double bx, double bz, double cx, double cz, double dx, double dz)
        {
            var adx = ax - dx; var adz = az - dz;
            var bdx = bx - dx; var bdz = bz - dz;
            var cdx = cx - dx; var cdz = cz - dz;
            var adSq = adx * adx + adz * adz;
            var bdSq = bdx * bdx + bdz * bdz;
            var cdSq = cdx * cdx + cdz * cdz;
            return adx * (bdz * cdSq - bdSq * cdz) - adz * (bdx * cdSq - bdSq * cdx) + adSq * (bdx * cdz - bdz * cdx);
        }

        private static int Apex(Tri t, int u, int v)
        {
            if (t.A != u && t.A != v) return t.A;
            if (t.B != u && t.B != v) return t.B;
            return t.C;
        }

        public RawMesh Run(int maxTriangles, float heightOffset)
        {
            var sw = AddPoint(0, 0);
            var se = AddPoint(_gridSize, 0);
            var ne = AddPoint(_gridSize, _gridSize);
            var nw = AddPoint(0, _gridSize);

            // Fixed CCW convention, verified to give normal.y > 0 for a flat
            // quad: (SW,NW,NE) and (SW,NE,SE). Every Bowyer-Watson insertion
            // below re-fans new triangles as (u,v,p) using boundary edges
            // that were themselves owned by a correctly-wound triangle, so
            // this orientation is preserved automatically throughout.
            var t1 = new Tri { A = sw, B = nw, C = ne };
            var t2 = new Tri { A = sw, B = ne, C = se };
            SetEdge(t1); SetEdge(t2);
            _tris.Add(t1); _tris.Add(t2);

            // Every grid point but the four corners (already inserted), row by row.
            var all = new int[(_gridSize + 1) * (_gridSize + 1) - 4];
            var allCount = 0;
            for (var gz = 0; gz <= _gridSize; gz++)
            for (var gx = 0; gx <= _gridSize; gx++)
            {
                if ((gx == 0 && gz == 0) || (gx == _gridSize && gz == 0) ||
                    (gx == _gridSize && gz == _gridSize) || (gx == 0 && gz == _gridSize))
                    continue;
                all[allCount++] = gx | (gz << PackShift);
            }
            var first = new[] { t1, t2 };
            Distribute(all, allCount, -1, first, new[] { MakeGeom(t1), MakeGeom(t2) });

            var aliveCount = 2;

            while (true)
            {
                Tri? worst = null;
                var worstErr = 0f;
                foreach (var t in _tris)
                {
                    if (!t.Alive || t.BestCandidate < 0) continue;
                    if (t.BestError > worstErr) { worstErr = t.BestError; worst = t; }
                }
                if (worst == null) break; // every remaining candidate is already exactly represented

                var lin = worst.BestCandidate;
                var pgx = lin & PackMask; var pgz = lin >> PackShift;

                // Compute the cavity WITHOUT mutating anything yet, so the
                // triangle budget can be checked before committing (the
                // cavity's net triangle-count change isn't known in advance
                // — it depends on how many neighboring triangles also have
                // this point inside their circumcircle).
                var (bad, boundary) = FindCavity(pgx, pgz, worst);
                var delta = boundary.Count - bad.Count;
                if (aliveCount + delta > maxTriangles) break;

                var p = AddPoint(pgx, pgz);
                CommitCavity(bad, boundary, p);
                aliveCount += delta;
            }

            var vertices = new List<(float X, float Y, float Z)>(_px.Count);
            for (var i = 0; i < _px.Count; i++)
                vertices.Add((_px[i] * _cellSize, _ph[i] + heightOffset, _pz[i] * _cellSize));

            // On an integer grid, exact collinearity among 3 grid points is
            // common (not a rare fluke) — a Bowyer-Watson cavity boundary
            // edge that happens to be exactly collinear with the inserted
            // point produces a genuinely zero-area triangle. Such a
            // triangle covers no real area, so dropping it costs nothing
            // and just avoids shipping degenerate geometry (undefined
            // winding/normal) in the final mesh.
            var triangles = new List<(int, int, int)>(aliveCount);
            foreach (var t in _tris)
            {
                if (!t.Alive) continue;
                var va = vertices[t.A]; var vb = vertices[t.B]; var vc = vertices[t.C];
                var area = MathF.Abs((vb.X - va.X) * (vc.Z - va.Z) - (vc.X - va.X) * (vb.Z - va.Z));
                if (area > 1e-3f) triangles.Add((t.A, t.B, t.C));
            }

            return new RawMesh { Vertices = vertices, Triangles = triangles };
        }

        /// <summary>Bowyer-Watson cavity discovery: BFS outward from the
        /// known containing triangle, collecting every triangle whose
        /// circumcircle contains (pgx,pgz) (the "bad" triangles — this is
        /// guaranteed contiguous by the Delaunay-empty-circumcircle
        /// invariant), plus the directed boundary edges of that region
        /// (each owned by a bad triangle whose neighbor across it is NOT
        /// bad, i.e. the edges that will bound the new fan of triangles
        /// around the inserted point).</summary>
        private (List<Tri> bad, List<(int u, int v)> boundary) FindCavity(int pgx, int pgz, Tri seed)
        {
            var bad = new List<Tri> { seed };
            var badSet = new HashSet<Tri> { seed };
            var queue = new Queue<Tri>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var (u, v) in Edges(t))
                {
                    if (!_edgeOwner.TryGetValue((v, u), out var nb) || !nb.Alive || badSet.Contains(nb)) continue;
                    var apex = Apex(nb, v, u);
                    // nb's own fixed-convention order starting from this edge
                    // is (v,u,apex) — clockwise in the standard sense, so
                    // InCircle needs the REVERSED (u,v,apex) to get a
                    // standard-CCW triple (see InCircle's doc comment).
                    var det = InCircle(_px[u], _pz[u], _px[v], _pz[v], _px[apex], _pz[apex], pgx, pgz);
                    if (det >= -1e-6) // inside, or on, nb's circumcircle
                    {
                        badSet.Add(nb);
                        bad.Add(nb);
                        queue.Enqueue(nb);
                    }
                }
            }

            var boundary = new List<(int, int)>();
            foreach (var t in bad)
                foreach (var (u, v) in Edges(t))
                {
                    var hasNeighbor = _edgeOwner.TryGetValue((v, u), out var nb);
                    if (!hasNeighbor || nb == null || !badSet.Contains(nb))
                        boundary.Add((u, v));
                }

            return (bad, boundary);
        }

        private static IEnumerable<(int, int)> Edges(Tri t)
        {
            yield return (t.A, t.B);
            yield return (t.B, t.C);
            yield return (t.C, t.A);
        }

        private void CommitCavity(List<Tri> bad, List<(int u, int v)> boundary, int p)
        {
            var total = 0;
            foreach (var t in bad) total += t.Candidates.Count;
            var candidates = new int[total];
            var at = 0;
            foreach (var t in bad) { t.Candidates.CopyTo(candidates, at); at += t.Candidates.Count; }
            foreach (var t in bad) { t.Alive = false; ClearEdges(t); }

            var newTris = new Tri[boundary.Count];
            var geom = new TriGeom[boundary.Count];
            for (var i = 0; i < newTris.Length; i++)
            {
                var (u, v) = boundary[i];
                var nt = new Tri { A = u, B = v, C = p };
                SetEdge(nt);
                _tris.Add(nt);
                newTris[i] = nt;
                geom[i] = MakeGeom(nt);
            }

            // The point just inserted is not a candidate any more.
            Distribute(candidates, total, _px[p] | (_pz[p] << PackShift), newTris, geom);
        }
    }
}
