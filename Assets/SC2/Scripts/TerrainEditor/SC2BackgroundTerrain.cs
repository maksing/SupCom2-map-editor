using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Info;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

/// <summary>
/// Procedural generator for the optional decorative "background terrain" -
/// a second SC2TerrainData instance surrounding the playable core on all
/// sides. Deliberately reuses SC2TerrainData itself (not a bespoke class) so
/// the exact same SC2TerrainLayerBuilder/SC2TerrainSimplifier pipeline the
/// core uses also drives the background.
///
/// Design:
///  - The background grid has the SAME cell size as the core, and its grid
///    lines coincide with the core's. The core's rectangle is therefore a
///    plain integer rectangle of background cells: those cells are carved out
///    (BuildPairIndexAndCarveHole) and the vertices along the rectangle's rim
///    sit exactly where the core's perimeter vertices sit. No stitching
///    geometry is needed at the seam.
///  - X/Z continuity across the seam is automatic from the shared grid. Only
///    height (Y) needs an explicit step: the rim vertices take the core's
///    perimeter heights (SyncRingFromCore) and the background blends from
///    them into its own falloff over RingBlendCells. The rim is locked during
///    simplification (SC2TerrainLayerBuilder's additionalLockedVertices), and
///    so is the core's own outer perimeter, so the seam stays closed at any
///    simplification.
///  - The footprint is limited to MaxFootprintCoreMultiple times the core, so
///    the full-density source grid stays within a workable size.
///  - The exported/baked mesh is simplified to ExportTriangleBudget triangles
///    (ExportSimplifyRatios) whatever the footprint, while the editor keeps
///    the full-density grid for sculpting and painting.
///  - The background's own local grid-index (0,0) is NOT world (0,0): it is
///    ComputeBounds' minX/minZ (frequently negative). BuildForExportOrPreview
///    returns that offset directly; callers translate the built mesh by it.
///
/// The LIVE viewport preview (SC2TerrainShell.BackgroundRuntime, a second
/// SC2TerrainRuntime instance) renders the full, unsimplified grid with the
/// carved cells skipped at the mesh level (see SC2TerrainRuntime.Init's
/// holeTriPairIndex parameter).
/// </summary>
public static class SC2BackgroundTerrain
{
    /// <summary>How many triangles the exported (and baked) background is simplified down to.</summary>
    public const int ExportTriangleBudget = 100_000;

    /// <summary>The largest footprint, as a multiple of the core's larger side.</summary>
    public const int MaxFootprintCoreMultiple = 3;

    /// <summary>Minimum margin (in cells) kept on every side of the carved hole, so the background can never be smaller than the
    /// hole itself and carve away everything.</summary>
    private const int MinMarginCells = 8;

    /// <summary>Distance (in cells) over which the background blends from the core's rim heights into its own falloff.</summary>
    private const float RingBlendCells = 32f;

    private static string _pendingNotice;

    /// <summary>A message for the user that a background rebuild produced (the old background was discarded when its grid density
    /// changed), or null. Reading clears it.</summary>
    public static string ConsumeNotice()
    {
        var notice = _pendingNotice;
        _pendingNotice = null;
        return notice;
    }

    private static float CoreLargerSide(SC2TerrainData coreData)
    {
        var cell = Mathf.Max(0.0001f, coreData.CellSize);
        return Mathf.Max(coreData.GridWidth, coreData.GridHeight) * cell;
    }

    /// <summary>The smallest footprint that still leaves a margin of background around the core.</summary>
    public static int MinFootprint(SC2TerrainData coreData)
    {
        var cell = Mathf.Max(0.0001f, coreData.CellSize);
        return Mathf.CeilToInt(CoreLargerSide(coreData) + 2f * MinMarginCells * cell);
    }

    /// <summary>The largest footprint: <see cref="MaxFootprintCoreMultiple"/> times the core's larger side.</summary>
    public static int MaxFootprint(SC2TerrainData coreData) =>
        Mathf.Max(MinFootprint(coreData), Mathf.FloorToInt(CoreLargerSide(coreData) * MaxFootprintCoreMultiple));

    public static int ClampFootprint(SC2TerrainData coreData, int size) =>
        Mathf.Clamp(size, MinFootprint(coreData), MaxFootprint(coreData));

