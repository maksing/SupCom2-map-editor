using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Keeps path-graph references independent of ObjMarkers list ordering. The
/// binary BDF format still uses marker indexes at export time; that conversion
/// belongs at the format boundary, not in the editor's working model.
/// </summary>
public static class SC2ObjectGraphRefs
{
    public static void MigrateLegacyIndexes(SC2TerrainData data)
    {
        if (data == null || data.ObjGraphs == null || data.ObjMarkers == null)
            return;

        foreach (var graph in data.ObjGraphs)
        {
            if (graph == null || graph.UsesMarkerIds)
                continue;

            graph.MarkerIdsA ??= new List<int>();
            graph.MarkerIdsB ??= new List<int>();
            graph.MarkerIdsA.Clear();
            graph.MarkerIdsB.Clear();

            var count = graph.EntriesA?.Count ?? 0;
            for (var i = 0; i < count; i++)
            {
                var a = graph.EntriesA[i];
                var b = graph.EntriesB != null && i < graph.EntriesB.Count ? graph.EntriesB[i] : -1;
                if (a < 0 || a >= data.ObjMarkers.Count || b < 0 || b >= data.ObjMarkers.Count)
                    continue;
                graph.MarkerIdsA.Add(data.ObjMarkers[a].Id);
                graph.MarkerIdsB.Add(data.ObjMarkers[b].Id);
            }
            graph.UsesMarkerIds = true;
        }
    }

    public static IEnumerable<(int A, int B)> Enumerate(SC2ObjGraph graph)
    {
        if (graph == null || !graph.UsesMarkerIds || graph.MarkerIdsA == null)
            yield break;

        for (var i = 0; i < graph.MarkerIdsA.Count; i++)
        {
            var b = graph.MarkerIdsB != null && i < graph.MarkerIdsB.Count ? graph.MarkerIdsB[i] : 0;
            if (graph.MarkerIdsA[i] != 0 && b != 0)
                yield return (graph.MarkerIdsA[i], b);
        }
    }

    public static int Count(SC2ObjGraph graph) => graph == null ? 0 : Enumerate(graph).Count();

    public static void AddEdge(SC2ObjGraph graph, int markerIdA, int markerIdB)
    {
        if (graph == null || markerIdA == 0 || markerIdB == 0 || markerIdA == markerIdB)
            return;
        graph.MarkerIdsA ??= new List<int>();
        graph.MarkerIdsB ??= new List<int>();
        graph.MarkerIdsA.Add(markerIdA);
        graph.MarkerIdsB.Add(markerIdB);
        graph.UsesMarkerIds = true;
    }

    public static void RemoveAt(SC2ObjGraph graph, int edgeIndex)
    {
        if (graph == null || edgeIndex < 0 || graph.MarkerIdsA == null || edgeIndex >= graph.MarkerIdsA.Count)
            return;
        graph.MarkerIdsA.RemoveAt(edgeIndex);
        if (graph.MarkerIdsB != null && edgeIndex < graph.MarkerIdsB.Count)
            graph.MarkerIdsB.RemoveAt(edgeIndex);
    }

    public static void RemoveEdgesReferencing(SC2ObjGraph graph, HashSet<int> markerIds)
    {
        if (graph?.MarkerIdsA == null || markerIds == null || markerIds.Count == 0)
            return;
        for (var i = graph.MarkerIdsA.Count - 1; i >= 0; i--)
        {
            var a = graph.MarkerIdsA[i];
            var b = graph.MarkerIdsB != null && i < graph.MarkerIdsB.Count ? graph.MarkerIdsB[i] : 0;
            if (markerIds.Contains(a) || markerIds.Contains(b))
                RemoveAt(graph, i);
        }
    }

    public static bool TryGetMarker(SC2TerrainData data, int id, out SC2ObjMarker marker)
    {
        marker = data?.ObjMarkers?.FirstOrDefault(x => x.Id == id);
        return marker != null;
    }
}
