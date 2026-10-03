using System;
using System.Collections.Generic;

/// <summary>
/// Updates a blend mask layer that holds pair outlines after the pair assignments changed: draws the outline of the cells
/// that newly became a pair's and removes the outline that is no longer along a pair border. Pure (no Unity types), so the
/// whole pipeline can be tested. Everything runs on the neighbourhood of the changed cells (see <see cref="Work"/>), not on
/// the whole mask.
/// </summary>
public static class SC2PairOutlineUpdater
{
    /// <summary>How an outline is drawn. Distances are in world units; Smoothing is in grid cells.</summary>
    public readonly struct Settings
    {
        public readonly float Width, Spread, Smoothing, Softness, Blend;
        public Settings(float width, float spread, float smoothing, float softness, float blend)
        { Width = width; Spread = spread; Smoothing = smoothing; Softness = softness; Blend = blend; }
    }

    /// <summary>Layer geometry: the mask in pixels and the terrain grid it covers.</summary>
    public readonly struct Geometry
    {
        public readonly int MaskWidth, MaskHeight, GridWidth, GridHeight;
        public readonly float CellSize;
        public Geometry(int maskWidth, int maskHeight, int gridWidth, int gridHeight, float cellSize)
        { MaskWidth = maskWidth; MaskHeight = maskHeight; GridWidth = gridWidth; GridHeight = gridHeight; CellSize = cellSize; }

        public float PixelsPerUnit => MaskWidth / (GridWidth * CellSize);
        public float CellPixels => (float)MaskWidth / GridWidth;
    }

    /// <summary>How far from a pair border an outline pixel can legitimately be: the wider of the inside band and the spread,
    /// plus the reach of the smoothing, plus one cell (the border sits on a cell edge).</summary>
    public static float ReachPixels(Geometry g, float width, float spread, float smoothingCells)
        => Math.Max(width, spread) * g.PixelsPerUnit + 2f * smoothingCells * g.CellPixels + g.CellPixels;

    /// <summary>The smallest outline that still shows on a mask: Width and Spread two mask pixels (below that the band is the
    /// one-pixel line along the border that is always drawn anyway), Smoothing one mask pixel (a smaller blur radius rounds to
    /// nothing). Width and Spread in world units, Smoothing in grid cells; Spread and Smoothing may also be 0 (off).</summary>
    public static Settings Minimum(Geometry g)
        => new Settings(MinimumBandPixels / g.PixelsPerUnit, MinimumBandPixels / g.PixelsPerUnit, 1f / g.CellPixels, 0f, 0f);

    private const float MinimumBandPixels = 2f;

    /// <summary>Smoothing above this (in grid cells) only distorts the shape: the corners of the regions are rounded off by more
    /// than the band can cover without lumps, and thin parts are eaten.</summary>
    public const float MaximumSmoothingCells = 1.5f;

    /// <summary><paramref name="settings"/> raised to <see cref="Minimum"/>: Width always, Spread and Smoothing when above zero
    /// (zero stays zero); Smoothing is also capped at <see cref="MaximumSmoothingCells"/>.</summary>
    public static Settings Clamp(Settings settings, Geometry g)
    {
        var minimum = Minimum(g);
        return new Settings(
            Math.Max(settings.Width, minimum.Width),
            settings.Spread > 0f ? Math.Max(settings.Spread, minimum.Spread) : 0f,
            settings.Smoothing > 0f ? Math.Min(MaximumSmoothingCells, Math.Max(settings.Smoothing, minimum.Smoothing)) : 0f,
            settings.Softness, settings.Blend);
    }

    /// <summary>How far from a pair border line a band pixel can be: the band reaches Width inside and Spread outside of the
    /// smoothed outline of the region, which itself can lie up to the maximum
    /// shift away from the border; the distance measure over-estimates by up to 8 %, and a pixel or two are slack for the
    /// soft edge.</summary>
    public static float SupportReachPixels(Geometry g, float width, float spread, float smoothingCells)
        => (Math.Max(width, spread) * g.PixelsPerUnit + (smoothingCells > 0f ? MaxShiftPixels(g) + 2f : 0f)) * 1.1f + 3f;

