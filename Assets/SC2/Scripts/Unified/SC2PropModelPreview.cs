using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A small orbiting 3D view of one game-asset model, drawn into an IMGUI rect — used by the prop
/// browser to show what a prop looks like before it is picked. The model is drawn with
/// Graphics.DrawMesh on a private layer into a hidden camera's RenderTexture, so nothing is added
/// to the map scene and no other camera can see it; it is lit by the same scene light and ambient
/// as placed props because it uses their materials.
/// </summary>
public sealed class SC2PropModelPreview : IDisposable
{
    private const int PreviewLayer = 31;
    private const int TextureSize = 512;
    private const int MaxCachedModels = 8;
    private const float FieldOfView = 30f;
    private const float PitchDegrees = 18f;
    private const float AutoRotateDegreesPerSecond = 28f;
    private const float DragDegreesPerPixel = 0.6f;
    private const float KeyLightIntensity = 1.3f;
    private const float MinZoom = 0.5f;
    private const float MaxZoom = 3f;
    private static readonly Color BackgroundColor = new Color(0.2f, 0.22f, 0.26f, 1f);

    private sealed class CachedModel
    {
        public SC2GameAssetScenePreview.StandalonePreview Model;
        public string Error;
    }

    private readonly Dictionary<string, CachedModel> _cache = new Dictionary<string, CachedModel>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _cacheOrder = new LinkedList<string>();
    private Camera _camera;
    private RenderTexture _target;
    private CachedModel _current;
    private const float DefaultYaw = 30f;
    private float _yaw = DefaultYaw;
    private float _zoom = 1f;
    private int _dragControlId;

    /// <summary>Why the current asset has no model (null while it has one, or none is selected).</summary>
    public string Error => _current?.Error;
    public bool HasModel => _current?.Model != null;

    public void SetAsset(SC2GameAsset asset, string gameDataRoot)
    {
        if (asset == null) { _current = null; return; }
        string key = string.IsNullOrEmpty(asset.SourcePath) ? asset.Kind + ":" + asset.Id : asset.SourcePath;
        if (!_cache.TryGetValue(key, out var cached))
        {
            cached = new CachedModel();
            if (!SC2GameAssetScenePreview.TryBuildStandalonePreview(asset, gameDataRoot, out cached.Model, out cached.Error))
                cached.Model = null;
            _cache[key] = cached;
            _cacheOrder.AddLast(key);
            EvictOldest(key);
        }
        else
        {
            _cacheOrder.Remove(key);
            _cacheOrder.AddLast(key);
        }

        if (!ReferenceEquals(cached, _current))
        {
            _current = cached;
            _yaw = DefaultYaw;
            _zoom = 1f;
        }
    }

    /// <summary>Draws the model into `rect`. The caller reserves the rect in its layout; the
    /// render itself only happens on Repaint.</summary>
    public void Draw(Rect rect)
    {
        var e = Event.current;
        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.GetTypeForControl(controlId))
        {
            case EventType.MouseDown:
                if (e.button == 0 && rect.Contains(e.mousePosition))
                {
                    _dragControlId = controlId;
                    GUIUtility.hotControl = controlId;
                    e.Use();
                }
                break;
            case EventType.MouseDrag:
                if (_dragControlId == controlId && GUIUtility.hotControl == controlId)
                {
                    _yaw -= e.delta.x * DragDegreesPerPixel;
                    e.Use();
                }
                break;
            case EventType.MouseUp:
                if (_dragControlId == controlId && GUIUtility.hotControl == controlId)
                {
                    _dragControlId = 0;
                    GUIUtility.hotControl = 0;
                    e.Use();
                }
                break;
            case EventType.ScrollWheel:
                if (rect.Contains(e.mousePosition))
                {
                    _zoom = Mathf.Clamp(_zoom * (1f - e.delta.y * 0.06f), MinZoom, MaxZoom);
                    e.Use();
                }
                break;
        }

        if (e.type != EventType.Repaint) return;

        if (_current?.Model == null || _current.Model.Mesh == null)
        {
            GUI.Box(rect, GUIContent.none);
            return;
        }

