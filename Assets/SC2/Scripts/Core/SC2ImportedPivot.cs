using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The origin (pivot) of an imported terrain.bdf object. The file keeps it in the extra-data header of its layers (the map-center
/// floats of each layer, written by the exporter from the object's origin); an object that was exported from Blender away from the
/// world origin must turn and scale about that point, not about the editor's (0, 0, 0), where everything near its own origin would
/// otherwise swing round and drift towards zero.
/// </summary>
public static class SC2ImportedPivot
{
    /// <summary>The first layer origin that is set (not all zero), or (0, 0, 0) when none is: a file that records no origin keeps
    /// turning about the world origin, as before.</summary>
    public static Vector3 FromLayerCenters(IEnumerable<float[]> layerCenters)
    {
        if (layerCenters == null) return Vector3.zero;
        foreach (var center in layerCenters)
        {
            if (center == null || center.Length < 3) continue;
            if (center[0] != 0f || center[1] != 0f || center[2] != 0f) return new Vector3(center[0], center[1], center[2]);
        }
        return Vector3.zero;
    }

    /// <summary>The position an object saved before objects turned about their origin has when it turns about it: the same placement
    /// (every vertex ends up where it did) expressed relative to the pivot. World = position + rotation * (scale * vertex) before,
    /// and position' + rotation * (scale * (vertex - pivot)) now.</summary>
    public static Vector3 ConvertLegacyPosition(Vector3 position, Quaternion rotation, Vector3 scale, Vector3 pivot) =>
        position + rotation * Vector3.Scale(scale, pivot);
}
