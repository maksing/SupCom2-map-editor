using System;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>Objects' "Units" slice — third and last spatial element
    /// type (after Markers/Props), same shape as
    /// SC2SetMarkerPositionCommand/SC2SetPropPositionCommand. Like Props
    /// (and unlike Markers), unit positions aren't referenced by ARMY_N
    /// StartPositions, so InvalidatedDomains stays empty — no Scenario
    /// cascade.</summary>
    public sealed class SC2SetUnitPositionCommand : ISC2DocumentCommand
    {
        private readonly int _unitId;
        private readonly Vector3 _newPosition;
        private Vector3 _previousPosition;

        public SC2SetUnitPositionCommand(int unitId, Vector3 newPosition)
        {
            _unitId = unitId;
            _newPosition = newPosition;
        }

        public string Name => $"Move Unit #{_unitId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var unit = FindUnit(document);
            _previousPosition = unit.Position;
            unit.Position = _newPosition;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindUnit(document).Position = _previousPosition;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjUnitNode FindUnit(SC2MapDocument document)
        {
            foreach (var unit in document.Objects.Units)
                if (unit.Id == _unitId)
                    return unit;
            throw new InvalidOperationException($"Unit #{_unitId} not found.");
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
