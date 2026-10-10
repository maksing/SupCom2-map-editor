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

    /// <summary>The object's origin in the model's own coordinates (SC2ImportedPivot): the transform sits on it, so a model vertex is
    /// placed at <c>Transform.TransformPoint(vertex - Pivot)</c>.</summary>
    public Vector3 Pivot;

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
}
