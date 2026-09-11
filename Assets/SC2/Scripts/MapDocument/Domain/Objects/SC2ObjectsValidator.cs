using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Two checks, both against known, real structural
    /// gaps (not invented):
    ///
    /// 1. Duplicate Id across ALL 8 element lists combined, not just within
    ///    one list — SC2ObjectsDocument.NextObjectId is a single counter for
    ///    every element type (SC2TerrainData.AllocateObjectId(), per
    ///    SC2ObjectsDocument's own doc comment), so Ids are meant to be
    ///    globally unique across Props/Markers/Graphs/Armies/Units/Zones/
    ///    Chains/SymmetryGroups, not just unique per-list.
    /// 2. A SC2ObjChain.MarkerNames entry that doesn't match any existing
    ///    marker's Name — SC2ObjChain links by marker NAME, not stable Id
    ///    (a documented, unfixed gap — see SC2ObjectsDocument's own doc
    ///    comment), so a rename or duplicate-name collision silently
    ///    produces a dangling reference with no error anywhere else in the
    ///    codebase today.
    /// 3. A Zone with a degenerate rect (X1 >= X2 or Z1 >= Z2) — added
    ///    alongside SC2SetZoneRectCommand. A zero-or-negative-area rect
    ///    isn't rejected anywhere else in this codebase and would silently
    ///    behave as an empty area wherever a zone is used for containment
    ///    checks.
    /// </summary>
    public static class SC2ObjectsValidator
    {
        public static SC2ValidationReport Validate(SC2ObjectsDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            var seenIds = new HashSet<int>();
            CheckDuplicateIds(document.Props, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.Markers, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.Graphs, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.Armies, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.Units, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.Zones, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.Chains, m => m.Id, seenIds, report);
            CheckDuplicateIds(document.SymmetryGroups, m => m.Id, seenIds, report);

            var markerNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var markerPlacements = new Dictionary<string, SC2ObjMarker>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var marker in document.Markers)
            {
                if (!string.IsNullOrEmpty(marker.Name) && !markerNames.Add(marker.Name))
                    report.Add(SC2ValidationSeverity.Error, "OBJ004", "Objects", marker.Id.ToString(),
                        $"Duplicate marker name '{marker.Name}'.",
                        "Marker names are unique keys in save.lua; keep the first marker and remove or rename the duplicate.");

                var placementKey = MarkerPlacementKey(marker);
                if (markerPlacements.TryGetValue(placementKey, out var existing) &&
                    SC2MarkerDeduplication.SamePlacement(existing, marker))
                    report.Add(SC2ValidationSeverity.Error, "OBJ005", "Objects", marker.Id.ToString(),
                        $"Marker '{marker.Name}' duplicates '{existing.Name}' at the same position and with the same type/prop.",
                        "Remove the duplicate marker; symmetry and repeated placement should produce only distinct positions.");
                else
                    markerPlacements[placementKey] = marker;
            }

            foreach (var chain in document.Chains)
            {
                foreach (var markerName in chain.MarkerNames)
                {
                    if (!markerNames.Contains(markerName))
                        report.Add(SC2ValidationSeverity.Warning, "OBJ002", "Objects", chain.Id.ToString(),
                            $"Chain '{chain.Name}' references marker name '{markerName}', which doesn't match any existing marker.",
                            "Rename the marker back, or update the chain to reference an existing marker name.");
                }
            }

            foreach (var zone in document.Zones)
            {
                if (zone.X1 >= zone.X2 || zone.Z1 >= zone.Z2)
                    report.Add(SC2ValidationSeverity.Warning, "OBJ003", "Objects", zone.Id.ToString(),
                        $"Zone '{zone.Name}' has a degenerate rect (X1={zone.X1}, Z1={zone.Z1}, X2={zone.X2}, Z2={zone.Z2}).",
                        "Ensure X1 < X2 and Z1 < Z2.");
            }

            return report;
        }

        private static void CheckDuplicateIds<T>(List<T> items, System.Func<T, int> getId, HashSet<int> seenIds, SC2ValidationReport report)
        {
            foreach (var item in items)
            {
                var id = getId(item);
                if (!seenIds.Add(id))
                    report.Add(SC2ValidationSeverity.Error, "OBJ001", "Objects", id.ToString(),
                        $"Duplicate object Id {id}.",
                        "Object Ids must be unique across all Props/Markers/Graphs/Armies/Units/Zones/Chains/SymmetryGroups.");
            }
        }

        private static string MarkerPlacementKey(SC2ObjMarker marker)
        {
            var epsilon = SC2MarkerDeduplication.PositionEpsilon;
            var x = System.Math.Round(marker.Position.x / epsilon);
            var y = System.Math.Round(marker.Position.y / epsilon);
            var z = System.Math.Round(marker.Position.z / epsilon);
            var army = SC2MarkerDeduplication.IsArmyStartMarker(marker.Name) ? "army" : "marker";
            return $"{army}\u001f{marker.Type ?? ""}\u001f{marker.Prop ?? ""}\u001f{x}\u001f{y}\u001f{z}";
        }
    }
}
