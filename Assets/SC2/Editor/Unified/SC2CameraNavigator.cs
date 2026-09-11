using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared camera navigation utilities for SC2 Editor.
/// Extracted from SC2SceneViewBridge and SC2GameViewEditorUI to eliminate duplication.
/// </summary>
public static class SC2CameraNavigator
{
    // ---- SceneView navigation (uses SceneView's internal rotation/pivot) ----

    public static void OrbitSceneView(SceneView sceneView, Vector2 delta)
    {
        var yaw = Quaternion.AngleAxis(delta.x * 0.35f, Vector3.up);
        var pitch = Quaternion.AngleAxis(delta.y * 0.35f, sceneView.rotation * Vector3.right);
        sceneView.rotation = yaw * pitch * sceneView.rotation;
    }

    public static void PanSceneView(SceneView sceneView, Vector2 delta)
    {
        var right = sceneView.rotation * Vector3.right;
        var up = sceneView.rotation * Vector3.up;
        var scale = Mathf.Max(0.01f, sceneView.size) * 0.0025f;
        sceneView.pivot += (-right * delta.x + up * delta.y) * scale;
    }

    public static void FlySceneView(SceneView sceneView, Event e)
    {
        var direction = Vector3.zero;
        if (e.keyCode == KeyCode.W) direction += sceneView.rotation * Vector3.forward;
        if (e.keyCode == KeyCode.S) direction -= sceneView.rotation * Vector3.forward;
        if (e.keyCode == KeyCode.D) direction += sceneView.rotation * Vector3.right;
        if (e.keyCode == KeyCode.A) direction -= sceneView.rotation * Vector3.right;
        if (e.keyCode == KeyCode.E) direction += Vector3.up;
        if (e.keyCode == KeyCode.Q) direction -= Vector3.up;

        if (direction == Vector3.zero)
            return;

        sceneView.pivot += direction.normalized * Mathf.Max(0.1f, sceneView.size * 0.08f);
        sceneView.Repaint();
        e.Use();
    }

    // ---- Camera navigation (world-space) ----

    public static void OrbitCamera(Camera cam, Vector2 delta)
    {
        var pivot = cam.transform.position + cam.transform.forward * 25f;
        cam.transform.RotateAround(pivot, Vector3.up, delta.x * 0.25f);
        cam.transform.RotateAround(pivot, cam.transform.right, -delta.y * 0.25f);
    }

    public static void PanCamera(Camera cam, Vector2 delta)
    {
        var scale = Mathf.Max(0.1f, Vector3.Distance(cam.transform.position, Vector3.zero)) * 0.0015f;
        cam.transform.position += (-cam.transform.right * delta.x + cam.transform.up * delta.y) * scale;
    }

    public static void ZoomCamera(Camera cam, float delta)
    {
        if (cam.orthographic)
            cam.orthographicSize = Mathf.Max(0.1f, cam.orthographicSize * (1f + delta * 0.06f));
        else
            cam.transform.position += cam.transform.forward * (-delta * 2f);
    }

    public static bool FlyCamera(Camera cam, KeyCode key)
    {
        var direction = Vector3.zero;
        if (key == KeyCode.W) direction += cam.transform.forward;
        if (key == KeyCode.S) direction -= cam.transform.forward;
        if (key == KeyCode.D) direction += cam.transform.right;
        if (key == KeyCode.A) direction -= cam.transform.right;
        if (key == KeyCode.E) direction += Vector3.up;
        if (key == KeyCode.Q) direction -= Vector3.up;

        if (direction == Vector3.zero)
            return false;

        cam.transform.position += direction.normalized * 2f;
        return true;
    }

    // ---- Focus ----

    public static void FocusOnSelection(SceneView sceneView)
    {
        var selected = Selection.activeGameObject;
        if (selected != null)
        {
            Selection.activeGameObject = selected;
            sceneView.FrameSelected();
            return;
        }
    }

    public static void FocusOnBounds(SceneView sceneView, GameObject root)
    {
        if (root == null)
            return;

        var bounds = CalculateBounds(root);
        sceneView.Frame(bounds, false);
    }

    public static void FocusCamera(Camera cam, GameObject go)
    {
        if (cam == null || go == null)
            return;

        var bounds = CalculateBounds(go);
        cam.transform.position = bounds.center - cam.transform.forward * Mathf.Max(20f, bounds.extents.magnitude * 2.5f);
    }

    // ---- Utilities ----

    public static Bounds CalculateBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(root.transform.position, Vector3.one * 20f);

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    public static Camera ResolveCamera(Transform context)
    {
        var cam = context?.GetComponent<Camera>();
        if (cam != null)
            return cam;

        cam = context?.GetComponentInParent<Camera>();
        if (cam != null)
            return cam;

        if (Camera.main != null)
            return Camera.main;

        return Object.FindFirstObjectByType<Camera>();
    }
}
