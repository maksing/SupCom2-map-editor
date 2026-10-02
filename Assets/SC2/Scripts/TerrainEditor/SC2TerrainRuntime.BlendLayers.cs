using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Blend mask editing layers on the live terrain (see SC2BlendMaskLayerStack). The active mask group's layers each
/// live in their own render texture (R = value, G = coverage); painting goes into the active layer's texture and
/// the layers are flattened bottom to top into <see cref="BlendRT"/>, which the terrain shader and the pairs'
/// RnmMaskBytes (and so the mask export) keep using unchanged.
///
/// The layer pixels are copied back into the stack's byte arrays only when an edit ends (CommitLayerEdits), and
/// always into a NEW array — the arrays are shared by undo snapshots, so they are never changed in place.
/// </summary>
public partial class SC2TerrainRuntime
{
    private SC2BlendMaskLayerStack _activeStack;
    private readonly List<RenderTexture> _layerRTs = new List<RenderTexture>();
    private Material _compositeMat;
    private bool _compositeDirty;
    private int _dirtyLayerIndex = -1;

    /// <summary>The layers of the mask group currently shown in <see cref="BlendRT"/>, or null before one is active.</summary>
    public SC2BlendMaskLayerStack ActiveBlendStack => _activeStack;

    public bool HasActiveLayers => _activeStack != null && BlendRT != null && _layerRTs.Count == _activeStack.Layers.Count && _layerRTs.Count > 0;

    /// <summary>Makes the layers of the active mask group live: finds the group's stack (creating a one-layer Base
    /// stack from the existing mask for a map painted before layers existed), builds one render texture per
    /// layer and flattens them into BlendRT.</summary>
    private void AttachLayerStack(SC2TerrainData data)
    {
        ReleaseLayerRTs();
        _activeStack = null;
        if (data == null || BlendRT == null || string.IsNullOrEmpty(_activeBlendMaskName)) return;

        var width = BlendRT.width;
        var height = BlendRT.height;
        var stack = data.FindBlendLayerStack(_activeBlendMaskName);
        if (stack != null && (stack.Width != width || stack.Height != height))
        {
            // Layers of another resolution cannot be drawn into this mask; start over from the composite.
            data.BlendLayerStacks.Remove(stack);
            stack = null;
        }
        if (stack == null)
        {
            byte[] maskBytes = null;
            foreach (var pair in data.Pairs)
                if (NormalizeMaskName(pair.RnmMaskName) == _activeBlendMaskName && pair.RnmMaskBytes != null && pair.RnmMaskBytes.Length == width * height * 4)
                { maskBytes = pair.RnmMaskBytes; break; }
            stack = new SC2BlendMaskLayerStack { MaskName = _activeBlendMaskName, Width = width, Height = height };
            stack.Layers.Add(SC2BlendMaskLayerMath.CreateBaseFromRgbaMask(maskBytes, width * height));
            data.BlendLayerStacks.Add(stack);
        }
        stack.Normalize();
        _activeStack = stack;
        BuildLayerRTs();
        RecompositeBlend();
    }

    private void BuildLayerRTs()
    {
        ReleaseLayerRTs();
        if (_activeStack == null) return;
        foreach (var layer in _activeStack.Layers)
        {
            var rt = CreateLayerRT();
            UploadLayer(layer, rt);
            _layerRTs.Add(rt);
        }
        _dirtyLayerIndex = -1;
    }

