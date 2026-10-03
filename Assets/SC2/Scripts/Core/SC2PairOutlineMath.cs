using System;
using System.Collections.Generic;

/// <summary>
/// The band along the inside of a pair's border that "Outline border" paints into the blend mask. A pair owns whole
/// grid cells (both triangles of a cell share the pair), so its region is rasterised from the cell assignments to the
/// mask's resolution; the band is then a distance falloff from the region's border. Pure math, shared with the tests.
/// </summary>
public static class SC2PairOutlineMath
{
    /// <summary>A rectangle of mask pixels (x and y of its first pixel, then its size).</summary>
    public readonly struct MaskRect
    {
        public readonly int X, Y, Width, Height;
        public MaskRect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    /// <summary>The mask pixels that cover the grid cells <c>[cellX0, cellX1) x [cellZ0, cellZ1)</c> (clamped to the grid),
    /// rounded outward. Working on such a rectangle instead of the whole mask is what keeps an outline update after a
    /// brush stroke fast: only the neighbourhood of the stroke is looked at.</summary>
    public static MaskRect MaskRectOfCells(int cellX0, int cellZ0, int cellX1, int cellZ1, int gridWidth, int gridHeight, int width, int height)
    {
        cellX0 = Math.Max(0, Math.Min(gridWidth, cellX0)); cellX1 = Math.Max(cellX0, Math.Min(gridWidth, cellX1));
        cellZ0 = Math.Max(0, Math.Min(gridHeight, cellZ0)); cellZ1 = Math.Max(cellZ0, Math.Min(gridHeight, cellZ1));
        var x0 = (int)Math.Floor((double)cellX0 * width / gridWidth);
        var x1 = (int)Math.Ceiling((double)cellX1 * width / gridWidth);
        var y0 = (int)Math.Floor((double)cellZ0 * height / gridHeight);
        var y1 = (int)Math.Ceiling((double)cellZ1 * height / gridHeight);
        return new MaskRect(x0, y0, Math.Max(0, Math.Min(width, x1) - x0), Math.Max(0, Math.Min(height, y1) - y0));
    }

    /// <summary>One byte per mask pixel: 1 where the pixel lies in a cell assigned to <paramref name="pairIndex"/>
    /// (either of its two triangles), else 0. Mask pixel (x, y) covers the cell under its centre; x runs along world X
    /// and y along world Z, the same mapping the paint brush uses.</summary>
    public static byte[] RasterizePairCells(short[] trianglePairIndex, int gridWidth, int gridHeight, int pairIndex, int width, int height)
        => RasterizePairCells(trianglePairIndex, gridWidth, gridHeight, pairIndex, width, height, new MaskRect(0, 0, width, height));

    /// <summary>As above, for the pixels of <paramref name="rect"/> only: the result has <c>rect.Width * rect.Height</c> bytes,
    /// row by row.</summary>
    public static byte[] RasterizePairCells(short[] trianglePairIndex, int gridWidth, int gridHeight, int pairIndex, int width, int height, MaskRect rect)
    {
        if (trianglePairIndex == null) throw new ArgumentNullException(nameof(trianglePairIndex));
        if (gridWidth < 1 || gridHeight < 1 || width < 1 || height < 1) throw new ArgumentOutOfRangeException();
        if (trianglePairIndex.Length < gridWidth * gridHeight * 2) throw new ArgumentException("Triangle assignments are shorter than the grid.", nameof(trianglePairIndex));
        CheckRect(rect, width, height);

        var inside = new byte[rect.Width * rect.Height];
        var cellOfColumn = new int[rect.Width];
        for (var x = 0; x < rect.Width; x++) cellOfColumn[x] = Math.Min(gridWidth - 1, (int)((rect.X + x + 0.5f) / width * gridWidth));
        for (var y = 0; y < rect.Height; y++)
        {
            var cellZ = Math.Min(gridHeight - 1, (int)((rect.Y + y + 0.5f) / height * gridHeight));
            var rowStart = y * rect.Width;
            for (var x = 0; x < rect.Width; x++)
            {
                var triangle = (cellZ * gridWidth + cellOfColumn[x]) * 2;
                if (trianglePairIndex[triangle] == pairIndex || trianglePairIndex[triangle + 1] == pairIndex) inside[rowStart + x] = 1;
            }
        }
        return inside;
    }

    /// <summary>One byte per mask pixel: 1 where the pixel lies in one of the listed grid cells (indices
    /// <c>z * gridWidth + x</c>), else 0 — the same pixel-to-cell mapping as <see cref="RasterizePairCells(short[], int, int, int, int, int)"/>.</summary>
    public static byte[] RasterizeCells(IEnumerable<int> cells, int gridWidth, int gridHeight, int width, int height)
        => RasterizeCells(cells, gridWidth, gridHeight, width, height, new MaskRect(0, 0, width, height));

    /// <summary>As above, for the pixels of <paramref name="rect"/> only.</summary>
    public static byte[] RasterizeCells(IEnumerable<int> cells, int gridWidth, int gridHeight, int width, int height, MaskRect rect)
    {
        if (cells == null) throw new ArgumentNullException(nameof(cells));
        if (gridWidth < 1 || gridHeight < 1 || width < 1 || height < 1) throw new ArgumentOutOfRangeException();
        CheckRect(rect, width, height);
        var flags = new bool[gridWidth * gridHeight];
        foreach (var cell in cells)
            if (cell >= 0 && cell < flags.Length) flags[cell] = true;

        var mask = new byte[rect.Width * rect.Height];
        var cellOfColumn = new int[rect.Width];
        for (var x = 0; x < rect.Width; x++) cellOfColumn[x] = Math.Min(gridWidth - 1, (int)((rect.X + x + 0.5f) / width * gridWidth));
        for (var y = 0; y < rect.Height; y++)
        {
            var cellZ = Math.Min(gridHeight - 1, (int)((rect.Y + y + 0.5f) / height * gridHeight));
            var rowStart = y * rect.Width;
            for (var x = 0; x < rect.Width; x++)
                if (flags[cellZ * gridWidth + cellOfColumn[x]]) mask[rowStart + x] = 1;
        }
        return mask;
    }

    private static void CheckRect(MaskRect rect, int width, int height)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width < 0 || rect.Height < 0 || rect.Right > width || rect.Bottom > height)
            throw new ArgumentOutOfRangeException(nameof(rect), "The rectangle lies outside the mask.");
    }

    /// <summary>Indices (<c>z * gridWidth + x</c>) of the cells that have a 4-neighbour assigned to a different pair (or to no
    /// pair): the cells along every pair border on the map. The map edge is not a border.</summary>
    public static List<int> BorderCells(short[] trianglePairIndex, int gridWidth, int gridHeight)
        => BorderCells(trianglePairIndex, gridWidth, gridHeight, 0, 0, gridWidth, gridHeight);

    /// <summary>As above, looking only at the cells <c>[cellX0, cellX1) x [cellZ0, cellZ1)</c> (neighbours outside that range are
    /// still compared against).</summary>
    public static List<int> BorderCells(short[] trianglePairIndex, int gridWidth, int gridHeight, int cellX0, int cellZ0, int cellX1, int cellZ1)
    {
        if (trianglePairIndex == null) throw new ArgumentNullException(nameof(trianglePairIndex));
        if (gridWidth < 1 || gridHeight < 1) throw new ArgumentOutOfRangeException();
        if (trianglePairIndex.Length < gridWidth * gridHeight * 2) throw new ArgumentException("Triangle assignments are shorter than the grid.", nameof(trianglePairIndex));
        cellX0 = Math.Max(0, cellX0); cellZ0 = Math.Max(0, cellZ0);
        cellX1 = Math.Min(gridWidth, cellX1); cellZ1 = Math.Min(gridHeight, cellZ1);
        var cells = new List<int>();
        for (var z = cellZ0; z < cellZ1; z++)
        for (var x = cellX0; x < cellX1; x++)
        {
            var cell = z * gridWidth + x;
            var pair = trianglePairIndex[cell * 2];
            if ((x > 0 && trianglePairIndex[(cell - 1) * 2] != pair) ||
                (x < gridWidth - 1 && trianglePairIndex[(cell + 1) * 2] != pair) ||
                (z > 0 && trianglePairIndex[(cell - gridWidth) * 2] != pair) ||
                (z < gridHeight - 1 && trianglePairIndex[(cell + gridWidth) * 2] != pair))
                cells.Add(cell);
        }
        return cells;
    }

    /// <summary>The border cells of <paramref name="after"/> (see BorderCells) whose border did not just change: the cell and the
    /// neighbour it differs from have the same assignment in <paramref name="before"/>. A border next to a cell that was
    /// reassigned is new or has moved, so an outline left near it belongs to the old position. With <paramref name="before"/>
    /// null every border counts.</summary>
    public static List<int> StableBorderCells(short[] after, short[] before, int gridWidth, int gridHeight, int cellX0, int cellZ0, int cellX1, int cellZ1)
    {
        if (after == null) throw new ArgumentNullException(nameof(after));
        if (before != null && before.Length != after.Length) throw new ArgumentException("Assignments differ in size.", nameof(before));
        if (before == null) return BorderCells(after, gridWidth, gridHeight, cellX0, cellZ0, cellX1, cellZ1);
        if (gridWidth < 1 || gridHeight < 1) throw new ArgumentOutOfRangeException();
        if (after.Length < gridWidth * gridHeight * 2) throw new ArgumentException("Triangle assignments are shorter than the grid.", nameof(after));
        cellX0 = Math.Max(0, cellX0); cellZ0 = Math.Max(0, cellZ0);
        cellX1 = Math.Min(gridWidth, cellX1); cellZ1 = Math.Min(gridHeight, cellZ1);

        bool Unchanged(int c) => after[c * 2] == before[c * 2] && after[c * 2 + 1] == before[c * 2 + 1];
        bool BorderWith(int cell, int other) => after[other * 2] != after[cell * 2] && Unchanged(other);

        var cells = new List<int>();
        for (var z = cellZ0; z < cellZ1; z++)
        for (var x = cellX0; x < cellX1; x++)
        {
            var cell = z * gridWidth + x;
            if (!Unchanged(cell)) continue;
            if ((x > 0 && BorderWith(cell, cell - 1)) ||
                (x < gridWidth - 1 && BorderWith(cell, cell + 1)) ||
                (z > 0 && BorderWith(cell, cell - gridWidth)) ||
                (z < gridHeight - 1 && BorderWith(cell, cell + gridWidth)))
                cells.Add(cell);
        }
        return cells;
    }

    /// <summary>A thin line of pixels (one pixel on each side) along every pair border edge in the cell range
    /// <c>[cellX0, cellX1) x [cellZ0, cellZ1)</c> (borders next to the range included), as a mask over <paramref name="rect"/>.
    /// With <paramref name="before"/> given, an edge counts only when both its cells have the same assignment in
    /// <paramref name="before"/> (see StableBorderCells), so a border that just moved has no line. Distances measured from
    /// these lines are distances from the border itself, not from the cells next to it.</summary>
    public static byte[] StableBorderLines(short[] after, short[] before, int gridWidth, int gridHeight,
        int cellX0, int cellZ0, int cellX1, int cellZ1, int width, int height, MaskRect rect)
    {
        if (after == null) throw new ArgumentNullException(nameof(after));
        if (before != null && before.Length != after.Length) throw new ArgumentException("Assignments differ in size.", nameof(before));
        bool Unchanged(int c) => before == null || (after[c * 2] == before[c * 2] && after[c * 2 + 1] == before[c * 2 + 1]);
        return BorderLines(after, gridWidth, gridHeight, cellX0, cellZ0, cellX1, cellZ1, width, height, rect, (c, n) => Unchanged(c) && Unchanged(n));
    }

    /// <summary>The border lines (see StableBorderLines) of the edges that touch a cell of <paramref name="cellSet"/> (non-zero
    /// per cell, <c>z * gridWidth + x</c>), in the state <paramref name="after"/> and, when given, also in the state
    /// <paramref name="before"/>: where the borders of those cells are now and where they were.</summary>
    public static byte[] TouchedBorderLines(short[] after, short[] before, byte[] cellSet, int gridWidth, int gridHeight,
        int cellX0, int cellZ0, int cellX1, int cellZ1, int width, int height, MaskRect rect)
    {
        if (cellSet == null || cellSet.Length != gridWidth * gridHeight) throw new ArgumentException("Cell set does not match the grid.", nameof(cellSet));
        bool Touches(int c, int n) => cellSet[c] != 0 || cellSet[n] != 0;
        var lines = BorderLines(after, gridWidth, gridHeight, cellX0, cellZ0, cellX1, cellZ1, width, height, rect, Touches);
        if (before == null) return lines;
        var old = BorderLines(before, gridWidth, gridHeight, cellX0, cellZ0, cellX1, cellZ1, width, height, rect, Touches);
        for (var i = 0; i < lines.Length; i++) if (old[i] != 0) lines[i] = 1;
        return lines;
    }

    private static byte[] BorderLines(short[] assignments, int gridWidth, int gridHeight, int cellX0, int cellZ0, int cellX1, int cellZ1,
        int width, int height, MaskRect rect, Func<int, int, bool> includeEdge)
    {
        if (assignments == null) throw new ArgumentNullException(nameof(assignments));
        if (assignments.Length < gridWidth * gridHeight * 2) throw new ArgumentException("Triangle assignments are shorter than the grid.", nameof(assignments));
        CheckRect(rect, width, height);
        var mask = new byte[rect.Width * rect.Height];
        cellX0 = Math.Max(0, cellX0 - 1); cellZ0 = Math.Max(0, cellZ0 - 1);
        cellX1 = Math.Min(gridWidth, cellX1); cellZ1 = Math.Min(gridHeight, cellZ1);

        // The first mask pixel whose centre lies in cell index i of a mask with `pixels` pixels over `cells` cells.
        int FirstPixel(int i, int pixels, int cells) => (int)Math.Ceiling((double)i * pixels / cells - 0.5);
        void Mark(int px, int py)
        {
            if (px < rect.X || px >= rect.Right || py < rect.Y || py >= rect.Bottom) return;
            mask[(py - rect.Y) * rect.Width + (px - rect.X)] = 1;
        }

        for (var z = cellZ0; z < cellZ1; z++)
        for (var x = cellX0; x < cellX1; x++)
        {
            var cell = z * gridWidth + x;
            if (x + 1 < gridWidth && assignments[(cell + 1) * 2] != assignments[cell * 2] && includeEdge(cell, cell + 1))
            {
                var edge = FirstPixel(x + 1, width, gridWidth);   // first pixel right of the edge
                for (var py = FirstPixel(z, height, gridHeight); py < FirstPixel(z + 1, height, gridHeight); py++) { Mark(edge - 1, py); Mark(edge, py); }
            }
            if (z + 1 < gridHeight && assignments[(cell + gridWidth) * 2] != assignments[cell * 2] && includeEdge(cell, cell + gridWidth))
            {
                var edge = FirstPixel(z + 1, height, gridHeight);
                for (var px = FirstPixel(x, width, gridWidth); px < FirstPixel(x + 1, width, gridWidth); px++) { Mark(px, edge - 1); Mark(px, edge); }
            }
        }
        return mask;
    }

    /// <summary>The pixels within <paramref name="reachPixels"/> of any pixel of <paramref name="mask"/> (those pixels
    /// included): 1 there, else 0.</summary>
    public static byte[] DilateMask(byte[] mask, int width, int height, float reachPixels)
    {
        if (mask == null || mask.Length != width * height) throw new ArgumentException("Mask size does not match.", nameof(mask));
        var dilated = new byte[mask.Length];
        var inverted = new byte[mask.Length];
        for (var i = 0; i < mask.Length; i++) inverted[i] = mask[i] == 0 ? (byte)1 : (byte)0;
        // Distance from every pixel to the nearest mask pixel: the inside-distance of the inverted mask.
        var distance = DistanceToOutside(inverted, width, height);
        for (var i = 0; i < dilated.Length; i++)
            if (mask[i] != 0 || distance[i] <= reachPixels) dilated[i] = 1;
        return dilated;
    }

    /// <summary>Coverage 0..255 of the band: 255 right at the region's border, falling to 0 at
    /// <paramref name="widthPixels"/> pixels inside it. The map edge is not a border (a region running off the map is not
    /// outlined there). <paramref name="softness"/> 0 gives a hard-edged band at full strength, 1 a linear fade over the
    /// whole width. A region with no border at all (it covers the whole map) gets an empty band.
    ///
    /// <paramref name="spreadPixels"/> above 0 continues the band OUTSIDE the region, onto the neighbouring pairs: pixels up
    /// to that far from the region get the same fade, measured from the border outward, so the transition straddles the
    /// border instead of stopping at it.
    ///
    /// A pair's region follows whole grid cells, so its border is a staircase and a distance measured from it gives a stepped,
    /// blocky band. <paramref name="smoothingPixels"/> above 0 first rounds the region off: the band is measured from the
    /// smoothed outline of the region (the region blurred over that many pixels, kept where at least half remains) instead of
    /// from the staircase, so it runs along a smooth curve. The band is one function of the position, the same on both sides
    /// of the real border, so the pairs meet at the same blend value there and the border between them does not show.
    /// <paramref name="maxShiftPixels"/> limits how far the smoothed outline may move away from the real border (a strong
    /// smoothing would otherwise cut through the corners of the staircase and leave the band floating beside the border): it
    /// stays within that distance of it, and <paramref name="maxOutwardShiftPixels"/> limits the same towards the outside
    /// (kept small, so that smoothing does not bridge a one-cell gap between two parts of a region and swallow the border
    /// along it). Where the smoothed outline and the real border part, the band keeps its full strength as far from the smooth
    /// outline as the corners of the staircase stand out of it, so the whole staircase is under the band and the band's edges
    /// are smooth; where they do not part (flat borders) the band is just Width and Spread, so smoothing adds no thickness.
    /// A separate part of the region that is too small to be smoothed (a single cell)
    /// keeps its own shape and gets an ordinary band, not one flooded by that margin.</summary>
    public static byte[] BuildBand(byte[] inside, int width, int height, float widthPixels, float softness, float smoothingPixels = 0f, float spreadPixels = 0f,
        float maxShiftPixels = float.MaxValue, float maxOutwardShiftPixels = float.MaxValue)
    {
        if (inside == null) throw new ArgumentNullException(nameof(inside));
        if (inside.Length != width * height) throw new ArgumentException("Region size does not match the mask size.", nameof(inside));
        if (widthPixels <= 0f && spreadPixels <= 0f) return new byte[inside.Length];
        if (smoothingPixels < 0.5f) return DistanceBand(inside, width, height, widthPixels, softness, spreadPixels);

        var smoothed = SmoothedRegion(inside, width, height, smoothingPixels, maxShiftPixels, maxOutwardShiftPixels, out var kept);
        var main = (byte[])smoothed.Clone();
        var anyKept = false;
        for (var i = 0; i < main.Length; i++) if (kept[i] != 0) { main[i] = 0; anyKept = true; }
        AddTips(main, inside, kept, width, height, Math.Min(smoothingPixels * 0.5f, maxShiftPixels));
        // How far the corners of the staircase stand out of the smooth outline, around each of them: the band keeps its full
        // strength that far from the smooth outline, so every corner is under it and the outer edge of the band is the smooth
        // outline moved by that much. Along a flat border (no staircase) the margin is nothing, so smoothing adds no thickness there.
        var margin = StaircaseMargin(inside, main, kept, width, height, maxShiftPixels);
        var band = DistanceBand(main, width, height, widthPixels, softness, spreadPixels, margin);
        if (anyKept)
        {
            var own = DistanceBand(kept, width, height, widthPixels, softness, spreadPixels);
            for (var i = 0; i < band.Length; i++) if (own[i] > band[i]) band[i] = own[i];
        }
        return band;
    }

    /// <summary>For every pixel: how far the staircase of <paramref name="region"/> stands out of the smoothed
    /// <paramref name="shape"/> nearby (0 where the border follows it). It is the distance, for the pixels of the region outside
    /// the shape and the pixels of the shape outside the region, to the other one; the largest such distance within reach of
    /// each pixel, smoothed so that the margin varies gently along the border.</summary>
    private static float[] StaircaseMargin(byte[] region, byte[] shape, byte[] kept, int width, int height, float maxShiftPixels)
    {
        var distanceToShape = OutsideDistance(shape, width, height);    // outside the shape: distance to it
        var distanceToRegion = OutsideDistance(region, width, height);  // outside the region: distance to it
        var standing = new float[region.Length];
        var largest = 0f;
        const float far = float.MaxValue * 0.25f;
        for (var i = 0; i < standing.Length; i++)
        {
            if (kept[i] != 0) continue;
            float v = 0f;
            if (region[i] != 0 && shape[i] == 0) v = distanceToShape[i];
            else if (region[i] == 0 && shape[i] != 0) v = distanceToRegion[i];
            if (v >= far) continue;
            standing[i] = v;
            if (v > largest) largest = v;
        }
        if (largest <= 0f) return standing;

        // The margin of a corner reaches along the border as far as the neighbouring corners (a few steps), then the blur
        // rounds the transition. The window is wider than the blur, so the margin at a corner is its own full value.
        var window = (int)Math.Min(24f, Math.Ceiling(Math.Min(largest, maxShiftPixels) * 3f) + 2f);
        var spread = MaxFilter(standing, width, height, window);
        return Blur(spread, width, height, Math.Max(1, window / 2 - 1), 2);
    }

    /// <summary>Largest value within <paramref name="radius"/> pixels (square window), separably.</summary>
    private static float[] MaxFilter(float[] source, int width, int height, int radius)
    {
        var a = new float[source.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var best = 0f;
                var from = Math.Max(0, x - radius); var to = Math.Min(width - 1, x + radius);
                for (var k = from; k <= to; k++) if (source[row + k] > best) best = source[row + k];
                a[row + x] = best;
            }
        }
        var b = new float[source.Length];
        for (var x = 0; x < width; x++)
        for (var y = 0; y < height; y++)
        {
            var best = 0f;
            var from = Math.Max(0, y - radius); var to = Math.Min(height - 1, y + radius);
            for (var k = from; k <= to; k++) if (a[k * width + x] > best) best = a[k * width + x];
            b[y * width + x] = best;
        }
        return b;
    }

    /// <summary>Adds to <paramref name="smoothed"/> the tips the smoothing cut off: the pixels of <paramref name="region"/> that lie
    /// further from the smoothed shape than the strip covers (a bump of a cell, the end of a diagonal band), with
    /// a short skirt of region pixels towards the shape. The band is then measured from the shape together with its tips, so
    /// the ring goes round them too, instead of leaving them standing outside it.</summary>
    private static void AddTips(byte[] smoothed, byte[] region, byte[] kept, int width, int height, float reach)
    {
        var distanceToShape = OutsideDistance(smoothed, width, height);
        var tips = new byte[region.Length];
        var any = false;
        for (var i = 0; i < tips.Length; i++)
            if (region[i] != 0 && kept[i] == 0 && smoothed[i] == 0 && distanceToShape[i] > reach && distanceToShape[i] < float.MaxValue * 0.25f)
            { tips[i] = 1; any = true; }
        if (!any) return;
        var near = DilateMask(tips, width, height, reach + 1f);
        for (var i = 0; i < smoothed.Length; i++)
            if (near[i] != 0 && region[i] != 0 && kept[i] == 0) smoothed[i] = 1;
    }

    /// <summary>For every pixel outside <paramref name="region"/>: its distance to the region (the inside-distance of the inverted mask).</summary>
    private static float[] OutsideDistance(byte[] region, int width, int height)
    {
        var inverted = new byte[region.Length];
        for (var i = 0; i < inverted.Length; i++) inverted[i] = region[i] == 0 ? (byte)1 : (byte)0;
        return DistanceToOutside(inverted, width, height);
    }

    /// <summary>The band measured from the border of <paramref name="region"/>: full strength next to it, fading over Width
    /// inside and Spread outside. With a <paramref name="margin"/> (per pixel) the strength stays full that much further from
    /// the border before the fade starts.</summary>
    private static byte[] DistanceBand(byte[] region, int width, int height, float widthPixels, float softness, float spreadPixels, float[] margin = null)
    {
        var band = new byte[region.Length];
        var hardUntil = 1f - Math.Max(0f, Math.Min(1f, softness));
        float Falloff(float distance, float span)
        {
            // The pixels next to the border (including its corners), on either side, get exactly the full strength.
            if (distance <= 1.5f) return 1f;
            // The first pixel next to the border is half a pixel from it.
            var t = Math.Max(0f, distance - 0.5f) / span;
            if (t >= 1f) return 0f;
            return t <= hardUntil ? 1f : (1f - t) / Math.Max(1e-4f, 1f - hardUntil);
        }

        if (widthPixels > 0f)
        {
            var distance = DistanceToOutside(region, width, height);
            for (var i = 0; i < band.Length; i++)
                if (region[i] != 0 && distance[i] < float.MaxValue * 0.25f) band[i] = ToByte(Falloff(margin == null ? distance[i] : Math.Max(0f, distance[i] - margin[i]), widthPixels));
        }
        if (spreadPixels > 0f)
        {
            var outside = new byte[region.Length];
            for (var i = 0; i < outside.Length; i++) outside[i] = region[i] == 0 ? (byte)1 : (byte)0;
            // The outside pixels' distance to the region: the inside-distance of the inverted mask.
            var distanceOut = DistanceToOutside(outside, width, height);
            for (var i = 0; i < band.Length; i++)
                if (outside[i] != 0 && distanceOut[i] < float.MaxValue * 0.25f) band[i] = ToByte(Falloff(margin == null ? distanceOut[i] : Math.Max(0f, distanceOut[i] - margin[i]), spreadPixels));
        }
        return band;
    }

    /// <summary>The region rounded off: blurred over <paramref name="radiusPixels"/> and kept where at least half remains, but
    /// never more than <paramref name="maxShiftPixels"/> away from the real region (not further out than that, and everything
    /// deeper inside than that stays in). A separate part of the region that the blur removed altogether (a single cell, a
    /// thin strip) stays as it is, so every part keeps an outline; those parts are marked in <paramref name="kept"/>.</summary>
    private static byte[] SmoothedRegion(byte[] region, int width, int height, float radiusPixels, float maxShiftPixels, float maxOutwardShiftPixels, out byte[] kept)
    {
        var blurred = Blur(ToFloat(region), width, height, (int)Math.Round(radiusPixels), 2);
        var smoothed = new byte[region.Length];
        for (var i = 0; i < smoothed.Length; i++) smoothed[i] = blurred[i] >= 0.5f ? (byte)1 : (byte)0;
        var result = smoothed;
        if (maxShiftPixels < float.MaxValue * 0.25f || maxOutwardShiftPixels < float.MaxValue * 0.25f)
        {
            var notRegion = new byte[region.Length];
            for (var i = 0; i < region.Length; i++) notRegion[i] = region[i] == 0 ? (byte)1 : (byte)0;
            var depthInRegion = DistanceToOutside(region, width, height);        // inside pixels: distance to the outside
            var distanceToRegion = DistanceToOutside(notRegion, width, height);  // outside pixels: distance to the region
            const float far = float.MaxValue * 0.25f;
            result = new byte[region.Length];
            for (var i = 0; i < result.Length; i++)
            {
                var nearRegion = region[i] != 0 || distanceToRegion[i] <= maxOutwardShiftPixels;
                var deepInside = region[i] != 0 && depthInRegion[i] > maxShiftPixels && depthInRegion[i] < far;
                result[i] = (smoothed[i] != 0 && nearRegion) || deepInside ? (byte)1 : (byte)0;
            }
        }

        // Every connected part of the region keeps at least its own shape (reported in `kept`).
        kept = new byte[region.Length];
        var visited = new bool[region.Length];
        var stack = new Stack<int>();
        var part = new List<int>();
        for (var start = 0; start < region.Length; start++)
        {
            if (region[start] == 0 || visited[start]) continue;
            part.Clear();
            var any = false;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                part.Add(i);
                if (result[i] != 0) any = true;
                var x = i % width;
                var y = i / width;
                if (x > 0 && region[i - 1] != 0 && !visited[i - 1]) { visited[i - 1] = true; stack.Push(i - 1); }
                if (x < width - 1 && region[i + 1] != 0 && !visited[i + 1]) { visited[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && region[i - width] != 0 && !visited[i - width]) { visited[i - width] = true; stack.Push(i - width); }
                if (y < height - 1 && region[i + width] != 0 && !visited[i + width]) { visited[i + width] = true; stack.Push(i + width); }
            }
            if (!any) foreach (var i in part) { result[i] = 1; kept[i] = 1; }
        }
        return result;
    }

    private static byte ToByte(float coverage) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(coverage * 255f)));

    private static float[] ToFloat(byte[] mask)
    {
        var result = new float[mask.Length];
        for (var i = 0; i < mask.Length; i++) result[i] = mask[i] != 0 ? 1f : 0f;
        return result;
    }

    /// <summary>Box blur of a width x height image, repeated <paramref name="passes"/> times (two passes approximate a
    /// Gaussian). The edges are clamped, so the map edge does not darken or lighten anything.</summary>
    private static float[] Blur(float[] source, int width, int height, int radius, int passes)
    {
        var a = (float[])source.Clone();
        var b = new float[a.Length];
        var window = radius * 2 + 1;
        for (var pass = 0; pass < passes; pass++)
        {
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                float sum = 0f;
                for (var k = -radius; k <= radius; k++) sum += a[row + Math.Max(0, Math.Min(width - 1, k))];
                for (var x = 0; x < width; x++)
                {
                    b[row + x] = sum / window;
                    sum += a[row + Math.Min(width - 1, x + radius + 1)] - a[row + Math.Max(0, x - radius)];
                }
            }
            for (var x = 0; x < width; x++)
            {
                float sum = 0f;
                for (var k = -radius; k <= radius; k++) sum += b[Math.Max(0, Math.Min(height - 1, k)) * width + x];
                for (var y = 0; y < height; y++)
                {
                    a[y * width + x] = sum / window;
                    sum += b[Math.Min(height - 1, y + radius + 1) * width + x] - b[Math.Max(0, y - radius) * width + x];
                }
            }
        }
        return a;
    }

    /// <summary>Distance in pixels from every inside pixel to the nearest outside pixel (two-pass chamfer, the same
    /// approximation the water depth texture uses). Outside pixels are 0; with no outside pixel everything stays at the
    /// "infinite" value.</summary>
    private static float[] DistanceToOutside(byte[] inside, int width, int height)
    {
        const float straight = 1f;
        const float diagonal = 1.41421356f;
        const float infinite = float.MaxValue * 0.25f;
        var dist = new float[inside.Length];
        for (var i = 0; i < dist.Length; i++) dist[i] = inside[i] != 0 ? infinite : 0f;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            if (inside[i] == 0) continue;
            var best = dist[i];
            if (x > 0) best = Math.Min(best, dist[i - 1] + straight);
            if (y > 0) best = Math.Min(best, dist[i - width] + straight);
            if (x > 0 && y > 0) best = Math.Min(best, dist[i - width - 1] + diagonal);
            if (x < width - 1 && y > 0) best = Math.Min(best, dist[i - width + 1] + diagonal);
            dist[i] = best;
        }
        for (var y = height - 1; y >= 0; y--)
        for (var x = width - 1; x >= 0; x--)
        {
            var i = y * width + x;
            if (inside[i] == 0) continue;
            var best = dist[i];
            if (x < width - 1) best = Math.Min(best, dist[i + 1] + straight);
            if (y < height - 1) best = Math.Min(best, dist[i + width] + straight);
            if (x < width - 1 && y < height - 1) best = Math.Min(best, dist[i + width + 1] + diagonal);
            if (x > 0 && y < height - 1) best = Math.Min(best, dist[i + width - 1] + diagonal);
            dist[i] = best;
        }
        return dist;
    }
}
