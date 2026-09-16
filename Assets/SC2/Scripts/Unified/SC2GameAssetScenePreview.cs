using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using SC2.MapEditor.Document;

/// <summary>
/// Creates runtime-visible scene representations for entries picked through
/// the unit/prop browser. The editable objects remain stored in
/// SC2TerrainData; this class is only their disposable visual projection.
///
/// Batched by asset (blueprint) id via Graphics.DrawMeshInstanced instead of
/// one real GameObject+MeshRenderer per placed instance — the previous
/// per-instance-GameObject approach was fine for a handful of objects, but a
/// map with hundreds of placed units (or a Props scatter-brush stroke
/// dropping hundreds of trees in one go) meant hundreds of real
/// GameObjects/MeshRenderers, each its own draw call. Every instance of one
/// blueprint now shares one loaded Mesh + Material[] (built once, cached in
/// its AssetBatch), with only a per-instance Matrix4x4 transform — mirrors
/// how FAForeverMapEditor's own UnitSource batches its unit rendering.
/// DrawAllBatches() must be called once per rendered frame (see
/// SC2GameViewEditorUI.LateUpdate) since Graphics.DrawMeshInstanced is an
/// immediate-mode call, unlike a real MeshRenderer Unity redraws on its own.
/// </summary>
public static class SC2GameAssetScenePreview
{
    private const int MaxInstancingCount = 1023; // Graphics.DrawMeshInstanced's own hard limit per call.

    /// <summary>Value-type replacement for the "unit:123"/"prop:456" string
    /// key this file used to rebuild via string concatenation every single
    /// frame, for every placed prop/unit, inside SyncTransforms. At map scale
    /// (say 1000+ props) that was 1000+ small string allocations 60 times a
    /// second, real avoidable GC pressure even though the actual rendering
    /// (GPU-instanced Graphics.DrawMeshInstanced) was already correctly
    /// batched. A struct key needs no allocation to construct or hash.
    /// ToString() is still provided for the handful of Debug.Log call sites
    /// that interpolate a key into a log line — those only run at PLACEMENT
    /// time (TryShow/Remove), never in the per-frame SyncTransforms/
    /// DrawAllBatches hot path, so allocating there is harmless.</summary>
    private readonly struct InstanceKey : IEquatable<InstanceKey>
    {
        public readonly bool IsUnit;
        public readonly int Id;
        public InstanceKey(bool isUnit, int id) { IsUnit = isUnit; Id = id; }
        public bool Equals(InstanceKey other) => IsUnit == other.IsUnit && Id == other.Id;
        public override bool Equals(object obj) => obj is InstanceKey other && Equals(other);
        public override int GetHashCode() => (Id << 1) ^ (IsUnit ? 1 : 0);
        public override string ToString() => (IsUnit ? "unit:" : "prop:") + Id;
    }

    private sealed class AssetBatch
    {
        public Mesh Mesh;
        public Material[] Materials;
        public ArchiveTextureResolver Resolver;
        public float UniformScale;
        public readonly Dictionary<InstanceKey, Matrix4x4> Instances = new Dictionary<InstanceKey, Matrix4x4>();
    }

    private static readonly Dictionary<string, AssetBatch> Batches = new Dictionary<string, AssetBatch>(); // keyed by concrete BP source
    private static readonly Dictionary<InstanceKey, string> InstanceAssetId = new Dictionary<InstanceKey, string>(); // instance -> batch key
    private static Matrix4x4[] _drawBuffer;

    /// <summary>Set every frame from the top toolbar's Visibility > Props
    /// toggle (SC2GameViewEditorUI's own LateUpdate, right before
    /// DrawAllBatches). Distinct from _hiddenPropIds (SC2ObjectsShell's
    /// per-object hide list, which only takes effect while the Objects tab
    /// is open and SyncTransforms actually runs) — this instead gates the
    /// real per-frame DRAW call itself, so it hides props on every tab
    /// immediately, not just the next time the Objects tab happens to
    /// resync. Units are unaffected by this specific field — a prop's key
    /// has InstanceKey.IsUnit == false, a unit's == true (see
    /// TryShowProp/TryShowUnit) — see HideUnits below for the unit
    /// equivalent.</summary>
    public static bool HideProps;

    /// <summary>Unit equivalent of HideProps above, same contract — set
    /// every frame from Visibility > Units. Previously did not exist at
    /// all: a unit's real 3D model had no visibility gate whatsoever, only
    /// its flat 2D pick-dot did (via the old combined "Objects" toggle) —
    /// the same icon-vs-model desync bug Props already
    /// had (Visibility > Props hid the model but not the dot, since those
    /// were two separate flags; here it was the exact inverse — nothing
    /// hid the model at all).</summary>
    public static bool HideUnits;