    /// <summary>How far the smoothed outline of a region may move away from the real (cell-shaped) border: about half a grid
    /// cell, which is what a staircase deviates from a smooth line through its middle.</summary>
    public static float MaxShiftPixels(Geometry g) => g.CellPixels * 0.5f + 1f;

    /// <summary>The same towards the outside, much less: a gap one cell wide between two parts of a region (on a diagonal its
    /// middle is only 0.35 cell from either side) must stay a gap, with the border along it outlined, not be bridged by the
    /// smoothing.</summary>
    public static float MaxOutwardShiftPixels(Geometry g) => g.CellPixels * 0.25f;

    /// <summary>The part of the mask an update has to look at. <c>Core</c> is where anything can change (the changed cells plus
    /// the outline's reach); <c>Crop</c> is Core plus a margin as wide again, so distances measured inside Core see every
    /// border they could reach.</summary>
    public readonly struct Work
    {
        public readonly SC2PairOutlineMath.MaskRect Core, Crop;
        public readonly int CropCellX0, CropCellZ0, CropCellX1, CropCellZ1;

        public Work(SC2PairOutlineMath.MaskRect core, SC2PairOutlineMath.MaskRect crop, int cx0, int cz0, int cx1, int cz1)
        { Core = core; Crop = crop; CropCellX0 = cx0; CropCellZ0 = cz0; CropCellX1 = cx1; CropCellZ1 = cz1; }

        public static Work Whole(Geometry g)
        {
            var full = new SC2PairOutlineMath.MaskRect(0, 0, g.MaskWidth, g.MaskHeight);
            return new Work(full, full, 0, 0, g.GridWidth, g.GridHeight);
        }

        /// <param name="minX">Inclusive cell bounds of everything that changed.</param>
        public static Work Around(Geometry g, int minX, int minZ, int maxX, int maxZ, float reachPixels)
        {
            var reachCellsX = (int)Math.Ceiling(reachPixels * g.GridWidth / g.MaskWidth) + 1;
            var reachCellsZ = (int)Math.Ceiling(reachPixels * g.GridHeight / g.MaskHeight) + 1;
            var core = SC2PairOutlineMath.MaskRectOfCells(minX - reachCellsX, minZ - reachCellsZ, maxX + 1 + reachCellsX, maxZ + 1 + reachCellsZ,
                g.GridWidth, g.GridHeight, g.MaskWidth, g.MaskHeight);
            var cx0 = Math.Max(0, minX - 2 * reachCellsX - 2);
            var cz0 = Math.Max(0, minZ - 2 * reachCellsZ - 2);
            var cx1 = Math.Min(g.GridWidth, maxX + 1 + 2 * reachCellsX + 2);
            var cz1 = Math.Min(g.GridHeight, maxZ + 1 + 2 * reachCellsZ + 2);
            var crop = SC2PairOutlineMath.MaskRectOfCells(cx0, cz0, cx1, cz1, g.GridWidth, g.GridHeight, g.MaskWidth, g.MaskHeight);
            return new Work(core, crop, cx0, cz0, cx1, cz1);
        }
    }

    /// <summary>The pixels of the work rectangle within <paramref name="supportReach"/> of a pair border that did not just
    /// change: the only place where outline may stay.</summary>
    public static byte[] ValidZone(Geometry g, short[] after, short[] before, Work work, float supportReach)
    {
        var borderLines = SC2PairOutlineMath.StableBorderLines(after, before, g.GridWidth, g.GridHeight,
            work.CropCellX0, work.CropCellZ0, work.CropCellX1, work.CropCellZ1, g.MaskWidth, g.MaskHeight, work.Crop);
        return SC2PairOutlineMath.DilateMask(borderLines, work.Crop.Width, work.Crop.Height, supportReach);
    }

    /// <summary>The cells whose assignment differs (with their bounds), or false when nothing changed.</summary>
    public static bool FindChangedCells(Geometry g, short[] before, short[] after,
        out int minX, out int minZ, out int maxX, out int maxZ, out List<int> changed)
    {
        minX = int.MaxValue; minZ = int.MaxValue; maxX = -1; maxZ = -1;
        changed = new List<int>();
        for (var cell = 0; cell * 2 + 1 < after.Length; cell++)
        {
            if (after[cell * 2] == before[cell * 2] && after[cell * 2 + 1] == before[cell * 2 + 1]) continue;
            var x = cell % g.GridWidth;
            var z = cell / g.GridWidth;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
            changed.Add(cell);
        }
        return maxX >= 0;
    }

