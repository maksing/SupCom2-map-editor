using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// How the terrain simplifier treats the junction with water: the ground within <see cref="Band"/> of the water surface is not
/// simplified at all, so the shoreline keeps its shape exactly while the mesh away from the water is simplified as hard as before.
/// Such vertices never merge with anything, so the mesh stays closed.
/// </summary>
public sealed class SC2ShoreDetail
{
    /// <summary>Height range (world units) around the water surface that counts as the junction with water.</summary>
    public const float DefaultBand = 1.5f;
    public readonly float WaterElevation;
    public readonly float Band;

    public SC2ShoreDetail(float waterElevation, float band = DefaultBand)
    {
        WaterElevation = waterElevation;
        Band = Mathf.Max(0f, band);
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
