using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Terrain's "Paint" slice — SC2TrianglePairMap's two fields
    /// (SlotIndexPerTriangleInt16 blob + the append-only SlotIds table)
    /// always change together in one edit (assigning triangles to a pair
    /// may need a brand-new slot appended first), so this swaps both
    /// wholesale in one command, same "caller computes the full new state,
    /// command just swaps it in" contract as
    /// SC2SetCostsLayerGridCommand/SC2SetHfieldTerrainTypesCommand — this
    /// project's paint commands never do partial in-place brush math
    /// themselves.
    ///
    /// This is the GPU-adjacent part of Terrain — among
    /// the most risky and least verifiable code in this domain, since
    /// the live SC2PaintMode/SC2TerrainRuntime path stays completely
    /// untouched by this command; it only gives the DOCUMENT model a real
    /// way to receive a finished paint result, not a live GPU brush.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.TerrainMaterials — which pair
    /// covers which triangle is a materials-assignment concern, not
    /// geometry. InvalidatedDomains is empty, same as the other two
    /// Materials-domain commands (only TerrainGeometry cascades).
    /// </summary>
    public sealed class SC2SetTrianglePairAssignmentsCommand : ISC2DocumentCommand
    {
        private readonly byte[] _newSlotIndexInt16Bytes;
        private readonly List<string> _newSlotIds;
        private readonly ISC2BlobStore _blobStore;
        private SC2BlobReference _previousBlob;
        private List<string> _previousSlotIds;

        public SC2SetTrianglePairAssignmentsCommand(byte[] newSlotIndexInt16Bytes, IEnumerable<string> newSlotIds, ISC2BlobStore blobStore)
        {
            _newSlotIndexInt16Bytes = newSlotIndexInt16Bytes ?? throw new ArgumentNullException(nameof(newSlotIndexInt16Bytes));
            if (newSlotIds == null) throw new ArgumentNullException(nameof(newSlotIds));
            _newSlotIds = new List<string>(newSlotIds);
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        }

        public string Name => "Paint Terrain.PairAssignments";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var map = document.Terrain.PairAssignments;
            _previousBlob = map.SlotIndexPerTriangleInt16;
            _previousSlotIds = new List<string>(map.SlotIds);

            map.SlotIndexPerTriangleInt16 = _blobStore.Put(new MemoryStream(_newSlotIndexInt16Bytes));
            map.SlotIds.Clear();
            map.SlotIds.AddRange(_newSlotIds);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var map = document.Terrain.PairAssignments;
            map.SlotIndexPerTriangleInt16 = _previousBlob;
            map.SlotIds.Clear();
            map.SlotIds.AddRange(_previousSlotIds);
            return BuildChangeSet(document, Name + " (undo)");
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
