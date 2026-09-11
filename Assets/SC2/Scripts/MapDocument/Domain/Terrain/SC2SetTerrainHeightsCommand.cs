using System;
using System.IO;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Terrain's "Geometry" slice — swaps SC2TerrainGrid.HeightsFloat32
    /// (a content-addressed blob, same pattern as SC2SetCostsLayerGridCommand/
    /// SC2SetHfieldTerrainTypesCommand) plus the Min/Max display fields that
    /// go with it.
    ///
    /// The one command in this whole batch where DirectDirtyFlags actually
    /// cascades: SC2DirtyFlags.TerrainGeometry is the one source
    /// SC2ExportDependencyGraph.ExpandStaleFlags expands from — a height
    /// edit invalidates HfieldHeightmap, Costs, AND Collision2 (all three
    /// are generated from the heightmap). InvalidatedDomains lists Hfield/
    /// Costs/Collision2 accordingly — the first command in this project
    /// where that list isn't empty for a reason other than Objects'
    /// Scenario cascade.
    /// </summary>
    public sealed class SC2SetTerrainHeightsCommand : ISC2DocumentCommand
    {
        private readonly byte[] _newHeightsFloat32Bytes;
        private readonly float _newMinimumHeight;
        private readonly float _newMaximumHeight;
        private readonly ISC2BlobStore _blobStore;
        private SC2BlobReference _previousHeights;
        private float _previousMinimumHeight;
        private float _previousMaximumHeight;

        public SC2SetTerrainHeightsCommand(byte[] newHeightsFloat32Bytes, float newMinimumHeight, float newMaximumHeight, ISC2BlobStore blobStore)
        {
            _newHeightsFloat32Bytes = newHeightsFloat32Bytes ?? throw new ArgumentNullException(nameof(newHeightsFloat32Bytes));
            _newMinimumHeight = newMinimumHeight;
            _newMaximumHeight = newMaximumHeight;
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        }

        public string Name => "Set Terrain.Heights";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var grid = document.Terrain.Grid;
            _previousHeights = grid.HeightsFloat32;
            _previousMinimumHeight = grid.MinimumHeight;
            _previousMaximumHeight = grid.MaximumHeight;

            grid.HeightsFloat32 = _blobStore.Put(new MemoryStream(_newHeightsFloat32Bytes));
            grid.MinimumHeight = _newMinimumHeight;
            grid.MaximumHeight = _newMaximumHeight;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var grid = document.Terrain.Grid;
            grid.HeightsFloat32 = _previousHeights;
            grid.MinimumHeight = _previousMinimumHeight;
            grid.MaximumHeight = _previousMaximumHeight;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.TerrainGeometry;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Terrain },
                InvalidatedDomains = new[] { SC2DomainId.Hfield, SC2DomainId.Costs, SC2DomainId.Collision2 },
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
