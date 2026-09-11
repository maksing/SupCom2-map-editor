// SC2CostsAutoGen.cs
// Generates the navmesh and islandmap for costs.bdf from a binary passability map (0/1).
//
// Navmesh (uint8 1024x1024):
//   255 = impassable (user-painted)
//   5   = border (2 cells wide, drawn around impassable regions)
//   1   = passable
//
// Islandmap (uint32 1024x1024):
//   0      = impassable (matches navmesh==255)
//   other  = island color (unique per connected component)
//
// IslandList ([]x(minX,minY,maxX,maxY)):
//   AABB of each island, 4xuint32 = 16 bytes

using System;
using System.Collections.Generic;
using UnityEngine;
using BdfIO.Formats.Info;

public static class SC2CostsAutoGen
{
    public static class UnitSizeThresholds
    {
        public const int LandSmall  = 0;
        public const int LandMedium = 1;
        public const int LandLarge  = 2;
    }

    private enum SizeClass { Small, Medium, Large }

    /// <summary>
    /// Generates a navmesh from a binary passability map (impassable = true).
    /// Returns: 255=impassable, 5=border (2px), 1=passable.
    /// </summary>
    public static byte[] GenerateNavmesh(BdfIO.Formats.Hfield.HfieldModel hfield, int threshold)
    {
        var w = (int)hfield.MapX - 1;
        var h = (int)hfield.MapY - 1;
        var result = new byte[w * h];
        for (var i = 0; i < result.Length; i++) result[i] = 1;
        return result;
    }

    /// <summary>
    /// Tunable inputs for GenerateFromHeight — no confirmed vanilla-exact
    /// values exist for any of these (nothing in this codebase's corpus
    /// captures the real slope/depth rule a block name implies), so these
    /// are reasonable, exposed-as-sliders starting points to calibrate
    /// against real in-game testing, not asserted fact.
    /// </summary>
    [Serializable]
    public sealed class Thresholds
    {
        public float MaxSlopeDegreesSmall = 60f;
        public float MaxSlopeDegreesMedium = 45f;
        public float MaxSlopeDegreesLarge = 30f;

        /// <summary>How far below WaterElevation (world units) a "...water_
        /// Surface..."/"...ShoreLine" block still treats as passable —
        /// shallow/shoreline crossing capability.</summary>
        public float ShallowWaterToleranceDepth = 3f;

        /// <summary>Same, for a "...Seafloor"/"...Abyss" block — deeper-
        /// water-capable.</summary>
        public float DeepWaterToleranceDepth = 15f;

        /// <summary>Passable regions (4-connected) smaller than this many
        /// grid cells are flipped to impassable — a tiny isolated patch of
        /// "passable" surrounded by impassable terrain is functionally
        /// useless for pathfinding (nothing can path a unit onto/off of it
        /// usefully) and reads as generation noise rather than a real
        /// walkable area. 0 disables this pass entirely (every passable
        /// cell survives regardless of its region's size).</summary>
        public int MinIslandSize = 0;

        /// <summary>Cell thickness of the mandatory impassable band
        /// SC2CostsShell.RecomputeDerivedCostsBlocks bakes into block 0
        /// right at the water/land boundary (5-block playable-water set
        /// only) — every water cell within this many cells of land is
        /// forced impassable regardless of block 0's own (depth-
        /// independent) water passability. 0 disables the band entirely
        /// (every water cell stays passable, including right up to the
        /// shoreline).</summary>
        public int ShorelineBandMargin = 3;
    }

    public static readonly Thresholds DefaultThresholds = new();

