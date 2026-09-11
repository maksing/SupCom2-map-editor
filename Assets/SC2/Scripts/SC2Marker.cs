// SC2Marker.cs
// MonoBehaviour data carrier for a MapMarker, plus a Scene view gizmo.
// Color is keyed by marker type, for orientation while importing/editing
// (symmetry, graphs, etc.).

using System;
using System.Collections.Generic;
using UnityEngine;

// SC2LuaField moved to SC2LuaField.cs so it can live in
// SC2.Core.asmdef without pulling this MonoBehaviour's UnityEditor.Handles
// usage in with it — see that file's header comment.

public class SC2Marker : MonoBehaviour
{
    public string MarkerName = "";
    public string MarkerType = "";

    /// <summary>
    /// true for markers that exist ONLY in _save.lua (ARMY_N, Mass,
    /// Combat Zone, Rally Point, Transport Marker, etc. — NOT part of
    /// mapobjs.bdf, see ScdImportWindow.ImportLuaOnlyMarkers). Such
    /// markers are not exported to mapobjs.bdf, but still participate in
    /// symmetry (including ARMY_N renaming) and _save.lua regeneration.
    /// </summary>
    public bool IsLuaOnly;

    /// <summary>
    /// "Lua-only" fields from _save.lua (color/hint/graph/prop/orientation/
    /// size/resource/amount/editorIcon/...) — absent from mapobjs.bdf,
    /// kept around to regenerate _save.lua (SC2SaveLuaWriter). Empty for
    /// markers imported without a save.lua or created manually (export
    /// then uses a minimal default set).
    /// </summary>
    public List<SC2LuaField> LuaExtras = new();

    private static readonly Color DefaultColor = new(1f, 1f, 1f, 0.9f);

    private static readonly (string Prefix, Color Color)[] TypeColors =
    {
        ("Air Path Node",        new Color(0.3f, 0.6f, 1f)),
        ("Land Path Node",       new Color(0.6f, 0.9f, 0.3f)),
        ("Amphibious Path",      new Color(0.3f, 0.9f, 0.9f)),
        ("Naval Path",           new Color(0.2f, 0.4f, 1f)),
        ("Water Path Node",      new Color(0.2f, 0.4f, 1f)),
        ("Mass",                 new Color(1f, 0.85f, 0.2f)),
        ("ARMY_",                new Color(1f, 0.2f, 0.2f)),
        ("Combat Zone",          new Color(1f, 0.5f, 0f)),
    };

    public Color GizmoColor
    {
        get
        {
            foreach (var (prefix, color) in TypeColors)
                if (MarkerType.StartsWith(prefix) || MarkerName.StartsWith(prefix))
                    return color;
            return DefaultColor;
        }
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = GizmoColor;
        Gizmos.DrawSphere(transform.position, 1.5f);
#if UNITY_EDITOR
        UnityEditor.Handles.Label(transform.position + Vector3.up * 2f, $"{MarkerName}\n[{MarkerType}]");
#endif
    }
}
