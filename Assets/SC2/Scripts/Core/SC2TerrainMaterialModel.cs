using System;
using System.Collections.Generic;
using UnityEngine;

// Split out of SC2TerrainData.cs: these three types have zero dependency on
// SC2TerrainData's own ScriptableObject machinery (SC2HfieldProjectionService,
// SC2Collision2MeshBuilder, SC2CostsAutoGen, SC2MapRawData, etc. — the exact
// dependency tree the standalone characterization project deliberately can't
// compile against), so
// keeping them in a separate file lets SC2MapDocument's SC2TerrainLegacyBridge
// (Scripts/MapDocument/Domain/Terrain/) be exercised by real, executed
// standalone tests instead of only Unity EditMode tests that can't actually
// run in this environment. Pure move — no behavior change.

/// <summary>One costs.bdf movement-cost layer, painted live over the procedural
/// terrain (see SC2CostsShell) — 1=passable, 5=border (auto), 255=impassable,
/// same encoding SC2CostsAutoGen/CostsPainterWindow already use for the
/// existing-map workflow's costs.bdf. Moved here for the same
/// standalone-compilability reason as SC2Material/SC2MaterialPair below —
/// needed by SC2CostsLegacyBridge (Scripts/MapDocument/Domain/Costs/).</summary>
[Serializable]
public class SC2CostsBlockData
{
    public string Name = "";
    public byte[] Navmesh = Array.Empty<byte>();
}

[Serializable] public class SC2Material
{
    // Stable identity independent of this entry's position in
    // SC2TerrainData.Materials. Empty on any asset
    // saved before this field existed; SC2TerrainData.EnsureStableIds() below
    // lazily assigns a GUID the first time such an asset is touched, so old
    // saves keep loading with no migration step required.
    public string Id="";
    public string Name="Material"; public Texture2D Diffuse,Normal,Mask; public Color Color=Color.white;

    // The actual relative path written into the exported terrain.bdf for
    // each slot should be visible AND directly
    // editable, not just derived silently from whichever Texture2D happens
    // to be assigned. Empty = no override, fall back to the existing
    // Texture-based resolution (SC2TerrainMaterialFactory.TryGetResourcePath)
    // exactly as before. Non-empty wins at export time regardless of
    // whether that path actually resolved to a loadable texture locally —
    // the point is being able to point at a real in-game path (e.g. a
    // vanilla texture not extracted into this project) that the editor
    // can't preview but should still export byte-correctly.
    public string DiffuseOverridePath="", NormalOverridePath="", MaskOverridePath="";
}

