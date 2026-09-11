using System;
using System.IO;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Fourth real ISC2DocumentCommand (after Environment/Collision2/Costs) —
    /// same blob-backed-grid shape as SC2SetCostsLayerGridCommand, since
    /// TerrainTypesUInt8 is Hfield's one real, populated field (a
    /// content-addressed SC2BlobReference over a byte grid, converted from
    /// the legacy ushort[] per the plan's own explicit instruction — see
    /// SC2HfieldDocument's own doc comment). Simpler than the Costs command:
    /// there's no per-layer Id to look up, since a document has exactly one
    /// terrain-types grid, not a list.
    ///
    /// Undo is cheap for the same reason as the Costs command: ISC2BlobStore
    /// never deletes/mutates a written blob, so Undo just restores the
    /// previous SC2BlobReference struct instead of re-writing bytes.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.HfieldTerrainTypes — a distinct
    /// flag from SC2DirtyFlags.HfieldHeightmap (the two are independent in
    /// the dependency graph: ExpandStaleFlags has no entry for
    /// HfieldTerrainTypes at all, confirming nothing downstream depends on
    /// terrain-type painting specifically — Costs/Collision2 depend on the
    /// HEIGHTMAP, not on terrain types).
    /// </summary>
    public sealed class SC2SetHfieldTerrainTypesCommand : ISC2DocumentCommand
    {
        private readonly byte[] _newGridBytes;
        private readonly ISC2BlobStore _blobStore;
        private SC2BlobReference _previousGrid;

        public SC2SetHfieldTerrainTypesCommand(byte[] newGridBytes, ISC2BlobStore blobStore)
        {
            _newGridBytes = newGridBytes ?? throw new ArgumentNullException(nameof(newGridBytes));
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        }

        public string Name => "Set Hfield.TerrainTypes";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousGrid = document.Hfield.TerrainTypesUInt8;
            document.Hfield.TerrainTypesUInt8 = _blobStore.Put(new MemoryStream(_newGridBytes));
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Hfield.TerrainTypesUInt8 = _previousGrid;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.HfieldTerrainTypes;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Hfield },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
