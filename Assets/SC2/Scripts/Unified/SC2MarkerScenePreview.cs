using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Creates runtime-visible 3D scene representations for markers, using the
/// real marker gizmo models
/// imported to Assets/SC2/Resources/MarkerModels/&lt;IconKey&gt;.fbx.
///
/// Real placed
/// markers render via the SAME GPU-instancing approach
/// SC2GameAssetScenePreview already uses for Props/Units
/// (Graphics.DrawMeshInstanced, batched by IconKey — 27 distinct types max)
/// instead of one real GameObject+MeshRenderer per marker. The previous
/// per-marker-GameObject design meant every frame's Sync() unconditionally
/// wrote a real Transform (SetPositionAndRotation/localScale) for every
/// marker, and rendering issued roughly one draw call per marker (no
/// batching) — a map with hundreds of markers (Mass/expansion/path nodes,
/// etc.) meant hundreds of draw calls where Props' own architecture needs
/// at most ~27. DrawAllBatches() must be called once per rendered frame
/// (SC2GameViewEditorUI.LateUpdate) for the exact same reason Props' own
/// DrawAllBatches does — Graphics.DrawMeshInstanced is immediate-mode, not
/// a persistent scene object Unity redraws on its own.
///
/// Placement GHOST previews (SyncGhosts/SyncGhostsMulti, shown while a
/// placement tool is armed, before the click) deliberately stay real
/// GameObjects — there are only ever a handful on screen at once (the
/// current cursor's landing spot(s)), so the per-object overhead this
/// class exists to eliminate for hundreds of PLACED markers never applies
/// to them, and keeping them as GameObjects keeps the existing tinted-
/// material-override approach (BuildAnchor's overrideMaterial parameter)
/// simple — GPU instancing has no equally simple per-instance-color story
/// (Graphics.DrawMeshInstanced applies one shared MaterialPropertyBlock to
/// the whole batch call, not per instance).
///
/// Per-IconKey instanced batches are built lazily from each marker prefab
/// as source Mesh/submesh/material draw parts. Their local matrices retain
/// the FBX root's imported axis conversion, while drawing the original
/// meshes directly works with Read/Write disabled. If batch discovery ever
/// fails for a given key, that ONE key transparently falls back to the
/// original real-GameObject-per-marker rendering instead of silently not
/// rendering at all — every other key keeps using the fast path.
///
/// Editable marker data stays in SC2TerrainData.ObjMarkers; this class only
/// maintains a disposable visual projection of it, reconciled every call to
/// Sync() (added/removed/retyped markers are picked up automatically). The
/// existing flat IMGUI viewport dot (DrawViewportDot) keeps handling
/// selection/drag/delete unchanged — this is a purely additive 3D visual,
/// exactly mirroring how Props already layer a real mesh preview underneath
/// their own dot.
/// </summary>
public static class SC2MarkerScenePreview
{
    private const string RootName = "__SC2_MarkerPreviews";

    /// <summary>These gizmo meshes sit at the marker's own stored position
    /// with no vertical separation from the ground, so a decal projected onto
    /// the same ground (also flush, see SC2Decal.shader's own Offset bias)
    /// has essentially zero real depth gap to win or lose against — making the
    /// decal's coverage of the gizmo come down to a depth-bias coin flip
    /// instead of a reliable, resolution-independent ZTest. A small genuine
    /// world-space lift gives the gizmo an unambiguous depth advantage no
    /// polygon-offset tuning on the decal's side can undo.</summary>
    private const float GroundLift = 0.15f;

    /// <summary>Every marker gizmo except Mass
    /// reads too small next to the terrain at real authored scale - scale
    /// them up 2x in the viewport. Mass is deliberately excluded (its flat
    /// resource-plate size is already correct).</summary>
    private const float DefaultGizmoScale = 2f;