    // Debug-only bookkeeping so a placement failure logs exactly once (not
    // every single frame - SyncMissingAssetPreviews retries a failed
    // object every frame with no caching of its own, by design) until
    // either it succeeds or the object is removed. Keyed the same as
    // InstanceAssetId. Purely diagnostic - never read by any non-logging code.
    private static readonly HashSet<InstanceKey> _loggedFailureKeys = new HashSet<InstanceKey>();

    public static bool TryShowUnit(int objectId, SC2GameAsset asset, string gameDataRoot, Vector3 position, Quaternion rotation, out string error)
    {
        return TryShowLogged(new InstanceKey(true, objectId), "unit", objectId, asset, gameDataRoot, position, rotation, 1f, out error);
    }

    public static bool TryShowProp(int objectId, SC2GameAsset asset, string gameDataRoot, Vector3 position, Quaternion rotation, float scale, out string error)
    {
        return TryShowLogged(new InstanceKey(false, objectId), "prop", objectId, asset, gameDataRoot, position, rotation, scale, out error);
    }

    /// <summary>Thin logging wrapper around TryShow, so a standalone
    /// Build & Run's own Player.log shows exactly what happens
    /// when a prop/unit is placed - which asset it resolved to, whether the
    /// mesh/material actually got created, and the real reason when it
    /// doesn't - instead of that only being visible through the Objects
    /// tab's own UI warning while the app is running interactively.</summary>
    private static bool TryShowLogged(InstanceKey key, string kindLabel, int objectId, SC2GameAsset asset, string gameDataRoot, Vector3 position, Quaternion rotation, float scale, out string error)
    {
        var ok = TryShow(key, asset, gameDataRoot, position, rotation, scale, out error);
        if (ok)
        {
            _loggedFailureKeys.Remove(key);
            Debug.Log($"[SC2 Placement] Created preview for {kindLabel} #{objectId} (blueprint '{asset?.Id}') at {position}, scale={scale:0.###}.");
        }
        else if (_loggedFailureKeys.Add(key))
        {
            Debug.LogWarning($"[SC2 Placement] No 3D model for {kindLabel} #{objectId} (blueprint '{asset?.Id}') at {position}: {error}");
        }
        return ok;
    }

    /// <summary>Lets a caller check whether a prop/unit already has a live
    /// preview instance before attempting to create one (SyncTransforms
    /// itself only updates existing records - see its own doc note - so a
    /// separate "create missing previews" pass needs this to avoid
    /// re-resolving an already-live preview every frame).</summary>
    public static bool HasUnitPreview(int objectId) => InstanceAssetId.ContainsKey(new InstanceKey(true, objectId));
    public static bool HasPropPreview(int objectId) => InstanceAssetId.ContainsKey(new InstanceKey(false, objectId));

    public static void SyncTransforms(SC2TerrainData data, HashSet<int> hiddenUnitIds = null, HashSet<int> hiddenPropIds = null)
    {
        if (data == null || InstanceAssetId.Count == 0) return;

        var alive = new HashSet<InstanceKey>();
        foreach (var prop in data.ObjProps)
        {
            // A hidden prop is deliberately left out of `alive` — the
            // cleanup pass below then removes its live preview instance the
            // same way it already does for a deleted prop, mirroring the
            // hidden-unit handling right below (no separate hide/show code
            // path needed here either).
            if (hiddenPropIds != null && hiddenPropIds.Contains(prop.Id)) continue;
            var key = new InstanceKey(false, prop.Id);
            if (!TryGetBatch(key, out var batch)) continue;
            alive.Add(key);
            batch.Instances[key] = BuildMatrix(prop.Position, prop.Rotation, prop.Scale * batch.UniformScale);
        }

        foreach (var unit in data.ObjUnits)
        {
            if (unit.IsGroup) continue;
            // A hidden unit is deliberately left out of `alive` — the
            // cleanup pass below then removes its live preview instance the
            // same way it already removes a deleted unit's, no separate
            // hide/show code path needed.
            if (hiddenUnitIds != null && hiddenUnitIds.Contains(unit.Id)) continue;
            var key = new InstanceKey(true, unit.Id);
            if (!TryGetBatch(key, out var batch)) continue;
            alive.Add(key);
            batch.Instances[key] = BuildMatrix(unit.Position, Quaternion.Euler(unit.OrientationEuler), batch.UniformScale);
        }

        // Reference units (SC2ObjReferenceUnit) share this same
        // InstanceKey(true, id) space with real ObjUnits — safe, since both
        // draw their id from the same SC2TerrainData.AllocateObjectId()
        // counter and can never collide.
        foreach (var refUnit in data.ObjReferenceUnits)
        {
            var key = new InstanceKey(true, refUnit.Id);
            if (!TryGetBatch(key, out var batch)) continue;
            alive.Add(key);
            batch.Instances[key] = BuildMatrix(refUnit.Position, Quaternion.Euler(refUnit.OrientationEuler), batch.UniformScale);
        }

        foreach (var key in InstanceAssetId.Keys.Where(key => !alive.Contains(key)).ToArray()) Remove(key);
    }

