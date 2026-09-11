// SC2IslandMap.cs
// Generates the islandmap and islandList for costs.bdf.
//
// Islandmap — per CostsBdf.cs's own verified format comment on
// CostsBlock.Islandmap ("8-bit, navmesh connected components, indices 0..254" /
// "8-bit grayscale, size = navmesh.Length = islandmap.Length"): ONE BYTE
// PER CELL. 255 = impassable (SAME sentinel value navmesh uses for
// impassable — confirmed against the real vanilla costs.bdf), 0..254 =
// distinct connected component index (cycling if there are more than 255
// islands). This function used to return a uint32-per-cell RGBA "island
// color" array instead (4x the byte size the actual costs.bdf format needs)
// — every caller then Buffer.BlockCopy'd that straight into
// CostsBlock.Islandmap, so the exported costs.bdf's Islandmap section was 4x
// the documented size and held colors, not the 8-bit grayscale index the
// format actually specifies. An intermediate version of this also used 0 for
// impassable instead of 255 — wrong per the real format, and inconsistent
// with navmesh's own impassable encoding. IslandColors below is now
// debug-visualization-only (see ColorForIslandIndex) — it never goes into
// the serialized costs.bdf.
//
// IslandList: AABB of each island (minX, minY, maxX, maxY) x uint32

using System.Collections.Generic;
using UnityEngine;

public static class SC2IslandMap
{
    // Debug-visualization-only palette (as in the original SC2 tooling) —
    // used by SC2CostsDebugImageWriter/ColorForIslandIndex to colorize the
    // 8-bit index map for human inspection. NOT part of the serialized
    // costs.bdf data anymore.
    private static readonly uint[] IslandColors =
    {
        0xFF0000FF, 0x00FF00FF, 0x0000FFFF, 0xFFFF00FF, 0xFF00FFFF, 0x00FFFFFF,
        0xFF8000FF, 0x8000FFFF, 0x00FF80FF, 0xFF0080FF, 0x80FF00FF, 0x0080FFFF,
        0xFFFFFFFF, 0xFF808080, 0x800000FF, 0x008000FF, 0x000080FF, 0x808000FF,
        0x800080FF, 0x008080FF, 0xFF4040FF, 0x40FF40FF, 0x4040FFFF, 0xFFFF40FF,
        0xFF40FFFF, 0x40FFFFFF, 0xC0C000FF, 0x00C0C0FF, 0xC000C0FF, 0xC08040FF,
        0x40C080FF, 0x8040C0FF,
    };

    /// <summary>Debug-only: maps an 8-bit island index (as stored in
    /// costs.bdf) back to a distinguishing color for visualization, e.g. the
    /// islandmap debug PNG. 255 (impassable) is white, matching navmesh's own
    /// impassable-is-white debug convention.</summary>
    public static Color32 ColorForIslandIndex(byte index)
    {
        if (index == 255) return new Color32(255, 255, 255, 255);
        var packed = IslandColors[index % IslandColors.Length];
        return new Color32((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), 255);
    }

    public static (byte[] Islandmap, uint[] IslandList) Compute(byte[] navmesh, int w, int h)
    {
        var islandmap = new byte[w * h];
        var visited   = new bool[w * h];
        var islandList = new List<uint>(); // minX,minY,maxX,maxY per island

        var islandCount = 0;

        for (var startY = 0; startY < h; startY++)
        for (var startX = 0; startX < w; startX++)
        {
            var si = startY * w + startX;
            if (visited[si] || navmesh[si] == 255) continue;

            // BFS flood fill. 8-bit index, 0..254 (255 is reserved for
            // impassable, same sentinel navmesh uses) — cycles if there are
            // more than 255 islands.
            var id = (byte)(islandCount % 255);
            islandCount++;

            uint minX = (uint)startX, minY = (uint)startY;
            uint maxX = (uint)startX, maxY = (uint)startY;

            var queue = new Queue<int>();
            queue.Enqueue(si);
            visited[si] = true;

            while (queue.Count > 0)
            {
                var idx = queue.Dequeue();
                var y = idx / w; var x = idx % w;
                islandmap[idx] = id;

                if (x < minX) minX = (uint)x;
                if (x > maxX) maxX = (uint)x;
                if (y < minY) minY = (uint)y;
                if (y > maxY) maxY = (uint)y;

                // 4-connected neighbours
                Enqueue(x-1, y, w, h, navmesh, visited, queue);
                Enqueue(x+1, y, w, h, navmesh, visited, queue);
                Enqueue(x, y-1, w, h, navmesh, visited, queue);
                Enqueue(x, y+1, w, h, navmesh, visited, queue);
            }

            islandList.Add(minX);
            islandList.Add(minY);
            islandList.Add(maxX);
            islandList.Add(maxY);
        }

        // Mark impassable cells as 255 — the same sentinel navmesh uses.
        for (var i = 0; i < navmesh.Length; i++)
            if (navmesh[i] == 255) islandmap[i] = 255;

        return (islandmap, islandList.ToArray());
    }

    private static void Enqueue(int x, int y, int w, int h, byte[] nav, bool[] vis, Queue<int> q)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        var i = y * w + x;
        if (vis[i] || nav[i] == 255) return;
        vis[i] = true;
        q.Enqueue(i);
    }
}
