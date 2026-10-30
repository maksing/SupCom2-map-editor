using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plans a regular grid of path nodes over a rectangle: where the nodes go and which pairs are joined. Each node is joined to
/// its neighbours at the right, below and on both lower diagonals (every pair of neighbours, 8 per node, exactly once). With a
/// passability test, only passable positions get a node, and a pair is joined only when the way between the two is passable too.
/// Pure: the caller turns the plan into markers and graph edges.
/// </summary>
public static class SC2PathGrid
{
    /// <summary>The most nodes one grid may have; a finer step is refused so a typing slip cannot freeze the editor.</summary>
    public const int MaxNodes = 20000;

    public sealed class Plan
    {
        public readonly List<Vector2> Nodes = new List<Vector2>();
        public readonly List<(int A, int B)> Edges = new List<(int A, int B)>();
        /// <summary>Grid size in nodes before the passability test removed any.</summary>
        public int Columns, Rows;
        /// <summary>True when the grid would have more than <see cref="MaxNodes"/> nodes; nothing is planned then.</summary>
        public bool TooManyNodes;
    }

    /// <summary>The grid over [x1, x2] x [z1, z2] with nodes <paramref name="step"/> apart, centred in the rectangle, on whole
    /// world units (markers sit on whole cells). <paramref name="passable"/> null means everywhere.</summary>
    public static Plan Build(float x1, float z1, float x2, float z2, float step, Func<float, float, bool> passable)
    {
        var plan = new Plan();
        step = Mathf.Max(1f, step);
        var columns = Mathf.FloorToInt((x2 - x1) / step) + 1;
        var rows = Mathf.FloorToInt((z2 - z1) / step) + 1;
        if (x2 < x1 || z2 < z1) return plan;
        plan.Columns = columns;
        plan.Rows = rows;
        if ((long)columns * rows > MaxNodes) { plan.TooManyNodes = true; return plan; }

        var startX = x1 + ((x2 - x1) - (columns - 1) * step) * 0.5f;
        var startZ = z1 + ((z2 - z1) - (rows - 1) * step) * 0.5f;
        var index = new int[columns * rows];
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var position = new Vector2(Mathf.Round(startX + column * step), Mathf.Round(startZ + row * step));
                if (passable != null && !passable(position.x, position.y)) { index[row * columns + column] = -1; continue; }
                index[row * columns + column] = plan.Nodes.Count;
                plan.Nodes.Add(position);
            }

        // Right, below, below-right and below-left: every neighbouring pair once.
        var neighbours = new[] { (1, 0), (0, 1), (1, 1), (-1, 1) };
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var a = index[row * columns + column];
                if (a < 0) continue;
                foreach (var (dc, dr) in neighbours)
                {
                    var c = column + dc; var r = row + dr;
                    if (c < 0 || c >= columns || r >= rows) continue;
                    var b = index[r * columns + c];
                    if (b < 0) continue;
                    if (passable != null && !WayIsPassable(plan.Nodes[a], plan.Nodes[b], passable)) continue;
                    plan.Edges.Add((a, b));
                }
            }
        return plan;
    }

    /// <summary>A quarter, half and three quarters of the way between two nodes (the nodes themselves are known to be passable).</summary>
    private static bool WayIsPassable(Vector2 from, Vector2 to, Func<float, float, bool> passable)
    {
        for (var i = 1; i <= 3; i++)
        {
            var point = Vector2.Lerp(from, to, i * 0.25f);
            if (!passable(point.x, point.y)) return false;
        }
        return true;
    }
}
