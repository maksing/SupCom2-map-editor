using System.Collections.Generic;

/// <summary>
/// Hand-ported registry of the engine's marker types, from
/// gamedata.scd/uncompiled/lua/common/MarkerTemplates.lua (21 types).
/// Ported directly to a C# table
/// instead of parsing the .lua file at runtime: the source file isn't part
/// of any shipped map archive (it's a common game-data file, not something
/// this editor's import/export pipeline ever needs to read/write), so a
/// static table is both simpler and has zero dependency on that file being
/// present next to a build.
///
/// Real-data note: only 14 of these 21 types
/// actually appear in any of the 56 surveyed vanilla maps' mapobjs.bdf
/// (Hydrocarbon/Combat Zone/Island/Naval Defensive Point/Naval Exclude/
/// Protected Experimental Construction/the four typed Path Node variants
/// never occur — real path nodes are all generically "Default Path Node",
/// differentiated by MapGraph membership, not by type name). The full list
/// is kept here anyway for completeness/custom maps; UI defaults should
/// prioritize the 14 that actually occur.
/// </summary>
public static class SC2MarkerTemplates
{
    public sealed class Template
    {
        public string Name = "";
        public bool Resource;
        public float Amount;
        public float Size;
        public bool Hint;
        public string Graph = "";
        public string Prop = "";
        public string ColorHex = "ffffffff";
        public string EditorIcon = "";

        /// <summary>Real marker artwork — a matching pair of a UI icon
        /// (Resources/EditorIcons/Markers/&lt;Key&gt;.png, used by the marker
        /// placement picker) and a real 3D gizmo mesh (Resources/MarkerModels/
        /// &lt;Key&gt;.fbx, instantiated in the viewport by SC2MarkerScenePreview)
        /// for 26 of the 30 registered types. The remaining 4
        /// types with no per-type asset (Hydrocarbon, Weather Definition,
        /// Base Marker, gpnav Playable Island — already grouped under one
        /// "Other" picker tile in MarkerCategories) share one generic
        /// "other" gizmo (Resources/MarkerModels/other.fbx) instead of the
        /// old plain flat-dot viewport rendering.</summary>
        public string IconKey = "";
    }