[Serializable] public class SC2MaterialPair
{
    // Same reasoning as SC2Material.Id: this is what SC2TrianglePairMap's
    // per-triangle slots reference (Scripts/MapDocument/Domain/Terrain/), not
    // this pair's index in SC2TerrainData.Pairs — the audit's confirmed
    // "changing a color already caused cross-pair reset bugs" finding traces
    // to that index being reused as identity elsewhere.
    public string Id="";
    public string Name="Pair";
    // Mat1Index/Mat2Index remain the authoritative fields every existing
    // reader (preview material building, BDF export) resolves against —
    // unchanged on purpose, so this fix touches zero downstream consumers.
    // Mat1Id/Mat2Id are the STABLE link a picker/delete operation should
    // read and write instead of doing index arithmetic directly: deleting a
    // material now recomputes Mat1Index/Mat2Index for every pair by looking
    // up where Mat1Id/Mat2Id currently live in Materials (SC2MaterialsMode.
    // DrawMaterialBlock's delete handler), instead of manually decrementing
    // "every index greater than i" — the exact index-shift arithmetic the
    // audit's confirmed cross-pair reference-reset bug traces to. Empty on
    // any pair saved before this existed; SC2TerrainData.EnsureStableIds()
    // backfills both from the current Mat1Index/Mat2Index the first time
    // such a pair is touched.
    public string Mat1Id="", Mat2Id="";
    public int   Mat1Index=-1, Mat2Index=-1;
    public Color PairColor=Color.white;
    public string RnmMaskName="mask01"; // mask name (without the _0/_1/_2.win.dds suffix)
    public List<SC2ShaderFlag> ShaderFlags=new();
    // Real terrain.bdf TerrainMaterial fields that had no pair-level home
    // until the ShaderSetup/State coverage pass (56-map corpus survey
    // confirmed real, if rare, usage) — "" State means the exported
    // TerrainMaterial.State stays null (plain opaque, today's existing
    // default behavior). AlphaTestThreshold only matters when the pair's
    // own ShaderFlags has "ALPHA_TEST" set to true.
    public string State="";
    public Vector2 TextureScroll=Vector2.zero;
    [Range(0,255)] public float AlphaTestThreshold=16f;
    // Only ever set by applying the "WATER" material preset today (real
    // terrain.bdf materials almost always leave both empty, implicitly
    // using the default terrain shader — see SC2MaterialPresets.cs).
    public string ShaderFile="";
    public string ShaderPrefix="";
    [NonSerialized] public Texture2D RnmMaskTexture;
    public byte[] RnmMaskBytes=Array.Empty<byte>();
    public int MaskWidth=2048, MaskHeight=2048;
    // RNM lighting bake data - populated only after the user clicks
    // "Bake Lighting" (SC2RnmBakeService); empty means "never baked", in which
    // case ExportRnmMaskByName falls back to its original hardcoded-128/opaque
    // placeholder, so an un-baked map still exports exactly as it did before
    // this feature existed. RnmMaskBytes' own alpha channel (the hand-painted
    // diffuse-blend factor) is unaffected - these are additive, separate data.
    public byte[] RnmBakedRgb0=Array.Empty<byte>();
    public byte[] RnmBakedRgb1=Array.Empty<byte>();
    public byte[] RnmBakedRgb2=Array.Empty<byte>();
    public byte[] RnmBakedShadowAlpha=Array.Empty<byte>();
    public float TilingScale=4f;
    [Range(0f,1f)] public float SimplifyRatio=0f;
    // TriangleIndices removed — exporter reads TrianglePairIndex[] directly.
    // This avoids allocating 2M-element List<int> per pair on large maps.
    [System.NonSerialized] private int _triCount = -1;
    public int GetTriangleCount(short[] pairIndex, int pairIdx)
    {
        if (_triCount >= 0) return _triCount;
        var n = 0;
        foreach (var pi in pairIndex) if (pi == pairIdx) n++;
        _triCount = n;
        return n;
    }
    public void InvalidateTriCount() => _triCount = -1;

    public void EnsureRnmTexture()
    {
        if(RnmMaskTexture!=null)return;
        RnmMaskTexture=new Texture2D(MaskWidth,MaskHeight,TextureFormat.RGBA32,false,true)
        {name=$"RNM_{Name}",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
        if(RnmMaskBytes.Length==MaskWidth*MaskHeight*4) RnmMaskTexture.LoadRawTextureData(RnmMaskBytes);
        else
        {
            // Was building this neutral fill purely as a local array for
            // the texture, never storing it back into RnmMaskBytes itself
            // — a fresh pair's RnmMaskBytes therefore stayed permanently
            // Array.Empty<byte>() even after this "ensure" ran, silently
            // breaking any CPU-side painter (SC2BackgroundEditMode.
            // ApplyPaintBlend) that checks RnmMaskBytes.Length ==
            // MaskWidth*MaskHeight*4 before writing to it — every paint
            // attempt on a never-baked pair bailed out immediately.
            var raw=new byte[MaskWidth*MaskHeight*4];
            for(var i=0;i<raw.Length;i+=4){raw[i]=128;raw[i+1]=128;raw[i+2]=128;raw[i+3]=0;}
            RnmMaskTexture.LoadRawTextureData(raw);
            RnmMaskBytes=raw;
        }
        RnmMaskTexture.Apply(false);
    }

    [NonSerialized] public Texture2D RnmPreviewMask0;
    [NonSerialized] public Texture2D RnmPreviewMask1;
    [NonSerialized] public Texture2D RnmPreviewMask2;

    /// <summary>True once SC2RnmBakeService has populated all 3 basis
    /// textures at the current mask resolution — mirrors the real-import
    /// gate SC2TerrainMaterialFactory.Create uses for _HasRNM, so a live
    /// preview (SC2BackgroundTerrain.CreateLivePairMaterial) only claims
    /// real RNM data once a bake has actually produced it.</summary>
    public bool HasBakedRnmLighting =>
        RnmBakedRgb0.Length == MaskWidth * MaskHeight * 3 &&
        RnmBakedRgb1.Length == MaskWidth * MaskHeight * 3 &&
        RnmBakedRgb2.Length == MaskWidth * MaskHeight * 3;

    /// <summary>(Re)builds RnmPreviewMask0/1/2 from the baked RGB bytes
    /// plus each channel's own alpha source, matching the exact _0/_1/_2
    /// layout SC2TerrainExporter.ExportRnmMaskByName writes to disk (RGB0
    /// + shadow alpha, RGB1 + hand-painted blend alpha, RGB2 + opaque) —
    /// so a live preview shows the same composition the exported material
    /// will, instead of always falling back to flat Sun-lit shading
    /// regardless of whether "Bake Lighting" has ever run. A no-op until
    /// HasBakedRnmLighting is true.</summary>
    public void RefreshRnmPreviewTextures()
    {
        if (!HasBakedRnmLighting) return;
        var w = MaskWidth; var h = MaskHeight; var n = w * h;
        var blendAlpha = RnmMaskBytes.Length == n * 4 ? RnmMaskBytes : null;
        var shadowAlpha = RnmBakedShadowAlpha.Length == n ? RnmBakedShadowAlpha : null;

        RnmPreviewMask0 = FillCombinedMask(RnmPreviewMask0, "RNMPreview0_" + Name, w, h, RnmBakedRgb0, i => shadowAlpha != null ? shadowAlpha[i] : (byte)255);
        RnmPreviewMask1 = FillCombinedMask(RnmPreviewMask1, "RNMPreview1_" + Name, w, h, RnmBakedRgb1, i => blendAlpha != null ? blendAlpha[i * 4 + 3] : (byte)0);
        RnmPreviewMask2 = FillCombinedMask(RnmPreviewMask2, "RNMPreview2_" + Name, w, h, RnmBakedRgb2, _ => 255);
    }

    private static Texture2D FillCombinedMask(Texture2D existing, string name, int w, int h, byte[] rgb, Func<int, byte> alphaAt)
    {
        var tex = existing != null && existing.width == w && existing.height == h
            ? existing
            : new Texture2D(w, h, TextureFormat.RGBA32, false, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var n = w * h;
        var raw = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            raw[i * 4] = rgb[i * 3];
            raw[i * 4 + 1] = rgb[i * 3 + 1];
            raw[i * 4 + 2] = rgb[i * 3 + 2];
            raw[i * 4 + 3] = alphaAt(i);
        }
        tex.LoadRawTextureData(raw);
        tex.Apply(false);
        return tex;
    }
}

// Radial2/4/6/8 (fixed-count enum values) consolidated into a single Radial
// case + a separate RadialCount field (SC2TerrainData.RadialCount, 2..8
// inclusive, including odd counts) so Terrain/Costs/Objects symmetry math
// can share one implementation instead of three that could disagree on
// what a given count means (Radial4 vs. Rotational4 used to compute
// genuinely different point patterns — a real, confirmed bug this
// consolidation fixes, not just a style cleanup).
public enum SymmetryMode{None,X,Y,XY,Radial}

/// <summary>Single shared source of truth for N-way rotational symmetry
/// angles (degrees, one per additional copy beyond the original) — used by
/// Terrain (SC2TerrainData.GetSymmetricPositions, world-space), Costs
/// (SC2Symmetry.GetSymmetricCells, grid-space), and Objects
/// (SC2ObjectsShell.InferCanonicalFromSlot, inverse) so all three always
/// agree on what "N-way" means. Lives in Scripts/Core (SC2.Core.asmdef)
/// rather than alongside SC2Symmetry.cs (which has no asmdef and compiles
/// into the implicit default assembly) specifically so SC2TerrainData.cs
/// — itself part of SC2.Core.asmdef — can call it directly: a custom
/// asmdef can never reference the implicit default assembly, only the
/// reverse. Count is clamped to 2..8 inclusive (including odd counts,
/// e.g. 3/5/7-player maps).</summary>
public static class SC2RadialSymmetryMath
{
    public static IEnumerable<float> RotationAngleSteps(int count)
    {
        var n = Mathf.Clamp(count, 2, 8);
        for (var k = 1; k < n; k++)
            yield return k * (360f / n);
    }
}
