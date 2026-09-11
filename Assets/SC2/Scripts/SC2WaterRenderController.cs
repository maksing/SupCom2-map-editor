using UnityEngine;

/// <summary>
/// Supplies the scene textures that the SC2 engine binds before water.hlsl:
/// a linear scene depth texture.
///
/// The real-time planar reflection camera (a full second render
/// pass mirroring the source camera across the water plane every frame) was
/// removed, along with SC2Water.shader's own reflection
/// sampling (_SC2PlanarReflectionTexture, the static environment cubemap,
/// and the Unity reflection-probe fallback) - water no longer reflects the
/// terrain/scene at all, only the sun's own specular highlight remains.
/// This class now only tracks whether water is active and keeps the
/// source camera's depth texture mode enabled while it is.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class SC2WaterRenderController : MonoBehaviour
{
    [SerializeField] private bool waterActive;
    [SerializeField] private float waterElevation;

    private Camera _sourceCamera;

    public static void Ensure(float elevation)
    {
        var camera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
        if (camera == null)
            return;

        var controller = camera.GetComponent<SC2WaterRenderController>();
        if (controller == null)
            controller = camera.gameObject.AddComponent<SC2WaterRenderController>();

        controller.waterActive = true;
        controller.waterElevation = elevation;
        controller.EnsureDepthTexture();
    }

    public static void DisableForActiveCamera()
    {
        var camera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
        var controller = camera != null ? camera.GetComponent<SC2WaterRenderController>() : null;
        if (controller == null)
            return;

        controller.waterActive = false;
    }

    private void OnEnable()
    {
        _sourceCamera = GetComponent<Camera>();
        EnsureDepthTexture();
    }

    private void OnValidate()
    {
        EnsureDepthTexture();
    }

    private void EnsureDepthTexture()
    {
        if (_sourceCamera == null)
            _sourceCamera = GetComponent<Camera>();
        if (_sourceCamera != null)
            _sourceCamera.depthTextureMode |= DepthTextureMode.Depth;
    }

    private void OnPreCull()
    {
        if (!waterActive)
            return;

        EnsureDepthTexture();
    }
}
