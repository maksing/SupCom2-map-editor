using System;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Fifth real ISC2DocumentCommand (after Environment/Collision2/Costs/
    /// Hfield) — the first one for Objects, deliberately the simplest slice
    /// of the largest, most complex domain (8 element lists:
    /// Props/Markers/Graphs/Armies/Units/Zones/Chains/SymmetryGroups).
    /// Markers are the most numerous, most commonly moved element (see the
    /// existing "Viewport drag-to-move for Props/Markers/Units" feature),
    /// and Position is a single field lookup-able by the already-stable
    /// Marker.Id — no new identity scheme needed, unlike Terrain's
    /// Materials/Pairs before their own stable-Id migration.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.Objects — the real flag
    /// SmartExport's dependency graph keys mapobjs.win.bdf to. Unlike every
    /// command built so far (Environment/Collision2/Costs/Hfield are all
    /// leaves with empty InvalidatedDomains), Objects is NOT a leaf:
    /// SC2ExportDependencyGraph.ExpandStaleFlags(Objects) adds SaveLua AND
    /// ScenarioLua to the dirty set, because _save.lua's Markers block IS
    /// Objects' own data (no separate domain — already accounted for via
    /// ChangedDomains) and _scenario.lua's StartPositions block is derived
    /// from ARMY_N markers (SC2ScenarioLuaWriter.Regenerate) — which DOES
    /// belong to a different domain (Scenario), so InvalidatedDomains
    /// correctly lists SC2DomainId.Scenario as newly stale.
    /// </summary>
    public sealed class SC2SetMarkerPositionCommand : ISC2DocumentCommand
    {
        private readonly int _markerId;
        private readonly Vector3 _newPosition;
        private Vector3 _previousPosition;

        public SC2SetMarkerPositionCommand(int markerId, Vector3 newPosition)
        {
            _markerId = markerId;
            _newPosition = newPosition;
        }

        public string Name => $"Move Marker #{_markerId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var marker = FindMarker(document);
            _previousPosition = marker.Position;
            marker.Position = _newPosition;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindMarker(document).Position = _previousPosition;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjMarker FindMarker(SC2MapDocument document)
        {
            foreach (var marker in document.Objects.Markers)
                if (marker.Id == _markerId)
                    return marker;
            throw new InvalidOperationException($"Marker #{_markerId} not found.");
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
                InvalidatedDomains = new[] { SC2DomainId.Scenario },
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
