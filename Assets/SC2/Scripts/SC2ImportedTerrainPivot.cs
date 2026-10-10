using UnityEngine;

/// <summary>The origin of an imported terrain.bdf object in the file's own coordinates (see SC2ImportedPivot): the object's mesh is
/// stored relative to it and its transform sits on it, so moving, turning and scaling the object work about its own origin.</summary>
public sealed class SC2ImportedTerrainPivot : MonoBehaviour
{
    public Vector3 Value;
}
