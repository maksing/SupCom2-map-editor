// SC2WaterPlane.cs
//
// A real water plane for from-scratch procedural maps.
// SC2TerrainData.EnsureDefaultMaterialsAndPair never created a water
// material (only "Default Grass"/"Default Rock"), and the Environment
// shell's Water tab (WaterEnabled/WaterElevation/...) was pure info.bdf
// metadata with zero live visual effect — nothing read it to spawn/toggle
// any water rendering. This gives both a real exported "Water"
// material+layer (so SC2HfieldProjectionService.IsWaterMaterial correctly
// excludes it from height projection, matching real vanilla files) and a
// live in-editor preview.
//
// Geometry spec (confirmed against the original concept doc, byte-verified
// on a reference map): the real vanilla water material is exactly 4
// vertices at the corners of the map's playable rectangle, at
// y = WaterElevation — not an arbitrarily-sized quad, and not clipped/
// masked to terrain height.

using UnityEngine;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;

public static class SC2WaterPlane
{
    public const string MaterialName = "Water";

    /// <summary>32x32 quads (1024 quads, 2048 triangles) — a subdivided
    /// grid, not a single 2-triangle quad, specifically for the BSP tree
    /// TerrainBdf/EdbTreeBuilder builds over this layer (real terrain.bdf
    /// BSP construction wants real internal structure to partition, not one
    /// degenerate pair of triangles spanning the whole map). Collision2's
    /// OWN separate water quad (SC2TerrainExporter.BuildCollision2Model)
    /// deliberately stays a flat 4-vertex/2-triangle quad — it's pure
    /// additive collision geometry, not something a BSP gets built over,
    /// so it has no reason to share this subdivision.</summary>
    public const int GridResolution = 32;

    /// <summary>One corner/vertex of the GridResolution x GridResolution
    /// subdivided plane, in world space — gx/gz range 0..GridResolution
    /// inclusive (GridResolution+1 verts per axis). Centered on the map's
    /// playable-area center (CostsGridSize/2, per SC2HfieldProjectionService's
    /// own doc comment establishing the vanilla convention that the playable
    /// footprint always starts at world-space (0,0)), NOT anchored at world
    /// origin — WaterPlaneSize is independent of and usually larger than
    /// CostsGridSize (default 4096 vs. 1024), so anchoring both plane and
    /// map at the same (0,0) corner left the plane covering mostly area
    /// outside the actual map instead of surrounding it symmetrically.</summary>
    private static Vector3 GridVertex(SC2TerrainData data, float waterElevation, int gx, int gz)
    {
        var size = Mathf.Max(1, data.WaterPlaneSize);
        var mapCenter = data.CostsGridSize / 2f;
        var origin = mapCenter - size / 2f;
        var x = origin + size * gx / (float)GridResolution;
        var z = origin + size * gz / (float)GridResolution;
        return new Vector3(x, waterElevation, z);
    }