    /// <summary>A uniform 180° Y-axis rotation was tried first
    /// but proven wrong via a batch-mode
    /// wireframe-vertex-plot comparison against the real Blender source
    /// ("M" left / "A"
    /// right, upright): the 180Y rotation produced "A M" (wrong order,
    /// matching the user's bad screenshot), while a plain Z-axis mirror
    /// with NO rotation reproduced the Blender source exactly ("M A",
    /// upright). Composing 180Y with a Z-mirror is mathematically a pure
    /// X-mirror of the un-rotated model (R_y(180) negates world X and Z;
    /// composing with a Z-only scale-mirror cancels the Z part, leaving
    /// only X mirrored) - which is why that combination looked "the same as
    /// no fix" to the user: still wrong, just wrong a different way. The
    /// correct, final transform is this Z-axis mirror alone, applied
    /// uniformly to all 27 markers via the anchor's scale - no rotation
    /// component beyond the marker's own OrientationEuler, no per-key
    /// exceptions.</summary>
    private static readonly Vector3 ZMirrorScale = new(1f, 1f, -1f);

    // ── Instanced batching for real PLACED markers ──────────────────────

    private const int MaxInstancingCount = 1023; // Graphics.DrawMeshInstanced's own hard limit per call.

    private sealed class MarkerDrawPart
    {
        public Mesh Mesh;
        public int SubMeshIndex;
        public Material Material;
        public Matrix4x4 LocalMatrix;
    }

    private sealed class MarkerBatch
    {
        public MarkerDrawPart[] Parts;
        public Bounds LocalBounds;
        public readonly Dictionary<int, Matrix4x4> Instances = new();
    }

    private static readonly Dictionary<string, MarkerBatch> Batches = new(); // keyed by IconKey
    private static readonly Dictionary<int, string> InstanceKey = new(); // marker Id -> IconKey (instanced path)
    private static readonly HashSet<string> InstancingFailedKeys = new(); // keys TryBuildInstancedBatch couldn't handle - logged once, permanently routed to the legacy fallback for this session

    private static Matrix4x4[] _drawBuffer;
    private static bool _loggedInstancingStatus;

    private static GameObject _root;
    private static readonly Dictionary<string, GameObject> PrefabCache = new();

    /// <summary><paramref name="hiddenIds"/> — markers whose Id is in this
    /// set are skipped entirely (their instance, if any, is torn down via
    /// the same "not in alive set" cleanup below every other removed marker
    /// already goes through) — the Markers tab's "By type" panel Hide/
    /// Isolate actions, mirroring SC2GameAssetScenePreview.SyncTransforms'
    /// own hidden-id parameters for Props/Units. <paramref name="visible"/>
    /// — the top-panel Visibility menu's "Markers" toggle; false tears down
    /// every instance via ClearAll() instead of syncing any of them.
    /// Unchecking Markers in Visibility hides the flat 2D dot (the caller's
    /// own showMarkers gate) but this real 3D gizmo model needs its own
    /// visibility gate too — same class of bug as Props/Units, fixed the
    /// same way
    /// (SC2GameAssetScenePreview.HideProps/HideUnits).</summary>
    public static void Sync(SC2TerrainData data, HashSet<int> hiddenIds = null, bool visible = true)
    {
        if (data == null || !visible) { ClearAll(); return; }

        var aliveInstanced = new HashSet<int>();
        var aliveLegacy = new HashSet<int>();
        foreach (var marker in data.ObjMarkers)
        {
            if (hiddenIds != null && hiddenIds.Contains(marker.Id)) continue;
            var key = ResolveIconKey(marker);
            if (string.IsNullOrEmpty(key)) continue;

            if (!InstancingFailedKeys.Contains(key))
            {
                if (!Batches.TryGetValue(key, out var batch))
                {
                    if (TryBuildInstancedBatch(key, out batch))
                    {
                        Batches[key] = batch;
                    }
                    else
                    {
                        InstancingFailedKeys.Add(key);
                        Debug.LogWarning($"[SC2 Marker] '{key}': could not build an instanced render batch — falling back to a real GameObject for every marker of this type (slower, but still correct).");
                    }
                }

                if (batch != null)
                {
                    aliveInstanced.Add(marker.Id);
                    InstanceKey[marker.Id] = key;
                    batch.Instances[marker.Id] = BuildMatrix(key, marker.Position, marker.OrientationEuler);
                    continue;
                }
            }

            // Legacy per-GameObject fallback — only reached for a key that
            // failed TryBuildInstancedBatch above.
            var prefab = LoadPrefab(key);
            if (prefab == null) continue;
            aliveLegacy.Add(marker.Id);
            if (!LegacyInstances.TryGetValue(marker.Id, out var anchor) || anchor == null)
            {
                anchor = BuildAnchor(prefab, $"Marker {marker.Id} ({marker.Type})", null);
                LegacyInstances[marker.Id] = anchor;
            }
            ApplyLegacyTransform(anchor, key, marker.Position, marker.OrientationEuler);
        }

        foreach (var id in InstanceKey.Keys.Where(id => !aliveInstanced.Contains(id)).ToArray())
            RemoveInstanced(id);
        foreach (var id in LegacyInstances.Keys.Where(id => !aliveLegacy.Contains(id)).ToArray())
            RemoveLegacy(id);
    }

