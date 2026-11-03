using System;
using System.Collections.Generic;
using System.Linq;

namespace SC2.MapEditor.Document
{
    /// <summary>Deterministic layout for a newly-authored graph. It never changes ids, wires or parameters.</summary>
    public static class SC2MissionGraphLayout
    {
        public static void AutoLayout(SC2MissionStage stage, float columnSpacing = 260f, float rowSpacing = 120f)
        {
            if (stage == null || stage.Nodes.Count == 0) return;
            var depth = new Dictionary<int, int>();
            var queue = new Queue<int>();
            queue.Enqueue(SC2MissionStage.StartNodeId);
            depth[SC2MissionStage.StartNodeId] = 0;
            while (queue.Count > 0)
            {
                var from = queue.Dequeue();
                var nextDepth = depth[from] + 1;
                foreach (var edge in stage.Edges.Where(e => e.FromNode == from).OrderBy(e => e.ToNode).ThenBy(e => e.ToPort, StringComparer.Ordinal))
                    if (!depth.ContainsKey(edge.ToNode)) { depth[edge.ToNode] = nextDepth; queue.Enqueue(edge.ToNode); }
            }

            var disconnected = depth.Values.DefaultIfEmpty(0).Max() + 1;
            foreach (var node in stage.Nodes.OrderBy(n => n.Id))
                if (!depth.ContainsKey(node.Id)) depth[node.Id] = disconnected;

            foreach (var group in stage.Nodes.GroupBy(n => depth[n.Id]).OrderBy(g => g.Key))
                foreach (var pair in group.OrderBy(n => n.Id).Select((node, index) => (node, index)))
                {
                    pair.node.X = group.Key * columnSpacing;
                    pair.node.Y = pair.index * rowSpacing;
                }
        }
    }
}