    public static void ClearAll()
    {
        var count = InstanceAssetId.Count;
        if (count > 0) Debug.Log($"[SC2 Placement] Clearing {count} live prop/unit preview(s) (terrain data switched).");
        foreach (var key in InstanceAssetId.Keys.ToArray()) Remove(key);
        ClearPropGhosts();
        // Let the new map's own draw calls log fresh, instead of staying
        // silent because the same blueprint id happened to log once already
        // on a previous, now-cleared map.
        _loggedDrawnAssetIds.Clear();
        _loggedDrawSkip.Clear();
    }

    /// <summary>True the first time DrawAllBatches actually reaches the
    /// non-instanced fallback branch below — read by SC2ObjectsShell to
    /// surface a real, visible explanation instead of a silent "nothing
    /// rendered." Graphics.DrawMeshInstanced has no error/exception path at
    /// all when the current device/graphics API doesn't support GPU
    /// instancing (SystemInfo.supportsInstancing == false) — it just draws
    /// nothing, silently, every frame, forever. Every earlier stage of this
    /// pipeline (blueprint/catalog resolution, SCM parsing, Mesh/Material
    /// creation) can and does report a real error through TryShowProp/
    /// TryShowUnit's own `out error` when something goes wrong — this was
    /// the one remaining step with no failure signal at all.</summary>
    public static bool UsedNonInstancedFallback { get; private set; }
    private static bool _loggedInstancingStatus;
    // Dedup for the actual per-frame draw call logging below - one log per
    // asset id, not per frame (DrawAllBatches runs every frame).
    private static readonly HashSet<string> _loggedDrawnAssetIds = new HashSet<string>();
    private static readonly HashSet<string> _loggedDrawSkip = new HashSet<string>();

    // Non-allocating GeometryUtility.CalculateFrustumPlanes(camera, planes)
    // overload needs a persistent, reused array — see IsSphereInFrustum's
    // own doc comment (SC2TerrainShell) for why this culling exists at all.
    private static readonly Plane[] _frustumPlanes = new Plane[6];

