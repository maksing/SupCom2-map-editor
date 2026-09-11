using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Objects' "Zones" slice — sets all 4 rect coordinates (X1/Z1/X2/Z2)
    /// together, since they define one rectangle and only make sense edited
    /// as a unit (SC2ObjectsValidator's OBJ003 check, added alongside this
    /// command, flags a degenerate result). DirtyFlags/InvalidatedDomains
    /// match every other Objects command.
    /// </summary>
    public sealed class SC2SetZoneRectCommand : ISC2DocumentCommand
    {
        private readonly int _zoneId;
        private readonly float _x1, _z1, _x2, _z2;
        private float _prevX1, _prevZ1, _prevX2, _prevZ2;

        public SC2SetZoneRectCommand(int zoneId, float x1, float z1, float x2, float z2)
        {
            _zoneId = zoneId;
            _x1 = x1; _z1 = z1; _x2 = x2; _z2 = z2;
        }

        public string Name => $"Resize Zone #{_zoneId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var zone = FindZone(document);
            (_prevX1, _prevZ1, _prevX2, _prevZ2) = (zone.X1, zone.Z1, zone.X2, zone.Z2);
            (zone.X1, zone.Z1, zone.X2, zone.Z2) = (_x1, _z1, _x2, _z2);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var zone = FindZone(document);
            (zone.X1, zone.Z1, zone.X2, zone.Z2) = (_prevX1, _prevZ1, _prevX2, _prevZ2);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjZone FindZone(SC2MapDocument document)
        {
            foreach (var zone in document.Objects.Zones)
                if (zone.Id == _zoneId)
                    return zone;
            throw new InvalidOperationException($"Zone #{_zoneId} not found.");
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
