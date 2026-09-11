#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class SC2GameViewTerrainBridge
{
    private static SC2TerrainShell _shell;
    private static SC2GameViewEditorUI _host;
    private static bool _sceneSubscribed;
    private static bool _terrainActive;
    private static int _lastSubtab = -1;
    private static bool? _lastPaintRnmSubMode;

    public static void SetTerrainActive(bool active)
    {
        _terrainActive = active;
        if (active)
            EnsureSceneSubscription();
    }

    public static bool DrawTerrainInspector(SC2GameViewEditorUI host, int subtabIndex)
    {
        if (host == null)
            return false;

        _host = host;
        _terrainActive = true;
        EnsureShell();
        EnsureSceneSubscription();
        TryAdoptSelectedTerrainData();

        var mode = ModeFromUnifiedSubtab(subtabIndex);
        if (_lastSubtab != subtabIndex || _shell.Mode != mode)
        {
            _shell.SetMode(mode);
            _lastSubtab = subtabIndex;
        }
        if (mode == SC2TerrainShell.EditorMode.Paint)
        {
            var rnmSubMode = subtabIndex == 4;
            if (_lastPaintRnmSubMode != rnmSubMode)
            {
                _shell.SetPaintRnmSubMode(rnmSubMode);
                _lastPaintRnmSubMode = rnmSubMode;
            }
        }
        else
        {
            _lastPaintRnmSubMode = null;
        }

        using (new EditorGUILayout.VerticalScope())
        {
            _shell.OnEmbeddedGUI(host.PanelContentWidth);

            if (subtabIndex == 4)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.HelpBox(
                    "Paint Blend uses the existing terrain editor paint/runtime data. Blend alpha, shadow factor and RNM lighting bake are tracked as separate work items.",
                    MessageType.Info);
            }
        }

        return true;
    }

    public static void HandleGameViewInput(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        if (!_terrainActive || _shell == null || camera == null)
            return;

        _shell.OnGameViewGUI(camera, viewportRect, mousePosition);
    }

    public static void Release()
    {
        Shutdown();
    }

    private static void EnsureShell()
    {
        if (_shell != null)
            return;

        _shell = ScriptableObject.CreateInstance<SC2TerrainShell>();
        _shell.hideFlags = HideFlags.HideAndDontSave;
        _shell.RepaintHost = RepaintHost;
    }

    private static void TryAdoptSelectedTerrainData()
    {
        if (_shell == null || _shell.Data != null)
            return;

        var asset = Selection.activeObject as SC2TerrainData;
        if (asset != null)
        {
            _shell.SetData(asset);
            return;
        }

        // SC2TerrainData is a ScriptableObject asset, not a scene component.
        // Scene terrain objects are handled by the existing object field workflow.
    }

    private static void EnsureSceneSubscription()
    {
        if (_sceneSubscribed)
            return;

        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.update += EditorTick;
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        EditorApplication.quitting += Shutdown;
        _sceneSubscribed = true;
    }

    private static void Shutdown()
    {
        if (_sceneSubscribed)
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= EditorTick;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            _sceneSubscribed = false;
        }

        if (_shell != null)
        {
            _shell.Dispose();
            UnityEngine.Object.DestroyImmediate(_shell);
            _shell = null;
        }

        _host = null;
        _terrainActive = false;
        _lastSubtab = -1;
        _lastPaintRnmSubMode = null;
    }

    private static void OnSceneGUI(SceneView sceneView)
    {
        if (!_terrainActive || _shell == null || sceneView == null)
            return;

        _shell.OnSceneGUI(sceneView);
    }

    private static void EditorTick()
    {
        if (!_terrainActive || _shell == null)
            return;

        _shell.Update();
    }

    private static void RepaintHost()
    {
        if (_host != null)
        {
            _host.Status = "Terrain editor updated";
            SceneView.RepaintAll();
        }
    }

    private static SC2TerrainShell.EditorMode ModeFromUnifiedSubtab(int subtabIndex)
    {
        switch (subtabIndex)
        {
            case 0:
                return SC2TerrainShell.EditorMode.Geometry;
            case 1:
                return SC2TerrainShell.EditorMode.Materials;
            case 2:
                return SC2TerrainShell.EditorMode.Pairs;
            case 3:
                return SC2TerrainShell.EditorMode.Paint;
            case 4:
                return SC2TerrainShell.EditorMode.Paint;
            case 5:
                return SC2TerrainShell.EditorMode.Simplified;
            case 6:
                return SC2TerrainShell.EditorMode.Export;
            default:
                return SC2TerrainShell.EditorMode.Geometry;
        }
    }
}
#endif
