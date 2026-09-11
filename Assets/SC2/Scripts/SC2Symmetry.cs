// SC2Symmetry.cs
// Shared symmetry tools: X/Z mirroring plus N-way rotational
// symmetry. Used by the costs/terrainTypes painter, and for Props/Markers/Graphs.
//
// Coordinates are an integer grid (0..size-1)^2; the map center defaults to
// (size/2, size/2) (a 1024x1024 grid has center (512,512)).

using System.Collections.Generic;
using UnityEngine;

public enum SC2SymmetryMode
{
    None,
    MirrorX,
    MirrorZ,
    MirrorXZ,
    Rotational,
}

public static class SC2Symmetry
{
    /// <summary>
    /// Returns every symmetric copy of <paramref name="cell"/>
    /// (including the cell itself, as the first element) for the given mode and center.
    /// Duplicates (e.g. a cell sitting on the symmetry axis) are NOT removed — the calling
    /// code must ignore a repeat write to the same cell itself if that
    /// matters (for painting, it doesn't — rewriting the same value is harmless).
    /// <paramref name="rotationalCount"/> only matters when mode==Rotational
    /// — 2..8 inclusive, including odd counts, clamped internally.
    /// </summary>
    public static IEnumerable<Vector2Int> GetSymmetricCells(Vector2Int cell, Vector2 center, int gridSize, SC2SymmetryMode mode, int rotationalCount = 4)
    {
        yield return cell;

        switch (mode)
        {
            case SC2SymmetryMode.None:
                break;

            case SC2SymmetryMode.MirrorX:
                yield return MirrorX(cell, center, gridSize);
                break;

            case SC2SymmetryMode.MirrorZ:
                yield return MirrorZ(cell, center, gridSize);
                break;

            case SC2SymmetryMode.MirrorXZ:
                yield return MirrorX(cell, center, gridSize);
                yield return MirrorZ(cell, center, gridSize);
                yield return MirrorZ(MirrorX(cell, center, gridSize), center, gridSize);
                break;

            case SC2SymmetryMode.Rotational:
                // SC2RadialSymmetryMath (Scripts/Core/SC2TerrainMaterialModel.cs)
                // is the shared source of truth for N-way rotational angles —
                // also used by Terrain (SC2TerrainData.GetSymmetricPositions)
                // and Objects (SC2ObjectsShell.InferCanonicalFromSlot) — so all
                // three domains always agree on what "N-way" means. It lives in
                // Scripts/Core rather than here because SC2TerrainData.cs (part
                // of SC2.Core.asmdef) can't reference this file (no asmdef,
                // implicit default assembly), only the reverse.
                foreach (var deg in SC2RadialSymmetryMath.RotationAngleSteps(rotationalCount))
                    yield return Rotate(cell, center, gridSize, deg);
                break;
        }
    }

    private static Vector2Int MirrorX(Vector2Int cell, Vector2 center, int gridSize) =>
        Clamp(new Vector2Int(Mathf.RoundToInt(2 * center.x - cell.x) - 1, cell.y), gridSize);

    private static Vector2Int MirrorZ(Vector2Int cell, Vector2 center, int gridSize) =>
        Clamp(new Vector2Int(cell.x, Mathf.RoundToInt(2 * center.y - cell.y) - 1), gridSize);

    private static Vector2Int Rotate(Vector2Int cell, Vector2 center, int gridSize, float degrees)
    {
        var rad = degrees * Mathf.Deg2Rad;
        var dx = cell.x + 0.5f - center.x;
        var dy = cell.y + 0.5f - center.y;
        var cos = Mathf.Cos(rad);
        var sin = Mathf.Sin(rad);
        var rx = dx * cos - dy * sin;
        var ry = dx * sin + dy * cos;
        return Clamp(new Vector2Int(Mathf.FloorToInt(center.x + rx), Mathf.FloorToInt(center.y + ry)), gridSize);
    }

    private static Vector2Int Clamp(Vector2Int v, int gridSize) => new(
        Mathf.Clamp(v.x, 0, gridSize - 1),
        Mathf.Clamp(v.y, 0, gridSize - 1));
}
