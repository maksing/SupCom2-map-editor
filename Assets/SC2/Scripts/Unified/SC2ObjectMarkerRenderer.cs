using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU-instanced replacement for the Objects tab's per-object IMGUI viewport
/// pick-dots (SC2ObjectsShell.DrawViewportGlyph) — the IMGUI path profiled
/// poorly at scale (2871 props): every on-screen dot was 2 GUI.DrawTexture
/// calls plus a GUI.matrix push/pop, several thousand draw calls a frame.
///
/// Architecture (mirrors SC2GameAssetScenePreview/SC2MarkerScenePreview's
/// own "batch by shared visual, one Graphics.DrawMeshInstanced per batch"
/// pattern): one shared 2-triangle quad mesh, drawn per category batch
/// (Prop/Unit/Marker x Normal/Selected, plus one extra per distinct marker
/// color actually in use — see Sync's own note on why markers can't share
/// a single "normal" bucket the way Props/Units do) via
/// Graphics.DrawMeshInstanced, chunked at its 1023-instance hard limit.
/// Each instance's Matrix4x4 is translation-only (Matrix4x4.Translate) —
/// rotation/scale are meaningless for a screen-space billboard — the
/// shared shader (Resources/SC2ObjectMarker.shader)
/// expands each instance into a constant-PIXEL-size diamond entirely in
/// clip space, so panning/zooming the camera needs zero CPU work: only the
/// VP matrix changes, and the GPU re-projects the same instance array.
///
/// Deliberately NOT implemented, by design rather than oversight:
/// - Per-instance color/flags/objectId GPU buffer — every batch here
///   already has one shared color (that's what makes it a "batch"), so a
///   per-instance color channel would only be needed for GPU-side picking,
///   which is deliberately deferred as a separate concern from rendering —
///   CPU-side picking (SC2ObjectsShell's existing
///   distance-to-cursor loop, unchanged) stays authoritative.
/// - Spatial-grid picking / ID render target — a
///   later step (after plain instancing is proven), not a blocker for
///   this pass.
/// - DrawMeshInstancedIndirect — plain instancing is more than sufficient
///   at the current scale.
/// </summary>
public static class SC2ObjectMarkerRenderer
{
    private const int MaxInstancingCount = 1023; // Graphics.DrawMeshInstanced's own hard limit per call.

    private const float NormalPixelSize = 9f;
    private const float SelectedPixelSize = 13f;
    private const float MarkerNormalPixelSize = 11f; // matches DrawViewportGlyph's own kind==Marker?11:9 split
    private const float MarkerSelectedPixelSize = 15f;
    private const float HoverPixelSize = 15f;
    private const float PropIconPixelSize = 18f; // bigger than the plain diamond dot — a real icon needs more pixels to read

    private static readonly Color PropNormalColor = new Color(.25f, .86f, 1f, .75f);
    private static readonly Color UnitNormalColor = new Color(.86f, .45f, 1f, .78f);
    private static readonly Color MarkerDefaultColor = new Color(.35f, .9f, .95f, .75f);
    private static readonly Color SelectedColor = new Color(1f, .9f, .12f, .95f);
    private static readonly Color MarkerSelectedColor = new Color(1f, .9f, .12f, .98f);

    /// <summary>Per-category Props rendering: trees use a tree icon, rocks
    /// use a rock icon, crates use a box icon, buildings use a buildings
    /// icon; everything
    /// else keeps the standard diamond, just with a different color per
    /// category, categories determined from the blueprint folders.
    /// Category = the first path segment after "/props/" in a prop's own
    /// BlueprintPath (e.g. "/props/Trees/oak01_prop.bp" -&gt; "Trees") — the
    /// exact same convention SC2UnitPropBrowser.GetPropFolder already uses
    /// for the Props browser's own folder filter, confirmed against the
    /// real gamedata.scd/props/ folder list (Barges/Buildings/Cranes/
    /// Crates/Debris/DefaultWreckage/Lights/Markers/RPLACEHOLDER/Rocks/
    /// Structures/Trees/Warehouses/Wreckage).</summary>
    private static readonly Dictionary<string, Texture2D> PropCategoryIcons = new(System.StringComparer.OrdinalIgnoreCase);
    private static bool _propCategoryIconsLoaded;

    private static void EnsurePropCategoryIcons()
    {
        if (_propCategoryIconsLoaded) return;
        _propCategoryIconsLoaded = true;
        void Load(string category, string resourceKey)
        {
            var tex = Resources.Load<Texture2D>("EditorIcons/Props/" + resourceKey);
            if (tex != null) PropCategoryIcons[category] = tex;
        }
        Load("Trees", "trees");
        Load("Rocks", "rocks");
        Load("Crates", "crates");
        Load("Buildings", "buildings");
    }

