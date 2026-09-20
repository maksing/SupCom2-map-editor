using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Live preview of the background terrain's seam-stitching skirt
/// (SC2BackgroundTerrain.BuildSeamSkirt) as its own small static mesh,
/// separate from the background's chunked SC2TerrainRuntime.
///
/// The skirt's own geometry (a ruled-surface strip fanning the core's fine
/// boundary to the background's coarse boundary) has no equivalent in
/// SC2TerrainRuntime's uniform per-chunk grid, so it can't just become more
/// chunks — it stays its own small mesh, rebuilt only when the topology
/// actually changes (background Regenerate/Footprint Size) or a sculpt
/// stroke that touched the seam ring just ended, not every drag frame —
/// same "not every frame" trade-off SC2BackgroundTerrainPreview's own
/// debounce used to make for the WHOLE background mesh, just now scoped to
/// only this thin strip, which is cheap enough to afford per-stroke.
///
/// Renders with the background runtime's OWN shared chunk material
/// (SC2TerrainRuntime.SharedChunkMat) rather than building a separate one —
/// an earlier version built its own material via
/// SC2BackgroundTerrain.CreateLivePairMaterial (the real in-game
/// SC2Terrain.shader), which showed a jarring bright/flat patch wherever a
/// pair had no real diffuse texture assigned yet (the common case): that
/// shader lights raw white-albedo-times-sun-color, while the chunk mesh's
/// MatCap-based editor preview renders a plausible surface regardless of
/// missing textures. Two different shaders answering "what does no-texture
/// look like" differently guarantees a visible seam-vs-body mismatch no
/// matter how carefully either one's own parameters are tuned — sharing the
/// exact same material is what actually fixes it.
/// </summary>
public static class SC2BackgroundSeamSkirt
{
    private static GameObject _go;
    private static Mesh _mesh;

