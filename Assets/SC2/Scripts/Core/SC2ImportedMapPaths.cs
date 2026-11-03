using System;
using System.Text.RegularExpressions;

/// <summary>
/// The path rules that make an imported map's files belong to the map it is exported as. An imported scenario names the folder and the
/// files of the map it came from; once the map is exported under another name (a new map that an existing one was imported into, or a
/// renamed map) those names have to follow, and the textures that only the imported map's own archive carried have to be written into
/// the exported map's own folder instead of being left pointing at the old one. Pure, so the rules can be tested.
/// </summary>
public static class SC2ImportedMapPaths
{
    // ScenarioInfo fields that name files of the map itself. `reverbPreset`, `description` and `name` are deliberately not here: the
    // first is a key into the game's audio data, the others are text.
    private static readonly Regex MapField = ScenarioField("map");
    private static readonly Regex SaveField = ScenarioField("save");
    private static readonly Regex ScriptField = ScenarioField("script");

    private static Regex ScenarioField(string key) =>
        new Regex(@"^([ \t]*)" + key + @"([ \t]*=[ \t]*)(['""])[^'""\r\n]*\3", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Points the <c>map</c>, <c>save</c> and <c>script</c> fields of a scenario at the files the export writes for
    /// <paramref name="mapName"/> (<c>/maps/NAME/NAME.scmap</c>, <c>_save.lua</c>, <c>_script.lua</c>). Everything else in the text is kept
    /// byte for byte, a field that is absent is not added, and only the first occurrence of each field is changed. With no name, or no text,
    /// the text is returned as it is.</summary>
    public static string RewriteScenarioPaths(string scenarioLua, string mapName)
    {
        if (string.IsNullOrEmpty(scenarioLua) || string.IsNullOrEmpty(mapName)) return scenarioLua;
        var text = scenarioLua;
        text = ReplaceFirst(text, MapField, "map", $"/maps/{mapName}/{mapName}.scmap");
        text = ReplaceFirst(text, SaveField, "save", $"/maps/{mapName}/{mapName}_save.lua");
        text = ReplaceFirst(text, ScriptField, "script", $"/maps/{mapName}/{mapName}_script.lua");
        return text;
    }

    private static string ReplaceFirst(string text, Regex field, string key, string value) =>
        field.Replace(text, m => m.Groups[1].Value + key + m.Groups[2].Value + m.Groups[3].Value + value + m.Groups[3].Value, 1);

    /// <summary>The game path prefix of the folder the export writes a map's own textures into (<c>/Textures/NAME/</c>).</summary>
    public static string OwnTextureFolder(string mapName) => "/Textures/" + mapName + "/";

    /// <summary>True when <paramref name="gamePath"/> is a file directly inside the map's own texture folder, which the export has
    /// already written (or will); such a path is never copied again.</summary>
    public static bool IsInOwnTextureFolder(string gamePath, string mapName)
    {
        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(mapName)) return false;
        var normalized = "/" + gamePath.Replace('\\', '/').TrimStart('/');
        var prefix = OwnTextureFolder(mapName);
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && normalized.IndexOf('/', prefix.Length) < 0;
    }

    /// <summary>Whether an imported material's texture has to be written into the exported map: any texture that is not in the map's own
    /// folder already and that the base game does not ship (or that the slot asks to copy). A texture of the base game is referenced by
    /// its game path.</summary>
    public static bool NeedsCopy(string gamePath, string mapName, bool shippedByGame, bool forceCopy)
    {
        if (string.IsNullOrEmpty(gamePath)) return false;
        if (IsInOwnTextureFolder(gamePath, mapName)) return false;
        return forceCopy || !shippedByGame;
    }

    /// <summary>The file name (without extension) a texture gets in the map's own folder: the leaf of its path without the extension and
    /// the platform infix, with every character that is not a letter, digit, underscore or hyphen replaced by an underscore.</summary>
    public static string FileStem(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return "texture";
        var normalized = gamePath.Replace('\\', '/');
        var leaf = normalized.Substring(normalized.LastIndexOf('/') + 1);
        if (leaf.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) leaf = leaf.Substring(0, leaf.Length - 4);
        if (leaf.EndsWith(".win", StringComparison.OrdinalIgnoreCase)) leaf = leaf.Substring(0, leaf.Length - 4);
        var safe = Regex.Replace(leaf, @"[^A-Za-z0-9_\-]", "_");
        return safe.Length == 0 ? "texture" : safe;
    }

    /// <summary>The first of <paramref name="stem"/>, <c>stem_2</c>, <c>stem_3</c>... for which <paramref name="isTaken"/> is false, so two
    /// different textures with the same leaf name never overwrite each other.</summary>
    public static string UniqueStem(string stem, Func<string, bool> isTaken)
    {
        if (isTaken == null || !isTaken(stem)) return stem;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = stem + "_" + suffix;
            if (!isTaken(candidate)) return candidate;
        }
    }
}
