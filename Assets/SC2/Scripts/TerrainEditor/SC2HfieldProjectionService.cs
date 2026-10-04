using System.Linq;
using UnityEngine;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;

/// <summary>
/// Single, shared hfield rasterizer for BdfIO.Formats.Terrain.TerrainModel.
///
/// This is the barycentric triangle-rasterization algorithm originally written
/// (and verified against real gameplay — units no longer fall through the
/// terrain) for TerrainMeshEditorWindow.RegenerateHfield, extracted so both
/// that window and SC2TerrainExporter (the procedural SC2TerrainData export
/// path, which previously never produced a hfield.bdf at all) share exactly
/// one implementation instead of two copies that could drift apart.
///
/// Critical invariant this exists to preserve: the projection MUST run against
/// the model that is actually about to be written to terrain.bdf — i.e. AFTER
/// per-pair simplification (SC2TerrainLayerBuilder.Build) and AFTER any
/// imported terrain.bdf objects have been merged in (SC2TerrainExporter.
/// MergeImportedTerrain) — never against the raw, pre-simplification editor
/// mesh. A hfield built from a different (denser/differently-shaped) mesh
/// than the one actually shipped in terrain.bdf would desync from the visible
/// surface exactly the way the original "units fall through the map" bug did.
/// </summary>
public static class SC2HfieldProjectionService
{
    // Vanilla SC2 maps use a 1024x1024 world-unit playable grid, hfield grid
    // is (mapX-1)x(mapY-1) cells = 1024x1024, i.e. 1025x1025 height points.
    // SC2TerrainExporter's UV2 normalization (pos.x/1024f) assumes the same
    // world-space convention, so this is not an independent guess — it must
    // match whatever the terrain mesh itself was authored/imported against.
    public const int DefaultMapX = 1025;
    public const int DefaultMapY = 1025;

    /// <summary>
    /// Rasterizes <paramref name="model"/>'s geometry into a HfieldModel.
    /// Water-shaded layers are excluded from the height projection (a water
    /// plane is a decorative render-only surface, not solid ground — including
    /// it would let a large flat water plane overwrite real terrain height
    /// wherever it's drawn after the real ground layers).
    /// </summary>
    /// <param name="model">The FINAL terrain model — after simplification and
    /// after merging any imported terrain objects — exactly as it is about to
    /// be serialized to terrain.bdf.</param>
    /// <param name="existingTerrainTypes">TerrainTypes array to pass through
    /// ((mapX-1)*(mapY-1) entries), e.g. from an existing/original hfield.bdf.
    /// Pass null (or a mismatched length) for a from-scratch map with no prior
    /// terrainTypes data — an all-zero array is used instead.</param>
    /// <param name="excludedMaterialNames">Material names to skip, same
    /// reasoning as the water exclusion but user-marked instead of
    /// name-pattern-detected — for decorative embedded geometry (rock/
    /// crystal clusters, etc. baked into an imported terrain.bdf as their
    /// own material) that isn't real walkable ground. Without this, such
    /// geometry gets rasterized into the heightfield like any other
    /// terrain — its complex, spiky shape then collapses to an essentially
    /// arbitrary last-triangle-wins height per grid cell (RasterizeTriangleHeight
    /// overwrites, it doesn't take a max), AND — for Collision2's TIN mesh
    /// specifically — the sharp local height variation eats a
    /// disproportionate share of the global 2500-triangle budget refining
    /// that one small area, starving the rest of the map. In practice only
    /// SC2TerrainExporter.BuildCollision2Model passes this (via
    /// SC2TerrainData.Collision2ExcludedMaterialNames, authored per-material
    /// in the Imported Terrain panel) — the shared hfield.bdf/Costs/RNM-bake
    /// projections deliberately call Project without it, since excluding a
    /// material from Collision2's own mesh must not also remove it from
    /// hfield.bdf or Costs.</param>
    /// <param name="bakeWaterElevation">Collision2-only: when set, floods
    /// every cell that has no real ground data at all (a true hole under
    /// excluded water geometry, e.g. an imported ocean with no separate
    /// seafloor mesh) or whose real ground height is below this elevation
    /// (a sculpted lake bed) up to this world-Y value, BEFORE FillGaps runs
    /// - not as a later patch over its output. This matters because FillGaps
    /// was written for small rasterization gaps (a few missed pixels at a
    /// triangle edge), not for entire material-excluded regions: its single
    /// left-to-right sweep can propagate one boundary cell's value across an
    /// ENTIRE hole in one pass (each newly-filled cell becomes an available
    /// neighbour for the very next cell in the same pass), so a large lake
    /// whose low-index edge happens to touch dry shore would otherwise get
    /// that shore's height smeared across the whole lake before any
    /// height-based check could catch it. Baking water first means FillGaps
    /// only ever sees genuinely small gaps again, exactly what it was
    /// designed for. Left null (the default) for every other caller
    /// (hfield.bdf/Costs/RNM-bake) - those must keep seeing every material's
    /// real, unflooded height, completely unchanged from today.</param>
    public static HfieldModel Project(
        TerrainModel model,
        ushort[] existingTerrainTypes = null,
        int mapX = DefaultMapX,
        int mapY = DefaultMapY,
        System.Collections.Generic.ICollection<string> excludedMaterialNames = null,
        float? bakeWaterElevation = null)
    {
        var heights = new float[mapX * mapY];
        var written = new bool[mapX * mapY];
        RasterizeModel(model, heights, written, mapX, mapY, excludedMaterialNames);
        return Finish(heights, written, existingTerrainTypes, mapX, mapY, bakeWaterElevation);
    }

