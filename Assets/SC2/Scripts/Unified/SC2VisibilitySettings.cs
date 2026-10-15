using UnityEngine;

/// <summary>The Visibility menu's toggles, kept per user (PlayerPrefs, like <see cref="SC2CustomizationSettings"/>): what was shown and
/// hidden when the editor was left comes back the next time it starts, for every map. Not part of the map document.</summary>
public static class SC2VisibilitySettings
{
    /// <summary>The toggles in the order <see cref="SC2GameViewEditorUI"/> reads and writes them.</summary>
    public static readonly string[] Names =
    {
        "Terrain", "Hfield", "Costs", "Collision2",
        "Props", "Markers", "Units", "ReferenceUnits",
        "GraphLand", "GraphAir", "GraphNaval", "GraphAmphibious",
        "Chains", "Areas", "Decals", "Water", "RNM", "Grid", "Wireframe",
    };

    private const string KeyPrefix = "SC2.Visibility.";

    /// <summary>Replaces each value with the saved one; a toggle that was never saved keeps the value it has (its default).</summary>
    public static void Load(bool[] values)
    {
        for (var i = 0; i < Names.Length && i < values.Length; i++)
            values[i] = PlayerPrefs.GetInt(KeyPrefix + Names[i], values[i] ? 1 : 0) != 0;
    }

    public static void Save(bool[] values)
    {
        for (var i = 0; i < Names.Length && i < values.Length; i++)
            PlayerPrefs.SetInt(KeyPrefix + Names[i], values[i] ? 1 : 0);
        PlayerPrefs.Save();
    }
}
