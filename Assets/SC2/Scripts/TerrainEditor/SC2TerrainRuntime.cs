using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Terrain runtime: chunk-based, CPU heights, GPU paint.
/// Heights stored as float[] on CPU → uploaded to chunk meshes on demand.
/// PairMapRT and BlendRT still GPU-resident (fast brush paint).
/// </summary>
public partial class SC2TerrainRuntime : IDisposable
{
    public const int CHUNK_SIZE = 64; // quads per chunk side (smaller = faster dirty rebuild)

    public int   GridWidth, GridHeight;
    public float CellSize, HeightScale;

    // ── CPU heights ───────────────────────────────────────────────────────────
    private float[] _heights; // (GridWidth+1)*(GridHeight+1)
    private int     _stride;  // GridWidth+1

    // ── GPU paint RTs ─────────────────────────────────────────────────────────
    public RenderTexture PairMapRT;  // R8, GridWidth×GridHeight
    public RenderTexture BlendRT;    // ARGB32, 2048×2048

    // ── Chunk grid ────────────────────────────────────────────────────────────
    private struct Chunk
    {
        public GameObject GO;
        public MeshFilter MF;
        public MeshRenderer MR;
        public Mesh Mesh;
        public int CX, CZ; // chunk grid index
    }
    private readonly List<Chunk> _chunks = new();
    private int _chunksX, _chunksZ;
    private GameObject _chunkRoot;

    // ── Brush materials ───────────────────────────────────────────────────────
    private Material _paintBrushMat;
    private Material _viewMat;
    private bool     _initialized;

    // ── Blend mask ────────────────────────────────────────────────────────────
    // Single BlendRT shown/painted at all times.
    // Each pair stores its own mask in RnmMaskBytes.
    // Switching active pair: save current BlendRT → old pair, load new pair → BlendRT.

    private string _activeBlendMaskName;

    /// <summary>Switch BlendRT to a specific pair's blend mask.</summary>
    public void SetActivePairForBlend(int pairIdx, SC2TerrainData data)
    {
        var pair = pairIdx >= 0 && pairIdx < data.Pairs.Count ? data.Pairs[pairIdx] : null;
        SetActiveRnmMaskGroup(pair != null ? pair.RnmMaskName : null, data);
    }

    public void SetActiveRnmMaskGroup(string maskName, SC2TerrainData data)
    {
        var next = NormalizeMaskName(maskName);
        // The layers on screen belong to one SC2TerrainData; when another one is passed (a different map was loaded
        // into this runtime) they are stale, must not be written into that data, and are reloaded from it.
        var dataChanged = _activeStack != null && data.BlendLayerStacks != null && !data.BlendLayerStacks.Contains(_activeStack);
        if (_activeBlendMaskName == next && !dataChanged) return;
        if (!string.IsNullOrEmpty(_activeBlendMaskName) && !dataChanged)
            FlushActiveBlendGroup(data);
        ReleaseLayerRTs();
        _activeStack = null;
        LoadMaskGroupToRT(next, data);
        _activeBlendMaskName = next;
        // Shows the group's editing layers (flattened into BlendRT, which replaces the plain mask load above).
        AttachLayerStack(data);
    }


    private void LoadMaskGroupToRT(string maskName, SC2TerrainData data)
    {
        var prev = RenderTexture.active;
        SC2MaterialPair pair = null;
        foreach (var p in data.Pairs)
            if (NormalizeMaskName(p.RnmMaskName) == maskName && p.RnmMaskBytes.Length == p.MaskWidth * p.MaskHeight * 4)
            { pair = p; break; }

        if (pair != null)
        {
            // Load saved bytes → temp Texture2D → blit to BlendRT
            var tex = new Texture2D(pair.MaskWidth, pair.MaskHeight, TextureFormat.RGBA32, false, true);
            tex.LoadRawTextureData(pair.RnmMaskBytes);
            tex.Apply(false);
            Graphics.Blit(tex, BlendRT);
            RenderTexture.active = prev;
            DestroyObject(tex);
        }
        else
        {
            // No saved data — clear to neutral (RGB=128, A=0)
            RenderTexture.active = BlendRT;
            GL.Clear(false, true, new Color(0.5f, 0.5f, 0.5f, 0f));
            RenderTexture.active = prev;
        }
    }

    /// <summary>Save current BlendRT to pair's RnmMaskBytes.</summary>
    public void FlushActiveBlendToPair(int pairIdx, SC2TerrainData data)
    {
        if (pairIdx >= 0 && pairIdx < data.Pairs.Count)
            data.Pairs[pairIdx].RnmMaskBytes = ReadBlendBytes(BlendRT);
    }

    public void FlushActiveBlendGroup(SC2TerrainData data)
    {
        if (data == null || data.Pairs.Count == 0 || BlendRT == null) return;
        var maskName = _activeBlendMaskName;
        if (string.IsNullOrEmpty(maskName)) return;

        // Layers: finish any pending paint (flatten, then copy the painted layer back into the stack) first, so
        // BlendRT below is the up-to-date composite of the stack.
        CommitLayerEdits();
        CompositeFlushPending = false;
        var bytes = ReadBlendBytes(BlendRT);
        for (var i = 0; i < data.Pairs.Count; i++)
            if (NormalizeMaskName(data.Pairs[i].RnmMaskName) == maskName)
                data.Pairs[i].RnmMaskBytes = bytes;
    }

    /// <summary>
    /// Read BlendRT → byte[] RGBA. Used for active pair readback.
    /// Blits to RGBA32 temp RT first to avoid format issues on D3D11.
    /// </summary>
    private static byte[] ReadBlendBytes(RenderTexture src)
    {
        var w = src.width; var h = src.height;
        // prev MUST be captured before the Blit below, not after: Graphics.Blit
        // leaves RenderTexture.active pointed at its destination (tmp) as a side
        // effect. Capturing "prev" afterwards would silently capture tmp itself
        // instead of the real previous target — then restoring "prev" below
        // would point RenderTexture.active at tmp right as it gets released via
        // ReleaseTemporary, leaving active pointed at an invalid/released RT for
        // the rest of the frame. This was the actual bug behind stray UI content
        // ending up in exported masks: it's not specific to this one call, but
        // this exact ordering mistake is present in several readback helpers.
        var prev = RenderTexture.active;
        // Blit to temp RGBA32 RT (guarantees correct channel order)
        var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                              RenderTextureReadWrite.Linear);
        Texture2D tex = null;
        try
        {
            Graphics.Blit(src, tmp);
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
        var bytes  = new byte[pixels.Length * 4];
        for (var pi = 0; pi < pixels.Length; pi++)
        {
            // Store neutral RGB (128) unconditionally and keep only alpha (the
            // blend weight). The RNM brush only ever authors alpha — RGB is a
            // constant 128 by construction — so anything non-128 in BlendRT's
            // colour channels is transient garbage (e.g. a frame of editor UI
            // left by a stray active render target) and must never be persisted
            // into RnmMaskBytes. This keeps the stored mask, its preview and the
            // export all clean at the single point where the bytes are written.
            bytes[pi*4]   = 128;
            bytes[pi*4+1] = 128;
            bytes[pi*4+2] = 128;
            bytes[pi*4+3] = pixels[pi].a;
        }
        DestroyObject(tex);
        return bytes;
    }

    /// <summary>
    /// Flush current BlendRT to the active RnmMaskName group only.
    /// Kept for older callers; it no longer overwrites unrelated mask groups.
    /// </summary>
    public void FlushAllBlendRTs(SC2TerrainData data)
    {
        FlushActiveBlendGroup(data);
    }

    // ── Pair/tiling LUT ──────────────────────────────────────────────────────
    private Texture2DArray _diffuse1Array, _diffuse2Array;
    private Texture2D      _pairLUT, _tilingLUT, _pairColorRT_tex;
    private Texture2D      _diffuse1Fallback, _diffuse2Fallback;
    private RenderTexture  _pairColorRT;
    private Material       _pairColorViewMat;
    private int            _lastPairHash = -1;
    // Cached for PublishDecalLightingGlobals, which has no other access to
    // the pair count SetAllPairMaterials already computed.
    private int            _lastPairCountForGlobals = 1;

    // Baked RNM lighting preview (SC2RnmBakeService/PreviewMode==4) — packed
    // into ONE Texture2DArray (layer = pairIdx*3 + basisIndex) instead of 3
    // separate arrays, since d3d11 caps a pixel shader at 16 total sampler
    // registers and SC2TerrainView.shader was already close to that — see
    // this shader's own _RnmArr Properties-block comment. See
    // SetRnmUvTransform's own doc comment for why this needs a separate
    // world-space UV transform from the diffuse arrays' uv1_tiled.
    private Texture2DArray _rnmArray;
    private Texture2D      _rnmFallback;
    private Texture2D      _greyTex;