    /// <summary>The topmost surface of several models at once (a cell takes the highest of them), with the same water exclusion
    /// as <see cref="Project"/>, as a heightmap of what can block the sun for the lighting bake. Only cracks a few cells wide
    /// are filled (<see cref="FillSmallGaps"/>); a cell no model covers stays at height 0, i.e. no occluder, where
    /// <see cref="Project"/> would spread the nearest surface across the whole hole - for a set of casters that does not
    /// cover the receiver (a frame around an empty middle) that painted a tall false wall over it.</summary>
    public static HfieldModel ProjectMany(System.Collections.Generic.IEnumerable<TerrainModel> models, int mapX, int mapY)
    {
        var heights = new float[mapX * mapY];
        var written = new bool[mapX * mapY];
        foreach (var model in models) RasterizeModel(model, heights, written, mapX, mapY, null);
        FillSmallGaps(heights, written, mapX, mapY, 2);
        return new HfieldModel
        {
            Version = 2,
            MapX = (uint)mapX,
            MapY = (uint)mapY,
            Heightmap = heights.Select(h => (ushort)Mathf.Clamp(Mathf.RoundToInt(h * 128f), 0, 65535)).ToArray(),
            TerrainTypes = System.Array.Empty<ushort>(),
            TerrainTypesElementSize = 1
        };
    }

