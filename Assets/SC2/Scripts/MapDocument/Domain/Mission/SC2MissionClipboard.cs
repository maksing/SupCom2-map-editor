using System.Collections.Generic;
using System.Globalization;

namespace SC2.MapEditor.Document
{
    /// <summary>A copy of some nodes of a stage and the wires among them.</summary>
    public sealed class SC2MissionClip
    {
        public readonly List<SC2MissionNode> Nodes = new();
        /// <summary>The wires whose two ends are both in the copy; a wire that leaves the copied nodes, or starts at the stage start, is not copied.</summary>
        public readonly List<SC2MissionEdge> Edges = new();

        public bool IsEmpty => Nodes.Count == 0;
    }

    /// <summary>Copy and paste of nodes inside the mission graph (also between stages). Pure data, so it can be tested without the panel.</summary>
    public static class SC2MissionClipboard
    {
        /// <summary>The parameters that hold the number of another node of the same stage; they follow the copy when that node was copied too.</summary>
        private static readonly string[] NodeReferenceParams = { "loop", "node" };

        public static SC2MissionClip Copy(SC2MissionStage stage, IEnumerable<int> ids)
        {
            var clip = new SC2MissionClip();
            if (stage == null || ids == null) return clip;
            var wanted = new HashSet<int>(ids);
            foreach (var node in stage.Nodes)
                if (wanted.Contains(node.Id)) clip.Nodes.Add(node.Clone());
            foreach (var edge in stage.Edges)
                if (wanted.Contains(edge.FromNode) && wanted.Contains(edge.ToNode)) clip.Edges.Add(edge.Clone());
            return clip;
        }

        /// <summary>Adds the copy to the stage with new ids, its top-left corner at (x, y), and returns the new nodes. The wires among the copied
        /// nodes are made again between the new ones.</summary>
        public static List<SC2MissionNode> Paste(SC2MissionStage stage, SC2MissionClip clip, float x, float y)
        {
            var created = new List<SC2MissionNode>();
            if (stage == null || clip == null || clip.IsEmpty) return created;

            var minX = float.MaxValue;
            var minY = float.MaxValue;
            foreach (var node in clip.Nodes)
            {
                if (node.X < minX) minX = node.X;
                if (node.Y < minY) minY = node.Y;
            }

            var map = new Dictionary<int, int>();
            foreach (var node in clip.Nodes)
            {
                var copy = node.Clone();
                copy.Id = stage.NextNodeId++;
                copy.X = x + (node.X - minX);
                copy.Y = y + (node.Y - minY);
                map[node.Id] = copy.Id;
                stage.Nodes.Add(copy);
                created.Add(copy);
            }

            foreach (var copy in created)
                foreach (var key in NodeReferenceParams)
                {
                    var value = copy.GetParam(key);
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var old) && map.TryGetValue(old, out var now))
                        copy.SetParam(key, now.ToString(CultureInfo.InvariantCulture));
                }

            foreach (var edge in clip.Edges)
                if (map.TryGetValue(edge.FromNode, out var from) && map.TryGetValue(edge.ToNode, out var to))
                    stage.Connect(from, edge.FromPort, to, edge.ToPort);
            return created;
        }
    }
}
