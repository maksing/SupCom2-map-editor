using UnityEngine;
using BdfIO.Formats.Terrain;

/// <summary>
/// Everything SC2TerrainExporter needs to merge one imported terrain.bdf
/// object into the map being exported (see
/// SC2TerrainExporter.MergeImportedTerrain). Shared between
/// SC2GameViewEditorUI (which owns the imported-terrain list and the live
/// GameObject/Transform) and SC2TerrainShell/SC2ExportPanel (which trigger
/// the export but don't own that list) via
/// SC2TerrainShell.GetImportedTerrainsForExport.
/// </summary>
public sealed class SC2ImportedTerrainExportData
{
    /// <summary>The original parsed model — its Materials/Layers/Vertices/Triangles are copied as-is into the export.</summary>
    public TerrainModel Model;

    /// <summary>Current position/rotation/scale (as edited in the Imported panel) — baked into vertex positions/normals at merge time.</summary>
    public Transform Transform;

    /// <summary>
    /// When true (default), the ENTIRE tangent frame (normal, tangent,
    /// binormal) is recomputed at merge time from the actual triangle
    /// geometry and the mesh's real UV1, instead of trusting what the
    /// imported terrain.bdf stores.
    ///
    /// Default-on because Blender-side terrain.bdf exporters write both
    /// halves of the frame as constants. Measured directly on a real
    /// user-supplied Blender export:
    ///  - Normals: 165432/165432 vertices (100%) stored exactly (0,1,0),
    ///    including vertices on near-vertical cliff faces whose true face
    ///    normal was (-0.848, 0.105, 0.519) — 63.7 degrees mean deviation
    ///    from the real geometric normal. All 56 vanilla maps measure
    ///    0.78..38.5 degrees, so genuine content is well clear of this.
    ///  - Tangents: exactly ONE distinct tangent direction,
    ///    (0.55, 0.00, -0.80), shared by all 165432 vertices — 69.8 degrees
    ///    from the correct UV1-derived tangent, with 93.8% of vertices off
    ///    by more than 45 degrees. Vanilla maps carry ~3850 distinct
    ///    tangent directions and measure 10.5..11.6 degrees.
    ///
    /// A single constant tangent means every normal-mapped surface is lit
    /// through the same wrong basis everywhere, locked to one world
    /// direction. That is what makes the game's normal-mapped structure
    /// tarmac decals (StructureUnit.CreateTarmac — rectangular, axis-aligned,
    /// sized by the blueprint's SkirtSizeX/SkirtSizeZ) render as bright
    /// axis-aligned bands during construction.
    ///
    /// Turn OFF to preserve the source file's own frame byte-for-byte —
    /// correct when re-exporting a genuine vanilla terrain.bdf, whose
    /// authored normals (with intentional split/hard edges) would be
    /// smoothed away by recomputation.
    /// </summary>
    public bool RecomputeNormals = true;

    public string Name;

    /// <summary>The imported entry's stable id (the same one the Lighting Bake panel lists it under); names the object in the
    /// bake's shadow rules, see SC2ShadowCasting. Null/empty = the object has no rules of its own.</summary>
    public string Id;

    /// <summary>Per-layer (per-material) name of the imported RNM mask this
    /// layer's lighting is baked onto (Terrain > Lighting Bake > Imported
    /// Objects); index = layer/material index of <see cref="Model"/>, "" or a
    /// missing entry = not baked. Null = none. See SC2RnmBakeService.</summary>
    public string[] RnmBakeMaskNames;

    /// <summary>The object's authored live materials (index-aligned with
    /// <see cref="Model"/>'s layers) — the bake reads each layer's current
    /// _RNMMask1 alpha from here to carry its blend over to a new mask.</summary>
    public Material[] Materials;

    /// <summary>Per-material real "RNM Lighting" value, kept in sync with
    /// the user's own Shader Setup checkbox edits (SC2GameViewEditorUI) —
    /// NEVER with the Textured/RNM debug preview toolbar, which forces the
    /// live Material's _EnableRNM property to a fixed value on every
    /// material purely for comparison-view purposes. Without this, export
    /// (SC2TerrainMaterialExporter.SyncShaderSetupAndStateFromScene) read
    /// _EnableRNM directly off the live Material — meaning any export
    /// performed while the toolbar wasn't specifically on "RNM" silently
    /// stripped RNM_LIGHTING from every material's exported ShaderSetup,
    /// regardless of the real imported/edited value. Confirmed the actual
    /// root cause of repeated "RNM_LIGHTING not active" reports. Null (or a
    /// too-short array) falls back to the old live-property read, for any
    /// caller that hasn't been updated to populate this.</summary>
    public float[] AuthoredEnableRnm;

    /// <summary>Per-slot manual texture-path overrides, keyed by
    /// "materialIndex:shaderProp" (SC2GameViewEditorUI.SC2ImportedTerrainEntry.
    /// TexturePathOverrides) — a non-empty entry wins verbatim over the live
    /// Material's own bound Texture at export time (SC2TerrainMaterialExporter.
    /// ApplyFromScene), the same "typed path always wins" rule
    /// RegTex/DiffuseOverridePath already use for painted-pair materials.
    /// Null (or no entry for a given slot) falls back to the existing
    /// Texture-based resolution, unchanged.</summary>
    public System.Collections.Generic.Dictionary<string, string> TexturePathOverrides;

    /// <summary>Per-slot "copy into map on export" opt-in, same
    /// "materialIndex:shaderProp" keys as TexturePathOverrides. A true entry
    /// makes a vanilla-archive texture get copied into the exported map
    /// instead of referenced by its real game path (see
    /// SC2VanillaArchiveClassifier). Null/missing = default (don't copy).</summary>
    public System.Collections.Generic.Dictionary<string, bool> TexturePathForceExport;

    /// <summary>When true, this entry stands in for the procedural
    /// decorative background terrain (SC2BackgroundTerrain) instead of
    /// the generator — the user has substituted their own custom
    /// terrain.bdf for that role. Merges through the normal
    /// MergeImportedTerrain path unchanged; SC2TerrainExporter only reads
    /// this flag to decide whether to also run the procedural generator
    /// (skipped when any entry has this set, to avoid two overlapping
    /// backgrounds).</summary>
    public bool IsBackgroundTerrain;
}
