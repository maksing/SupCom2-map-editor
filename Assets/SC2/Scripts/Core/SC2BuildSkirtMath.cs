using System;

/// <summary>A building's skirt in whole height-grid vertices; both ends are included.</summary>
public readonly struct SC2SkirtRect
{
    public readonly int X0, X1, Z0, Z1;
    public SC2SkirtRect(int x0, int x1, int z0, int z1) { X0 = x0; X1 = x1; Z0 = z0; Z1 = z1; }
}

/// <summary>The outcome of the ground check for one skirt, with the numbers it was decided by.</summary>
public readonly struct SC2SkirtReport
{
    /// <summary>True for the levelled check (border of the skirt), false for the plain one (inside the skirt).</summary>
    public readonly bool Levelled;
    /// <summary>False when the skirt needs a vertex outside the height grid; the numbers below are then 0.</summary>
    public readonly bool InsideGrid;
    public readonly bool Buildable;
    /// <summary>Lowest and highest height among the vertices that were looked at.</summary>
    public readonly float MinHeight, MaxHeight;
    /// <summary>Levelled check only: the level the ground is levelled to.</summary>
    public readonly float Pivot;
    /// <summary>Levelled check: the largest distance of the border from the pivot. Plain check: the spread of the skirt.</summary>
    public readonly float Deviation;
    public readonly float Tolerance;

    public SC2SkirtReport(bool levelled, bool insideGrid, bool buildable, float minHeight, float maxHeight, float pivot, float deviation, float tolerance)
    {
        Levelled = levelled; InsideGrid = insideGrid; Buildable = buildable;
        MinHeight = minHeight; MaxHeight = maxHeight; Pivot = pivot; Deviation = deviation; Tolerance = tolerance;
    }
}

/// <summary>Which Costs block limits where a building can be built.</summary>
public enum SC2BuildCostsRule
{
    /// <summary>The building is not limited by Costs (an air or orbital building).</summary>
    None,
    /// <summary>The land block (Land_Medium; Land_medium on a map with water).</summary>
    LandMedium,
    /// <summary>The block that lets land buildings reach into shallow water (Land_Medium_Water_Surface).</summary>
    LandMediumWaterSurface,
    /// <summary>Land_Medium_Water_Surface, and only where the ground is under the water surface.</summary>
    WaterSurfaceOnly,
    /// <summary>The map has nothing this building can stand on (a water building on a map without water).</summary>
    Impossible,
}

/// <summary>A building already on the map: its centre, its footprint and the way it is turned (degrees, Unity's yaw).</summary>
public readonly struct SC2PlacedFootprint
{
    public readonly float X, Z, SizeX, SizeZ, YawDegrees;
    public SC2PlacedFootprint(float x, float z, float sizeX, float sizeZ, float yawDegrees)
    {
        X = x; Z = z; SizeX = sizeX; SizeZ = sizeZ; YawDegrees = yawDegrees;
    }
}

/// <summary>
/// Whether the ground under a building is level enough to build on, by the game's own two checks (the heightmap is uint16, a
/// vertex's height is raw / 128, one vertex is one world unit in X and Z, and only whole vertices are looked at - never the
/// squares between them or an interpolated surface):
/// - FlattenSkirt = true (nearly every building; the game levels the ground when construction starts, so only how far the edges
///   are from one common level matters): the border vertices of the skirt are compared with a pivot level, the last sampled
///   vertex rounded up, and the largest deviation from it must not exceed the building's MaxGroundVariation.
/// - FlattenSkirt = false: every vertex inside the skirt, and the whole spread of heights there, must not exceed MaxGroundVariation.
/// </summary>
public static class SC2BuildSkirtMath
{
    public const float HeightScale = 128f;
    public const float DefaultMaxGroundVariation = 1f;
    /// <summary>No shipped blueprint sets Physics.FlattenSkirt at all, so a building without it gets the game's own default, which is
    /// the levelling kind for nearly every building.</summary>
    public const bool DefaultFlattenSkirt = true;

