using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Terrain's "Pairs" slice — relinks a SC2MaterialPairDocument's
    /// Material1Id/Material2Id. This is the exact operation the legacy
    /// index-shift bug (delete-material relinking by index instead of a
    /// stable Id) was about, and the exact thing
    /// SC2TerrainValidator's TER002/TER003 checks guard going forward — this
    /// command doesn't validate the new Ids exist itself (Do/Undo just
    /// record whatever the caller passes, same as every other command in
    /// this project — validation is a separate, explicit pass via
    /// SC2TerrainValidator, not baked into command execution), but a caller
    /// that runs the validator after executing this command will now catch
    /// a relink to a nonexistent material immediately.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.TerrainMaterials — pairs are
    /// materials-domain data (SC2MaterialPairDocument lives in
    /// SC2TerrainDocument.MaterialPairs, alongside Materials).
    /// InvalidatedDomains is empty, same reasoning as the other two
    /// Materials-domain commands.
    /// </summary>
    public sealed class SC2SetMaterialPairLinkCommand : ISC2DocumentCommand
    {
        private readonly string _pairId;
        private readonly string _newMaterial1Id;
        private readonly string _newMaterial2Id;
        private string _previousMaterial1Id;
        private string _previousMaterial2Id;

        public SC2SetMaterialPairLinkCommand(string pairId, string newMaterial1Id, string newMaterial2Id)
        {
            _pairId = pairId ?? throw new ArgumentNullException(nameof(pairId));
            _newMaterial1Id = newMaterial1Id ?? "";
            _newMaterial2Id = newMaterial2Id ?? "";
        }

        public string Name => $"Relink Pair '{_pairId}'";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var pair = FindPair(document);
            _previousMaterial1Id = pair.Material1Id;
            _previousMaterial2Id = pair.Material2Id;
            pair.Material1Id = _newMaterial1Id;
            pair.Material2Id = _newMaterial2Id;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var pair = FindPair(document);
            pair.Material1Id = _previousMaterial1Id;
            pair.Material2Id = _previousMaterial2Id;
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