    /// <summary>Must be called once per rendered frame (SC2GameViewEditorUI's
    /// own LateUpdate - runs unconditionally in both the Editor and a
    /// standalone Build, unlike Update() which is play-mode-gated) - Graphics.
    /// DrawMeshInstanced only draws for the current frame, it is not a
    /// persistent scene object the way a MeshRenderer GameObject is.
    /// <paramref name="camera"/> may be null (e.g. before the game camera
    /// resolves) — culling is simply skipped that frame, same as before this
    /// was added.</summary>
    public static void DrawAllBatches(Camera camera)
    {
        if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
        // SystemInfo.supportsInstancing is a real per-device/graphics-API
        // capability check, not something this project can assume - on a
        // device/driver where it's false, Graphics.DrawMeshInstanced below
        // silently draws nothing at all, with no exception, no log, and no
        // way for TryShowProp/TryShowUnit's own error reporting to have
        // caught it earlier (mesh/material creation there succeeds fine;
        // it's only the actual draw call that's a no-op). Falling back to
        // one plain Graphics.DrawMesh call per instance is slower for a
        // large scatter-brush stroke, but is a basic, universally-supported
        // Unity API with no such silent-failure mode - guaranteed to
        // actually put pixels on screen on any device Unity itself runs on.
        var useInstancing = SystemInfo.supportsInstancing;
        UsedNonInstancedFallback = !useInstancing;
        if (!_loggedInstancingStatus)
        {
            _loggedInstancingStatus = true;
            Debug.Log(useInstancing
                ? "[SC2 Placement] GPU instancing is supported - prop/unit previews draw via Graphics.DrawMeshInstanced."
                : "[SC2 Placement] GPU instancing is NOT supported on this device/graphics API - falling back to per-instance Graphics.DrawMesh calls.");
        }

        foreach (var kvp in Batches)
        {
            var assetId = kvp.Key;
            var batch = kvp.Value;
            if (batch.Mesh == null || batch.Materials == null || batch.Materials.Length == 0)
            {
                if (_loggedDrawSkip.Add(assetId))
                    Debug.LogWarning($"[SC2 Draw] '{assetId}': skipping draw - mesh or materials missing (mesh={(batch.Mesh != null)}, materialCount={batch.Materials?.Length ?? 0}).");
                continue;
            }
            int count = batch.Instances.Count;
            if (count == 0) continue;

            // Logs the FIRST real Graphics.DrawMeshInstanced/DrawMesh call
            // actually issued for this asset - not just "a preview was
            // registered" (TryShowLogged, above) but the literal per-frame
            // draw call that puts it on screen. Every subsequent frame runs
            // the identical code path with the same data, so logging once
            // is enough to prove the call genuinely fires with sane
            // parameters; logging every frame at 60fps would flood the log
            // for no extra information.
            var firstDrawThisAsset = _loggedDrawnAssetIds.Add(assetId);
            if (firstDrawThisAsset)
                Debug.Log($"[SC2 Draw] '{assetId}': first real draw call this session - {count} instance(s), {batch.Mesh.subMeshCount} submesh(es), mesh bounds size={batch.Mesh.bounds.size}, mode={(useInstancing ? "DrawMeshInstanced" : "DrawMesh (fallback)")}. Sample transform: {batch.Instances.Values.First().GetColumn(3)}.");

            // Pre-filtered ONCE per batch (not once per submesh — a
            // multi-submesh mesh would otherwise redo the exact same
            // visibility/culling work once per submesh for no reason).
            // See SC2TerrainShell.IsSphereInFrustum's own doc comment for
            // why the frustum test exists at all: Graphics.DrawMeshInstanced
            // culls the whole submitted batch as one group, never
            // individual instances, and a batch here spans every instance
            // of one blueprint across the ENTIRE map — without this,
            // scattered decorative props tank FPS the moment the camera can
            // see even one of them, since the GPU ends up shading every
            // instance of that type anywhere on the map, every frame.
            var baseRadius = batch.Mesh.bounds.extents.magnitude;
            _cullScratch.Clear();
            foreach (var instance in batch.Instances)
            {
                if (!IsInstanceVisible(instance.Key)) continue;
                if (camera != null)
                {
                    var pos = (Vector3)instance.Value.GetColumn(3);
                    var radius = baseRadius * InstanceScaleMagnitude(instance.Value);
                    if (!SC2TerrainShell.IsSphereInFrustum(_frustumPlanes, pos, radius)) continue;
                }
                _cullScratch.Add(instance.Value);
            }
            if (_cullScratch.Count == 0) continue;

            // ShadowCastingMode.Off + receiveShadows:false (was On/true) —
            // frustum culling only skips instances entirely OFF-screen; it
            // does nothing for a genuinely large visible cluster (e.g. a
            // scatter-brushed forest filling the view), where framerate still
            // drops hard once the models themselves appear. Shadows
            // roughly double the per-instance draw cost (a full extra
            // shadow-caster pass, plus a per-pixel shadow-map sample when
            // receiving) and are the single biggest lever available for
            // decorative preview geometry that doesn't need to be
            // shadow-accurate in an editor. Chosen over a max-draw-distance
            // cull (which would also fix the wide/zoomed-out case but makes
            // props visibly pop in/out) per direct user preference.
            if (useInstancing)
            {
                int bufferSize = Mathf.Min(_cullScratch.Count, MaxInstancingCount);
                if (_drawBuffer == null || _drawBuffer.Length < bufferSize) _drawBuffer = new Matrix4x4[bufferSize];

                for (int subMesh = 0; subMesh < batch.Mesh.subMeshCount; subMesh++)
                {
                    Material material = batch.Materials[Mathf.Min(subMesh, batch.Materials.Length - 1)];
                    if (material == null) continue;

                    int n = 0;
                    foreach (var matrix in _cullScratch)
                    {
                        _drawBuffer[n++] = matrix;
                        if (n == _drawBuffer.Length)
                        {
                            Graphics.DrawMeshInstanced(batch.Mesh, subMesh, material, _drawBuffer, n, null, ShadowCastingMode.Off, false);
                            n = 0;
                        }
                    }
                    if (n > 0) Graphics.DrawMeshInstanced(batch.Mesh, subMesh, material, _drawBuffer, n, null, ShadowCastingMode.Off, false);
                }
            }
            else
            {
                for (int subMesh = 0; subMesh < batch.Mesh.subMeshCount; subMesh++)
                {
                    Material material = batch.Materials[Mathf.Min(subMesh, batch.Materials.Length - 1)];
                    if (material == null) continue;
                    foreach (var matrix in _cullScratch)
                        Graphics.DrawMesh(batch.Mesh, matrix, material, 0, null, subMesh, null, ShadowCastingMode.Off, false);
                }
            }
        }
    }

    // Reused across batches/frames to avoid a per-batch List allocation —
    // grows once to the largest batch's instance count, then stays there.
    private static readonly List<Matrix4x4> _cullScratch = new List<Matrix4x4>();

    /// <summary>Approximates an instance's world-space scale factor from its
    /// TRS matrix (each local-axis column's length already factors out
    /// rotation, since rotation preserves vector length) — used to scale
    /// the mesh's own local-space bounds radius into a real world-space
    /// cull radius per instance, so a scaled-up prop isn't culled too
    /// aggressively at the edge of the frustum.</summary>
    private static float InstanceScaleMagnitude(in Matrix4x4 m)
    {
        var x = ((Vector3)m.GetColumn(0)).magnitude;
        var y = ((Vector3)m.GetColumn(1)).magnitude;
        var z = ((Vector3)m.GetColumn(2)).magnitude;
        return Mathf.Max(x, Mathf.Max(y, z));
    }

