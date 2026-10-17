using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// A map with a symmetry should have its Mass markers symmetric too: each Mass marker has a twin on every point the symmetry maps it
    /// to. A marker without one is a warning (the map plays unevenly, or the symmetry is only partly applied), not an error: a map may be
    /// symmetric apart from a deliberate extra.
    /// </summary>
    public static class SC2MassSymmetryValidator
    {
        public const string Code = "OBJ006";

        public static SC2ValidationReport Validate(IReadOnlyList<SC2ObjMarker> markers, SymmetryMode mode, Vector3 center, int radialCount)
        {
            var report = new SC2ValidationReport();
            if (mode == SymmetryMode.None)
            {
                // No symmetry to measure against: a Mass marker is fine when some standard symmetry of the map pairs it with the others.
                foreach (var marker in SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(markers, center))
                    report.Add(SC2ValidationSeverity.Warning, Code, "Objects", marker.Id.ToString(CultureInfo.InvariantCulture),
                        $"Mass marker '{marker.Name}' ({N(marker.Position.x)}, {N(marker.Position.z)}) has no symmetric twin: " +
                        "the map has no symmetry set, and no other Mass marker mirrors or rotates this one about the map centre.",
                        "Place the missing Mass marker(s) so the Mass markers are symmetric, or ignore this if the map is meant to be asymmetric.");
                return report;
            }
            var description = SC2MassSymmetryCheck.Describe(mode, radialCount);
            foreach (var twin in SC2MassSymmetryCheck.FindMissingTwins(markers, mode, center, radialCount))
            {
                report.Add(SC2ValidationSeverity.Warning, Code, "Objects", twin.MarkerId.ToString(CultureInfo.InvariantCulture),
                    $"Mass marker '{twin.MarkerName}' ({N(twin.MarkerPosition.x)}, {N(twin.MarkerPosition.z)}) has no symmetric twin: " +
                    $"the map is {description}, so another Mass marker should be at ({N(twin.Expected.x)}, {N(twin.Expected.z)}).",
                    "Place a Mass marker at that point (with symmetry on, the Objects tab places the copies itself), or move this marker onto the symmetry axis.");
            }
            return report;
        }

        private static string N(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
