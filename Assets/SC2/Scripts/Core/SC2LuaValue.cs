// SC2LuaValue.cs
// Minimal helpers for extracting values out of Lua expressions like
// STRING( 'text' ) / VECTOR3( x, y, z ) — used both when importing
// lua-only markers (ARMY_N/Mass/Combat Zone/..., see ScdImportWindow) and
// when regenerating them (SC2SaveLuaWriter).

using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

public static class SC2LuaValue
{
    private static readonly Regex StringRe = new(@"STRING\(\s*'(?<v>[^']*)'\s*\)", RegexOptions.Compiled);
    private static readonly Regex BareStringRe = new(@"^\s*'(?<v>[^']*)'\s*$", RegexOptions.Compiled);
    private static readonly Regex Vector3Re = new(
        @"VECTOR3\(\s*(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)\s*,\s*(?<z>-?[\d.]+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex FloatRe = new(@"FLOAT\(\s*(?<v>-?[\d.]+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex IntegerRe = new(@"INTEGER\(\s*(?<v>-?\d+)\s*\)", RegexOptions.Compiled);
    private static readonly Regex QuaternionRe = new(
        @"QUATERNION\(\s*(?<w>-?[\d.]+)\s*,\s*(?<x>-?[\d.]+)\s*,\s*(?<y>-?[\d.]+)\s*,\s*(?<z>-?[\d.]+)\s*\)", RegexOptions.Compiled);

    /// <summary>Accepts the STRING(...) wrapper, or a bare Lua string literal
    /// with no wrapper at all — real _save.lua Armies blocks store
    /// personality/plans/type/Alliances values as plain 'text' with no
    /// STRING() around them (only Markers consistently use the wrapper).</summary>
    public static string? ExtractString(string raw)
    {
        var m = StringRe.Match(raw);
        if (m.Success) return m.Groups["v"].Value;
        var bm = BareStringRe.Match(raw);
        return bm.Success ? bm.Groups["v"].Value : null;
    }

    public static Vector3? ExtractVector3(string raw)
    {
        var m = Vector3Re.Match(raw);
        if (!m.Success) return null;
        return new Vector3(
            float.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Accepts FLOAT(...)/INTEGER(...) wrappers, or a bare numeric
    /// literal with no wrapper at all (seen in some real _save.lua fields,
    /// e.g. StartPositions' plain {x, y, z} triples).</summary>
    public static float? ExtractFloat(string raw)
    {
        var m = FloatRe.Match(raw);
        if (m.Success) return float.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
        var im = IntegerRe.Match(raw);
        if (im.Success) return float.Parse(im.Groups["v"].Value, CultureInfo.InvariantCulture);
        if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bare)) return bare;
        return null;
    }

    /// <summary>QUATERNION(w, x, y, z) — matches the wrapper's own component
    /// order (w first), same order SC2ObjectsExport writes it in.</summary>
    public static Quaternion? ExtractQuaternion(string raw)
    {
        var m = QuaternionRe.Match(raw);
        if (!m.Success) return null;
        return new Quaternion(
            float.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["z"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture));
    }
}
