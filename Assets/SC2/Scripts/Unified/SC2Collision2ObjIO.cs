// SC2Collision2ObjIO.cs
// Minimal Wavefront .obj read/write for the Collision2 preview mesh — lets
// the user round-trip a generated collision2 mesh through Blender for
// manual correction (smoothing/fixing bad triangles) and bring the result
// back in. Positions only (collision2.bdf itself has no UV/normal data —
// see Collision2Model's own doc comment), 1-indexed faces per the .obj spec.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BdfIO.Formats.Terrain;

public static class SC2Collision2ObjIO
{
    public static void Write(string path, IReadOnlyList<(float X, float Y, float Z)> vertices, IReadOnlyList<TerrainTriangle> triangles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# SC2 Collision2 mesh export");
        foreach (var (x, y, z) in vertices)
            sb.AppendLine($"v {F(x)} {F(y)} {F(z)}");
        foreach (var t in triangles)
            sb.AppendLine($"f {t.V0 + 1} {t.V1 + 1} {t.V2 + 1}");
        File.WriteAllText(path, sb.ToString());
    }

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses vertices + triangulated faces from a plain-text
    /// Wavefront .obj. Tolerates the common face-vertex forms
    /// (v / v/vt / v/vt/vn / v//vn) and fan-triangulates any face with more
    /// than 3 vertices (Blender defaults to quads unless the user ticks
    /// "Triangulate Faces" on export).</summary>
    public static (List<(float X, float Y, float Z)> Vertices, List<TerrainTriangle> Triangles) Read(string path)
    {
        var vertices = new List<(float, float, float)>();
        var triangles = new List<TerrainTriangle>();
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts[0] == "v" && parts.Length >= 4)
            {
                vertices.Add((
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    float.Parse(parts[3], CultureInfo.InvariantCulture)));
            }
            else if (parts[0] == "f" && parts.Length >= 4)
            {
                var indices = new int[parts.Length - 1];
                for (var i = 1; i < parts.Length; i++)
                {
                    var token = parts[i].Split('/')[0]; // vertex index always comes first in v, v/vt, v/vt/vn, v//vn
                    var idx = int.Parse(token, CultureInfo.InvariantCulture);
                    // OBJ is 1-indexed; a negative index is relative-to-end (rare, but valid per spec).
                    indices[i - 1] = idx > 0 ? idx - 1 : vertices.Count + idx;
                }
                for (var i = 1; i < indices.Length - 1; i++) // fan-triangulate n-gons around the first vertex
                    triangles.Add(new TerrainTriangle { V0 = (uint)indices[0], V1 = (uint)indices[i], V2 = (uint)indices[i + 1] });
            }
        }
        return (vertices, triangles);
    }
}
