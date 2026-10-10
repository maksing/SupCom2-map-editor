public enum SC2EditorMainTab
{
    Terrain,
    Costs,
    Collision2,
    Objects,
    Decals,
    Environment,
    Scenario,
    Import,
    Export,
    Diagnostics
}

public enum SC2ViewportTool
{
    Select,
    Move,
    Rotate,
    Scale,
    Rect,
    Paint,
    Measure
}

public static class SC2EditorTabs
{
    public static readonly string[] MainTabs =
    {
        "Terrain",
        "Costs",
        "Collision2",
        "Objects",
        "Decals",
        "Environment",
        "Scenario",
        "Import",
        "Export",
        "Diagnostics"
    };

    public static readonly string[] TerrainTabs =
    {
        "Geometry",
        "Materials",
        "Pairs",
        "Paint Materials",
        "Simplified",
        "Imported",
        "Water",
        "Lighting Bake",
        "Background"
    };

    public static readonly string[] CostsTabs =
    {
        "Overview",
        "Unit Layer",
        "Auto Generate",
        "Paint Overrides",
        "Terrain Types",
        "Islands/Validation"
    };

    public static readonly string[] CollisionTabs = { "Collision2" };

    public static readonly string[] ObjectsTabs =
    {
        "Armies",
        "Units",
        "Props",
        "Markers",
        "Paths",
        "Areas",
        "Chains"
    };

    // Was 4 sub-tabs (Catalog/Place/Edit/Atlas), now
    // reorganized into one always-visible panel (SC2DecalsShell.
    // DrawEmbeddedGUI) instead of tab-switching between them. Atlas Editor
    // stays a separate modal (opened via a button in that one panel), not
    // folded in, since it needs real screen width for precise dragging.
    // Kept as a single-entry array (rather than removed) so the sub-tabs
    // row still renders consistently with every other main tab.
    public static readonly string[] DecalsTabs = { "Decals" };

    // Water + Water FX merged into one "Water" tab (its contents split into
    // collapsible foldout categories instead). PointCloud (was a foldout
    // inside Glow), Bloom (was a foldout inside Glow), and Camera's 5
    // foldouts (Fog of War, Intel, Depth of Field, Sharpen, Render Passes)
    // were each promoted to their own top-level tab; "Map" was split into
    // "Air Elevation" and "Wind" the same way — per direction, once
    // something is its own tab the redundant inner foldout comes out too
    // (see SC2InfoModelFieldDrawer's matching Draw*Tab methods; only Water
    // and Fog remain multi-category and keep real foldouts).
    // Order matters: see SC2EnvironmentShell/SC2InfoBdfEditorPanel's
    // matching subTab switch statements, which index into this array.
    public static readonly string[] EnvironmentTabs =
    {
        "Sun",
        "Shadow",
        "Water",
        "Fog",
        "Glow",
        "Bloom",
        "Point Clouds",
        "Fog of War",
        "Intel",
        "Depth of Field",
        "Sharpen",
        "Render Passes",
        "Air Elevation",
        "Wind",
        "Textures",
        "Advanced"
    };

    public static readonly string[] ImportTabs =
    {
        "Existing .scd",
        "Terrain BDF",
        "Custom Geometry",
        "Collision Mesh"
    };

    public static readonly string[] ExportTabs =
    {
        "Export",
        "Validation"
    };

    public static readonly string[] DiagnosticsTabs =
    {
        "Import",
        "Export",
        "Scene",
        "Logs"
    };

    /// <summary>"Scripts" holds _script.lua's one real per-map setting
    /// (playable area) — split out from "Info" (Map Name) since the two are
    /// unrelated concerns (Scenario metadata vs. mission-script content).</summary>
    public static readonly string[] ScenarioTabs =
    {
        "Info",
        "Scripts",
        "Preview"
    };

    public static string[] GetSubtabs(SC2EditorMainTab tab)
    {
        switch (tab)
        {
            case SC2EditorMainTab.Terrain:
                return TerrainTabs;
            case SC2EditorMainTab.Costs:
                return CostsTabs;
            case SC2EditorMainTab.Collision2:
                return CollisionTabs;
            case SC2EditorMainTab.Objects:
                return ObjectsTabs;
            case SC2EditorMainTab.Decals:
                return DecalsTabs;
            case SC2EditorMainTab.Environment:
                return EnvironmentTabs;
            case SC2EditorMainTab.Scenario:
                return ScenarioTabs;
            case SC2EditorMainTab.Import:
                return ImportTabs;
            case SC2EditorMainTab.Export:
                return ExportTabs;
            case SC2EditorMainTab.Diagnostics:
                return DiagnosticsTabs;
            default:
                return new[] { tab.ToString() };
        }
    }
}
