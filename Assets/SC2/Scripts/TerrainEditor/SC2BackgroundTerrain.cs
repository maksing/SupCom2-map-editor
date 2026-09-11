using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Info;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

/// <summary>
/// Procedural generator for the optional decorative "background terrain" —
/// a second, low-detail SC2TerrainData instance surrounding the playable
/// core on all sides. Deliberately reuses SC2TerrainData itself (not a
/// bespoke class) so the exact same SC2TerrainLayerBuilder/
/// SC2TerrainSimplifier pipeline the core uses also drives the background,
/// including its own user-tunable per-pair SimplifyRatio.
///
/// Design (see the "Decorative background terrain" plan for the full
/// rationale):
///  - The background grid is generated at a CELL SIZE THAT'S A WHOLE
///    MULTIPLE of the core's own CellSize (ComputeCellMultiplier), not
///    necessarily matching it 1:1 — a coarser, genuinely low-detail
///    resolution is both the point ("decorative background") and the only
///    way a large core (the default grid is already 512x512 cells) can
///    still fit inside the 131,072-triangle budget with real margin left
///    around the hole. Because the multiplier always evenly divides the
///    core's GridWidth/GridHeight, the core's own rectangle boundary still
///    lands EXACTLY on a background grid line regardless of the chosen
///    multiplier — alignment never depended on matching resolution, only
///    on being a whole multiple of it. World-space bounds are rounded
///    outward to that (possibly coarser) cell size (ComputeBounds), so
///    excluding the core's inner square stays a trivial per-CELL
///    inside/outside test in integer grid-index space
///    (BuildPairIndexAndCarveHole), never a partial-triangle clip.
///  - X/Z continuity across the seam is automatic from that shared grid.
///    Only height (Y) needs an explicit step: the ring of background
///    vertices immediately bordering the hole is overwritten with the
///    core's real Heights[] at the same world position
///    (CopyRingHeightsFromCore), and that same ring — plus the core's own
///    true outer perimeter — must be LOCKED during simplification
///    (SC2TerrainLayerBuilder's additionalLockedVertices) so the guarantee
///    survives arbitrary SimplifyRatio choices on either side.
///  - The background's own local grid-index (0,0) is NOT world (0,0) — it
///    represents whatever ComputeBounds' minX/minZ resolves to (frequently
///    negative). BuildForExportOrPreview returns that offset directly;
///    callers translate the built mesh's vertices by it.
/// </summary>
public static class SC2BackgroundTerrain
{
    /// <summary>Ceiling on the background's PRE-simplification grid
    /// dimension (cells per side) — 256*256*2 = 131,072 triangles, exactly
    /// the plan's own stated maximum, applied to the SOURCE grid directly
    /// (SimplifyRatio, default 0f, only ever reduces further BELOW this;
    /// it doesn't exist to reach it).</summary>
    private const int MaxGridDimension = 256;

    /// <summary>Minimum margin (in background-grid cells) reserved on
    /// EVERY side of the carved hole, so the background can never end up
    /// smaller than — or exactly equal to — the hole itself (which would
    /// carve away 100% of it and render nothing, the actual bug this
    /// margin exists to prevent). See ComputeCellMultiplier's own comment
    /// for why a fine CellSize match with the core isn't always possible
    /// within the 131,072 budget once this margin is accounted for.</summary>
    private const int MinMarginCells = 8;

