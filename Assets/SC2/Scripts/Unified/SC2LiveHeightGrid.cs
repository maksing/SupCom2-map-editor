// SC2LiveHeightGrid.cs
//
// A previous fix routed Costs
// painting's cursor raycast through TryRaycastImportedTerrain (Physics
// against a MeshCollider) with a fallback to RaycastTerrainHeight (analytic
// against ONLY the live procedural terrain) — two different, source-specific
// mechanisms picked by trial. This is a more fundamental approach:
// Costs painting (and anything else that needs "what's the ground height/
// position here") should sample ONE generated heightmap grid that's already
// correctly source-agnostic — the exact same projection Hfield/Collision2
// export already uses (SC2HfieldProjectionService.Project over the merged
// live+imported model, SC2TerrainExporter.BuildPreviewGeometryModel) — not
// re-derive "is this live, imported, or something else" per caller.
//
// This wraps that same projection as a small live-editing cache: rebuilt
// only when something asks for a stale grid (EnsureFresh short-circuits
// when not dirty), analytic ray-marched the same way
// SC2TerrainShell.RaycastTerrainHeight already does for the live-only case
// (that algorithm is reused as-is, just against this grid's Heightmap
// instead of SC2TerrainRuntime).

using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using BdfIO.Formats.Hfield;

public sealed class SC2LiveHeightGrid
{
    private HfieldModel _model;
    private bool _dirty = true;

    // After the terrain triangle budget was
    // raised (SC2TerrainShell.kTargetTriCount, 131_072 -> 524_288) and this
    // grid started actually getting invalidated after every real geometry
    // edit (sculpt-stroke MouseUp, pair-paint MouseUp — see
    // SC2GeometryMode.cs/SC2PaintMode.cs's own matching comments), a real
    // stutter appeared right after releasing the brush: EnsureFresh's full
    // rebuild (BuildPreviewGeometryModel's per-pair mesh build +
    // SC2HfieldProjectionService.Project's whole-grid rasterization) now
    // runs on literally the very next query — almost always the mouse-
    // coordinate HUD, since it queries every frame the cursor moves in the
    // viewport — turning "one flag flip" into "one full expensive rebuild
    // per stroke, synchronously, on the main thread."
    //
    // Debounced the same way SC2TerrainShell.SchedulePreviewBuild already
    // debounces the (equally expensive) Simplified Preview mesh rebuild
    // (kPreviewBuildDelay): MarkDirty pushes a deadline outward instead of
    // demanding an immediate rebuild on the next query, so a rapid burst of
    // strokes collapses into ONE rebuild after painting actually pauses,
    // and the rebuild that does happen lands a moment after mouse-up rather
    // than instantly at the release itself (steadily less perceptible, not
    // a hard freeze tied to letting go of the brush). Every consumer keeps
    // reading the previous (stroke-old, not multi-second-old) model in the
    // meantime — fine for Costs painting/Objects placement/the HUD, none of
    // which need millisecond-fresh data mid-stroke.
    private const float DebounceSeconds = 0.25f;
    private double _rebuildDeadline;

