using UnityEngine;

/// <summary>
/// UV1 texture-coordinate generation for exported terrain geometry — used
/// wherever the exporter/RNM baker used to assign `Uv1U = pos.x / uvScale,
/// Uv1V = pos.z / uvScale` directly.
///
/// That planar (world X/Z) projection makes a tiled texture visibly stretch
/// on steep terrain: walking up a cliff covers far more real 3D surface
/// distance than the flat X/Z distance between the same two grid columns,
/// so the same UV span (and therefore the same tile of texture) gets
/// smeared across a much longer real-world slope.
///
/// FIRST ATTEMPT (reverted): accumulated real 3D edge length independently
/// per ROW (for u) and per COLUMN (for v) — i.e. u depended on a row's OWN
/// height profile, v on a column's OWN height profile. That is NOT safe:
/// two neighboring rows with different height profiles (real terrain
/// varies in both X and Z at once, not just along one axis) accumulate
/// unrelated u totals at the same x, so the map (x,z) -> (u,v) is not just
/// stretched, it can locally FOLD — its Jacobian sign can flip. A flipped
/// triangle samples the texture mirrored/torn relative to its neighbors,
/// which is exactly the streaky, overlapping mess a real export showed.
///
/// THIS version fixes that by making u depend ONLY on x (never z) and v
/// depend ONLY on z (never x) — u(x) is the accumulated stretch factor
/// AVERAGED ACROSS EVERY ROW at each x-step (so it's still data-driven, not
/// a flat 1-to-1 copy of world X), and v(z) is likewise averaged across
/// every column. Because u is a strictly increasing function of x alone and
/// v a strictly increasing function of z alone, the map (x,z) -> (u(x),v(z))
/// has Jacobian diag(du/dx, dv/dz) with both entries always positive — a
/// pure non-uniform axis scale, which by construction can NEVER flip a
/// triangle's winding, no matter how the height field varies. The
/// trade-off: a ridge that only runs through part of the grid gets a
/// smaller correction than its own local slope alone would call for (it's
/// averaged in with flatter rows/columns) — deliberately conservative
/// rather than risking the previous failure mode again.
/// </summary>
public static class SC2TerrainUv1Unwrap
{
    /// <summary>Builds per-axis stretch profiles — u[x] for x in
    /// [0..gridWidth], v[z] for z in [0..gridHeight] — NOT a
    /// (gridWidth+1)*(gridHeight+1) 2D grid like the reverted first attempt;
    /// u only varies with x, v only varies with z, which is exactly what
    /// keeps this construction winding-safe (see class doc comment).
    /// Deliberately UNSCALED (raw world-unit accumulated distance, not
    /// divided by any tiling scale) — a pair's own UV1 tiling scale
    /// (SC2TerrainData.EffectiveUvTilingScale) can differ per pair even
    /// though every pair shares the same underlying terrain geometry, so
    /// scaling belongs at Sample's call site.</summary>
    public static void BuildProfiles(float[] heights, int gridWidth, int gridHeight, float cellSize,
        out float[] u, out float[] v, float heightWeight = 1f)
    {
        var vw = gridWidth + 1;
        var vh = gridHeight + 1;
        u = new float[vw];
        v = new float[vh];
        if (heights == null || heights.Length < vw * vh || cellSize <= 0f || vw < 1 || vh < 1) return;

        // heightWeight multiplies dh before it's folded into the arc-length
        // distance — 1 is the physically real 3D edge length; higher values
        // deliberately exaggerate the correction (SC2TerrainData.
        // Uv1HeightWeight's own doc comment) so a steep step claims even
        // more UV space than its real surface distance alone would give it.
        // Still strictly increasing in x alone (u) / z alone (v) for ANY
        // weight >= 0, so this can never affect the winding-safety guarantee
        // (see this class's own doc comment) — it only scales HOW MUCH
        // extra distance a slope adds, never which direction.
        var hw2 = heightWeight * heightWeight;

        // u[x]: for each x-step, average sqrt(cellSize^2 + (dh*weight)^2)
        // across EVERY row at that x — same step size added for every row
        // regardless of z, so u ends up identical along any vertical grid
        // line by construction (no per-row divergence to fold the map).
        u[0] = 0f;
        for (var x = 1; x < vw; x++)
        {
            var sum = 0f;
            for (var z = 0; z < vh; z++)
            {
                var dh = heights[z * vw + x] - heights[z * vw + x - 1];
                sum += Mathf.Sqrt(cellSize * cellSize + dh * dh * hw2);
            }
            u[x] = u[x - 1] + sum / vh;
        }

        v[0] = 0f;
        for (var z = 1; z < vh; z++)
        {
            var sum = 0f;
            for (var x = 0; x < vw; x++)
            {
                var dh = heights[z * vw + x] - heights[(z - 1) * vw + x];
                sum += Mathf.Sqrt(cellSize * cellSize + dh * dh * hw2);
            }
            v[z] = v[z - 1] + sum / vw;
        }
    }

