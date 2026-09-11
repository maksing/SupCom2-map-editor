using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Shared marker identity rules used at the two boundaries where duplicate
/// markers can enter the working model: source import and viewport placement.
/// Imported marker tables are keyed by name, while placement also needs a
/// spatial/type check because symmetry slots can collapse after grid snapping.
/// </summary>
public static class SC2MarkerDeduplication
{
    public const float PositionEpsilon = 0.001f;

    public static bool SameName(string a, string b) =>
        !string.IsNullOrEmpty(a) &&
        !string.IsNullOrEmpty(b) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static bool SamePosition(Vector3 a, Vector3 b, float epsilon = PositionEpsilon) =>
        (a - b).sqrMagnitude <= epsilon * epsilon;

    public static bool IsArmyStartMarker(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("ARMY_", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.Length == 5) return false;
        for (var i = 5; i < name.Length; i++)
            if (name[i] < '0' || name[i] > '9') return false;
        return true;
    }

    public static bool SamePlacementKind(SC2ObjMarker a, SC2ObjMarker b)
    {
        if (a == null || b == null) return false;
        return string.Equals(a.Type ?? "", b.Type ?? "", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Prop ?? "", b.Prop ?? "", StringComparison.OrdinalIgnoreCase) &&
               IsArmyStartMarker(a.Name) == IsArmyStartMarker(b.Name);
    }

    public static bool SamePlacement(SC2ObjMarker a, SC2ObjMarker b, float epsilon = PositionEpsilon) =>
        SamePlacementKind(a, b) && SamePosition(a.Position, b.Position, epsilon);

    public static List<Vector3> DistinctPositions(IEnumerable<Vector3> positions, float epsilon = PositionEpsilon)
    {
        var result = new List<Vector3>();
        if (positions == null) return result;

        foreach (var position in positions)
        {
            var duplicate = false;
            foreach (var existing in result)
            {
                if (!SamePosition(existing, position, epsilon)) continue;
                duplicate = true;
                break;
            }
            if (!duplicate) result.Add(position);
        }
        return result;
    }

    /// <summary>
    /// First-wins cleanup for imported data. Lua marker tables cannot expose
    /// two usable values for the same key in game, so retaining renamed copies
    /// in the editor creates objects the game itself would have skipped.
    /// References owned by graphs, chains, armies and symmetry groups are
    /// redirected to the retained marker before the duplicate is removed.
    /// </summary>
#if UNITY_5_3_OR_NEWER
    public static int RemoveDuplicates(SC2TerrainData data)
    {
        if (data == null) return 0;
        data.EnsureObjectsLists();
        data.EnsureObjectIds();

        var retained = new List<SC2ObjMarker>();
        var idRemap = new Dictionary<int, int>();
        var nameRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var marker in data.ObjMarkers)
        {
            if (marker == null) continue;
            var existing = retained.FirstOrDefault(candidate =>
                SameName(candidate.Name, marker.Name) || SamePlacement(candidate, marker));
            if (existing == null)
            {
                retained.Add(marker);
                continue;
            }

            idRemap[marker.Id] = existing.Id;
            if (!string.IsNullOrEmpty(marker.Name) &&
                !string.Equals(marker.Name, existing.Name, StringComparison.Ordinal))
                nameRemap[marker.Name] = existing.Name;
            skipped++;
        }

        if (skipped == 0) return 0;

        data.ObjMarkers.Clear();
        data.ObjMarkers.AddRange(retained);

        int RemapId(int id) => idRemap.TryGetValue(id, out var retainedId) ? retainedId : id;
        string RemapName(string name) =>
            !string.IsNullOrEmpty(name) && nameRemap.TryGetValue(name, out var retainedName)
                ? retainedName
                : name;

        foreach (var graph in data.ObjGraphs)
        {
            if (graph == null || !graph.UsesMarkerIds) continue;
            var edgeCount = Math.Min(graph.MarkerIdsA.Count, graph.MarkerIdsB.Count);
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);
            var cleanA = new List<int>(edgeCount);
            var cleanB = new List<int>(edgeCount);
            for (var i = 0; i < edgeCount; i++)
            {
                var a = RemapId(graph.MarkerIdsA[i]);
                var b = RemapId(graph.MarkerIdsB[i]);
                if (a == b) continue;
                var low = Math.Min(a, b);
                var high = Math.Max(a, b);
                if (!seenEdges.Add(low + ":" + high)) continue;
                cleanA.Add(a);
                cleanB.Add(b);
            }
            graph.MarkerIdsA = cleanA;
            graph.MarkerIdsB = cleanB;
        }

        foreach (var chain in data.ObjChains)
            if (chain?.MarkerNames != null)
                chain.MarkerNames = chain.MarkerNames
                    .Select(RemapName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

        foreach (var army in data.ObjArmies)
            if (army != null)
                army.MarkerId = RemapId(army.MarkerId);
        data.ObjArmies = data.ObjArmies
            .Where(army => army != null)
            .GroupBy(army => army.MarkerId != 0 ? "id:" + army.MarkerId : "name:" + (army.Name ?? ""), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        foreach (var group in data.ObjSymmetryGroups)
        {
            if (group == null || !string.Equals(group.Kind, "Marker", StringComparison.OrdinalIgnoreCase)) continue;
            var seenMembers = new HashSet<int>();
            foreach (var member in group.Members)
            {
                member.ObjectId = RemapId(member.ObjectId);
                seenMembers.Add(member.ObjectId);
            }
            group.Members = group.Members
                .Where(member => seenMembers.Remove(member.ObjectId))
                .ToList();
        }
        data.ObjSymmetryGroups.RemoveAll(group =>
            group != null &&
            string.Equals(group.Kind, "Marker", StringComparison.OrdinalIgnoreCase) &&
            group.Members.Count < 2);

        return skipped;
    }
#endif
}