    // The debounce above only delayed/coalesced the freeze, it never
    // reduced its actual cost. Two real, independent fixes stacked on top
    // of it — both calls below use them:
    //
    // 1. BuildPreviewGeometryModel(..., skipSimplification: true) — this
    //    grid only ever needs accurate triangle POSITIONS
    //    (SC2HfieldProjectionService.Project rasterizes positions only,
    //    never reads a vertex's normal), never a game-shippable REDUCED
    //    mesh — but SC2TerrainLayerBuilder.Build was always run with every
    //    pair's real, configured SimplifyRatio, paying for full mesh
    //    decimation + a whole-terrain normal recompute purely to throw
    //    both away moments later. Forcing skipSimplification routes every
    //    pair through Simplify's own ratio<=0 fast path (a plain repack,
    //    no decimation) and skips the normal computation entirely — see
    //    SC2TerrainLayerBuilder.Build's own doc comment on that parameter.
    //    Real callers (the actual export, Collision2 preview) are
    //    untouched — they still need the genuine simplified+shaded result
    //    and don't pass this.
    // 2. What's left (rasterizing the full grid — genuinely still a real
    //    fraction of a second on a large map) is pure C# (confirmed: no
    //    Mesh/GameObject/Component/Texture2D calls, only Mathf and plain
    //    array/POCO work), so — except for the very first build below,
    //    which still blocks once since there's no stale model yet to keep
    //    serving meanwhile — every REFRESH of an already-built model now
    //    runs on a background Task instead, matching this class's own
    //    already-stated tolerance ("every consumer keeps reading the
    //    previous, stroke-old model... fine for Costs painting/Objects
    //    placement/the HUD, none of which need millisecond-fresh data"):
    //    that stale window just moved from "blocking the main thread" to
    //    "silently serving the old model for a bit longer, in the
    //    background." Background-threading pure C# work like this already
    //    has precedent elsewhere in this codebase (SC2UnitPropBrowser's
    //    asset indexing, SC2PaintMode's async GPU readback) — same
    //    reasoning, same non-critical-freshness contract.
    private Task<HfieldModel> _rebuildTask;

    /// <summary>Read-only access to the last-built projection (MapX/MapY,
    /// world-Y*128-encoded Heightmap ushort[] — same packing
    /// SC2HfieldProjectionService.Project always used) — callers that need
    /// the WHOLE grid at once (e.g. SC2HeightmapExporter, exporting the same
    /// canonical CostsGridSize+1 resolution hfield.bdf/Costs/Collision2
    /// already use) rather than one point at a time via SampleHeight. Null
    /// until EnsureFresh has run at least once.</summary>
    public HfieldModel Model => _model;

    /// <summary>Call whenever live or imported geometry might have changed
    /// (terrain edits, import add/remove/undo/redo, ImportOnlyMode toggle,
    /// grid size change) — cheap, just flips a flag and (re)starts the
    /// debounce window; the actual rebuild is deferred to a later
    /// EnsureFresh call, once DebounceSeconds have passed with no further
    /// MarkDirty calls.</summary>
    public void MarkDirty()
    {
        _dirty = true;
        _rebuildDeadline = Time.realtimeSinceStartupAsDouble + DebounceSeconds;
    }

    // Set right before kicking off the background Task in EnsureFresh below,
    // read back when that Task's result is consumed — lets the log line
    // report how long the ACTUAL background computation took, even though
    // it never blocks the caller. See EnsureFresh's own timing-log lines.
    private System.Diagnostics.Stopwatch _rebuildStopwatch;

    // Testing the "background work is real, but its allocation burst
    // triggers a GC pause that freezes the WHOLE process anyway" hypothesis
    // — user confirmed the stutter is still there even though this class's
    // own heavy work no longer runs on the main thread. GC.CollectionCount
    // deltas (esp. gen2, the expensive blocking kind) captured around the
    // background Task tell us whether a collection actually happened
    // during/because of it, independent of which thread triggered it — .NET
    // GC pauses are process-wide, not per-thread, so a gen2 collection
    // caused by this background allocation burst would stall the main
    // thread too despite EnsureFresh itself never blocking.
    private (int Gen0, int Gen1, int Gen2) _gcCountsBeforeRebuild;

    /// <summary>Set inside the background Task.Run below, read back by the
    /// completion log — lets a slow/high-GC background rebuild be told apart
    /// from a fast direct-resample one instead of guessing from wall-clock
    /// time alone (e.g. an imported-terrain map falling back to the slow
    /// TerrainModel path every time, vs. an occasional slow direct resample).</summary>
    private bool _lastRebuildUsedDirectPath;

