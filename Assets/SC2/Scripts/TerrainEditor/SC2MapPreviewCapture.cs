using System;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// Off-screen camera capture for the two vanilla-map-shaped preview images:
/// &lt;MapName&gt;.minimap.win.dds (auto-rendered fresh at every export — see
/// SC2TerrainExporter.BuildExportArtifacts) and &lt;MapName&gt;_PC_mapshot.dds
/// (captured once by the user via the Export tab's "Capture Preview" button,
/// stored on SC2TerrainData.MapShotBgraPixels, re-used at every export until
/// re-captured). Both render terrain + props only — no markers, pick-dots,
/// brush cursors or other editor-only overlays; the minimap additionally
/// forces the terrain into pure RNM display (regardless of whatever preview
/// mode/overlay is actually on screen) and excludes units, real or reference
/// (see CaptureMinimap/RenderTerrainOnlyForCapture) — the _PC_mapshot preview
/// is a free-framed screenshot and keeps showing units.
///
/// Capture is deliberately deferred to the very start of the NEXT
/// SC2GameViewEditorUI.LateUpdate (ProcessPendingCapture, called there
/// before anything else) rather than rendered synchronously the moment the
/// "Capture"/"Export" button is clicked (which always happens from OnGUI,
/// itself always running AFTER that same frame's LateUpdate has already
/// executed). This ordering is load-bearing, not a stylistic choice:
/// SC2GameAssetScenePreview/SC2MarkerScenePreview/SC2ObjectMarkerRenderer
/// all submit their per-frame Graphics.DrawMesh(...)/DrawMeshInstanced(...)
/// calls with camera:null, which Unity broadcasts to EVERY camera that
/// renders during that same engine frame — not just whichever camera a
/// caller happened to pass in for frustum culling. A temp capture camera
/// rendered synchronously from OnGUI would therefore also pick up whatever
/// markers/pick-dots that frame's LateUpdate had already queued for the
/// main viewport camera. By running strictly BEFORE LateUpdate's own
/// marker/pick-dot draw calls (which come later in that same method), our
/// own explicit SC2GameAssetScenePreview.DrawAllBatches(tempCamera) call
/// for props/units is the only thing queued when our temp camera renders —
/// no suppression flags on the marker renderers needed.
/// </summary>
public static class SC2MapPreviewCapture
{
    public const int MinimapSize = 1024;
    public const int MapShotWidth = 448;
    public const int MapShotHeight = 206;

    private static Action _pendingWork;

    public static bool IsCapturePending => _pendingWork != null;

    /// <summary>Call once per frame, at the very top of
    /// SC2GameViewEditorUI.LateUpdate — see this class's own doc comment for
    /// why the ordering matters.</summary>
    public static void ProcessPendingCapture()
    {
        var work = _pendingWork;
        _pendingWork = null;
        if (work == null) return;
        try { work(); }
        catch (Exception ex) { Debug.LogException(ex); }
    }

    /// <summary>Queues a top-down minimap capture; onCaptured runs next
    /// frame with the result (null if no camera could be resolved).</summary>
    /// <param name="terrainShell">Forces the render into pure RNM terrain display for the
    /// duration of the capture, regardless of whatever preview mode/overlay the user actually has
    /// active (see SC2TerrainShell.RenderTerrainOnlyForCapture) — optional only because some call
    /// sites predate this; a minimap captured without it renders whatever mode is on screen.</param>
    public static void RequestMinimapCapture(SC2TerrainData data, Action<Texture2D> onCaptured, SC2TerrainShell terrainShell = null)
    {
        // An imported custom picture replaces the render everywhere a minimap is used.
        _pendingWork = () => onCaptured?.Invoke(DecodeCustomMinimap(data) ?? CaptureMinimap(data, terrainShell));
    }

