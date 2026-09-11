using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Objects' "Armies" slice — renames an army by stable Id,
    /// same shape as every other single-string-field-by-Id command in this
    /// project.</summary>
    public sealed class SC2SetArmyNameCommand : ISC2DocumentCommand
    {
        private readonly int _armyId;
        private readonly string _newName;
        private string _previousName;

        public SC2SetArmyNameCommand(int armyId, string newName)
        {
            _armyId = armyId;
            _newName = newName ?? "";
        }

        public string Name => $"Rename Army #{_armyId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var army = FindArmy(document);
            _previousName = army.Name;
            army.Name = _newName;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindArmy(document).Name = _previousName;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjArmy FindArmy(SC2MapDocument document)
        {
            foreach (var army in document.Objects.Armies)
                if (army.Id == _armyId)
                    return army;
            throw new InvalidOperationException($"Army #{_armyId} not found.");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Objects;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Objects },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
