using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Which of SC2MaterialDocument's 3 texture reference slots a
    /// SC2SetMaterialTextureCommand targets.</summary>
    public enum SC2MaterialTextureSlot
    {
        Diffuse,
        Normal,
        Mask
    }

    /// <summary>
    /// Extends Terrain's Materials coverage beyond
    /// SC2SetMaterialNameCommand's Name field to the 3 texture-reference
    /// slots (SC2TextureReference.Name — Texture2D.name, the one identity
    /// SC2TerrainLegacyBridge populates today; see SC2TextureReference's own
    /// doc comment for why it's this minimal). Same slot-enum shape as
    /// SC2SetEnvironmentTexturePathCommand/SC2SetDecalsAtlasPathCommand.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.TerrainMaterials, same as the
    /// Name command — both are Materials-domain edits, and
    /// ExpandStaleFlags has no entry for TerrainMaterials, so
    /// InvalidatedDomains stays empty.
    /// </summary>
    public sealed class SC2SetMaterialTextureCommand : ISC2DocumentCommand
    {
        private readonly string _materialId;
        private readonly SC2MaterialTextureSlot _slot;
        private readonly SC2TextureReference _newTexture;
        private SC2TextureReference _previousTexture;

        public SC2SetMaterialTextureCommand(string materialId, SC2MaterialTextureSlot slot, string newTextureName)
            : this(materialId, slot, new SC2TextureReference { Name = newTextureName ?? "" })
        {
        }

        public SC2SetMaterialTextureCommand(string materialId, SC2MaterialTextureSlot slot, SC2TextureReference newTexture)
        {
            _materialId = materialId ?? throw new ArgumentNullException(nameof(materialId));
            _slot = slot;
            _newTexture = (newTexture ?? new SC2TextureReference()).Clone();
        }

        public string Name => $"Set Material '{_materialId}'.{_slot}Texture";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var slotRef = GetSlot(FindMaterial(document), _slot);
            _previousTexture = slotRef.Clone();
            slotRef.CopyFrom(_newTexture);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            GetSlot(FindMaterial(document), _slot).CopyFrom(_previousTexture);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2MaterialDocument FindMaterial(SC2MapDocument document)
        {
            var material = document.Terrain.FindMaterial(_materialId);
            if (material == null)
                throw new InvalidOperationException($"Material '{_materialId}' not found.");
            return material;
        }

        private static SC2TextureReference GetSlot(SC2MaterialDocument material, SC2MaterialTextureSlot slot) => slot switch
        {
            SC2MaterialTextureSlot.Diffuse => material.Diffuse,
            SC2MaterialTextureSlot.Normal => material.Normal,
            SC2MaterialTextureSlot.Mask => material.Mask,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

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