    private RenderTexture CreateLayerRT()
    {
        // ARGB32 and Linear like BlendRT: the layer stores raw data (value in R, coverage in G), never colour.
        var rt = new RenderTexture(BlendRT.width, BlendRT.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { name = "SC2_BlendLayer", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt.Create();
        return rt;
    }

    private void UploadLayer(SC2BlendMaskLayer layer, RenderTexture rt)
    {
        var pixelCount = rt.width * rt.height;
        var prev = RenderTexture.active;
        if (layer.Data == null || layer.Data.Length != pixelCount * SC2BlendMaskLayerMath.BytesPerPixel)
        {
            RenderTexture.active = rt;
            GL.Clear(false, true, new Color(0f, 0f, 0f, 1f));
            RenderTexture.active = prev;
            return;
        }

        var rgba = new byte[pixelCount * 4];
        for (var i = 0; i < pixelCount; i++)
        {
            rgba[i * 4] = layer.Data[i * 2];
            rgba[i * 4 + 1] = layer.Data[i * 2 + 1];
            rgba[i * 4 + 3] = 255;
        }
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
        tex.LoadRawTextureData(rgba);
        tex.Apply(false);
        Graphics.Blit(tex, rt);
        RenderTexture.active = prev;
        DestroyObject(tex);
    }

    private byte[] ReadLayerRT(RenderTexture rt)
    {
        var w = rt.width; var h = rt.height;
        // prev must be captured before the Blit (see ReadBlendBytes: Graphics.Blit leaves RenderTexture.active at its destination).
        var prev = RenderTexture.active;
        var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Texture2D tex = null;
        try
        {
            Graphics.Blit(rt, tmp);
            RenderTexture.active = tmp;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }
        var pixels = tex.GetPixels32();
        var data = new byte[pixels.Length * SC2BlendMaskLayerMath.BytesPerPixel];
        for (var i = 0; i < pixels.Length; i++)
        {
            data[i * 2] = pixels[i].r;
            data[i * 2 + 1] = pixels[i].g;
        }
        DestroyObject(tex);
        return data;
    }

    private void ReleaseLayerRTs()
    {
        foreach (var rt in _layerRTs) SafeRelease(rt);
        _layerRTs.Clear();
        _dirtyLayerIndex = -1;
        _compositeDirty = false;
    }

    private bool EnsureCompositeMaterial()
    {
        if (_compositeMat != null) return true;
        // Resources first, Shader.Find second: a shader no material asset references can be stripped from a build, but
        // everything under Resources/ is always included (the same reasoning as SC2UnitMaterialFactory).
        var shader = Resources.Load<Shader>("TerrainEditorShaders/SC2BlendComposite") ?? Shader.Find("Hidden/SC2/BlendComposite");
        if (shader == null) { Debug.LogError("[SC2] BlendComposite shader not found"); return false; }
        _compositeMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        return true;
    }

    /// <summary>Flattens the visible layers into BlendRT (neutral RGB, blend factor in alpha).</summary>
    public void RecompositeBlend()
    {
        _compositeDirty = false;
        if (!HasActiveLayers || !EnsureCompositeMaterial()) return;

        var prev = RenderTexture.active;
        RenderTexture.active = BlendRT;
        GL.Clear(false, true, new Color(0.5f, 0.5f, 0.5f, 0f));
        var tmp = RenderTexture.GetTemporary(BlendRT.descriptor);
        for (var i = 0; i < _activeStack.Layers.Count; i++)
        {
            var layer = _activeStack.Layers[i];
            if (!layer.Visible || layer.Opacity <= 0f) continue;
            _compositeMat.SetTexture("_LayerTex", _layerRTs[i]);
            _compositeMat.SetFloat("_Opacity", Mathf.Clamp01(layer.Opacity));
            Graphics.Blit(BlendRT, tmp, _compositeMat);
            Graphics.Blit(tmp, BlendRT);
        }
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);
    }

    /// <summary>Called after a paint tick (all symmetry copies painted) so the layers are flattened once per tick.</summary>
    public void RecompositeBlendIfDirty()
    {
        if (_compositeDirty) RecompositeBlend();
    }

    /// <summary>Copies the layer that was painted since the last commit from its render texture into the stack, as
    /// a new array. Call before reading or snapshotting the stack's data.</summary>
    public void CommitLayerEdits()
    {
        RecompositeBlendIfDirty();
        if (!HasActiveLayers || _dirtyLayerIndex < 0 || _dirtyLayerIndex >= _layerRTs.Count) { _dirtyLayerIndex = -1; return; }
        _activeStack.Layers[_dirtyLayerIndex].Data = ReadLayerRT(_layerRTs[_dirtyLayerIndex]);
        _dirtyLayerIndex = -1;
    }

    /// <summary>Rebuilds the render textures from the (just edited) stack — after a layer was added, removed,
    /// moved, merged or restored by undo — and flattens it into BlendRT.</summary>
    public void RefreshLayersFromStack()
    {
        if (_activeStack == null) return;
        _activeStack.Normalize();
        BuildLayerRTs();
        RecompositeBlend();
    }

    /// <summary>Replaces the active mask group's stack with <paramref name="stack"/> (an undo snapshot's copy; its
    /// data arrays are shared, which is safe because arrays are never changed in place) and shows it.</summary>
    public void ReplaceActiveBlendStack(SC2TerrainData data, SC2BlendMaskLayerStack stack)
    {
        if (data == null || stack == null || BlendRT == null) return;
        var restored = stack.Clone();
        var index = data.BlendLayerStacks.FindIndex(s => s != null && s.MaskName == restored.MaskName);
        if (index >= 0) data.BlendLayerStacks[index] = restored; else data.BlendLayerStacks.Add(restored);
        _activeStack = restored;
        RefreshLayersFromStack();
    }

    /// <summary>Selects the layer the brush paints into, committing any pending paint of the previous one first.</summary>
    public void SelectBlendLayer(int index)
    {
        if (_activeStack == null) return;
        CommitLayerEdits();
        _activeStack.ActiveIndex = Mathf.Clamp(index, 0, _activeStack.Layers.Count - 1);
    }

    /// <summary>Fills the whole active layer with one value and coverage (0..1 each) — the Reset / Fill buttons.</summary>
    public void FillActiveLayer(float value, float coverage)
    {
        if (!HasActiveLayers) return;
        var index = Mathf.Clamp(_activeStack.ActiveIndex, 0, _layerRTs.Count - 1);
        var prev = RenderTexture.active;
        RenderTexture.active = _layerRTs[index];
        GL.Clear(false, true, new Color(value, coverage, 0f, 1f));
        RenderTexture.active = prev;
        _dirtyLayerIndex = index;
        _compositeDirty = true;
    }

    /// <summary>Soft brush dab into the active layer: paints toward <paramref name="blendValue"/> (or 0 when
    /// <paramref name="erase"/>), or, with <paramref name="removeFromLayer"/>, lowers the layer's coverage.</summary>
    private void PaintActiveLayer(Vector3 worldPos, float radius, float strength, float blendValue, bool erase, bool removeFromLayer,
        Texture customBrush, float rotationRadians, Vector2 brushMirror, bool brushSwapAxes)
    {
        var index = Mathf.Clamp(_activeStack.ActiveIndex, 0, _layerRTs.Count - 1);
        // The bottom layer always covers every pixel, so there is nothing to remove from it.
        if (removeFromLayer && index == 0) return;
        if (!EnsureShaders()) return;

        ConfigureBlendBrush(worldPos, radius, strength, blendValue, erase, customBrush, rotationRadians, brushMirror, brushSwapAxes, removeFromLayer ? 4 : 3);
        var target = _layerRTs[index];
        var tmp = RenderTexture.GetTemporary(target.descriptor);
        var prevActive = RenderTexture.active;
        Graphics.Blit(target, tmp, _paintBrushMat);
        Graphics.Blit(tmp, target);
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(tmp);
        _dirtyLayerIndex = index;
        _compositeDirty = true;
    }

    private void ConfigureBlendBrush(Vector3 worldPos, float radius, float strength, float blendValue, bool erase,
        Texture customBrush, float rotationRadians, Vector2 brushMirror, bool brushSwapAxes, int paintMode)
    {
        var uvX = worldPos.x / (GridWidth * CellSize);
        var uvZ = worldPos.z / (GridHeight * CellSize);
        var uvR = Mathf.Max(radius / (GridWidth * CellSize), 0.001f);
        _paintBrushMat.SetVector("_BrushUV", new Vector4(uvX, uvZ, 0, 0));
        _paintBrushMat.SetFloat("_BrushRadius", uvR);
        _paintBrushMat.SetFloat("_BrushStrength", strength);
        _paintBrushMat.SetColor("_PaintColor", new Color(.5f, .5f, .5f, erase ? 0f : blendValue));
        _paintBrushMat.SetInt("_PaintMode", paintMode);
        // Custom stamp brush (Blend/RNM only — see SC2PaintBrush.shader; Pair assign/erase always keeps the round brush).
        var useCustom = customBrush != null;
        _paintBrushMat.SetTexture("_BrushTex", useCustom ? customBrush : Texture2D.whiteTexture);
        _paintBrushMat.SetFloat("_UseCustomBrush", useCustom ? 1f : 0f);
        // Per-stroke randomized rotation, rolled once by SC2PaintMode at mouse-down and passed unchanged for the whole stroke.
        _paintBrushMat.SetFloat("_BrushRotation", rotationRadians);
        // Mirrors the stamp's content for a REFLECTED symmetric copy — see SC2SymmetryMath.GetSlotMirror.
        _paintBrushMat.SetVector("_BrushMirror", brushMirror);
        _paintBrushMat.SetFloat("_BrushSwapAxes", brushSwapAxes ? 1f : 0f);
    }

    private void DisposeBlendLayers()
    {
        ReleaseLayerRTs();
        _activeStack = null;
        if (_compositeMat != null) { DestroyObject(_compositeMat); _compositeMat = null; }
    }
}
