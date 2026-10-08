using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>The Collision2 settings that shape the mesh's extent: the floor plane (the lowest height a vertex may have) and the
    /// custom area (the world rectangle in X / Z the mesh covers, when it should not follow the hfield / costs size).</summary>
    public readonly struct SC2CollisionExtents
    {
        public readonly bool FloorEnabled;
        public readonly float FloorHeight;
        public readonly bool CustomArea;
        public readonly float AreaMinX, AreaMinZ, AreaSizeX, AreaSizeZ;

        public SC2CollisionExtents(bool floorEnabled, float floorHeight, bool customArea, float areaMinX, float areaMinZ, float areaSizeX, float areaSizeZ)
        {
            FloorEnabled = floorEnabled; FloorHeight = floorHeight; CustomArea = customArea;
            AreaMinX = areaMinX; AreaMinZ = areaMinZ; AreaSizeX = areaSizeX; AreaSizeZ = areaSizeZ;
        }

        public static SC2CollisionExtents From(SC2CollisionDocument document) => new SC2CollisionExtents(
            document.FloorEnabled, document.FloorHeight, document.CustomArea,
            document.AreaMinX, document.AreaMinZ, document.AreaSizeX, document.AreaSizeZ);

        public void WriteTo(SC2CollisionDocument document)
        {
            document.FloorEnabled = FloorEnabled; document.FloorHeight = FloorHeight; document.CustomArea = CustomArea;
            document.AreaMinX = AreaMinX; document.AreaMinZ = AreaMinZ; document.AreaSizeX = AreaSizeX; document.AreaSizeZ = AreaSizeZ;
        }

        public bool Equals(SC2CollisionExtents other) =>
            FloorEnabled == other.FloorEnabled && FloorHeight == other.FloorHeight && CustomArea == other.CustomArea &&
            AreaMinX == other.AreaMinX && AreaMinZ == other.AreaMinZ && AreaSizeX == other.AreaSizeX && AreaSizeZ == other.AreaSizeZ;
    }

    /// <summary>Sets the Collision2 floor plane and area as one undoable step, committed once per edit (not once per keystroke or slider
    /// frame). Same shape as <see cref="SC2SetCollision2VerticalOffsetCommand"/>: only Collision2 is dirtied.</summary>
    public sealed class SC2SetCollision2ExtentsCommand : ISC2DocumentCommand
    {
        private readonly SC2CollisionExtents _newValue;
        private SC2CollisionExtents _previousValue;

        public SC2SetCollision2ExtentsCommand(SC2CollisionExtents newValue)
        {
            _newValue = newValue;
        }

        public string Name => "Set Collision2 floor / area";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousValue = SC2CollisionExtents.From(document.Collision2);
            _newValue.WriteTo(document.Collision2);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousValue.WriteTo(document.Collision2);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName) => new SC2DocumentChangeSet
        {
            BeforeRevision = document.Revision,
            AfterRevision = document.Revision + 1,
            CommandName = commandName,
            DirectDirtyFlags = SC2DirtyFlags.Collision2,
            ChangedDomains = new[] { SC2DomainId.Collision2 },
            InvalidatedDomains = Array.Empty<SC2DomainId>(),
            TimestampUtc = DateTime.UtcNow,
        };
    }
}