    /// <summary>The cells of every 4-connected area of <paramref name="pairIndex"/> that contains one of <paramref name="seeds"/>:
    /// the closed regions a stroke touched. Also returns their bounds.</summary>
    public static List<int> ConnectedPairCells(Geometry g, short[] assignments, int pairIndex, IEnumerable<int> seeds,
        out int minX, out int minZ, out int maxX, out int maxZ)
    {
        minX = int.MaxValue; minZ = int.MaxValue; maxX = -1; maxZ = -1;
        var visited = new bool[g.GridWidth * g.GridHeight];
        var cells = new List<int>();
        var stack = new Stack<int>();
        foreach (var seed in seeds)
        {
            if (visited[seed] || assignments[seed * 2] != pairIndex) continue;
            visited[seed] = true;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                var cell = stack.Pop();
                cells.Add(cell);
                var x = cell % g.GridWidth;
                var z = cell / g.GridWidth;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
                void Visit(int next)
                {
                    if (visited[next] || assignments[next * 2] != pairIndex) return;
                    visited[next] = true;
                    stack.Push(next);
                }
                if (x > 0) Visit(cell - 1);
                if (x < g.GridWidth - 1) Visit(cell + 1);
                if (z > 0) Visit(cell - g.GridWidth);
                if (z < g.GridHeight - 1) Visit(cell + g.GridWidth);
            }
        }
        return cells;
    }

    /// <summary>Updates <paramref name="layer"/> (2 bytes per pixel, edited in place; pass a private copy) for the move from the
    /// assignments <paramref name="before"/> to <paramref name="after"/>.
    ///
    /// Outline that is no longer along a border next to the changed cells is removed. With <paramref name="drawOutlines"/> the
    /// outline of the closed regions the change touched is made afresh: the regions of <paramref name="pairIndex"/> (when not
    /// negative) that the changed cells belong to, and the regions of every other pair next to the changed cells that had an
    /// outline before (a pair painted over, or a neighbour), so what is left of them keeps its outline, a single cell included.
    /// Their old outline (all round the region, not just next to the changed cells) is removed and the region's whole border
    /// is drawn again, inside the region and, Spread, onto its neighbours. So the outline of a region always matches its
    /// current shape and the current settings, however often it is repainted. Returns whether the layer changed.
    ///
    /// <paramref name="used"/> holds the largest settings outlines may have been drawn with, which sets how far from a border
    /// old outline is looked for.</summary>
    public static bool Update(byte[] layer, Geometry g, short[] before, short[] after, int pairIndex, bool drawOutlines, Settings current, Settings used)
    {
        if (!FindChangedCells(g, before, after, out _, out _, out _, out _, out var changed))
            return false;
        current = Clamp(current, g);
        var reach = ReachPixels(g, Math.Max(used.Width, current.Width), Math.Max(used.Spread, current.Spread), Math.Max(used.Smoothing, current.Smoothing));
        var supportReach = SupportReachPixels(g, Math.Max(used.Width, current.Width), Math.Max(used.Spread, current.Spread), Math.Max(used.Smoothing, current.Smoothing));
        var reachCellsX = (int)Math.Ceiling(reach * g.GridWidth / g.MaskWidth) + 1;
        var reachCellsZ = (int)Math.Ceiling(reach * g.GridHeight / g.MaskHeight) + 1;

        // A stroke with symmetry (or several strokes) changes cells in places far apart. Each place is updated on its own
        // work rectangle instead of one rectangle round all of them, which would be most of the map.
        var changedAny = false;
        foreach (var cluster in Cluster(g, after, pairIndex, drawOutlines, changed, reachCellsX, reachCellsZ))
        {
            var work = Work.Around(g, cluster.MinX, cluster.MinZ, cluster.MaxX, cluster.MaxZ, reach);
            changedAny |= UpdateIn(layer, g, work, before, after, pairIndex, cluster.Ring, cluster.Changed, current, supportReach, drawOutlines);
        }
        return changedAny;
    }

    private sealed class ChangeCluster
    {
        public List<int> Changed = new List<int>();
        public List<int> Ring;
        public int MinX, MinZ, MaxX, MaxZ;
    }

    /// <summary>Splits the changed cells into groups that lie so far apart that their work rectangles (cell bounds, with the closed
    /// region of the painted pair they belong to, plus the reach of the outline) do not touch.</summary>
    private static List<ChangeCluster> Cluster(Geometry g, short[] after, int pairIndex, bool drawOutlines, List<int> changed, int reachCellsX, int reachCellsZ)
    {
        // First by blocks: cells in blocks that touch (8 neighbours) belong together.
        var blockX = 2 * reachCellsX + 2;
        var blockZ = 2 * reachCellsZ + 2;
        var blocksX = (g.GridWidth + blockX - 1) / blockX;
        var blocksZ = (g.GridHeight + blockZ - 1) / blockZ;
        var label = new int[blocksX * blocksZ];
        foreach (var cell in changed) label[(cell / g.GridWidth / blockZ) * blocksX + (cell % g.GridWidth) / blockX] = -1;
        var groups = new List<ChangeCluster>();
        var stack = new Stack<int>();
        for (var start = 0; start < label.Length; start++)
        {
            if (label[start] != -1) continue;
            var id = groups.Count + 1;
            groups.Add(new ChangeCluster());
            label[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var block = stack.Pop();
                var bx = block % blocksX; var bz = block / blocksX;
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = bx + dx; var nz = bz + dz;
                    if (nx < 0 || nz < 0 || nx >= blocksX || nz >= blocksZ) continue;
                    var n = nz * blocksX + nx;
                    if (label[n] != -1) continue;
                    label[n] = id;
                    stack.Push(n);
                }
            }
        }
        foreach (var cell in changed)
            groups[label[(cell / g.GridWidth / blockZ) * blocksX + (cell % g.GridWidth) / blockX] - 1].Changed.Add(cell);

        // Then the closed regions of the painted pair: a region that several groups belong to joins them, whatever the distance.
        var clusters = new List<ChangeCluster>(groups);
        for (var i = 0; i < clusters.Count; i++) Describe(g, after, pairIndex, drawOutlines, clusters[i]);
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < clusters.Count && !merged; i++)
            for (var j = i + 1; j < clusters.Count && !merged; j++)
            {
                if (!Touch(clusters[i], clusters[j], reachCellsX, reachCellsZ)) continue;
                clusters[i].Changed.AddRange(clusters[j].Changed);
                clusters.RemoveAt(j);
                Describe(g, after, pairIndex, drawOutlines, clusters[i]);
                merged = true;
            }
        }
        return clusters;
    }

    // Bounds of the cluster's changed cells and, when its changed cells belong to the painted pair, of the closed regions of the pair.
    private static void Describe(Geometry g, short[] after, int pairIndex, bool drawOutlines, ChangeCluster cluster)
    {
        cluster.MinX = int.MaxValue; cluster.MinZ = int.MaxValue; cluster.MaxX = -1; cluster.MaxZ = -1;
        foreach (var cell in cluster.Changed)
        {
            var x = cell % g.GridWidth; var z = cell / g.GridWidth;
            if (x < cluster.MinX) cluster.MinX = x;
            if (x > cluster.MaxX) cluster.MaxX = x;
            if (z < cluster.MinZ) cluster.MinZ = z;
            if (z > cluster.MaxZ) cluster.MaxZ = z;
        }
        cluster.Ring = null;
        if (!drawOutlines || pairIndex < 0) return;
        var seeds = cluster.Changed.FindAll(c => after[c * 2] == pairIndex || after[c * 2 + 1] == pairIndex);
        if (seeds.Count == 0) return;
        cluster.Ring = ConnectedPairCells(g, after, pairIndex, seeds, out var rx0, out var rz0, out var rx1, out var rz1);
        if (cluster.Ring.Count == 0) return;
        cluster.MinX = Math.Min(cluster.MinX, rx0); cluster.MinZ = Math.Min(cluster.MinZ, rz0);
        cluster.MaxX = Math.Max(cluster.MaxX, rx1); cluster.MaxZ = Math.Max(cluster.MaxZ, rz1);
    }

    private static bool Touch(ChangeCluster a, ChangeCluster b, int reachCellsX, int reachCellsZ)
    {
        var gapX = 2 * reachCellsX + 2;
        var gapZ = 2 * reachCellsZ + 2;
        return a.MinX - gapX <= b.MaxX && b.MinX - gapX <= a.MaxX && a.MinZ - gapZ <= b.MaxZ && b.MinZ - gapZ <= a.MaxZ;
    }

    /// <summary><see cref="Update(byte[], Geometry, short[], short[], int, bool, Settings, Settings)"/> drawing the outline of
    /// <paramref name="pairIndex"/> (when not negative) and of the other outlined pairs next to the change.</summary>
    public static bool Update(byte[] layer, Geometry g, short[] before, short[] after, int pairIndex, Settings current, Settings used)
        => Update(layer, g, before, after, pairIndex, true, current, used);

    /// <summary>As <see cref="Update(byte[], Geometry, short[], short[], int, bool, Settings, Settings)"/> on a given work area,
    /// for callers that already know it. <paramref name="ringCells"/> are the cells of the region(s) of
    /// <paramref name="pairIndex"/> to outline afresh (null or empty: none); <paramref name="changedCells"/> the cells that
    /// changed (null: none). <paramref name="before"/> null counts every border as unchanged and has no other pairs to redraw.</summary>
    public static bool UpdateIn(byte[] layer, Geometry g, Work work, short[] before, short[] after, int pairIndex,
        List<int> ringCells, List<int> changedCells, Settings current, float supportReach, bool drawOthers = false)
    {
        var zone = ValidZone(g, after, before, work, supportReach);

        // The pairs whose outline is made afresh.
        var pairs = new List<int>();
        if (pairIndex >= 0 && ringCells != null && ringCells.Count > 0) pairs.Add(pairIndex);
        if (drawOthers && before != null && changedCells != null)
            foreach (var other in OutlinedPairsNextTo(layer, g, work, before, after, changedCells, pairIndex))
                if (!pairs.Contains(other)) pairs.Add(other);

        byte[] band = null;
        if (pairs.Count > 0)
        {
            // The old outline of these pairs goes entirely: everything near their borders, now and before. That is every area
            // of the pairs in the work rectangle, not only the touched ones, because an area next to a touched one shares
            // outline with it, and all of them are drawn again below.
            var cellSet = new byte[g.GridWidth * g.GridHeight];
            for (var z = work.CropCellZ0; z < work.CropCellZ1; z++)
            for (var x = work.CropCellX0; x < work.CropCellX1; x++)
                if (pairs.Contains(after[(z * g.GridWidth + x) * 2])) cellSet[z * g.GridWidth + x] = 1;
            if (ringCells != null) foreach (var cell in ringCells) cellSet[cell] = 1;
            if (changedCells != null) foreach (var cell in changedCells) cellSet[cell] = 1;
            var ringLines = SC2PairOutlineMath.TouchedBorderLines(after, before, cellSet, g.GridWidth, g.GridHeight,
                work.CropCellX0, work.CropCellZ0, work.CropCellX1, work.CropCellZ1, g.MaskWidth, g.MaskHeight, work.Crop);
            var ringZone = SC2PairOutlineMath.DilateMask(ringLines, work.Crop.Width, work.Crop.Height, supportReach);
            for (var i = 0; i < zone.Length; i++) if (ringZone[i] != 0) zone[i] = 0;

            band = new byte[work.Crop.Width * work.Crop.Height];
            foreach (var pair in pairs)
            {
                var region = SC2PairOutlineMath.RasterizePairCells(after, g.GridWidth, g.GridHeight, pair, g.MaskWidth, g.MaskHeight, work.Crop);
                var pairBand = SC2PairOutlineMath.BuildBand(region, work.Crop.Width, work.Crop.Height, current.Width * g.PixelsPerUnit, current.Softness,
                    current.Smoothing * g.CellPixels, current.Spread * g.PixelsPerUnit, MaxShiftPixels(g), MaxOutwardShiftPixels(g));
                for (var i = 0; i < band.Length; i++) if (pairBand[i] > band[i]) band[i] = pairBand[i];
            }
            // Only inside Core: the margin of the work rectangle is not cleared again, and the band there is not reliable
            // (the smoothing sees the rectangle's edge).
            for (var y = 0; y < work.Crop.Height; y++)
            for (var x = 0; x < work.Crop.Width; x++)
            {
                var mx = work.Crop.X + x; var my = work.Crop.Y + y;
                if (mx < work.Core.X || mx >= work.Core.Right || my < work.Core.Y || my >= work.Core.Bottom) band[y * work.Crop.Width + x] = 0;
            }
            if (Array.TrueForAll(band, b => b == 0)) band = null;
        }

        var changed = SC2BlendMaskLayerMath.ClearOutsideZoneInPlace(layer, g.MaskWidth, work.Crop, zone, work.Core);
        if (band != null) changed |= SC2BlendMaskLayerMath.ApplyBandInPlace(layer, g.MaskWidth, work.Crop, band, current.Blend);
        return changed;
    }

    /// <summary>The pairs, other than <paramref name="except"/>, that had a region next to the changed cells and an outline round
    /// it: those of the changed cells before the change, and the neighbours of the changed cells afterwards. A pair counts as
    /// outlined when most of the border pixels of its regions (in <paramref name="before"/>, within the work rectangle) carry
    /// outline in <paramref name="layer"/> as it is now.</summary>
    private static List<int> OutlinedPairsNextTo(byte[] layer, Geometry g, Work work, short[] before, short[] after, List<int> changedCells, int except)
    {
        var candidates = new HashSet<int>();
        foreach (var cell in changedCells)
        {
            if (before[cell * 2] >= 0) candidates.Add(before[cell * 2]);
            var x = cell % g.GridWidth;
            var z = cell / g.GridWidth;
            if (x > 0 && after[(cell - 1) * 2] >= 0) candidates.Add(after[(cell - 1) * 2]);
            if (x < g.GridWidth - 1 && after[(cell + 1) * 2] >= 0) candidates.Add(after[(cell + 1) * 2]);
            if (z > 0 && after[(cell - g.GridWidth) * 2] >= 0) candidates.Add(after[(cell - g.GridWidth) * 2]);
            if (z < g.GridHeight - 1 && after[(cell + g.GridWidth) * 2] >= 0) candidates.Add(after[(cell + g.GridWidth) * 2]);
        }
        candidates.Remove(except);

        var outlined = new List<int>();
        foreach (var pair in candidates)
        {
            // Only pairs that still have a region in the work rectangle need an outline now.
            var cellSet = new byte[g.GridWidth * g.GridHeight];
            var stillThere = false;
            for (var z = work.CropCellZ0; z < work.CropCellZ1; z++)
            for (var x = work.CropCellX0; x < work.CropCellX1; x++)
            {
                var cell = z * g.GridWidth + x;
                if (after[cell * 2] == pair) stillThere = true;
                if (before[cell * 2] == pair) cellSet[cell] = 1;
            }
            if (!stillThere) continue;

            var lines = SC2PairOutlineMath.TouchedBorderLines(before, null, cellSet, g.GridWidth, g.GridHeight,
                work.CropCellX0, work.CropCellZ0, work.CropCellX1, work.CropCellZ1, g.MaskWidth, g.MaskHeight, work.Crop);
            int total = 0, covered = 0;
            for (var y = 0; y < work.Crop.Height; y++)
            for (var x = 0; x < work.Crop.Width; x++)
            {
                if (lines[y * work.Crop.Width + x] == 0) continue;
                total++;
                if (layer[((work.Crop.Y + y) * g.MaskWidth + work.Crop.X + x) * SC2BlendMaskLayerMath.BytesPerPixel + 1] >= 128) covered++;
            }
            if (total > 0 && covered * 5 >= total * 2) outlined.Add(pair);
        }
        return outlined;
    }
}
