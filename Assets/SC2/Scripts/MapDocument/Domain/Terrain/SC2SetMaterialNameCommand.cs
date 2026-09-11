using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The first ISC2DocumentCommand for Terrain:
    /// no ISC2DocumentCommand existed anywhere for Terrain before this one
    /// (see SC2MapDocument.Terrain's own doc comment). Terrain's live editing
    /// (SC2TerrainShell/SC2MaterialsPairsMode) still writes directly to
    /// SC2TerrainData through its own bespoke undo stacks — same
    /// "read-only bridge, no real command" state Environment/Collision2/
    /// Costs/Hfield/Objects were all in before their own first commands.
    ///
    /// Mirrors SC2SetEnvironmentTexturePathCommand's shape exactly: a single
    /// string field (Name), addressed by the already-stable Material.Id
    /// (SC2TerrainDocument.FindMaterial) — deliberately the smallest possible slice of a much
    /// larger domain (Grid/Layers/Materials/MaterialPairs/PairAssignments/
    /// Blend/Rnm/Simplification/Normals/Symmetry).
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.TerrainMaterials.
    /// ExpandStaleFlags has no entry for TerrainMaterials specifically (only
    /// TerrainGeometry cascades to Hfield/Costs/Collision2) — a material
    /// rename doesn't invalidate anything else, so InvalidatedDomains is
    /// empty, same as the four leaf-domain commands before Objects.
    /// </summary>
    public sealed class SC2SetMaterialNameCommand : ISC2DocumentCommand
    {
        private readonly string _materialId;
        private readonly string _newName;
        private string _previousName;

        public SC2SetMaterialNameCommand(string materialId, string newName)
        {
            _materialId = materialId ?? throw new ArgumentNullException(nameof(materialId));
            _newName = newName ?? "";
        }

        public string Name => $"Rename Material '{_materialId}'";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var material = FindMaterial(document);
            _previousName = material.Name;
            material.Name = _newName;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindMaterial(document).Name = _previousName;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2MaterialDocument FindMaterial(SC2MapDocument document)
        {
            var material = document.Terrain.FindMaterial(_materialId);
            if (material == null)
                throw new InvalidOperationException($"Material '{_materialId}' not found.");
            return material;
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.TerrainMaterials;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Terrain },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