    public static void Rebuild(SC2TerrainData coreData, SC2TerrainShell shell)
    {
        var bgRuntime = shell?.BackgroundRuntime;
        if (coreData == null || !coreData.IsInitialized || !coreData.BackgroundTerrainEnabled || bgRuntime?.SharedChunkMat == null)
        {
            Destroy();
            return;
        }

        var bg = SC2BackgroundTerrain.EnsureGenerated(coreData);
        if (bg == null)
        {
            Destroy();
            return;
        }

        SC2BackgroundTerrain.BuildSeamSkirt(coreData, out var verts, out var normals, out var tris);
        if (tris.Length == 0)
        {
            Destroy();
            return;
        }

        // BuildSeamSkirt reads coreData.Heights/bg.Heights directly — the
        // SETTLED CPU arrays, which only re-sync from their own live
        // runtime on stroke MouseUp (see SC2GeometryMode's and
        // SC2BackgroundEditMode's own comments on that). Mid-drag, that
        // made the skirt visibly lag whichever side was actively being
        // sculpted by exactly one stroke (it kept showing the height from
        // before the CURRENT stroke started). Re-sample every vertex's Y
        // live here instead — same fix class as
        // SC2TerrainShell.SampleLiveRuntimeHeight/SampleLiveBackgroundHeight
        // already applied to the brush gizmo/reprojection for the same
        // staleness reason. Which side a vertex belongs to is decided by
        // the same "inside the core's own rectangle" test the uv1 push
        // below already needs anyway.
        var coreWForHeight = coreData.GridWidth * coreData.CellSize;
        var coreDForHeight = coreData.GridHeight * coreData.CellSize;
        var uv2 = new Vector2[verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            var onCore = v.x >= 0f && v.x <= coreWForHeight && v.z >= 0f && v.z <= coreDForHeight;
            v.y = onCore ? shell.SampleLiveRuntimeHeight(v.x, v.z) : shell.SampleLiveBackgroundHeight(v.x, v.z);
            verts[i] = v;
            // The stretch-compensated tiling coordinate of the CORE for every skirt vertex, continued
            // past the core's edge for the ones outside it — the same thing the exporter writes into
            // UV1. Taking the background's own coordinate for the outer vertices (as an earlier
            // version did) put two unrelated coordinate frames (core-local vs background-local,
            // hundreds of units apart) at the two ends of a strip only one coarse cell wide, so the
            // texture was smeared across it.
            uv2[i] = shell.Runtime.SampleUv1TileExtended(v.x, v.z);
        }

        if (_go == null)
        {
            _go = new GameObject("SC2 Background Seam Skirt") { hideFlags = HideFlags.DontSave };
            var mf = _go.AddComponent<MeshFilter>();
            var mr = _go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _mesh = new Mesh { name = "SC2BackgroundSeamSkirtMesh", indexFormat = IndexFormat.UInt32 };
            mf.sharedMesh = _mesh;
        }
        _go.SetActive(true);

        // BuildSeamSkirt already returns world-space vertices (see its own
        // doc comment), so this object stays at identity transform.
        //
        // uv0/uv1 match SC2TerrainRuntime.RebuildChunk's own exact
        // convention for the surrounding chunks — uv0 is RAW world XZ
        // (SharedChunkMat's shader applies its own per-pair tiling via
        // _TilingLUT, sampled through uv1 below; pre-dividing here like the
        // old separate-material version did would double-apply tiling
        // against that shader), uv1 is grid-position normalized to the
        // background's own full bounds (0..1) — mathematically identical to
        // RebuildChunk's `gx/GridWidth, gz/GridHeight` since
        // worldPos = minX + gx*CellSize and span = GridWidth*CellSize.
        SC2BackgroundTerrain.ComputeBounds(coreData, coreData.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out var bgCell);
        var spanX = Mathf.Max(1f, maxX - minX);
        var spanZ = Mathf.Max(1f, maxZ - minZ);
        // The skirt's own INNER edge sits at the core's true boundary — by
        // design, physically inside the carved-out hole (BuildPairIndexAndCarveHole
        // marks that whole region, expanded one coarse cell further per
        // ExpandRingByOneCoarseCell, as -1/"unassigned" in PairMapRT). Using
        // an inner vertex's own true position for uv1 therefore samples
        // straight into that unassigned region — SC2TerrainView.shader's
        // own "no pair here" fallback (a flat checker), exactly the empty-
        // looking seam this was meant to fix. uv1 (pair/tiling lookup) is
        // pushed just outside the core rectangle for any such vertex,
        // landing on the nearest REAL background territory instead; uv0
        // (raw world XZ, used only for tiling continuity) keeps the true
        // position so the texture doesn't visibly jump across the strip.
        var coreW = coreData.GridWidth * coreData.CellSize;
        var coreD = coreData.GridHeight * coreData.CellSize;
        var margin = bgCell * 1.5f; // clears the one-coarse-cell-wider carve ring with room to spare
        var coreCx = coreW * 0.5f;
        var coreCz = coreD * 0.5f;
        var halfW = coreW * 0.5f + margin;
        var halfD = coreD * 0.5f + margin;
        var uv0 = new Vector2[verts.Length];
        var uv1 = new Vector2[verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            uv0[i] = new Vector2(verts[i].x, verts[i].z);
            var sx = verts[i].x;
            var sz = verts[i].z;
            if (sx > -margin && sx < coreW + margin && sz > -margin && sz < coreD + margin)
            {
                // Push radially outward from the core rectangle's own
                // center, scaling (dx,dz) up until the point clears the
                // rectangle through whichever axis it's already closer to
                // exiting via (a standard center-to-point AABB-exit scale:
                // min of the two per-axis "how far to reach this axis'
                // half-extent" ratios) — a corner vertex escapes diagonally
                // instead of snapping to a single edge, which is what let
                // two triangle corners sharing one degenerate/near-corner
                // vertex end up pushed to two DIFFERENT, far-apart edges
                // (one clamped in X, its triangle neighbor clamped in Z),
                // stretching that triangle's interpolated sampling straight
                // back through the unassigned hole in the middle and
                // showing the shader's flat "no pair" fallback there.
                var dx = sx - coreCx;
                var dz = sz - coreCz;
                var scaleX = Mathf.Abs(dx) > 0.0001f ? halfW / Mathf.Abs(dx) : float.MaxValue;
                var scaleZ = Mathf.Abs(dz) > 0.0001f ? halfD / Mathf.Abs(dz) : float.MaxValue;
                var scale = Mathf.Min(scaleX, scaleZ);
                if (scale > 1f && scale < float.MaxValue)
                {
                    sx = coreCx + dx * scale;
                    sz = coreCz + dz * scale;
                }
                else
                {
                    // dx and dz both ~0 (a vertex sitting almost exactly on
                    // the core's own center — not expected in practice for
                    // a boundary-hugging skirt, but fall back to a fixed
                    // safe direction rather than leaving it unclamped).
                    sx = coreCx + halfW;
                }
            }
            uv1[i] = new Vector2((sx - minX) / spanX, (sz - minZ) / spanZ);
        }

        _mesh.Clear();
        _mesh.SetVertices(verts);
        _mesh.SetNormals(normals);
        _mesh.SetUVs(0, uv0);
        _mesh.SetUVs(1, uv1);
        _mesh.SetUVs(2, uv2);
        _mesh.SetTriangles(tris, 0);
        _mesh.RecalculateBounds();

        _go.GetComponent<MeshRenderer>().sharedMaterial = bgRuntime.SharedChunkMat;
    }

    public static void Destroy()
    {
        if (_go != null) Object.DestroyImmediate(_go);
        _go = null;
        _mesh = null;
    }
}
