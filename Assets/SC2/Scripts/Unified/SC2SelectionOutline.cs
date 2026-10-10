using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// An outline along the silhouette of the selected object's renderers, as Blender draws one, drawn by a command buffer on the game
/// camera after its forward pass: the renderers are drawn flat into a mask, and the pixels just outside the mask get the outline
/// colour. Used for the imported terrain object selected in the Imported tab.
/// </summary>
public static class SC2SelectionOutline
{
    private static Camera _camera;
    private static CommandBuffer _buffer;
    private static Material _material;
    private static readonly List<Renderer> _renderers = new List<Renderer>();
    private static int _signature;
    private static int _maskId = Shader.PropertyToID("_SC2SelectionOutlineMask");

    /// <summary>Outlines <paramref name="renderers"/> on <paramref name="camera"/>; a null or empty list (or no camera) removes the
    /// outline. Cheap to call every frame: the command buffer is rebuilt only when the camera or the set of renderers changes.</summary>
    public static void Set(Camera camera, IReadOnlyList<Renderer> renderers, float width = 2f)
    {
        if (camera == null || renderers == null || renderers.Count == 0) { Clear(); return; }
        if (_material == null)
        {
            var shader = Resources.Load<Shader>("SC2SelectionOutline") ?? Shader.Find("SC2/SelectionOutline");
            if (shader == null) { Clear(); return; }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
        _material.SetFloat("_OutlineWidth", Mathf.Clamp(width, 1f, 3f));

        var signature = camera.GetInstanceID();
        foreach (var renderer in renderers) signature = signature * 31 + (renderer != null ? renderer.GetInstanceID() : 0);
        if (_buffer != null && ReferenceEquals(_camera, camera) && signature == _signature) return;

        Clear();
        _camera = camera;
        _signature = signature;
        _renderers.AddRange(renderers);
        _buffer = new CommandBuffer { name = "SC2 Selection Outline" };
        _buffer.GetTemporaryRT(_maskId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.R8);
        _buffer.SetRenderTarget(_maskId);
        _buffer.ClearRenderTarget(false, true, Color.black);
        foreach (var renderer in _renderers)
        {
            if (renderer == null) continue;
            var subMeshes = renderer is MeshRenderer ? Mathf.Max(1, renderer.GetComponent<MeshFilter>()?.sharedMesh?.subMeshCount ?? 1) : 1;
            for (var subMesh = 0; subMesh < subMeshes; subMesh++) _buffer.DrawRenderer(renderer, _material, subMesh, 0);
        }
        _buffer.Blit(_maskId, BuiltinRenderTextureType.CameraTarget, _material, 1);
        _buffer.ReleaseTemporaryRT(_maskId);
        camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _buffer);
    }

    public static void Clear()
    {
        if (_buffer != null)
        {
            if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _buffer);
            _buffer.Release();
        }
        _buffer = null;
        _camera = null;
        _signature = 0;
        _renderers.Clear();
    }
}