    /// <summary>Fills a cell that no triangle covered with the average of its covered neighbours, <paramref name="passes"/>
    /// times, each pass reading only what was covered before it started - so a hole grows by one cell per pass and a crack of
    /// up to twice <paramref name="passes"/> cells closes, but a larger hole keeps its middle.</summary>
    private static void FillSmallGaps(float[] h, bool[] written, int w, int sz, int passes)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            var before = (bool[])written.Clone();
            var anyFilled = false;
            for (var y = 0; y < sz; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (before[i]) continue;
                float sum = 0;
                var n = 0;
                if (x > 0 && before[i - 1]) { sum += h[i - 1]; n++; }
                if (x < w - 1 && before[i + 1]) { sum += h[i + 1]; n++; }
                if (y > 0 && before[i - w]) { sum += h[i - w]; n++; }
                if (y < sz - 1 && before[i + w]) { sum += h[i + w]; n++; }
                if (n == 0) continue;
                h[i] = sum / n;
                written[i] = true;
                anyFilled = true;
            }
            if (!anyFilled) break;
        }
    }

    private static void RasterizeModel(TerrainModel model, float[] heights, bool[] written, int mapX, int mapY,
        System.Collections.Generic.ICollection<string> excludedMaterialNames)
    {
        for (var li = 0; li < model.Layers.Count; li++)
        {
            var layer = model.Layers[li];
            var mat = li < model.Materials.Count ? model.Materials[li] : null;
            if (IsWaterMaterial(mat))
                continue;
            if (excludedMaterialNames != null && excludedMaterialNames.Count > 0 && mat != null && !string.IsNullOrEmpty(mat.Name) && excludedMaterialNames.Contains(mat.Name))
                continue;

            var vertBase = layer.CumulativeVertices;
            var triStart = layer.CumulativeIndices / 3;
            var triEnd = li + 1 < model.Layers.Count
                ? model.Layers[li + 1].CumulativeIndices / 3
                : model.Triangles.Count;

            for (var t = triStart; t < triEnd; t++)
            {
                var tri = model.Triangles[t];
                var v0 = model.Vertices[vertBase + (int)tri.V0];
                var v1 = model.Vertices[vertBase + (int)tri.V1];
                var v2 = model.Vertices[vertBase + (int)tri.V2];
                RasterizeTriangleHeight(v0, v1, v2, heights, written, mapX, mapY);
            }
        }
    }

    private static HfieldModel Finish(float[] heights, bool[] written, ushort[] existingTerrainTypes, int mapX, int mapY,
        float? bakeWaterElevation)
    {
        if (bakeWaterElevation.HasValue)
        {
            var we = bakeWaterElevation.Value;
            for (var i = 0; i < heights.Length; i++)
            {
                if (!written[i] || heights[i] < we)
                {
                    heights[i] = we;
                    written[i] = true;
                }
            }
        }

        FillGaps(heights, written, mapX, mapY);

        var terrainTypesLength = (mapX - 1) * (mapY - 1);
        ushort[] terrainTypes;
        if (existingTerrainTypes != null && existingTerrainTypes.Length == terrainTypesLength)
        {
            terrainTypes = existingTerrainTypes;
        }
        else
        {
            // 0 isn't a real/known TerrainTypes id — a from-scratch grid with
            // nothing painted must default to SC2TerrainData.DefaultTerrainTypeId
            // ("Default", verified vanilla value 2), not an all-zero array.
            terrainTypes = new ushort[terrainTypesLength];
            for (var i = 0; i < terrainTypes.Length; i++) terrainTypes[i] = SC2TerrainData.DefaultTerrainTypeId;
        }

        return new HfieldModel
        {
            Version = 2,
            MapX = (uint)mapX,
            MapY = (uint)mapY,
            Heightmap = heights.Select(h => (ushort)Mathf.Clamp(Mathf.RoundToInt(h * 128f), 0, 65535)).ToArray(),
            TerrainTypes = terrainTypes,
            // Verified vanilla hfield.bdf stores TerrainTypes as 8-bit (only
            // ids 2/"Default" and 3/"Chasm_Bottom" occur, both fit a byte) —
            // HfieldModel.TerrainTypesElementSize defaults to 2 (legacy
            // u16), which would serialize this as 16-bit and not match the
            // real format. HfieldBdf.Serialize still double-checks every
            // value actually fits a byte before honoring this.
            TerrainTypesElementSize = 1
        };
    }

    /// <summary>
    /// Same water heuristic as the original TerrainMeshEditorWindow code:
    /// a material is treated as water if its shader file or name mentions
    /// "water" — the decorative water plane material is never solid ground.
    /// </summary>
    public static bool IsWaterMaterial(TerrainMaterial mat) =>
        mat != null && (
            (mat.ShaderFile?.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
            (mat.Name?.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

    /// <summary>
    /// Rasterizes one triangle's XZ footprint into the height grid via
    /// barycentric interpolation — gives an exact match to the rendered mesh
    /// surface, unlike rounding each vertex to its nearest grid point (which
    /// is what let units fall through the terrain between grid points after
    /// any non-integer Transform).
    ///
    /// When two triangles (same layer or different layers -
    /// e.g. a bridge deck with a river/ground mesh directly underneath it)
    /// cover the same grid cell, this used to unconditionally overwrite
    /// heights[idx] with whichever triangle got rasterized LAST (purely an
    /// artifact of model.Layers/Triangles iteration order, not anything
    /// about the geometry itself) - so the lower of two overlapping meshes
    /// could silently win over the higher one, or vice versa, depending on
    /// unrelated ordering. A heightfield has exactly one Y per XZ cell by
    /// construction, and for gameplay purposes (unit pathing height, camera
    /// collision) that one value must be the TOPMOST real surface - what a
    /// unit standing there would actually be on top of - never an arbitrary
    /// pick between overlapping meshes. Fixed by only overwriting a cell
    /// already written by an earlier triangle if the new one is HIGHER.
    /// </summary>
    private static void RasterizeTriangleHeight(
        TerrainVertex v0, TerrainVertex v1, TerrainVertex v2,
        float[] heights, bool[] written, int mapW, int mapH)
    {
        var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(v0.PosX, Mathf.Min(v1.PosX, v2.PosX))));
        var maxX = Mathf.Min(mapW - 1, Mathf.CeilToInt(Mathf.Max(v0.PosX, Mathf.Max(v1.PosX, v2.PosX))));
        var minZ = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(v0.PosZ, Mathf.Min(v1.PosZ, v2.PosZ))));
        var maxZ = Mathf.Min(mapH - 1, Mathf.CeilToInt(Mathf.Max(v0.PosZ, Mathf.Max(v1.PosZ, v2.PosZ))));
        if (minX > maxX || minZ > maxZ) return;

        var denom = (v1.PosZ - v2.PosZ) * (v0.PosX - v2.PosX) + (v2.PosX - v1.PosX) * (v0.PosZ - v2.PosZ);
        if (Mathf.Abs(denom) < 1e-8f) return; // degenerate triangle (zero area in XZ)

        for (var gz = minZ; gz <= maxZ; gz++)
        for (var gx = minX; gx <= maxX; gx++)
        {
            var w0 = ((v1.PosZ - v2.PosZ) * (gx - v2.PosX) + (v2.PosX - v1.PosX) * (gz - v2.PosZ)) / denom;
            var w1 = ((v2.PosZ - v0.PosZ) * (gx - v2.PosX) + (v0.PosX - v2.PosX) * (gz - v2.PosZ)) / denom;
            var w2 = 1f - w0 - w1;

            const float eps = 1e-4f;
            if (w0 < -eps || w1 < -eps || w2 < -eps) continue;

            var height = w0 * v0.PosY + w1 * v1.PosY + w2 * v2.PosY;
            var idx = gz * mapW + gx;
            if (written[idx] && heights[idx] >= height) continue; // a higher surface already covers this cell - keep it
            heights[idx] = height;
            written[idx] = true;
        }
    }

    /// <summary>
    /// Nearest-neighbour fill for grid points no triangle covered (geometry
    /// holes, or map edges outside the terrain mesh's footprint).
    /// </summary>
    private static void FillGaps(float[] h, bool[] written, int w, int sz)
    {
        for (var pass = 0; pass < 5; pass++)
        {
            var anyFilled = false;
            for (var y = 0; y < sz; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (written[i]) continue;
                float sum = 0;
                var n = 0;
                foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= sz) continue;
                    var j = ny * w + nx;
                    if (written[j]) { sum += h[j]; n++; }
                }
                if (n > 0) { h[i] = sum / n; written[i] = true; anyFilled = true; }
            }
            if (!anyFilled) break;
        }
    }
}
