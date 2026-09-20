using System;

/// <summary>Pure (Unity-free) navmesh rules for costs.bdf, measured on the
/// vanilla map corpus: cross (4-neighbour) growth between blocks, the wall
/// cost ring, and the soft-wall gradient in water. Cell values: 255 = wall,
/// 1 = free, 2..254 = cost of a passable cell near a wall.
///
/// A wall in water is a "soft" wall (shore blocking), any other wall is a
/// "hard" wall (cliff, obstacle). Vanilla builds the ring from hard walls
/// only, except in the Large seafloor block (index 3) where every wall counts.</summary>
public static class SC2CostsNavmeshRules
{
    public const byte Wall = 255;

    /// <summary>Cost a passable cell gets from a wall at distance 1 / 2 in one
    /// of the four axis directions (contributions of the four directions add up).</summary>
    public const int RingNearCost = 64;
    public const int RingFarCost = 16;
    public const int MaxCost = 254;

    /// <summary>Water gradient next to walls: 1 + floor(GradientScale * Gauss(walls, GradientSigma)).</summary>
    public const float GradientSigma = 1.4f;
    public const float GradientScale = 475f;

    /// <summary>Growth of the wall area between blocks, in cross steps.</summary>
    public const int LandGrowFirst = 1;
    public const int LandGrowSecond = 3;
    public const int WaterBandGrow = 2;

