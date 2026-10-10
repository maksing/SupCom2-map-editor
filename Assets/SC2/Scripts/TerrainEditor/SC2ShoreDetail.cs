using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// How the terrain simplifier treats the junction with water: the ground within <see cref="Band"/> of the water surface is clustered on
/// a grid <see cref="Refine"/> times finer than the rest of its pair, so the shoreline keeps most of its shape while the mesh away
/// from the water is simplified as hard as before. Such vertices never merge with coarser neighbours, so the mesh stays closed.
/// </summary>
public sealed class SC2ShoreDetail
{
    /// <summary>Height range (world units) around the water surface that counts as the junction with water.</summary>
    public const float DefaultBand = 1.5f;
    /// <summary>How many times finer than the pair's own grid the shore is clustered.</summary>
    public const int DefaultRefine = 2;

    public readonly float WaterElevation;
    public readonly float Band;
    public readonly int Refine;

    public SC2ShoreDetail(float waterElevation, float band = DefaultBand, int refine = DefaultRefine)
    {
        WaterElevation = waterElevation;
        Band = Mathf.Max(0f, band);
        Refine = Mathf.Max(1, refine);
    }

    public bool IsShore(float y) => Mathf.Abs(y - WaterElevation) <= Band;

    /// <summary>The shore options for a map, or null when its water is off (nothing to treat differently).</summary>
    public static SC2ShoreDetail For(SC2TerrainData data, SC2MapSession session)
    {
        if (data == null) return null;
        var info = SC2TerrainExporter.BuildInfoModel(data, session, quiet: true);
        return info.WaterEnabled != 0 ? new SC2ShoreDetail(info.WaterElevation) : null;
    }
}