    /// <summary>The background's own world-space rectangle, rounded outward to whole cells - purely derived from coreData + size,
    /// never persisted, so it can never drift out of sync with either. Both dimensions come out as exact multiples of the cell by
    /// construction, which is what guarantees grid-line alignment with the core.</summary>
    public static void ComputeBounds(SC2TerrainData coreData, int size, out float minX, out float minZ, out float maxX, out float maxZ, out float bgCell)
    {
        bgCell = Mathf.Max(0.0001f, coreData.CellSize);
        var coreW = coreData.GridWidth * bgCell;
        var coreD = coreData.GridHeight * bgCell;
        var centerX = coreW * 0.5f;
        var centerZ = coreD * 0.5f;
        var half = ClampFootprint(coreData, size) * 0.5f;
        minX = Mathf.Floor((centerX - half) / bgCell) * bgCell;
        minZ = Mathf.Floor((centerZ - half) / bgCell) * bgCell;
        maxX = Mathf.Ceil((centerX + half) / bgCell) * bgCell;
        maxZ = Mathf.Ceil((centerZ + half) / bgCell) * bgCell;
    }

    /// <summary>The core's own grid-index range, expressed in the background's grid-index space: where the core's (0,0) and
    /// (GridWidth,GridHeight) corners land. Exact integer arithmetic - the cells are the same size and ComputeBounds always rounds
    /// to whole cells.</summary>
    private static void ComputeCoreGridRange(SC2TerrainData coreData, float minX, float minZ, float bgCell,
        out int coreGxMin, out int coreGzMin, out int coreGxMax, out int coreGzMax)
    {
        coreGxMin = Mathf.RoundToInt((0f - minX) / bgCell);
        coreGzMin = Mathf.RoundToInt((0f - minZ) / bgCell);
        coreGxMax = coreGxMin + coreData.GridWidth;
        coreGzMax = coreGzMin + coreData.GridHeight;
    }

