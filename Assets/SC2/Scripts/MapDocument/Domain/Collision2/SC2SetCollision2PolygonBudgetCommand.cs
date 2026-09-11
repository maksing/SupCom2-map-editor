using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Mirrors SC2SetCollision2VerticalOffsetCommand exactly, for the other
    /// mutable field on SC2CollisionGenerationSettings — MaxPolygons (the
    /// ground TIN's triangle budget, user-adjustable 2500-10000; see
    /// SC2Collision2Shell's slider). The binary format itself has no fixed-
    /// size ceiling here (vertex/triangle counts are plain UInt32 fields in
    /// Collision2Bdf.Serialize) — 2500 was a vanilla-observed convention,
    /// not a format limit, so raising it is safe at the file-format level.
    /// </summary>
    public sealed class SC2SetCollision2PolygonBudgetCommand : ISC2DocumentCommand
    {
        private readonly int _newValue;
        private int _previousValue;

        public SC2SetCollision2PolygonBudgetCommand(int newValue)
        {
            _newValue = newValue;
        }

        public string Name => "Set Collision2.MaxPolygons";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousValue = document.Collision2.Generation.MaxPolygons;
            document.Collision2.Generation.MaxPolygons = _newValue;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Collision2.Generation.MaxPolygons = _previousValue;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Collision2;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Collision2 },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
