using System;
using System.Collections.Generic;
using SC2.MapEditor.SmartExport;
using UnityEngine;

[Serializable]
public sealed class SC2EditorContext
{
    public SC2MapRawData MapRawData;
    public GameObject MapRoot;
    public GameObject ActiveTerrain;
    public UnityEngine.Object SelectedAsset;

    public SC2EditorMainTab MainTab;
    public int SubTabIndex;
    public SC2ViewportTool ViewportTool = SC2ViewportTool.Select;
    public SC2DirtyFlags DirtyFlags;

    public bool OverlayTerrain = true;
    public bool OverlayHfield;
    public bool OverlayCosts;
    public bool OverlayCollision2;
    public bool OverlayProps = true;
    public bool OverlayMarkers = true;
    public bool OverlayUnits = true;
    public bool OverlayReferenceUnits = true;
    public bool OverlayGraphLand = true;
    public bool OverlayGraphAir = true;
    public bool OverlayGraphNaval = true;
    public bool OverlayGraphAmphibious = true;
    public bool OverlayChains = true;
    public bool OverlayAreas = true;
    public bool OverlayWater = true;
    public bool OverlayRNM;
    public bool OverlayGrid;
    public bool OverlayWireframe;

    public bool SceneBridgeEnabled = true;
    public bool CustomNavigationEnabled = true;
    public bool DrawViewportHud = true;
    public bool SelectByClick = true;
    public bool UseTransformHandles = true;

    public string StatusMessage = "Ready";
    public string ImportPath = "";
    public string ExportPath = "";

    public List<string> ObjectFilters = new List<string>
    {
        "All",
        "Armies",
        "Resources",
        "Paths",
        "Props",
        "Areas",
        "Selected"
    };

    public int ActiveObjectFilterIndex;

    public void EnsureDefaults()
    {
        if (ObjectFilters == null)
            ObjectFilters = new List<string>();

        if (ObjectFilters.Count == 0)
        {
            ObjectFilters.Add("All");
            ObjectFilters.Add("Armies");
            ObjectFilters.Add("Resources");
            ObjectFilters.Add("Paths");
            ObjectFilters.Add("Props");
            ObjectFilters.Add("Areas");
            ObjectFilters.Add("Selected");
        }
    }

    public string ActiveSubtabName
    {
        get
        {
            var subtabs = SC2EditorTabs.GetSubtabs(MainTab);
            if (subtabs.Length == 0)
                return "";

            if (SubTabIndex < 0)
                SubTabIndex = 0;
            if (SubTabIndex >= subtabs.Length)
                SubTabIndex = subtabs.Length - 1;

            return subtabs[SubTabIndex];
        }
    }

    public void SetMainTab(SC2EditorMainTab tab)
    {
        EnsureDefaults();

        if (MainTab == tab)
            return;

        MainTab = tab;
        SubTabIndex = 0;
    }

    public string GetOverlaySummary()
    {
        EnsureDefaults();

        var enabled = new List<string>();
        if (OverlayTerrain) enabled.Add("Terrain");
        if (OverlayHfield) enabled.Add("Hfield");
        if (OverlayCosts) enabled.Add("Costs");
        if (OverlayCollision2) enabled.Add("Collision2");
        if (OverlayProps) enabled.Add("Props");
        if (OverlayMarkers) enabled.Add("Markers");
        if (OverlayUnits) enabled.Add("Units");
        if (OverlayReferenceUnits) enabled.Add("ReferenceUnits");
        if (OverlayGraphLand) enabled.Add("Path:Land");
        if (OverlayGraphAir) enabled.Add("Path:Air");
        if (OverlayGraphNaval) enabled.Add("Path:Naval");
        if (OverlayGraphAmphibious) enabled.Add("Path:Amphibious");
        if (OverlayChains) enabled.Add("Chains");
        if (OverlayAreas) enabled.Add("Areas");
        if (OverlayWater) enabled.Add("Water");
        if (OverlayRNM) enabled.Add("RNM");
        if (OverlayGrid) enabled.Add("Grid");
        if (OverlayWireframe) enabled.Add("Wireframe");

        return enabled.Count == 0 ? "None" : string.Join(", ", enabled.ToArray());
    }

    /// <summary>
    /// Copy overlay state from the GameView component into this context.
    /// Call from the GameView when it wants to push its overlay toggles to the shared state.
    /// </summary>
    public void ApplyOverlayState(bool terrain, bool hfield, bool costs, bool collision2,
        bool props, bool markers, bool units, bool referenceUnits,
        bool graphLand, bool graphAir, bool graphNaval, bool graphAmphibious,
        bool chains, bool areas, bool water, bool rnm, bool grid, bool wireframe)
    {
        OverlayTerrain = terrain;
        OverlayHfield = hfield;
        OverlayCosts = costs;
        OverlayCollision2 = collision2;
        OverlayProps = props;
        OverlayMarkers = markers;
        OverlayUnits = units;
        OverlayReferenceUnits = referenceUnits;
        OverlayGraphLand = graphLand;
        OverlayGraphAir = graphAir;
        OverlayGraphNaval = graphNaval;
        OverlayGraphAmphibious = graphAmphibious;
        OverlayChains = chains;
        OverlayAreas = areas;
        OverlayWater = water;
        OverlayRNM = rnm;
        OverlayGrid = grid;
        OverlayWireframe = wireframe;
    }
}
