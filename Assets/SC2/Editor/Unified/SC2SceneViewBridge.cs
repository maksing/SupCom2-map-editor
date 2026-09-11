using UnityEditor;
using UnityEngine;

public sealed class SC2SceneViewBridge
{
    private readonly SC2EditorContext _context;
    private readonly EditorWindow _owner;
    private bool _subscribed;
    private bool _isOrbiting;
    private bool _isPanning;
    private bool _isFlying;
    private Vector2 _lastMousePosition;

    public SC2SceneViewBridge(SC2EditorContext context, EditorWindow owner)
    {
        _context = context;
        _owner = owner;
    }

    public void Enable()
    {
        if (_subscribed)
            return;

        SceneView.duringSceneGui += OnSceneGUI;
        _subscribed = true;
    }

    public void Disable()
    {
        if (!_subscribed)
            return;

        SceneView.duringSceneGui -= OnSceneGUI;
        _subscribed = false;
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!_context.SceneBridgeEnabled)
            return;

        SyncUnityTool();
        HandleNavigation(sceneView);
        HandleSelection();
        HandleTransformTools();

        if (_context.DrawViewportHud)
            DrawHud(sceneView);
    }

    private void SyncUnityTool()
    {
        switch (_context.ViewportTool)
        {
            case SC2ViewportTool.Move:
                Tools.current = Tool.Move;
                break;
            case SC2ViewportTool.Rotate:
                Tools.current = Tool.Rotate;
                break;
            case SC2ViewportTool.Scale:
                Tools.current = Tool.Scale;
                break;
            case SC2ViewportTool.Rect:
                Tools.current = Tool.Rect;
                break;
            case SC2ViewportTool.Select:
            case SC2ViewportTool.Paint:
            case SC2ViewportTool.Measure:
                Tools.current = Tool.None;
                break;
        }
    }

    private void HandleNavigation(SceneView sceneView)
    {
        if (!_context.CustomNavigationEnabled)
            return;

        var e = Event.current;
        if (e == null)
            return;

        // Ignore events from other EditorWindow GUIs (clicks on buttons/panels).
        if (e.type == EventType.MouseDown && GUIUtility.hotControl != 0)
            return;

        if (e.type == EventType.ScrollWheel)
        {
            sceneView.size = Mathf.Max(0.05f, sceneView.size * (1f + e.delta.y * 0.04f));
            sceneView.Repaint();
            e.Use();
            return;
        }

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F)
        {
            FocusSelectionOrMap(sceneView);
            e.Use();
            return;
        }

        if (e.type == EventType.MouseDown)
        {
            _lastMousePosition = e.mousePosition;
            _isOrbiting = e.alt && e.button == 0;
            _isPanning = e.button == 2 || (e.alt && e.button == 1);
            _isFlying = e.button == 1 && !e.alt;
        }

        if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)
        {
            _isOrbiting = false;
            _isPanning = false;
            _isFlying = false;
        }

        if (e.type == EventType.MouseDrag && (_isOrbiting || _isPanning))
        {
            var delta = e.mousePosition - _lastMousePosition;
            _lastMousePosition = e.mousePosition;

            if (_isOrbiting)
                Orbit(sceneView, delta);
            else
                Pan(sceneView, delta);

            sceneView.Repaint();
            e.Use();
            return;
        }

        if (e.type == EventType.KeyDown && _isFlying)
            Fly(sceneView, e);
    }

    private static void Orbit(SceneView sceneView, Vector2 delta)
    {
        SC2CameraNavigator.OrbitSceneView(sceneView, delta);
    }

    private static void Pan(SceneView sceneView, Vector2 delta)
    {
        SC2CameraNavigator.PanSceneView(sceneView, delta);
    }

    private static void Fly(SceneView sceneView, Event e)
    {
        SC2CameraNavigator.FlySceneView(sceneView, e);
    }

    private void FocusSelectionOrMap(SceneView sceneView)
    {
        var selected = Selection.activeGameObject;
        if (selected != null)
        {
            SC2CameraNavigator.FocusOnSelection(sceneView);
            return;
        }

        SC2CameraNavigator.FocusOnBounds(sceneView, _context.MapRoot);
    }

    private void HandleSelection()
    {
        if (!_context.SelectByClick || _context.ViewportTool != SC2ViewportTool.Select)
            return;

        var e = Event.current;
        if (e == null || e.type != EventType.MouseDown || e.button != 0 || e.alt)
            return;

        var controlId = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(controlId);

        var picked = HandleUtility.PickGameObject(e.mousePosition, false);
        if (picked == null)
            return;

        Selection.activeGameObject = picked;
        _context.StatusMessage = "Selected: " + picked.name;
        _owner.Repaint();
        e.Use();
    }

    private void HandleTransformTools()
    {
        if (!_context.UseTransformHandles)
            return;

        var selected = Selection.activeTransform;
        if (selected == null)
            return;

        EditorGUI.BeginChangeCheck();

        if (_context.ViewportTool == SC2ViewportTool.Move)
        {
            var next = Handles.PositionHandle(selected.position, selected.rotation);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(selected, "SC2 Move Object");
                selected.position = next;
                _context.StatusMessage = "Moved: " + selected.name;
            }
            return;
        }

        if (_context.ViewportTool == SC2ViewportTool.Rotate)
        {
            var next = Handles.RotationHandle(selected.rotation, selected.position);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(selected, "SC2 Rotate Object");
                selected.rotation = next;
                _context.StatusMessage = "Rotated: " + selected.name;
            }
            return;
        }

        if (_context.ViewportTool == SC2ViewportTool.Scale)
        {
            var next = Handles.ScaleHandle(selected.localScale, selected.position, selected.rotation, HandleUtility.GetHandleSize(selected.position));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(selected, "SC2 Scale Object");
                selected.localScale = next;
                _context.StatusMessage = "Scaled: " + selected.name;
            }
        }
    }

    private void DrawHud(SceneView sceneView)
    {
        Handles.BeginGUI();

        GUILayout.BeginArea(new Rect(10f, 10f, 360f, 112f), "SC2 Map Editor", EditorStyles.helpBox);
        GUILayout.Label("Tool: " + _context.ViewportTool + " | Tab: " + _context.MainTab + " / " + _context.ActiveSubtabName);
        GUILayout.Label("Overlays: " + _context.GetOverlaySummary(), EditorStyles.miniLabel);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Select", EditorStyles.miniButtonLeft)) _context.ViewportTool = SC2ViewportTool.Select;
        if (GUILayout.Button("Move", EditorStyles.miniButtonMid)) _context.ViewportTool = SC2ViewportTool.Move;
        if (GUILayout.Button("Rotate", EditorStyles.miniButtonMid)) _context.ViewportTool = SC2ViewportTool.Rotate;
        if (GUILayout.Button("Scale", EditorStyles.miniButtonRight)) _context.ViewportTool = SC2ViewportTool.Scale;
        GUILayout.EndHorizontal();
        if (Event.current.type == EventType.Repaint)
            Event.current.Use();

        GUILayout.Label("Nav: Alt+LMB orbit, MMB/Alt+RMB pan, RMB+WASD/QE fly, wheel zoom, F focus", EditorStyles.miniLabel);
        GUILayout.EndArea();

        Handles.EndGUI();
    }
}
