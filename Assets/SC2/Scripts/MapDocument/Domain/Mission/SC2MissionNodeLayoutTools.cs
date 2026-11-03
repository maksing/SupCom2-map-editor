using System;
using System.Collections.Generic;
using System.Linq;

namespace SC2.MapEditor.Document
{
    /// <summary>Moving several nodes at once: snapping to the grid, aligning and spreading them. Positions are graph units, as the nodes store them.</summary>
    public static class SC2MissionNodeLayoutTools
    {
        /// <summary>The size of a grid cell the nodes snap to, in graph units.</summary>
        public const float GridSize = 20f;

        public static float Snap(float value, float grid = GridSize) => grid <= 0f ? value : MathF.Round(value / grid) * grid;

        public static void SnapToGrid(IEnumerable<SC2MissionNode> nodes, float grid = GridSize)
        {
            foreach (var node in nodes)
            {
                node.X = Snap(node.X, grid);
                node.Y = Snap(node.Y, grid);
            }
        }

        /// <summary>Puts the nodes on the left edge of the leftmost one.</summary>
        public static void AlignLeft(IList<SC2MissionNode> nodes)
        {
            if (nodes.Count < 2) return;
            var x = nodes.Min(n => n.X);
            foreach (var node in nodes) node.X = x;
        }

        /// <summary>Puts the nodes on the top edge of the topmost one.</summary>
        public static void AlignTop(IList<SC2MissionNode> nodes)
        {
            if (nodes.Count < 2) return;
            var y = nodes.Min(n => n.Y);
            foreach (var node in nodes) node.Y = y;
        }

        /// <summary>Spreads the nodes evenly between the leftmost and the rightmost one, in the order they are now left to right.</summary>
        public static void DistributeHorizontally(IList<SC2MissionNode> nodes)
        {
            if (nodes.Count < 3) return;
            var ordered = nodes.OrderBy(n => n.X).ToList();
            var first = ordered[0].X;
            var step = (ordered[ordered.Count - 1].X - first) / (ordered.Count - 1);
            for (var i = 0; i < ordered.Count; i++) ordered[i].X = first + step * i;
        }

        /// <summary>Spreads the nodes evenly between the topmost and the lowest one, in the order they are now top to bottom.</summary>
        public static void DistributeVertically(IList<SC2MissionNode> nodes)
        {
            if (nodes.Count < 3) return;
            var ordered = nodes.OrderBy(n => n.Y).ToList();
            var first = ordered[0].Y;
            var step = (ordered[ordered.Count - 1].Y - first) / (ordered.Count - 1);
            for (var i = 0; i < ordered.Count; i++) ordered[i].Y = first + step * i;
        }
    }
}
