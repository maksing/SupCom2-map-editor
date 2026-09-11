using UnityEngine;

/// <summary>Marks geometry that participates in decal placement/projection.
/// Imported terrain meshes and the live runtime terrain use the same marker,
/// preventing props and editor helper meshes from becoming decal surfaces.</summary>
public sealed class SC2DecalProjectionSurface : MonoBehaviour
{
}
