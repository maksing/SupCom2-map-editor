// SC2SymmetrySettings.cs
// Shared map symmetry settings: one instance per map
// (usually under the imported scene's root). Center is the world-space
// (X,Z) coordinates of the map/symmetry-axis center (defaults to (0,0);
// correct for a 1024x1024 map whose playable rectangle is centered at
// (0,0) — otherwise adjust manually after import).

using UnityEngine;

public class SC2SymmetrySettings : MonoBehaviour
{
    public SC2SymmetryMode Mode = SC2SymmetryMode.None;
    public Vector2 Center = Vector2.zero;

    /// <summary>Only meaningful when Mode==Rotational — 2..8 inclusive,
    /// including odd counts. Mirrors SC2TerrainData.RadialCount's role for
    /// the unified system, but kept as its own field here since this legacy
    /// scene component is independent of SC2TerrainData.</summary>
    public int RadialCount = 4;

    /// <summary>
    /// Total number of army start positions — used for renaming ARMY_N.
    /// Defaults to the symmetry slot count (2 for mirrors, N for
    /// Rotational), but can be larger (not every symmetry slot is a
    /// start position).
    /// </summary>
    public int TotalArmies = 2;

    /// <summary>Number of symmetry "slots", including the original (0) — Mirror*=2..4, Rotational=RadialCount, None=1.</summary>
    public int SlotCount => Mode switch
    {
        SC2SymmetryMode.None => 1,
        SC2SymmetryMode.MirrorX => 2,
        SC2SymmetryMode.MirrorZ => 2,
        SC2SymmetryMode.MirrorXZ => 4,
        SC2SymmetryMode.Rotational => Mathf.Clamp(RadialCount, 2, 8),
        _ => 1
    };
}