    /// <summary>Appends a real "Water" material + one-layer BSP-ready block
    /// (a GridResolution x GridResolution subdivided plane, CCW winding as
    /// seen from above matching TerrainMeshBuilder's own confirmed
    /// convention) to the export model — call before RebuildAllBsp, same as
    /// MergeImportedTerrain, so it gets a real BSP tree built over real
    /// internal structure, not a bare 2-triangle unindexed quad.</summary>
    public static void AppendToExportModel(TerrainModel model, SC2TerrainData data, float waterElevation)
    {
        var vertexBase = model.Vertices.Count;
        var uvScale = Mathf.Max(data.WaterUvScale, 0.01f);
        const int res = GridResolution;

        TerrainVertex MakeVertex(Vector3 p) => new()
        {
            PosX = p.x, PosY = p.y, PosZ = p.z,
            Uv1U = p.x / uvScale, Uv1V = p.z / uvScale,
            Uv2U = p.x / uvScale, Uv2V = p.z / uvScale,
            NormalX = 0, NormalY = 1, NormalZ = 0,
            TangentX = 1, TangentY = 0, TangentZ = 0,
            BinormalX = 0, BinormalY = 0, BinormalZ = 1,
        };

        for (var gz = 0; gz <= res; gz++)
        for (var gx = 0; gx <= res; gx++)
            model.Vertices.Add(MakeVertex(GridVertex(data, waterElevation, gx, gz)));

        var layer = new BdfIO.Formats.Terrain.TerrainLayer { CumulativeVertices = vertexBase, CumulativeIndices = model.Triangles.Count * 3 };
        model.Layers.Add(layer);

        // Local (layer-relative) indices, matching every other layer's own
        // convention (TerrainBdf/EdbTreeBuilder resolve indices relative to
        // the layer's own CumulativeVertices, never global). Per-quad
        // winding matches the original single-quad version's own verified
        // +Y-normal convention (0,2,1 / 0,3,2 for corners in row-major
        // gx,gz order).
        for (var gz = 0; gz < res; gz++)
        for (var gx = 0; gx < res; gx++)
        {
            var i00 = (uint)(gz * (res + 1) + gx);
            var i10 = (uint)(gz * (res + 1) + gx + 1);
            var i01 = (uint)((gz + 1) * (res + 1) + gx);
            var i11 = (uint)((gz + 1) * (res + 1) + gx + 1);
            model.Triangles.Add(new TerrainTriangle { V0 = i00, V1 = i11, V2 = i10 });
            model.Triangles.Add(new TerrainTriangle { V0 = i00, V1 = i01, V2 = i11 });
        }

        model.Materials.Add(BuildWaterMaterial(data));
    }

    /// <summary>Real vanilla water-material identity, confirmed directly
    /// against the corpus: Name varies across real maps
    /// ("Water", "water", "Water01", "Water_01" all occur), but
    /// ShaderFile/ShaderPrefix/State are exactly this on every real
    /// water.hlsl-shaded material checked (27 of 56 vanilla maps have real
    /// water; 2 more use a "fakewater" terrain.hlsl texture trick, a
    /// different, unrelated technique not reproduced here). Setting these
    /// for real (not just relying on SC2HfieldProjectionService.
    /// IsWaterMaterial's Name-substring fallback) makes the exported
    /// material's identity actually match what a real vanilla file has, not
    /// just something that happens to pass this project's own detection
    /// heuristic.
    ///
    /// Texture paths come from SC2TerrainData.WaterTexturePaths (real
    /// vanilla defaults, user-editable in the Materials panel) — these are
    /// plain path strings written into terrain.bdf, resolved by the user's
    /// own real game installation at load time, not Unity assets this
    /// project needs to bundle (see that field's own doc comment). Slot 6
    /// (waterDepth) is genuinely per-map — filled in from MapName here
    /// whenever the user has left it blank, never overriding an explicit
    /// value they've set.
    ///
    /// Used for both the real export AND the live preview
    /// (SC2WaterPreview) — SC2WaterMaterialFactory.TrySetTexture resolves
    /// these through SC2TerrainMaterialFactory.LoadTexturePublic's
    /// optional: true lookup, so a real local texture (e.g. already copied
    /// in by a previous map .scd import that happened to share these paths)
    /// genuinely loads and displays, while a miss stays quiet instead of
    /// warning on every rebuild.</summary>
    public static TerrainMaterial BuildWaterMaterial(SC2TerrainData data)
    {
        var mat = new TerrainMaterial
        {
            Name = MaterialName,
            ShaderFile = "/effects/water.hlsl",
            ShaderPrefix = "Water",
            State = "WaterState",
        };

        var paths = data.WaterTexturePaths;
        for (var i = 0; i < mat.TexturePaths.Length && i < paths.Length; i++)
        {
            var p = paths[i];
            mat.TexturePaths[i] = string.IsNullOrEmpty(p) ? null : p;
        }
        if (string.IsNullOrEmpty(paths.Length > 6 ? paths[6] : null))
        {
            var mapName = string.IsNullOrEmpty(data.MapName) ? "NewMap" : data.MapName;
            mat.TexturePaths[6] = $"/maps/{mapName}/{mapName}.waterDepth.dds";
        }

        return mat;
    }