    // Non-allocating GeometryUtility.CalculateFrustumPlanes(camera, planes)
    // overload needs a persistent, reused array — see
    // SC2TerrainShell.IsSphereInFrustum's own doc comment for why this
    // culling exists at all.
    private static readonly Plane[] _frustumPlanes = new Plane[6];
    // Reused across batches/frames to avoid a per-batch List allocation.
    private static readonly List<Matrix4x4> _cullScratch = new();

    /// <summary>Must be called once per rendered frame (SC2GameViewEditorUI's
    /// own LateUpdate) — Graphics.DrawMeshInstanced only draws for the
    /// current frame, unlike the legacy fallback's real MeshRenderer
    /// GameObjects, which Unity keeps redrawing on its own once created.
    /// <paramref name="camera"/> may be null — culling is simply skipped
    /// that frame.</summary>
    public static void DrawAllBatches(Camera camera)
    {
        // Same silent-failure hazard as SC2GameAssetScenePreview's own
        // DrawAllBatches (see that class's doc comment + the
        // project_prop_unit_render_build_fix memory) — a device/driver
        // without GPU instancing support makes Graphics.DrawMeshInstanced a
        // silent no-op, so this falls back to plain Graphics.DrawMesh calls
        // instead of drawing nothing.
        var useInstancing = SystemInfo.supportsInstancing;
        if (!_loggedInstancingStatus)
        {
            _loggedInstancingStatus = true;
            Debug.Log(useInstancing
                ? "[SC2 Marker] GPU instancing is supported - marker previews draw via Graphics.DrawMeshInstanced."
                : "[SC2 Marker] GPU instancing is NOT supported on this device/graphics API - falling back to per-instance Graphics.DrawMesh calls.");
        }

        if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);

