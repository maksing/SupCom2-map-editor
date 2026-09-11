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

    public void EnsureFresh(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains)
    {
        if (!_dirty) return;
        // Never withhold the very FIRST build behind the debounce — with no
        // model yet, there's no "recent enough" fallback to keep serving,
        // and a real caller (Costs painting on first use, etc.) needs
        // something now, not in 250ms.
        if (_model != null && Time.realtimeSinceStartupAsDouble < _rebuildDeadline) return;
        _dirty = false;

        if (data == null)
        {
            _model = null;
            return;
        }

        var geomModel = SC2TerrainExporter.BuildPreviewGeometryModel(data, importedTerrains);
        var mapSize = Mathf.Max(1, data.CostsGridSize) + 1;
        // No material exclusion here - Costs painting/auto-generation must
        // see the real height of every material, including anything marked
        // "excluded" for Collision2 specifically (SC2TerrainData.
        // Collision2ExcludedMaterialNames only applies to Collision2's own
        // separate projection, see SC2TerrainExporter.BuildCollision2Model).
        _model = SC2HfieldProjectionService.Project(geomModel, null, mapSize, mapSize);
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