    /// <summary>Builds/updates a standalone Unity Mesh for the live editor
    /// preview (not part of the exported model) - same GridResolution x
    /// GridResolution subdivided geometry AppendToExportModel builds (so
    /// the live view matches what actually ships in the BSP), reused/
    /// updated in place so this can be called every frame without
    /// allocating a new Mesh object (SetVertices/SetUVs/SetIndices still
    /// reallocate their own backing arrays each call — cheap at this
    /// vertex count, ~1089 verts for the default 32x32 grid).</summary>
    public static void UpdatePreviewMesh(Mesh mesh, SC2TerrainData data, float waterElevation)
    {
        var uvScale = Mathf.Max(data.WaterUvScale, 0.01f);
        const int res = GridResolution;
        var vertsPerAxis = res + 1;

        var verts = new Vector3[vertsPerAxis * vertsPerAxis];
        var normals = new Vector3[verts.Length];
        var tangents = new Vector4[verts.Length];
        var uvs = new Vector2[verts.Length];
        for (var gz = 0; gz <= res; gz++)
        for (var gx = 0; gx <= res; gx++)
        {
            var idx = gz * vertsPerAxis + gx;
            var p = GridVertex(data, waterElevation, gx, gz);
            verts[idx] = p;
            normals[idx] = Vector3.up;
            tangents[idx] = new Vector4(1f, 0f, 0f, 1f);
            // World-scale UV0 (matches AppendToExportModel's Uv1/Uv2, not a
            // fixed 0..1 quad) - SC2/Water's shader tiles wave-normal/foam
            // sampling directly off this UV, so the preview needs the same
            // real-world scale as the export to look right, not an
            // arbitrary one-tile-per-map mapping.
            uvs[idx] = new Vector2(p.x / uvScale, p.z / uvScale);
        }

        var indices = new int[res * res * 6];
        var ii = 0;
        for (var gz = 0; gz < res; gz++)
        for (var gx = 0; gx < res; gx++)
        {
            var i00 = gz * vertsPerAxis + gx;
            var i10 = gz * vertsPerAxis + gx + 1;
            var i01 = (gz + 1) * vertsPerAxis + gx;
            var i11 = (gz + 1) * vertsPerAxis + gx + 1;
            indices[ii++] = i00; indices[ii++] = i11; indices[ii++] = i10;
            indices[ii++] = i00; indices[ii++] = i01; indices[ii++] = i11;
        }

        mesh.Clear();
        mesh.indexFormat = verts.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTangents(tangents);
        mesh.SetUVs(0, uvs);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0);
        mesh.RecalculateBounds();
    }
}

/// <summary>
/// Live in-editor water plane — creates/updates/destroys a standalone
/// GameObject as InfoModel.WaterEnabled/WaterElevation change, called every
/// tick from SC2TerrainShell.Update() (same pattern as SC2Environment.
/// FindOrCreateSunLight: a bare, unparented GameObject, not a terrain-chunk
/// child — there's no public terrain-root Transform to parent under, and
/// this doesn't need one).
///
/// Known limitation, not a bug: for a from-scratch map there is no real
/// imported "Water" TerrainMaterial with real foam/normal/depth texture
/// paths (those are copyrighted game assets this project never bundles,
/// same reasoning as every other "no real vanilla texture available"
/// gap elsewhere in this codebase) - the shader renders with its own
/// null/default texture slots, not the real vanilla look. The plane's
/// position/size/color/wave parameters are still real and live-correct.
/// </summary>
public static class SC2WaterPreview
{
    private static GameObject _go;
    private static Mesh _mesh;
    private static Material _material;
    private static byte[] _lastHeaderSnapshot;