        foreach (var batch in Batches.Values)
        {
            if (batch.Parts == null || batch.Parts.Length == 0) continue;
            if (batch.Instances.Count == 0) continue;

            // See SC2GameAssetScenePreview.DrawAllBatches' own copy of this
            // same pre-filter for why it exists — Graphics.DrawMeshInstanced
            // culls the whole submitted batch as one group, never individual
            // instances, and a batch here already spans every marker of one
            // IconKey across the entire map.
            var baseRadius = batch.LocalBounds.extents.magnitude;
            _cullScratch.Clear();
            foreach (var instance in batch.Instances)
            {
                if (camera != null)
                {
                    var pos = instance.Value.MultiplyPoint3x4(batch.LocalBounds.center);
                    var radius = baseRadius * InstanceScaleMagnitude(instance.Value);
                    if (!SC2TerrainShell.IsSphereInFrustum(_frustumPlanes, pos, radius)) continue;
                }
                _cullScratch.Add(instance.Value);
            }
            if (_cullScratch.Count == 0) continue;

            // ShadowCastingMode.Off + receiveShadows:false (was On/true) —
            // see SC2GameAssetScenePreview.DrawAllBatches' own copy of this
            // same change for the full reasoning (frustum culling alone
            // doesn't help once a large cluster is genuinely on screen).
            if (useInstancing)
            {
                var bufferSize = Mathf.Min(_cullScratch.Count, MaxInstancingCount);
                if (_drawBuffer == null || _drawBuffer.Length < bufferSize) _drawBuffer = new Matrix4x4[bufferSize];

                foreach (var part in batch.Parts)
                {
                    if (part.Mesh == null || part.Material == null) continue;

                    var n = 0;
                    foreach (var matrix in _cullScratch)
                    {
                        _drawBuffer[n++] = matrix * part.LocalMatrix;
                        if (n == _drawBuffer.Length)
                        {
                            Graphics.DrawMeshInstanced(part.Mesh, part.SubMeshIndex, part.Material, _drawBuffer, n, null, ShadowCastingMode.Off, false);
                            n = 0;
                        }
                    }
                    if (n > 0) Graphics.DrawMeshInstanced(part.Mesh, part.SubMeshIndex, part.Material, _drawBuffer, n, null, ShadowCastingMode.Off, false);
                }
            }
            else
            {
                foreach (var part in batch.Parts)
                {
                    if (part.Mesh == null || part.Material == null) continue;
                    foreach (var matrix in _cullScratch)
                        Graphics.DrawMesh(part.Mesh, matrix * part.LocalMatrix, part.Material, 0, null, part.SubMeshIndex, null, ShadowCastingMode.Off, false);
                }
            }
        }
    }

    /// <summary>Same reasoning/implementation as
    /// SC2GameAssetScenePreview.InstanceScaleMagnitude — approximates an
    /// instance's world-space scale from its TRS matrix's local-axis column
    /// lengths, so the mesh's local-space bounds radius can be scaled into
    /// a real world-space cull radius per instance.</summary>
    private static float InstanceScaleMagnitude(in Matrix4x4 m)
    {
        var x = ((Vector3)m.GetColumn(0)).magnitude;
        var y = ((Vector3)m.GetColumn(1)).magnitude;
        var z = ((Vector3)m.GetColumn(2)).magnitude;
        return Mathf.Max(x, Mathf.Max(y, z));
    }

    private static Matrix4x4 BuildMatrix(string key, Vector3 position, Vector3 orientationEuler)
    {
        var rotation = Quaternion.Euler(orientationEuler);
        var scale = key == "mass" ? 1f : DefaultGizmoScale;
        return Matrix4x4.TRS(position + Vector3.up * GroundLift, rotation, ZMirrorScale * scale);
    }

    private static void RemoveInstanced(int id)
    {
        if (!InstanceKey.TryGetValue(id, out var key)) return;
        InstanceKey.Remove(id);
        if (Batches.TryGetValue(key, out var batch)) batch.Instances.Remove(id);
    }

    /// <summary>Builds an instanced draw-part list from every
    /// MeshFilter/MeshRenderer in the marker prefab. The source FBX meshes
    /// deliberately keep Read/Write disabled, so this must not use
    /// Mesh.CombineMeshes: that API needs CPU-readable vertex data in a
    /// player build and made every marker type fall through the optimized
    /// path after the instancing migration. Each original mesh/submesh is
    /// instead submitted directly and carries its transform relative to an
    /// identity anchor. Instantiating the prefab as a CHILD of that anchor
    /// is equally important: Unity's FBX importer stores the Blender-Z-up to
    /// Unity-Y-up correction on the model root. Instantiating with an
    /// explicit Quaternion.identity overwrote that correction and left the
    /// mostly-flat marker artwork edge-on or otherwise misplaced.</summary>
    private static bool TryBuildInstancedBatch(string key, out MarkerBatch batch)
    {
        batch = null;
        var prefab = LoadPrefab(key);
        if (prefab == null) return false;

        GameObject tempAnchor = null;
        try
        {
            tempAnchor = new GameObject("SC2MarkerBatchSource " + key) { hideFlags = HideFlags.HideAndDontSave };
            tempAnchor.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            tempAnchor.transform.localScale = Vector3.one;
            var modelInstance = Object.Instantiate(prefab, tempAnchor.transform, false);
            modelInstance.hideFlags = HideFlags.HideAndDontSave;
            tempAnchor.SetActive(false);

            var meshFilters = modelInstance.GetComponentsInChildren<MeshFilter>(true);
            if (meshFilters.Length == 0) return false;

            var parts = new List<MarkerDrawPart>();
            var localBounds = new Bounds();
            var hasBounds = false;
            foreach (var meshFilter in meshFilters)
            {
                var sharedMesh = meshFilter.sharedMesh;
                if (sharedMesh == null) continue;
                var renderer = meshFilter.GetComponent<MeshRenderer>();
                if (renderer == null) continue;
                var rendererMats = renderer.sharedMaterials;
                var localMatrix = tempAnchor.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                EncapsulateTransformedBounds(ref localBounds, ref hasBounds, sharedMesh.bounds, localMatrix);
                for (var sub = 0; sub < sharedMesh.subMeshCount; sub++)
                {
                    var srcMat = rendererMats != null && sub < rendererMats.Length ? rendererMats[sub]
                        : rendererMats != null && rendererMats.Length > 0 ? rendererMats[0] : null;
                    parts.Add(new MarkerDrawPart
                    {
                        Mesh = sharedMesh,
                        SubMeshIndex = sub,
                        Material = BuildInstancedMaterial(srcMat, key, parts.Count),
                        LocalMatrix = localMatrix
                    });
                }
            }
            if (parts.Count == 0) return false;

            batch = new MarkerBatch
            {
                Parts = parts.ToArray(),
                LocalBounds = hasBounds ? localBounds : new Bounds(Vector3.zero, Vector3.one)
            };
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[SC2 Marker] '{key}': exception building instanced batch ({ex.Message}).");
            return false;
        }
        finally
        {
            if (tempAnchor != null)
            {
                if (Application.isPlaying) Object.Destroy(tempAnchor);
                else Object.DestroyImmediate(tempAnchor);
            }
        }
    }

    private static void EncapsulateTransformedBounds(ref Bounds result, ref bool initialized, Bounds source, Matrix4x4 matrix)
    {
        var min = source.min;
        var max = source.max;
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        for (var z = 0; z < 2; z++)
        {
            var corner = matrix.MultiplyPoint3x4(new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z));
            if (!initialized)
            {
                result = new Bounds(corner, Vector3.zero);
                initialized = true;
            }
            else
            {
                result.Encapsulate(corner);
            }
        }
    }

    /// <summary>The material of one draw part: the albedo texture and colour of the source material (never the shared asset itself;
    /// instancing needs enableInstancing, and changing an imported FBX material in place would be a persistent side effect) on
    /// SC2/MarkerModel, with instancing turned on. The anchor's Z mirror (ZMirrorScale) makes the instance matrix's determinant
    /// negative; a single renderer flips the winding for that on its own, an instanced draw does not, so the faces would come out
    /// inside out under the Standard shader, which has no cull setting. SC2/MarkerModel culls the front faces instead.</summary>
    private static Material BuildInstancedMaterial(Material source, string key, int index)
    {
        var shader = Resources.Load<Shader>("SC2MarkerModel") ?? Shader.Find("SC2/MarkerModel");
        Material material;
        if (shader != null)
        {
            material = new Material(shader) { name = $"SC2Marker {key} {index}", enableInstancing = true };
            if (source != null)
            {
                if (source.HasProperty("_MainTex") && source.GetTexture("_MainTex") != null) material.SetTexture("_MainTex", source.GetTexture("_MainTex"));
                if (source.HasProperty("_Color")) material.SetColor("_Color", source.GetColor("_Color"));
                else if (source.HasProperty("_BaseColor")) material.SetColor("_Color", source.GetColor("_BaseColor"));
            }
            else
            {
                material.SetColor("_Color", new Color(0.7f, 0.25f, 0.7f));
            }
            return material;
        }

        if (source != null)
        {
            material = new Material(source) { name = $"SC2Marker {key} {index}", enableInstancing = true };
        }
        else
        {
            var fallback = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color") ?? SC2FallbackShader.Get();
            material = new Material(fallback) { name = $"SC2Marker {key} {index} (missing material)", color = new Color(0.7f, 0.25f, 0.7f), enableInstancing = true };
        }
        if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)CullMode.Front);
        return material;
    }

    public static void ClearAll()
    {
        // Batches (the built Mesh/Material cache) and PrefabCache
        // deliberately persist across this — matches SC2GameAssetScenePreview's
        // own convention (its own Batches dict is never cleared here
        // either) and the pre-existing PrefabCache field, which was never
        // cleared even by the old real-GameObject implementation this
        // replaces: rebuilding a 27-entry mesh cache from scratch every
        // time the terrain data switches (e.g. New/Load Terrain) would be
        // pure waste, since the same 27 marker prefabs are identical
        // regardless of which map is open.
        foreach (var batch in Batches.Values) batch.Instances.Clear();
        InstanceKey.Clear();
        foreach (var id in LegacyInstances.Keys.ToArray()) RemoveLegacy(id);
        ClearGhosts();
    }

    // ── Legacy per-GameObject fallback (only for a key TryBuildInstancedBatch
    // couldn't handle) ───────────────────────────────────────────────────

    private static readonly Dictionary<int, GameObject> LegacyInstances = new();

    /// <summary>Skips the Transform write entirely when nothing actually
    /// changed since last frame — a rendering-
    /// optimization follow-up. A real Unity Transform write
    /// (SetPositionAndRotation/localScale) is meaningfully more expensive
    /// than a plain value comparison (it propagates hierarchy-dirty flags
    /// and recomputes cached matrices), and on any given frame the vast
    /// majority of markers using this legacy fallback are NOT being
    /// actively dragged/edited, so skipping the write for those is a real
    /// win. Safe because it's driven by comparing the anchor's actual
    /// current transform against the target every frame, not a manually
    /// maintained dirty flag threaded through every mutation call site
    /// (placement/drag/delete/undo/import/paste/symmetry) — that approach
    /// was considered and rejected: missing even one call site would
    /// silently leave a stale visual, whereas a per-frame comparison can
    /// never miss a change. (The instanced paths above don't get this same
    /// treatment — their per-frame "write" is just a Matrix4x4 dictionary
    /// assignment, already cheaper than the 16-float comparison this would
    /// cost to guard it.)</summary>
    private static void ApplyLegacyTransform(GameObject anchor, string key, Vector3 position, Vector3 orientationEuler)
    {
        position += Vector3.up * GroundLift;
        var rotation = Quaternion.Euler(orientationEuler);
        var scale = key == "mass" ? 1f : DefaultGizmoScale;
        var targetScale = ZMirrorScale * scale;
        var t = anchor.transform;
        if (t.position == position && t.rotation == rotation && t.localScale == targetScale) return;
        t.SetPositionAndRotation(position, rotation);
        t.localScale = targetScale;
    }

    private static void RemoveLegacy(int id)
    {
        if (!LegacyInstances.TryGetValue(id, out var instance)) return;
        LegacyInstances.Remove(id);
        if (instance != null) Object.Destroy(instance);
    }

    // ── Placement ghost preview ─────────────────────────────────────────
    // The click-to-place tools (Markers/Army — see SC2ObjectsShell's
    // PlacementKind) show only a small flat 2D screen-space dot before the
    // click (DrawPlacementPreview's DrawViewportGlyph), which reads as
    // nothing by comparison since real placed markers render as full 3D
    // gizmo models (this same class's Sync above). This shows
    // the real 3D model at every symmetry-mirrored landing spot, tinted a
    // single flat unlit color so it's unambiguously "about to place," not
    // confusable with any of the 27 markers' own real per-type colors/
    // textures once actually placed.
    //
    // Deliberately kept as real GameObjects (not folded into the instanced
    // batching above) — see this file's own top-of-file doc comment for why.
    private static readonly List<GameObject> GhostInstances = new();
    private static Material _ghostMaterial;
    private static readonly Color GhostColor = new(0.15f, 0.95f, 1f, 1f);

    /// <summary>Call every frame a placement tool is armed, with the current
    /// cursor's landing spot(s) (including symmetry slots) for the marker
    /// key about to be placed. Pass an empty/null list (or a null/unknown
    /// key) to clear — callers should do this unconditionally when no valid
    /// landing spot exists this frame (cursor off terrain, tool disarmed,
    /// etc.) rather than leaving the last frame's ghosts stuck in place.</summary>
    public static void SyncGhosts(string key, IReadOnlyList<Vector3> positions, Vector3 orientationEuler)
    {
        ClearGhosts();
        if (string.IsNullOrEmpty(key) || positions == null || positions.Count == 0) return;
        var prefab = LoadPrefab(key);
        if (prefab == null) return;

        var material = GetGhostMaterial();
        foreach (var pos in positions)
        {
            var anchor = BuildAnchor(prefab, "Marker Placement Preview", material);
            ApplyLegacyTransform(anchor, key, pos, orientationEuler);
            GhostInstances.Add(anchor);
        }
    }

    /// <summary>Same purpose as SyncGhosts, but for a preset placement
    /// (SC2ObjectsShell.PlacementKind.MarkerPreset) where several entries —
    /// potentially different marker types, each at its own offset from the
    /// cursor — land in one click. Takes one (key, position, orientation)
    /// tuple per entry instead of one shared key/orientation for every
    /// position, so a mixed-type preset previews correctly too, not just
    /// today's uniform-type presets.</summary>
    public static void SyncGhostsMulti(IReadOnlyList<(string key, Vector3 position, Vector3 orientationEuler)> entries)
    {
        ClearGhosts();
        if (entries == null || entries.Count == 0) return;
        var material = GetGhostMaterial();
        foreach (var (key, position, orientationEuler) in entries)
        {
            if (string.IsNullOrEmpty(key)) continue;
            var prefab = LoadPrefab(key);
            if (prefab == null) continue;
            var anchor = BuildAnchor(prefab, "Marker Placement Preview", material);
            ApplyLegacyTransform(anchor, key, position, orientationEuler);
            GhostInstances.Add(anchor);
        }
    }

    public static void ClearGhosts()
    {
        foreach (var g in GhostInstances) if (g != null) Object.Destroy(g);
        GhostInstances.Clear();
    }

    private static Material GetGhostMaterial()
    {
        if (_ghostMaterial != null) return _ghostMaterial;
        var shader = Shader.Find("Unlit/Color") ?? SC2FallbackShader.Get();
        _ghostMaterial = new Material(shader) { name = "SC2MarkerGhostPreview", hideFlags = HideFlags.DontSave, color = GhostColor };
        return _ghostMaterial;
    }

    /// <summary>Confirmed via reflection that Unity's FBX
    /// importer bakes the real Blender-Z-up -> Unity-Y-up correction onto
    /// each prefab's OWN root transform (e.g. army.fbx root localEuler =
    /// (270.02, 0, 0) - with that rotation applied the model's bounds
    /// correctly span Y=[0..3.18], i.e. standing upright). Setting world
    /// position/rotation directly on the instantiated root (the original,
    /// simpler version of this code) overwrote that baked correction,
    /// leaving the model in Blender's raw Z-up orientation - lying flat
    /// instead of standing. Fix: instantiate the prefab as a child of a
    /// separate anchor GameObject (worldPositionStays:false, so the prefab
    /// keeps its own baked local transform untouched) and only ever
    /// move/rotate the ANCHOR to the marker's world transform. If
    /// overrideMaterial is non-null, every renderer in the instantiated
    /// prefab gets its materials replaced with it (used for the ghost
    /// preview's flat tint) - real placed markers pass null and keep their
    /// own imported materials.</summary>
    private static GameObject BuildAnchor(GameObject prefab, string name, Material overrideMaterial)
    {
        var anchor = new GameObject(name) { hideFlags = HideFlags.DontSave };
        anchor.transform.SetParent(GetRoot().transform, false);
        var modelInstance = Object.Instantiate(prefab, anchor.transform, false);
        modelInstance.hideFlags = HideFlags.DontSave;
        if (overrideMaterial != null)
        {
            foreach (var renderer in modelInstance.GetComponentsInChildren<Renderer>())
            {
                var mats = renderer.sharedMaterials;
                for (var i = 0; i < mats.Length; i++) mats[i] = overrideMaterial;
                renderer.sharedMaterials = mats;
            }
        }
        return anchor;
    }

    private static string ResolveIconKey(SC2ObjMarker marker)
    {
        if (SC2ObjectsShell.IsArmyStartPosition(marker.Name)) return SC2MarkerTemplates.ArmyIconKey;
        // Markers of the removed weather types, from an older map, keep their gizmo.
        return SC2MarkerTemplates.Find(marker.Type)?.IconKey
            ?? (string.Equals(marker.Type, "Weather Definition", System.StringComparison.OrdinalIgnoreCase) ? "other"
                : string.Equals(marker.Type, "Weather Generator", System.StringComparison.OrdinalIgnoreCase) ? "weather_generator" : null);
    }

    private static GameObject LoadPrefab(string key)
    {
        if (PrefabCache.TryGetValue(key, out var cached)) return cached;
        var prefab = Resources.Load<GameObject>("MarkerModels/" + key);
        PrefabCache[key] = prefab;
        return prefab;
    }

    private static GameObject GetRoot()
    {
        if (_root != null) return _root;
        _root = new GameObject(RootName) { hideFlags = HideFlags.DontSave };
        return _root;
    }
}
