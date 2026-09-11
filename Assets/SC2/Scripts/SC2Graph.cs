// SC2Graph.cs
// MonoBehaviour data carrier for a MapGraph: the graph's name plus a list of
// edges (pairs of indices into the map's shared marker list). Draws edges as
// lines in the Scene view — useful for checking AirGraph/LandGraph/AmphibiousGraph/
// NavalGraph after import and for the symmetry tools.

using System.Collections.Generic;
using UnityEngine;

public class SC2Graph : MonoBehaviour
{
    public string GraphName = "";

    /// <summary>Pairs of indices into <see cref="MarkerTransforms"/> (= the MapMarker[] order in mapobjs.bdf).</summary>
    public List<Vector2Int> Entries = new();

    /// <summary>References to every map marker in their original order (shared across all graphs).</summary>
    public List<Transform> MarkerTransforms = new();

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        foreach (var (a, b) in EnumerateEdges())
            if (a != null && b != null)
                Gizmos.DrawLine(a.position, b.position);
    }

    private IEnumerable<(Transform?, Transform?)> EnumerateEdges()
    {
        foreach (var e in Entries)
        {
            Transform? a = e.x >= 0 && e.x < MarkerTransforms.Count ? MarkerTransforms[e.x] : null;
            Transform? b = e.y >= 0 && e.y < MarkerTransforms.Count ? MarkerTransforms[e.y] : null;
            yield return (a, b);
        }
    }
}
