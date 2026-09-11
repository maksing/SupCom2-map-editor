using SC2.MapEditor.SmartExport;
using UnityEditor;
using UnityEngine;

// EDITOR ADAPTER: this is an Editor-only EditorWindow, not the main UI path.
// It hosts the same runtime SC2TerrainShell used by SC2GameViewEditorUI, so
// terrain-editing logic itself lives in one place — but this window's own
// layout/chrome only exists in the Unity Editor and has no standalone Build &
// Run equivalent. New terrain-editing features belong in SC2TerrainShell, not
// in this window's Editor-only layout code.
public sealed class SC2MapEditorWindow : EditorWindow
{
    private const float LeftPanelWidth = 280f;
    private const float HeaderHeight = 92f;
    private const float StatusBarHeight = 22f;

    [SerializeField] private SC2EditorContext _context = new SC2EditorContext();
    private SC2TerrainShell _terrainShell;
    private SC2SceneViewBridge _sceneBridge;
    private SC2CostsTerrainTypesEditor _costsEditor;
    private SC2EnvironmentEditor _environmentEditor;
    private Vector2 _leftScroll;
    private Vector2 _mainScroll;
    private string _newObjectFilter = "";
    private string _scenarioLuaPath = "scenario.lua";
    private string _scenarioMapPath = "";
    private string _scenarioScriptPath = "";

    [MenuItem("SC2 Map Editor/Open")]
    public static void Open()
    {
        var window = GetWindow<SC2MapEditorWindow>("SC2 Map Editor");
        window.minSize = new Vector2(1100f, 640f);
        window.Show();
        SC2GameViewEditorBootstrap.EnsureGameViewUi();
    }

    private void OnEnable()
    {
        if (_context == null)
            _context = new SC2EditorContext();

        _context.EnsureDefaults();
        _sceneBridge = new SC2SceneViewBridge(_context, this);
        _sceneBridge.Enable();
        _terrainShell = ScriptableObject.CreateInstance<SC2TerrainShell>();
        _terrainShell.RepaintHost = () => Repaint();
        _costsEditor = new SC2CostsTerrainTypesEditor();
    }

    private void OnDisable()
    {
        if (_sceneBridge != null)
            _sceneBridge.Disable();

        if (_terrainShell != null)
        {
            _terrainShell.Dispose();
            DestroyImmediate(_terrainShell);
            _terrainShell = null;
        }

        if (_costsEditor != null)
        {
            _costsEditor.Dispose();
            _costsEditor = null;
        }

        _environmentEditor = null;
    }

    private void OnGUI()
    {
        SC2EditorStyles.EnsureReady();
        _context.EnsureDefaults();

        DrawAppShellHeader();
        DrawBody();
        HandleKeyboardShortcuts();
        DrawStatusBar();
    }

    private void DrawAppShellHeader()
    {
        EditorGUILayout.BeginVertical(SC2EditorStyles.HeaderBox, GUILayout.Height(HeaderHeight));

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("SC2 MAP EDITOR", EditorStyles.boldLabel, GUILayout.Width(SC2UiScale.Pxf(150f)));
        GUILayout.Label("Unified map cockpit", EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(128f)));

        GUILayout.Space(8f);
        DrawHeaderStatusPill("Map", _context.MapRoot != null ? _context.MapRoot.name : "None", 180f);
        DrawHeaderStatusPill("Raw", _context.MapRawData != null ? _context.MapRawData.name : "None", 180f);
        DrawHeaderStatusPill("Dirty", _context.DirtyFlags == SC2DirtyFlags.None ? "Clean" : _context.DirtyFlags.ToString(), 210f);