    /// <summary>Unity has no built-in Texture2D.greyTexture (only white/
    /// black) — used as the default RNM basis color for an un-baked pair, so
    /// PreviewMode==4 reads as "flat but lit" instead of solid black before
    /// any bake has run.</summary>
    private Texture2D GreyTexture()
    {
        if (_greyTex != null) return _greyTex;
        _greyTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "SC2_RnmGrey" };
        _greyTex.SetPixel(0, 0, new Color32(128, 128, 128, 255));
        _greyTex.Apply(false);
        return _greyTex;
    }

    // ── Init ─────────────────────────────────────────────────────────────────

    /// <summary>holeTriPairIndex, when given, is the SAME array/convention
    /// as SC2TerrainData.TrianglePairIndex (2 entries per cell, row-major,
    /// -1 = no geometry there). The core never passes this (always null —
    /// its own terrain has no concept of a carved hole), so its chunk
    /// meshes are built exactly as before. The background terrain does:
    /// its own carved-out core footprint (SC2BackgroundTerrain.
    /// BuildPairIndexAndCarveHole) needs those cells to render no
    /// triangles at all, not just a different material — this is purely
    /// topological and only ever set once here at Init (painting never
    /// turns a cell into/out of -1, see SC2BackgroundEditMode's own
    /// ApplyPaintPair guard), so no per-stroke index-buffer rebuild is
    /// needed to keep it correct.</summary>
    public void Init(int w, int h, float cell, float heightScale, short[] holeTriPairIndex = null)
    {
        if (_initialized) return;
        _initialized = true;
        GridWidth = w; GridHeight = h; CellSize = cell; HeightScale = heightScale;
        _stride = w + 1;
        _holeTriPairIndex = holeTriPairIndex;

        _heights = new float[_stride * (h + 1)];
        CreatePaintRTs();
        CreateChunks();
    }

    private short[] _holeTriPairIndex;

    /// <summary>The same array Init's holeTriPairIndex was called with
    /// (null for the core, which has no carved hole). Exposed so a caller
    /// syncing PairMapRT paint results back to CPU (SC2BackgroundEditMode)
    /// can re-clamp any hole cell the brush's raw GPU blit painted over
    /// back to -1 — the brush itself has no concept of a hole (it's the
    /// same shader/material the core's unholed PaintPair uses), so without
    /// this a stroke that merely overlaps the seam would silently "fill
    /// in" part of the carved-out core footprint in the exported data.</summary>
    public short[] HoleTriPairIndex => _holeTriPairIndex;

    /// <summary>The one Material shared by every chunk (see its own field
    /// comment above). Exposed so SC2BackgroundSeamSkirt's own small
    /// stitching mesh can use the EXACT same material/shader (MatCap-based
    /// editor preview, SC2TerrainView.shader) as the surrounding chunks
    /// instead of a separately-built one — a background pair with no real
    /// diffuse texture assigned (the common case before the user paints
    /// real materials in) renders as a plausible MatCap surface here, but
    /// as a jarring flat-white-lit-by-raw-sun-color patch through the real
    /// in-game SC2Terrain.shader (CreateLivePairMaterial) the skirt used to
    /// build for itself — the two shaders answer "what does no-texture look
    /// like" completely differently, so anything short of sharing the exact
    /// same material guarantees a visible seam-vs-body color mismatch.</summary>
    public Material SharedChunkMat => _sharedChunkMat;

    /// <summary>Places this runtime's chunk root somewhere other than world
    /// origin — every chunk mesh's vertices are already built in the
    /// runtime's own local grid space, so this is the only thing needed to
    /// host a second, independently-positioned instance (e.g. the
    /// decorative background terrain, which surrounds the core at a real
    /// world offset — see SC2BackgroundTerrain.GetWorldOffset).</summary>
    public void SetWorldOrigin(Vector3 offset)
    {
        if (_chunkRoot != null) _chunkRoot.transform.position = offset;
    }

    /// <summary>World-space XZ -> RNM basis-texture UV transform for
    /// PreviewMode==4 (rnmUV = (worldXZ - offset) * (1/span)) — deliberately
    /// separate from the diffuse arrays' own uv1_tiled (world XZ / each
    /// pair's TilingScale), because SC2TerrainExporter uses a DIFFERENT UV2
    /// convention for RNM masks than for diffuse: the core tiles at a fixed
    /// 1024-unit period regardless of any pair's TilingScale, while the
    /// background normalizes to its own bounding box instead (matches
    /// SC2TerrainExporter.Export's two Uv2 branches) since its footprint can
    /// span many thousands of world units. Called once per
    /// ApplyPreviewModeToChunksFor, by the caller — this runtime has no way
    /// to know on its own whether it's the core or the background.</summary>
    public void SetRnmUvTransform(Vector2 offset, Vector2 span)
    {
        if (_sharedChunkMat == null) return;
        _sharedChunkMat.SetVector("_RnmUvOffset", new Vector4(offset.x, offset.y, 0f, 0f));
        var invX = span.x > 0.0001f ? 1f / span.x : 0f;
        var invY = span.y > 0.0001f ? 1f / span.y : 0f;
        _sharedChunkMat.SetVector("_RnmUvInvScale", new Vector4(invX, invY, 0f, 0f));
    }

    /// <summary>Publishes this runtime's pair-map/RNM/tiling-LUT state as GLOBAL shader properties
    /// suffixed "_Core"/"_Bg" so SC2Decal.shader — a Resources-only material with no reference to
    /// either terrain runtime — can reproduce the exact "which pair, was it actually baked, what
    /// does it look like" lookup the terrain's own chunk mesh already does, from a decal's own
    /// world position, instead of lighting itself with a generic directional light. worldOffset/span
    /// MUST be absolute WORLD-space (not this runtime's own _RnmUvOffset, which for the background
    /// is shifted into its own local frame — see SetRnmUvTransform's own doc comment); callers
    /// already have the pre-shift world bounds on hand at their own SetRnmUvTransform call site.</summary>
    public void PublishDecalLightingGlobals(string suffix, Vector2 worldOffset, Vector2 span)
    {
        Shader.SetGlobalInt("_RnmValid_" + suffix, _rnmArray != null ? 1 : 0);
        if (_rnmArray != null) Shader.SetGlobalTexture("_RnmArr_" + suffix, _rnmArray);
        if (PairMapRT != null) Shader.SetGlobalTexture("_PairMapTex_" + suffix, PairMapRT);
        if (_tilingLUT != null) Shader.SetGlobalTexture("_TilingLUT_" + suffix, _tilingLUT);
        Shader.SetGlobalInt("_PairCount_" + suffix, _lastPairCountForGlobals);
        Shader.SetGlobalVector("_RnmUvOffset_" + suffix, new Vector4(worldOffset.x, worldOffset.y, 0f, 0f));
        var invX = span.x > 0.0001f ? 1f / span.x : 0f;
        var invY = span.y > 0.0001f ? 1f / span.y : 0f;
        Shader.SetGlobalVector("_RnmUvInvScale_" + suffix, new Vector4(invX, invY, 0f, 0f));
    }

    // Static/map-wide cache for PublishImportedRnmGlobals below — this baked
    // texture belongs to neither the core nor the background runtime
    // instance (it covers imported terrain.bdf objects, which can sit
    // anywhere relative to either), so it isn't stored per-instance the way
    // _rnmArray is.
    private static Texture2D _importedRnmTex;
    private static int _importedRnmCacheKey = int.MinValue;

    /// <summary>Publishes SC2RnmBakeService's baked imported-terrain world RNM texture
    /// (SC2TerrainData.ImportedRnmBakedRgba) as globals for SC2Decal.shader's third, last-resort
    /// lookup (after core/background both say "no data here") — see PublishDecalLightingGlobals'
    /// own doc comment for why decals need this at all. Rebuilds the cached Texture2D only when the
    /// bake actually changed (width/height/generation), not every call.</summary>
    public static void PublishImportedRnmGlobals(SC2TerrainData coreData)
    {
        if (coreData == null || !coreData.HasImportedRnmBake)
        {
            Shader.SetGlobalInt("_ImportedRnmValid", 0);
            return;
        }
        var w = coreData.ImportedRnmWidth;
        var h = coreData.ImportedRnmHeight;
        var key = ((w * 397 + h) * 31 + coreData.RnmBakeGeneration) * 31 + coreData.ImportedRnmBakedRgba.Length;
        if (key != _importedRnmCacheKey || _importedRnmTex == null || _importedRnmTex.width != w || _importedRnmTex.height != h)
        {
            if (_importedRnmTex != null) DestroyObject(_importedRnmTex);
            _importedRnmTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "SC2_ImportedRnmTex",
            };
            _importedRnmTex.LoadRawTextureData(coreData.ImportedRnmBakedRgba);
            _importedRnmTex.Apply(false);
            _importedRnmCacheKey = key;
        }
        Shader.SetGlobalTexture("_ImportedRnmTex", _importedRnmTex);
        Shader.SetGlobalInt("_ImportedRnmValid", 1);
        Shader.SetGlobalVector("_ImportedRnmWorldOffset", new Vector4(coreData.ImportedRnmWorldOriginX, coreData.ImportedRnmWorldOriginZ, 0f, 0f));
        var invX = coreData.ImportedRnmWorldSpanX > 0.0001f ? 1f / coreData.ImportedRnmWorldSpanX : 0f;
        var invY = coreData.ImportedRnmWorldSpanZ > 0.0001f ? 1f / coreData.ImportedRnmWorldSpanZ : 0f;
        Shader.SetGlobalVector("_ImportedRnmInvScale", new Vector4(invX, invY, 0f, 0f));
    }

    public void Dispose()
    {
        foreach (var c in _chunks) if (c.GO) DestroyObject(c.GO);
        _chunks.Clear();
        if (_chunkRoot != null) DestroyObject(_chunkRoot);
        if (_sharedChunkMat != null) DestroyObject(_sharedChunkMat);
        DisposeBlendLayers();
        SafeRelease(PairMapRT); SafeRelease(BlendRT); SafeRelease(_pairColorRT);
        if (_diffuse1Array != null) DestroyObject(_diffuse1Array);
        if (_diffuse2Array != null) DestroyObject(_diffuse2Array);
        if (_rnmArray != null) DestroyObject(_rnmArray);
        if (_greyTex != null) DestroyObject(_greyTex);
        if (_pairLUT       != null) DestroyObject(_pairLUT);
        if (_tilingLUT     != null) DestroyObject(_tilingLUT);
        if (_pairColorRT_tex != null) DestroyObject(_pairColorRT_tex);
        if (_pairColorViewMat != null) DestroyObject(_pairColorViewMat);
        if (_paintBrushMat != null) DestroyObject(_paintBrushMat);
        if (_viewMat != null) DestroyObject(_viewMat);
        if (_blendPreviewSnapshot != null) { DestroyObject(_blendPreviewSnapshot); _blendPreviewSnapshot = null; }
        _chunkRoot = null; _sharedChunkMat = null;
        PairMapRT = null; BlendRT = null; _pairColorRT = null;
        _diffuse1Array = null; _diffuse2Array = null; _diffuse1Fallback = null; _diffuse2Fallback = null; _pairLUT = null; _tilingLUT = null;
        _rnmArray = null; _rnmFallback = null; _greyTex = null;
        _pairColorRT_tex = null; _pairColorViewMat = null; _paintBrushMat = null; _viewMat = null;
        _activeBlendMaskName = null;
        _initialized = false;
    }

    /// <summary>Every chunk's own mesh with its local-to-world matrix (the
    /// background runtime's chunk root sits at a world offset), empty while
    /// the chunk root is hidden. This is the ground the decal tools clip
    /// against for the live terrain - taken from the runtime itself rather
    /// than found by a scene search, so it never depends on scene-object
    /// lookup or renderer state.</summary>
    public IEnumerable<(Mesh Mesh, Matrix4x4 LocalToWorld)> VisibleChunkMeshes()
    {
        if (_chunkRoot == null || !_chunkRoot.activeInHierarchy) yield break;
        foreach (var chunk in _chunks)
            if (chunk.Mesh != null && chunk.GO != null)
                yield return (chunk.Mesh, chunk.GO.transform.localToWorldMatrix);
    }

    public void SetVisible(bool visible)
    {
        if (_chunkRoot != null) _chunkRoot.SetActive(visible);
    }

    // ── Paint RTs ─────────────────────────────────────────────────────────────

    private void CreatePaintRTs()
    {
        // Both RTs below explicitly force RenderTextureReadWrite.Linear instead
        // of leaving it as the 4-arg constructor's Default. Default resolves
        // based on the project's color space setting, and — critically — the
        // Unity Editor's GUI rendering and a standalone Player's GUI rendering
        // can interpret "Default" differently for the very same texture and
        // stored values (this is exactly why every readback helper elsewhere
        // in this file already forces Linear explicitly). Neither RT stores
        // "photographic" color data that should get sRGB treatment — PairMapRT
        // is a raw pair-index encoded in R8, and BlendRT is a raw neutral
        // constant (0.5) plus a blend-weight alpha — so forcing Linear here
        // makes the raw bytes render identically in the Editor and in a
        // standalone Build & Run player. Without this, a freshly-cleared,
        // untouched BlendRT displayed correct neutral grey in the Editor's
        // Paint Blend preview box but solid black in Build & Run, even though
        // the underlying stored values were identical on both.
        PairMapRT = new RenderTexture(GridWidth, GridHeight, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            { name="SC2_PairMap", filterMode=FilterMode.Point, wrapMode=TextureWrapMode.Clamp };
        PairMapRT.Create();
        // Fill pair 0 (R=0)
        var prevRT = RenderTexture.active;
        RenderTexture.active = PairMapRT;
        GL.Clear(false, true, Color.black);
        RenderTexture.active = prevRT;

        BlendRT = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { name="SC2_Blend", filterMode=FilterMode.Bilinear, wrapMode=TextureWrapMode.Clamp };
        BlendRT.Create();
        // Initialize with neutral RNM values: RGB=128 (neutral normal), A=0 (no blend)
        var prevB = RenderTexture.active;
        RenderTexture.active = BlendRT;
        GL.Clear(false, true, new Color(0.5f, 0.5f, 0.5f, 0f));
        RenderTexture.active = prevB;
    }

    // ── Chunk grid ────────────────────────────────────────────────────────────

    // Single shared material used by ALL chunks. All chunks have identical shader
    // properties (PairMapTex/BlendTex/LUTs/mode) — only mesh position differs —
    // so one Material instance avoids O(chunkCount) SetProperty calls on every
    // preview refresh (this was a real cause of MouseUp/Update stalls at large
    // grid resolutions: 1000+ chunks × 9 SetX calls = thousands of calls per refresh).
    private Material _sharedChunkMat;

    private void CreateChunks()
    {
        _chunksX = Mathf.CeilToInt((float)GridWidth  / CHUNK_SIZE);
        _chunksZ = Mathf.CeilToInt((float)GridHeight / CHUNK_SIZE);

        _chunkRoot = new GameObject("SC2TerrainChunks")
            { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
        _chunkRoot.AddComponent<SC2DecalProjectionSurface>();

        EnsureViewMat();
        _sharedChunkMat = new Material(_viewMat) { name = "SC2_SharedChunkMat" };
        SetChunkMaterial(_sharedChunkMat, 0, 0);

        for (var cz = 0; cz < _chunksZ; cz++)
        for (var cx = 0; cx < _chunksX; cx++)
        {
            var go = new GameObject($"Chunk_{cx}_{cz}") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_chunkRoot.transform, false);

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            mr.sharedMaterial    = _sharedChunkMat; // shared, not per-chunk instance

            var mesh = new Mesh { name=$"Chunk_{cx}_{cz}", indexFormat=IndexFormat.UInt32 };
            mesh.MarkDynamic();
            BuildChunkIndices(mesh, cx, cz);
            mf.sharedMesh = mesh;

            _chunks.Add(new Chunk { GO=go, MF=mf, MR=mr, Mesh=mesh, CX=cx, CZ=cz });
        }

        // Build all chunk vertex data from current heights
        RebuildAllChunks();
    }

    private void BuildChunkIndices(Mesh mesh, int cx, int cz)
    {
        var qx = Mathf.Min(CHUNK_SIZE, GridWidth  - cx * CHUNK_SIZE);
        var qz = Mathf.Min(CHUNK_SIZE, GridHeight - cz * CHUNK_SIZE);
        var vx = qx + 1;
        var gx0 = cx * CHUNK_SIZE;
        var gz0 = cz * CHUNK_SIZE;
        // _holeTriPairIndex null (every core chunk) -> full uniform grid,
        // identical to this method's behavior before hole support existed.
        // Non-null (background only) -> per-cell carved-hole cull, using
        // the exact SAME global-cell indexing SC2BackgroundTerrain.
        // BuildPairIndexAndCarveHole wrote it with.
        var tris = _holeTriPairIndex == null ? new int[qx * qz * 6] : null;
        var trisList = _holeTriPairIndex != null ? new List<int>(qx * qz * 6) : null;
        var t = 0;
        for (var z = 0; z < qz; z++)
        for (var x = 0; x < qx; x++)
        {
            var i00 = z*vx+x; var i10=i00+1; var i01=(z+1)*vx+x; var i11=i01+1;
            if (_holeTriPairIndex == null)
            {
                tris[t++]=i00; tris[t++]=i01; tris[t++]=i10;
                tris[t++]=i10; tris[t++]=i01; tris[t++]=i11;
                continue;
            }
            var cellIdx = ((gz0 + z) * GridWidth + (gx0 + x)) * 2;
            if (cellIdx < 0 || cellIdx + 1 >= _holeTriPairIndex.Length) continue;
            if (_holeTriPairIndex[cellIdx] >= 0) { trisList.Add(i00); trisList.Add(i01); trisList.Add(i10); }
            if (_holeTriPairIndex[cellIdx + 1] >= 0) { trisList.Add(i10); trisList.Add(i01); trisList.Add(i11); }
        }
        // Allocate vertex arrays (will be filled by RebuildChunk)
        mesh.SetVertices(new Vector3[(qx+1)*(qz+1)]);
        mesh.SetNormals(new Vector3[(qx+1)*(qz+1)]);
        mesh.SetUVs(0, new Vector2[(qx+1)*(qz+1)]);
        mesh.SetUVs(1, new Vector2[(qx+1)*(qz+1)]);
        mesh.SetUVs(2, new Vector2[(qx+1)*(qz+1)]);
        if (tris != null) mesh.SetTriangles(tris, 0);
        else mesh.SetTriangles(trisList, 0);
    }

    // ── Chunk rebuild (CPU, only dirty chunks) ────────────────────────────────

    public void RebuildAllChunks()
    {
        foreach (var c in _chunks)
            RebuildChunk(c);
    }

    // ── UV1 stretch-compensated tiling coordinate ─────────────────────────────
    // See SC2TerrainUv1Unwrap's own doc comment for the algorithm and why a
    // flat world-XZ tiling coordinate visibly stretches a texture on steep
    // terrain. Cached, not recomputed every RebuildChunk (which fires often
    // during sculpting/painting — this class exists specifically to keep
    // that path cheap) — lazily built the first time a chunk is rebuilt
    // (matching what a fresh export already does, so a never-touched map's
    // preview already agrees with what would be exported) and refreshed on
    // demand by RecalculateUv1 after terrain edits change the geometry.
    private float[] _uv1ProfileU, _uv1ProfileV;
    // Set from SC2TerrainData.Uv1HeightWeight by the last RecalculateUv1
    // call — this runtime only holds raw grid arrays, not the owning
    // SC2TerrainData, so it can't read that field itself. A lazy
    // EnsureUv1Profile build (before the button is ever pressed) uses
    // whatever this defaults to (1, physically accurate) rather than a
    // possibly-tuned saved value — a minor, self-correcting staleness: one
    // press of the button afterwards always brings it in sync.
    private float _uv1HeightWeight = 1f;

    /// <summary>Rebuilds the stretch-compensated tiling profile from the
    /// CURRENT heights and refreshes every chunk's mesh to use it — the
    /// live-preview counterpart of what the exporter already computes fresh
    /// on every export. Manual/on-demand (see this class's own field
    /// comment for why), not automatic on every edit.</summary>
    public void RecalculateUv1(float heightWeight = 1f)
    {
        _uv1HeightWeight = heightWeight;
        SC2TerrainUv1Unwrap.BuildProfiles(_heights, GridWidth, GridHeight, CellSize, out _uv1ProfileU, out _uv1ProfileV, _uv1HeightWeight);
        RebuildAllChunks();
    }

    private void EnsureUv1Profile()
    {
        if (_uv1ProfileU != null) return;
        SC2TerrainUv1Unwrap.BuildProfiles(_heights, GridWidth, GridHeight, CellSize, out _uv1ProfileU, out _uv1ProfileV, _uv1HeightWeight);
    }

    /// <summary>Same stretch-compensated tiling coordinate RebuildChunk
    /// assigns its own chunk vertices, for an external caller whose mesh
    /// isn't part of this runtime's regular chunk grid — currently only
    /// SC2BackgroundSeamSkirt's own thin stitching strip, which bridges this
    /// runtime's edge to another SC2TerrainRuntime's and needs BOTH sides'
    /// tiling to agree with their real surrounding chunks. Takes this
    /// runtime's own LOCAL x/z (world space for the core, background-local
    /// for the background — same frame RebuildChunk itself uses).</summary>
    public Vector2 SampleUv1Tile(float localX, float localZ)
    {
        EnsureUv1Profile();
        return SC2TerrainUv1Unwrap.Sample(_uv1ProfileU, _uv1ProfileV, GridWidth, GridHeight, CellSize, localX, localZ);
    }

    /// <summary>SampleUv1Tile that continues past this runtime's own grid edge (see
    /// SC2TerrainUv1Unwrap.SampleExtended) — for the seam skirt, which reaches beyond the core.</summary>
    public Vector2 SampleUv1TileExtended(float localX, float localZ)
    {
        EnsureUv1Profile();
        return SC2TerrainUv1Unwrap.SampleExtended(_uv1ProfileU, _uv1ProfileV, GridWidth, GridHeight, CellSize, localX, localZ);
    }

    /// <summary>
    /// Sets (or clears) the costs/terrainTypes brush overlay — see
    /// SC2CostsShell. Unlike an earlier version of this that issued ad-hoc
    /// Graphics.DrawMesh calls from OnGUI once per frame, this just sets a
    /// texture parameter on the shared, persistent chunk material — sampled
    /// automatically every frame during the terrain's NORMAL camera render,
    /// same approach _PairColorTex/SetPairColorRT already use reliably.
    /// The DrawMesh-from-OnGUI approach depended on exactly when OnGUI fires
    /// relative to the camera's render pass, which isn't guaranteed: it could
    /// skip a frame whenever anything upstream consumed the input Event
    /// (making the overlay flicker or vanish on mouse-up), and the Unity
    /// Editor's "Game" tab does not necessarily repaint every frame the same
    /// way a real Build &amp; Run player's Update loop does — a passive shader
    /// sample has neither problem, since the chunk always renders and
    /// automatically shows whatever texture is currently set here.
    /// </summary>
    public void SetCostsOverlayTexture(Texture overlayTex)
    {
        if (_sharedChunkMat == null) return;
        _sharedChunkMat.SetInt("_CostsOverlayEnabled", overlayTex != null ? 1 : 0);
        if (overlayTex != null)
            _sharedChunkMat.SetTexture("_CostsOverlayTex", overlayTex);
    }

    /// <summary>View menu's "Load Reference Image" — see SC2Terrain.shader's
    /// own _ReferenceOverlayEnabled comment for why this REPLACES the whole
    /// terrain render (an early-return in frag(), not a blended overlay
    /// like SetCostsOverlayTexture above) whenever enabled, and why it's
    /// sampled from world-space XZ rather than the mesh's own uv2. worldSize
    /// is the real world extent (GridWidth*CellSize, GridHeight*CellSize) to
    /// normalize by — passed by the caller rather than assumed like Costs'
    /// own hardcoded 1024 default, so this stays correct on a differently-
    /// sized map.</summary>
    public void SetReferenceOverlay(Texture overlayTex, bool enabled, Vector2 worldSize, float opacity = 1f)
    {
        if (_sharedChunkMat == null) return;
        _sharedChunkMat.SetFloat("_ReferenceOverlayOpacity", Mathf.Clamp01(opacity));
        if (overlayTex != null)
            _sharedChunkMat.SetTexture("_ReferenceOverlayTex", overlayTex);
        _sharedChunkMat.SetVector("_ReferenceOverlaySize", new Vector4(Mathf.Max(1f, worldSize.x), Mathf.Max(1f, worldSize.y), 0f, 0f));
        _sharedChunkMat.SetInt("_ReferenceOverlayEnabled", enabled && overlayTex != null ? 1 : 0);
    }

    /// <summary>Reported directly: "show the 1-cell heightmap grid across
    /// the map" — same persistent-shader-parameter approach as
    /// SetCostsOverlayTexture above (procedural in the shader, no texture
    /// upload needed here — see TerrainView.shader's ApplyHeightGrid).</summary>
    public void SetHeightGridOverlay(bool enabled, float cellSize)
    {
        if (_sharedChunkMat == null) return;
        _sharedChunkMat.SetInt("_HeightGridEnabled", enabled ? 1 : 0);
        _sharedChunkMat.SetFloat("_HeightGridCellSize", Mathf.Max(cellSize, 0.001f));
    }

    /// <summary>Blender-style edit-mode wireframe over the live terrain —
    /// distinct from SetHeightGridOverlay's arbitrary 1-world-unit reference
    /// grid above: cellSize here is the real mesh CellSize, so the drawn
    /// lines are the actual triangulation (see TerrainView.shader's
    /// ApplyWireframe for how one quad's 2 triangles are expressed
    /// procedurally without per-vertex barycentric data).</summary>
    public void SetWireframeOverlay(bool enabled, float cellSize)
    {
        if (_sharedChunkMat == null) return;
        _sharedChunkMat.SetInt("_WireframeEnabled", enabled ? 1 : 0);
        _sharedChunkMat.SetFloat("_WireframeCellSize", Mathf.Max(cellSize, 0.001f));
    }

    /// <summary>Which of TerrainView.shader's 6 procedural MatCap looks
    /// (Standard/Steel/Zebra/Tan/Dark/Normal — see MatCapLit's own doc
    /// comment) the Mats/Pairs/unassigned-Textured preview colors shade
    /// with — the Preview toolbar's own gallery-style picker, matching 6
    /// specific balls from Blender's own MatCap picker.</summary>
    public void SetMatCapPreset(int preset)
    {
        if (_sharedChunkMat == null) return;
        _sharedChunkMat.SetInt("_MatCapPreset", preset);
    }

    private void RebuildChunk(Chunk c)
    {
        EnsureUv1Profile();

        var qx = Mathf.Min(CHUNK_SIZE, GridWidth  - c.CX * CHUNK_SIZE);
        var qz = Mathf.Min(CHUNK_SIZE, GridHeight - c.CZ * CHUNK_SIZE);
        var vx = qx + 1; var vz = qz + 1;
        var n = vx * vz;

        var verts   = new Vector3[n];
        var normals = new Vector3[n];
        var uv1arr  = new Vector2[n]; // raw world XZ — wireframe/costs/height-grid overlays AND RNM basis UV all key off this; do not repurpose it for tiling.
        var uv2arr  = new Vector2[n]; // world UV [0..1]
        var uv3arr  = new Vector2[n]; // stretch-compensated tiling UV (shader applies each pair's own scale)

        var ox = c.CX * CHUNK_SIZE; // origin in grid
        var oz = c.CZ * CHUNK_SIZE;

        for (var lz = 0; lz < vz; lz++)
        for (var lx = 0; lx < vx; lx++)
        {
            var gx = ox + lx;
            var gz = oz + lz;
            var wx = gx * CellSize;
            var wz = gz * CellSize;
            var h  = GetHeight(gx, gz);

            var vi = lz * vx + lx;
            verts[vi]  = new Vector3(wx, h, wz);
            uv1arr[vi] = new Vector2(wx, wz); // raw world XZ; shader applies tiling
            uv2arr[vi] = new Vector2((float)gx / GridWidth, (float)gz / GridHeight);
            // Exact grid vertex (gx,gz) — a direct profile lookup, no
            // bilinear sampling needed (unlike the exporter's own
            // Sample(), whose simplified submesh vertices can land between
            // grid points).
            uv3arr[vi] = new Vector2(_uv1ProfileU[gx], _uv1ProfileV[gz]);

            // Normal from finite differences (grid neighbours)
            var hL = GetHeight(Mathf.Max(0, gx-1), gz);
            var hR = GetHeight(Mathf.Min(GridWidth, gx+1), gz);
            var hD = GetHeight(gx, Mathf.Max(0, gz-1));
            var hU = GetHeight(gx, Mathf.Min(GridHeight, gz+1));
            normals[vi] = new Vector3(hL-hR, 2f*CellSize, hD-hU).normalized;
        }

        c.Mesh.SetVertices(verts);
        c.Mesh.SetNormals(normals);
        c.Mesh.SetUVs(0, uv1arr);
        c.Mesh.SetUVs(1, uv2arr);
        c.Mesh.SetUVs(2, uv3arr);
        c.Mesh.RecalculateBounds();
    }

    private void RebuildDirtyChunks(Vector3 worldPos, float radius)
    {
        var cs = CHUNK_SIZE * CellSize;
        foreach (var c in _chunks)
        {
            var cx = c.CX * cs + cs * .5f;
            var cz = c.CZ * cs + cs * .5f;
            var dx = cx - worldPos.x; var dz = cz - worldPos.z;
            if (dx*dx + dz*dz <= (radius + cs) * (radius + cs))
                RebuildChunk(c);
        }
    }

    // ── LOD ──────────────────────────────────────────────────────────────────

    public void UpdateLOD(Vector3 camPos) { /* Simple: no LOD for now — full res */ }

    // ── Heights API ───────────────────────────────────────────────────────────

    public float GetHeight(int gx, int gz)
    {
        gx = Mathf.Clamp(gx, 0, GridWidth);
        gz = Mathf.Clamp(gz, 0, GridHeight);
        return _heights[gz * _stride + gx];
    }

    public void SetHeight(int gx, int gz, float h)
    {
        if ((uint)gx > (uint)GridWidth || (uint)gz > (uint)GridHeight) return;
        _heights[gz * _stride + gx] = SC2TerrainData.ClampHeight(h);
    }

    public float[] GetHeightsCopy() { var c = new float[_heights.Length]; _heights.CopyTo(c, 0); return c; }

    /// <summary>
    /// Restore PairMapRT from CPU TrianglePairIndex array (called on load).
    /// Writes pair index as R=N/255 into each corresponding quad texel.
    /// </summary>
    public void RestorePairMapFromData(SC2TerrainData data)
    {
        var w = GridWidth; var h = GridHeight;
        if (data.TrianglePairIndex.Length < w * h * 2) return;

        // Build byte array: one byte per quad = pair index
        var bytes = new byte[w * h * 4]; // RGBA32
        for (var gz = 0; gz < h; gz++)
        for (var gx = 0; gx < w; gx++)
        {
            var ti   = (gz * w + gx) * 2;
            var pi   = data.TrianglePairIndex[ti]; // pair index, -1=unassigned
            var val  = (pi < 0) ? (byte)255 : (byte)Mathf.Clamp(pi, 0, 254);
            var idx  = (gz * w + gx) * 4;
            bytes[idx] = val; bytes[idx+1] = 0; bytes[idx+2] = 0; bytes[idx+3] = 255;
        }

        // Upload to PairMapRT via Texture2D blit
        var tex  = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        tex.LoadRawTextureData(bytes);
        tex.Apply(false);
        var prevActive = RenderTexture.active;
        Graphics.Blit(tex, PairMapRT);
        RenderTexture.active = prevActive;
        DestroyObject(tex);
    }

    /// <summary>
    /// Restore BlendRT from saved RnmMaskBytes on project load.
    /// Groups by RnmMaskName — loads the first mask group found.
    /// </summary>
    public void RestoreBlendRTFromData(SC2TerrainData data)
    {
        if (data.Pairs.Count == 0) return;

        // Find first pair with valid RnmMaskBytes — use its mask as the active BlendRT
        // Group by RnmMaskName: load the "mask01" group (or first available)
        SC2MaterialPair found = null;
        // First try the default mask name
        foreach (var p in data.Pairs)
        {
            var maskName = string.IsNullOrEmpty(p.RnmMaskName) ? "mask01" : p.RnmMaskName;
            if (maskName == "mask01" && p.RnmMaskBytes.Length == p.MaskWidth * p.MaskHeight * 4)
            { found = p; break; }
        }
        // Fallback: any pair with valid bytes
        if (found == null)
            foreach (var p in data.Pairs)
                if (p.RnmMaskBytes.Length == p.MaskWidth * p.MaskHeight * 4)
                { found = p; break; }

        if (found == null) return; // no saved mask data

        // Load bytes → Texture2D → blit to BlendRT
        var tex = new Texture2D(found.MaskWidth, found.MaskHeight, TextureFormat.RGBA32, false, true);
        tex.LoadRawTextureData(found.RnmMaskBytes);
        tex.Apply(false);
        var prevActive = RenderTexture.active;
        Graphics.Blit(tex, BlendRT);
        RenderTexture.active = prevActive;
        DestroyObject(tex);
        _activeBlendMaskName = NormalizeMaskName(found.RnmMaskName);
        AttachLayerStack(data);

        UnityEngine.Debug.Log($"[SC2Runtime] Restored BlendRT from pair '{found.Name}' mask '{found.RnmMaskName}'");
    }

    private static string NormalizeMaskName(string maskName)
    {
        return string.IsNullOrWhiteSpace(maskName) ? "mask01" : maskName.Trim();
    }

    public void UploadHeightsCPU(float[] heights)
    {
        if (heights == null || heights.Length == 0) return;
        var expected = _stride * (GridHeight + 1);
        if (heights.Length != expected)
        { Debug.LogWarning($"[SC2Runtime] Height size mismatch {heights.Length} vs {expected}"); return; }
        for (var i = 0; i < _heights.Length; i++)
            _heights[i] = SC2TerrainData.ClampHeight(heights[i]);
        RebuildAllChunks();
    }

    public void ImportHeightmap(Texture2D tex, float scale)
    {
        HeightScale = scale;
        var pixels = tex.GetPixels();
        var tw = tex.width; var th = tex.height;
        var vw = GridWidth+1; var vh = GridHeight+1;
        for (var gz = 0; gz < vh; gz++)
        for (var gx = 0; gx < vw; gx++)
        {
            var px = Mathf.Clamp(Mathf.RoundToInt((float)gx/GridWidth*(tw-1)), 0, tw-1);
            var py = Mathf.Clamp(Mathf.RoundToInt((float)gz/GridHeight*(th-1)), 0, th-1);
            _heights[gz*_stride+gx] = SC2TerrainData.ClampHeight(pixels[py*tw+px].grayscale * scale);
        }
        RebuildAllChunks();
    }

    // ── GPU sculpt brush ──────────────────────────────────────────────────────
    // Modifies CPU heights directly, then rebuilds dirty chunks

    public void SculptBrush(Vector3 worldPos, float radius, float strength, float hardness,
                             int mode,
                             Color32[] brushMask = null, int brushMaskW = 0, int brushMaskH = 0,
                             float brushRotationRad = 0f, Vector2 brushMirror = default, bool brushSwapAxes = false,
                             Vector3 symmetryCenter = default, float slotRotationDeg = 0f)
    {
        if (brushMirror == default) brushMirror = Vector2.one;
        // Noise (mode 4) below needs its OWN mirror/rotation transform, separate from
        // brushMirror/brushRotationRad above (which shape the custom-stamp FALLOFF, not the noise
        // pattern) — see that case's own comment.
        var slotRotationRad = -slotRotationDeg * Mathf.Deg2Rad;
        var r2 = radius * radius;
        // 2x the effect the raw Strength value produces (i.e. at Strength's
        // max of 1, the brush now moves twice as much height per tick as
        // before) — the 0-1 Strength slider range itself is unchanged, only
        // its actual power.
        var dt = strength * 0.016f * 60f;
        var x0 = Mathf.Max(0,          Mathf.FloorToInt((worldPos.x - radius) / CellSize));
        var x1 = Mathf.Min(GridWidth,  Mathf.CeilToInt ((worldPos.x + radius) / CellSize));
        var z0 = Mathf.Max(0,          Mathf.FloorToInt((worldPos.z - radius) / CellSize));
        var z1 = Mathf.Min(GridHeight, Mathf.CeilToInt ((worldPos.z + radius) / CellSize));

        for (var gz = z0; gz <= z1; gz++)
        for (var gx = x0; gx <= x1; gx++)
        {
            var wx = gx * CellSize; var wz = gz * CellSize;
            var dx = wx - worldPos.x; var dz = wz - worldPos.z;
            var d2 = dx*dx + dz*dz;
            // A custom stamp used to paint beyond the round brush-radius
            // indicator; it is now clipped to this same circle too (inside
            // SampleBrushMask), so the plain circular pre-clip here is safe
            // again regardless of whether a mask is active.
            if (d2 > r2) continue;
            var falloff = brushMask != null
                ? SampleBrushMask(brushMask, brushMaskW, brushMaskH, dx, dz, radius, brushRotationRad, brushMirror, brushSwapAxes)
                : HardnessFalloff(Mathf.Sqrt(d2) / radius, hardness);
            if (falloff <= 0f) continue;
            var amt     = falloff * dt;
            var idx     = gz * _stride + gx;
            switch (mode)
            {
                case 0: _heights[idx] += amt; break;
                case 1: _heights[idx] -= amt; break;
                case 2: _heights[idx]  = Mathf.Lerp(_heights[idx], AvgH(gx,gz), amt*.5f); break;
                case 4:
                {
                    // Sampling Perlin noise at the vertex's own absolute world position (wx, wz)
                    // made a symmetric copy's bumps completely uncorrelated with the original —
                    // Perlin noise isn't symmetric around an arbitrary point, so a mirrored/rotated
                    // copy needs to sample the SAME noise VALUE its slot-0 counterpart would, by
                    // mapping this vertex back to that counterpart's position (mirror is its own
                    // inverse; -slotRotationDeg undoes a radial copy's own rotation) before
                    // sampling — identical to how GetSlotRotationDegrees/GetSlotMirror already
                    // describe the position relationship itself, just applied to a single vertex
                    // instead of the whole brush.
                    var relX = wx - symmetryCenter.x; var relZ = wz - symmetryCenter.z;
                    var canonX = brushSwapAxes ? relZ : relX;
                    var canonZ = brushSwapAxes ? relX : relZ;
                    canonX *= brushMirror.x; canonZ *= brushMirror.y;
                    if (Mathf.Abs(slotRotationRad) > 0.0001f)
                    {
                        var s = Mathf.Sin(slotRotationRad); var c = Mathf.Cos(slotRotationRad);
                        var rx = canonX * c - canonZ * s; var rz = canonX * s + canonZ * c;
                        canonX = rx; canonZ = rz;
                    }
                    canonX += symmetryCenter.x; canonZ += symmetryCenter.z;
                    _heights[idx] += (Mathf.PerlinNoise(canonX * .05f, canonZ * .05f) - .5f) * amt * 2;
                    break;
                }
            }
            _heights[idx] = SC2TerrainData.ClampHeight(_heights[idx]);
        }
        RebuildDirtyChunks(worldPos, radius);
    }

    /// <summary>
    /// Blender-style Flatten: samples the plane of the terrain under the brush (see SC2FlattenMath) and
    /// pulls the vertices in the radius toward it. With <paramref name="lockPlane"/> the plane is sampled
    /// once, on the first dab of a stroke, and kept in <paramref name="lockedPlane"/> for the rest of it
    /// (Blender's "Original Plane"); otherwise it is re-sampled under the brush on every dab.
    /// </summary>
    public void SculptFlatten(Vector3 worldPos, float radius, float strength, float hardness,
                              ref SC2FlattenPlane lockedPlane, bool lockPlane,
                              Color32[] brushMask = null, int brushMaskW = 0, int brushMaskH = 0,
                              float brushRotationRad = 0f, bool rebuildChunks = true,
                              Vector2 brushMirror = default, bool brushSwapAxes = false)
    {
        if (brushMirror == default) brushMirror = Vector2.one;
        var r2 = radius * radius;
        float Weight(float dx, float dz)
        {
            var d2 = dx * dx + dz * dz;
            if (d2 > r2) return 0f;
            return brushMask != null
                ? SampleBrushMask(brushMask, brushMaskW, brushMaskH, dx, dz, radius, brushRotationRad, brushMirror, brushSwapAxes)
                : SC2FlattenMath.SmoothFalloff(Mathf.Sqrt(d2) / radius, hardness);
        }

        SC2FlattenPlane plane;
        if (lockPlane && lockedPlane.Valid) plane = lockedPlane;
        else
        {
            // The plane comes from the middle of the brush only, unweighted (see SC2FlattenMath.AreaRadiusFactor).
            var areaRadius = radius * SC2FlattenMath.AreaRadiusFactor;
            plane = SC2FlattenMath.ComputeAreaPlane(_heights, GridWidth, GridHeight, CellSize, worldPos, areaRadius,
                (dx, dz) => SC2FlattenMath.AreaWeight(radius, dx, dz));
            if (lockPlane) lockedPlane = plane;
        }

        // Same per-dab power as the other sculpt brushes (SculptBrush's dt).
        var amount = strength * 0.016f * 60f;
        if (!SC2FlattenMath.ApplyToHeights(_heights, GridWidth, GridHeight, CellSize, worldPos, radius, amount, plane,
                Weight, SC2TerrainData.MinHeight, SC2TerrainData.MaxHeight))
            return;
        if (rebuildChunks) RebuildDirtyChunks(worldPos, radius);
    }

    /// <summary>Rebuilds the chunk meshes within <paramref name="radius"/> of a position — for a caller
    /// that applied several dabs with rebuildChunks off and rebuilds once over the swept area.</summary>
    public void RebuildChunksAround(Vector3 worldPos, float radius) => RebuildDirtyChunks(worldPos, radius);

    /// <summary>
    /// Set exact height within brush radius with hardness-controlled edge.
    /// hardness=0 → smooth cosine blend, hardness=1 → instant hard step.
    /// </summary>
    public void SculptSetHeight(Vector3 worldPos, float radius, float strength, float hardness, float targetH,
                                 Color32[] brushMask = null, int brushMaskW = 0, int brushMaskH = 0,
                                 float brushRotationRad = 0f, Vector2 brushMirror = default, bool brushSwapAxes = false)
    {
        if (brushMirror == default) brushMirror = Vector2.one;
        var r2 = radius * radius;
        var x0 = Mathf.Max(0,          Mathf.FloorToInt((worldPos.x - radius) / CellSize));
        var x1 = Mathf.Min(GridWidth,  Mathf.CeilToInt ((worldPos.x + radius) / CellSize));
        var z0 = Mathf.Max(0,          Mathf.FloorToInt((worldPos.z - radius) / CellSize));
        var z1 = Mathf.Min(GridHeight, Mathf.CeilToInt ((worldPos.z + radius) / CellSize));

        for (var gz = z0; gz <= z1; gz++)
        for (var gx = x0; gx <= x1; gx++)
        {
            var wx = gx * CellSize; var wz = gz * CellSize;
            var dx = wx - worldPos.x; var dz = wz - worldPos.z;
            var d2 = dx*dx + dz*dz;
            // See SculptBrush's own comment — the circular pre-clip is safe
            // for the mask case too now (SampleBrushMask clips to the same
            // circle internally).
            if (d2 > r2) continue;
            var falloff = brushMask != null
                ? SampleBrushMask(brushMask, brushMaskW, brushMaskH, dx, dz, radius, brushRotationRad, brushMirror, brushSwapAxes)
                : HardnessFalloff(Mathf.Sqrt(d2) / radius, hardness);
            var blend = Mathf.Clamp01(falloff * Mathf.Max(0.01f, strength));
            var idx   = gz * _stride + gx;
            _heights[idx] = SC2TerrainData.ClampHeight(Mathf.Lerp(_heights[idx], SC2TerrainData.ClampHeight(targetH), blend));
        }
        RebuildDirtyChunks(worldPos, radius);
    }

    /// <summary>CPU mirror of SC2PaintBrush.shader's custom-stamp branch —
    /// clips to the same circular radius boundary every other brush respects
    /// (the square stamp footprint's corners
    /// reach sqrt(2)*radius, visibly outside the round brush-radius
    /// indicator, before this clip was added), then mirrors/rotates
    /// (dx,dz)/radius and bilinear-samples mask's luminance*alpha at the
    /// resulting stamp UV. Returns 0 outside the circle (equivalent to "no
    /// effect on this vertex"). brushMirror/brushSwapAxes mirror a REFLECTED
    /// symmetric copy's stamp the same way SC2PaintBrush.shader's own
    /// _BrushMirror/_BrushSwapAxes do for RNM painting — see
    /// SC2SymmetryMath.GetSlotMirror — since a caller sculpting with symmetry
    /// and a custom brush needs the SAME per-copy mirroring/rotation a custom
    /// RNM stroke already gets, or a mirrored copy sculpts an identical,
    /// unmirrored bump instead of one facing the other way.</summary>
    private static float SampleBrushMask(Color32[] mask, int w, int h, float dx, float dz, float radius, float rotationRad,
        Vector2 brushMirror, bool brushSwapAxes)
    {
        var ox = dx / radius; var oz = dz / radius;
        // Circular clip on the UNROTATED offset — the boundary itself must
        // not rotate with the stamp content.
        if (ox * ox + oz * oz > 1f) return 0f;

        var mx = brushSwapAxes ? oz : ox;
        var mz = brushSwapAxes ? ox : oz;
        mx *= brushMirror.x; mz *= brushMirror.y;

        if (Mathf.Abs(rotationRad) > 0.0001f)
        {
            var s = Mathf.Sin(rotationRad); var c = Mathf.Cos(rotationRad);
            var rx = mx * c - mz * s; var rz = mx * s + mz * c;
            mx = rx; mz = rz;
        }
        ox = mx; oz = mz;
        if (Mathf.Abs(ox) > 1f || Mathf.Abs(oz) > 1f) return 0f;
        // The stamp texture's row order runs opposite the brush's own world-space Z (north/+Z is
        // the bottom row, not the top), so V is flipped here, once, after every other transform —
        // same fix in SC2PaintBrush.shader's matching stampUv line, for the identical reason.
        var u = ox * 0.5f + 0.5f; var v = -oz * 0.5f + 0.5f;
        var fx = u * (w - 1); var fy = v * (h - 1);
        var x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, w - 1);
        var y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, h - 1);
        var x1 = Mathf.Min(x0 + 1, w - 1);
        var y1 = Mathf.Min(y0 + 1, h - 1);
        var tx = fx - x0; var ty = fy - y0;
        // See SC2PaintBrush.shader's matching fix for the full reasoning —
        // a plain grayscale stamp (no real alpha channel) decodes with
        // alpha=1 uniformly, its shape sitting in RGB instead; luminance*
        // alpha reads either convention (grayscale-only, or a real alpha-
        // transparency brush) correctly.
        float Sample(int x, int y)
        {
            var c = mask[y * w + x];
            var luminance = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
            return luminance * (c.a / 255f);
        }
        return Mathf.Lerp(Mathf.Lerp(Sample(x0, y0), Sample(x1, y0), tx), Mathf.Lerp(Sample(x0, y1), Sample(x1, y1), tx), ty);
    }

    // Falloff: hardness=0 → cosine, hardness=1 → full strength with hard edge
    private static float HardnessFalloff(float t, float hardness)
    {
        if (hardness >= 0.999f) return 1f;
        var soft = Mathf.Cos(t * Mathf.PI * .5f);
        // Hard zone: [0..hardness] → 1, then cosine falloff to edge
        float hard;
        if (t <= hardness) hard = 1f;
        else hard = Mathf.Cos((t - hardness) / Mathf.Max(1f - hardness, 0.001f) * Mathf.PI * .5f);
        return Mathf.Lerp(soft, hard, hardness);
    }

    private float AvgH(int gx, int gz)
    {
        var s = 0f; var n = 0;
        for (var dz=-1;dz<=1;dz++) for (var dx=-1;dx<=1;dx++)
        {
            if (dx==0&&dz==0) continue;
            var nx=gx+dx; var nz=gz+dz;
            if ((uint)nx<=(uint)GridWidth && (uint)nz<=(uint)GridHeight)
            { s+=_heights[nz*_stride+nx]; n++; }
        }
        return n>0 ? s/n : _heights[gz*_stride+gx];
    }

    // ── GPU pair paint ────────────────────────────────────────────────────────

    public void PaintPair(Vector3 worldPos, float radius, float strength, int pairIdx, bool erase=false)
    {
        if (!EnsureShaders()) return;
        var uvX = worldPos.x / (GridWidth  * CellSize);
        var uvZ = worldPos.z / (GridHeight * CellSize);
        var uvR = Mathf.Max(radius / (GridWidth * CellSize), 0.001f);
        _paintBrushMat.SetVector("_BrushUV",      new Vector4(uvX, uvZ, 0, 0));
        _paintBrushMat.SetFloat ("_BrushRadius",  uvR);
        _paintBrushMat.SetFloat ("_BrushStrength",strength);
        _paintBrushMat.SetColor ("_PaintColor",   new Color(pairIdx/255f, 0, 0, 1));
        _paintBrushMat.SetInt   ("_PaintMode",    erase ? 1 : 0);
        var tmp = RenderTexture.GetTemporary(PairMapRT.descriptor);
        var prevActive = RenderTexture.active;
        Graphics.Blit(PairMapRT, tmp, _paintBrushMat);
        Graphics.Blit(tmp, PairMapRT);
        // Graphics.Blit leaves RenderTexture.active pointed at its destination
        // (PairMapRT here) as a side effect. This is called mid-frame from
        // OnGUI mouse-drag handling, so if left unrestored, every subsequent
        // IMGUI draw call for the rest of that frame — the whole rest of the
        // editor UI — would render into PairMapRT instead of the screen.
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(tmp);
    }

    public void PaintBlend(Vector3 worldPos, float radius, float strength, float blendValue, bool erase=false, Texture customBrush=null, float rotationRadians=0f,
        Vector2 brushMirror = default, bool brushSwapAxes = false, bool removeFromLayer = false)
    {
        if (brushMirror == default) brushMirror = Vector2.one;
        if (!EnsureShaders()) return;
        // With editing layers the stroke goes into the active layer's texture and BlendRT is re-flattened by the
        // caller once per tick (RecompositeBlendIfDirty); without a stack the mask is painted directly as before.
        if (HasActiveLayers)
        {
            PaintActiveLayer(worldPos, radius, strength, blendValue, erase, removeFromLayer, customBrush, rotationRadians, brushMirror, brushSwapAxes);
            return;
        }
        ConfigureBlendBrush(worldPos, radius, strength, blendValue, erase, customBrush, rotationRadians, brushMirror, brushSwapAxes, 2);
        var tmp = RenderTexture.GetTemporary(BlendRT.descriptor);
        var prevActive = RenderTexture.active;
        Graphics.Blit(BlendRT, tmp, _paintBrushMat);
        Graphics.Blit(tmp, BlendRT);
        // See PaintPair above: must restore, or the rest of this OnGUI frame's
        // UI gets rendered into BlendRT instead of the screen — this is the
        // exact bug that caused exported RNM masks to contain a captured
        // frame of the editor's own UI.
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(tmp);
    }

    // ── Preview modes ─────────────────────────────────────────────────────────

    public void SetPreviewMode(int mode)
    {
        if (_sharedChunkMat != null)
        {
            _sharedChunkMat.SetInt("_PreviewMode", mode);
            ApplyPreviewBrightness(_sharedChunkMat, mode);
        }
    }

    public void SetHeightGradientOverlay(bool enabled, float minHeight, float maxHeight)
    {
        if (_sharedChunkMat == null) return;
        if (maxHeight <= minHeight) maxHeight = minHeight + 1f;
        _sharedChunkMat.SetInt("_HeightGradientEnabled", enabled ? 1 : 0);
        _sharedChunkMat.SetVector("_HeightGradientMinMax", new Vector4(minHeight, maxHeight, 0f, 0f));
    }

    public void SetAllPairMaterials(PairMaterialData[] pairs, int previewMode, int bakeGeneration = 0)
    {
        var n = Mathf.Min(pairs?.Length ?? 0, 72);
        _lastPairCountForGlobals = Mathf.Max(n, 1);
        RebuildDiffuseArrays(pairs, n);
        // RebuildRnmArrays does a full CPU readback (CopyRawToArray) per
        // layer, same cost class as RebuildDiffuseArrays. Only rebuild the
        // real per-pair content when PreviewMode==4 is actually going to
        // sample it, AND only when bakeGeneration (SC2TerrainData.
        // RnmBakeGeneration, bumped once per completed Bake) has actually
        // moved since the last rebuild — without this second check, simply
        // leaving the RNM preview mode on while sculpting/painting (each of
        // which calls ApplyPreviewModeToChunks for unrelated reasons) redid
        // this full rebuild on every such call, a real perf regression with
        // no equivalent cost before real baked-data sampling existed.
        if (previewMode == 4) RebuildRnmArrays(pairs, n, bakeGeneration);
        else EnsureFallbackRnmArrays();
        RebuildLUTs(pairs, n);
        var mat = _sharedChunkMat;
        if (mat == null) return;
        mat.SetTexture("_PairMapTex",  PairMapRT);
        mat.SetTexture("_BlendTex",    BlendRT);
        mat.SetTexture("_PairLUT",     _pairLUT);
        mat.SetTexture("_TilingLUT",   _tilingLUT);
        if (_diffuse1Array != null) mat.SetTexture("_Diffuse1Arr", _diffuse1Array);
        if (_diffuse2Array != null) mat.SetTexture("_Diffuse2Arr", _diffuse2Array);
        mat.SetTexture("_Diffuse1Fallback", _diffuse1Fallback != null ? _diffuse1Fallback : Texture2D.whiteTexture);
        mat.SetTexture("_Diffuse2Fallback", _diffuse2Fallback != null ? _diffuse2Fallback : Texture2D.whiteTexture);
        if (_rnmArray != null) mat.SetTexture("_RnmArr", _rnmArray);
        mat.SetTexture("_RnmFallback", _rnmFallback != null ? _rnmFallback : GreyTexture());
        // Was Application.isEditor ? 1 : 0 — that always evaluated to 0 in a
        // standalone Build & Run player, so Textured mode with 2+ material
        // pairs could never sample Texture2DArray there and silently fell back
        // to a single flat diffuse texture for the whole terrain. Array
        // texture support is a GPU/platform capability, not an editor-vs-build
        // distinction.
        mat.SetInt("_UseArrayTextures", SystemInfo.supports2DArrayTextures ? 1 : 0);
        mat.SetColor("_AmbientColor",  Color.white);
        mat.SetInt  ("_PairCount",     Mathf.Max(n,1));
        mat.SetInt  ("_PreviewMode",   previewMode);
        ApplyPreviewBrightness(mat, previewMode);
    }

    private static void ApplyPreviewBrightness(Material mat, int previewMode)
    {
        if (mat == null || !mat.HasProperty("_TexturedBrightness"))
            return;

        // Mode 4 (TexturedRNM) does its own readability fix directly in the
        // shader (a contrast stretch around the midtone, RNM_CONTRAST) —
        // a flat brightness multiplier here was tried first and just pushed
        // everything toward saturating white instead of making the shadow
        // read as darker. See SC2TerrainView.shader's own comment on this.
        mat.SetFloat("_TexturedBrightness", previewMode == 0 ? 2f : 1f);
    }

    public struct PairMaterialData
    {
        public Texture2D Diffuse1, Diffuse2;
        public Color     Color1, Color2;
        public float     TilingScale;
        // Baked RNM basis textures (SC2MaterialPair.RnmPreviewMask0/1/2) —
        // null when the pair has never been baked, in which case
        // RebuildRnmArrays' own CopyRawToArray null-fallback fills that
        // layer with flat mid-grey instead of leaving it undefined.
        public Texture2D Rnm0, Rnm1, Rnm2;
    }

    private void RebuildLUTs(PairMaterialData[] pairs, int n)
    {
        if (_pairLUT == null)
            _pairLUT = new Texture2D(72,2,TextureFormat.RGBA32,false,true)
                { filterMode=FilterMode.Point, wrapMode=TextureWrapMode.Clamp, name="SC2_PairLUT" };
        if (_tilingLUT == null)
            _tilingLUT = new Texture2D(72,1,TextureFormat.RGBAFloat,false,true)
                { filterMode=FilterMode.Point, wrapMode=TextureWrapMode.Clamp, name="SC2_TilingLUT" };
        var row0=new Color[72]; var row1=new Color[72]; var tiling=new Color[72];
        for (var i=0;i<72;i++)
        {
            row0[i]   = (pairs!=null&&i<n) ? pairs[i].Color1 : Color.grey;
            row1[i]   = (pairs!=null&&i<n) ? pairs[i].Color2 : Color.grey;
            var ts    = (pairs!=null&&i<n&&pairs[i].TilingScale>0) ? 1f/pairs[i].TilingScale : 0.25f;
            // .a was always 1 (unused) — repurposed as a per-pair "has real
            // baked RNM data" flag (Rnm0 is only non-null when shaderMode==4
            // AND the pair was actually baked — see ApplyPreviewModeToChunksFor)
            // so PreviewMode==4 can fall back to plain sun-lit shading for an
            // un-baked pair instead of running the RNM formula against a
            // meaningless grey placeholder — matches the real game's own
            // _HasRNM gate (SC2TerrainMaterialFactory/SC2Terrain.shader),
            // which falls back to normal ComputeLight sun shading rather
            // than RNM math when a material has no real mask data.
            var hasBakedRnm = pairs != null && i < n && pairs[i].Rnm0 != null ? 1f : 0f;
            tiling[i] = new Color(ts,ts,ts,hasBakedRnm);
        }
        _pairLUT.SetPixels(0,0,72,1,row0); _pairLUT.SetPixels(0,1,72,1,row1); _pairLUT.Apply(false);
        _tilingLUT.SetPixels(tiling); _tilingLUT.Apply(false);
    }

    public void ApplyPairColourView(SC2TerrainData data, bool blend)
    {
        if (_pairColorViewMat == null)
        {
            var s = Shader.Find("Hidden/SC2/PairColorView");
            if (s == null)
                s = Resources.Load<Shader>("TerrainEditorShaders/SC2PairColorView");
            if (s==null) return;
            _pairColorViewMat = new Material(s) { hideFlags=HideFlags.HideAndDontSave };
        }
        var count = Mathf.Min(data.Pairs.Count, 72);
        var cols  = new Vector4[72];
        for (var i=0;i<count;i++) { var c=data.Pairs[i].PairColor; cols[i]=new Vector4(c.r,c.g,c.b,1); }
        _pairColorViewMat.SetVectorArray("_PairColors", cols);
        _pairColorViewMat.SetInt("_PairCount", count);
        if (_pairColorRT==null||_pairColorRT.width!=PairMapRT.width)
        {
            SafeRelease(_pairColorRT);
            _pairColorRT=new RenderTexture(PairMapRT.width,PairMapRT.height,0,RenderTextureFormat.ARGB32)
                { filterMode=FilterMode.Point, name="SC2_PairColor" };
            _pairColorRT.Create();
        }
        var prevActivePCV = RenderTexture.active;
        Graphics.Blit(PairMapRT, _pairColorRT, _pairColorViewMat);
        RenderTexture.active = prevActivePCV;
        SetPairColorRT(_pairColorRT);
    }

    public void RestorePairIndexView() => SetPairColorRT(PairMapRT);

    private void SetPairColorRT(RenderTexture rt)
    {
        if (_sharedChunkMat != null) _sharedChunkMat.SetTexture("_PairColorTex", rt ?? PairMapRT);
    }

    // ── CPU readback (export) ─────────────────────────────────────────────────

    /// <summary>
    /// Read PairMapRT to CPU using GetPixels() — most reliable cross-platform method.
    /// Returns byte[] where each byte = pair index (0..254), 255=unassigned.
    /// </summary>
    public byte[] ReadPairIndexCPU()
    {
        var w = PairMapRT.width; var h = PairMapRT.height;
        // See ReadBlendBytes above: prev must be captured BEFORE the Blit,
        // since Blit leaves RenderTexture.active pointed at its destination.
        var prev = RenderTexture.active;
        // Blit to a temporary ARGB32 RT, read via Texture2D.GetPixels()
        // This path works correctly on D3D11 for R8→ARGB32 conversion
        var tmp  = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                               RenderTextureReadWrite.Linear);
        Texture2D tex = null;
        try
        {
            Graphics.Blit(PairMapRT, tmp);
            RenderTexture.active = tmp;
            tex = new Texture2D(w, h, TextureFormat.ARGB32, false, true);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }

        // ARGB32: byte layout = A,R,G,B per pixel
        var raw = tex.GetRawTextureData<byte>();
        var res = new byte[w * h];
        for (var i = 0; i < res.Length; i++)
            res[i] = raw[i * 4 + 1]; // R byte in ARGB32
        DestroyObject(tex);
        return res;
    }

    private bool _pairReadbackPending;

    /// <summary>
    /// Non-blocking GPU→CPU readback of PairMapRT via AsyncGPUReadback.
    /// Unlike ReadPairIndexCPU (synchronous ReadPixels — blocks the main thread
    /// until the GPU finishes ALL pending work), this queues a request and
    /// invokes the callback once data is actually ready. Use this on MouseUp
    /// for interactive painting — the sync version causes multi-second "Hold on"
    /// stalls at large grid resolutions.
    /// </summary>
    public void ReadPairIndexCPUAsync(System.Action<byte[]> onComplete)
    {
        if (_pairReadbackPending) return;
        _pairReadbackPending = true;

        var w = PairMapRT.width; var h = PairMapRT.height;
        var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                              RenderTextureReadWrite.Linear);
        // AsyncGPUReadback.Request reads tmp directly by reference — it doesn't
        // need RenderTexture.active — but Graphics.Blit still leaves
        // RenderTexture.active pointed at tmp as a side effect regardless. Left
        // unrestored, that dangling reference would still be "active" for
        // every GUI draw call for the rest of this frame, and later gets
        // released out from under it when the async callback below fires.
        var prevActive = RenderTexture.active;
        Graphics.Blit(PairMapRT, tmp);
        RenderTexture.active = prevActive;

        UnityEngine.Rendering.AsyncGPUReadback.Request(tmp, 0, TextureFormat.ARGB32, request =>
        {
            _pairReadbackPending = false;
            RenderTexture.ReleaseTemporary(tmp);
            if (request.hasError)
            {
                // Do NOT fall back to the sync path — that reintroduces the stall.
                // Skip this sync; next paint stroke retries.
                return;
            }
            var raw = request.GetData<byte>();
            var res = new byte[w * h];
            for (var i = 0; i < res.Length; i++)
                res[i] = raw[i * 4 + 1]; // R byte in ARGB32
            onComplete?.Invoke(res);
        });
    }

    public byte[] ReadBlendCPU()
    {
        var tex  = new Texture2D(BlendRT.width,BlendRT.height,TextureFormat.RGBA32,false);
        var prev = RenderTexture.active;
        RenderTexture.active = BlendRT;
        tex.ReadPixels(new Rect(0,0,tex.width,tex.height),0,0); tex.Apply();
        RenderTexture.active = prev;
        var raw = tex.GetRawTextureData<byte>();
        var res = new byte[raw.Length]; raw.CopyTo(res);
        DestroyObject(tex);
        return res;
    }

    /// <summary>Inverse of ReadBlendCPU — for RNM Undo/Redo (see
    /// SC2PaintMode.ApplyActiveBlendState). Same load-bytes-into-Texture2D-
    /// then-Blit pattern LoadMaskGroupToRT already uses, exposed publicly
    /// since undo restores an exact previously-read byte[] rather than a
    /// pair's persisted RnmMaskBytes.</summary>
    public void UploadBlendCPU(byte[] bytes)
    {
        if (BlendRT == null || bytes == null)
            return;

        var tex = new Texture2D(BlendRT.width, BlendRT.height, TextureFormat.RGBA32, false, true);
        tex.LoadRawTextureData(bytes);
        tex.Apply(false);
        // prev must be captured BEFORE the Blit, not after — see
        // ReadBlendBytes's own doc comment for why (Graphics.Blit leaves
        // RenderTexture.active pointed at its destination as a side effect).
        var prev = RenderTexture.active;
        Graphics.Blit(tex, BlendRT);
        RenderTexture.active = prev;
        DestroyObject(tex);
    }

    private Texture2D _blendPreviewSnapshot;

    /// <summary>
    /// Renders the current BlendRT into a small CPU-owned Texture2D and returns
    /// it, for the Paint Blend preview swatch. This exists specifically so the
    /// UI never draws the live BlendRT RenderTexture through IMGUI: IMGUI batches
    /// and defers its actual GPU draws to the end of the frame, and drawing a
    /// live RenderTexture that way behaves differently in a standalone player
    /// than in the Editor — the deferred draw could bind BlendRT as the active
    /// target and composite the whole UI frame straight into it, which is
    /// exactly how a screenshot of the editor's own interface ended up baked
    /// into the terrain's blend mask in Build & Run. A plain Texture2D drawn via
    /// IMGUI has no such feedback path. The snapshot is downscaled (cheap) and
    /// callers throttle how often they refresh it.
    /// </summary>
    public Texture2D GetBlendPreviewSnapshot()
    {
        if (BlendRT == null) return null;
        const int size = 128;
        if (_blendPreviewSnapshot == null)
            _blendPreviewSnapshot = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
                { name = "SC2_BlendPreviewSnapshot", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };

        var prev = RenderTexture.active;
        var tmp = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(BlendRT, tmp);
            RenderTexture.active = tmp;
            _blendPreviewSnapshot.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            _blendPreviewSnapshot.Apply(false);
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }
        return _blendPreviewSnapshot;
    }

    // ── Diffuse arrays ────────────────────────────────────────────────────────

    private void RebuildDiffuseArrays(PairMaterialData[] pairs, int n)
    {
        var hash = n;
        for (var i=0;i<n&&pairs!=null;i++)
            hash = hash*31 + (pairs[i].Diffuse1?.GetInstanceID()??0)
                           + (pairs[i].Diffuse2?.GetInstanceID()??0)*7;
        var layers = Mathf.Max(n,1);
        if (hash == _lastPairHash &&
            _diffuse1Array != null &&
            _diffuse2Array != null &&
            _diffuse1Array.depth == layers &&
            _diffuse2Array.depth == layers)
            return;
        SC2Log.Log($"[SC2Runtime] RebuildDiffuseArrays: hash changed ({_lastPairHash}→{hash}), rebuilding {n} layer(s)...");
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        _lastPairHash = hash;
        if (_diffuse1Array!=null) { DestroyObject(_diffuse1Array); _diffuse1Array = null; }
        if (_diffuse2Array!=null) { DestroyObject(_diffuse2Array); _diffuse2Array = null; }
        var refTex = (pairs!=null&&n>0) ? (pairs[0].Diffuse1??pairs[0].Diffuse2) : null;
        _diffuse1Fallback = pairs != null && n > 0 && pairs[0].Diffuse1 != null ? pairs[0].Diffuse1 : Texture2D.whiteTexture;
        _diffuse2Fallback = pairs != null && n > 0 && pairs[0].Diffuse2 != null ? pairs[0].Diffuse2 : _diffuse1Fallback;
        var w = refTex!=null ? refTex.width : 64;
        var h = refTex!=null ? refTex.height : 64;
        SC2Log.Log($"[SC2Runtime] Texture2DArray dimensions: {w}x{h}, {layers} layers");
        _diffuse1Array = new Texture2DArray(w,h,layers,TextureFormat.RGBA32,true,false)
            { wrapMode=TextureWrapMode.Repeat, filterMode=FilterMode.Bilinear, name="SC2_D1Arr" };
        _diffuse2Array = new Texture2DArray(w,h,layers,TextureFormat.RGBA32,true,false)
            { wrapMode=TextureWrapMode.Repeat, filterMode=FilterMode.Bilinear, name="SC2_D2Arr" };
        for (var i=0;i<layers;i++)
        {
            var swLayer = System.Diagnostics.Stopwatch.StartNew();
            var d1 = pairs!=null&&i<pairs.Length?pairs[i].Diffuse1:null;
            var d2 = pairs!=null&&i<pairs.Length?pairs[i].Diffuse2:null;
            SC2Log.Log($"[SC2Runtime]   Layer {i}: D1={(d1!=null?$"{d1.name} {d1.width}x{d1.height} {d1.format}":"null")} " +
                       $"D2={(d2!=null?$"{d2.name} {d2.width}x{d2.height} {d2.format}":"null")}");
            CopyToArray(_diffuse1Array, d1, i, w, h);
            CopyToArray(_diffuse2Array, d2, i, w, h);
            SC2Log.Log($"[SC2Runtime]   Layer {i}: copied in {swLayer.ElapsedMilliseconds}ms");
        }
        SC2Log.Log($"[SC2Runtime] All layers copied in {swTotal.ElapsedMilliseconds}ms, calling Apply()...");
        var swApply = System.Diagnostics.Stopwatch.StartNew();
        _diffuse1Array.Apply(); _diffuse2Array.Apply();
        SC2Log.Log($"[SC2Runtime] Apply() done in {swApply.ElapsedMilliseconds}ms. Total RebuildDiffuseArrays: {swTotal.ElapsedMilliseconds}ms");
    }

    /// <summary>Same per-pair Texture2DArray pattern as RebuildDiffuseArrays
    /// above, for the 3 baked RNM basis textures (SC2MaterialPair.
    /// RnmPreviewMask0/1/2) instead of the 2 diffuse ones. Deliberately NOT
    /// hash-cached like RebuildDiffuseArrays is: baked lighting only changes
    /// on an explicit "Bake Lighting" click or a Paint Blend stroke, and
    /// this whole method only ever runs from the same discrete, UI-event-
    /// driven call sites ApplyPreviewModeToChunks(For) already has (never a
    /// per-frame path) — a caching layer here would just be complexity with
    /// no measured cost to justify it. Reuses the diffuse arrays' own w/h so
    /// this doesn't need a separate memory-budget decision: RNM lighting is
    /// low-frequency data, so downsampling from the mask's real (often
    /// 2048x2048) resolution to whatever the diffuse arrays already settled
    /// on costs nothing visible.</summary>
    private int _lastRnmHash = -1;

    private void RebuildRnmArrays(PairMaterialData[] pairs, int n, int bakeGeneration)
    {
        var pairCount = Mathf.Max(n, 1);
        var layers = pairCount * 3;
        // Cache-skip mirrors RebuildDiffuseArrays' own _lastPairHash — but
        // RnmPreviewMask0/1/2 are REUSED Texture2D instances across a
        // re-bake (SC2TerrainMaterialModel.FillCombinedMask only creates a
        // new one when the size changes), so their GetInstanceID() alone
        // can't detect "the same pair got re-baked with new content" —
        // bakeGeneration (bumped once per completed Bake) is folded in
        // specifically to catch that case.
        var hash = pairCount * 397 + bakeGeneration;
        for (var i = 0; i < n && pairs != null; i++)
            hash = hash * 31 + (pairs[i].Rnm0?.GetInstanceID() ?? 0);
        if (hash == _lastRnmHash && _rnmArray != null && _rnmArray.depth == layers)
            return;
        _lastRnmHash = hash;

        if (_rnmArray != null) { DestroyObject(_rnmArray); _rnmArray = null; }
        // Deliberately NOT tied to the diffuse arrays' own resolution
        // (unlike an earlier version of this) — that's whatever a pair's
        // assigned Diffuse texture happens to be, which can be small (or
        // the 64x64 placeholder when pair 0 has no diffuse texture at all),
        // silently flattening the baked lighting's real spatial detail down
        // to near-nothing regardless of the actual bake quality. A fixed
        // resolution keeps RNM lighting an independent decision.
        const int w = 1024, h = 1024;
        _rnmArray = new Texture2DArray(w, h, layers, TextureFormat.RGBA32, false, true)
            { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "SC2_RnmArr" };
        _rnmFallback = pairs != null && n > 0 && pairs[0].Rnm0 != null ? pairs[0].Rnm0 : GreyTexture();
        var bakedPairs = 0;
        for (var i = 0; i < pairCount; i++)
        {
            var r0 = pairs != null && i < pairs.Length ? pairs[i].Rnm0 : null;
            var r1 = pairs != null && i < pairs.Length ? pairs[i].Rnm1 : null;
            var r2 = pairs != null && i < pairs.Length ? pairs[i].Rnm2 : null;
            if (r0 != null) bakedPairs++;
            CopyRawToArray(_rnmArray, r0, i * 3 + 0, w, h);
            CopyRawToArray(_rnmArray, r1, i * 3 + 1, w, h);
            CopyRawToArray(_rnmArray, r2, i * 3 + 2, w, h);
        }
        _rnmArray.Apply();
        SC2Log.Log($"[SC2Runtime] RebuildRnmArrays: gen={bakeGeneration}, {bakedPairs}/{pairCount} pair(s) have real baked data, array {w}x{h}x{layers}.");
    }

    private static void CopyToArray(Texture2DArray arr, Texture2D src, int layer, int w, int h)
    {
        if (src == null)
        {
            var pixels = new Color32[w * h];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(128, 128, 128, 255);
            arr.SetPixels32(pixels, layer, 0);
            return;
        }

        var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        Texture2D readable = null;
        try
        {
            Graphics.Blit(src, tmp);
            RenderTexture.active = tmp;
            readable = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            readable.Apply(false);
            arr.SetPixels32(readable.GetPixels32(), layer, 0);
        }
        finally
        {
            if (readable != null) DestroyObject(readable);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }
    }

    /// <summary>Same as CopyToArray above, but for raw/linear data (baked
    /// RNM basis colors) rather than sRGB diffuse albedo — uses a Linear
    /// intermediate RenderTexture and a linear readback Texture2D so the
    /// blit doesn't apply an unwanted gamma curve to values that were never
    /// gamma-encoded in the first place (matches RnmPreviewMask0/1/2's own
    /// construction in SC2TerrainMaterialModel.FillCombinedMask, and the
    /// real import's own isLinear:true for _RNMMask0/1/2 in
    /// SC2TerrainMaterialFactory).</summary>
    private static void CopyRawToArray(Texture2DArray arr, Texture2D src, int layer, int w, int h)
    {
        if (src == null)
        {
            var pixels = new Color32[w * h];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(128, 128, 128, 255);
            arr.SetPixels32(pixels, layer, 0);
            return;
        }

        var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var prev = RenderTexture.active;
        Texture2D readable = null;
        try
        {
            Graphics.Blit(src, tmp);
            RenderTexture.active = tmp;
            readable = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            readable.Apply(false);
            arr.SetPixels32(readable.GetPixels32(), layer, 0);
        }
        finally
        {
            if (readable != null) DestroyObject(readable);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
        }
    }

    // ── Material / Shader setup ───────────────────────────────────────────────

    private void SetChunkMaterial(Material mat, int cx, int cz)
    {
        EnsureFallbackDiffuseArrays();
        EnsureFallbackRnmArrays();
        // uv2 per chunk: offset into global [0..1] UV
        // Stored per-vertex in uv2, so no need for per-material UV offset
        mat.SetTexture("_PairMapTex",  PairMapRT);
        mat.SetTexture("_BlendTex",    BlendRT);
        mat.SetTexture("_PairColorTex",PairMapRT);
        mat.SetTexture("_Diffuse1Arr", _diffuse1Array);
        mat.SetTexture("_Diffuse2Arr", _diffuse2Array);
        mat.SetTexture("_Diffuse1Fallback", _diffuse1Fallback != null ? _diffuse1Fallback : Texture2D.whiteTexture);
        mat.SetTexture("_Diffuse2Fallback", _diffuse2Fallback != null ? _diffuse2Fallback : Texture2D.whiteTexture);
        mat.SetTexture("_RnmArr", _rnmArray);
        mat.SetTexture("_RnmFallback", _rnmFallback != null ? _rnmFallback : GreyTexture());
        mat.SetVector ("_RnmUvOffset", new Vector4(0f, 0f, 0f, 0f));
        // Default only — SetRnmUvTransform (called unconditionally by
        // SC2TerrainShell.ApplyPreviewModeToChunksFor after every material
        // rebuild) overwrites this with the real footprint immediately
        // after. Still uses THIS runtime's own real GridWidth/GridHeight*
        // CellSize rather than a hardcoded 1024 so a chunk rendered even
        // one frame before that follow-up call (or on a code path that
        // skips it) doesn't sample the wrong fraction of the RNM mask on a
        // non-1024-unit map.
        var defaultSpanX = Mathf.Max(1f, GridWidth * CellSize);
        var defaultSpanZ = Mathf.Max(1f, GridHeight * CellSize);
        mat.SetVector ("_RnmUvInvScale", new Vector4(1f / defaultSpanX, 1f / defaultSpanZ, 0f, 0f));
        mat.SetInt    ("_UseArrayTextures", SystemInfo.supports2DArrayTextures ? 1 : 0);
        mat.SetColor  ("_AmbientColor",Color.white);
        mat.SetInt    ("_PreviewMode", 0);
        ApplyPreviewBrightness(mat, 0);
        mat.SetInt    ("_PairCount",   1);

    }

    private void EnsureFallbackDiffuseArrays()
    {
        if (_diffuse1Array != null && _diffuse2Array != null)
            return;

        if (_diffuse1Array != null) DestroyObject(_diffuse1Array);
        if (_diffuse2Array != null) DestroyObject(_diffuse2Array);

        _diffuse1Array = new Texture2DArray(4, 4, 1, TextureFormat.RGBA32, false, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            name = "SC2_D1Arr_Fallback"
        };
        _diffuse2Array = new Texture2DArray(4, 4, 1, TextureFormat.RGBA32, false, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            name = "SC2_D2Arr_Fallback"
        };

        var neutral = new Color32[16];
        for (var i = 0; i < neutral.Length; i++)
            neutral[i] = new Color32(128, 128, 128, 255);
        _diffuse1Array.SetPixels32(neutral, 0);
        _diffuse2Array.SetPixels32(neutral, 0);
        _diffuse1Array.Apply(false);
        _diffuse2Array.Apply(false);
    }

    /// <summary>Same reasoning as EnsureFallbackDiffuseArrays above — a
    /// freshly-created chunk needs SOME _RnmArr bound before the first real
    /// SetAllPairMaterials/RebuildRnmArrays call happens, or Unity renders
    /// the unbound Texture2DArray slot as an error/pink texture.</summary>
    private void EnsureFallbackRnmArrays()
    {
        if (_rnmArray != null)
            return;

        _rnmArray = new Texture2DArray(4, 4, 3, TextureFormat.RGBA32, false, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Point,
            name = "SC2_RnmArr_Fallback"
        };
        var neutral = new Color32[16];
        for (var i = 0; i < neutral.Length; i++) neutral[i] = new Color32(128, 128, 128, 255);
        for (var layer = 0; layer < 3; layer++) _rnmArray.SetPixels32(neutral, layer);
        _rnmArray.Apply(false);
    }

    private bool EnsureShaders()
    {
        if (_paintBrushMat == null)
        {
            var s = Shader.Find("Hidden/SC2/PaintBrush");
            if (s == null)
                s = Resources.Load<Shader>("TerrainEditorShaders/SC2PaintBrush");
            if (s==null) { Debug.LogError("[SC2] PaintBrush shader not found"); return false; }
            _paintBrushMat = new Material(s) { hideFlags=HideFlags.HideAndDontSave };
        }
        return true;
    }

    private void EnsureViewMat()
    {
        if (_viewMat != null) return;
        var s = Resources.Load<Shader>("TerrainEditorShaders/SC2TerrainView");
        if (s == null)
            s = Shader.Find("Hidden/SC2/TerrainView");
        if (s==null) { Debug.LogError("[SC2] TerrainView shader not found"); return; }
        _viewMat = new Material(s) { hideFlags=HideFlags.HideAndDontSave };
    }

    static void SafeRelease(RenderTexture rt)
    { if (rt) { rt.Release(); DestroyObject(rt); } }

    private static void DestroyObject(UnityEngine.Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            UnityEngine.Object.Destroy(obj);
        else
            UnityEngine.Object.DestroyImmediate(obj);
    }
}