    /// <summary>The real fix the skipSimplification/background-Task/GC
    /// instrumentation above was all diagnosing toward: for the common case
    /// (no imported terrain.bdf objects to merge, live procedural geometry
    /// actually in play), building a whole TerrainModel (data.Vertices/
    /// Triangles regenerated from scratch, a full triangle soup, one
    /// SC2TerrainBuiltLayer per pair) just to rasterize it BACK into a
    /// heightfield is pure waste — data.Heights already IS a uniform
    /// heightfield, at (GridWidth+1)x(GridHeight+1) resolution, CellSize
    /// world units apart. Resampling it directly at the target CostsGridSize
    /// resolution (same bilinear pattern SampleHeight below already uses,
    /// same "world unit == hfield grid index" convention this project's
    /// Costs/Hfield/decal pipeline already established elsewhere — see e.g.
    /// SC2TerrainExporter's own "world (0,0) to (CostsGridSize,
    /// CostsGridSize)" comment) skips essentially every allocation in the
    /// slow path (data.Vertices/Triangles regen, per-pair triangle buffers,
    /// the TerrainModel/HfieldModel intermediate representations,
    /// FillGaps) — confirmed via this class's own GC.CollectionCount
    /// instrumentation that the slow path's allocation volume, not which
    /// thread it ran on, was the real cause of a still-visible stutter even
    /// after the background-Task fix (10 gen2 collections per rebuild — a
    /// GC pause stalls the WHOLE process, not just the thread that
    /// triggered it, so moving the work off the main thread alone couldn't
    /// fix this).
    ///
    /// Water is deliberately NOT special-cased here: the slow path already
    /// calls SC2HfieldProjectionService.Project without a
    /// bakeWaterElevation, and Project always skips a water-material
    /// layer's own triangles when rasterizing (IsWaterMaterial), so the
    /// synthetic water plane never actually contributed height data to
    /// THIS consumer in the first place — this fast path already produces
    /// the same result as the slow path for a core-only map, not an
    /// approximation of it.
    ///
    /// Returns false (caller falls back to the existing slow, but always-
    /// correct, TerrainModel+Project path) whenever something this
    /// resample can't account for is in play: no data, not initialized,
    /// ImportOnlyMode (the live plane contributes nothing at all — only
    /// imported geometry matters), or any imported terrain.bdf objects to
    /// merge (their geometry isn't in data.Heights at all).</summary>
    private static bool TryBuildDirectFromHeights(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains, out HfieldModel model)
    {
        model = null;
        if (data == null || !data.IsInitialized || data.ImportOnlyMode) return false;
        if (importedTerrains != null && importedTerrains.Count > 0) return false;
        if (data.Heights == null || data.Heights.Length == 0) return false;

        var srcW = data.GridWidth + 1;
        var srcH = data.GridHeight + 1;
        if (data.Heights.Length < srcW * srcH) return false; // stale/mismatched cache — let the slow (self-healing) path handle it
        var cellSize = Mathf.Max(0.0001f, data.CellSize);

        float SampleWorldHeight(float worldX, float worldZ)
        {
            var fx = Mathf.Clamp(worldX / cellSize, 0f, data.GridWidth);
            var fz = Mathf.Clamp(worldZ / cellSize, 0f, data.GridHeight);
            var x0 = Mathf.FloorToInt(fx); var z0 = Mathf.FloorToInt(fz);
            var x1 = Mathf.Min(data.GridWidth, x0 + 1); var z1 = Mathf.Min(data.GridHeight, z0 + 1);
            var tx = fx - x0; var tz = fz - z0;
            float H(int gx, int gz) => data.Heights[gz * srcW + gx];
            return Mathf.Lerp(Mathf.Lerp(H(x0, z0), H(x1, z0), tx), Mathf.Lerp(H(x0, z1), H(x1, z1), tx), tz);
        }

        var mapSize = Mathf.Max(1, data.CostsGridSize) + 1;
        // Same encoding SC2HfieldProjectionService.Project's own Heightmap
        // construction uses (ushort = round(worldY * 128), clamped) — must
        // match exactly, this array is read back the same way regardless
        // of which path built it.
        var heightmap = new ushort[mapSize * mapSize];
        for (var z = 0; z < mapSize; z++)
        for (var x = 0; x < mapSize; x++)
            heightmap[z * mapSize + x] = (ushort)Mathf.Clamp(Mathf.RoundToInt(SampleWorldHeight(x, z) * 128f), 0, 65535);

        // TerrainTypes is never read by this class's own consumers
        // (SampleHeight/TryRaycast only ever touch Heightmap/MapX/MapY) —
        // Array.Empty instead of the slow path's full (mapSize-1)^2
        // DefaultTerrainTypeId fill, one more allocation this fast path
        // doesn't need to pay for.
        model = new HfieldModel
        {
            Version = 2,
            MapX = (uint)mapSize,
            MapY = (uint)mapSize,
            Heightmap = heightmap,
            TerrainTypes = System.Array.Empty<ushort>(),
            TerrainTypesElementSize = 1
        };
        return true;
    }

