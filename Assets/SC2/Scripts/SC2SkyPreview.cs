using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>
/// The live sky of the Game View: the SkyMap cubemap of the Environment (info.bdf) drawn behind everything as the camera's skybox
/// with the game's own sky shader (SC2/Sky, effects/sky.hlsl). With no SkyMap, or none that can be found, the camera keeps what it
/// had. Ticked every frame by the terrain shell, like <see cref="SC2WaterPreview"/>.
/// </summary>
public static class SC2SkyPreview
{
    /// <summary>How long a sky texture that could not be found is left alone before the next try: finding it walks the texture
    /// folders and the game archive, which is too much work for every frame.</summary>
    private const float RetrySeconds = 3f;

    private static Material _material;
    private static string _resolvedPath = "\0";
    private static Cubemap _cubemap;
    private static float _nextRetryTime;

    private static Camera _camera;
    private static CameraClearFlags _savedClearFlags;
    private static Material _savedSkybox;
    private static bool _applied;

    public static void Ensure(SC2TerrainData data, SC2MapSession session, bool visible = true)
    {
        if (data == null || !data.IsInitialized || !visible)
        {
            Restore();
            return;
        }

        var info = SC2TerrainExporter.BuildInfoModel(data, session, quiet: true);
        var cubemap = Resolve(info.SkyMap);
        var camera = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
        if (cubemap == null || camera == null)
        {
            Restore();
            return;
        }
        Apply(camera, cubemap);
    }

    /// <summary>Makes <paramref name="cubemap"/> the sky of <paramref name="camera"/>.</summary>
    public static void Apply(Camera camera, Cubemap cubemap)
    {
        if (_material == null)
        {
            var shader = Resources.Load<Shader>("SC2Sky") ?? Shader.Find("SC2/Sky");
            if (shader == null) return;
            _material = new Material(shader) { name = "mat_Sky", hideFlags = HideFlags.HideAndDontSave };
        }
        _material.SetTexture("_SkyMap", cubemap);
        // The cubemap of a DDS is not flagged sRGB, so in a linear project its values would be taken for linear light.
        _material.SetFloat("_SkyDecode", QualitySettings.activeColorSpace == ColorSpace.Linear && !cubemap.isDataSRGB ? 1f : 0f);

        if (_applied && _camera != camera) Restore();
        if (!_applied)
        {
            _camera = camera;
            _savedClearFlags = camera.clearFlags;
            _savedSkybox = RenderSettings.skybox;
            _applied = true;
        }
        // Set on every tick: another system may have changed either since.
        if (RenderSettings.skybox != _material) RenderSettings.skybox = _material;
        if (camera.clearFlags != CameraClearFlags.Skybox) camera.clearFlags = CameraClearFlags.Skybox;
    }

    /// <summary>Gives the camera back what it had before the sky was applied.</summary>
    public static void Restore()
    {
        if (!_applied) return;
        if (_camera != null) _camera.clearFlags = _savedClearFlags;
        if (RenderSettings.skybox == _material) RenderSettings.skybox = _savedSkybox;
        _camera = null;
        _savedSkybox = null;
        _applied = false;
    }

    public static void Destroy()
    {
        Restore();
        if (_material != null) { Object.DestroyImmediate(_material); _material = null; }
        _cubemap = null;
        _resolvedPath = "\0";
    }

    private static Cubemap Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) { _resolvedPath = path ?? ""; _cubemap = null; return null; }
        if (path == _resolvedPath && (_cubemap != null || Time.realtimeSinceStartup < _nextRetryTime)) return _cubemap;

        _resolvedPath = path;
        _cubemap = SC2TerrainMaterialFactory.LoadTexturePublicAny(path) as Cubemap;
        if (_cubemap == null) _nextRetryTime = Time.realtimeSinceStartup + RetrySeconds;
        return _cubemap;
    }
}
