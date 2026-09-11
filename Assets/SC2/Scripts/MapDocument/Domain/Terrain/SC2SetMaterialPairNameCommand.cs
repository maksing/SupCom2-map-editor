using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Renames a material pair by stable Id — mirrors
    /// SC2SetMaterialNameCommand exactly. Built specifically to plug a real,
    /// pre-existing gap in the live editor: SC2PairsMode.DrawDetails's
    /// `pair.Name = EditorGUILayout.TextField("Name", pair.Name);` had NO
    /// undo checkpoint at all (unlike PairColor, which does call
    /// RecordUndoCheckpoint) — this command is what
    /// SC2TerrainCombinedUndo/SC2TerrainShell.ExecuteCommand wires that edit
    /// through, adding undo where none existed rather than replacing
    /// anything that already worked.
    /// </summary>
    public sealed class SC2SetMaterialPairNameCommand : ISC2DocumentCommand
    {
        private readonly string _pairId;
        private readonly string _newName;
        private string _previousName;

        public SC2SetMaterialPairNameCommand(string pairId, string newName)
        {
            _pairId = pairId ?? throw new ArgumentNullException(nameof(pairId));
            _newName = newName ?? "";
        }

        public string Name => $"Rename Pair '{_pairId}'";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var pair = FindPair(document);
            _previousName = pair.Name;
            pair.Name = _newName;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindPair(document).Name = _previousName;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2MaterialPairDocument FindPair(SC2MapDocument document)
        {
            var pair = document.Terrain.FindPair(_pairId);
            if (pair == null)
                throw new InvalidOperationException($"Material pair '{_pairId}' not found.");
            return pair;
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
