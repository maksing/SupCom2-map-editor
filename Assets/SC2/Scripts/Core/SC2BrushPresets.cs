using System;

/// <summary>
/// How a built-in brush image (Resources/Brushes/NAME.png) is written into the brush texture path that a map stores
/// (CustomBrushTexturePath / CustomGeometryBrushTexturePath): "brush:NAME". Any other value is an ordinary texture path.
/// </summary>
public static class SC2BrushPresets
{
    public const string Prefix = "brush:";

    public static string ToPath(string name) => Prefix + name;

    /// <summary>True when <paramref name="path"/> names a built-in brush; <paramref name="name"/> is its file name without extension.</summary>
    public static bool TryGetName(string path, out string name)
    {
        name = null;
        if (string.IsNullOrEmpty(path) || !path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var text = path.Substring(Prefix.Length).Trim();
        if (text.Length == 0) return false;
        name = text;
        return true;
    }

    /// <summary>"ridge_spine_b" -> "Ridge spine b".</summary>
    public static string DisplayName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var text = name.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
