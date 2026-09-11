using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Second real ISC2DocumentCommand (after
    /// SC2SetEnvironmentTexturePathCommand) — same reasoning for picking
    /// Collision2 next: SC2TerrainData persists exactly one Collision2-
    /// related field, Collision2HeightOffset (mapped here to
    /// SC2CollisionDocument.Generation.VerticalOffset — see
    /// SC2CollisionLegacyBridge's own doc comment), so it's the smallest
    /// possible next slice, not by the plan's own domain ordering.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.Collision2 — already the real
    /// flag SmartExport's dependency graph keys collision2.win.bdf to
    /// (SC2ExportDependencyGraph.CreateDefaultFileMasks). ExpandStaleFlags
    /// only ever ADDS Collision2 as a result of TerrainGeometry/
    /// HfieldHeightmap changing (Collision2 is generated FROM hfield) — it
    /// never lists anything that becomes stale because Collision2 itself
    /// changed, so InvalidatedDomains is correctly empty here, same as
    /// Environment's command.
    /// </summary>
    public sealed class SC2SetCollision2VerticalOffsetCommand : ISC2DocumentCommand
    {
        private readonly float _newValue;
        private float _previousValue;

        public SC2SetCollision2VerticalOffsetCommand(float newValue)
        {
            _newValue = newValue;
        }

        public string Name => "Set Collision2.VerticalOffset";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousValue = document.Collision2.Generation.VerticalOffset;
            document.Collision2.Generation.VerticalOffset = _newValue;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Collision2.Generation.VerticalOffset = _previousValue;
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