    /// <summary>Samples a BuildProfiles result at an arbitrary position in
    /// the SAME local coordinate frame the profiles were built in (world
    /// space for the core terrain, background-local
    /// space for the background) — export mesh simplification can place a
    /// vertex anywhere along an edge, not just on an original grid vertex,
    /// so this can't be a plain array lookup. u and v are looked up
    /// independently (1D lerp each, not 2D bilinear) since each is a
    /// function of only one axis. Clamps to the profile bounds.</summary>
    public static Vector2 Sample(float[] u, float[] v, int gridWidth, int gridHeight, float cellSize, float localX, float localZ)
    {
        if (cellSize <= 0f || gridWidth <= 0 || gridHeight <= 0 || u.Length == 0 || v.Length == 0) return Vector2.zero;
        var fx = Mathf.Clamp(localX / cellSize, 0f, gridWidth);
        var fz = Mathf.Clamp(localZ / cellSize, 0f, gridHeight);
        var x0 = Mathf.FloorToInt(fx); var x1 = Mathf.Min(x0 + 1, gridWidth); var tx = fx - x0;
        var z0 = Mathf.FloorToInt(fz); var z1 = Mathf.Min(z0 + 1, gridHeight); var tz = fz - z0;
        var uu = Mathf.Lerp(u[x0], u[x1], tx);
        var vv = Mathf.Lerp(v[z0], v[z1], tz);
        return new Vector2(uu, vv);
    }

    /// <summary>Like <see cref="Sample"/>, but a position outside the profiled grid continues the
    /// profile linearly with the slope of its last cell instead of clamping to the edge value, so a
    /// surface reaching past the profiled grid keeps texturing at that grid's own scale instead of
    /// collapsing into streaks of one clamped coordinate.</summary>
    public static Vector2 SampleExtended(float[] u, float[] v, int gridWidth, int gridHeight, float cellSize, float localX, float localZ)
    {
        if (cellSize <= 0f || gridWidth <= 0 || gridHeight <= 0 || u.Length == 0 || v.Length == 0) return Vector2.zero;
        var inside = Sample(u, v, gridWidth, gridHeight, cellSize, localX, localZ);
        var fx = localX / cellSize;
        var fz = localZ / cellSize;
        var uu = inside.x; var vv = inside.y;
        if (fx < 0f && gridWidth >= 1 && u.Length > 1) uu += fx * (u[1] - u[0]);
        else if (fx > gridWidth && u.Length > gridWidth) uu += (fx - gridWidth) * (u[gridWidth] - u[gridWidth - 1]);
        if (fz < 0f && gridHeight >= 1 && v.Length > 1) vv += fz * (v[1] - v[0]);
        else if (fz > gridHeight && v.Length > gridHeight) vv += (fz - gridHeight) * (v[gridHeight] - v[gridHeight - 1]);
        return new Vector2(uu, vv);
    }
}
