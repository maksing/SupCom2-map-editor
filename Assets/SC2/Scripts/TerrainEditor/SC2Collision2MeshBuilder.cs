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
            public List<int> Candidates = new(); // grid linear index (gz*mapX+gx) of not-yet-inserted points inside this tri
            public int BestCandidate = -1;
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

        /// <summary>Barycentric weights of grid point (gx,gz) relative to
        /// triangle t's 3 corners, in the XZ (map) plane only — height never
        /// enters this test, it's purely "which triangle's footprint
        /// contains this point."</summary>
        private bool Bary(Tri t, int gx, int gz, out float wa, out float wb, out float wc)
        {
            float ax = _px[t.A], az = _pz[t.A];
            float bx = _px[t.B], bz = _pz[t.B];
            float cx = _px[t.C], cz = _pz[t.C];
            var denom = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(denom) < 1e-9f) { wa = wb = wc = 0f; return false; }
            wa = ((bz - cz) * (gx - cx) + (cx - bx) * (gz - cz)) / denom;
            wb = ((cz - az) * (gx - cx) + (ax - cx) * (gz - cz)) / denom;
            wc = 1f - wa - wb;
            return true;
        }

        private bool TryAssign(Tri t, int gx, int gz)
        {
            if (!Bary(t, gx, gz, out var wa, out var wb, out var wc)) return false;
            if (wa < -Eps || wb < -Eps || wc < -Eps) return false;
            t.Candidates.Add(gz * _mapX + gx);
            return true;
        }

        private void RecomputeBest(Tri t)
        {
            t.BestCandidate = -1;
            t.BestError = -1f;
            for (var i = t.Candidates.Count - 1; i >= 0; i--)
            {
                var lin = t.Candidates[i];
                var gx = lin % _mapX; var gz = lin / _mapX;
                if (!Bary(t, gx, gz, out var wa, out var wb, out var wc) || wa < -Eps || wb < -Eps || wc < -Eps)
                {
                    t.Candidates.RemoveAt(i); // stale/mis-owned, shouldn't normally happen
                    continue;
                }
                var plane = wa * _ph[t.A] + wb * _ph[t.B] + wc * _ph[t.C];
                var err = MathF.Abs(H(gx, gz) - plane);
                if (err > t.BestError) { t.BestError = err; t.BestCandidate = lin; }
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

            for (var gz = 0; gz <= _gridSize; gz++)
            for (var gx = 0; gx <= _gridSize; gx++)
            {
                if ((gx == 0 && gz == 0) || (gx == _gridSize && gz == 0) ||
                    (gx == _gridSize && gz == _gridSize) || (gx == 0 && gz == _gridSize))
                    continue; // the 4 corners are already inserted

                if (!TryAssign(t1, gx, gz))
                    TryAssign(t2, gx, gz);
            }
            RecomputeBest(t1);
            RecomputeBest(t2);

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
                var pgx = lin % _mapX; var pgz = lin / _mapX;

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
            var candidates = new List<int>();
            foreach (var t in bad) candidates.AddRange(t.Candidates);
            foreach (var t in bad) { t.Alive = false; ClearEdges(t); }

            var newTris = new List<Tri>(boundary.Count);
            foreach (var (u, v) in boundary)
            {
                var nt = new Tri { A = u, B = v, C = p };
                SetEdge(nt);
                _tris.Add(nt);
                newTris.Add(nt);
            }

            var pgx = _px[p]; var pgz = _pz[p];
            foreach (var lin in candidates)
            {
                var gx = lin % _mapX; var gz = lin / _mapX;
                if (gx == pgx && gz == pgz) continue; // the point just inserted
                foreach (var nt in newTris)
                    if (TryAssign(nt, gx, gz)) break;
            }
            foreach (var nt in newTris) RecomputeBest(nt);
        }
    }
}