    /// <param name="getImportedTerrains">Lazy — invoked at most once, only
    /// when a rebuild is actually about to happen (both call sites below sit
    /// behind the early-return checks), and always synchronously on the
    /// CALLING thread before any Task.Run below starts (its own
    /// implementation, SC2GameViewEditorUI.GetImportedTerrainsForExport,
    /// touches Transform — a main-thread-only Unity API — so it must never
    /// run inside the background task itself). Confirmed real cost: every
    /// per-point height query (SC2TerrainShell.SampleLiveHeightGrid) used to
    /// call this eagerly regardless of dirty state, and the real
    /// implementation allocates a fresh List (plus one wrapper object per
    /// imported terrain) every single call — with a decal preview rebuild
    /// alone issuing well over 100k per-point queries, that was one of the
    /// largest hidden GC sources in this whole pipeline.</param>
    public void EnsureFresh(SC2TerrainData data, System.Func<List<SC2ImportedTerrainExportData>> getImportedTerrains)
    {
        // Pick up a background rebuild kicked off by an EARLIER EnsureFresh
        // call, regardless of _dirty/deadline state below — it may have
        // started before this call and only just finished.
        if (_rebuildTask != null && _rebuildTask.IsCompleted)
        {
            if (_rebuildTask.Status == TaskStatus.RanToCompletion)
                _model = _rebuildTask.Result;
            else if (_rebuildTask.IsFaulted)
                Debug.LogException(_rebuildTask.Exception);
            var gcAfter = (System.GC.CollectionCount(0), System.GC.CollectionCount(1), System.GC.CollectionCount(2));
            SC2Log.Log($"[SC2LiveHeightGrid] Background rebuild finished in {_rebuildStopwatch?.ElapsedMilliseconds ?? -1}ms (status={_rebuildTask.Status}, path={(_lastRebuildUsedDirectPath ? "direct resample" : "slow TerrainModel")}) — never blocked the main thread. " +
                $"GC collections during it: gen0={gcAfter.Item1 - _gcCountsBeforeRebuild.Gen0}, gen1={gcAfter.Item2 - _gcCountsBeforeRebuild.Gen1}, gen2={gcAfter.Item3 - _gcCountsBeforeRebuild.Gen2}.");
            _rebuildTask = null;
        }

        if (!_dirty) return;

        if (_model == null)
        {
            // Cold start — no stale model to keep serving in the
            // meantime, so this one still blocks (same as before this
            // fix): a real caller (Costs painting on first use, etc.)
            // needs something now, not on some later frame. Never
            // withheld behind the debounce either, for the same reason.
            _dirty = false;
            if (data == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var importedTerrains = getImportedTerrains?.Invoke();
            var propSolids0 = SC2PropHeightmapGeometry.BuildSolids(data);
            if (TryBuildDirectFromHeights(data, importedTerrains, out var directModel0))
            {
                SC2HfieldProjectionService.MergeSolids(directModel0, propSolids0);
                _model = directModel0;
                SC2Log.Log($"[SC2LiveHeightGrid] Cold-start build (blocking, direct resample) took {sw.ElapsedMilliseconds}ms.");
                return;
            }
            var mapSize0 = Mathf.Max(1, data.CostsGridSize) + 1;
            var geomModel0 = SC2TerrainExporter.BuildPreviewGeometryModel(data, importedTerrains, skipSimplification: true);
            _model = SC2HfieldProjectionService.Project(geomModel0, null, mapSize0, mapSize0, extraSolids: propSolids0);
            SC2Log.Log($"[SC2LiveHeightGrid] Cold-start build (blocking, slow TerrainModel path — imported terrain or ImportOnlyMode in play) took {sw.ElapsedMilliseconds}ms.");
            return;
        }

        if (Time.realtimeSinceStartupAsDouble < _rebuildDeadline) return;
        if (_rebuildTask != null) return; // already rebuilding in the background — stay dirty, the block above will pick up the result on a later query

        _dirty = false;
        if (data == null)
        {
            _model = null;
            return;
        }

        var mapSize = Mathf.Max(1, data.CostsGridSize) + 1;
        // No material exclusion here - Costs painting/auto-generation must
        // see the real height of every material, including anything marked
        // "excluded" for Collision2 specifically (SC2TerrainData.
        // Collision2ExcludedMaterialNames only applies to Collision2's own
        // separate projection, see SC2TerrainExporter.BuildCollision2Model).
        var importedTerrainsForRebuild = getImportedTerrains?.Invoke(); // main thread, before Task.Run — see this method's own doc comment
        var propSolidsForRebuild = SC2PropHeightmapGeometry.BuildSolids(data); // reads the game archives: main thread too
        _rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _gcCountsBeforeRebuild = (System.GC.CollectionCount(0), System.GC.CollectionCount(1), System.GC.CollectionCount(2));
        _rebuildTask = Task.Run(() =>
        {
            var importedTerrains = importedTerrainsForRebuild;
            if (TryBuildDirectFromHeights(data, importedTerrains, out var directModel))
            {
                _lastRebuildUsedDirectPath = true;
                SC2HfieldProjectionService.MergeSolids(directModel, propSolidsForRebuild);
                return directModel;
            }
            _lastRebuildUsedDirectPath = false;
            var geomModel = SC2TerrainExporter.BuildPreviewGeometryModel(data, importedTerrains, skipSimplification: true);
            return SC2HfieldProjectionService.Project(geomModel, null, mapSize, mapSize, extraSolids: propSolidsForRebuild);
        });
    }

    /// <summary>Forces this grid to reflect the CURRENT terrain state right
    /// now, bypassing MarkDirty's normal debounce/background-Task behavior
    /// entirely — for a consumer that needs THIS stroke's real height
    /// immediately, not up to DebounceSeconds later served from whatever
    /// stale _model happened to still be cached.
    ///
    /// Real bug this fixes: SC2DecalsShell.EnsurePreview reads live height
    /// through this grid (SC2TerrainShell.SampleLiveHeightGrid) right after
    /// a completed stroke bumps TerrainGeometryVersion — the SAME frame,
    /// well before DebounceSeconds could possibly have elapsed — so the
    /// normal EnsureFresh above always served the PREVIOUS stroke's _model,
    /// visibly one stroke behind. Cheap enough to call synchronously now
    /// that the common case routes through TryBuildDirectFromHeights (the
    /// same ~100-250ms/low-GC cost EnsureFresh's own background path was
    /// already paying moments later regardless — this just pays it now,
    /// on-demand, instead of a frame late).
    ///
    /// Abandons any in-flight background _rebuildTask rather than letting
    /// it complete normally: that task closed over an OLDER Data.Heights
    /// snapshot, so if EnsureFresh's own "pick up a finished background
    /// rebuild" block later saw it complete, it would silently overwrite
    /// the fresh _model this method just computed with stale data.</summary>
    public void EnsureFreshImmediate(SC2TerrainData data, System.Func<List<SC2ImportedTerrainExportData>> getImportedTerrains)
    {
        if (data == null) return;
        _rebuildTask = null;
        var importedTerrains = getImportedTerrains?.Invoke();
        var propSolids = SC2PropHeightmapGeometry.BuildSolids(data);
        if (!TryBuildDirectFromHeights(data, importedTerrains, out var model))
        {
            var mapSize = Mathf.Max(1, data.CostsGridSize) + 1;
            var geomModel = SC2TerrainExporter.BuildPreviewGeometryModel(data, importedTerrains, skipSimplification: true);
            model = SC2HfieldProjectionService.Project(geomModel, null, mapSize, mapSize, extraSolids: propSolids);
        }
        else SC2HfieldProjectionService.MergeSolids(model, propSolids);
        _model = model;
        _dirty = false;
        _rebuildDeadline = Time.realtimeSinceStartupAsDouble + DebounceSeconds;
    }

    /// <summary>Same analytic ray-march SC2TerrainShell.RaycastTerrainHeight
    /// already uses for the live-only heightfield, generalized to this
    /// grid's Heightmap (world units = grid cells 1:1, matching
    /// SC2HfieldProjectionService/SC2CostsShell's own established
    /// convention — no separate CellSize scaling needed here).</summary>
    public bool TryRaycast(Ray ray, out Vector3 hit)
    {
        hit = default;
        if (_model == null || _model.MapX <= 1 || _model.MapY <= 1)
            return false;

        var maxX = _model.MapX - 1;
        var maxZ = _model.MapY - 1;
        if (!SC2TerrainShell.RaycastXZBoundsPublic(ray, maxX, maxZ, out var enter, out var exit))
            return false;

        if (float.IsNegativeInfinity(enter)) enter = 0f;
        if (float.IsPositiveInfinity(exit)) exit = 10000f;
        enter = Mathf.Max(0f, enter);
        if (exit <= enter) return false;

        const int steps = 256;
        var previousT = enter;
        var previousDelta = RayDelta(ray, previousT);
        if (Mathf.Abs(previousDelta) < 0.001f)
        {
            hit = ray.GetPoint(previousT);
            hit.y = SampleHeight(hit.x, hit.z);
            return true;
        }

        for (var i = 1; i <= steps; i++)
        {
            var t = Mathf.Lerp(enter, exit, i / (float)steps);
            var delta = RayDelta(ray, t);
            if (Mathf.Abs(delta) < 0.001f || Mathf.Sign(delta) != Mathf.Sign(previousDelta))
            {
                var a = previousT;
                var b = t;
                for (var j = 0; j < 10; j++)
                {
                    var mid = (a + b) * 0.5f;
                    var midDelta = RayDelta(ray, mid);
                    if (Mathf.Sign(midDelta) == Mathf.Sign(previousDelta)) a = mid;
                    else b = mid;
                }

                hit = ray.GetPoint((a + b) * 0.5f);
                hit.y = SampleHeight(hit.x, hit.z);
                return true;
            }

            previousT = t;
            previousDelta = delta;
        }

        return false;
    }

    private float RayDelta(Ray ray, float t)
    {
        var p = ray.GetPoint(t);
        return p.y - SampleHeight(p.x, p.z);
    }

    public float SampleHeight(float worldX, float worldZ)
    {
        if (_model == null || _model.Heightmap == null || _model.MapX <= 0 || _model.MapY <= 0)
            return 0f;

        var mapX = (int)_model.MapX;
        var mapY = (int)_model.MapY;
        var gx = Mathf.Clamp(worldX, 0f, mapX - 1);
        var gz = Mathf.Clamp(worldZ, 0f, mapY - 1);
        var x0 = Mathf.FloorToInt(gx);
        var z0 = Mathf.FloorToInt(gz);
        var x1 = Mathf.Min(mapX - 1, x0 + 1);
        var z1 = Mathf.Min(mapY - 1, z0 + 1);
        var tx = gx - x0;
        var tz = gz - z0;

        // Heightmap stores world-Y * 128 as ushort (SC2HfieldProjectionService.
        // Project's own encoding) - divide back down to world units.
        float H(int gxi, int gzi) => _model.Heightmap[gzi * mapX + gxi] / 128f;
        var h00 = H(x0, z0); var h10 = H(x1, z0); var h01 = H(x0, z1); var h11 = H(x1, z1);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
    }
}