    /// <summary>Mirrors SC2UnitPropBrowser.GetPropFolder's own path parsing
    /// (first segment after "/props/"), operating on a PLACED prop's own
    /// BlueprintPath instead of a catalog entry's SourcePath — kept as a
    /// separate small function rather than sharing code across the two
    /// unrelated classes/assemblies-of-concern.</summary>
    private static string GetPropCategory(string blueprintPath)
    {
        if (string.IsNullOrEmpty(blueprintPath)) return "";
        var normalized = blueprintPath.Replace('\\', '/');
        const string marker = "/props/";
        var idx = normalized.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
        string relative;
        if (idx >= 0) relative = normalized.Substring(idx + marker.Length);
        else if (normalized.StartsWith("props/", System.StringComparison.OrdinalIgnoreCase)) relative = normalized.Substring("props/".Length);
        else return "";
        relative = relative.TrimStart('/');
        var sep = relative.IndexOf('/');
        return sep > 0 ? relative.Substring(0, sep) : "";
    }

    /// <summary>Deterministic hash-to-hue so every distinct category
    /// (however many real folders the game data actually has, or a custom
    /// map's own props) gets a stable, visually distinct diamond color
    /// without hand-listing every folder name — same category always
    /// resolves to the same color, across sessions.</summary>
    private static Color ColorForCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return PropNormalColor;
        unchecked
        {
            var hash = 17;
            foreach (var c in category) hash = hash * 31 + char.ToLowerInvariant(c);
            var hue = (Mathf.Abs(hash) % 1000) / 1000f;
            var rgb = Color.HSVToRGB(hue, 0.62f, 0.95f);
            return new Color(rgb.r, rgb.g, rgb.b, 0.8f);
        }
    }

    private sealed class Batch
    {
        public Material Material;
        public readonly List<Vector3> Positions = new();
    }

    private static Mesh _quadMesh;
    private static Shader _shader;
    private static readonly Dictionary<string, Batch> _batches = new();
    private static readonly HashSet<string> _touchedThisSync = new();
    private static Matrix4x4[] _drawBuffer;

    // Hover — a single instance, mutated in place every frame regardless of
    // the dirty-gated Sync below (cheap: one SetColor/SetFloat, no list
    // rebuild) since it changes on every mouse move, not just on a real
    // Data edit.
    private static Material _hoverMaterial;
    private static bool _hasHover;
    private static Vector3 _hoverPosition;
    private static readonly List<Vector3> _hoverScratch = new() { Vector3.zero };

    private static void EnsureMesh()
    {
        if (_quadMesh != null) return;
        _quadMesh = new Mesh { name = "SC2ObjectMarkerQuad" };
        _quadMesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        _quadMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        _quadMesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        // The vertex shader repositions everything in clip space using the
        // instance matrix's translation, ignoring this mesh's own tiny
        // local-space geometry entirely for that purpose — a huge bounds
        // keeps Unity's own per-draw-call bounds check (a coarse cull
        // applied to the whole submitted batch, same caveat already
        // documented on SC2TerrainShell.IsSphereInFrustum) from ever
        // discarding a marker whose real world position is far from
        // (0,0,0) in local-mesh terms.
        _quadMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
    }

    private static Shader ResolveShader()
    {
        if (_shader != null) return _shader;
        _shader = Resources.Load<Shader>("SC2ObjectMarker") ?? SC2FallbackShader.Get();
        return _shader;
    }

    private static Material CreateMaterial(Color color, float pixelSize)
    {
        var material = new Material(ResolveShader()) { enableInstancing = true };
        ApplyMaterialParams(material, color, pixelSize);
        return material;
    }

    private static void ApplyMaterialParams(Material material, Color color, float pixelSize)
    {
        material.SetColor("_Color", color);
        material.SetColor("_InnerColor", new Color(.04f, .05f, .07f, Mathf.Clamp01(color.a)));
        material.SetFloat("_PixelSize", pixelSize);
    }

    private static Batch GetOrCreateBatch(string key, Color color, float pixelSize)
    {
        if (_batches.TryGetValue(key, out var batch)) return batch;
        batch = new Batch { Material = CreateMaterial(color, pixelSize) };
        _batches[key] = batch;
        return batch;
    }

    private static Material CreateIconMaterial(Texture2D icon, float pixelSize)
    {
        var material = new Material(ResolveShader()) { enableInstancing = true };
        material.SetTexture("_IconTex", icon);
        material.SetFloat("_UseIcon", 1f);
        material.SetFloat("_PixelSize", pixelSize);
        return material;
    }

    private static Batch GetOrCreateIconBatch(string key, Texture2D icon, float pixelSize)
    {
        if (_batches.TryGetValue(key, out var batch)) return batch;
        batch = new Batch { Material = CreateIconMaterial(icon, pixelSize) };
        _batches[key] = batch;
        return batch;
    }

    /// <summary>Rebuilds every category's position list from scratch. Only
    /// call this when something that could actually change the result
    /// happened — SC2ObjectsShell gates this behind the exact same
    /// _dataVersion dirty-flag/safety-net it already uses for
    /// SC2GameAssetScenePreview.SyncTransforms, not unconditionally every
    /// frame/GUI event.</summary>
    /// <param name="showProps">Mirrors the Visibility &gt; Props overlay —
    /// when false, no prop gets a dot at all (no selected-only carve-out;
    /// Props doesn't have the Paths/Chains cross-tab-selection workflow
    /// Markers does).</param>
    /// <param name="showUnits">Same contract as <paramref name="showProps"/>, for Units.</param>
    /// <param name="showMarkers">When false, only currently-selected markers
    /// still get a dot — matches the original IMGUI "else if
    /// (_selMarkers.Count > 0)" fallback (see SC2ObjectsShell.OnGameViewGUI's
    /// own comment on it): a common Paths/Chains workflow hides marker
    /// clutter but still needs box-select/Connect feedback on the ones
    /// already selected.</param>
    public static void Sync(SC2TerrainData data, HashSet<int> selProps, HashSet<int> selUnits, HashSet<int> selMarkers,
        HashSet<int> hiddenPropIds, HashSet<int> hiddenUnitIds, HashSet<int> hiddenMarkerIds,
        bool showProps, bool showUnits, bool showMarkers)
    {
        EnsureMesh();
        _touchedThisSync.Clear();
        foreach (var batch in _batches.Values) batch.Positions.Clear();

        if (data == null) return;

        if (showProps)
        {
            EnsurePropCategoryIcons();
            foreach (var prop in data.ObjProps)
            {
                if (hiddenPropIds != null && hiddenPropIds.Contains(prop.Id)) continue;
                var selected = selProps != null && selProps.Contains(prop.Id);
                if (selected)
                {
                    const string key = "prop:selected";
                    var selectedBatch = GetOrCreateBatch(key, SelectedColor, SelectedPixelSize);
                    selectedBatch.Positions.Add(prop.Position);
                    _touchedThisSync.Add(key);
                    continue;
                }

                var category = GetPropCategory(prop.BlueprintPath);
                Batch batch;
                string batchKey;
                if (!string.IsNullOrEmpty(category) && PropCategoryIcons.TryGetValue(category, out var icon))
                {
                    batchKey = "prop:icon:" + category;
                    batch = GetOrCreateIconBatch(batchKey, icon, PropIconPixelSize);
                }
                else
                {
                    batchKey = "prop:color:" + (string.IsNullOrEmpty(category) ? "default" : category.ToLowerInvariant());
                    batch = GetOrCreateBatch(batchKey, ColorForCategory(category), NormalPixelSize);
                }
                batch.Positions.Add(prop.Position);
                _touchedThisSync.Add(batchKey);
            }
        }

        if (showUnits)
            foreach (var unit in data.ObjUnits)
            {
                if (unit.IsGroup) continue;
                if (hiddenUnitIds != null && hiddenUnitIds.Contains(unit.Id)) continue;
                var selected = selUnits != null && selUnits.Contains(unit.Id);
                var key = selected ? "unit:selected" : "unit:normal";
                var batch = selected
                    ? GetOrCreateBatch(key, SelectedColor, SelectedPixelSize)
                    : GetOrCreateBatch(key, UnitNormalColor, NormalPixelSize);
                batch.Positions.Add(unit.Position);
                _touchedThisSync.Add(key);
            }

        // Markers can't share one "normal" bucket the way Props/Units do —
        // each marker TEMPLATE (and, since SC2ObjMarker.ColorHex is a
        // per-instance field, potentially each individual placed marker)
        // carries its own configurable color, unlike Props/Units' fixed
        // 2-color scheme. Grouped dynamically by resolved color instead —
        // in practice bounded by however many distinct marker
        // types/colors are actually placed on the map (a small, fixed
        // catalog — see SC2MarkerTemplates), not by marker COUNT, so this
        // still collapses thousands of markers into a handful of batches
        // in the common case. When showMarkers is off, only selected
        // markers are included at all (see this method's own param doc).
        foreach (var marker in data.ObjMarkers)
        {
            var selected = selMarkers != null && selMarkers.Contains(marker.Id);
            if (!showMarkers && !selected) continue;
            if (hiddenMarkerIds != null && hiddenMarkerIds.Contains(marker.Id)) continue;
            string key;
            Color color;
            float pixelSize;
            if (selected)
            {
                key = "marker:selected";
                color = MarkerSelectedColor;
                pixelSize = MarkerSelectedPixelSize;
            }
            else
            {
                var template = SC2MarkerTemplates.Find(marker.Type);
                color = template != null ? SC2MarkerTemplates.HexToColor(marker.ColorHex) : MarkerDefaultColor;
                pixelSize = MarkerNormalPixelSize;
                key = "marker:c:" + ColorUtility.ToHtmlStringRGBA(color);
            }
            var batch = GetOrCreateBatch(key, color, pixelSize);
            batch.Positions.Add(marker.Position);
            _touchedThisSync.Add(key);
        }

        // Drop batches nothing touched this pass (e.g. the last marker of
        // one custom color got deleted/recolored) — otherwise the batch
        // dictionary only ever grows across a long editing session.
        List<string> stale = null;
        foreach (var key in _batches.Keys)
            if (!_touchedThisSync.Contains(key))
                (stale ??= new List<string>()).Add(key);
        if (stale != null)
            foreach (var key in stale)
            {
                if (_batches.TryGetValue(key, out var batch) && batch.Material != null) SafeDestroy(batch.Material);
                _batches.Remove(key);
            }
    }

    /// <summary>Updates the single hover glyph — cheap (no list rebuild),
    /// safe to call every frame regardless of Sync's own dirty-gating.
    /// Pass null to hide it (nothing currently hovered, or
    /// _showAllViewportDots-equivalent legacy mode is active).</summary>
    public static void SetHover(Vector3? position, Color color, bool isMarker)
    {
        if (!position.HasValue)
        {
            _hasHover = false;
            return;
        }
        EnsureMesh();
        if (_hoverMaterial == null) _hoverMaterial = CreateMaterial(color, HoverPixelSize);
        else ApplyMaterialParams(_hoverMaterial, color, isMarker ? HoverPixelSize + 2f : HoverPixelSize);
        _hasHover = true;
        _hoverPosition = position.Value;
    }

    /// <summary>Must be called once per rendered frame (same LateUpdate
    /// call site as SC2GameAssetScenePreview/SC2MarkerScenePreview's own
    /// DrawAllBatches) — Graphics.DrawMeshInstanced is immediate-mode, not
    /// a persistent scene object.</summary>
    public static void DrawAllBatches(Camera camera)
    {
        if (camera == null) return; // nothing sensible to project a billboard against without a real camera.
        EnsureMesh();
        foreach (var batch in _batches.Values)
            DrawBatch(batch.Material, batch.Positions, batch.Positions.Count);
        if (_hasHover && _hoverMaterial != null)
        {
            _hoverScratch[0] = _hoverPosition;
            DrawBatch(_hoverMaterial, _hoverScratch, 1);
        }
    }

    private static void DrawBatch(Material material, List<Vector3> positions, int count)
    {
        if (material == null || count == 0) return;
        var bufferSize = Mathf.Min(count, MaxInstancingCount);
        if (_drawBuffer == null || _drawBuffer.Length < bufferSize) _drawBuffer = new Matrix4x4[bufferSize];

        var written = 0;
        for (var i = 0; i < count; i++)
        {
            _drawBuffer[written++] = Matrix4x4.Translate(positions[i]);
            if (written == _drawBuffer.Length)
            {
                Graphics.DrawMeshInstanced(_quadMesh, 0, material, _drawBuffer, written, null, ShadowCastingMode.Off, false);
                written = 0;
            }
        }
        if (written > 0)
            Graphics.DrawMeshInstanced(_quadMesh, 0, material, _drawBuffer, written, null, ShadowCastingMode.Off, false);
    }

    /// <summary>Disposes every batch's Material (map switch) — mirrors
    /// SC2GameAssetScenePreview.ClearAll's own reasoning.</summary>
    public static void ClearAll()
    {
        foreach (var batch in _batches.Values)
            if (batch.Material != null) SafeDestroy(batch.Material);
        _batches.Clear();
        if (_hoverMaterial != null) { SafeDestroy(_hoverMaterial); _hoverMaterial = null; }
        _hasHover = false;
    }

    /// <summary>Object.Destroy defers to end-of-frame and requires
    /// Application.isPlaying — this class also runs in the Editor's Edit
    /// Mode (via [ExecuteAlways] hosts), where DestroyImmediate is the
    /// correct call. Same pattern as SC2GameAssetScenePreview.SafeDestroy.</summary>
    private static void SafeDestroy(Object obj)
    {
        if (Application.isPlaying) Object.Destroy(obj);
        else Object.DestroyImmediate(obj);
    }
}