    private static bool IsInstanceVisible(InstanceKey key) =>
        !(HideProps && !key.IsUnit) &&
        !(HideUnits && key.IsUnit);

    private static Matrix4x4 BuildMatrix(Vector3 position, Quaternion rotation, float scale)
    {
        return Matrix4x4.TRS(position, rotation, Vector3.one * Mathf.Max(0.0001f, scale));
    }

    private static bool TryGetBatch(InstanceKey key, out AssetBatch batch)
    {
        batch = null;
        return InstanceAssetId.TryGetValue(key, out string assetId) && Batches.TryGetValue(assetId, out batch);
    }

    private static bool TryShow(InstanceKey key, SC2GameAsset asset, string gameDataRoot, Vector3 position, Quaternion rotation, float objectScale, out string error)
    {
        Remove(key);
        if (!TryGetOrBuildBatch(key.ToString(), asset, gameDataRoot, out var batch, out error))
            return false;

        string batchKey = GetBatchKey(asset);
        var matrix = BuildMatrix(position, rotation, Mathf.Max(0.0001f, objectScale) * batch.UniformScale);
        batch.Instances[key] = matrix;
        InstanceAssetId[key] = batchKey;
        Debug.Log($"[SC2 Mesh] {key}: instance registered at {position}, effectiveScale={Mathf.Max(0.0001f, objectScale) * batch.UniformScale:0.###} (objectScale={objectScale:0.###} x asset.UniformScale={batch.UniformScale:0.###}), batch now has {batch.Instances.Count} instance(s) total.");
        return true;
    }