    /// <summary>The background's grid-index of the core's (0,0) corner - what a caller needs to map a core vertex onto the matching
    /// background rim vertex.</summary>
    public static void GetCoreOriginInBackground(SC2TerrainData coreData, out int gx, out int gz)
    {
        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out _, out _, out var bgCell);
        ComputeCoreGridRange(coreData, minX, minZ, bgCell, out gx, out gz, out _, out _);
    }

    /// <summary>The background's world-space offset (minX/minZ of ComputeBounds) - every vertex a caller gets back from
    /// BuildForExportOrPreview, or reads directly out of coreData.BackgroundTerrainData.Heights via grid index, still needs this
    /// added to land in real world space. Cheap/deterministic, safe to call every frame.</summary>
    public static Vector3 GetWorldOffset(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.IsInitialized) return Vector3.zero;
        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out _, out _, out _);
        return new Vector3(minX, 0f, minZ);
    }

    /// <summary>Returns the existing coreData.BackgroundTerrainData UNCHANGED if one already exists AND its stored grid still matches
    /// what current settings would produce - never overwrites hand-painted heights/materials/pairs in that case. Calls Regenerate
    /// when none exists yet, OR when the stored grid no longer matches the current cell size / grid size / footprint: a background
    /// from before the grid density followed the core, or one resized by the footprint, has nothing that can carry over to a
    /// different grid. This is what preview/export should call on every tick; the explicit "Regenerate" button exists for the one
    /// real discard case this can't cover - a fresh procedural pass at the SAME settings.</summary>
    public static SC2TerrainData EnsureGenerated(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.IsInitialized) return null;
        var bg = coreData.BackgroundTerrainData;
        if (bg == null) return Regenerate(coreData);
        return TopologyMatches(coreData, bg) ? bg : Regenerate(coreData);
    }

    /// <summary>True when bg's own stored CellSize/GridWidth/GridHeight already match what ComputeBounds would produce right now for
    /// coreData's CURRENT state - i.e. the offset GetWorldOffset returns today is the same one bg's vertices were built against.</summary>
    private static bool TopologyMatches(SC2TerrainData coreData, SC2TerrainData bg)
    {
        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out var bgCell);
        var expectedGw = Mathf.Max(2, Mathf.RoundToInt((maxX - minX) / bgCell));
        var expectedGh = Mathf.Max(2, Mathf.RoundToInt((maxZ - minZ) / bgCell));
        return bg.GridWidth == expectedGw && bg.GridHeight == expectedGh && Mathf.Abs(bg.CellSize - bgCell) < 0.0001f;
    }

    /// <summary>Builds/refreshes coreData.BackgroundTerrainData IN PLACE (same instance reused across calls) - ALWAYS overwrites
    /// Heights/TrianglePairIndex from scratch, i.e. discards any hand-painted geometry/pairs. Deterministic given coreData +
    /// BackgroundTerrainSize. Call only from an explicit user action (see EnsureGenerated for the non-destructive alternative every
    /// per-tick caller should use).</summary>
    public static SC2TerrainData Regenerate(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.IsInitialized) return null;

        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out var bgCell);
        var gw = Mathf.Max(2, Mathf.RoundToInt((maxX - minX) / bgCell));
        var gh = Mathf.Max(2, Mathf.RoundToInt((maxZ - minZ) / bgCell));

        var bg = coreData.BackgroundTerrainData;
        if (bg != null && bg.IsInitialized && bg.GridWidth > 0 && Mathf.Abs(bg.CellSize - bgCell) > 0.0001f)
        {
            _pendingNotice = "The background was rebuilt at the core's grid density (it no longer needs a seam strip); " +
                             "the hand edits of the old, coarser background were discarded.";
            SC2Log.LogWarning("[SC2BackgroundTerrain] " + _pendingNotice);
        }
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

        // Height: a cheap, deterministic falloff away from the hole (so repeated regeneration is visually stable, not
        // re-randomized), based purely on grid-index distance outside the core rectangle. Not physically simulated - this is
        // decorative geometry that only needs to look plausible from normal RTS camera distance. Right at the rim it is the
        // core's own perimeter height, blended into the falloff over RingBlendCells so the surface leaves the seam without a step.
        var edgeHeight = AverageCoreEdgeHeight(coreData);
        var vw = gw + 1;
        var coreVw = coreData.GridWidth + 1;
        var falloffSpan = Mathf.Max(1f, Mathf.Min(gw, gh) * 0.5f);
        for (var gz = 0; gz <= gh; gz++)
        for (var gx = 0; gx <= gw; gx++)
        {
            var cx = Mathf.Clamp(gx - holeGxMin, 0, coreData.GridWidth);
            var cz = Mathf.Clamp(gz - holeGzMin, 0, coreData.GridHeight);
            var coreIdx = cz * coreVw + cx;
            var coreH = coreIdx < coreData.Heights.Length ? coreData.Heights[coreIdx] : SC2TerrainData.DefaultFlatHeight;
            var dOut = DistanceOutsideRect(gx, gz, holeGxMin, holeGzMin, holeGxMax, holeGzMax);
            if (dOut <= 0f)
            {
                bg.Heights[gz * vw + gx] = coreH;
                continue;
            }
            var wx = minX + gx * bgCell;
            var wz = minZ + gz * bgCell;
            var t = Mathf.Clamp01(dOut / falloffSpan);
            var noise = (Mathf.PerlinNoise(wx * 0.008f + 1000f, wz * 0.008f + 1000f) - 0.5f) * 60f;
            var natural = edgeHeight + noise * t;
            var w = Mathf.Clamp01(dOut / RingBlendCells);
            w = w * w * (3f - 2f * w);
            bg.Heights[gz * vw + gx] = Mathf.Lerp(coreH, natural, w);
        }

        EnsureBackgroundPair(bg);
        BuildPairIndexAndCarveHole(bg, holeGxMin, holeGzMin, holeGxMax, holeGzMax);
        bg.InvalidateGeometryCache();

        coreData.BackgroundTerrainData = bg;
        return bg;
    }

    /// <summary>Copies the core's perimeter heights onto the matching rim vertices of the background, so the two surfaces meet at
    /// exactly the same height all along the seam. Returns whether anything changed.</summary>
    public static bool SyncRingFromCore(SC2TerrainData coreData, SC2TerrainData bg)
    {
        if (coreData == null || bg == null || coreData.Heights == null || bg.Heights == null) return false;
        GetCoreOriginInBackground(coreData, out var gx0, out var gz0);
        var cw = coreData.GridWidth;
        var ch = coreData.GridHeight;
        var coreVw = cw + 1;
        var bgVw = bg.GridWidth + 1;
        var changed = false;

        void Copy(int cx, int cz)
        {
            var bgIdx = (gz0 + cz) * bgVw + gx0 + cx;
            var coreIdx = cz * coreVw + cx;
            if (bgIdx < 0 || bgIdx >= bg.Heights.Length || coreIdx >= coreData.Heights.Length) return;
            if (bg.Heights[bgIdx] == coreData.Heights[coreIdx]) return;
            bg.Heights[bgIdx] = coreData.Heights[coreIdx];
            changed = true;
        }

        for (var cx = 0; cx <= cw; cx++) { Copy(cx, 0); Copy(cx, ch); }
        for (var cz = 1; cz < ch; cz++) { Copy(0, cz); Copy(cw, cz); }
        return changed;
    }

    /// <summary>The smallest detail a background pair can be set to.</summary>
    public const float MinDetail = 0.02f;

    /// <summary>A background pair's Simplify slider, 0.02 to 1: 1 keeps the pair's share of <see cref="ExportTriangleBudget"/> and
    /// lower values keep that fraction of it, so the background can be made lighter than the budget but never heavier. It is
    /// stored in the pair's SimplifyRatio (as 1 - detail, so the default 0 means full detail) and therefore saved, undone and
    /// copied like every other pair field.</summary>
    public static float GetPairDetail(SC2MaterialPair pair) => Mathf.Clamp(1f - pair.SimplifyRatio, MinDetail, 1f);

    public static void SetPairDetail(SC2MaterialPair pair, float detail) => pair.SimplifyRatio = 1f - Mathf.Clamp(detail, MinDetail, 1f);

    /// <summary>How many source triangles background pair <paramref name="pairIndex"/> has, from the pair's cached count (cheap enough
    /// to draw every frame; see EstimateExportTriangles).</summary>
    public static int PairSourceTriangles(SC2TerrainData bg, int pairIndex) =>
        bg?.Pairs != null && bg.TrianglePairIndex != null && pairIndex >= 0 && pairIndex < bg.Pairs.Count
            ? bg.Pairs[pairIndex].GetTriangleCount(bg.TrianglePairIndex, pairIndex)
            : 0;

    private static int[] CountPairTriangles(SC2TerrainData bg)
    {
        var counts = new int[bg.Pairs != null ? bg.Pairs.Count : 0];
        if (bg.TrianglePairIndex == null) return counts;
        foreach (var pair in bg.TrianglePairIndex)
            if (pair >= 0 && pair < counts.Length) counts[pair]++;
        return counts;
    }

    // What pair i is simplified down to: its share of the budget (by how many source triangles it has), times its detail.
    private static float PairTargetTriangles(int[] counts, int total, int i, SC2MaterialPair pair) =>
        total <= 0 ? 0f : ExportTriangleBudget * (counts[i] / (float)total) * GetPairDetail(pair);

    /// <summary>The simplification ratio of every background pair (what SC2TerrainSimplifier takes: it keeps 1 - ratio of the pair's
    /// triangles), so that the background comes out at its budget: the whole background at most <see cref="ExportTriangleBudget"/>
    /// triangles, shared between the pairs by their size, each pair lowered further by its own detail. 0 where a pair is already
    /// within its target.</summary>
    public static float[] ExportSimplifyRatios(SC2TerrainData bg)
    {
        var ratios = new float[bg?.Pairs != null ? bg.Pairs.Count : 0];
        if (ratios.Length == 0) return ratios;
        var counts = CountPairTriangles(bg);
        var total = 0;
        foreach (var c in counts) total += c;
        for (var i = 0; i < ratios.Length; i++)
        {
            if (counts[i] <= 0) continue;
            var target = Mathf.Max(2f, PairTargetTriangles(counts, total, i, bg.Pairs[i]));
            ratios[i] = target >= counts[i] ? 0f : Mathf.Clamp(1f - target / counts[i], 0f, 0.995f);
        }
        return ratios;
    }

    /// <summary>About how many triangles the background exports with its current settings. Drawn every frame on the Background tab,
    /// so it reads the pairs' cached triangle counts (SC2MaterialPair.GetTriangleCount) instead of passing over millions of cells
    /// each time; the export itself (ExportSimplifyRatios) always counts afresh.</summary>
    public static int EstimateExportTriangles(SC2TerrainData bg)
    {
        if (bg?.Pairs == null || bg.TrianglePairIndex == null) return 0;
        var counts = new int[bg.Pairs.Count];
        for (var i = 0; i < counts.Length; i++) counts[i] = bg.Pairs[i].GetTriangleCount(bg.TrianglePairIndex, i);
        var total = 0;
        foreach (var c in counts) total += c;
        var sum = 0f;
        for (var i = 0; i < counts.Length; i++)
            if (counts[i] > 0) sum += Mathf.Min(counts[i], Mathf.Max(2f, PairTargetTriangles(counts, total, i, bg.Pairs[i])));
        return Mathf.RoundToInt(sum);
    }

    /// <summary>Generates/refreshes the background, then runs it through the SAME simplify pipeline the core uses, simplified to
    /// <see cref="ExportTriangleBudget"/> triangles at most (each pair's Simplify slider can lower that) with its rim locked so the seam against the core can never open.
    /// worldOffset is the translate every returned vertex still needs (the background's own local grid-index (0,0) is NOT world
    /// (0,0) - see this class' own header comment).</summary>
    public static SC2TerrainBuildResult BuildForExportOrPreview(SC2TerrainData coreData, out Vector3 worldOffset)
    {
        worldOffset = Vector3.zero;
        var bg = EnsureGenerated(coreData);
        if (bg == null) return new SC2TerrainBuildResult();

        ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out _, out _, out var bgCell);
        worldOffset = new Vector3(minX, 0f, minZ);

        // The core's saved heights are authoritative at the seam, whatever the live preview last showed.
        SyncRingFromCore(coreData, bg);

        ComputeCoreGridRange(coreData, minX, minZ, bgCell, out var holeGxMin, out var holeGzMin, out var holeGxMax, out var holeGzMax);
        var ring = ComputeRing(bg, holeGxMin, holeGzMin, holeGxMax, holeGzMax);

        // The triangles that touch a hole corner are the one place where vertices of two different sides are neighbours (see
        // HoleSideRegion), so the few vertices right at each corner stay as they are.
        const int cornerKeepCells = 2;
        var vw = bg.GridWidth + 1;
        foreach (var cornerX in new[] { holeGxMin, holeGxMax })
        foreach (var cornerZ in new[] { holeGzMin, holeGzMax })
            for (var gz = Mathf.Max(0, cornerZ - cornerKeepCells); gz <= Mathf.Min(bg.GridHeight, cornerZ + cornerKeepCells); gz++)
            for (var gx = Mathf.Max(0, cornerX - cornerKeepCells); gx <= Mathf.Min(bg.GridWidth, cornerX + cornerKeepCells); gx++)
                ring.Add(gz * vw + gx);

        // Only the rim itself is locked; no extra full-density ring or corner area is kept next to the hole.
        return SC2TerrainLayerBuilder.Build(bg, sortByTriangleCount: true, null, ring, false, ExportSimplifyRatios(bg),
            protectRingAroundUnassigned: false, clusterRegion: HoleSideRegion(bgCell, holeGxMin, holeGzMin, holeGxMax, holeGzMax));
    }

    private static void EnsureBackgroundPair(SC2TerrainData bg)
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

    /// <summary>Which side of the hole a background vertex (in the background's own coordinates) is on: left/middle/right by
    /// column and below/middle/above by row, as one id. The simplifier merges vertices that fall in the same grid cell into their
    /// average. Along a straight edge of the hole every vertex of such a cell is on the same side, so the average stays outside it;
    /// a cell holding a hole CORNER mixes vertices of two sides, whose average can land inside the hole and fold triangles over
    /// the core. Keeping the sides apart means a merged vertex is always the average of points in one region that does not
    /// contain the hole - no extra full-density area is needed at the corners.</summary>
    private static System.Func<float, float, int> HoleSideRegion(float cell, int gxMin, int gzMin, int gxMax, int gzMax)
    {
        float x0 = gxMin * cell, x1 = gxMax * cell, z0 = gzMin * cell, z1 = gzMax * cell;
        return (x, z) => (x < x0 ? 0 : x > x1 ? 2 : 1) * 3 + (z < z0 ? 0 : z > z1 ? 2 : 1);
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
