using System.IO;
using UnityEngine;

/// <summary>
/// Debug-only PNG dump of exactly what gets serialized into costs.bdf — for
/// visually verifying the navmesh/islandmap/terrainTypes grids actually match
/// what was painted, without needing a hex viewer. Lives in Scripts/ (not
/// Editor/) so it compiles and can run from both the Editor-only costs
/// painter (SC2CostsTerrainTypesEditor, Mode A) and the procedural export
/// path (SC2TerrainExporter, Mode B — works in a standalone Build &amp; Run
/// player too, via plain Texture2D.EncodeToPNG + File.WriteAllBytes, neither
/// of which are Editor-only APIs).
/// </summary>
public static class SC2CostsDebugImageWriter
{
    /// <summary>
    /// Writes one grayscale PNG per costs block, encoding the raw navmesh
    /// byte value (1=passable, 5=border, 255=impassable) directly as
    /// intensity — impassable areas show up bright white, borders a faint
    /// grey, passable areas near-black.
    /// </summary>
    public static void WriteNavmeshPngs(System.Collections.Generic.IReadOnlyList<(string name, byte[] navmesh)> blocks, int gridW, int gridH, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        for (var i = 0; i < blocks.Count; i++)
        {
            var (name, navmesh) = blocks[i];
            if (navmesh == null || navmesh.Length != gridW * gridH) continue;
            WriteGrayscale(navmesh, gridW, gridH, Path.Combine(outputDir, $"navmesh_{i}_{SafeName(name)}.png"));
        }
    }

    /// <summary>
    /// Writes one grayscale PNG per costs block's islandmap — ONE BYTE PER
    /// CELL, raw 8-bit value, exactly matching CostsBdf.cs's verified format
    /// comment on CostsBlock.Islandmap ("8-bit grayscale, size =
    /// navmesh.Length = islandmap.Length"). 255 = impassable — the SAME
    /// sentinel navmesh uses, so it already shows up white here with no
    /// remapping needed, directly comparable to the navmesh debug PNG. 0..254
    /// = distinct island indices (dark grays, since they're small numbers —
    /// expected, not a bug).
    ///
    /// This used to expect a uint32-per-cell RGBA color array (4x this byte
    /// size) because SC2IslandMap.Compute used to return exactly that, and
    /// an intermediate version used 0 for impassable instead of 255 (wrong
    /// per the real format) — see SC2IslandMap.cs's own doc comment for the
    /// history. Neither was this writer's bug; it always just faithfully
    /// dumped whatever it was given.
    /// </summary>
    public static void WriteIslandmapPngs(System.Collections.Generic.IReadOnlyList<(string name, byte[] islandmap)> blocks, int gridW, int gridH, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var cellCount = gridW * gridH;
        for (var i = 0; i < blocks.Count; i++)
        {
            var (name, islandmap) = blocks[i];
            if (islandmap == null || islandmap.Length != cellCount) continue;
            WriteGrayscale(islandmap, gridW, gridH, Path.Combine(outputDir, $"islandmap_{i}_{SafeName(name)}.png"));
        }
    }

    /// <summary>
    /// Writes one grayscale PNG for the TerrainTypes grid — the raw id value
    /// itself as intensity (e.g. 2 or 3), exactly matching hfield.bdf's real
    /// 8-bit-per-cell format, the same convention WriteNavmeshPngs/
    /// WriteIslandmapPngs already use. This is deliberately NOT colorized:
    /// a colorized/palette-based dump doesn't match what's actually in the
    /// file and can't be diffed against a hex view of the real bytes. IDs
    /// are small (2, 3, ...) so this looks near-black — that's correct, not
    /// a bug, same as islandmap's small island indices.
    /// </summary>
    public static void WriteTerrainTypesPng(ushort[] terrainTypes, int gridW, int gridH, string outputDir)
    {
        if (terrainTypes == null || terrainTypes.Length != gridW * gridH) return;
        Directory.CreateDirectory(outputDir);
        var bytes = new byte[terrainTypes.Length];
        for (var i = 0; i < terrainTypes.Length; i++)
            bytes[i] = (byte)terrainTypes[i];
        WriteGrayscale(bytes, gridW, gridH, Path.Combine(outputDir, "terraintypes.png"));
    }

    private static void WriteGrayscale(byte[] values, int w, int h, string path)
    {
        var pixels = new Color32[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            pixels[i] = new Color32(v, v, v, 255);
        }
        WritePng(pixels, w, h, path);
    }

    private static void WritePng(Color32[] pixels, int w, int h, string path)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
        tex.SetPixels32(pixels);
        tex.Apply(false);
        var png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        File.WriteAllBytes(path, png);
    }

    private static string SafeName(string s) =>
        string.IsNullOrEmpty(s) ? "block" : System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Za-z0-9_\-]", "_");
}