    /// <summary>Resolves (parsing/building fresh, or reusing the shared
    /// cache) the Mesh+Materials for `asset`, WITHOUT registering any
    /// instance — the shared core both TryShow above and the placement-ghost
    /// preview below use, so a ghost gets the exact same real mesh a placed
    /// prop would (and for free, already-cached, if one's already been
    /// placed this session), with zero duplicated SCM-parsing logic.
    /// `logKey` is only used to prefix diagnostic log lines.</summary>
    private static bool TryGetOrBuildBatch(string logKey, SC2GameAsset asset, string gameDataRoot, out AssetBatch batch, out string error)
    {
        batch = null;
        error = null;
        if (asset == null) { error = "Blueprint entry is missing."; return false; }
        if (string.IsNullOrWhiteSpace(gameDataRoot) || !SC2GameArchiveRegistry.TryGetReader(out var reader))
        {
            error = "Game data root is not configured.";
            return false;
        }

        try
        {
            string batchKey = GetBatchKey(asset);
            if (!Batches.TryGetValue(batchKey, out batch))
            {
                Debug.Log($"[SC2 Mesh] {logKey}: building new batch for asset '{asset.Id}' (kind={asset.Kind}, {asset.Lods.Count} LOD(s) declared, UniformScale={asset.UniformScale:0.###}).");

                SC2GameAssetLod lod = asset.Lods.FirstOrDefault(candidate => candidate.ModelPaths != null && candidate.ModelPaths.Count > 0);
                if (lod == null)
                {
                    error = "No resolved SCM exists for " + asset.Id + ".";
                    Debug.LogWarning($"[SC2 Mesh] {logKey}: no LOD has a resolved ModelPaths entry. LODs: " +
                        string.Join(" | ", asset.Lods.Select(l => $"lod{l.Index} declaredMeshPath='{l.DeclaredMeshPath}'")));
                    return false;
                }

                Debug.Log($"[SC2 Mesh] {logKey}: using LOD {lod.Index} (declaredMeshPath='{lod.DeclaredMeshPath}'), {lod.ModelPaths.Count} candidate SCM path(s): {string.Join(" | ", lod.ModelPaths)}");

                // ModelPaths are now normalized archive-relative paths (the
                // browser's discovery pass is archive-native — see
                // SC2GameAssetDiscovery.BuildFromArchive) — resolve existence
                // via the reader's entry list instead of File.Exists.
                string modelPath = lod.ModelPaths.FirstOrDefault(candidate => ArchiveHasEntry(reader, candidate));
                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    error = "Resolved SCM file no longer exists.";
                    Debug.LogWarning($"[SC2 Mesh] {logKey}: none of the {lod.ModelPaths.Count} candidate SCM path(s) exist in the configured game archives.");
                    return false;
                }
                Debug.Log($"[SC2 Mesh] {logKey}: reading SCM file '{modelPath}'.");

                SC2ScmModel model;
                using (var stream = new MemoryStream(ReadArchiveEntryBytes(reader, modelPath)))
                    model = SC2ScmModelReader.Read(stream, modelPath);
                Debug.Log($"[SC2 Mesh] {logKey}: SCM v{model.Version} parsed - vertices={model.Vertices.Count}, indices={model.Indices.Count} ({model.TriangleCount} tris), materialRanges={model.Materials.Count}, bones={model.Bones.Count}.");

                Mesh mesh = CreateMesh(model, asset.Id);
                if (mesh == null)
                {
                    error = "SCM has no renderable triangles.";
                    Debug.LogWarning($"[SC2 Mesh] {logKey}: CreateMesh returned null (vertices={model.Vertices.Count}, indices={model.Indices.Count} - needs >=3 indices and >0 vertices).");
                    return false;
                }
                Debug.Log($"[SC2 Mesh] {logKey}: Unity Mesh built - subMeshCount={mesh.subMeshCount}, bounds center={mesh.bounds.center}, bounds size={mesh.bounds.size}.");

                var resolver = new ArchiveTextureResolver(reader);
                Material[] materials = CreateMaterials(lod, mesh.subMeshCount, resolver, asset.Id);
                var materialSetCount = lod.MaterialSets.FirstOrDefault(s => s.Materials.Count > 0)?.Materials.Count ?? 0;
                Debug.Log($"[SC2 Mesh] {logKey}: materials built - count={materials.Length}, shader='{(materials.Length > 0 && materials[0] != null ? materials[0].shader.name : "none")}', declaredBlueprintMaterials={materialSetCount}, enableInstancing={(materials.Length > 0 && materials[0] != null && materials[0].enableInstancing)}.");

                batch = new AssetBatch { Mesh = mesh, Materials = materials, Resolver = resolver, UniformScale = Mathf.Max(0.0001f, asset.UniformScale) };
                Batches.Add(batchKey, batch);
            }
            else
            {
                Debug.Log($"[SC2 Mesh] {logKey}: reusing already-loaded batch for asset '{asset.Id}' ({batch.Instances.Count} existing instance(s), mesh bounds size={batch.Mesh?.bounds.size}).");
            }
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not create SCM preview: " + exception.Message;
            Debug.LogException(exception);
            batch = null;
            return false;
        }
    }

    // ---- Placement ghost preview (props) ----
    // Armed single-prop placement used to show only a
    // flat translucent 2D dot (DrawViewportGlyph), never the prop's real 3D
    // model, unlike Marker/Army placement's SC2MarkerScenePreview ghosts.
    // Real GameObjects here, not the GPU-instanced batch system above -
    // ghosts are always few (one canonical + a handful of symmetry mirrors,
    // never hundreds), so per-instance MeshRenderers are fine, and it lets
    // each ghost carry its own translucent tinted material (the shared
    // instanced draw path has exactly one material per blueprint, with no
    // room for a "this one's a preview" variant).
    // Same shader/color convention as SC2MarkerScenePreview's own ghost
    // material (Unlit/Color, solid distinctive tint — not alpha-blended;
    // the marker ghost reads as "preview" through its color alone, not
    // transparency, so the prop ghost matches that established pattern
    // instead of inventing a separate transparent-material setup).
    private static readonly List<GameObject> _propGhosts = new();
    private static Material _propGhostMaterial;
    private static readonly Color PropGhostColor = new Color(.25f, .86f, 1f, 1f);

    private static Material GetPropGhostMaterial()
    {
        if (_propGhostMaterial != null) return _propGhostMaterial;
        var shader = Shader.Find("Unlit/Color") ?? SC2FallbackShader.Get();
        _propGhostMaterial = new Material(shader) { name = "SC2PropGhostPreview", hideFlags = HideFlags.DontSave, color = PropGhostColor };
        return _propGhostMaterial;
    }

    /// <summary>Shows a translucent real-mesh preview of `asset` at every
    /// given placement (one per symmetry slot, mirroring how DrawPlacement
    /// Preview already computes multiple positions for Marker/Army ghosts).
    /// Always clears any previous ghosts first — same "resync from scratch
    /// every frame" contract as SC2MarkerScenePreview.SyncGhosts, so a
    /// caller can call this unconditionally every GUI frame without tracking
    /// prior state itself.</summary>
    public static void SyncPropGhosts(SC2GameAsset asset, string gameDataRoot, IReadOnlyList<(Vector3 position, Quaternion rotation, float scale)> placements)
    {
        ClearPropGhosts();
        if (asset == null || placements == null || placements.Count == 0) return;
        if (!TryGetOrBuildBatch("ghost", asset, gameDataRoot, out var batch, out _)) return;

        var material = GetPropGhostMaterial();
        foreach (var p in placements)
        {
            var go = new GameObject("SC2PropGhost") { hideFlags = HideFlags.DontSave };
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = batch.Mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.transform.SetPositionAndRotation(p.position, p.rotation);
            go.transform.localScale = Vector3.one * Mathf.Max(0.0001f, p.scale) * batch.UniformScale;
            _propGhosts.Add(go);
        }
    }

    public static void ClearPropGhosts()
    {
        foreach (var go in _propGhosts)
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        _propGhosts.Clear();
    }

    private static Mesh CreateMesh(SC2ScmModel model, string name)
    {
        if (model == null || model.Vertices.Count == 0 || model.Indices.Count < 3) return null;
        var vertices = new Vector3[model.Vertices.Count];
        var normals = new Vector3[model.Vertices.Count];
        var tangents = new Vector4[model.Vertices.Count];
        var uv = new Vector2[model.Vertices.Count];
        for (int i = 0; i < model.Vertices.Count; i++)
        {
            SC2ScmVertex source = model.Vertices[i];
            vertices[i] = ReadVector3(source.Position, Vector3.zero);
            Vector3 normal = ReadVector3(source.Normal, Vector3.up);
            Vector3 tangent = ReadVector3(source.Tangent, Vector3.right);
            Vector3 binormal = ReadVector3(source.Binormal, Vector3.forward);
            normals[i] = normal;
            float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), binormal) < 0f ? -1f : 1f;
            tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, handedness);

            // SCM stores texture V in the game's convention. Unity previews
            // need the same flip used by the reference Blender importer.
            Vector2 sourceUv = ReadVector2(source.Uv1);
            uv[i] = new Vector2(sourceUv.x, 1f - sourceUv.y);
        }

        var mesh = new Mesh { name = "SCM " + name };
        if (vertices.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.uv = uv;

        List<SC2ScmMaterialRange> ranges = model.Materials.Where(range => IsValidRange(range, model.Indices.Count)).ToList();
        if (ranges.Count == 0)
        {
            mesh.subMeshCount = 1;
            mesh.SetTriangles(model.Indices.Select(index => (int)index).ToArray(), 0, true);
        }
        else
        {
            mesh.subMeshCount = ranges.Count;
            for (int subMesh = 0; subMesh < ranges.Count; subMesh++)
            {
                SC2ScmMaterialRange range = ranges[subMesh];
                int start = (int)range.FirstIndex;
                int count = (int)range.FaceCount * 3;
                var triangles = new int[count];
                for (int index = 0; index < count; index++) triangles[index] = model.Indices[start + index];
                mesh.SetTriangles(triangles, subMesh, true);
            }
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    private static string GetBatchKey(SC2GameAsset asset)
    {
        // asset.SourcePath is now always a normalized archive-relative path
        // (see SC2GameAssetDiscovery.BuildFromArchive) — already a stable,
        // canonical key on its own; no absolute-path resolution needed (or
        // meaningful — there's no real filesystem path behind it any more).
        if (!string.IsNullOrWhiteSpace(asset.SourcePath))
            return asset.SourcePath.Replace('\\', '/');
        return (asset.Kind ?? "Blueprint") + ":" + asset.Id;
    }

    private static bool IsValidRange(SC2ScmMaterialRange range, int indexCount)
    {
        if (range == null || range.FaceCount == 0 || range.FirstIndex >= indexCount) return false;
        long count = (long)range.FaceCount * 3L;
        return count <= indexCount - range.FirstIndex;
    }

    private static Material[] CreateMaterials(SC2GameAssetLod lod, int subMeshCount, ArchiveTextureResolver resolver, string assetId)
    {
        SC2GameAssetMaterialSet set = lod.MaterialSets.FirstOrDefault(candidate => candidate.Materials.Count > 0);
        Material[] declared = new Material[0];
        if (set != null)
        {
            try
            {
                declared = SC2UnitMaterialFactory.CreateMaterials(set, resolver, new Color(0.16f, 0.45f, 0.9f), new Color(0.62f, 0.78f, 1f));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("SC2 model preview: unit shader setup failed for '" + assetId + "'; using visible fallback materials. " + exception.Message);
            }
        }
        var result = new Material[Mathf.Max(1, subMeshCount)];
        for (int index = 0; index < result.Length; index++)
        {
            if (declared.Length > 0) result[index] = declared[Mathf.Min(index, declared.Length - 1)];
            else result[index] = CreateFallbackMaterial(assetId, index);
        }
        return result;
    }

    private static Material CreateFallbackMaterial(string assetId, int index)
    {
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color");
        if (shader == null) throw new InvalidOperationException("No built-in fallback shader is available for SCM preview.");
        // Standard/URP-Lit/Unlit-Color all declare instancing support
        // themselves; still needs the material-side opt-in (see the same
        // note on SC2UnitMaterialFactory.CreateMaterial) for
        // Graphics.DrawMeshInstanced (below) to batch it.
        var material = new Material(shader) { name = "SC2 Missing Material " + assetId + " " + index, color = new Color(0.7f, 0.25f, 0.7f), enableInstancing = true };
        // Same reasoning as SC2Unit.shader's own "Cull Off" - real SCM
        // meshes' triangle winding is a roughly even mix relative to their
        // stored normals, not consistently one direction, so backface
        // culling hides a large fraction of the geometry regardless of
        // view angle. Harmless no-op if this fallback shader has no _Cull
        // property (SetInt on an unknown property is silently ignored).
        if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Off);
        return material;
    }

    /// <summary>Removes one instance from its asset's batch. When that was
    /// the last instance of that asset, the whole batch (mesh/materials/
    /// resolver) is disposed too - a blueprint no longer placed anywhere
    /// shouldn't keep its loaded SCM/textures resident.</summary>
    private static void Remove(InstanceKey key)
    {
        _loggedFailureKeys.Remove(key);
        if (!InstanceAssetId.TryGetValue(key, out string assetId)) return;
        InstanceAssetId.Remove(key);

        if (!Batches.TryGetValue(assetId, out AssetBatch batch)) return;
        batch.Instances.Remove(key);
        if (batch.Instances.Count > 0) return;

        Batches.Remove(assetId);
        if (batch.Mesh != null) SafeDestroy(batch.Mesh);
        if (batch.Materials != null) foreach (Material material in batch.Materials) if (material != null) SafeDestroy(material);
        batch.Resolver?.Dispose();
    }

    /// <summary>Object.Destroy only defers the actual destruction to end of
    /// frame and requires Application.isPlaying - calling it while this
    /// component runs in the Unity Editor's Edit Mode (via [ExecuteAlways],
    /// not Play Mode) logs "Destroy may not be called from edit mode!" every
    /// time. DestroyImmediate is the correct call in that context.</summary>
    private static void SafeDestroy(UnityEngine.Object obj)
    {
        if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
        else UnityEngine.Object.DestroyImmediate(obj);
    }

    private static Vector3 ReadVector3(float[] values, Vector3 fallback)
    {
        return values == null || values.Length < 3 ? fallback : new Vector3(values[0], values[1], values[2]);
    }

    private static Vector2 ReadVector2(float[] values)
    {
        return values == null || values.Length < 2 ? Vector2.zero : new Vector2(values[0], values[1]);
    }

    /// <summary>Resolves a material's raw declared texture path against the
    /// game's own .scd archives (via the shared SC2GameArchiveRegistry
    /// reader) instead of a loose-file gamedata copy. Deliberately its own
    /// simple direct/.win-suffix-swap check rather than reusing the
    /// browser's own SC2GameAssetFileResolver (that one's expensive
    /// case-insensitive fuzzy/sibling/basename fallback search is meant to
    /// run once during discovery, not repeated on Unity's main thread every
    /// time a placed prop/unit needs its four material textures).</summary>
    private sealed class ArchiveTextureResolver : ISC2UnitTextureResolver, IDisposable
    {
        private readonly ISC2SourceContainerReader _reader;
        private readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public ArchiveTextureResolver(ISC2SourceContainerReader reader)
        {
            _reader = reader;
        }

        public Texture2D Resolve(string blueprintPath)
        {
            if (string.IsNullOrWhiteSpace(blueprintPath)) return null;
            Texture2D cached;
            if (_cache.TryGetValue(blueprintPath, out cached)) return cached;
            string path = ResolvePath(blueprintPath);
            Texture2D texture = null;
            if (!string.IsNullOrWhiteSpace(path)) SC2CustomTextureImporter.TryLoadPreviewFromArchive(_reader, path, out texture, out _);
            _cache[blueprintPath] = texture;
            return texture;
        }

        public void Dispose()
        {
            foreach (Texture2D texture in _cache.Values.Where(texture => texture != null).Distinct()) SafeDestroy(texture);
            _cache.Clear();
        }

        private string ResolvePath(string blueprintPath)
        {
            string relative = blueprintPath.Replace('\\', '/').Trim().TrimStart('/');
            if (ArchiveHasEntry(_reader, relative)) return relative;

            if (relative.EndsWith(".win.dds", StringComparison.OrdinalIgnoreCase))
            {
                string withoutWin = relative.Substring(0, relative.Length - ".win.dds".Length) + ".dds";
                return ArchiveHasEntry(_reader, withoutWin) ? withoutWin : null;
            }
            if (relative.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                string withWin = relative.Substring(0, relative.Length - ".dds".Length) + ".win.dds";
                return ArchiveHasEntry(_reader, withWin) ? withWin : null;
            }
            return null;
        }
    }

    /// <summary>ISC2SourceContainerReader has no O(1) existence check on its
    /// own interface (SC2MultiSourceContainerReader's own Contains is a
    /// concrete-type-only convenience) — a linear scan over Entries is fine
    /// here since every call site above is memoized (ArchiveTextureResolver's
    /// _cache, and the one-shot model-candidate lookup in TryGetOrBuildBatch),
    /// never repeated per-frame.</summary>
    private static bool ArchiveHasEntry(ISC2SourceContainerReader reader, string normalizedPath) =>
        reader.Entries.Any(e => string.Equals(e.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>ZipArchiveEntry.Open() (the real backing stream for a packed
    /// .scd) is forward-only/non-seekable, but SC2ScmModelReader.Read seeks —
    /// buffer the whole entry into memory first.</summary>
    private static byte[] ReadArchiveEntryBytes(ISC2SourceContainerReader reader, string normalizedPath)
    {
        using var stream = reader.OpenEntry(normalizedPath);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
