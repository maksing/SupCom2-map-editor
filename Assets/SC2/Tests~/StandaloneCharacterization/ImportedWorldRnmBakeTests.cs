using BdfIO.Formats.Terrain;
using UnityEngine;

namespace SC2CharacterizationTests;

/// <summary>SC2RnmBakeService.BakeImportedWorldRnm's own bounds/Uv2-remap/combine logic — the
/// per-texel lighting itself is already covered by ImportedRnmBakeTests (SC2RnmMaskBaker.
/// BakeMaskGroup, the shared rasterizer that method also calls). BakeImportedWorldRnm is private,
/// so these tests replicate its exact bounds-then-remap step (see that method's own doc comment)
/// and feed the result into the same public BakeMaskGroup, rather than call it directly — this
/// verifies the intended behavior (a decal-lighting-consistency texture keyed by real world
/// position, not an object's own local UV2) without needing SC2RnmBakeService's internals
/// exposed.</summary>
public class ImportedWorldRnmBakeTests
{
    private const int Size = 32;
    private const float Pad = 8f;

    /// <summary>A flat, upward-facing quad at a real WORLD rect, deliberately far from the
    /// origin (so a bug that assumes world position starts at 0 would fail these tests) and with
    /// its Uv2 initially set to something unrelated — the imported object's own per-object
    /// lightmap UV2, which BakeImportedWorldRnm overwrites, never reads.</summary>
    private static TerrainModel WorldQuad(float minX, float minZ, float maxX, float maxZ)
    {
        var model = new TerrainModel { Version = 6 };
        var layer = new TerrainLayer { CumulativeVertices = 0, CumulativeIndices = 0 };
        model.Layers.Add(layer);
        model.Materials.Add(new TerrainMaterial { Name = "imported" });

        TerrainVertex V(float x, float z) => new()
        {
            PosX = x, PosY = 0f, PosZ = z,
            Uv1U = (x - minX) * 0.1f, Uv1V = (z - minZ) * 0.1f, // real UV1 gradient -> a usable tangent
            Uv2U = 0.75f, Uv2V = 0.75f, // the object's own unrelated local UV2 — must be overwritten, never read
            NormalX = 0f, NormalY = 1f, NormalZ = 0f,
        };
        model.Vertices.Add(V(minX, minZ));
        model.Vertices.Add(V(maxX, minZ));
        model.Vertices.Add(V(maxX, maxZ));
        model.Vertices.Add(V(minX, maxZ));
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 1, V2 = 2 });
        model.Triangles.Add(new TerrainTriangle { V0 = 0, V1 = 2, V2 = 3 });
        return model;
    }

    /// <summary>Exactly SC2RnmBakeService.BakeImportedWorldRnm's own bounds computation + Uv2
    /// overwrite, for the one layer index 0 — kept in lockstep with that method by this test's own
    /// header comment; if the production formula changes, update this to match and re-verify the
    /// asserts below still hold.</summary>
    private static void ApplyWorldRemap(TerrainModel model, out float minX, out float minZ, out float spanX, out float spanZ)
    {
        minX = float.MaxValue; minZ = float.MaxValue;
        var maxX = float.MinValue; var maxZ = float.MinValue;
        foreach (var v in model.Vertices)
        {
            if (v.PosX < minX) minX = v.PosX; if (v.PosX > maxX) maxX = v.PosX;
            if (v.PosZ < minZ) minZ = v.PosZ; if (v.PosZ > maxZ) maxZ = v.PosZ;
        }
        minX -= Pad; minZ -= Pad; maxX += Pad; maxZ += Pad;
        spanX = Mathf.Max(1f, maxX - minX);
        spanZ = Mathf.Max(1f, maxZ - minZ);

        for (var i = 0; i < model.Vertices.Count; i++)
        {
            var v = model.Vertices[i];
            v.Uv2U = (v.PosX - minX) / spanX;
            v.Uv2V = (v.PosZ - minZ) / spanZ;
            model.Vertices[i] = v;
        }
    }

    private static SC2RnmMaskBaker.Settings OverheadSun() => new()
    {
        ToSun = Vector3.up, // straight overhead: nothing can occlude it, no heightmap needed
        Hfield = null,
        ShadowSoftRadius = 0,
        ShadowSoftRadiusUnderwater = 0,
        SunColor = Color.white,
        ShadowColor = Color.black,
        SunIntensity = 1f,
    };

    [Fact]
    public void WorldRemap_ComputesThePaddedBoundingBoxOfRealWorldPositions_NotUv2()
    {
        // Deliberately far from the origin and non-square, so a bug that assumes world position
        // starts at 0 or that width==height would fail this.
        var model = WorldQuad(5000f, -3000f, 5200f, -2900f);

        ApplyWorldRemap(model, out var minX, out var minZ, out var spanX, out var spanZ);

        Assert.Equal(5000f - Pad, minX, 3);
        Assert.Equal(-3000f - Pad, minZ, 3);
        Assert.Equal(200f + Pad * 2, spanX, 3);
        Assert.Equal(100f + Pad * 2, spanZ, 3);
    }

    [Fact]
    public void WorldRemap_ThenBakeMaskGroup_CoversExactlyTheTrianglesWorldFootprint()
    {
        var model = WorldQuad(0f, 0f, 64f, 64f);
        ApplyWorldRemap(model, out _, out _, out _, out _);

        var result = SC2RnmMaskBaker.BakeMaskGroup(model, SC2RnmMaskBaker.ComputeVertexTangents(model),
            new[] { 0 }, Size, Size, OverheadSun());

        // The quad plus Pad on every side should cover the great majority of the mask, and
        // definitely its own center.
        var center = (Size / 2) * Size + Size / 2;
        Assert.True(result.Covered[center], "the quad's own world-space center must be covered");
        var coveredCount = 0;
        foreach (var c in result.Covered) if (c) coveredCount++;
        Assert.True(coveredCount > Size * Size / 2, "a padded full-mask quad should cover most texels");
    }

    [Fact]
    public void WorldRemap_TwoDisjointObjects_OnlyCoverTheirOwnFootprints_NotTheGapBetween()
    {
        // Two far-apart quads sharing ONE world bake — the combined bounding box spans the gap
        // between them, but only the two real footprints should end up covered; a decal sitting in
        // the gap must fall through to the next lighting source, not read stale/wrong baked data.
        // Each quad is comfortably larger than one texel (~32 world units/texel at this span and
        // Size) so a sub-texel-sized triangle's own rasterization edge cases can't produce a false
        // negative here.
        var model = WorldQuad(0f, 0f, 100f, 100f);
        var far = WorldQuad(900f, 0f, 1000f, 100f);
        var farLayer = new TerrainLayer { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
        model.Layers.Add(farLayer);
        model.Materials.Add(far.Materials[0]);
        foreach (var v in far.Vertices) model.Vertices.Add(v);
        // Triangle indices are LOCAL to their own layer (BakeMaskGroup itself adds
        // layer.CumulativeVertices — see its own vertBase) — far's indices are already correct
        // as-is, copying them with an extra offset on top would double-count it.
        foreach (var t in far.Triangles) model.Triangles.Add(t);

        ApplyWorldRemap(model, out _, out _, out _, out _);
        var result = SC2RnmMaskBaker.BakeMaskGroup(model, SC2RnmMaskBaker.ComputeVertexTangents(model),
            new[] { 0, 1 }, Size, Size, OverheadSun());

        // The combined padded span is ~1016 world units wide across 32 texels (~32 units/texel) —
        // check a small column range at each end rather than one exact index, since the exact texel
        // boundary a ~100-unit-wide quad lands on isn't worth hand-deriving precisely; the middle
        // half of the mask is pure gap, with no real geometry anywhere near it.
        var row = Size / 2; // both objects span the same Z range (0..100), any row in it works
        var nearCovered = false; var farCovered = false;
        for (var x = 0; x < 4; x++) if (result.Covered[row * Size + x]) nearCovered = true;
        for (var x = Size - 4; x < Size; x++) if (result.Covered[row * Size + x]) farCovered = true;
        Assert.True(nearCovered, "the near object's own footprint must be covered");
        Assert.True(farCovered, "the far object's own footprint must be covered");
        for (var x = Size / 4; x < Size * 3 / 4; x++)
            Assert.False(result.Covered[row * Size + x], $"gap column {x} must NOT read as covered");
    }

    /// <summary>Independently reproduces SC2RnmBakeService.BakeImportedWorldRnm's own per-texel RGB
    /// combine step (NOT calling it — it's private, see this file's own header comment) from the
    /// SAME raw basis bytes SC2TerrainView.shader's live-terrain preview and SC2Decal.shader's own
    /// imported-lighting fallback both read at runtime — this is the actual formula under test, in
    /// its own right, not a copy compared against itself.</summary>
    private static byte CombineAndContrastStretch(byte m0, byte m1, byte m2)
    {
        var combined = (m0 / 255f + m1 / 255f + m2 / 255f) * 0.5774f;
        combined = Mathf.Clamp01((combined - 0.5f) * 2.2f + 0.5f);
        return (byte)Mathf.RoundToInt(combined * 255f);
    }

    [Fact]
    public void CombinedRgb_OnAFullySunlitUnshadowedFlatSurface_IsBright()
    {
        var model = WorldQuad(0f, 0f, 64f, 64f);
        ApplyWorldRemap(model, out _, out _, out _, out _);
        var result = SC2RnmMaskBaker.BakeMaskGroup(model, SC2RnmMaskBaker.ComputeVertexTangents(model),
            new[] { 0 }, Size, Size, OverheadSun());

        var i = (Size / 2) * Size + Size / 2;
        Assert.Equal(255, result.ShadowAlpha[i]); // unshadowed, matching ImportedRnmBakeTests' own equivalent assert
        for (var c = 0; c < 3; c++)
        {
            var combined = CombineAndContrastStretch(result.Rgb0[i * 3 + c], result.Rgb1[i * 3 + c], result.Rgb2[i * 3 + c]);
            // Loose bound, same spirit as ImportedRnmBakeTests' own ">40" raw-basis assert — the
            // exact byte depends on FinalizeLighting's own calibration, not hand-derivable here;
            // clearly on the bright side of mid-grey (128) is what this is actually checking.
            Assert.True(combined > 140, $"channel {c}: an unshadowed, directly sun-lit surface should combine bright, was {combined}");
        }
    }

    [Fact]
    public void CombineAndContrastStretch_IsMonotonicWithTheRawBasisValues()
    {
        // The contrast stretch must not invert brighter-vs-darker ordering — a texel with more
        // raw basis light must never combine to a DARKER result than a dimmer one.
        var dim = CombineAndContrastStretch(80, 80, 80);
        var bright = CombineAndContrastStretch(200, 200, 200);
        Assert.True(bright > dim);
    }
}
