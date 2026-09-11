#if !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Runtime replacement for the Editor's native Object Picker window, used by
/// the EditorGUILayout.ObjectField shim (UnityEditorRuntimeShim.cs).
///
/// The shim's ObjectField used to just display the current object's name with
/// no way to change it — clicking did nothing, since there's no
/// AssetDatabase/Project window to pick from in a standalone build. That's
/// exactly why "Terrain Data" (an ObjectField for the active SC2TerrainData)
/// could never be switched to another instance in a build: the field was
/// permanently read-only there.
///
/// Lists candidates via Resources.FindObjectsOfTypeAll(type) filtered to an
/// exact type match — the same "whatever's currently loaded in memory"
/// approach already used by SC2TerrainShell's runtime texture picker, so it
/// naturally includes anything created via ScriptableObject.CreateInstance
/// this session (e.g. every terrain loaded via File → Load Terrain...) even
/// though none of it lives in an actual Resources/ folder.
/// </summary>
internal static class SC2RuntimeObjectPicker
{
    private static int _openControlId = -1;
    private static Type _openType;
    private static UnityEngine.Object[] _openCandidates;
    private static Rect _anchorRect;
    private static Vector2 _scroll;

    private static int _pendingControlId = -1;
    private static UnityEngine.Object _pendingResult;
    private static bool _pendingIsSet;

    public static bool IsOpen(int controlId) => _openControlId == controlId;
    public static bool IsAnyOpen => _openControlId >= 0;

    public static void Toggle(int controlId, Type type, Rect anchorScreenRect)
    {
        if (_openControlId == controlId)
        {
            _openControlId = -1;
            return;
        }

        _openControlId = controlId;
        _openType = type;
        _openCandidates = Resources.FindObjectsOfTypeAll(type)
            .Where(o => o != null && o.GetType() == type)
            .OrderBy(o => o.name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _anchorRect = anchorScreenRect;
        _scroll = Vector2.zero;
    }

    public static void UpdateAnchor(int controlId, Rect anchorScreenRect)
    {
        if (_openControlId == controlId)
            _anchorRect = anchorScreenRect;
    }

    public static UnityEngine.Object ConsumePendingResult(int controlId, UnityEngine.Object fallback)
    {
        if (_pendingControlId != controlId)
            return fallback;

        _pendingControlId = -1;
        var result = _pendingIsSet ? _pendingResult : fallback;
        _pendingIsSet = false;
        return result;
    }

    public static void DrawOverlays()
    {
        if (_openControlId < 0)
            return;

        const float rowHeight = 20f;
        const float maxListHeight = 220f;
        var width = Mathf.Clamp(_anchorRect.width, 160f, Mathf.Max(160f, Screen.width - 20f));
        var itemCount = 1 + (_openCandidates?.Length ?? 0); // +1 for "None"
        var height = Mathf.Min(maxListHeight, itemCount * rowHeight + 6f);

        var x = Mathf.Clamp(_anchorRect.x, 4f, Mathf.Max(4f, Screen.width - width - 4f));
        var y = _anchorRect.yMax + 2f;
        if (y + height > Screen.height - 4f)
            y = Mathf.Max(4f, _anchorRect.y - height - 2f);

        var listRect = new Rect(x, y, width, height);

        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && !listRect.Contains(e.mousePosition))
        {
            _openControlId = -1;
            return;
        }

        var previousDepth = GUI.depth;
        GUI.depth = -2000;

        GUI.Box(listRect, GUIContent.none, GUI.skin.box);
        GUILayout.BeginArea(listRect);
        _scroll = GUILayout.BeginScrollView(_scroll);

        if (GUILayout.Button("None", GUILayout.Height(rowHeight - 2f)))
        {
            _pendingControlId = _openControlId;
            _pendingResult = null;
            _pendingIsSet = true;
            _openControlId = -1;
        }

        if (_openCandidates != null)
        {
            foreach (var candidate in _openCandidates)
            {
                var label = candidate != null ? candidate.name : "<destroyed>";
                if (GUILayout.Button(label, GUILayout.Height(rowHeight - 2f)))
                {
                    _pendingControlId = _openControlId;
                    _pendingResult = candidate;
                    _pendingIsSet = true;
                    _openControlId = -1;
                }
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        GUI.depth = previousDepth;

        if (e != null && e.type != EventType.Layout && e.type != EventType.Repaint)
            e.Use();
    }
}
#endif