    /// <summary>Diagnostic: logs only on a real state transition
    /// (not every tick — compared against this each call), so it stays
    /// spam-free the same way other log-volume fixes elsewhere in this
    /// codebase do. Added specifically to pin down a water-mesh visibility
    /// bug that wasn't reproducible via static code reading alone.</summary>
    private static string _lastLoggedState;

    private static void LogStateIfChanged(string state)
    {
        if (state == _lastLoggedState) return;
        _lastLoggedState = state;
        Debug.Log($"[SC2Water] {state}");
    }

    /// <summary>overlayVisible: the top-panel Visibility menu's "Water"
    /// checkbox — was previously a dead toggle (persisted,
    /// synced, but nothing read it). Deliberately AND'd with WaterEnabled
    /// below, not OR'd — unlike Costs/Collision2's overlay checkboxes
    /// (which force those overlays ON from tabs where they'd otherwise be
    /// off), the water preview is already unconditionally shown whenever
    /// WaterEnabled is true regardless of active tab, so this checkbox's
    /// natural role is a plain show/hide override, same pattern as
    /// SC2TerrainShell.SetTerrainOverlayVisible.</summary>
    /// <param name="importedTerrains">When any of these already carries a
    /// real "Water" TerrainMaterial (detected the same way
    /// SC2TerrainExporter.BuildExportArtifacts' own "alreadyHasWater" check
    /// does — SC2HfieldProjectionService.IsWaterMaterial), the synthetic
    /// preview plane is skipped entirely: a fully-Blender-imported map's
    /// real water is already visible in the scene as part of that imported
    /// object's own mesh/material, and this app has no way to know its
    /// authored elevation matches WaterElevation — showing a second,
    /// synthetic plane on top would either double up two overlapping water
    /// surfaces or show one at the wrong height. Bind to what's already
    /// there instead of creating a new one, matching the real export's own
    /// behavior exactly.</param>
    public static void Ensure(SC2TerrainData data, SC2MapSession session, bool overlayVisible = true, System.Collections.Generic.List<SC2ImportedTerrainExportData> importedTerrains = null)
    {
        // overlayVisible previously only ever affected the
        // synthetic preview plane below (via Destroy()) - imported terrain's
        // own real water geometry lives in that object's regular
        // MeshRenderer, always active, and was never touched by this
        // checkbox at all. Set independently of every other gate in this method (data null,
        // WaterEnabled) - imported water is real per-map geometry this class
        // never creates/destroys, so only the "should it currently be
        // visible" bit belongs here, evaluated unconditionally on every call
        // rather than only on the path that also happens to bind the
        // synthetic plane's own material.
        ApplyImportedWaterVisibility(importedTerrains, overlayVisible);

        if (data == null || !data.IsInitialized || !overlayVisible)
        {
            SC2WaterRenderController.DisableForActiveCamera();
            LogStateIfChanged($"inactive: data={(data == null ? "null" : "ok")} initialized={data?.IsInitialized} overlayVisible={overlayVisible}");
            Destroy();
            return;
        }

        var info = SC2TerrainExporter.BuildInfoModel(data, session, quiet: true);
        if (info.WaterEnabled == 0)
        {
            SC2WaterRenderController.DisableForActiveCamera();
            LogStateIfChanged("WaterEnabled=0 (destroyed/not shown)");
            Destroy();
            return;
        }

        var importedAlreadyHasWater = importedTerrains != null && importedTerrains.Exists(
            t => t.Model.Materials.Exists(SC2HfieldProjectionService.IsWaterMaterial));
        if (importedAlreadyHasWater)
        {
            LogStateIfChanged("imported terrain already has real water — bound to it instead of a synthetic plane");
            Destroy();
            BindImportedWater(importedTerrains, info);
            return;
        }

        // A real, reproducible bug found here — _go is a scene
        // GameObject, but _material is a plain Material asset-like Object
        // that isn't scene-bound. If _go is ever destroyed by something
        // OTHER than this class's own Destroy() (e.g. a scene reload that
        // tears down every GameObject but leaves standalone Object
        // references like _material untouched), _go comes back null here
        // while _material is still a perfectly valid, non-null reference.
        // The freshly (re)created GameObject below used to leave its
        // MeshRenderer.sharedMaterial at null, relying entirely on the
        // material-rebuild block further down to assign it — but that block
        // is gated behind "_material == null || header changed", neither of
        // which is true in this exact case, so the new renderer's material
        // was never assigned and the water plane rendered as nothing,
        // with toggling WaterEnabled off/on again unable to bring it back
        // (first noticed via the "Import from info.bdf..." button, though
        // the underlying condition isn't specific to that action). Fixed
        // by unconditionally re-asserting
        // the renderer's material below, every call, once both _go and
        // _material are known to exist - a cheap property set, not
        // something that needs gating the way the actual property REBUILD
        // above it does.
        var recreatedGo = _go == null;
        if (recreatedGo)
        {
            _go = new GameObject("SC2 Water Preview") { hideFlags = HideFlags.DontSave };
            var mf = _go.AddComponent<MeshFilter>();
            _go.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "SC2WaterPreview" };
            mf.sharedMesh = _mesh;
        }

