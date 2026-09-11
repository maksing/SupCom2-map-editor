using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// SC2GameViewEditorUI lives in Scripts/Unified/ (runtime folder).
// Unity compiles Editor folder after Scripts, so the type is available here.
public static class SC2GameViewEditorBootstrap
{
    private const string RigName = "SC2 Game View Editor UI";

    [MenuItem("SC2 Map Editor/Show Game View UI")]
    public static Component EnsureGameViewUi()
    {
        var uiType = typeof(SC2GameViewEditorUI);
        var existing = UnityEngine.Object.FindFirstObjectByType(uiType) as Component;
        if (existing != null)
        {
            ConfigureDefaultCamera(existing.GetComponent<Camera>() ?? existing.GetComponentInParent<Camera>() ?? Camera.main);
            SetField(existing, "ShowUI", true);
            SetField(existing, "MapName", EditorSceneManager.GetActiveScene().name);
            SetField(existing, "Status", "Game View UI ready");
            Selection.activeGameObject = existing.gameObject;
            EditorGUIUtility.PingObject(existing.gameObject);
            return existing;
        }

        var camera = Camera.main;
        if (camera == null)
            camera = UnityEngine.Object.FindFirstObjectByType<Camera>();

        if (camera == null)
        {
            var cameraGo = new GameObject("Main Camera");
            Undo.RegisterCreatedObjectUndo(cameraGo, "Create SC2 Game View Camera");
            camera = cameraGo.AddComponent<Camera>();
            cameraGo.tag = "MainCamera";
        }

        ConfigureDefaultCamera(camera);

        var cameraObject = camera.gameObject;
        Undo.RecordObject(cameraObject, "Create SC2 Game View UI");
        cameraObject.name = string.IsNullOrEmpty(cameraObject.name) ? RigName : cameraObject.name;

        var ui = cameraObject.GetComponent(uiType);
        if (ui == null)
            ui = cameraObject.AddComponent(uiType);

        SetField(ui, "ShowUI", true);
        SetField(ui, "MapName", EditorSceneManager.GetActiveScene().name);
        SetField(ui, "Status", "Game View UI ready");

        Selection.activeGameObject = cameraObject;
        EditorGUIUtility.PingObject(cameraObject);
        if (!Application.isPlaying)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        return ui;
    }

    private static void ConfigureDefaultCamera(Camera camera)
    {
        if (camera == null)
            return;

        Undo.RecordObject(camera.transform, "Configure SC2 Game View Camera");
        Undo.RecordObject(camera, "Configure SC2 Game View Camera");

        camera.transform.position = new Vector3(512f, 512f, 512f);
        camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        camera.orthographic = false;
        camera.orthographicSize = 512f;
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 10000f;
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.usePhysicalProperties = false;
        camera.ResetAspect();
        camera.ResetCullingMatrix();
        camera.ResetProjectionMatrix();
    }

    private static void SetField(Component component, string fieldName, object value)
    {
        if (component == null)
            return;

        var field = component.GetType().GetField(fieldName);
        if (field != null)
            field.SetValue(component, value);
    }
}
