using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Objects' "SymmetryGroups" slice — sets Kind, the one plain
    /// string field on SC2ObjSymmetryGroup (defaults to "Marker"). Doesn't
    /// touch Members (the group's actual linked elements) — that's a
    /// structurally different, bigger edit (break/rebuild a group) left for
    /// later, same reasoning as SC2SetGraphNameCommand leaving edges
    /// alone.</summary>
    public sealed class SC2SetSymmetryGroupKindCommand : ISC2DocumentCommand
    {
        private readonly int _groupId;
        private readonly string _newKind;
        private string _previousKind;

        public SC2SetSymmetryGroupKindCommand(int groupId, string newKind)
        {
            _groupId = groupId;
            _newKind = newKind ?? "";
        }

        public string Name => $"Set SymmetryGroup #{_groupId}.Kind";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var group = FindGroup(document);
            _previousKind = group.Kind;
            group.Kind = _newKind;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindGroup(document).Kind = _previousKind;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjSymmetryGroup FindGroup(SC2MapDocument document)
        {
            foreach (var group in document.Objects.SymmetryGroups)
                if (group.Id == _groupId)
                    return group;
            throw new InvalidOperationException($"SymmetryGroup #{_groupId} not found.");
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
