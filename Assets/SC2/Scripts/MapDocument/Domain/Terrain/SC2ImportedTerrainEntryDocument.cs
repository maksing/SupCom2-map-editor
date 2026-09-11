using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>One imported-material's live shader-property overrides —
    /// mirrors SC2GameViewEditorUI's in-session ImportedMaterialSnapshot,
    /// just persisted as resource-path strings/floats instead of live
    /// Texture/Material references. Keyed by shader property name (e.g.
    /// "_Diffuse1", "_EnableRNM"), same property set SC2GameViewEditorUI's
    /// ImportedMaterialTextureProps/ImportedMaterialFlagProps arrays already
    /// track for the live per-frame undo snapshot.</summary>
    public sealed class SC2ImportedTerrainMaterialOverrideDocument
    {
        public Dictionary<string, string> TextureResourcePaths = new();
        public Dictionary<string, float> Flags = new();

        public SC2ImportedTerrainMaterialOverrideDocument Clone() => new()
        {
            TextureResourcePaths = new Dictionary<string, string>(TextureResourcePaths),
            Flags = new Dictionary<string, float>(Flags),
        };
    }

    /// <summary>
    /// One imported terrain.bdf object placed in the scene
    /// (SC2GameViewEditorUI._importedTerrains) — this had NO document
    /// schema at all before now: its state lived only in that in-memory
    /// list and live GameObjects, so a fully-Blender-imported map's imported
    /// terrain could not survive Save/Load. OriginalBdf stores the exact
    /// bytes the entry was created from (SC2TerrainBdfImporter.ImportResult.
    /// RawBytes) — re-parsing those bytes (SC2TerrainBdfImporter.
    /// ImportFromBytes) reconstructs the as-imported baseline mesh/materials
    /// deterministically, so this schema only needs to persist what a user
    /// could actually have changed since import: the whole-object transform,
    /// RecomputeNormals, and per-material shader-property overrides — not a
    /// second copy of the (often large) parsed geometry itself.
    /// </summary>
    public sealed class SC2ImportedTerrainEntryDocument
    {
        public string Id = "";
        public string DisplayName = "";
        public SC2BlobReference OriginalBdf;
        public float[] Position = { 0f, 0f, 0f };
        public float[] EulerAngles = { 0f, 0f, 0f };
        public float[] Scale = { 1f, 1f, 1f };
        public bool RecomputeNormals = true;
        /// <summary>Index-aligned with the reconstructed Root's per-submesh
        /// materials array (same order TerrainMeshBuilder.Build always
        /// produces for a given parsed model) — not a dictionary keyed by
        /// name, since imported materials commonly share names.</summary>
        public List<SC2ImportedTerrainMaterialOverrideDocument> MaterialOverrides = new();

        public SC2ImportedTerrainEntryDocument Clone()
        {
            var clone = new SC2ImportedTerrainEntryDocument
            {
                Id = Id,
                DisplayName = DisplayName,
                OriginalBdf = OriginalBdf,
                Position = (float[])Position.Clone(),
                EulerAngles = (float[])EulerAngles.Clone(),
                Scale = (float[])Scale.Clone(),
                RecomputeNormals = RecomputeNormals,
            };
            foreach (var m in MaterialOverrides)
                clone.MaterialOverrides.Add(m.Clone());
            return clone;
        }
    }
}