        GUILayout.FlexibleSpace();
        _context.SceneBridgeEnabled = GUILayout.Toggle(_context.SceneBridgeEnabled, "Scene Bridge", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(100f)));
        _context.CustomNavigationEnabled = GUILayout.Toggle(_context.CustomNavigationEnabled, "Unity Nav", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(90f)));
        _context.DrawViewportHud = GUILayout.Toggle(_context.DrawViewportHud, "HUD", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(44f)));
        if (GUILayout.Button("Game View UI", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(94f))))
            SC2GameViewEditorBootstrap.EnsureGameViewUi();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("File", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(54f))))
            ShowFileMenu();
        if (GUILayout.Button("Edit", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(54f))))
            ShowEditMenu();
        if (GUILayout.Button("View", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(56f))))
            ShowViewMenu();
        if (GUILayout.Button("Tools", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(58f))))
            ShowToolsMenu();
        if (GUILayout.Button("Help", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(56f))))
            ShowHelpMenu();

        GUILayout.Space(10f);
        GUILayout.Label("Tool", EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(28f)));
        DrawViewportToolStrip();

        GUILayout.FlexibleSpace();
        GUILayout.Label("Viewport", EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(54f)));
        EditorGUILayout.EndHorizontal();

        var nextTab = (SC2EditorMainTab)GUILayout.Toolbar((int)_context.MainTab, SC2EditorTabs.MainTabs, SC2EditorStyles.TabButton);
        _context.SetMainTab(nextTab);

        var subtabs = SC2EditorTabs.GetSubtabs(_context.MainTab);
        if (subtabs.Length > 1 || _context.MainTab != SC2EditorMainTab.Objects && _context.MainTab != SC2EditorMainTab.Scenario)
            _context.SubTabIndex = GUILayout.Toolbar(Mathf.Clamp(_context.SubTabIndex, 0, subtabs.Length - 1), subtabs, SC2EditorStyles.TabButton);

        EditorGUILayout.EndVertical();
    }

    private static void DrawHeaderStatusPill(string label, string value, float width)
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbarButton, GUILayout.Width(width));
        GUILayout.Label(label + ":", EditorStyles.miniBoldLabel, GUILayout.Width(SC2UiScale.Pxf(40f)));
        GUILayout.Label(value, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBody()
    {
        var bodyRect = new Rect(0f, HeaderHeight, position.width, position.height - HeaderHeight - StatusBarHeight);
        var leftRect = new Rect(bodyRect.x, bodyRect.y, LeftPanelWidth, bodyRect.height);
        var mainRect = new Rect(bodyRect.x + LeftPanelWidth, bodyRect.y, bodyRect.width - LeftPanelWidth, bodyRect.height);

        GUILayout.BeginArea(leftRect, SC2EditorStyles.PanelBox);
        _leftScroll = EditorGUILayout.BeginScrollView(_leftScroll);
        DrawContextPanel();
        DrawOverlayPanel();
        DrawViewportPanel();
        EditorGUILayout.EndScrollView();
        GUILayout.EndArea();

        GUILayout.BeginArea(mainRect);
        _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
        DrawActiveTabPanel();
        EditorGUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawContextPanel()
    {
        EditorGUILayout.LabelField("Context", SC2EditorStyles.SectionHeader);
        _context.MapRawData = (SC2MapRawData)EditorGUILayout.ObjectField("Map Raw Data", _context.MapRawData, typeof(SC2MapRawData), false);
        _context.MapRoot = (GameObject)EditorGUILayout.ObjectField("Map Root", _context.MapRoot, typeof(GameObject), true);
        _context.ActiveTerrain = (GameObject)EditorGUILayout.ObjectField("Active Terrain", _context.ActiveTerrain, typeof(GameObject), true);
        _context.SelectedAsset = EditorGUILayout.ObjectField("Selected Asset", _context.SelectedAsset, typeof(Object), false);

        if (GUILayout.Button("Use Selection as Map Root"))
        {
            _context.MapRoot = Selection.activeGameObject;
            _context.StatusMessage = _context.MapRoot != null ? "Map root assigned from selection" : "No selected GameObject";
        }

        if (GUILayout.Button("Focus Selection/Map Root"))
            SceneView.lastActiveSceneView?.FrameSelected();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Overlay Layers", SC2EditorStyles.SectionHeader);
        _context.OverlayTerrain = EditorGUILayout.ToggleLeft("Terrain", _context.OverlayTerrain);
        _context.OverlayHfield = EditorGUILayout.ToggleLeft("Hfield", _context.OverlayHfield);
        _context.OverlayCosts = EditorGUILayout.ToggleLeft("Costs", _context.OverlayCosts);
        _context.OverlayCollision2 = EditorGUILayout.ToggleLeft("Collision2", _context.OverlayCollision2);
        _context.OverlayProps = EditorGUILayout.ToggleLeft("Props", _context.OverlayProps);
        _context.OverlayMarkers = EditorGUILayout.ToggleLeft("Markers", _context.OverlayMarkers);
        _context.OverlayUnits = EditorGUILayout.ToggleLeft("Units", _context.OverlayUnits);
        _context.OverlayGraphLand = EditorGUILayout.ToggleLeft("Path: Land", _context.OverlayGraphLand);
        _context.OverlayGraphAir = EditorGUILayout.ToggleLeft("Path: Air", _context.OverlayGraphAir);
        _context.OverlayGraphNaval = EditorGUILayout.ToggleLeft("Path: Naval", _context.OverlayGraphNaval);
        _context.OverlayGraphAmphibious = EditorGUILayout.ToggleLeft("Path: Amphibious", _context.OverlayGraphAmphibious);
        _context.OverlayChains = EditorGUILayout.ToggleLeft("Chains", _context.OverlayChains);
        _context.OverlayAreas = EditorGUILayout.ToggleLeft("Areas", _context.OverlayAreas);
        _context.OverlayWater = EditorGUILayout.ToggleLeft("Water", _context.OverlayWater);
        _context.OverlayRNM = EditorGUILayout.ToggleLeft("RNM", _context.OverlayRNM);
        EditorGUILayout.Space();
    }

    private void DrawOverlayPanel()
    {
        SC2EditorStyles.DrawDivider();
        EditorGUILayout.LabelField("Overlay Layers", SC2EditorStyles.SectionHeader);
        _context.OverlayTerrain = EditorGUILayout.ToggleLeft("Terrain", _context.OverlayTerrain);
        _context.OverlayHfield = EditorGUILayout.ToggleLeft("Hfield", _context.OverlayHfield);
        _context.OverlayCosts = EditorGUILayout.ToggleLeft("Costs", _context.OverlayCosts);
        _context.OverlayCollision2 = EditorGUILayout.ToggleLeft("Collision2", _context.OverlayCollision2);
        _context.OverlayProps = EditorGUILayout.ToggleLeft("Props", _context.OverlayProps);
        _context.OverlayMarkers = EditorGUILayout.ToggleLeft("Markers", _context.OverlayMarkers);
        _context.OverlayUnits = EditorGUILayout.ToggleLeft("Units", _context.OverlayUnits);
        _context.OverlayGraphLand = EditorGUILayout.ToggleLeft("Path: Land", _context.OverlayGraphLand);
        _context.OverlayGraphAir = EditorGUILayout.ToggleLeft("Path: Air", _context.OverlayGraphAir);
        _context.OverlayGraphNaval = EditorGUILayout.ToggleLeft("Path: Naval", _context.OverlayGraphNaval);
        _context.OverlayGraphAmphibious = EditorGUILayout.ToggleLeft("Path: Amphibious", _context.OverlayGraphAmphibious);
        _context.OverlayChains = EditorGUILayout.ToggleLeft("Chains", _context.OverlayChains);
        _context.OverlayAreas = EditorGUILayout.ToggleLeft("Areas", _context.OverlayAreas);
        _context.OverlayWater = EditorGUILayout.ToggleLeft("Water", _context.OverlayWater);
        _context.OverlayRNM = EditorGUILayout.ToggleLeft("RNM", _context.OverlayRNM);
        EditorGUILayout.Space();
    }

    private void DrawViewportPanel()
    {
        SC2EditorStyles.DrawDivider();
        EditorGUILayout.LabelField("Viewport Tools", SC2EditorStyles.SectionHeader);
        DrawViewportToolStrip();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Rect")) _context.ViewportTool = SC2ViewportTool.Rect;
        if (GUILayout.Button("Paint")) _context.ViewportTool = SC2ViewportTool.Paint;
        if (GUILayout.Button("Measure")) _context.ViewportTool = SC2ViewportTool.Measure;
        EditorGUILayout.EndHorizontal();

        _context.SelectByClick = EditorGUILayout.ToggleLeft("Select by viewport click", _context.SelectByClick);
        _context.UseTransformHandles = EditorGUILayout.ToggleLeft("Draw transform handles", _context.UseTransformHandles);
        EditorGUILayout.HelpBox("Navigation: Alt+LMB orbit, MMB/Alt+RMB pan, wheel zoom, F focus. Unity built-in scene navigation remains available.", MessageType.None);
        EditorGUILayout.Space();
    }

    private void DrawViewportToolStrip()
    {
        if (GUILayout.Toggle(_context.ViewportTool == SC2ViewportTool.Select, "Select", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(58f))))
            _context.ViewportTool = SC2ViewportTool.Select;
        if (GUILayout.Toggle(_context.ViewportTool == SC2ViewportTool.Move, "Move", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(58f))))
            _context.ViewportTool = SC2ViewportTool.Move;
        if (GUILayout.Toggle(_context.ViewportTool == SC2ViewportTool.Rotate, "Rotate", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(64f))))
            _context.ViewportTool = SC2ViewportTool.Rotate;
        if (GUILayout.Toggle(_context.ViewportTool == SC2ViewportTool.Scale, "Scale", SC2EditorStyles.MiniStatus, GUILayout.Width(SC2UiScale.Pxf(58f))))
            _context.ViewportTool = SC2ViewportTool.Scale;
    }

    private void DrawActiveTabPanel()
    {
        EditorGUILayout.LabelField(_context.MainTab + " / " + _context.ActiveSubtabName, EditorStyles.largeLabel);
        EditorGUILayout.Space();

        switch (_context.MainTab)
        {
            case SC2EditorMainTab.Terrain:
                DrawTerrainPanel();
                break;
            case SC2EditorMainTab.Costs:
                DrawCostsPanel();
                break;
            case SC2EditorMainTab.Collision2:
                DrawCollisionPanel();
                break;
            case SC2EditorMainTab.Objects:
                DrawObjectsPanel();
                break;
            case SC2EditorMainTab.Environment:
                DrawEnvironmentPanel();
                break;
            case SC2EditorMainTab.Scenario:
                DrawScenarioPanel();
                break;
            case SC2EditorMainTab.Import:
                DrawImportPanel();
                break;
            case SC2EditorMainTab.Export:
                DrawExportPanel();
                break;
            case SC2EditorMainTab.Diagnostics:
                DrawDiagnosticsPanel();
                break;
        }
    }

    private void DrawTerrainPanel()
    {
        if (_terrainShell == null)
        {
            _terrainShell = ScriptableObject.CreateInstance<SC2TerrainShell>();
            _terrainShell.RepaintHost = () => Repaint();
        }

        // Wire active terrain from context
        if (_context.ActiveTerrain != null && _terrainShell.Data == null)
        {
            var terrainData = _context.ActiveTerrain.GetComponentInChildren<SC2TerrainData>();
            if (terrainData != null)
                _terrainShell.SetData(terrainData);
        }

        _terrainShell.OnGUI();
    }

    private void DrawCostsPanel()
    {
        if (_costsEditor == null)
            _costsEditor = new SC2CostsTerrainTypesEditor();

        if (!_costsEditor.IsLoaded)
        {
            EditorGUILayout.HelpBox("No costs/hfield data loaded yet. Assign Map Raw Data in the Context panel, then Load.", MessageType.Info);
            EditorGUI.BeginDisabledGroup(_context.MapRawData == null);
            if (GUILayout.Button("Load Costs + Hfield", GUILayout.Height(SC2UiScale.Pxf(28f))))
                _costsEditor.Load(_context.MapRawData);
            EditorGUI.EndDisabledGroup();
            return;
        }

        var subtabs = SC2EditorTabs.CostsTabs;
        var subtab = subtabs[Mathf.Clamp(_context.SubTabIndex, 0, subtabs.Length - 1)];
        switch (subtab)
        {
            case "Overview": DrawCostsOverview(); break;
            case "Unit Layer": DrawCostsUnitLayer(); break;
            case "Auto Generate": DrawCostsAutoGenerate(); break;
            case "Paint Overrides": DrawCostsPaintOverrides(); break;
            case "Terrain Types": DrawTerrainTypesPaint(); break;
            case "Islands/Validation": DrawCostsIslandsValidation(); break;
            default: EditorGUILayout.HelpBox("Unknown subtab.", MessageType.Warning); break;
        }
    }

    private void DrawCostsOverview()
    {
        EditorGUILayout.LabelField("Overview", SC2EditorStyles.SectionHeader);
        EditorGUILayout.LabelField("Source", _context.MapRawData != null ? _context.MapRawData.name : "None");
        EditorGUILayout.LabelField("Costs blocks", SC2CostsTerrainTypesEditor.AllBlockNames.Length + " (" + string.Join(", ", SC2CostsTerrainTypesEditor.AllBlockNames) + ")");
        EditorGUILayout.HelpBox(
            "Always writes all 5 blocks (Land_Small/Medium/Large, Water_Small/Large) — there is " +
            "no automatic detection yet of whether this map actually has playable water, so dry " +
            "maps do NOT get reduced to the vanilla 3-block profile. Terrain Types is a separate " +
            "layer stored inside hfield.bdf, not costs.bdf — see the Terrain Types subtab.", MessageType.Warning);

        var symmetry = SC2CostsTerrainTypesEditor.FindSymmetrySettings(_context.MapRoot);
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Global Symmetry", SC2EditorStyles.SectionHeader);
        if (symmetry != null)
        {
            EditorGUILayout.LabelField("Mode", symmetry.Mode.ToString());
            EditorGUILayout.LabelField("Center", symmetry.Center.ToString());
            EditorGUILayout.HelpBox("Costs and Terrain Types painting mirror through this same SC2SymmetrySettings component used by markers/props — edit it here or via the Objects symmetry tool.", MessageType.None);
        }
        else
        {
            EditorGUILayout.HelpBox("No SC2SymmetrySettings found on Map Root. Painting will be unsymmetrical until one is added.", MessageType.Info);
            EditorGUI.BeginDisabledGroup(_context.MapRoot == null);
            if (GUILayout.Button("Add Symmetry Settings To Map Root"))
                SC2CostsTerrainTypesEditor.EnsureSymmetrySettings(_context.MapRoot);
            EditorGUI.EndDisabledGroup();
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Reload From Disk", GUILayout.Height(SC2UiScale.Pxf(24f))))
            _costsEditor.Load(_context.MapRawData);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Debug", SC2EditorStyles.SectionHeader);
        _costsEditor.DebugSaveCostsImages = EditorGUILayout.ToggleLeft(
            "Save debug PNGs on Save (navmesh + islandmap per block, terrain types)",
            _costsEditor.DebugSaveCostsImages);
        EditorGUILayout.HelpBox("Writes into a CostsDebug/ subfolder next to costs.bytes — exactly what was just serialized, for visual verification.", MessageType.None);
    }

    private void DrawCostsUnitLayer()
    {
        EditorGUILayout.LabelField("Select Movement-Cost Layer", SC2EditorStyles.SectionHeader);
        var names = SC2CostsTerrainTypesEditor.AllBlockNames;
        _costsEditor.AllBlocks = EditorGUILayout.ToggleLeft("Paint all blocks at once", _costsEditor.AllBlocks);
        EditorGUI.BeginDisabledGroup(_costsEditor.AllBlocks);
        var next = GUILayout.Toolbar(Mathf.Clamp(_costsEditor.ActiveBlock, 0, names.Length - 1), names);
        if (next != _costsEditor.ActiveBlock)
            _costsEditor.SetActiveBlock(next);
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "Land_Small_water_Surface_ShoreLine, Land_medium, Land_Large_water_Abyss_ShoreLine, " +
            "Land_Large_water_Seafloor, Land_Medium_Water_Surface are the vanilla naming for these " +
            "5 slots on water-capable maps — this project keeps the shorter Land_*/Water_* labels " +
            "internally; the underlying costs.bdf block order/count is unaffected.", MessageType.None);
    }

    private void DrawCostsAutoGenerate()
    {
        EditorGUILayout.LabelField("Auto Generate", SC2EditorStyles.SectionHeader);
        EditorGUILayout.HelpBox(
            "SC2CostsAutoGen.GenerateNavmesh is currently a placeholder (returns all-passable) — " +
            "real slope/water-depth based generation from hfield is not implemented yet. Use Paint " +
            "Overrides to hand-paint impassable areas until that lands.", MessageType.Warning);
        if (GUILayout.Button("Run Auto-Generate (all blocks)", GUILayout.Height(SC2UiScale.Pxf(28f))))
            _costsEditor.AutoGenerateCosts();
    }

    private void DrawCostsPaintOverrides()
    {
        _costsEditor.SetActiveTarget(SC2CostsTerrainTypesEditor.PaintTarget.Costs);
        EditorGUILayout.LabelField("Paint Overrides", SC2EditorStyles.SectionHeader);

        var painting = GUILayout.Toggle(_costsEditor.IsPainting, _costsEditor.IsPainting ? "■ Painting ACTIVE (click terrain in Scene view)" : "▶ Enable Painting", "Button", GUILayout.Height(SC2UiScale.Pxf(26f)));
        if (painting != _costsEditor.IsPainting)
            _costsEditor.SetPainting(painting, _context.MapRoot);

        _costsEditor.CostsMode = (SC2CostsTerrainTypesEditor.CostsPaintModePublic)EditorGUILayout.Popup("Brush Mode", (int)_costsEditor.CostsMode, new[] { "Impassable (255)", "Passable (1)" });
        _costsEditor.BrushRadius = EditorGUILayout.IntSlider("Brush Radius", _costsEditor.BrushRadius, 1, 64);
        _costsEditor.AllBlocks = EditorGUILayout.ToggleLeft("All blocks at once", _costsEditor.AllBlocks);
        if (!_costsEditor.AllBlocks)
            _costsEditor.SetActiveBlock(EditorGUILayout.Popup("Block", _costsEditor.ActiveBlock, SC2CostsTerrainTypesEditor.AllBlockNames));

        DrawUndoRedoRow();

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Save Costs + Hfield", GUILayout.Height(SC2UiScale.Pxf(28f))))
            _costsEditor.Save();
    }

    private void DrawTerrainTypesPaint()
    {
        _costsEditor.SetActiveTarget(SC2CostsTerrainTypesEditor.PaintTarget.TerrainTypes);
        EditorGUILayout.LabelField("Terrain Types", SC2EditorStyles.SectionHeader);
        EditorGUILayout.HelpBox(
            "Paints hfield.bdf's TerrainTypes grid. These presets are editable labels/colors, not a " +
            "confirmed vanilla-game id table — rename/recolor them to match whatever surface-type " +
            "convention this map actually needs.", MessageType.Info);

        var painting = GUILayout.Toggle(_costsEditor.IsPainting, _costsEditor.IsPainting ? "■ Painting ACTIVE (click terrain in Scene view)" : "▶ Enable Painting", "Button", GUILayout.Height(SC2UiScale.Pxf(26f)));
        if (painting != _costsEditor.IsPainting)
            _costsEditor.SetPainting(painting, _context.MapRoot);

        _costsEditor.BrushRadius = EditorGUILayout.IntSlider("Brush Radius", _costsEditor.BrushRadius, 1, 64);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Palette", EditorStyles.miniBoldLabel);
        var presets = _costsEditor.TerrainTypePresets;
        for (var i = 0; i < presets.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            var selected = GUILayout.Toggle(_costsEditor.ActiveTerrainTypePreset == i, "", GUILayout.Width(SC2UiScale.Pxf(18f)));
            if (selected && _costsEditor.ActiveTerrainTypePreset != i)
                _costsEditor.SetActiveTerrainTypePreset(i);
            var swatch = GUILayoutUtility.GetRect(16f, 16f, GUILayout.Width(SC2UiScale.Pxf(16f)));
            EditorGUI.DrawRect(swatch, presets[i].Color);
            presets[i].Label = EditorGUILayout.TextField(presets[i].Label);
            EditorGUILayout.LabelField("id " + presets[i].Id, GUILayout.Width(SC2UiScale.Pxf(40f)));
            EditorGUILayout.EndHorizontal();
        }

        DrawUndoRedoRow();

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Save Costs + Hfield", GUILayout.Height(SC2UiScale.Pxf(28f))))
            _costsEditor.Save();
    }

    private void DrawCostsIslandsValidation()
    {
        EditorGUILayout.LabelField("Islands / Validation", SC2EditorStyles.SectionHeader);
        if (GUILayout.Button("Recompute Islands (all blocks)", GUILayout.Height(SC2UiScale.Pxf(28f))))
            _costsEditor.AutoGenerateIslandsOnly();

        EditorGUILayout.Space(4);
        var names = SC2CostsTerrainTypesEditor.AllBlockNames;
        for (var b = 0; b < _costsEditor.BlockCount; b++)
        {
            var label = b < names.Length ? names[b] : ("Block_" + b);
            EditorGUILayout.LabelField(label, _costsEditor.CountIslands(b) + " island(s)");
        }
    }

    private void DrawUndoRedoRow()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(_costsEditor.UndoCount == 0);
        if (GUILayout.Button("↩ Undo (" + _costsEditor.UndoCount + ")"))
            _costsEditor.Undo();
        EditorGUI.EndDisabledGroup();
        EditorGUI.BeginDisabledGroup(_costsEditor.RedoCount == 0);
        if (GUILayout.Button("↪ Redo (" + _costsEditor.RedoCount + ")"))
            _costsEditor.Redo();
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();
    }

    private BdfIO.Formats.Collision2.Collision2Model _lastCollision2Model;
    private string _lastCollision2Path;
    private string _lastCollision2Error;

    private void DrawCollisionPanel()
    {
        EditorGUILayout.LabelField("Overview", SC2EditorStyles.SectionHeader);
        EditorGUILayout.HelpBox(
            "collision2.bdf is auto-generated from the hfield during Export (SC2TerrainExporter.BuildCollision2Model) — " +
            "an error-adaptive quadtree mesh (concentrates the ~2500-triangle budget on steep/high-relief areas instead " +
            "of spending it uniformly), wrapped in a single flat EDB leaf node (real vanilla collision2.bdf files never " +
            "carry an actual BSP tree — checked against 3 real maps). There is no separate manual generation step here; " +
            "use the Export tab. This panel can load and validate the last exported file.",
            MessageType.Info);

        EditorGUILayout.LabelField("Validate Exported File", SC2EditorStyles.SectionHeader);
        EditorGUILayout.BeginVertical(SC2EditorStyles.PanelBox);

        if (GUILayout.Button("Load && Validate Last Exported collision2.bdf", GUILayout.Height(SC2UiScale.Pxf(24f))))
            LoadAndValidateCollision2();

        if (!string.IsNullOrEmpty(_lastCollision2Error))
        {
            EditorGUILayout.HelpBox(_lastCollision2Error, MessageType.Error);
        }
        else if (_lastCollision2Model != null)
        {
            var triCount = _lastCollision2Model.Triangles.Count;
            EditorGUILayout.LabelField("File", _lastCollision2Path);
            EditorGUILayout.LabelField("Vertices", _lastCollision2Model.Vertices.Count.ToString());
            EditorGUILayout.LabelField("Triangles", $"{triCount} / 2500 {(triCount <= 2500 ? "(OK)" : "(OVER BUDGET)")}");
            EditorGUILayout.LabelField("EDB Nodes", _lastCollision2Model.Nodes.Count.ToString());
            EditorGUILayout.LabelField("MapCenter", $"({_lastCollision2Model.MapCenter[0]:F1}, {_lastCollision2Model.MapCenter[1]:F1}, {_lastCollision2Model.MapCenter[2]:F1})");
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.LabelField("Manual Editing (low priority)", SC2EditorStyles.SectionHeader);
        EditorGUILayout.BeginVertical(SC2EditorStyles.PanelBox, GUILayout.Height(SC2UiScale.Pxf(60f)));
        EditorGUILayout.HelpBox("Manual in-Unity collision editing is not planned for MVP — the intended manual path is Blender import/export.", MessageType.None);
        if (GUILayout.Button("Export Mesh To Blender", GUILayout.Height(SC2UiScale.Pxf(24f))))
            Debug.Log("[SC2] Export collision mesh to Blender — stub");
        if (GUILayout.Button("Import Corrected Mesh", GUILayout.Height(SC2UiScale.Pxf(24f))))
            Debug.Log("[SC2] Import corrected collision mesh — stub");
        EditorGUILayout.EndVertical();
    }

    private void LoadAndValidateCollision2()
    {
        _lastCollision2Model = null;
        _lastCollision2Error = null;
        _lastCollision2Path = null;

        var outputDir = _context.ExportPath;
        if (string.IsNullOrEmpty(outputDir) || !System.IO.Directory.Exists(outputDir))
        {
            _lastCollision2Error = "Set a valid Output Path on the Export tab first, then export at least once.";
            return;
        }

        var matches = System.IO.Directory.GetFiles(outputDir, "*.collision2.win.bdf", System.IO.SearchOption.AllDirectories);
        if (matches.Length == 0)
        {
            _lastCollision2Error = $"No *.collision2.win.bdf found under {outputDir}. Export the map first.";
            return;
        }

        try
        {
            var path = matches[0];
            var bytes = System.IO.File.ReadAllBytes(path);
            var container = BdfIO.Core.BdfContainer.Read(bytes, BdfIO.Formats.Collision2.Collision2Bdf.Padding);
            _lastCollision2Model = BdfIO.Formats.Collision2.Collision2Bdf.Parse(container);
            _lastCollision2Path = path;
        }
        catch (System.Exception ex)
        {
            _lastCollision2Error = $"Failed to parse: {ex.Message}";
            SC2Log.LogException(ex);
        }
    }

    private void DrawEnvironmentPanel()
    {
        if (_environmentEditor == null)
            _environmentEditor = new SC2EnvironmentEditor { RepaintHost = Repaint };

        // Auto-(re)load when the Context panel's Map Raw Data changes, same
        // pattern DrawCostsPanel already uses for _costsEditor — avoids a
        // stale info.bdf model lingering after the user swaps which map
        // they're editing.
        if (_context.MapRawData != null && !_environmentEditor.IsLoaded)
            _environmentEditor.Load(_context.MapRawData);

        // Same reasoning as the try/catch around _costsEditor's embedded
        // GUI: an exception here shouldn't blank the rest of the window.
        try
        {
            _environmentEditor.DrawEmbeddedGUI();
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
            EditorGUILayout.HelpBox("Environment panel error (see log): " + ex.Message, MessageType.Error);
        }
    }

    private void DrawScenarioPanel()
    {
        EditorGUILayout.LabelField("Scenario Definition", SC2EditorStyles.SectionHeader);
        EditorGUILayout.HelpBox("Scenario entry point is scenario.lua. It resolves map/save/script paths; export must produce .scd with scenario.lua as the entry file.", MessageType.Info);

        _scenarioLuaPath = EditorGUILayout.TextField("scenario.lua Path", _scenarioLuaPath);
        _scenarioMapPath = EditorGUILayout.TextField("Map/Save Path", _scenarioMapPath);
        _scenarioScriptPath = EditorGUILayout.TextField("Script Path", _scenarioScriptPath);

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Validate Scenario Files", GUILayout.Height(SC2UiScale.Pxf(28f))))
            ValidateScenarioFiles();
    }

    private void DrawDiagnosticsPanel()
    {
        EditorGUILayout.LabelField("Checks & Reports", SC2EditorStyles.SectionHeader);
        EditorGUILayout.HelpBox("Diagnostics tab is reserved for batch vanilla import, BDF parse reports, scene build checks, material path checks, and export validation.", MessageType.Info);

        EditorGUILayout.BeginVertical(SC2EditorStyles.PanelBox, GUILayout.Height(SC2UiScale.Pxf(200f)));
        if (GUILayout.Button("Batch Vanilla Import", GUILayout.Height(SC2UiScale.Pxf(28f))))
            SC2VanillaImportDiagnostics.RunFromMenu();
        if (GUILayout.Button("BDF Parse Report", GUILayout.Height(SC2UiScale.Pxf(28f))))
            Debug.Log("[SC2] BDF parse report — stub");
        if (GUILayout.Button("Scene Build Check", GUILayout.Height(SC2UiScale.Pxf(28f))))
            Debug.Log("[SC2] Scene build check — stub");
        if (GUILayout.Button("Material Path Check", GUILayout.Height(SC2UiScale.Pxf(28f))))
            Debug.Log("[SC2] Material path check — stub");
        if (GUILayout.Button("Export Validation", GUILayout.Height(SC2UiScale.Pxf(28f))))
            Debug.Log("[SC2] Export validation — stub");
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Log Access", SC2EditorStyles.SectionHeader);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Open Log File", GUILayout.Height(SC2UiScale.Pxf(28f))))
            EditorApplication.ExecuteMenuItem("SC2/Open Log File");
        if (GUILayout.Button("Reveal Log Folder", GUILayout.Height(SC2UiScale.Pxf(28f))))
            EditorApplication.ExecuteMenuItem("SC2/Reveal Log Folder");
        EditorGUILayout.EndHorizontal();
    }

    private void DrawObjectsPanel()
    {
        EditorGUILayout.HelpBox("Objects is one complex gameplay scene editor. Filters are editable so new object categories can be added without changing the shell.", MessageType.Info);

        if (_context.ObjectFilters.Count == 0)
            _context.ObjectFilters.Add("All");

        _context.ActiveObjectFilterIndex = GUILayout.Toolbar(
            Mathf.Clamp(_context.ActiveObjectFilterIndex, 0, _context.ObjectFilters.Count - 1),
            _context.ObjectFilters.ToArray());

        EditorGUILayout.BeginHorizontal();
        _newObjectFilter = EditorGUILayout.TextField(_newObjectFilter);
        if (GUILayout.Button("Add Filter", GUILayout.Width(SC2UiScale.Pxf(88f))) && !string.IsNullOrWhiteSpace(_newObjectFilter))
        {
            _context.ObjectFilters.Add(_newObjectFilter.Trim());
            _newObjectFilter = "";
        }
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginDisabledGroup(_context.ObjectFilters.Count <= 1);
        if (GUILayout.Button("Remove Active Filter"))
            _context.ObjectFilters.RemoveAt(Mathf.Clamp(_context.ActiveObjectFilterIndex, 0, _context.ObjectFilters.Count - 1));
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Default object height anchoring should use hfield. A separate height movement mode will handle explicit Y/height-offset edits.", MessageType.None);
    }

    private string _importScdPath = "";
    private bool _importUseShader;

    private void DrawImportPanel()
    {
        EditorGUILayout.LabelField("Existing .scd", SC2EditorStyles.SectionHeader);
        ScdImportWindow.DrawImportControls(ref _importScdPath, ref _importUseShader);
        _context.ImportPath = _importScdPath;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Not yet implemented", SC2EditorStyles.SectionHeader);
        EditorGUILayout.HelpBox("Blender-exported terrain.bdf import, custom geometry objects, and collision mesh import are planned but not wired up yet.", MessageType.Info);
    }

    private void DrawExportPanel()
    {
        DrawBridgeButtons("Open existing .scd exporter", "SC2/Export .scd...");
        _context.ExportPath = EditorGUILayout.TextField("Output Path", _context.ExportPath);
        _context.DirtyFlags = (SC2DirtyFlags)EditorGUILayout.EnumFlagsField("Dirty Flags", _context.DirtyFlags);

        var expandedDirtyFlags = SC2ExportDependencyGraph.ExpandStaleFlags(_context.DirtyFlags);
        var fileMasks = SC2ExportDependencyGraph.CreateDefaultFileMasks();
        EditorGUILayout.LabelField("Affected Files", EditorStyles.boldLabel);
        var affectedCount = 0;
        foreach (var pair in fileMasks)
        {
            if (!SC2ExportDependencyGraph.IsDirtyOrStale(expandedDirtyFlags, pair.Value))
                continue;

            affectedCount++;
            var staleReason = SC2ExportDependencyGraph.GetStaleReason(_context.DirtyFlags, pair.Value);
            EditorGUILayout.LabelField("- " + pair.Key + (string.IsNullOrEmpty(staleReason) ? "" : " (" + staleReason + ")"));
        }

        if (affectedCount == 0)
            EditorGUILayout.LabelField("No dirty files. Smart export can passthrough originals.");
    }

    private static void DrawBridgeButtons(string label, string menuItem)
    {
        if (GUILayout.Button(label))
            EditorApplication.ExecuteMenuItem(menuItem);
    }

    private void ValidateScenarioFiles()
    {
        var luaOk = !string.IsNullOrEmpty(_scenarioLuaPath) && System.IO.File.Exists(_scenarioLuaPath);
        var mapOk = string.IsNullOrEmpty(_scenarioMapPath) || System.IO.File.Exists(_scenarioMapPath);
        var scriptOk = string.IsNullOrEmpty(_scenarioScriptPath) || System.IO.File.Exists(_scenarioScriptPath);

        if (luaOk && mapOk && scriptOk)
            Debug.Log("[SC2] Scenario files validated successfully.");
        else
            Debug.LogWarning($"[SC2] Scenario validation failed — lua: {luaOk}, map: {mapOk}, script: {scriptOk}");
    }

    private void DrawStatusBar()
    {
        var rect = new Rect(0f, position.height - StatusBarHeight, position.width, StatusBarHeight);
        GUILayout.BeginArea(rect, SC2EditorStyles.PanelBox);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Tab: " + _context.MainTab + " / " + _context.ActiveSubtabName, EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(210f)));
        GUILayout.Label("Tool: " + _context.ViewportTool, EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(90f)));
        GUILayout.Label("Dirty: " + _context.DirtyFlags, EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(220f)));
        GUILayout.Label("Selected: " + (Selection.activeGameObject != null ? Selection.activeGameObject.name : "None"), EditorStyles.miniLabel, GUILayout.Width(SC2UiScale.Pxf(190f)));
        GUILayout.Label(_context.StatusMessage, EditorStyles.miniLabel);
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private static void ShowFileMenu()
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Import .scd..."), false, () => EditorApplication.ExecuteMenuItem("SC2/Import .scd..."));
        menu.AddItem(new GUIContent("Export .scd..."), false, () => EditorApplication.ExecuteMenuItem("SC2/Export .scd..."));
        menu.ShowAsContext();
    }

    private void ShowEditMenu()
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Select Tool"), _context.ViewportTool == SC2ViewportTool.Select, () => _context.ViewportTool = SC2ViewportTool.Select);
        menu.AddItem(new GUIContent("Move Tool"), _context.ViewportTool == SC2ViewportTool.Move, () => _context.ViewportTool = SC2ViewportTool.Move);
        menu.AddItem(new GUIContent("Rotate Tool"), _context.ViewportTool == SC2ViewportTool.Rotate, () => _context.ViewportTool = SC2ViewportTool.Rotate);
        menu.AddItem(new GUIContent("Scale Tool"), _context.ViewportTool == SC2ViewportTool.Scale, () => _context.ViewportTool = SC2ViewportTool.Scale);
        menu.ShowAsContext();
    }

    private void ShowViewMenu()
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Scene Bridge"), _context.SceneBridgeEnabled, () => _context.SceneBridgeEnabled = !_context.SceneBridgeEnabled);
        menu.AddItem(new GUIContent("Unity-like Navigation"), _context.CustomNavigationEnabled, () => _context.CustomNavigationEnabled = !_context.CustomNavigationEnabled);
        menu.AddItem(new GUIContent("Viewport HUD"), _context.DrawViewportHud, () => _context.DrawViewportHud = !_context.DrawViewportHud);
        menu.ShowAsContext();
    }

    private static void ShowToolsMenu()
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Scene Visibility"), false, () => EditorApplication.ExecuteMenuItem("SC2/Scene Visibility"));
        menu.ShowAsContext();
    }

    private static void ShowHelpMenu()
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Open Log File"), false, () => EditorApplication.ExecuteMenuItem("SC2/Open Log File"));
        menu.AddItem(new GUIContent("Reveal Log Folder"), false, () => EditorApplication.ExecuteMenuItem("SC2/Reveal Log Folder"));
        menu.ShowAsContext();
    }

    private void HandleKeyboardShortcuts()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
            return;

        // Skip shortcuts if any control has focus (includes TextField inside terrain panel)
        if (GUIUtility.keyboardControl != 0)
            return;

        // Allow terrain shell to intercept shortcuts when in Terrain tab
        if (_context.MainTab == SC2EditorMainTab.Terrain && _terrainShell != null)
        {
            if (_terrainShell.HandleKeyboardShortcut(e))
            {
                e.Use();
                return;
            }
        }

        var tabs = SC2EditorTabs.MainTabs;
        var currentIdx = (int)_context.MainTab;

        if (e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha9)
        {
            var idx = e.keyCode - KeyCode.Alpha0;
            if (idx < tabs.Length)
            {
                _context.SetMainTab((SC2EditorMainTab)idx);
                e.Use();
            }
        }
        else if (e.keyCode == KeyCode.Tab)
        {
            int next;
            if (e.shift)
                next = (currentIdx - 1 + tabs.Length) % tabs.Length;
            else
                next = (currentIdx + 1) % tabs.Length;

            _context.SetMainTab((SC2EditorMainTab)next);
            Repaint();
            e.Use();
        }
    }

    private void OnSceneGUI()
    {
        if (_terrainShell != null && _context.MainTab == SC2EditorMainTab.Terrain)
            _terrainShell.OnSceneGUI(SceneView.lastActiveSceneView);

        if (_costsEditor != null && _costsEditor.IsLoaded && _context.MainTab == SC2EditorMainTab.Costs)
            _costsEditor.OnSceneGUI(SceneView.lastActiveSceneView, _context.MapRoot);
    }

    private void Update()
    {
        if (_terrainShell != null && _context.MainTab == SC2EditorMainTab.Terrain)
            _terrainShell.Update();
    }
}