    /// <summary>The Costs block that limits a building, from its blueprint's Physics.BuildOnLayerCaps. A map without the water blocks
    /// (the three land blocks) only has Land_Medium, so water buildings cannot be built on it at all and a land building that may
    /// also stand on the seabed is limited by the land block alone.</summary>
    public static SC2BuildCostsRule SelectCostsRule(bool hasLayerCaps, bool land, bool seabed, bool water, bool mapHasWaterBlocks)
    {
        if (!hasLayerCaps) return SC2BuildCostsRule.None;
        if (water) return mapHasWaterBlocks ? SC2BuildCostsRule.WaterSurfaceOnly : SC2BuildCostsRule.Impossible;
        if (seabed)
        {
            if (mapHasWaterBlocks) return SC2BuildCostsRule.LandMediumWaterSurface;
            return land ? SC2BuildCostsRule.LandMedium : SC2BuildCostsRule.Impossible;
        }
        return land ? SC2BuildCostsRule.LandMedium : SC2BuildCostsRule.None;
    }

    /// <summary>Cells (index z * (mapX - 1) + x, a cell being the square above and to the right of its vertex) that lie under the water:
    /// the mean height of the cell's four corners is below the water surface.</summary>
    public static byte[] WaterCells(ushort[] heights, int mapX, int mapY, float waterElevation)
    {
        var width = mapX - 1; var height = mapY - 1;
        var cells = new byte[Math.Max(0, width * height)];
        for (var z = 0; z < height; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = heights[z * mapX + x] + heights[z * mapX + x + 1] + heights[(z + 1) * mapX + x] + heights[(z + 1) * mapX + x + 1];
                if (sum / 4f / HeightScale < waterElevation) cells[z * width + x] = 1;
            }
        }
        return cells;
    }

    /// <summary>Clears every centre in <paramref name="centers"/> (a mask over the vertices) whose footprint covers a cell that is
    /// not allowed, or reaches outside the grid. <paramref name="allowedCells"/> is indexed like <see cref="WaterCells"/>. Returns a new mask.</summary>
    public static byte[] RestrictToAllowedCells(byte[] centers, int mapX, int mapY, float sizeX, float sizeZ, byte[] allowedCells)
    {
        var result = (byte[])centers.Clone();
        var width = mapX - 1; var height = mapY - 1;
        if (width <= 0 || height <= 0) return result;
        if (allowedCells == null || allowedCells.Length < width * height) return result;

        // Prefix sums of the cells that are not allowed: any rectangle of cells is then answered with four lookups.
        var stride = width + 1;
        var blocked = new int[stride * (height + 1)];
        for (var z = 0; z < height; z++)
            for (var x = 0; x < width; x++)
                blocked[(z + 1) * stride + x + 1] = blocked[z * stride + x + 1] + blocked[(z + 1) * stride + x] - blocked[z * stride + x]
                                                    + (allowedCells[z * width + x] != 0 ? 0 : 1);

        int loX = (int)Math.Floor(-sizeX * 0.5f), hiX = (int)Math.Ceiling(sizeX * 0.5f) - 1;
        int loZ = (int)Math.Floor(-sizeZ * 0.5f), hiZ = (int)Math.Ceiling(sizeZ * 0.5f) - 1;
        for (var z = 0; z < mapY; z++)
        {
            for (var x = 0; x < mapX; x++)
            {
                if (result[z * mapX + x] == 0) continue;
                int x0 = x + loX, x1 = x + hiX, z0 = z + loZ, z1 = z + hiZ;
                if (x0 < 0 || z0 < 0 || x1 >= width || z1 >= height
                    || blocked[(z1 + 1) * stride + x1 + 1] - blocked[z0 * stride + x1 + 1] - blocked[(z1 + 1) * stride + x0] + blocked[z0 * stride + x0] > 0)
                    result[z * mapX + x] = 0;
            }
        }
        return result;
    }

    /// <summary>Whether every cell a building of this footprint centred on (centerX, centerZ) covers is allowed (same cells as
    /// <see cref="RestrictToAllowedCells"/> for a centre on a vertex).</summary>
    public static bool FootprintCellsAllowed(byte[] allowedCells, int cellsWide, int cellsHigh, float centerX, float centerZ, float sizeX, float sizeZ)
    {
        if (allowedCells == null) return true;
        int x0 = (int)Math.Floor(centerX - sizeX * 0.5f), x1 = (int)Math.Ceiling(centerX + sizeX * 0.5f) - 1;
        int z0 = (int)Math.Floor(centerZ - sizeZ * 0.5f), z1 = (int)Math.Ceiling(centerZ + sizeZ * 0.5f) - 1;
        if (x0 < 0 || z0 < 0 || x1 >= cellsWide || z1 >= cellsHigh) return false;
        for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
                if (allowedCells[z * cellsWide + x] == 0) return false;
        return true;
    }

    /// <summary>The skirt of a building of the given footprint centred on (centerX, centerZ), cut down to whole vertices: the
    /// footprint is centred on the position, each end is truncated to an integer and the upper end belongs to the skirt.</summary>
    public static SC2SkirtRect SkirtAround(float centerX, float centerZ, float sizeX, float sizeZ)
    {
        var halfX = sizeX * 0.5f;
        var halfZ = sizeZ * 0.5f;
        return new SC2SkirtRect(
            (int)Math.Floor(centerX - halfX), (int)Math.Floor(centerX + halfX),
            (int)Math.Floor(centerZ - halfZ), (int)Math.Floor(centerZ + halfZ));
    }

    /// <summary>The game's check for one skirt. A skirt that needs a vertex outside the grid cannot be built on.</summary>
    public static bool CanBuild(ushort[] heights, int mapX, int mapY, SC2SkirtRect rect, bool flattenSkirt, float maxGroundVariation)
    {
        return Evaluate(heights, mapX, mapY, rect, flattenSkirt, maxGroundVariation).Buildable;
    }

    /// <summary>The same check with the numbers it was decided by, for showing why a spot is refused.</summary>
    public static SC2SkirtReport Evaluate(ushort[] heights, int mapX, int mapY, SC2SkirtRect rect, bool flattenSkirt, float maxGroundVariation)
    {
        return flattenSkirt
            ? CheckEdgeFlatness(heights, mapX, mapY, rect, maxGroundVariation)
            : CheckAreaFlatness(heights, mapX, mapY, rect, maxGroundVariation);
    }

    // OCCUPY_CheckEdgeFlatness: the border of the skirt, rows first, then the two columns; the last vertex sampled is (x1, z1), and its
    // height rounded up is the level the ground is levelled to.
    private static SC2SkirtReport CheckEdgeFlatness(ushort[] heights, int mapX, int mapY, SC2SkirtRect rect, float maxGroundVariation)
    {
        var x0 = rect.X0; var x1 = rect.X1;
        var z0 = rect.Z0; var z1 = rect.Z1;
        if (x0 < 0 || z0 < 0 || x1 >= mapX || z1 >= mapY)
            return new SC2SkirtReport(true, false, false, 0f, 0f, 0f, 0f, maxGroundVariation);

        int min = int.MaxValue, max = int.MinValue, last = 0;
        void Sample(int x, int z)
        {
            int raw = heights[z * mapX + x];
            if (raw < min) min = raw;
            if (raw > max) max = raw;
            last = raw;
        }

        for (var x = x0; x <= x1; x++) Sample(x, z0);
        for (var x = x0; x <= x1; x++) Sample(x, z1);
        for (var z = z0; z <= z1; z++)
        {
            Sample(x0, z);
            Sample(x1, z);
        }

        var pivot = (float)Math.Ceiling(last / HeightScale);
        var deviation = Math.Max(max / HeightScale - pivot, pivot - min / HeightScale);
        return new SC2SkirtReport(true, true, maxGroundVariation >= deviation, min / HeightScale, max / HeightScale, pivot, deviation, maxGroundVariation);
    }

    // OCCUPY_CheckAreaFlatness: every vertex of the skirt, no levelling.
    private static SC2SkirtReport CheckAreaFlatness(ushort[] heights, int mapX, int mapY, SC2SkirtRect rect, float maxGroundVariation)
    {
        if (rect.X0 < 0 || rect.Z0 < 0 || rect.X1 >= mapX || rect.Z1 >= mapY)
            return new SC2SkirtReport(false, false, false, 0f, 0f, 0f, 0f, maxGroundVariation);

        int min = int.MaxValue, max = int.MinValue;
        for (var z = rect.Z0; z <= rect.Z1; z++)
        {
            for (var x = rect.X0; x <= rect.X1; x++)
            {
                int raw = heights[z * mapX + x];
                if (raw < min) min = raw;
                if (raw > max) max = raw;
            }
        }
        var spread = (max - min) / HeightScale;
        return new SC2SkirtReport(false, true, spread <= maxGroundVariation, min / HeightScale, max / HeightScale, 0f, spread, maxGroundVariation);
    }

    /// <summary>Whether an axis-aligned footprint (centre, size) and a placed, possibly turned, footprint overlap. Footprints that only
    /// touch along an edge do not overlap.</summary>
    public static bool Overlaps(float centerX, float centerZ, float sizeX, float sizeZ, in SC2PlacedFootprint other)
    {
        const float Epsilon = 1e-4f;
        var halfX = sizeX * 0.5f; var halfZ = sizeZ * 0.5f;
        var otherHalfX = other.SizeX * 0.5f; var otherHalfZ = other.SizeZ * 0.5f;
        var yaw = other.YawDegrees * (float)(Math.PI / 180.0);
        var cos = (float)Math.Cos(yaw); var sin = (float)Math.Sin(yaw);
        // The other footprint's own axes in the world: Unity's yaw turns +X to (cos, -sin) and +Z to (sin, cos).
        float ax = cos, az = -sin, bx = sin, bz = cos;
        float dx = other.X - centerX, dz = other.Z - centerZ;

        // Separating axis test on the four edge directions of the two rectangles.
        if (Math.Abs(dx) >= halfX + otherHalfX * Math.Abs(ax) + otherHalfZ * Math.Abs(bx) - Epsilon) return false;
        if (Math.Abs(dz) >= halfZ + otherHalfX * Math.Abs(az) + otherHalfZ * Math.Abs(bz) - Epsilon) return false;
        var onA = Math.Abs(dx * ax + dz * az);
        if (onA >= otherHalfX + halfX * Math.Abs(ax) + halfZ * Math.Abs(az) - Epsilon) return false;
        var onB = Math.Abs(dx * bx + dz * bz);
        if (onB >= otherHalfZ + halfX * Math.Abs(bx) + halfZ * Math.Abs(bz) - Epsilon) return false;
        return true;
    }

    /// <summary>The first of the placed footprints that a building of this footprint centred on (centerX, centerZ) would overlap, or -1.</summary>
    public static int FindOverlap(float centerX, float centerZ, float sizeX, float sizeZ, SC2PlacedFootprint[] others)
    {
        if (others == null) return -1;
        for (var i = 0; i < others.Length; i++)
            if (Overlaps(centerX, centerZ, sizeX, sizeZ, others[i])) return i;
        return -1;
    }

    /// <summary>Clears, in a mask made by <see cref="BuildMask"/> for the same footprint size, every vertex where the building would
    /// overlap one of the placed footprints. Returns a new mask; the one passed in is left as it was.</summary>
    public static byte[] BlockOverlapping(byte[] mask, int mapX, int mapY, float sizeX, float sizeZ, SC2PlacedFootprint[] others)
    {
        var result = (byte[])mask.Clone();
        if (others == null) return result;
        foreach (var other in others)
        {
            // Everything this footprint can reach lies inside the bounding box of the turned rectangle grown by half our size.
            var yaw = other.YawDegrees * (float)(Math.PI / 180.0);
            var cos = Math.Abs((float)Math.Cos(yaw)); var sin = Math.Abs((float)Math.Sin(yaw));
            var reachX = sizeX * 0.5f + (other.SizeX * cos + other.SizeZ * sin) * 0.5f;
            var reachZ = sizeZ * 0.5f + (other.SizeX * sin + other.SizeZ * cos) * 0.5f;
            var xMin = Math.Max(0, (int)Math.Floor(other.X - reachX)); var xMax = Math.Min(mapX - 1, (int)Math.Ceiling(other.X + reachX));
            var zMin = Math.Max(0, (int)Math.Floor(other.Z - reachZ)); var zMax = Math.Min(mapY - 1, (int)Math.Ceiling(other.Z + reachZ));
            for (var z = zMin; z <= zMax; z++)
                for (var x = xMin; x <= xMax; x++)
                    if (result[z * mapX + x] != 0 && Overlaps(x, z, sizeX, sizeZ, other))
                        result[z * mapX + x] = 0;
        }
        return result;
    }

    /// <summary>From the mask of valid centres (see <see cref="BuildMask"/> and <see cref="BlockOverlapping"/>) to the mask of the
    /// ground such a building could stand on: for every cell (index z * mapX + x, the cell above and to the right of that vertex), 1
    /// when the footprint of at least one valid placement covers it. Next to another building this leaves exactly that building's
    /// footprint uncovered, and along a cliff only the ground no placement can reach.</summary>
    public static byte[] CoverageMask(byte[] centers, int mapX, int mapY, float sizeX, float sizeZ)
    {
        var covered = new byte[centers.Length];
        if (mapX <= 0 || mapY <= 0) return covered;

        // A footprint centred on vertex c covers the cells from floor(c - half) to ceil(c + half) - 1.
        int loX = (int)Math.Floor(-sizeX * 0.5f), hiX = (int)Math.Ceiling(sizeX * 0.5f) - 1;
        int loZ = (int)Math.Floor(-sizeZ * 0.5f), hiZ = (int)Math.Ceiling(sizeZ * 0.5f) - 1;

        // Along X: a cell x is covered when a valid centre lies in [x - hiX, x - loX], counted with a running sum per row.
        var alongX = new byte[centers.Length];
        var sums = new int[Math.Max(mapX, mapY) + 1];
        for (var z = 0; z < mapY; z++)
        {
            for (var x = 0; x < mapX; x++) sums[x + 1] = sums[x] + (centers[z * mapX + x] != 0 ? 1 : 0);
            for (var x = 0; x < mapX; x++)
            {
                var from = Math.Max(0, x - hiX); var to = Math.Min(mapX - 1, x - loX);
                if (from <= to && sums[to + 1] - sums[from] > 0) alongX[z * mapX + x] = 1;
            }
        }

        // Then the same along Z over what the first pass found.
        for (var x = 0; x < mapX; x++)
        {
            for (var z = 0; z < mapY; z++) sums[z + 1] = sums[z] + alongX[z * mapX + x];
            for (var z = 0; z < mapY; z++)
            {
                var from = Math.Max(0, z - hiZ); var to = Math.Min(mapY - 1, z - loZ);
                if (from <= to && sums[to + 1] - sums[from] > 0) covered[z * mapX + x] = 1;
            }
        }
        return covered;
    }

    /// <summary>For every vertex of the grid (index z * mapX + x): 1 when a building of the given footprint centred on that vertex
    /// can be built there, 0 when not. The same answer as <see cref="CanBuild"/> with <see cref="SkirtAround"/> for each vertex.</summary>
    public static byte[] BuildMask(ushort[] heights, int mapX, int mapY, float sizeX, float sizeZ, bool flattenSkirt, float maxGroundVariation)
    {
        var mask = new byte[Math.Max(0, mapX * mapY)];
        if (heights == null || mapX <= 0 || mapY <= 0 || heights.Length < mask.Length) return mask;

        if (flattenSkirt)
        {
            for (var z = 0; z < mapY; z++)
                for (var x = 0; x < mapX; x++)
                    if (CheckEdgeFlatness(heights, mapX, mapY, SkirtAround(x, z, sizeX, sizeZ), maxGroundVariation).Buildable)
                        mask[z * mapX + x] = 1;
            return mask;
        }

        // The skirt keeps one shape wherever it is placed, so the spread over it comes from a running min / max along X and then
        // along Z instead of a full scan of the skirt at every vertex.
        var shape = SkirtAround(0, 0, sizeX, sizeZ);
        var rowMin = new ushort[mask.Length];
        var rowMax = new ushort[mask.Length];
        for (var z = 0; z < mapY; z++)
        {
            for (var x = 0; x < mapX; x++)
            {
                if (x + shape.X0 < 0 || x + shape.X1 >= mapX) continue;
                ushort lo = ushort.MaxValue, hi = ushort.MinValue;
                for (var k = x + shape.X0; k <= x + shape.X1; k++)
                {
                    var raw = heights[z * mapX + k];
                    if (raw < lo) lo = raw;
                    if (raw > hi) hi = raw;
                }
                rowMin[z * mapX + x] = lo;
                rowMax[z * mapX + x] = hi;
            }
        }

        for (var z = 0; z < mapY; z++)
        {
            if (z + shape.Z0 < 0 || z + shape.Z1 >= mapY) continue;
            for (var x = 0; x < mapX; x++)
            {
                if (x + shape.X0 < 0 || x + shape.X1 >= mapX) continue;
                ushort lo = ushort.MaxValue, hi = ushort.MinValue;
                for (var k = z + shape.Z0; k <= z + shape.Z1; k++)
                {
                    var index = k * mapX + x;
                    if (rowMin[index] < lo) lo = rowMin[index];
                    if (rowMax[index] > hi) hi = rowMax[index];
                }
                if ((hi - lo) / HeightScale <= maxGroundVariation) mask[z * mapX + x] = 1;
            }
        }
        return mask;
    }
}