    /// <summary>Grows a mask by <paramref name="steps"/> cross (4-neighbour) steps,
    /// i.e. every cell within L1 distance <paramref name="steps"/> of a set cell.</summary>
    public static bool[] DilateCross(bool[] mask, int w, int h, int steps)
    {
        var current = (bool[])mask.Clone();
        for (var step = 0; step < steps; step++)
        {
            var next = (bool[])current.Clone();
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                if (current[idx]) continue;
                if ((x > 0 && current[idx - 1]) || (x < w - 1 && current[idx + 1])
                    || (y > 0 && current[idx - w]) || (y < h - 1 && current[idx + w]))
                    next[idx] = true;
            }
            current = next;
        }
        return current;
    }

    /// <summary>Renders one block. <paramref name="ringSource"/> are the walls that
    /// produce the cost ring (null = all walls). With <paramref name="water"/> and
    /// <paramref name="gradientWalls"/> set, passable water cells also get the soft-wall
    /// gradient (the larger of ring and gradient wins).</summary>
    public static byte[] Build(bool[] walls, bool[] ringSource, bool[] water, bool[] gradientWalls, int w, int h)
    {
        ringSource ??= walls;
        var nav = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * w + x;
            if (walls[idx]) { nav[idx] = Wall; continue; }
            var sum = 0;
            sum += DirectionCost(ringSource, w, h, x, y, 1, 0);
            sum += DirectionCost(ringSource, w, h, x, y, -1, 0);
            sum += DirectionCost(ringSource, w, h, x, y, 0, 1);
            sum += DirectionCost(ringSource, w, h, x, y, 0, -1);
            nav[idx] = sum == 0 ? (byte)1 : (byte)Math.Min(sum, MaxCost);
        }

        if (water != null && gradientWalls != null)
            ApplyWaterGradient(nav, walls, water, gradientWalls, w, h);
        return nav;
    }

    private static int DirectionCost(bool[] source, int w, int h, int x, int y, int dx, int dy)
    {
        var x1 = x + dx; var y1 = y + dy;
        if (x1 < 0 || x1 >= w || y1 < 0 || y1 >= h) return 0;
        if (source[y1 * w + x1]) return RingNearCost;
        var x2 = x1 + dx; var y2 = y1 + dy;
        if (x2 < 0 || x2 >= w || y2 < 0 || y2 >= h) return 0;
        return source[y2 * w + x2] ? RingFarCost : 0;
    }

    /// <summary>Gaussian blur of the wall mask (reflected borders, radius 4 sigma),
    /// written into passable water cells as max(current, 1 + floor(scale * blur)).</summary>
    private static void ApplyWaterGradient(byte[] nav, bool[] walls, bool[] water, bool[] gradientWalls, int w, int h)
    {
        var radius = (int)(4f * GradientSigma + 0.5f);
        var kernel = new double[radius * 2 + 1];
        double total = 0;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = Math.Exp(-0.5 * i * i / (GradientSigma * GradientSigma));
            total += kernel[i + radius];
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= total;

        var rowHasWall = new bool[h];
        var any = false;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (gradientWalls[y * w + x]) { rowHasWall[y] = true; any = true; break; }
        if (!any) return;

        var horizontal = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            if (!rowHasWall[y]) continue;
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                double acc = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    if (gradientWalls[row + Reflect(x + k, w)]) acc += kernel[k + radius];
                }
                horizontal[row + x] = (float)acc;
            }
        }

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = y * w + x;
            if (walls[idx] || !water[idx]) continue;
            double acc = 0;
            for (var k = -radius; k <= radius; k++)
            {
                var yy = Reflect(y + k, h);
                if (rowHasWall[yy]) acc += kernel[k + radius] * horizontal[yy * w + x];
            }
            var value = (int)Math.Floor(1.0 + GradientScale * acc);
            if (value > MaxCost) value = MaxCost;
            if (value > nav[idx]) nav[idx] = (byte)value;
        }
    }

    // Mirror index at the borders (edge cell repeated), the usual "reflect" convention.
    private static int Reflect(int i, int n)
    {
        if (i < 0) i = -i - 1;
        if (i >= n) i = 2 * n - i - 1;
        return Math.Min(Math.Max(i, 0), n - 1);
    }

    // ------------------------------------------------------------------
    // Block derivation
    // ------------------------------------------------------------------

    /// <summary>Derived wall masks of every block, the automatic outline of each block
    /// (wall cells that exist only because of the automatic growth, before the user's
    /// "unneeded" marks) and the cells a Shore pin opened next to it.</summary>
    public sealed class Profile
    {
        public bool[][] Walls;
        public bool[][] Outline;
        public bool[] OpenedByPins;
    }

    // Opens the marked outline cells of one block. Marks on cells that are not (or no
    // longer) outline do nothing.
    private static void OpenMarked(bool[] walls, bool[] outline, byte[] mask)
    {
        if (mask == null) return;
        for (var i = 0; i < walls.Length && i < mask.Length; i++)
            if (mask[i] != 0 && outline[i]) walls[i] = false;
    }

    private static bool[] OutlineOf(bool[] walls, bool[] walls0, bool[] exclude = null)
    {
        var outline = new bool[walls.Length];
        for (var i = 0; i < walls.Length; i++)
            outline[i] = walls[i] && !walls0[i] && (exclude == null || !exclude[i]);
        return outline;
    }

    private static byte[] MaskOf(byte[][] masks, int block) =>
        masks != null && block < masks.Length ? masks[block] : null;

    /// <summary>Land-only set (3 blocks): b1 = b0 + 1, b2 = b1 + 3 cross steps.</summary>
    public static bool[][] DeriveLandOnlyWalls(bool[] walls0, int w, int h) =>
        DeriveLandOnlyProfile(walls0, null, w, h).Walls;

    /// <param name="outlineOpen">Per block (index = block, may be null): non-zero marks an
    /// automatic outline cell of that block as unneeded, i.e. passable.</param>
    public static Profile DeriveLandOnlyProfile(bool[] walls0, byte[][] outlineOpen, int w, int h)
    {
        var w1 = DilateCross(walls0, w, h, LandGrowFirst);
        var w2 = DilateCross(w1, w, h, LandGrowSecond);
        var o1 = OutlineOf(w1, walls0);
        var o2 = OutlineOf(w2, walls0);
        OpenMarked(w1, o1, MaskOf(outlineOpen, 1));
        OpenMarked(w2, o2, MaskOf(outlineOpen, 2));
        return new Profile { Walls = new[] { (bool[])walls0.Clone(), w1, w2 }, Outline = new[] { new bool[walls0.Length], o1, o2 } };
    }

    /// <summary>Playable-water set (5 blocks, order Small shoreline, Medium, Large abyss
    /// shoreline, Large seafloor, Medium surface). Land walls grow by cross steps
    /// (Medium +1, Large +4 in total); water follows the editor's own shoreline logic:
    /// b0 keeps its shoreline band, b1 blocks all water, b2/b3 grow the band by
    /// <see cref="WaterBandGrow"/>. Pinned cells of b4 (the shore-hint block) override
    /// b4's wall state and carry into b3; growth-only wall cells next to a pinned
    /// passable cell open with it.</summary>
    /// <param name="pinMask">Non-zero = the cell of b4 is pinned; null = no pins.</param>
    /// <param name="pinnedWall">Wall state of a pinned cell of b4.</param>
    public static bool[][] DeriveWaterProfileWalls(bool[] walls0, bool[] water, byte[] pinMask, bool[] pinnedWall, int w, int h) =>
        DeriveWaterProfile(walls0, water, pinMask, pinnedWall, null, w, h).Walls;

    /// <param name="openedByPins">The growth-only wall cells that were opened next to
    /// a pinned passable cell (null when nothing is pinned) — the editor tints them
    /// as shore, like the pinned cells themselves.</param>
    public static bool[][] DeriveWaterProfileWalls(bool[] walls0, bool[] water, byte[] pinMask, bool[] pinnedWall, int w, int h, out bool[] openedByPins)
    {
        var profile = DeriveWaterProfile(walls0, water, pinMask, pinnedWall, null, w, h);
        openedByPins = profile.OpenedByPins;
        return profile.Walls;
    }

    /// <param name="outlineOpen">Per block (index = block, may be null): non-zero marks an
    /// automatic outline cell of that block as unneeded, i.e. passable. b3 grows from
    /// b4's already-opened walls, then its own marks apply. b1's blocking of open water
    /// is not outline and cannot be marked.</param>
    public static Profile DeriveWaterProfile(bool[] walls0, bool[] water, byte[] pinMask, bool[] pinnedWall, byte[][] outlineOpen, int w, int h)
    {
        var openedByPins = (bool[])null;
        var n = walls0.Length;
        var hard0 = new bool[n];
        for (var i = 0; i < n; i++) hard0[i] = walls0[i] && !water[i];

        var medium = DilateCross(hard0, w, h, LandGrowFirst);
        var large = DilateCross(medium, w, h, LandGrowSecond);

        var w1 = (bool[])medium.Clone();
        for (var i = 0; i < n; i++) if (water[i]) w1[i] = true;
        var o1 = OutlineOf(w1, walls0, water);

        var w2 = (bool[])large.Clone();
        var band2 = DilateCross(walls0, w, h, WaterBandGrow);
        for (var i = 0; i < n; i++) if (water[i] && band2[i]) w2[i] = true;
        var o2 = OutlineOf(w2, walls0);

        var w4 = (bool[])medium.Clone();
        for (var i = 0; i < n; i++) if (walls0[i] && water[i]) w4[i] = true;
        if (pinMask != null && pinnedWall != null)
        {
            // A cell that is a wall in b4 only because of the +1 growth (not in b0)
            // and touches a pinned passable cell opens with it: Shore must not leave
            // a one-cell wall strip between the opened shore and the land.
            openedByPins = new bool[n];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!medium[i] || walls0[i] || i >= pinMask.Length) continue;
                if ((x > 0 && OpenPin(pinMask, pinnedWall, i - 1)) || (x < w - 1 && OpenPin(pinMask, pinnedWall, i + 1))
                    || (y > 0 && OpenPin(pinMask, pinnedWall, i - w)) || (y < h - 1 && OpenPin(pinMask, pinnedWall, i + w)))
                {
                    w4[i] = false;
                    openedByPins[i] = true;
                }
            }
            for (var i = 0; i < n && i < pinMask.Length; i++)
                if (pinMask[i] != 0) w4[i] = pinnedWall[i];
        }
        var o4 = OutlineOf(w4, walls0);
        OpenMarked(w4, o4, MaskOf(outlineOpen, 4));

        var land4 = new bool[n];
        for (var i = 0; i < n; i++) land4[i] = w4[i] && !water[i];
        var w3 = DilateCross(land4, w, h, LandGrowSecond);
        var band3 = DilateCross(w4, w, h, WaterBandGrow);
        for (var i = 0; i < n; i++) if (water[i] && band3[i]) w3[i] = true;
        var o3 = OutlineOf(w3, walls0);

        OpenMarked(w1, o1, MaskOf(outlineOpen, 1));
        OpenMarked(w2, o2, MaskOf(outlineOpen, 2));
        OpenMarked(w3, o3, MaskOf(outlineOpen, 3));

        return new Profile
        {
            Walls = new[] { (bool[])walls0.Clone(), w1, w2, w3, w4 },
            Outline = new[] { new bool[n], o1, o2, o3, o4 },
            OpenedByPins = openedByPins,
        };
    }

    private static bool OpenPin(byte[] pinMask, bool[] pinnedWall, int i) => i < pinMask.Length && pinMask[i] != 0 && !pinnedWall[i];

    /// <summary>The shore zone: cells opened by Shore pins in b4 — pinned passable cells
    /// and the growth-only wall cells that open with them (not the ones the user marked
    /// as unneeded outline). Computed from the block wall
    /// masks so the editor and the exporter agree.</summary>
    public static bool[] ShoreZone(bool[] walls0, bool[] walls1, bool[] walls4, bool[] water, byte[] pinMask, byte[] outlineOpen4 = null)
    {
        var zone = new bool[walls0.Length];
        var any = false;
        for (var i = 0; i < zone.Length; i++)
        {
            var pinnedOpen = pinMask != null && i < pinMask.Length && pinMask[i] != 0 && !walls4[i];
            // a growth cell the user marked as unneeded outline in b4 is not a shore
            var markedByUser = outlineOpen4 != null && i < outlineOpen4.Length && outlineOpen4[i] != 0;
            var growthOpen = !walls0[i] && walls1[i] && !walls4[i] && !water[i] && !markedByUser;
            if (pinnedOpen || growthOpen) { zone[i] = true; any = true; }
        }
        return any ? zone : null;
    }

    /// <summary>Renders block <paramref name="block"/> of a derived or painted wall set.
    /// <paramref name="wallsOfBlock4"/> feeds b3's gradient. Outside the 5-block profile every
    /// wall feeds the ring and there is no gradient. <paramref name="shoreZone"/> (see
    /// <see cref="ShoreZone"/>): walls inside it give no ring in b0, b1 and b2 — a shore
    /// is an entrance, so no outline on the land side of it.</summary>
    public static byte[] BuildBlock(int block, int blockCount, bool[] walls, bool[] wallsOfBlock4, bool[] water, int w, int h, bool[] shoreZone = null)
    {
        if (blockCount != 5 || water == null)
            return Build(walls, null, null, null, w, h);

        bool[] ringSource;
        if (block == 3) ringSource = walls;
        else
        {
            // b2's walls are the b4 growth grown by another LandGrowSecond steps, so its
            // outline lies that much further out than the zone itself.
            var zone = shoreZone != null && block <= 2
                ? (block == 2 ? DilateCross(shoreZone, w, h, LandGrowSecond) : shoreZone)
                : null;
            ringSource = new bool[walls.Length];
            for (var i = 0; i < walls.Length; i++)
                ringSource[i] = walls[i] && !water[i] && !(zone != null && zone[i]);
        }
        var gradientWalls = block == 3 && wallsOfBlock4 != null ? wallsOfBlock4 : walls;
        return Build(walls, ringSource, water, gradientWalls, w, h);
    }
}