        SC2WaterPlane.UpdatePreviewMesh(_mesh, data, info.WaterElevation);

        // Only touch the material (property writes, or a first-time
        // allocation) when the underlying header bytes actually changed
        // since last tick - avoids doing that every single frame while
        // nothing's being edited, same reasoning SC2CostsShell's
        // _overlayDirty flag exists for its own texture rebuild.
        //
        // This gate was observed, in a real play session, to
        // fail open far more often than expected (each of the water
        // shader's 7 textures individually re-resolved 550 000+ times over
        // one session per the Player.log) — root cause not fully pinned
        // down statically (SC2TerrainMaterialFactory's new load-result
        // cache and HasProperty guards independently close the resulting
        // log-spam/warning-spam regardless of why this gate fires). What IS
        // fixed here, unconditionally: even when this branch DOES run more
        // than strictly necessary, it no longer pays for a fresh Material
        // allocation + shader-variant lookup + DestroyImmediate of the old
        // one every time - ApplyTo updates the existing material's
        // properties in place, which is cheap and log-free by construction
        // (plain SetTexture/SetColor/SetVector calls).
        if (_material == null || !HeaderUnchanged(info))
        {
            // Build the same Vertices+Layer+Material triple the real export
            // path does (AppendToExportModel), not just a bare Materials
            // entry - SC2WaterMaterialFactory.ApplyTo locates the real
            // "Water" TerrainMaterial via model.Materials.Find (needs a
            // real material entry present, even though it only ever reads
            // InfoModel fields for world-space bounds, never the mesh's own
            // vertex positions - so this subdivided grid changing vertex
            // COUNT here has no effect on that lookup).
            var syntheticModel = new BdfIO.Formats.Terrain.TerrainModel();
            SC2WaterPlane.AppendToExportModel(syntheticModel, data, info.WaterElevation);
            if (_material == null)
                _material = SC2WaterMaterialFactory.Create(syntheticModel, info);
            else
                SC2WaterMaterialFactory.ApplyTo(_material, syntheticModel, info);
            _lastHeaderSnapshot = (byte[])info.Header.Clone();
        }

        // See the long comment above _go's recreation block: this must run
        // every call, not just inside the rebuild branch above, or a _go
        // that was recreated this tick while _material survived from before
        // (with an unchanged header) ends up with no material assigned at
        // all. A plain property set on an existing component - cheap
        // regardless of whether it was actually necessary this call.
        if (recreatedGo || _go.GetComponent<MeshRenderer>().sharedMaterial != _material)
            _go.GetComponent<MeshRenderer>().sharedMaterial = _material;