    public static readonly Template[] All =
    {
        new() { Name = "Blank Marker", Prop = "/props/markers/M_Blank_prop.bp", ColorHex = "ff800080", IconKey = "blank_marker" },
        new() { Name = "Mass", Resource = true, Amount = 100f, Size = 6f, Prop = "/props/markers/M_Mass_prop.bp", ColorHex = "ff808080", EditorIcon = "/textures/editor/marker_mass.bmp", IconKey = "mass" },
        new() { Name = "Hydrocarbon", Resource = true, Amount = 100f, Size = 3f, Prop = "/props/markers/M_Hydrocarbon_prop.bp", ColorHex = "ff008000", IconKey = "other" },
        new() { Name = "Combat Zone", Hint = true, Prop = "/props/markers/M_CombatZone_prop.bp", ColorHex = "ff800000", IconKey = "combat_zone" },
        new() { Name = "Defensive Point", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "ff008000", IconKey = "defensive_point" },
        new() { Name = "Effect", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "ffbffcd0", IconKey = "effect" },
        new() { Name = "Expansion Area", Hint = true, Prop = "/props/markers/M_Expansion_prop.bp", ColorHex = "ff008080", IconKey = "expansion_area" },
        new() { Name = "Large Expansion Area", Hint = true, Prop = "/props/markers/M_Expansion_prop.bp", ColorHex = "ffff0080", IconKey = "large_expansion_area" },
        new() { Name = "Naval Area", Hint = true, Prop = "/props/markers/M_Expansion_prop.bp", ColorHex = "ff0000FF", IconKey = "naval_area" },
        new() { Name = "Naval Defensive Point", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "ff0080FF", IconKey = "naval_defensive_point" },
        new() { Name = "Naval Rally Point", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "ff00FFFF", IconKey = "naval_rally_point" },
        new() { Name = "Protected Experimental Construction", Hint = true, Prop = "/props/markers/M_Expansion_prop.bp", ColorHex = "ff0000AA", IconKey = "protected_experimental_construction" },
        new() { Name = "Rally Point", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "FF808000", IconKey = "rally_point" },
        new() { Name = "Island", Hint = true, Prop = "/props/markers/M_CombatZone_prop.bp", ColorHex = "ff0000AA", IconKey = "island" },
        new() { Name = "Naval Exclude", Hint = true, Prop = "/props/markers/M_CombatZone_prop.bp", ColorHex = "ff0000AA", IconKey = "naval_exclude" },
        new() { Name = "Weather Definition", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "FF808000", IconKey = "other" },
        new() { Name = "Weather Generator", Hint = true, Prop = "/props/markers/M_Defensive_prop.bp", ColorHex = "FF808000", IconKey = "weather_generator" },
        new() { Name = "Camera Info", Prop = "/props/markers/M_Camera_prop.bp", ColorHex = "ff808000", EditorIcon = "/textures/editor/marker_mass.bmp", IconKey = "camera_info" },
        new() { Name = "Attack Marker", Prop = "/props/markers/M_Blank_prop.bp", ColorHex = "ff800080", IconKey = "attack_marker" },
        new() { Name = "Transport Marker", Prop = "/props/markers/M_Blank_prop.bp", ColorHex = "ff80A088", IconKey = "transport_marker" },
        new() { Name = "Naval Link", Prop = "/props/markers/M_Blank_prop.bp", ColorHex = "ffff0000", IconKey = "naval_link" },
        new() { Name = "Land Path Node", Hint = true, Graph = "DefaultLand", Prop = "/props/markers/M_Path_prop.bp", ColorHex = "ff00ff00", IconKey = "land_path_node" },
        new() { Name = "Water Path Node", Hint = true, Graph = "DefaultWater", Prop = "/props/markers/M_Path_prop.bp", ColorHex = "ff0000ff", IconKey = "water_path_node" },
        new() { Name = "Air Path Node", Hint = true, Graph = "DefaultAir", Prop = "/props/markers/M_Path_prop.bp", ColorHex = "ffffffff", IconKey = "air_path_node" },
        new() { Name = "Amphibious Path Node", Hint = true, Graph = "DefaultAmphibious", Prop = "/props/markers/M_Path_prop.bp", ColorHex = "ff00ffff", IconKey = "amphibious_path_node" },

        // Real-data types seen in vanilla mapobjs.bdf but absent from
        // MarkerTemplates.lua — kept here so the registry covers
        // every type actually found on disk, not just the documented ones.
        new() { Name = "Default Path Node", Hint = true, Prop = "/props/markers/M_Path_prop.bp", ColorHex = "ff008000", IconKey = "default_path_node" },
        new() { Name = "Sound", Prop = "", ColorHex = "ff008000", IconKey = "sound" },
        new() { Name = "Base Marker", Prop = "", ColorHex = "ff008000", IconKey = "other" },
        new() { Name = "gpnav Playable Island", Prop = "", ColorHex = "ff008000", IconKey = "other" },

        // Not a real engine type — an authoring convenience (FAF map editor
        // analysis, "author once, resolve at export"). SC2ObjectsExport
        // resolves this to "Land Path Node" or "Water Path Node" by sampling
        // terrain height against the map's water elevation, so a path can be
        // laid out without deciding land/water per node up front. Never
        // written to mapobjs.bdf/_save.lua as-is.
        new() { Name = "Auto Path Node", Hint = true, Prop = "/props/markers/M_Path_prop.bp", ColorHex = "ffffff00", IconKey = "auto_path_node" },
    };

    /// <summary>Resources-relative key for the Army start-position gizmo
    /// (C:\Users\user\Desktop\Markers\Army) — Army start positions are
    /// SC2ObjMarker entries typed "Blank Marker" (see SC2ObjectsShell's
    /// ArmyPositionRe/NextArmyName), not a real registered marker template,
    /// so this lives here as a standalone constant rather than a Template
    /// entry. Used by SC2MarkerScenePreview (viewport gizmo) and
    /// SC2ObjectsShell.DrawArmies (Armies tab header icon).</summary>
    public const string ArmyIconKey = "army";

    private static Dictionary<string, Template> _byName;

    public static Template Find(string typeName)
    {
        if (_byName == null)
        {
            _byName = new Dictionary<string, Template>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in All) _byName[t.Name] = t;
        }
        return _byName.TryGetValue(typeName ?? "", out var found) ? found : null;
    }

    public static UnityEngine.Color HexToColor(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 8)
            return new UnityEngine.Color(1f, 1f, 1f, 0.9f);
        // Format is AARRGGBB (as stored in the lua files, e.g. "ff008000").
        byte Parse(int i) => byte.Parse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber);
        var a = Parse(0); var r = Parse(2); var g = Parse(4); var b = Parse(6);
        return new UnityEngine.Color32(r, g, b, a);
    }
}
