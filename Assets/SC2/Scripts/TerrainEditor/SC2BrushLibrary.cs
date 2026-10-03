using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// The built-in brush images (Resources/Brushes): loading them by the "brush:NAME" path a map stores (see SC2BrushPresets) and the
/// row of thumbnails the Paint Blend and Geometry brush panels show above their "Brush Image" field.
/// </summary>
public static class SC2BrushLibrary
{
    private const string Folder = "Brushes";
    private static Texture2D[] _all;

    /// <summary>Every built-in brush, by name.</summary>
    public static IReadOnlyList<Texture2D> All => _all ??= Resources.LoadAll<Texture2D>(Folder).OrderBy(t => t.name, System.StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The built-in brush a "brush:NAME" path names, or null.</summary>
    public static Texture2D Load(string path)
    {
        if (!SC2BrushPresets.TryGetName(path, out var name)) return null;
        foreach (var texture in All)
            if (string.Equals(texture.name, name, System.StringComparison.OrdinalIgnoreCase)) return texture;
        return null;
    }

    /// <summary>A stored brush path as a texture: a built-in brush, else whatever texture the path names (game archive, file).</summary>
    public static Texture Resolve(string path) => (Texture)Load(path) ?? SC2TerrainMaterialFactory.LoadTexturePublicAny(path);

    /// <summary>The thumbnails of the built-in brushes, <paramref name="columns"/> per row, the one in <paramref name="currentPath"/>
    /// outlined. Returns the "brush:NAME" path of the one clicked this frame, else null.</summary>
    public static string DrawPresetGrid(string currentPath, float width, int columns = 4)
    {
        var brushes = All;
        if (brushes.Count == 0) return null;
        var gap = 4f;
        var size = Mathf.Floor((width - gap * (columns - 1)) / columns);
        SC2BrushPresets.TryGetName(currentPath, out var currentName);
        string clicked = null;
        for (var start = 0; start < brushes.Count; start += columns)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(width), GUILayout.MaxWidth(width));
            for (var i = start; i < start + columns; i++)
            {
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
                if (i >= brushes.Count) continue;
                var texture = brushes[i];
                var selected = string.Equals(texture.name, currentName, System.StringComparison.OrdinalIgnoreCase);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTexture(rect, Texture2D.blackTexture);
                    GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
                    if (selected)
                    {
                        var line = new Color(0.45f, 0.7f, 1f, 1f);
                        var old = GUI.color; GUI.color = line;
                        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2), Texture2D.whiteTexture);
                        GUI.DrawTexture(new Rect(rect.x, rect.yMax - 2, rect.width, 2), Texture2D.whiteTexture);
                        GUI.DrawTexture(new Rect(rect.x, rect.y, 2, rect.height), Texture2D.whiteTexture);
                        GUI.DrawTexture(new Rect(rect.xMax - 2, rect.y, 2, rect.height), Texture2D.whiteTexture);
                        GUI.color = old;
                    }
                }
                if (GUI.Button(rect, new GUIContent("", SC2BrushPresets.DisplayName(texture.name)), GUIStyle.none))
                    clicked = SC2BrushPresets.ToPath(texture.name);
            }
            GUILayout.EndHorizontal();
        }
        return clicked;
    }
}