        LogStateIfChanged($"active: elevation={info.WaterElevation} gridSize={data.CostsGridSize} " +
                           $"go.active={_go.activeInHierarchy} go.pos={_go.transform.position} " +
                           $"meshBounds={_mesh.bounds} vertCount={_mesh.vertexCount} " +
                           $"rendererEnabled={_go.GetComponent<MeshRenderer>().enabled} " +
                           $"material={(_material != null ? _material.name : "null")} " +
                           $"waterColorA={info.WaterColorA} maxDepth={info.MaxWaterDepth}");
    }

    /// <summary>Finds each imported terrain object's own "Water"-shaded
    /// material (assigned by SC2TerrainMaterialFactory.Create at import
    /// time — real SC2/Water shader, but only ever a hardcoded placeholder
    /// tint, never textures or WaterElevation/SunDirection/etc-derived
    /// properties, and never refreshed again afterward) and calls
    /// SC2WaterMaterialFactory.ApplyTo on it every tick, the same way this
    /// class already keeps the synthetic preview plane's own material live.
    /// This is the actual "bind, don't create a new one" behavior for a
    /// map whose water geometry came from a real imported terrain.bdf: the
    /// mesh/geometry is never touched (still whatever the import authored),
    /// only the material's shader PROPERTIES are kept in sync with the
    /// live Environment values, exactly like the synthetic plane's own
    /// material already was.</summary>
    private static void BindImportedWater(System.Collections.Generic.List<SC2ImportedTerrainExportData> importedTerrains, InfoModel info)
    {
        foreach (var imported in importedTerrains)
        {
            if (!imported.Model.Materials.Exists(SC2HfieldProjectionService.IsWaterMaterial)) continue;
            var go = imported.Transform != null ? imported.Transform.gameObject : null;
            var renderer = go != null ? go.GetComponent<MeshRenderer>() : null;
            if (renderer == null) continue;

            var mats = renderer.sharedMaterials;
            foreach (var mat in mats)
            {
                if (mat == null || mat.shader == null || mat.shader.name != "SC2/Water") continue;
                // ApplyTo mutates this Material asset's own properties in
                // place (SetColor/SetTexture/SetVector) - no need to
                // reassign renderer.sharedMaterials back, same reasoning
                // this class's own synthetic-plane refresh already
                // documents above.
                SC2WaterMaterialFactory.ApplyTo(mat, imported.Model, info);
            }
        }
    }

    /// <summary>Sets _SC2WaterVisible on every real imported-terrain water
    /// material so the shader's own clip() can hide/show it independently of
    /// the shared MeshRenderer's other (non-water) materials - see the call
    /// site's comment in Ensure() for why this can't be done via
    /// SetActive/renderer.enabled instead.</summary>
    private static void ApplyImportedWaterVisibility(System.Collections.Generic.List<SC2ImportedTerrainExportData> importedTerrains, bool visible)
    {
        if (importedTerrains == null) return;
        foreach (var imported in importedTerrains)
        {
            var go = imported.Transform != null ? imported.Transform.gameObject : null;
            var renderer = go != null ? go.GetComponent<MeshRenderer>() : null;
            if (renderer == null) continue;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || mat.shader == null || mat.shader.name != "SC2/Water") continue;
                mat.SetFloat("_SC2WaterVisible", visible ? 1f : 0f);
            }
        }
    }

    private static bool HeaderUnchanged(InfoModel info)
    {
        if (_lastHeaderSnapshot == null || _lastHeaderSnapshot.Length != info.Header.Length) return false;
        for (var i = 0; i < info.Header.Length; i++)
            if (_lastHeaderSnapshot[i] != info.Header[i]) return false;
        return true;
    }

    public static void Destroy()
    {
        if (_go != null) { UnityEngine.Object.DestroyImmediate(_go); _go = null; }
        if (_mesh != null) { UnityEngine.Object.DestroyImmediate(_mesh); _mesh = null; }
        if (_material != null) { UnityEngine.Object.DestroyImmediate(_material); _material = null; }
        _lastHeaderSnapshot = null;
    }
}
