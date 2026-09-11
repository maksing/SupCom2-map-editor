using System;
using System.IO;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Third real ISC2DocumentCommand (after SC2SetEnvironmentTexturePathCommand
    /// and SC2SetCollision2VerticalOffsetCommand) — the first one whose edited
    /// value is blob-backed rather than a scalar/string field directly on the
    /// document, since Costs' one real, populated field
    /// (SC2CostsLayerDocument.FinalGrid) is a content-addressed
    /// SC2BlobReference over a ~1MB byte grid (1=passable, 5=border,
    /// 255=impassable), not something small enough to hold twice inline the
    /// way Environment's strings or Collision2's float were.
    ///
    /// Undo is still cheap despite the grid's size: ISC2BlobStore is
    /// content-addressed and never deletes/mutates a blob once written (see
    /// its own doc comment: "no compaction/GC/retention policy yet"), so
    /// Undo doesn't need to re-write anything — it just restores the
    /// PREVIOUS SC2BlobReference struct, which still resolves against
    /// whatever bytes were already stored for it.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.Costs — the real flag
    /// SmartExport's dependency graph already keys costs.win.bdf to
    /// (SC2ExportDependencyGraph.CreateDefaultFileMasks). ExpandStaleFlags
    /// only ever ADDS Costs as a result of TerrainGeometry/HfieldHeightmap
    /// changing (Costs is generated FROM hfield) — nothing becomes stale
    /// because Costs itself changed, so InvalidatedDomains is empty here too.
    /// </summary>
    public sealed class SC2SetCostsLayerGridCommand : ISC2DocumentCommand
    {
        private readonly string _layerId;
        private readonly byte[] _newGridBytes;
        private readonly ISC2BlobStore _blobStore;
        private SC2BlobReference _previousGrid;

        public SC2SetCostsLayerGridCommand(string layerId, byte[] newGridBytes, ISC2BlobStore blobStore)
        {
            _layerId = layerId ?? throw new ArgumentNullException(nameof(layerId));
            _newGridBytes = newGridBytes ?? throw new ArgumentNullException(nameof(newGridBytes));
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        }

        public string Name => $"Paint Costs.{_layerId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var layer = FindLayer(document);
            _previousGrid = layer.FinalGrid;
            layer.FinalGrid = _blobStore.Put(new MemoryStream(_newGridBytes));
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindLayer(document).FinalGrid = _previousGrid;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2CostsLayerDocument FindLayer(SC2MapDocument document)
        {
            var layer = document.Costs.Find(_layerId);
            if (layer == null)
                throw new InvalidOperationException($"Costs layer '{_layerId}' not found.");
            return layer;
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Costs;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Costs },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