    /// <summary>Picks how many core cells one background cell should span
    /// (a coarser, decorative resolution — the whole point of "low-detail
    /// background," not a corner-cut). MUST divide both GridWidth and
    /// GridHeight EXACTLY: this is what keeps the core rectangle's own
    /// boundary landing on an exact background grid line at ANY multiplier,
    /// not just 1 — the alignment guarantee never depended on matching the
    /// core's fine resolution 1:1, only on background cells being a whole
    /// multiple of it.
    ///
    /// Root cause this fixes — the background failed to render at all after
    /// the 131,072-triangle cap landed: the core's own default
    /// grid is 512x512 cells (SC2TerrainShell.kTargetTriCount = 524,288
    /// triangles) — already 4x wider than a 256x256 background budget at
    /// matching CellSize, so the "hole" was bigger than the whole
    /// background grid and BuildPairIndexAndCarveHole carved away every
    /// single cell. Using a coarser background cell (k * core CellSize)
    /// shrinks the hole's footprint in background-grid-cell terms without
    /// touching the core at all, freeing room for a real margin within the
    /// same 131,072-triangle budget.</summary>
    /// <param name="size">The requested world-space footprint (Background
    /// tab's own "Footprint Size" slider). The slider used to have no
    /// visible effect:
    /// this used to ignore size entirely and pick k purely from the core's
    /// own grid — meaning ComputeBounds' own half-extent ceiling
    /// (MaxGridDimension cells at whatever bgCell this method happened to
    /// pick) had nothing to do with what the user actually asked for, and
    /// could end up far below the slider's own declared range, silently
    /// clamping every drag past that point to the same value. Now picks
    /// the smallest (finest) k that BOTH leaves real margin around the
    /// carved core hole AND leaves the grid able to actually reach the
    /// requested size within the same MaxGridDimension cell budget — a
    /// bigger request coarsens the background as needed to stay in
    /// budget, instead of silently refusing to grow at all.</param>
    private static int ComputeCellMultiplier(SC2TerrainData coreData, int size)
    {
        var gw = Mathf.Max(1, coreData.GridWidth);
        var gh = Mathf.Max(1, coreData.GridHeight);
        var largerSide = Mathf.Max(gw, gh);
        var coreCell = Mathf.Max(0.0001f, coreData.CellSize);
        for (var k = 1; k <= 4096; k *= 2)
        {
            if (gw % k != 0 || gh % k != 0) continue;
            if (largerSide / k > MaxGridDimension - MinMarginCells) continue;
            var bgCellCandidate = coreCell * k;
            var halfCellsNeeded = Mathf.CeilToInt(Mathf.Max(bgCellCandidate, size * 0.5f) / bgCellCandidate);
            if (2 * halfCellsNeeded <= MaxGridDimension) return k;
        }
        // Fallback for a GridWidth/GridHeight with no convenient shared
        // power-of-two divisor (e.g. a hand-edited grid size): the largest
        // power-of-two divisor of gcd(gw,gh) — may leave less margin than
        // MinMarginCells, or in a pathological case none at all, but is
        // still exact-aligned and strictly better than k=1's guaranteed
        // total hole-out.
        var g = Gcd(gw, gh);
        var best = 1;
        while (best * 2 <= g && g % (best * 2) == 0) best *= 2;
        return Mathf.Max(1, best);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) { (a, b) = (b, a % b); }
        return Mathf.Max(1, a);
    }

    /// <summary>The background's own world-space rectangle, rounded
    /// outward to bgCell — purely derived from coreData + size, never
    /// persisted, so it can never drift out of sync with either. Both
    /// dimensions come out as exact multiples of bgCell by construction,
    /// which is what guarantees grid-line alignment.</summary>
    public static void ComputeBounds(SC2TerrainData coreData, int size, out float minX, out float minZ, out float maxX, out float maxZ, out float bgCell)
    {
        var coreCell = Mathf.Max(0.0001f, coreData.CellSize);
        bgCell = coreCell * ComputeCellMultiplier(coreData, size);
        var coreW = coreData.GridWidth * coreCell;
        var coreD = coreData.GridHeight * coreCell;
        var centerX = coreW * 0.5f;
        var centerZ = coreD * 0.5f;
        var half = Mathf.Max(bgCell, size * 0.5f);
        half = Mathf.Min(half, MaxGridDimension * 0.5f * bgCell);
        minX = Mathf.Floor((centerX - half) / bgCell) * bgCell;
        minZ = Mathf.Floor((centerZ - half) / bgCell) * bgCell;
        maxX = Mathf.Ceil((centerX + half) / bgCell) * bgCell;
        maxZ = Mathf.Ceil((centerZ + half) / bgCell) * bgCell;
    }

    /// <summary>The core's own grid-index range, expressed in the
    /// BACKGROUND's local (possibly coarser) grid-index space — i.e. where
    /// the core's (0,0) and (GridWidth,GridHeight) corners land once
    /// you've moved to a coordinate system whose own (0,0) is
    /// ComputeBounds' minX/minZ and whose own cell is bgCell. Exact
    /// integer arithmetic — safe because bgCell is always coreCell times
    /// an integer that evenly divides both GridWidth and GridHeight
    /// (ComputeCellMultiplier), and ComputeBounds always rounds to whole
    /// bgCells.</summary>
    private static void ComputeCoreGridRange(SC2TerrainData coreData, float minX, float minZ, float bgCell,
        out int coreGxMin, out int coreGzMin, out int coreGxMax, out int coreGzMax)
    {
        coreGxMin = Mathf.RoundToInt((0f - minX) / bgCell);
        coreGzMin = Mathf.RoundToInt((0f - minZ) / bgCell);
        var k = Mathf.Max(1, Mathf.RoundToInt(bgCell / Mathf.Max(0.0001f, coreData.CellSize)));
        coreGxMax = coreGxMin + coreData.GridWidth / k;
        coreGzMax = coreGzMin + coreData.GridHeight / k;
    }

    /// <summary>The background's world-space offset (minX/minZ of
    /// ComputeBounds) — every vertex a caller gets back from
    /// BuildForExportOrPreview, or reads directly out of
    /// coreData.BackgroundTerrainData.Heights via grid index, still needs
    /// this added to land in real world space. Cheap/deterministic, safe
    /// to call every frame (e.g. from an edit tool converting a raycast
    /// hit back to grid coordinates).</summary>
    public static Vector3 GetWorldOffset(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.IsInitialized) return Vector3.zero;
        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out _, out _, out _);
        return new Vector3(minX, 0f, minZ);
    }

    /// <summary>Returns the existing coreData.BackgroundTerrainData
    /// UNCHANGED if one already exists AND its stored grid still matches
    /// what current settings would produce — never overwrites hand-painted
    /// heights/materials/pairs in that case. Calls Regenerate (a real,
    /// fresh build) when none exists yet, OR when the stored grid has
    /// drifted out of sync with the current CellSize/GridWidth/GridHeight/
    /// BackgroundTerrainSize — changing Footprint Size without clicking Regenerate left
    /// the OLD grid/offset baked into BackgroundTerrainData while every
    /// preview/export tick recomputed a DIFFERENT offset/cell size from
    /// the NEW setting — the two disagreed about exactly where the seam
    /// sits, opening a gap). A genuine topology mismatch has nothing worth
    /// preserving anyway — Heights/TrianglePairIndex are sized for the OLD
    /// grid and can't meaningfully carry over to a different one — so
    /// auto-regenerating here isn't a new "discard edits" risk, it's
    /// fixing data that was already wrong the moment settings changed.
    /// This is what preview/export should call on every tick; the
    /// EXPLICIT "Regenerate" button exists for the one real discard case
    /// this can't cover — the user wants a fresh procedural pass at the
    /// SAME settings, overwriting their own hand edits on purpose.</summary>
    public static SC2TerrainData EnsureGenerated(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.IsInitialized) return null;
        var bg = coreData.BackgroundTerrainData;
        if (bg == null) return Regenerate(coreData);
        return TopologyMatches(coreData, bg) ? bg : Regenerate(coreData);
    }

    /// <summary>True when bg's own stored CellSize/GridWidth/GridHeight
    /// already match what ComputeBounds/ComputeCellMultiplier would
    /// produce right now for coreData's CURRENT state — i.e. the offset
    /// GetWorldOffset returns today is the same one bg's vertices were
    /// actually built against.</summary>
    private static bool TopologyMatches(SC2TerrainData coreData, SC2TerrainData bg)
    {
        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out var bgCell);
        var expectedGw = Mathf.Max(2, Mathf.RoundToInt((maxX - minX) / bgCell));
        var expectedGh = Mathf.Max(2, Mathf.RoundToInt((maxZ - minZ) / bgCell));
        return bg.GridWidth == expectedGw && bg.GridHeight == expectedGh && Mathf.Abs(bg.CellSize - bgCell) < 0.0001f;
    }

    /// <summary>Builds/refreshes coreData.BackgroundTerrainData IN PLACE
    /// (same instance reused across calls, matching SC2WaterPreview's own
    /// "rebuild, don't reallocate every tick" convention) — ALWAYS
    /// overwrites Heights/TrianglePairIndex/Materials from scratch, i.e.
    /// discards any hand-painted geometry/pairs. Fully deterministic given
    /// coreData + BackgroundTerrainSize, EXCEPT the one thing a user
    /// actually tunes via the Background tab's own slider (not painting) —
    /// the background's single default pair's SimplifyRatio — which is
    /// read from whatever instance already exists (if any) and carried
    /// forward. Call only from an explicit user action (see
    /// EnsureGenerated's own doc comment for the non-destructive
    /// alternative every per-tick caller should use instead).</summary>
    public static SC2TerrainData Regenerate(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.IsInitialized) return null;

        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out var bgCell);
        var gw = Mathf.Max(2, Mathf.RoundToInt((maxX - minX) / bgCell));
        var gh = Mathf.Max(2, Mathf.RoundToInt((maxZ - minZ) / bgCell));
        var k = Mathf.Max(1, Mathf.RoundToInt(bgCell / Mathf.Max(0.0001f, coreData.CellSize)));

        var bg = coreData.BackgroundTerrainData;
        // Default 0f (no further reduction): the source grid itself is
        // already capped at MaxGridDimension, i.e. the plan's own
        // 131,072-triangle default/ceiling — SimplifyRatio only needs to
        // move off zero if the user wants to go BELOW that, not to reach it.
        var keepRatio = bg != null && bg.Pairs != null && bg.Pairs.Count > 0 ? bg.Pairs[0].SimplifyRatio : 0f;
        if (bg == null)
        {
            bg = ScriptableObject.CreateInstance<SC2TerrainData>();
            bg.name = "BackgroundTerrainData (generated)";
            bg.hideFlags = HideFlags.DontSave;
        }

        bg.GridWidth = gw;
        bg.GridHeight = gh;
        bg.CellSize = bgCell;
        bg.IsInitialized = true;
        bg.Heights = new float[(gw + 1) * (gh + 1)];

        ComputeCoreGridRange(coreData, minX, minZ, bgCell, out var holeGxMin, out var holeGzMin, out var holeGxMax, out var holeGzMax);
        // The background's OWN kept mesh is carved one coarse cell LARGER
        // than the core's true footprint on every side — that extra ring is
        // deliberately empty of background geometry, filled instead by
        // BuildSeamSkirt's fine-to-coarse fan (see that method's own doc
        // comment for why: a straight coarse edge sitting exactly on the
        // core's true, finely-detailed boundary is the actual crack, caused
        // by the differing mesh density between the two. This carved boundary becomes the real (interior, all-
        // background) edge the skirt's OUTER points attach to — no core
        // height-copy belongs here anymore, it's genuine background
        // territory with the background's own natural falloff height.
        ExpandRingByOneCoarseCell(gw, gh, holeGxMin, holeGzMin, holeGxMax, holeGzMax,
            out var carveGxMin, out var carveGzMin, out var carveGxMax, out var carveGzMax);

        // Height: a cheap, deterministic falloff away from the hole (so
        // repeated regeneration — e.g. every live-preview tick — is
        // visually stable, not re-randomized), based purely on grid-index
        // distance outside the core rectangle. Not physically simulated —
        // this is decorative, background-only geometry that only needs to
        // look plausible from normal RTS camera distance.
        var edgeHeight = AverageCoreEdgeHeight(coreData);
        var vw = gw + 1;
        var falloffSpan = Mathf.Max(1f, Mathf.Min(gw, gh) * 0.5f);
        for (var gz = 0; gz <= gh; gz++)
        for (var gx = 0; gx <= gw; gx++)
        {
            var wx = minX + gx * bgCell;
            var wz = minZ + gz * bgCell;
            var dOut = DistanceOutsideRect(gx, gz, holeGxMin, holeGzMin, holeGxMax, holeGzMax);
            var t = Mathf.Clamp01(dOut / falloffSpan);
            var noise = (Mathf.PerlinNoise(wx * 0.008f + 1000f, wz * 0.008f + 1000f) - 0.5f) * 60f;
            bg.Heights[gz * vw + gx] = edgeHeight + noise * t;
        }

        EnsureBackgroundPair(bg, keepRatio);
        BuildPairIndexAndCarveHole(bg, carveGxMin, carveGzMin, carveGxMax, carveGzMax);
        bg.InvalidateGeometryCache();

        coreData.BackgroundTerrainData = bg;
        return bg;
    }

    /// <summary>Grows a hole-boundary rectangle (in background vertex-index
    /// space) by exactly one coarse cell on every side, clamped to the
    /// background's own valid grid range — the boundary BuildSeamSkirt's
    /// fan attaches to on its outer (coarse) end.</summary>
    private static void ExpandRingByOneCoarseCell(int gw, int gh, int gxMin, int gzMin, int gxMax, int gzMax,
        out int outGxMin, out int outGzMin, out int outGxMax, out int outGzMax)
    {
        outGxMin = Mathf.Max(0, gxMin - 1);
        outGzMin = Mathf.Max(0, gzMin - 1);
        outGxMax = Mathf.Min(gw, gxMax + 1);
        outGzMax = Mathf.Min(gh, gzMax + 1);
    }

    /// <summary>Generates/refreshes the background, then runs it through
    /// the SAME simplify pipeline the core uses, with its own boundary
    /// ring locked so arbitrary SimplifyRatio choices can never open a
    /// gap at the seam. worldOffset is the translate every returned
    /// vertex still needs (the background's own local grid-index (0,0) is
    /// NOT world (0,0) — see this class' own header comment).</summary>
    public static SC2TerrainBuildResult BuildForExportOrPreview(SC2TerrainData coreData, out Vector3 worldOffset)
    {
        worldOffset = Vector3.zero;
        var bg = EnsureGenerated(coreData);
        if (bg == null) return new SC2TerrainBuildResult();

        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out _, out _, out var bgCell);
        worldOffset = new Vector3(minX, 0f, minZ);

        var gw = bg.GridWidth;
        var gh = bg.GridHeight;
        ComputeCoreGridRange(coreData, minX, minZ, bgCell, out var holeGxMin, out var holeGzMin, out var holeGxMax, out var holeGzMax);
        ExpandRingByOneCoarseCell(gw, gh, holeGxMin, holeGzMin, holeGxMax, holeGzMax,
            out var carveGxMin, out var carveGzMin, out var carveGxMax, out var carveGzMax);
        var ring = ComputeRing(bg, carveGxMin, carveGzMin, carveGxMax, carveGzMax);

        return SC2TerrainLayerBuilder.Build(bg, sortByTriangleCount: true, null, ring);
    }

    /// <summary>The seam is only crack-free "for free" when the background
    /// and core share the exact same vertex density along the boundary
    /// (k==1). At k&gt;1, the background's kept mesh is carved one WHOLE
    /// coarse cell larger than the core's true footprint (see Regenerate's
    /// ExpandRingByOneCoarseCell) specifically to leave room for THIS
    /// method to fill that band with a real, non-degenerate quad strip:
    /// INNER edge = the core's true, fine-resolution boundary (many
    /// points, real Heights[] data — exactly what the core's own mesh
    /// renders there); OUTER edge = the background's own remaining first
    /// ring, one coarse cell further out (its own two real endpoints, A
    /// and B — everything in between is a straight-line proxy along A-B,
    /// which is fine since that's already how the background's own coarse
    /// mesh renders that same edge). Each of the k sub-steps forms a
    /// proper 2-triangle quad between one inner point and its matching
    /// interpolated outer proxy — a standard ruled-surface strip, not a
    /// fan, so no two triangles ever pinch through a shared single vertex.
    ///
    /// (Two real bugs in an earlier version of this method — confirmed by
    /// a screenshot showing a jagged "sawtooth" seam — are what this
    /// design specifically avoids: (1) fanning every fine sub-point to
    /// whichever single real A/B endpoint was nearer, which self-
    /// overlaps/pinches badly whenever the core's edge height varies
    /// across the segment; (2) reusing A/B themselves as the fine
    /// polyline's own first/last points instead of real core positions,
    /// collapsing the strip's two ends onto the wrong edge entirely.)
    ///
    /// World-space, safe to append directly into either the export model
    /// or the live preview mesh with no further transform. Returns empty
    /// arrays when k==1 (background already at core resolution — no gap to
    /// stitch) or background is off/uninitialized.</summary>
    public static void BuildSeamSkirt(SC2TerrainData coreData, out Vector3[] verts, out Vector3[] normals, out int[] tris)
    {
        verts = System.Array.Empty<Vector3>();
        normals = System.Array.Empty<Vector3>();
        tris = System.Array.Empty<int>();
        if (coreData == null || !coreData.IsInitialized) return;

        var bg = EnsureGenerated(coreData);
        if (bg == null) return;

        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out _, out _, out var bgCell);
        var k = Mathf.Max(1, Mathf.RoundToInt(bgCell / Mathf.Max(0.0001f, coreData.CellSize)));
        if (k <= 1) return; // background already matches core resolution — no gap possible

        ComputeCoreGridRange(coreData, minX, minZ, bgCell, out var holeGxMin, out var holeGzMin, out var holeGxMax, out var holeGzMax);
        ExpandRingByOneCoarseCell(bg.GridWidth, bg.GridHeight, holeGxMin, holeGzMin, holeGxMax, holeGzMax,
            out var carveGxMin, out var carveGzMin, out var carveGxMax, out var carveGzMax);

        var coreCell = coreData.CellSize;
        var coreVw = coreData.GridWidth + 1;
        var coreHeights = coreData.Heights;
        Vector3 CoreVertex(int cgx, int cgz)
        {
            cgx = Mathf.Clamp(cgx, 0, coreData.GridWidth);
            cgz = Mathf.Clamp(cgz, 0, coreData.GridHeight);
            var idx = cgz * coreVw + cgx;
            var h = idx >= 0 && idx < coreHeights.Length ? coreHeights[idx] : SC2TerrainData.DefaultFlatHeight;
            return new Vector3(cgx * coreCell, h, cgz * coreCell);
        }

        var bgVw = bg.GridWidth + 1;
        var bgHeights = bg.Heights;
        Vector3 BgVertex(int bgGx, int bgGz)
        {
            bgGx = Mathf.Clamp(bgGx, 0, bg.GridWidth);
            bgGz = Mathf.Clamp(bgGz, 0, bg.GridHeight);
            var idx = bgGz * bgVw + bgGx;
            var h = idx >= 0 && idx < bgHeights.Length ? bgHeights[idx] : SC2TerrainData.DefaultFlatHeight;
            return new Vector3(minX + bgGx * bgCell, h, minZ + bgGz * bgCell);
        }

        // bg index -> core index along one axis. Indices outside
        // [holeMin,holeMax] (i.e. the one-cell margin ExpandRingByOneCoarseCell
        // added) clamp to the core's own edge (0 or GridWidth/GridHeight) —
        // that segment's fine points all collapse toward the real corner.
        // The resulting zero-area triangles are filtered out entirely by
        // AddTriIfValid below, not just left in as "harmless" — see its
        // own comment for why a raw zero-area triangle with 3 distinct
        // index slots isn't actually harmless for every downstream reader.
        int ToCoreX(int bgGx) => Mathf.Clamp((bgGx - holeGxMin) * k, 0, coreData.GridWidth);
        int ToCoreZ(int bgGz) => Mathf.Clamp((bgGz - holeGzMin) * k, 0, coreData.GridHeight);

        var vertList = new List<Vector3>();
        var triList = new List<int>();

        // One coarse outer segment (A..B, real background vertices, fixed)
        // stitched to the matching k-cell span of the core's true fine
        // edge via a proper QUAD strip: for each of the k sub-steps, an
        // inner (core) point and an OUTER PROXY point — A/B themselves
        // linearly interpolated along the segment, not snapped to a
        // single shared anchor — form one quad (2 triangles). This is a
        // standard ruled-surface strip, not a fan: no two triangles ever
        // share a single pinch-point vertex, which is what produced a
        // jagged/self-overlapping "sawtooth" seam in an earlier version
        // (confirmed by screenshot) that fanned every sub-step to whichever
        // single real A/B endpoint was nearer, AND — a separate bug in
        // that same earlier version — wrongly reused A/B themselves as the
        // fine polyline's own first/last points instead of real core
        // positions, collapsing the strip's two ends onto each other.
        // Confirmed via Blender verification
        // (Face Orientation overlay showed the top/right bands reversed):
        // this used to emit BOTH triangle windings per triangle as a
        // "safety margin" instead of deriving the correct one — wrongly
        // assumed harmless since Unity's own renderer draws double-sided
        // geometry fine either way. It was NOT harmless downstream: the
        // real exported file only ever keeps ONE winding per triangle
        // (through TerrainBdf.RebuildAllBsp's per-layer repack), and since
        // this method is called with the same relative index pattern for
        // all 4 differently-facing sides of the rectangle, exactly 2 of
        // the 4 (whichever pair happens to disagree with the other pair's
        // "outward" direction) ended up with their SURVIVING triangle
        // wound backwards — invisible/inside-out from the real game's
        // single-sided renderer even though Unity's own preview never
        // showed anything wrong. Fixed by deriving the correct winding
        // directly from real geometry (a terrain skirt always faces
        // upward) instead of emitting redundant copies and hoping one
        // survives correctly.
        //
        // Also rejects a triangle whenever any two of its three corners
        // sit at the same POSITION (not just a different vertex-list
        // index): a real Blender import
        // script (bmesh.faces.new) throws "found the same (BMVert) used
        // multiple times" on these. The raw file itself tolerates a
        // zero-area triangle with 3 distinct index slots (harmless,
        // doesn't render) — but TerrainBdf.RebuildAllBsp's own per-layer
        // BSP repack deduplicates vertices BY POSITION, which turns
        // exactly this shape of triangle into one with a literally
        // repeated index — something the raw exported file HAS to
        // tolerate for any strict downstream reader (this Blender script,
        // but plausibly others too), not just Unity's own permissive
        // renderer. Corner segments (see ToCoreX/ToCoreZ's own
        // clamp-collapse comment) are exactly where this happens, since
        // every inner point along a corner segment collapses to the SAME
        // real core corner position.
        void AddTriIfValid(int a, int b, int c)
        {
            if (a == b || b == c || a == c) return;
            var pa = vertList[a]; var pb = vertList[b]; var pc = vertList[c];
            if ((pa - pb).sqrMagnitude < 1e-8f || (pb - pc).sqrMagnitude < 1e-8f || (pa - pc).sqrMagnitude < 1e-8f) return;
            // A skirt triangle always faces roughly upward (+Y) — flip to
            // whichever of the two windings actually produces an
            // upward-facing cross product, rather than assuming the
            // caller's own index order already got it right for this
            // particular side of the rectangle.
            var normal = Vector3.Cross(pb - pa, pc - pa);
            if (normal.y < 0f) { triList.Add(a); triList.Add(c); triList.Add(b); }
            else { triList.Add(a); triList.Add(b); triList.Add(c); }
        }

        void StitchSegment(int outerBgX0, int outerBgZ0, int outerBgX1, int outerBgZ1,
                            int coreX0, int coreZ0, int coreX1, int coreZ1)
        {
            var a = BgVertex(outerBgX0, outerBgZ0);
            var b = BgVertex(outerBgX1, outerBgZ1);
            var innerIdx = new int[k + 1];
            var outerIdx = new int[k + 1];
            for (var j = 0; j <= k; j++)
            {
                var t = j / (float)k;
                var cx = Mathf.RoundToInt(Mathf.Lerp(coreX0, coreX1, t));
                var cz = Mathf.RoundToInt(Mathf.Lerp(coreZ0, coreZ1, t));
                innerIdx[j] = vertList.Count; vertList.Add(CoreVertex(cx, cz));
                outerIdx[j] = vertList.Count; vertList.Add(Vector3.Lerp(a, b, t));
            }
            for (var j = 0; j < k; j++)
            {
                var i0 = innerIdx[j]; var i1 = innerIdx[j + 1];
                var o0 = outerIdx[j]; var o1 = outerIdx[j + 1];
                // One correctly-oriented triangle per half of the quad —
                // AddTriIfValid derives the right winding itself now, so
                // the earlier duplicate opposite-winding calls are gone
                // (also halves this skirt's own triangle count).
                AddTriIfValid(i0, o0, i1);
                AddTriIfValid(i1, o0, o1);
            }
        }

        // Top (core Z=0) / bottom (core Z=GridHeight) bands — full
        // expanded width, including the two corner segments on each end.
        for (var bx = carveGxMin; bx < carveGxMax; bx++)
            StitchSegment(bx, carveGzMin, bx + 1, carveGzMin, ToCoreX(bx), 0, ToCoreX(bx + 1), 0);
        for (var bx = carveGxMin; bx < carveGxMax; bx++)
            StitchSegment(bx, carveGzMax, bx + 1, carveGzMax, ToCoreX(bx), coreData.GridHeight, ToCoreX(bx + 1), coreData.GridHeight);

        // Left (core X=0) / right (core X=GridWidth) bands — ALSO full
        // expanded height (carveGzMin..carveGzMax), matching the top/bottom
        // bands' own full width. This is NOT double coverage at the 4
        // corner cells: each corner cell's own core-side point collapses
        // to the SAME single core corner (ToCoreX/ToCoreZ both clamp to
        // that corner from either direction), so the top/bottom band's
        // corner wedge and the left/right band's corner wedge are the two
        // COMPLEMENTARY triangular halves of that one cell (split along
        // its diagonal through the core corner point), not an overlap.
        // (An earlier version stopped these bands at holeGzMin/holeGzMax
        // to "avoid double coverage" — that assumption was wrong: the
        // top/bottom band's corner segment only ever covers HALF of the
        // corner cell, so stopping the left/right bands short of it left
        // the other half — the actual missing polygon at all 4 corners,
        // confirmed absent from both the live
        // preview and the export.)
        for (var bz = carveGzMin; bz < carveGzMax; bz++)
            StitchSegment(carveGxMin, bz, carveGxMin, bz + 1, 0, ToCoreZ(bz), 0, ToCoreZ(bz + 1));
        for (var bz = carveGzMin; bz < carveGzMax; bz++)
            StitchSegment(carveGxMax, bz, carveGxMax, bz + 1, coreData.GridWidth, ToCoreZ(bz), coreData.GridWidth, ToCoreZ(bz + 1));

        verts = vertList.ToArray();
        tris = triList.ToArray();
        normals = ComputeFlatVertexNormals(verts, tris);
    }

    private static Vector3[] ComputeFlatVertexNormals(Vector3[] verts, int[] tris)
    {
        var normals = new Vector3[verts.Length];
        for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.zero;
        for (var t = 0; t < tris.Length; t += 3)
        {
            var a = tris[t]; var b = tris[t + 1]; var c = tris[t + 2];
            var n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i].sqrMagnitude > 1e-8f ? normals[i].normalized : Vector3.up;
        return normals;
    }

    /// <summary>Real SC2/Terrain-shader material built DIRECTLY from a live
    /// background pair's own Texture2D references — no export-time path-
    /// string resolution needed (that's SC2TerrainMaterialFactory.Create's
    /// job, for a parsed TerrainMaterial coming from a file). The
    /// background's live preview previously always used a flat per-pair
    /// color (SC2PreviewMaterialFactory), unlike the core's own runtime
    /// chunks.
    ///
    /// _RNMMask1 is bound to the pair's own live blend-mask texture
    /// (SC2MaterialPair.RnmMaskTexture — the SAME field/texture the core's
    /// own Paint Blend brush already paints into) so SC2Terrain.shader's
    /// blend factor (tex2D(_RNMMask1, uv2).a) reflects real painted data —
    /// this works WITHOUT setting _HasRNM (left at its shader default, 0):
    /// the Diffuse1/Diffuse2 blend runs unconditionally, before the
    /// _HasRNM-gated 3-basis RNM lighting branch even starts, so ordinary
    /// Sun-lit shading already blends correctly through the shader's
    /// non-RNM branch even with no bake ever run.
    ///
    /// (Real directional RNM lighting CAN now also be baked for the
    /// background — SC2RnmBakeService.BakeBackground, triggered by the
    /// Background tab's own "Bake Background Lighting" button — this
    /// live preview material just never shows that specific 3-basis
    /// result itself (doesn't set _HasRNM); baked data only actually
    /// renders once exported into the real game. <paramref
    /// name="bgPreviewMode"/> is a separate, purely cosmetic stand-in: it
    /// sets SC2Terrain.shader's own _BgPreviewMode override (0=off,
    /// 1=RNM approximation, 2=Mats, 3=Pairs, 4=Slope), which mirrors the
    /// live procedural terrain's own Textured/RNM/Mats/Pairs/Slope
    /// viewport toggle using data already on this material (worldNormal,
    /// the pair's own blend mask, and its Material1/2/Pair colors), so
    /// background matches core's look under the same toggle instead of
    /// always rendering flat.)</summary>
    public static Material CreateLivePairMaterial(SC2MaterialPair pair, SC2Material m1, SC2Material m2, int bgPreviewMode = 0)
    {
        var shader = SC2TerrainMaterialFactory.ResolveShader("SC2/Terrain", "SC2Terrain");
        var material = new Material(shader) { name = $"mat_{pair?.Name ?? "Background"}" };
        material.SetInt("_BgPreviewMode", bgPreviewMode);
        material.SetColor("_BgPreviewMatColor1", m1?.Color ?? Color.grey);
        material.SetColor("_BgPreviewMatColor2", m2?.Color ?? Color.grey);
        material.SetColor("_BgPreviewPairColor", pair?.PairColor ?? Color.grey);
        if (m1?.Diffuse != null) material.SetTexture("_Diffuse1", m1.Diffuse);
        if (m2?.Diffuse != null) material.SetTexture("_Diffuse2", m2.Diffuse);
        if (m1?.Normal != null) material.SetTexture("_Normal1", m1.Normal);
        if (m2?.Normal != null) material.SetTexture("_Normal2", m2.Normal);
        if (m1?.Mask != null) material.SetTexture("_Mask1", m1.Mask);
        if (m2?.Mask != null) material.SetTexture("_Mask2", m2.Mask);
        // Real per-texel baked lighting (SC2RnmBakeService.BakeBackground)
        // previously had zero effect on this live preview — CreateLivePairMaterial
        // never set _HasRNM or bound _RNMMask0/2 at all, so the real RNM
        // branch in SC2Terrain.shader's frag() was always skipped regardless
        // of whether a bake had run, falling back to flat Sun-lit shading
        // next to the core (which DOES show its own real baked lighting).
        // RefreshRnmPreviewTextures combines the baked RGB with each mask's
        // own alpha source (shadow/blend/opaque) to match exactly what
        // SC2TerrainExporter.ExportRnmMaskByName writes to disk.
        if (pair != null && pair.HasBakedRnmLighting)
        {
            pair.RefreshRnmPreviewTextures();
            material.SetTexture("_RNMMask0", pair.RnmPreviewMask0);
            material.SetTexture("_RNMMask1", pair.RnmPreviewMask1);
            material.SetTexture("_RNMMask2", pair.RnmPreviewMask2);
            material.SetFloat("_HasRNM", 1f);
        }
        else if (pair?.RnmMaskTexture != null)
        {
            material.SetTexture("_RNMMask1", pair.RnmMaskTexture);
        }

        // Real Shader Setup flags/State/TextureScroll — same fields the
        // "Pairs" UI already lets a user edit for background pairs
        // (SC2PairsMode is reused as-is), but previously never reached this
        // live preview material at all (only real import-time material
        // creation, SC2TerrainMaterialFactory.Create, applied them) — the
        // preview always showed the shader's raw Properties-block defaults
        // regardless of what a pair's own flags said. ApplyShaderSetup
        // resets every _EnableXxx to 0 first, so the flags list must be
        // complete (see EnsureBackgroundPair's own updated default set).
        var setup = new List<ShaderSetupEntry>();
        if (pair?.ShaderFlags != null)
            foreach (var flag in pair.ShaderFlags)
            {
                if (!flag.Value) continue;
                var value = flag.Key == "ALPHA_TEST"
                    ? Mathf.RoundToInt(pair.AlphaTestThreshold).ToString()
                    : "1";
                setup.Add(new ShaderSetupEntry(flag.Key, value));
            }
        SC2TerrainMaterialFactory.ApplyShaderSetup(material, setup);
        SC2TerrainMaterialFactory.ApplyBlendState(material, pair != null && !string.IsNullOrEmpty(pair.State) ? pair.State : null);
        material.SetVector("_TextureScroll", new Vector4(pair?.TextureScroll.x ?? 0f, pair?.TextureScroll.y ?? 0f, 0, 0));

        // The preview mesh doesn't cast/receive Unity shadows (see its own
        // MeshRenderer setup) — force this off regardless of the pair's
        // own ShaderFlags/ApplyShaderSetup result above, so the shader
        // never samples an absent shadow map.
        material.SetFloat("_EnableDynamicShadows", 0f);
        return material;
    }

    private static void EnsureBackgroundPair(SC2TerrainData bg, float simplifyRatio)
    {
        if (bg.Materials == null || bg.Materials.Count < 2)
        {
            bg.Materials = new List<SC2Material>
            {
                new SC2Material { Name = "Background Ground", Color = new Color(0.32f, 0.40f, 0.24f) },
                new SC2Material { Name = "Background Rock", Color = new Color(0.40f, 0.38f, 0.34f) },
            };
        }

        if (bg.Pairs == null) bg.Pairs = new List<SC2MaterialPair>();
        if (bg.Pairs.Count == 0)
        {
            bg.Pairs.Add(new SC2MaterialPair
            {
                Name = "Background",
                Mat1Index = 0,
                Mat2Index = 1,
                PairColor = new Color(0.32f, 0.40f, 0.24f),
                SimplifyRatio = simplifyRatio,
                // Distinct from SC2MaterialPair's own class-level default
                // ("mask01") — a from-scratch core pair uses that SAME
                // default, and until the export's own "bg_" prefix
                // (SC2TerrainExporter.cs) was added, both instances'
                // RnmMaskBytes/baked lighting silently collided into ONE
                // shared exported mask (whichever was written first "won").
                // Named explicitly here too so it's self-evidently distinct
                // on the Pairs tab, not just internally disambiguated at
                // export time.
                RnmMaskName = "background",
                // Same default set core pairs get (SC2MaterialsPairsMode.
                // kShaderFlags/DefaultFlags) — previously a hand-picked
                // subset missing BLENDING/RNM_LIGHTING/SPECULAR_LIGHTING/
                // REFLECT/CHROMATIC_DISPERSION. That was harmless only
                // because CreateLivePairMaterial never applied ShaderFlags
                // to the material at all (the shader's own raw Properties
                // defaults, almost all =1, did the actual work) — now that
                // it does, a pair's flag LIST is authoritative, so it needs
                // to start complete or those effects (blend painting
                // included) would silently go dark on a fresh background.
                ShaderFlags = new List<SC2ShaderFlag>
                {
                    new SC2ShaderFlag { Key = "AMBIENT_LIGHTING", Value = true },
                    new SC2ShaderFlag { Key = "DIFFUSE_LIGHTING", Value = true },
                    new SC2ShaderFlag { Key = "SPECULAR_LIGHTING", Value = true },
                    new SC2ShaderFlag { Key = "RNM_LIGHTING", Value = true },
                    new SC2ShaderFlag { Key = "BLENDING", Value = true },
                    new SC2ShaderFlag { Key = "FOG", Value = true },
                    new SC2ShaderFlag { Key = "DYNAMIC_SHADOWS", Value = true },
                    new SC2ShaderFlag { Key = "REFLECT", Value = true },
                    new SC2ShaderFlag { Key = "CHROMATIC_DISPERSION", Value = true },
                    new SC2ShaderFlag { Key = "GLOW", Value = false },
                    new SC2ShaderFlag { Key = "ENVIRONMENT_MAPPING", Value = false },
                    new SC2ShaderFlag { Key = "INFINITE_DISTANCE", Value = false },
                    new SC2ShaderFlag { Key = "ALPHA_TEST", Value = false },
                }
            });
        }
        else
        {
            bg.Pairs[0].SimplifyRatio = simplifyRatio;
        }
    }

    /// <summary>Writes bg.TrianglePairIndex directly (matching
    /// SC2TerrainData.BuildTriangles()' own row-major, 2-triangles-per-cell
    /// ordering exactly), assigning -1 (unassigned — invisible to
    /// SC2TerrainLayerBuilder.Build's pair bucketing) to every cell whose
    /// index range falls entirely within the core's own grid range, and
    /// pair 0 to everything else. Because both grids share CellSize and
    /// ComputeBounds always rounds to whole cells, a cell is NEVER
    /// partially inside — this is exact integer containment, no clipping
    /// anywhere.</summary>
    private static void BuildPairIndexAndCarveHole(SC2TerrainData bg, int coreGxMin, int coreGzMin, int coreGxMax, int coreGzMax)
    {
        var gw = bg.GridWidth;
        var gh = bg.GridHeight;
        var n = gw * gh * 2;
        var idx = new short[n];
        var t = 0;
        for (var gz = 0; gz < gh; gz++)
        for (var gx = 0; gx < gw; gx++)
        {
            var inHole = gx >= coreGxMin && gx + 1 <= coreGxMax && gz >= coreGzMin && gz + 1 <= coreGzMax;
            var pair = inHole ? (short)-1 : (short)0;
            idx[t++] = pair;
            idx[t++] = pair;
        }
        bg.TrianglePairIndex = idx;
        if (bg.Pairs != null)
            foreach (var p in bg.Pairs)
                p?.InvalidateTriCount();
    }

    /// <summary>The background-local flat vertex indices forming the ring
    /// immediately bordering the carved hole — exactly the core rectangle's
    /// own perimeter, expressed in background grid-index space.</summary>
    private static HashSet<int> ComputeRing(SC2TerrainData bg, int coreGxMin, int coreGzMin, int coreGxMax, int coreGzMax)
    {
        var ring = new HashSet<int>();
        var vw = bg.GridWidth + 1;
        var vh = bg.GridHeight + 1;
        var gxLo = Mathf.Clamp(coreGxMin, 0, vw - 1);
        var gxHi = Mathf.Clamp(coreGxMax, 0, vw - 1);
        var gzLo = Mathf.Clamp(coreGzMin, 0, vh - 1);
        var gzHi = Mathf.Clamp(coreGzMax, 0, vh - 1);
        for (var gz = gzLo; gz <= gzHi; gz++)
        for (var gx = gxLo; gx <= gxHi; gx++)
        {
            if (gx != coreGxMin && gx != coreGxMax && gz != coreGzMin && gz != coreGzMax) continue;
            ring.Add(gz * vw + gx);
        }
        return ring;
    }

    private static float AverageCoreEdgeHeight(SC2TerrainData coreData)
    {
        var perimeter = SC2TerrainSimplifier.FindOuterPerimeterVertices(coreData);
        if (perimeter.Count == 0) return SC2TerrainData.DefaultFlatHeight;
        var sum = 0f;
        var count = 0;
        foreach (var i in perimeter)
        {
            if (i < 0 || i >= coreData.Heights.Length) continue;
            sum += coreData.Heights[i];
            count++;
        }
        return count > 0 ? sum / count : SC2TerrainData.DefaultFlatHeight;
    }

    private static float DistanceOutsideRect(int gx, int gz, int minGx, int minGz, int maxGx, int maxGz)
    {
        var dx = gx < minGx ? minGx - gx : gx > maxGx ? gx - maxGx : 0;
        var dz = gz < minGz ? minGz - gz : gz > maxGz ? gz - maxGz : 0;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// Live in-editor/in-build preview of the decorative background terrain —
/// structural template is SC2WaterPreview (lazy GameObject/Mesh, "skip the
/// synthetic preview if an imported object already covers this role"
/// branch), but simpler: flat per-pair colored materials
/// (SC2PreviewMaterialFactory, the same ones SC2SimplifiedPreviewMode
/// already uses) instead of real shader/texture wiring, since the
/// background pair typically has no real textures assigned.
///
/// Regeneration is time-debounced, not per-tick: even with
/// SC2BackgroundTerrain's own MaxGridDimension safety cap, re-running
/// Perlin-noise height generation + vertex-clustering simplification on a
/// grid that can still be over a million source triangles every single
/// OnGUI/Update call would be a real, avoidable frame-time cost. A ~1s
/// cadence keeps the preview visually live (height edits near the seam,
/// SimplifyRatio slider drags) without paying that cost every frame.
/// </summary>
public static class SC2BackgroundTerrainPreview
{
    private const double RegenerateIntervalSeconds = 1.0;

    private static GameObject _go;
    private static Mesh _mesh;
    private static MeshCollider _collider;
    private static Material[] _materials;
    private static double _nextRegenerateAt;
    private static bool _forceRegenerate = true;

    /// <summary>Call whenever a setting this preview depends on changes
    /// (enable toggle, size, UV scale, SimplifyRatio slider) so the next
    /// Ensure() call regenerates immediately instead of waiting out the
    /// debounce interval.</summary>
    public static void MarkDirty() => _forceRegenerate = true;

    /// <summary>Sets _BgPreviewMode/height-gradient state on every current
    /// material without touching mesh/material identity — cheap enough to
    /// call every tick, same reasoning as the core terrain's own per-frame
    /// SC2TerrainRuntime.SetAllPairMaterials/SetHeightGradientOverlay
    /// preview-mode refresh. Material1/2/Pair colors are NOT refreshed
    /// here (they don't change without a full pair rebuild, which already
    /// goes through CreateLivePairMaterial). A no-op on the
    /// SC2PreviewMaterialFactory.MakeGrey fallback (pair == null), since
    /// that material uses a different shader with no such properties.</summary>
    private static void ApplyPreviewOverrides(int bgPreviewMode, bool heightGradientEnabled, float heightGradientMin, float heightGradientMax)
    {
        if (_materials == null) return;
        if (heightGradientMax <= heightGradientMin) heightGradientMax = heightGradientMin + 1f;
        var heightMinMax = new Vector4(heightGradientMin, heightGradientMax, 0f, 0f);
        foreach (var mat in _materials)
        {
            if (mat == null) continue;
            if (mat.HasProperty("_BgPreviewMode")) mat.SetInt("_BgPreviewMode", bgPreviewMode);
            if (mat.HasProperty("_BgPreviewHeightGradientEnabled")) mat.SetFloat("_BgPreviewHeightGradientEnabled", heightGradientEnabled ? 1f : 0f);
            if (mat.HasProperty("_BgPreviewHeightGradientMinMax")) mat.SetVector("_BgPreviewHeightGradientMinMax", heightMinMax);
        }
    }

    public static void Ensure(SC2TerrainData coreData, bool overlayVisible, System.Collections.Generic.List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session = null,
        int bgPreviewMode = 0, bool heightGradientEnabled = false, float heightGradientMin = 0f, float heightGradientMax = 200f)
    {
        var hasCustomBackground = importedTerrains != null && importedTerrains.Exists(t => t.IsBackgroundTerrain);
        if (coreData == null || !coreData.IsInitialized || !coreData.BackgroundTerrainEnabled || !overlayVisible || hasCustomBackground)
        {
            Destroy();
            return;
        }

        var now = Time.realtimeSinceStartupAsDouble;
        if (_go != null && !_forceRegenerate && now < _nextRegenerateAt)
        {
            _go.SetActive(true);
            // Cheap per-tick refresh, independent of the mesh/material
            // regenerate debounce below — toggling the Terrain tab's own
            // Textured/RNM/Mats/Pairs/Slope/Height selector must feel as
            // immediate here as it already does for the live procedural
            // terrain's own chunks.
            ApplyPreviewOverrides(bgPreviewMode, heightGradientEnabled, heightGradientMin, heightGradientMax);
            return;
        }
        _forceRegenerate = false;
        _nextRegenerateAt = now + RegenerateIntervalSeconds;

        var build = SC2BackgroundTerrain.BuildForExportOrPreview(coreData, out var offset);
        var bg = coreData.BackgroundTerrainData;
        if (bg == null || build.Layers.Count == 0)
        {
            Destroy();
            return;
        }

        // Background-local normalized [0,1] bounds — used for BOTH the
        // preview's UV2 (RNM/blend-mask lookup, below) and
        // SC2TerrainExporter's own background export loop, so a painted
        // blend mask lands at the exact same spot in-editor and in-game
        // regardless of BackgroundTerrainUvScale (a UV1/tiling-only
        // concern, unrelated to where the mask itself samples).
        SC2BackgroundTerrain.ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out _);
        var spanX = Mathf.Max(1f, maxX - minX);
        var spanZ = Mathf.Max(1f, maxZ - minZ);

        if (_go == null)
        {
            _go = new GameObject("SC2 Background Terrain Preview") { hideFlags = HideFlags.DontSave };
            var mf = _go.AddComponent<MeshFilter>();
            var mr = _go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // MeshCollider — the only way SC2BackgroundEditMode's CPU-only
            // brush finds where the cursor is on the (possibly hand-
            // sculpted) surface, since there's no live SC2TerrainRuntime for
            // the background to raycast against the way the core's own
            // tools do. Rebuilt in lockstep with _mesh below, so it's never
            // more than one debounce tick stale.
            _collider = _go.AddComponent<MeshCollider>();
            _mesh = new Mesh { name = "SC2BackgroundTerrainPreviewMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mf.sharedMesh = _mesh;
        }
        _go.SetActive(true);
        _go.transform.position = offset;

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uv1 = new List<Vector2>();
        var uv2 = new List<Vector2>();
        var subTris = new List<List<int>>();
        var mats = new List<Material>();
        var pair0SubmeshIndex = -1;
        InfoModel infoForEnv = null;
        if (session != null) infoForEnv = SC2TerrainExporter.BuildInfoModel(coreData, session, quiet: true);
        foreach (var layer in build.Layers)
        {
            var s = layer.Mesh;
            if (s.Triangles == null || s.Triangles.Length == 0) continue;
            var vOffset = verts.Count;
            verts.AddRange(s.Vertices);
            normals.AddRange(s.Normals);
            var pair = layer.PairIndex >= 0 && layer.PairIndex < bg.Pairs.Count ? bg.Pairs[layer.PairIndex] : null;
            var tilingScale = coreData.EffectiveUvTilingScale(pair);
            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var worldPos = s.Vertices[vi] + offset;
                uv1.Add(new Vector2(worldPos.x / tilingScale, worldPos.z / tilingScale));
                uv2.Add(new Vector2((worldPos.x - minX) / spanX, (worldPos.z - minZ) / spanZ));
            }
            var tri = new List<int>(s.Triangles.Length);
            for (var i = 0; i < s.Triangles.Length; i++) tri.Add(s.Triangles[i] + vOffset);
            subTris.Add(tri);
            if (layer.PairIndex == 0) pair0SubmeshIndex = subTris.Count - 1;
            var m1 = pair != null && pair.Mat1Index >= 0 && pair.Mat1Index < bg.Materials.Count ? bg.Materials[pair.Mat1Index] : null;
            var m2 = pair != null && pair.Mat2Index >= 0 && pair.Mat2Index < bg.Materials.Count ? bg.Materials[pair.Mat2Index] : null;
            var liveMat = pair != null ? SC2BackgroundTerrain.CreateLivePairMaterial(pair, m1, m2, bgPreviewMode) : SC2PreviewMaterialFactory.MakeGrey("Background");
            if (infoForEnv != null) SC2TerrainMaterialFactory.ApplyEnvironment(liveMat, infoForEnv);
            mats.Add(liveMat);
        }

        // Seam-stitching skirt — world-space verts (SC2BackgroundTerrain.
        // BuildSeamSkirt sources everything from coreData's own arrays
        // directly), so they need the SAME 'offset' subtracted that _go's
        // own transform.position already applies to the rest of this mesh
        // via the world->local conversion every other GameObject gets for
        // free from its Transform. Merged into pair 0's OWN submesh
        // (matching the export's own material merge)
        // rather than a separate submesh/material, when that submesh
        // exists; falls back to its own submesh only in the edge case
        // where pair 0 contributed no visible triangles this tick.
        SC2BackgroundTerrain.BuildSeamSkirt(coreData, out var skirtVerts, out var skirtNormals, out var skirtTris);
        if (skirtTris.Length > 0)
        {
            var pair0 = bg.Pairs.Count > 0 ? bg.Pairs[0] : null;
            var skirtTilingScale = coreData.EffectiveUvTilingScale(pair0);
            var localSkirtVerts = new Vector3[skirtVerts.Length];
            for (var i = 0; i < skirtVerts.Length; i++)
            {
                localSkirtVerts[i] = skirtVerts[i] - offset;
                var worldPos = skirtVerts[i];
                uv1.Add(new Vector2(worldPos.x / skirtTilingScale, worldPos.z / skirtTilingScale));
                uv2.Add(new Vector2((worldPos.x - minX) / spanX, (worldPos.z - minZ) / spanZ));
            }
            var vOffset = verts.Count;
            verts.AddRange(localSkirtVerts);
            normals.AddRange(skirtNormals);
            if (pair0SubmeshIndex < 0)
            {
                var tri = new List<int>(skirtTris.Length);
                for (var i = 0; i < skirtTris.Length; i++) tri.Add(skirtTris[i] + vOffset);
                subTris.Add(tri);
                Material skirtMat;
                if (pair0 != null)
                {
                    var m1 = pair0.Mat1Index >= 0 && pair0.Mat1Index < bg.Materials.Count ? bg.Materials[pair0.Mat1Index] : null;
                    var m2 = pair0.Mat2Index >= 0 && pair0.Mat2Index < bg.Materials.Count ? bg.Materials[pair0.Mat2Index] : null;
                    skirtMat = SC2BackgroundTerrain.CreateLivePairMaterial(pair0, m1, m2, bgPreviewMode);
                    if (infoForEnv != null) SC2TerrainMaterialFactory.ApplyEnvironment(skirtMat, infoForEnv);
                }
                else skirtMat = SC2PreviewMaterialFactory.MakeGrey("Background");
                mats.Add(skirtMat);
            }
            else
            {
                var target = subTris[pair0SubmeshIndex];
                for (var i = 0; i < skirtTris.Length; i++) target.Add(skirtTris[i] + vOffset);
            }
        }

        SC2PreviewMaterialFactory.Destroy(_materials);
        _mesh.Clear();
        _mesh.SetVertices(verts);
        if (normals.Count == verts.Count) _mesh.SetNormals(normals);
        if (uv1.Count == verts.Count) _mesh.SetUVs(0, uv1);
        if (uv2.Count == verts.Count) _mesh.SetUVs(1, uv2);
        _mesh.subMeshCount = Mathf.Max(1, subTris.Count);
        if (subTris.Count == 0) _mesh.SetTriangles(System.Array.Empty<int>(), 0);
        else for (var i = 0; i < subTris.Count; i++) _mesh.SetTriangles(subTris[i], i);
        _mesh.RecalculateBounds();
        if (normals.Count != verts.Count) _mesh.RecalculateNormals();

        _materials = mats.Count > 0 ? mats.ToArray() : new[] { SC2PreviewMaterialFactory.MakeGrey("EmptyBackgroundPreview") };
        // CreateLivePairMaterial above already set _BgPreviewMode per
        // material; this also covers the height-gradient overlay state,
        // which isn't per-pair and so isn't set there.
        ApplyPreviewOverrides(bgPreviewMode, heightGradientEnabled, heightGradientMin, heightGradientMax);
        _go.GetComponent<MeshRenderer>().sharedMaterials = _materials;

        // MeshCollider requires an explicit re-set (not just relying on the
        // shared Mesh object mutating in place) to pick up a topology
        // change — a plain property re-set is cheap relative to the mesh
        // rebuild that already just happened.
        if (_collider != null) { _collider.sharedMesh = null; _collider.sharedMesh = _mesh; }
    }

    /// <summary>Raycasts the live background preview surface — the only
    /// way SC2BackgroundEditMode's CPU brush knows where the cursor is.
    /// Returns false (no throw) when there's currently no preview to hit,
    /// e.g. background disabled or a custom imported terrain stands in for
    /// it instead.</summary>
    public static bool TryRaycast(Ray ray, out RaycastHit hit, float maxDistance = 100000f)
    {
        hit = default;
        return _collider != null && _go != null && _go.activeInHierarchy && _collider.Raycast(ray, out hit, maxDistance);
    }

    /// <summary>True for the preview's own GameObject (or anything parented
    /// under it) — the MeshCollider TryRaycast needs for the CPU brush also
    /// makes this object a hit for SC2GameViewEditorUI's generic click-to-
    /// select, which then hijacks the orbit/pan pivot to this object's own
    /// transform.position — the exact same failure mode already fixed
    /// for imported terrain.bdf/decals.bdf roots. Callers should feed this
    /// into the same IsGenericSelectionExcluded exclusion list those use.</summary>
    public static bool IsPreviewObject(GameObject go) =>
        go != null && _go != null && (go == _go || go.transform.IsChildOf(_go.transform));

    public static void Destroy()
    {
        SC2PreviewMaterialFactory.Destroy(_materials);
        _materials = null;
        if (_go != null) Object.DestroyImmediate(_go);
        _go = null;
        _mesh = null;
        _collider = null;
        _forceRegenerate = true;
    }
}
