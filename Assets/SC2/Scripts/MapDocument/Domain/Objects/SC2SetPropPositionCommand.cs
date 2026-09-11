using System;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Objects' "Props" slice — mirrors SC2SetMarkerPositionCommand exactly
    /// (same stable-Id-lookup, same Position field), for the other spatial
    /// element type Objects owns. DirectDirtyFlags/InvalidatedDomains are
    /// identical to the Marker command's reasoning: SC2DirtyFlags.Objects,
    /// and ExpandStaleFlags(Objects) still expands into SaveLua/ScenarioLua
    /// — but props aren't referenced by ARMY_N StartPositions the way
    /// markers are, so unlike SC2SetMarkerPositionCommand this one does NOT
    /// invalidate Scenario. (_save.lua's Props block is still Objects' own
    /// data — SaveLua dirtying doesn't imply a separate domain went stale.)
    /// </summary>
    public sealed class SC2SetPropPositionCommand : ISC2DocumentCommand
    {
        private readonly int _propId;
        private readonly Vector3 _newPosition;
        private Vector3 _previousPosition;

        public SC2SetPropPositionCommand(int propId, Vector3 newPosition)
        {
            _propId = propId;
            _newPosition = newPosition;
        }

        public string Name => $"Move Prop #{_propId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var prop = FindProp(document);
            _previousPosition = prop.Position;
            prop.Position = _newPosition;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindProp(document).Position = _previousPosition;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjProp FindProp(SC2MapDocument document)
        {
            foreach (var prop in document.Objects.Props)
                if (prop.Id == _propId)
                    return prop;
            throw new InvalidOperationException($"Prop #{_propId} not found.");
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
