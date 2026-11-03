using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Harness-only: stands in for the IMGUI calls of the mission panel and records where every clickable control was drawn.</summary>
public static class MissionProbe
{
    public struct Control
    {
        public string Kind;
        public string Label;
        public Rect ScreenRect;
        public bool Enabled;
    }

    public static readonly List<Control> Controls = new();
    public static Vector2 GuiOrigin;
    public static bool HaveOrigin;

    private static void Record(string kind, string label, Rect guiRect, bool topLevel = false)
    {
        if (Event.current.type != EventType.Repaint) return;
        var p = GUIUtility.GUIToScreenPoint(guiRect.position);
        if (topLevel && !HaveOrigin) { GuiOrigin = p - guiRect.position; HaveOrigin = true; }
        Controls.Add(new Control { Kind = kind, Label = label, ScreenRect = new Rect(p, guiRect.size), Enabled = GUI.enabled });
    }

    public static bool Button(Rect r, string t) { var v = GUI.Button(r, t); Record("Button", t, r, true); return v; }
    public static bool Button(Rect r, string t, GUIStyle s) { var v = GUI.Button(r, t, s); Record("Button", t, r, true); return v; }
    public static bool LButton(string t, params GUILayoutOption[] o) { var v = GUILayout.Button(t, o); Record("Button", t, GUILayoutUtility.GetLastRect()); return v; }
    public static bool LButton(string t, GUIStyle s, params GUILayoutOption[] o) { var v = GUILayout.Button(t, s, o); Record("Button", t, GUILayoutUtility.GetLastRect()); return v; }
    public static bool Toggle(Rect r, bool value, string t) { var v = GUI.Toggle(r, value, t); Record("Toggle", t, r, true); return v; }
    public static bool Toggle(Rect r, bool value, string t, GUIStyle s) { var v = GUI.Toggle(r, value, t, s); Record("Toggle", t, r, true); return v; }
    public static bool LToggle(bool value, string t, params GUILayoutOption[] o) { var v = GUILayout.Toggle(value, t, o); Record("Toggle", t, GUILayoutUtility.GetLastRect()); return v; }
    public static bool LToggle(bool value, GUIContent c, GUIStyle s, params GUILayoutOption[] o) { var v = GUILayout.Toggle(value, c, s, o); Record("Toggle", c.text, GUILayoutUtility.GetLastRect()); return v; }
    public static bool LToggle(bool value, string t, GUIStyle s, params GUILayoutOption[] o) { var v = GUILayout.Toggle(value, t, s, o); Record("Toggle", t, GUILayoutUtility.GetLastRect()); return v; }
    public static string TextField(string t, params GUILayoutOption[] o) { var v = GUILayout.TextField(t, o); Record("Text", "text", GUILayoutUtility.GetLastRect()); return v; }
    public static string TextArea(string t, params GUILayoutOption[] o) { var v = GUILayout.TextArea(t, o); Record("Text", "area", GUILayoutUtility.GetLastRect()); return v; }

    /// <summary>What UnityEditorRuntimeShim.Popup does in a build, with the window's GUI origin taken off the screen position.</summary>
    public static int Popup(string fieldKey, int sel, string[] items, GUIStyle style)
    {
        sel = Mathf.Clamp(sel, 0, items.Length - 1);
        var popupKey = "popup:" + fieldKey;
        var isOpen = HarnessDropdown.IsOpen(popupKey);
        var label = items[sel] + (isOpen ? "  ^" : "  v");
        if (GUILayout.Button(label, style))
            HarnessDropdown.Toggle(popupKey, items, ToWindow(GUILayoutUtility.GetLastRect()));
        else if (isOpen && Event.current.type == EventType.Repaint)
            HarnessDropdown.UpdateAnchor(popupKey, ToWindow(GUILayoutUtility.GetLastRect()));
        Record("Popup", items[sel], GUILayoutUtility.GetLastRect());
        return HarnessDropdown.ConsumePendingResult(popupKey, sel);
    }

    public static Rect ToWindow(Rect guiRect)
    {
        var p = GUIUtility.GUIToScreenPoint(guiRect.position) - GuiOrigin;
        return new Rect(p, guiRect.size);
    }

    public static int Popup(int sel, string[] items, GUIStyle style)
    {
        var v = EditorGUILayout.Popup(sel, items, style);
        Record("Popup", items != null && sel >= 0 && sel < items.Length ? items[sel] : "", GUILayoutUtility.GetLastRect());
        return v;
    }
}