        if (_dragControlId == 0) _yaw += AutoRotateDegreesPerSecond * Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        EnsureTarget();
        Render(_current.Model, _target, _yaw, _zoom);
        GUI.DrawTexture(rect, _target, ScaleMode.ScaleToFit, false);
    }

    /// <summary>Renders `model` once at the default angle into a new, caller-owned Texture2D (the
    /// caller destroys it) — a still thumbnail for a card, as opposed to the live view Draw
    /// produces. Must be called during a Repaint, like Draw.</summary>
    public Texture2D RenderThumbnail(SC2GameAssetScenePreview.StandalonePreview model, int width, int height)
    {
        // Multisampled temporary, read back to the CPU: each cached thumbnail is then just its
        // resolved pixels, and no Graphics.Blit runs inside OnGUI.
        var temporary = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 4);
        Render(model, temporary, DefaultYaw, 1f);
        var result = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "SC2PropThumbnail",
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var previousActive = RenderTexture.active;
        RenderTexture.active = temporary;
        result.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        result.Apply(false, false);
        RenderTexture.active = previousActive;
        RenderTexture.ReleaseTemporary(temporary);
        return result;
    }

    /// <summary>Frees every cached model plus the camera and render texture; the next SetAsset
    /// rebuilds what it needs.</summary>
    public void Dispose()
    {
        foreach (var cached in _cache.Values) cached.Model?.Dispose();
        _cache.Clear();
        _cacheOrder.Clear();
        _current = null;
        if (_camera != null) DestroyObject(_camera.gameObject);
        _camera = null;
        if (_target != null) { _target.Release(); DestroyObject(_target); }
        _target = null;
    }

    private void EvictOldest(string keepKey)
    {
        while (_cacheOrder.Count > MaxCachedModels)
        {
            string oldest = _cacheOrder.First.Value;
            if (string.Equals(oldest, keepKey, StringComparison.OrdinalIgnoreCase)) break;
            _cacheOrder.RemoveFirst();
            if (_cache.TryGetValue(oldest, out var evicted))
            {
                if (ReferenceEquals(evicted, _current)) _current = null;
                evicted.Model?.Dispose();
                _cache.Remove(oldest);
            }
        }
    }

    private void Render(SC2GameAssetScenePreview.StandalonePreview model, RenderTexture target, float yaw, float zoom)
    {
        EnsureCamera();

        var mesh = model.Mesh;
        var bounds = mesh.bounds;
        float scale = model.UniformScale;
        float radius = Mathf.Max(0.0001f, bounds.extents.magnitude * scale);
        float distance = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 1.08f / zoom;

        // The model is recentred on the origin and rotated in place; the camera sits above and
        // behind it on a fixed pitch.
        var cameraRotation = Quaternion.Euler(PitchDegrees, 0f, 0f);
        _camera.transform.SetPositionAndRotation(cameraRotation * new Vector3(0f, 0f, -distance), cameraRotation);
        _camera.nearClipPlane = Mathf.Max(0.001f, distance * 0.02f);
        _camera.farClipPlane = distance + radius * 4f;

        var matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, yaw, 0f), Vector3.one * scale) * Matrix4x4.Translate(-bounds.center);
        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
        {
            var material = model.Materials[Mathf.Min(subMesh, model.Materials.Length - 1)];
            if (material == null) continue;
            Graphics.DrawMesh(mesh, matrix, material, PreviewLayer, _camera, subMesh, null, ShadowCastingMode.Off, false);
        }

        var previousActive = RenderTexture.active;
        GL.PushMatrix();
        _camera.targetTexture = target;
        _camera.Render();
        _camera.targetTexture = null;
        GL.PopMatrix();
        RenderTexture.active = previousActive;
    }

    private void EnsureTarget()
    {
        if (_target != null) return;
        _target = new RenderTexture(TextureSize, TextureSize, 24, RenderTextureFormat.ARGB32)
        {
            name = "SC2PropModelPreview",
            antiAliasing = 4,
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    private void EnsureCamera()
    {
        if (_camera == null)
        {
            var go = new GameObject("SC2PropModelPreviewCamera") { hideFlags = HideFlags.HideAndDontSave };
            _camera = go.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = BackgroundColor;
            _camera.cullingMask = 1 << PreviewLayer;
            _camera.fieldOfView = FieldOfView;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.useOcclusionCulling = false;

            // A key light of its own, parented to the camera so the model is always lit from the
            // same side as seen: it is Important so it is the main light the unit shader reads
            // (instead of whatever the map's sun happens to be), and its culling mask keeps it off
            // every scene object.
            var lightObject = new GameObject("SC2PropModelPreviewLight") { hideFlags = HideFlags.HideAndDontSave };
            lightObject.transform.SetParent(go.transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(25f, -30f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = KeyLightIntensity;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.cullingMask = 1 << PreviewLayer;
        }
    }

    private static void DestroyObject(UnityEngine.Object obj)
    {
        if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
        else UnityEngine.Object.DestroyImmediate(obj);
    }
}