    /// <summary>The imported custom minimap as an RGBA32 texture in Unity's natural orientation (the
    /// same form a rendered capture has), or null when none is set.</summary>
    public static Texture2D DecodeCustomMinimap(SC2TerrainData data)
    {
        if (data?.CustomMinimapPng == null || data.CustomMinimapPng.Length == 0) return null;
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(decoded, data.CustomMinimapPng, false)) return null;
            // LoadImage may hand back RGB24; the callers expect 4 bytes per pixel.
            var rgba = new Texture2D(decoded.width, decoded.height, TextureFormat.RGBA32, false);
            rgba.SetPixels32(decoded.GetPixels32());
            rgba.Apply(false, false);
            return rgba;
        }
        finally { UnityEngine.Object.DestroyImmediate(decoded); }
    }

    /// <summary>Opaque black BGRA pixels of the map-select preview size — what is exported when no preview
    /// was captured or imported (the vanilla frame is stamped on top).</summary>
    public static byte[] BlackMapShotBgra()
    {
        var pixels = new byte[MapShotWidth * MapShotHeight * 4];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return pixels;
    }

    /// <summary>Queues a capture that reuses the CURRENT main viewport
    /// camera's exact position/rotation/FOV — "whatever the user is looking
    /// at right now" is the framing, matching how the user is meant to pick
    /// the mapshot's angle (navigate the viewport, then press Capture).</summary>
    public static void RequestPreviewCapture(Action<Texture2D> onCaptured)
    {
        _pendingWork = () => onCaptured?.Invoke(CapturePreviewFromCurrentView());
    }

    private static Camera ResolveSourceCamera()
    {
        if (Camera.main != null) return Camera.main;
        return UnityEngine.Object.FindFirstObjectByType<Camera>();
    }

    private static Texture2D CaptureMinimap(SC2TerrainData data, SC2TerrainShell terrainShell)
    {
        var sourceCamera = ResolveSourceCamera();
        if (sourceCamera == null || data == null) return null;

        var worldWidth = Mathf.Max(1f, data.GridWidth * data.CellSize);
        var worldHeight = Mathf.Max(1f, data.GridHeight * data.CellSize);
        var center = new Vector3(worldWidth * 0.5f, 0f, worldHeight * 0.5f);
        // High enough above the tallest plausible terrain/prop to never
        // clip — the far clip plane below gives plenty of range under it.
        var eye = center + Vector3.up * 4000f;

        // No content-level mirror here (unlike the interactive Top view's
        // own SC2TopViewMirror-driven flip): RenderOffscreen already applies
        // one vertical flip on every capture on this project's target
        // platform (graphicsUVStartsAtTop's D3D readback correction below).
        // Real vanilla minimap.win.dds files (checked directly against
        // their own hfield.bdf heightmap — same row-major gz*mapW+gx
        // layout this editor's own grid uses, no relative flip between
        // them) need exactly that ONE flip, not a second one stacked on
        // top — a second content-level mirror here would cancel it back
        // out to an unflipped (wrong) result.
        Texture2D Render() => RenderOffscreen(sourceCamera, MinimapSize, MinimapSize, tempCam =>
        {
            tempCam.orthographic = true;
            tempCam.orthographicSize = Mathf.Max(worldWidth, worldHeight) * 0.5f;
            tempCam.nearClipPlane = 1f;
            tempCam.farClipPlane = 8000f;
            tempCam.transform.SetPositionAndRotation(eye, Quaternion.Euler(90f, 0f, 0f));
        }, hideUnits: true);

        // Real vanilla minimap.win.dds files are the terrain's own diffuse/RNM lighting
        // colors plus decorative props (rocks, trees) — never whatever debug preview mode
        // (Mats/Pairs/Slope/Height, or a plain unlit Textured view) the mapmaker happens to be
        // looking at, and never reference buildings/real units (SC2GameAssetScenePreview has no
        // separate flag for "reference" vs "real" units — see RenderOffscreen's own hideUnits
        // param — so both are excluded together). terrainShell is only null for call sites that
        // predate this; without it the capture falls back to whatever mode is on screen.
        Texture2D result = null;
        if (terrainShell != null) terrainShell.RenderTerrainOnlyForCapture(() => result = Render());
        else result = Render();
        return result;
    }

    private static Texture2D CapturePreviewFromCurrentView()
    {
        var sourceCamera = ResolveSourceCamera();
        if (sourceCamera == null) return null;

        var tex = RenderOffscreen(sourceCamera, MapShotWidth, MapShotHeight, tempCam =>
        {
            tempCam.orthographic = sourceCamera.orthographic;
            tempCam.orthographicSize = sourceCamera.orthographicSize;
            tempCam.fieldOfView = sourceCamera.fieldOfView;
            tempCam.nearClipPlane = sourceCamera.nearClipPlane;
            tempCam.farClipPlane = sourceCamera.farClipPlane;
            tempCam.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
        });
        if (tex != null) ApplyEdgeVignette(tex);
        return tex;
    }

    /// <summary>Shared off-screen render: a throwaway temp Camera (not the
    /// real interactive one — creating a second camera rather than
    /// repurposing sourceCamera avoids ever disturbing the live viewport
    /// mid-frame), props/units drawn explicitly (see this class's own doc
    /// comment for why this must happen before returning control), read
    /// back into a plain RGBA32 Texture2D (Unity's own bottom-row-0
    /// orientation — callers writing this out to DDS must flip rows first,
    /// see ToBgraBytes/ToRgbBytes).</summary>
    /// <param name="hideUnits">true for the minimap (real units and reference units — which
    /// share the same SC2GameAssetScenePreview category, see HideUnits' own field comment —
    /// must never appear baked into the exported minimap texture); false for the free-framed
    /// _PC_mapshot preview, which is meant to look like an actual screenshot of the map.</param>
    private static Texture2D RenderOffscreen(Camera sourceCamera, int width, int height, Action<Camera> configure, bool hideUnits = false)
    {
        var go = new GameObject("SC2MapPreviewCaptureCamera") { hideFlags = HideFlags.HideAndDontSave };
        var cam = go.AddComponent<Camera>();
        cam.CopyFrom(sourceCamera);
        cam.enabled = false; // never part of Unity's own automatic per-frame render loop — only ever driven by our own explicit Render() call below

        var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        configure(cam);

        // Props always shown here regardless of the user's current Visibility > Props toggle
        // state, per the "props visible, nothing else real-time" requirement — units (which
        // includes reference units, see hideUnits' own param comment) follow the caller's
        // choice instead. SC2GameViewEditorUI's own LateUpdate (running right after this whole
        // capture, in the same call) unconditionally re-derives HideProps/HideUnits from the
        // live Visibility toggles again for the main camera's own subsequent draws, so this
        // temporary override never leaks into the interactive view.
        var previousHideProps = SC2GameAssetScenePreview.HideProps;
        var previousHideUnits = SC2GameAssetScenePreview.HideUnits;
        SC2GameAssetScenePreview.HideProps = false;
        SC2GameAssetScenePreview.HideUnits = hideUnits;

        Texture2D tex = null;
        try
        {
            SC2GameAssetScenePreview.DrawAllBatches(cam);
            cam.Render();

            var previousActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = previousActive;

            // Manually driving a throwaway camera's Render() into a
            // RenderTexture (rather than letting Unity render a camera to
            // the screen through its normal per-frame pipeline) reads back
            // vertically flipped on APIs whose texture origin is top-left
            // (D3D/Metal/Vulkan — SystemInfo.graphicsUVStartsAtTop == true,
            // true on Windows' default DX backend, which is why this only
            // ever showed up there) — a widely-reported Unity gotcha for
            // manual off-screen camera capture, independent of and on top
            // of the deliberate content-level mirror CaptureMinimap applies
            // for the vanilla +Z-down-screen convention below. OpenGL/GLES
            // (graphicsUVStartsAtTop == false) doesn't need this.
            if (SystemInfo.graphicsUVStartsAtTop)
                FlipTextureVertically(tex);
        }
        finally
        {
            SC2GameAssetScenePreview.HideProps = previousHideProps;
            SC2GameAssetScenePreview.HideUnits = previousHideUnits;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(go);
        }
        return tex;
    }

    /// <summary>In-place row flip for a freshly-ReadPixels'd Texture2D — see
    /// the graphicsUVStartsAtTop check in RenderOffscreen for why this is
    /// needed at all.</summary>
    private static void FlipTextureVertically(Texture2D tex)
    {
        var flipped = FlipRowsVertically(tex.GetRawTextureData(), tex.width, tex.height, 4);
        tex.LoadRawTextureData(flipped);
        tex.Apply(false, false);
    }

    // The alpha frame is the exact one real vanilla _PC_mapshot.dds files
    // carry (feathered edge, rounded corners, diagonally cut bottom-left) —
    // see SC2MapShotFrameMask.
    private static void ApplyEdgeVignette(Texture2D tex)
    {
        var pixels = tex.GetRawTextureData(); // RGBA32, bottom row first; alpha is byte 3
        SC2MapShotFrameMask.ApplyToBgra(pixels, tex.width, tex.height, bottomRowFirst: true);
        tex.LoadRawTextureData(pixels);
        tex.Apply(false, false);
    }

    /// <summary>DDS stores row 0 at the TOP; Unity's Texture2D (and
    /// ReadPixels) stores row 0 at the BOTTOM — confirmed by
    /// SC2CustomTextureImporter's own read-side FlipUncompressedRowsVertically/
    /// FlipCompressedBlocksVertically, which undo exactly this on load. A
    /// writer has to flip the other way, or the exported image is upside
    /// down relative to every real vanilla file (and to this same reader).
    /// Public: SC2TerrainExporter calls this on the ALREADY-STORED
    /// SC2TerrainData.MapShotBgraPixels right before DDS-encoding it — that
    /// array itself is kept in Unity's own natural (unflipped) orientation
    /// (see ToBgraBytes below) specifically so the Export tab's own
    /// on-screen thumbnail preview doesn't come out upside down.</summary>
    public static byte[] FlipRowsVertically(byte[] data, int width, int height, int bytesPerPixel)
    {
        var stride = width * bytesPerPixel;
        var result = new byte[data.Length];
        for (var y = 0; y < height; y++)
            Array.Copy(data, y * stride, result, (height - 1 - y) * stride, stride);
        return result;
    }

    /// <summary>Converts a captured RGBA32 Texture2D into the raw BGRA byte
    /// layout SC2TerrainData.MapShotBgraPixels stores — Unity's own natural
    /// (unflipped) row orientation, matching what a Texture2D built via
    /// LoadRawTextureData(..., TextureFormat.BGRA32) for an on-screen
    /// preview expects. FlipRowsVertically is applied separately, only at
    /// the point of actually writing the DDS file (see its own doc
    /// comment) — not here.</summary>
    public static byte[] ToBgraBytes(Texture2D tex)
    {
        var rgba = tex.GetRawTextureData();
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];     // B
            bgra[i + 1] = rgba[i + 1]; // G
            bgra[i + 2] = rgba[i];     // R
            bgra[i + 3] = rgba[i + 3]; // A
        }
        return bgra;
    }

    /// <summary>Extracts a plain RGB byte[] (3 bytes/pixel, no alpha) from a
    /// captured RGBA32 Texture2D for SC2DdsEncoder.BuildDxt1SingleMip,
    /// row-flipped for DDS's top-row-0 convention.</summary>
    public static byte[] ToRgbBytes(Texture2D tex)
    {
        var rgba = FlipRowsVertically(tex.GetRawTextureData(), tex.width, tex.height, 4);
        var pixelCount = rgba.Length / 4;
        var rgb = new byte[pixelCount * 3];
        for (var i = 0; i < pixelCount; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }
        return rgb;
    }
}
