/// <summary>How a prop, marker or unit gets its Y: from the terrain heightmap, from the surface of the imported 3D model, or not at all.
/// Stored as the two flags the objects already had (<c>AnchorToTerrain</c>, which now only says "Y is derived") and
/// <c>AnchorToModel</c>, so maps saved before the 3D-model mode load unchanged.</summary>
public enum SC2HeightMode
{
    /// <summary>Y follows the terrain: the imported terrain model where there is one, the heightmap elsewhere (what "Attach to terrain" always did).</summary>
    Heightmap = 0,
    /// <summary>Y follows the surface nearest to the object's own height among every layer under it: the imported 3D terrain model's
    /// surfaces (a bridge deck, the ground under it) and the runtime terrain. So an object stays where it was put, under a bridge too.</summary>
    Model = 1,
    /// <summary>Y is an absolute value the object keeps.</summary>
    Free = 2,
}

public static class SC2HeightModes
{
    public static SC2HeightMode From(bool anchorToTerrain, bool anchorToModel) =>
        !anchorToTerrain ? SC2HeightMode.Free : anchorToModel ? SC2HeightMode.Model : SC2HeightMode.Heightmap;

    public static void Apply(SC2HeightMode mode, out bool anchorToTerrain, out bool anchorToModel)
    {
        anchorToTerrain = mode != SC2HeightMode.Free;
        anchorToModel = mode == SC2HeightMode.Model;
    }

    public static string Label(SC2HeightMode mode) => mode switch
    {
        SC2HeightMode.Heightmap => "Heightmap",
        SC2HeightMode.Model => "3D model",
        _ => "Free",
    };

    /// <summary>The surface of the 3D model nearest to <paramref name="referenceY"/>, or null when there is none. A column through the
    /// model can cross several surfaces (a bridge deck and the ground under it); an object belongs to the one it is already on, so
    /// moving it along the ground under a bridge keeps it under the bridge.</summary>
    public static float? NearestSurface(System.Collections.Generic.IEnumerable<float> surfaces, float referenceY)
    {
        float? best = null;
        var bestDistance = float.MaxValue;
        foreach (var y in surfaces)
        {
            var distance = System.Math.Abs(y - referenceY);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = y;
        }
        return best;
    }

    /// <summary>The Y of an anchored object. <paramref name="modelHeight"/> is the imported model's surface under it when there is one;
    /// <paramref name="terrainHeight"/> is the terrain height Heightmap mode uses. Model mode keeps <paramref name="currentY"/> when
    /// there is no surface at all under the object.</summary>
    public static float AnchoredY(SC2HeightMode mode, float? modelHeight, float terrainHeight, float currentY, float offset) => mode switch
    {
        SC2HeightMode.Model => modelHeight.HasValue ? modelHeight.Value + offset : currentY,
        SC2HeightMode.Heightmap => terrainHeight + offset,
        _ => currentY,
    };
}