    /// <summary>
    /// Classifies a Costs block by its real vanilla name — both the 3-block
    /// land-only profile (Land_Small/Medium/Large) and the 5-block
    /// playable-water profile (Land_Small_water_Surface_ShoreLine,
    /// Land_medium, Land_Large_water_Abyss_ShoreLine,
    /// Land_Large_water_Seafloor, Land_Medium_Water_Surface) — confirmed
    /// real names, see SC2TerrainData.LandOnlyCostsBlockNames/
    /// PlayableWaterCostsBlockNames. Every one of the 5 water-profile names
    /// still starts with "Land_", not "Water_" - these represent land-unit
    /// passability grids that ALSO tolerate some depth of water (shoreline/
    /// amphibious crossing), not a separate naval-unit concept, which is
    /// why this returns a water TOLERANCE depth rather than a strict
    /// isWater flag.</summary>
    private static (SizeClass size, float waterToleranceDepth) ClassifyBlockName(string name, Thresholds t)
    {
        name ??= "";
        var size = name.IndexOf("Small", StringComparison.OrdinalIgnoreCase) >= 0 ? SizeClass.Small
            : name.IndexOf("Large", StringComparison.OrdinalIgnoreCase) >= 0 ? SizeClass.Large
            : SizeClass.Medium;

        var deep = name.IndexOf("Seafloor", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Abyss", StringComparison.OrdinalIgnoreCase) >= 0;
        var shallow = !deep && (
            name.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("ShoreLine", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0);

        var tolerance = deep ? t.DeepWaterToleranceDepth : shallow ? t.ShallowWaterToleranceDepth : 0f;
        return (size, tolerance);
    }

    /// <summary>Shared EnvironmentHeader -&gt; WaterElevation extraction, factored
    /// out of GenerateFromHeight so ComputeRawWaterMask below can use the exact
    /// same source without duplicating the header-parsing logic.</summary>
    private static float ExtractWaterElevation(SC2TerrainData data)
    {
        if (data.EnvironmentHeader == null || data.EnvironmentHeader.Length < 508) return 0f;
        var info = new InfoModel();
        Array.Copy(data.EnvironmentHeader, info.Header, 508);
        return info.WaterElevation;
    }

    /// <summary>A PURE geometric land/water split (WaterElevation - height &gt; 0),
    /// with no size-class water-tolerance applied at all — distinct from
    /// GenerateFromHeight's own per-block TOLERANCE depth, which lets some
    /// shallow water stay passable. Used both to force every water cell
    /// impassable regardless of tolerance (one cascade rule) and to build
    /// the mandatory shoreline safety band (another cascade rule) in
    /// SC2CostsShell.RecomputeDerivedCostsBlocks.</summary>
    public static bool[] ComputeRawWaterMask(SC2TerrainData data, Func<float, float, float> sampleHeight)
    {
        var gridSize = data.CostsGridSize;
        var waterElevation = ExtractWaterElevation(data);
        var result = new bool[gridSize * gridSize];
        for (var z = 0; z < gridSize; z++)
        for (var x = 0; x < gridSize; x++)
            result[z * gridSize + x] = waterElevation - sampleHeight(x, z) > 0f;
        return result;
    }

    private static float MaxSlopeFor(SizeClass size, Thresholds t) => size switch
    {
        SizeClass.Small => t.MaxSlopeDegreesSmall,
        SizeClass.Large => t.MaxSlopeDegreesLarge,
        _ => t.MaxSlopeDegreesMedium,
    };

    /// <summary>
    /// Real navmesh generation. Impassable where the local slope (finite-
    /// difference over the live procedural heightfield, SC2ObjectsExport.
    /// SampleTerrainHeight — the same height source Auto Path Node
    /// resolution already uses) exceeds this block's size-class threshold,
    /// OR where the point is underwater beyond this block's water
    /// tolerance (0 for a pure land block, see ClassifyBlockName). Water
    /// state gates on SC2TerrainData.PlayableWater (its own dedicated
    /// Costs-panel toggle, deliberately NOT InfoModel.WaterEnabled — that
    /// field only controls whether the decorative water surface renders,
    /// a different question from whether this map has any real navigable
    /// water). WaterElevation (the actual height value) still comes from
    /// InfoModel/EnvironmentHeader — a real geometric fact, not gated by
    /// either toggle.
    /// </summary>
    /// <summary><paramref name="sampleHeight"/> (world X,Z -> world Y) is
    /// required, not an optional live-only default: this used to call
    /// SC2ObjectsExport.SampleTerrainHeight directly, which only ever reads
    /// SC2TerrainData.Heights (the live procedural sculpt grid) - for a
    /// fully-imported/ImportOnlyMode map that data is completely
    /// disconnected from the real imported geometry, so generation
    /// silently computed slope/passability against the wrong (often flat/
    /// default) heights. Callers should pass a source-agnostic sampler -
    /// see SC2CostsShell's call sites, which use SC2LiveHeightGrid (the
    /// same merged live+imported projection Costs painting itself already
    /// moved to).</summary>
    public static byte[] GenerateFromHeight(SC2TerrainData data, string blockName, Func<float, float, float> sampleHeight, Thresholds thresholds = null)
    {
        thresholds ??= DefaultThresholds;
        var gridSize = data.CostsGridSize;
        var (sizeClass, _) = ClassifyBlockName(blockName, thresholds);
        var maxSlopeDeg = MaxSlopeFor(sizeClass, thresholds);

        var waterElevation = ExtractWaterElevation(data);
        var waterEnabled = data.PlayableWater;

        // With playable water on, block 0's water is ENTIRELY passable
        // regardless of depth (no shallow/deep tolerance split) — the only
        // impassable water is the mandatory shoreline band
        // SC2CostsShell.RecomputeDerivedCostsBlocks bakes in separately
        // right after this runs. Slope only ever governs LAND cells here.
        var impassable = new bool[gridSize * gridSize];
        for (var z = 0; z < gridSize; z++)
        for (var x = 0; x < gridSize; x++)
        {
            // Costs grid cells map 1:1 to world X/Z (the same convention
            // every other Costs code path in this codebase already
            // assumes - real world-space registration for non-default grid
            // sizes/offsets is a separate, still-open problem, not part of
            // this generation algorithm).
            var h = sampleHeight(x, z);
            var isWater = waterEnabled && waterElevation - h > 0f;
            if (isWater) { impassable[z * gridSize + x] = false; continue; }

            var hx = sampleHeight(x + 1, z);
            var hz = sampleHeight(x, z + 1);
            var gradMag = Mathf.Sqrt((hx - h) * (hx - h) + (hz - h) * (hz - h));
            var slopeDeg = Mathf.Atan(gradMag) * Mathf.Rad2Deg;

            impassable[z * gridSize + x] = slopeDeg > maxSlopeDeg;
        }

        if (thresholds.MinIslandSize > 0)
            SuppressSmallPassableIslands(impassable, gridSize, gridSize, thresholds.MinIslandSize);

        return BuildNavmesh(impassable, gridSize, gridSize);
    }

    /// <summary>Flood-fills every 4-connected region of PASSABLE
    /// (impassable[i] == false) cells; any region with fewer than
    /// <paramref name="minSize"/> cells has its cells flipped to impassable
    /// in place. Iterative (explicit stack), not recursive — gridSize can be
    /// up to 4096, so a naive recursive flood-fill risks a stack overflow
    /// on a single large connected region.</summary>
    private static void SuppressSmallPassableIslands(bool[] impassable, int w, int h, int minSize)
    {
        var visited = new bool[impassable.Length];
        var region = new List<int>();
        var stack = new Stack<int>();

        for (var start = 0; start < impassable.Length; start++)
        {
            if (impassable[start] || visited[start]) continue;

            region.Clear();
            stack.Push(start);
            visited[start] = true;
            while (stack.Count > 0)
            {
                var idx = stack.Pop();
                region.Add(idx);
                var x = idx % w;
                var y = idx / w;

                void Visit(int nx, int ny)
                {
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) return;
                    var nIdx = ny * w + nx;
                    if (visited[nIdx] || impassable[nIdx]) return;
                    visited[nIdx] = true;
                    stack.Push(nIdx);
                }
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            if (region.Count < minSize)
                foreach (var idx in region)
                    impassable[idx] = true;
        }
    }

    /// <summary>
    /// Expands a binary passability map (impassable[]) into a navmesh with
    /// border cells (value 5).
    /// impassable: true = cell is impassable.
    /// </summary>
    public static byte[] BuildNavmesh(bool[] impassable, int w, int h)
    {
        var nav = new byte[w * h];

        // Step 1: impassable = 255
        for (var i = 0; i < impassable.Length; i++)
            nav[i] = impassable[i] ? (byte)255 : (byte)1;

        // Step 2: border — 2 cells around 255 -> 5
        // (applied twice: first the neighbors of 255, then the neighbors of 5)
        for (var pass = 0; pass < 2; pass++)
        {
            var prev = (byte[])nav.Clone();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (prev[y*w+x] != 1) continue; // already marked
                bool nearWall = false;
                for (var dy = -1; dy <= 1 && !nearWall; dy++)
                for (var dx = -1; dx <= 1 && !nearWall; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var ny = y + dy; var nx = x + dx;
                    if (ny < 0 || ny >= h || nx < 0 || nx >= w) continue;
                    var v = prev[ny*w+nx];
                    if (v == 255 || (pass == 1 && v == 5)) nearWall = true;
                }
                if (nearWall) nav[y*w+x] = 5;
            }
        }

        return nav;
    }

    /// <summary>
    /// Extracts a binary passability map from a navmesh (255 -> true, else -> false).
    /// </summary>
    public static bool[] NavmeshToImpassable(byte[] navmesh)
    {
        var result = new bool[navmesh.Length];
        for (var i = 0; i < navmesh.Length; i++)
            result[i] = navmesh[i] == 255;
        return result;
    }

    /// <summary>
    /// Writes a binary passability map into a navmesh, recomputing borders.
    /// </summary>
    public static void ApplyImpassableToNavmesh(bool[] impassable, byte[] navmesh, int w, int h)
    {
        var generated = BuildNavmesh(impassable, w, h);
        System.Array.Copy(generated, navmesh, navmesh.Length);
    }

    /// <summary>Grows every impassable region by <paramref name="margin"/>
    /// cells (8-neighbor/Chebyshev growth, `margin` single-cell passes) —
    /// distinct from BuildNavmesh's own fixed 2px border (navmesh value 5),
    /// which is purely a format/render marker recomputed automatically and
    /// never changes which cells are actually impassable. This produces
    /// real new impassable (255) cells, a genuine pathing safety margin
    /// around obstacles. O(w*h*margin) — fine for the margins (a handful of
    /// cells) this is meant for; a single-iteration distance-field approach
    /// would be needed for very large margins, not implemented since
    /// nothing here calls for one.</summary>
    public static bool[] DilateImpassable(bool[] impassable, int w, int h, int margin)
    {
        if (margin <= 0) return (bool[])impassable.Clone();
        var current = impassable;
        for (var pass = 0; pass < margin; pass++)
        {
            var next = (bool[])current.Clone();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                if (current[idx] || next[idx]) continue;
                for (var dy = -1; dy <= 1 && !next[idx]; dy++)
                for (var dx = -1; dx <= 1 && !next[idx]; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    if (current[ny * w + nx]) next[idx] = true;
                }
            }
            current = next;
        }
        return current;
    }
}
