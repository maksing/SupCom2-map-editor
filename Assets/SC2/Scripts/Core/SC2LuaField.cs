using System;

// Split out of SC2Marker.cs: SC2LuaField itself has zero dependency beyond
// System, but the rest of that file defines SC2Marker : MonoBehaviour with
// an OnDrawGizmos() that calls UnityEditor.Handles.Label under #if
// UNITY_EDITOR — pulling that whole file in (as SC2ObjMarker.ExtraFields,
// Scripts/SC2ObjectsModel.cs, needs this struct) would have forced a
// UnityEditor reference onto SC2.Core.asmdef, which otherwise doesn't need
// one.

/// <summary>
/// One "key-value" pair from a _save.lua Markers section (e.g.
/// <c>color = STRING( 'ff00ffff' )</c>), excluding <c>type</c>/
/// <c>position</c>/<c>adjacentTo</c> — those are recomputed at export time
/// (SC2SaveLuaWriter), but PRESERVE THEIR POSITION in the list (field order
/// in the final lua only matters for diff readability, not for the engine).
/// </summary>
[Serializable]
public struct SC2LuaField
{
    public string Key;
    public string RawValue; // as it appears in lua, e.g. "STRING( 'ff00ffff' )", "VECTOR3( 0, 0, 0 )"
}
