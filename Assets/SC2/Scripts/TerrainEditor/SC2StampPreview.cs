using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The preview of a stamp brush while it is being placed (press, drag, release). The stamp image itself is laid over the terrain as a
/// translucent mesh that follows the ground (and, for Raise / Lower, is lifted or sunk by the height the stamp will add, so its shape
/// reads before it is committed); the brush ring, an arrow along the stamp's own +X axis and a size / angle / alpha read-out are drawn on
/// top. The mesh lives on a persistent GameObject that the game camera renders every frame, so it does not depend on when OnGUI runs; it
/// hides itself when nothing has refreshed it for a few frames (the tab was left mid-gesture).
/// </summary>
public static class SC2StampPreview
{
    private const int GhostSize = 64;
    private const int MaxGhosts = 24;
    private const int GridCells = 40;
    private const int StaleFrames = 3;

    private sealed class GhostEntry { public Texture2D Texture; public byte[] Alpha; }
    private static readonly Dictionary<int, GhostEntry> Ghosts = new Dictionary<int, GhostEntry>();

    private static GameObject _go;
    private static Mesh _mesh;
    private static Material _material;
    private static Vector3[] _vertices;
    private static Vector2[] _uvs;
    private static int _lastFrame = -1;
    private static int _lastKey;

    private static GhostEntry GhostFor(Texture stamp)
    {
        if (stamp == null) return null;
        var key = stamp.GetInstanceID();
        if (Ghosts.TryGetValue(key, out var cached) && cached.Texture != null) return cached;
        if (Ghosts.Count >= MaxGhosts)
        {
            foreach (var old in Ghosts.Values) if (old.Texture != null) UnityEngine.Object.Destroy(old.Texture);
            Ghosts.Clear();
        }

        var rt = RenderTexture.GetTemporary(GhostSize, GhostSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var previous = RenderTexture.active;
        Texture2D read = null;
        try
        {
            Graphics.Blit(stamp, rt);
            RenderTexture.active = rt;
            read = new Texture2D(GhostSize, GhostSize, TextureFormat.RGBA32, false, true);
            read.ReadPixels(new Rect(0, 0, GhostSize, GhostSize), 0, 0);
            read.Apply(false);
            var pixels = read.GetPixels32();
            var alphas = new byte[pixels.Length];
            var half = (GhostSize - 1) * 0.5f;
            for (var y = 0; y < GhostSize; y++)
            for (var x = 0; x < GhostSize; x++)
            {
                var c = pixels[y * GhostSize + x];
                var luminance = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                var dx = (x - half) / half; var dy = (y - half) / half;
                var alpha = dx * dx + dy * dy <= 1f ? Mathf.Clamp01(luminance * (c.a / 255f)) : 0f;
                var b = (byte)Mathf.RoundToInt(alpha * 255f);
                alphas[y * GhostSize + x] = b;
                pixels[y * GhostSize + x] = new Color32(255, 255, 255, b);
            }
            var ghost = new Texture2D(GhostSize, GhostSize, TextureFormat.RGBA32, false, true)
                { name = "SC2StampGhost", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            ghost.SetPixels32(pixels);
            ghost.Apply(false);
            return Ghosts[key] = new GhostEntry { Texture = ghost, Alpha = alphas };
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            if (read != null) UnityEngine.Object.Destroy(read);
        }
    }

    private static float SampleAlpha(byte[] alpha, float u, float v)
    {
        var fx = Mathf.Clamp01(u) * (GhostSize - 1); var fy = Mathf.Clamp01(v) * (GhostSize - 1);
        var x0 = (int)fx; var y0 = (int)fy;
        var x1 = Mathf.Min(x0 + 1, GhostSize - 1); var y1 = Mathf.Min(y0 + 1, GhostSize - 1);
        var tx = fx - x0; var ty = fy - y0;
        float A(int x, int y) => alpha[y * GhostSize + x] / 255f;
        return Mathf.Lerp(Mathf.Lerp(A(x0, y0), A(x1, y0), tx), Mathf.Lerp(A(x0, y1), A(x1, y1), tx), ty);
    }

    private static bool EnsureObject()
    {
        if (_go != null && _mesh != null && _material != null) return true;
        if (_go != null) UnityEngine.Object.Destroy(_go);
        var shader = Resources.Load<Shader>("SC2StampPreview") ?? Shader.Find("SC2/StampPreview");
        if (shader == null) return false;

        var n = GridCells + 1;
        _vertices = new Vector3[n * n];
        _uvs = new Vector2[n * n];
        var triangles = new int[GridCells * GridCells * 6];
        var t = 0;
        for (var j = 0; j < GridCells; j++)
        for (var i = 0; i < GridCells; i++)
        {
            var a = j * n + i; var b = a + 1; var c = a + n; var d = c + 1;
            triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
            triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
        }
        _mesh = new Mesh { name = "SC2StampPreviewMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt16, hideFlags = HideFlags.HideAndDontSave };
        _mesh.MarkDynamic();
        _mesh.vertices = _vertices;
        _mesh.uv = _uvs;
        _mesh.triangles = triangles;

        _material = new Material(shader) { name = "SC2StampPreview", hideFlags = HideFlags.HideAndDontSave };
        _go = new GameObject("SC2 Stamp Preview") { hideFlags = HideFlags.HideAndDontSave };
        _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
        var renderer = _go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = _material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return true;
    }

    /// <summary>Hides the ground mesh.</summary>
    public static void Hide()
    {
        if (_go != null && _go.activeSelf) _go.SetActive(false);
    }

    /// <summary>Called every frame from the game view's LateUpdate: a preview nothing has refreshed for a few frames (the tab or tool
    /// was left in the middle of the gesture) goes away on its own.</summary>
    public static void HideIfStale()
    {
        if (_go != null && _go.activeSelf && Time.frameCount - _lastFrame > StaleFrames) _go.SetActive(false);
    }

    /// <summary>Updates the preview. <paramref name="angleRad"/> is <see cref="SC2StampGesture.AngleRad"/> (world angle of the stamp's +X
    /// axis), <paramref name="alpha"/> the strength the stamp will be placed with, <paramref name="heightAtFullBrightness"/> how far the ground
    /// moves where the stamp is brightest (positive up, 0 for a tool that does not move it). Call on every event while the stamp is
    /// being placed; the mesh is rebuilt only when something changed.</summary>
    public static void Draw(Camera camera, Rect viewportRect, Vector3 anchor, float radius, float angleRad, Texture stamp, float alpha,
        Color color, Func<float, float, float> sampleHeight, string readout, float heightAtFullBrightness = 0f)
    {
        if (Event.current == null || camera == null) return;
        ShowGround(anchor, radius, angleRad, stamp, alpha, color, sampleHeight, heightAtFullBrightness);

        if (Event.current.type != EventType.Repaint) return;

        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, anchor, radius, color, Color.clear, sampleHeight: sampleHeight);

        var y = sampleHeight != null ? sampleHeight(anchor.x, anchor.z) : anchor.y;
        var centre = SC2TerrainShell.WorldToGameGui(camera, viewportRect, new Vector3(anchor.x, y, anchor.z));
        if (!viewportRect.Contains(centre)) return;

        // The arrow along the stamp's +X axis, on the terrain.
        var cos = Mathf.Cos(angleRad); var sin = Mathf.Sin(angleRad);
        const int steps = 8;
        var previousPoint = centre;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var wx = anchor.x + cos * radius * t; var wz = anchor.z + sin * radius * t;
            var wy = sampleHeight != null ? sampleHeight(wx, wz) : anchor.y;
            var point = SC2TerrainShell.WorldToGameGui(camera, viewportRect, new Vector3(wx, wy, wz));
            if (SC2TerrainShell.ClipSegmentToViewport(viewportRect, previousPoint, point, out var a, out var b))
                SC2TerrainShell.DrawGuiLineSegment(a, b, color, 2f);
            previousPoint = point;
        }

        if (!string.IsNullOrEmpty(readout))
        {
            var previousColor = GUI.color;
            var rect = new Rect(centre.x + 14f, centre.y + 14f, 260f, 22f);
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), readout);
            GUI.color = Color.white;
            GUI.Label(rect, readout);
            GUI.color = previousColor;
        }
    }

    /// <summary>Lays the stamp mesh on the ground (and shows it); <see cref="Draw"/> calls this on every event.</summary>
    public static void ShowGround(Vector3 anchor, float radius, float angleRad, Texture stamp, float alpha, Color color,
        Func<float, float, float> sampleHeight, float heightAtFullBrightness)
    {
        _lastFrame = Time.frameCount;
        var ghost = GhostFor(stamp);
        if (ghost == null || !EnsureObject()) return;
        if (!_go.activeSelf) _go.SetActive(true);

        var key = 17;
        key = key * 31 + anchor.GetHashCode();
        key = key * 31 + radius.GetHashCode();
        key = key * 31 + angleRad.GetHashCode();
        key = key * 31 + alpha.GetHashCode();
        key = key * 31 + heightAtFullBrightness.GetHashCode();
        key = key * 31 + stamp.GetInstanceID();
        _material.mainTexture = ghost.Texture;
        _material.SetColor("_Color", new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha) * 0.8f));
        _material.SetFloat("_Shade", Mathf.Abs(heightAtFullBrightness) > 0.01f ? 1f : 0f);
        if (key == _lastKey) return;
        _lastKey = key;

        // The stamp image's square [-1, 1] x [-1, 1] (px right, py down the image rows) lies on the ground at
        // anchor + radius * (px * (cos, sin) + py * (-sin, cos)); image row v counts from the bottom, so v = (1 - py) / 2.
        var cos = Mathf.Cos(angleRad); var sin = Mathf.Sin(angleRad);
        var lift = Mathf.Max(0.2f, radius * 0.003f);
        var n = GridCells + 1;
        for (var j = 0; j < n; j++)
        for (var i = 0; i < n; i++)
        {
            var px = i / (float)GridCells * 2f - 1f;
            var py = j / (float)GridCells * 2f - 1f;
            var wx = anchor.x + radius * (px * cos - py * sin);
            var wz = anchor.z + radius * (px * sin + py * cos);
            var u = px * 0.5f + 0.5f; var v = 0.5f - py * 0.5f;
            var wy = (sampleHeight != null ? sampleHeight(wx, wz) : anchor.y) + lift;
            if (heightAtFullBrightness != 0f) wy += heightAtFullBrightness * alpha * SampleAlpha(ghost.Alpha, u, v);
            _vertices[j * n + i] = new Vector3(wx, wy, wz);
            _uvs[j * n + i] = new Vector2(u, v);
        }
        _mesh.vertices = _vertices;
        _mesh.uv = _uvs;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }
}
