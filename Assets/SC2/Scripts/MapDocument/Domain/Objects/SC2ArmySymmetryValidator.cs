using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// A map with a symmetry should have its army start positions symmetric too: each ARMY_n marker has a twin on every point the
    /// symmetry maps it to. Without a symmetry setting an army is fine when some standard symmetry of the map pairs it with the
    /// others. A missing twin is a warning (a deliberately uneven start is possible), the same as for Mass markers.
    /// </summary>
    public static class SC2ArmySymmetryValidator
    {
        public const string Code = "OBJ007";

        public static SC2ValidationReport Validate(IReadOnlyList<SC2ObjMarker> markers, SymmetryMode mode, Vector3 center, int radialCount)
        {
            var report = new SC2ValidationReport();
            if (mode == SymmetryMode.None)
            {
                foreach (var marker in SC2MassSymmetryCheck.FindUnpairedWithoutSymmetry(markers, center, belongs: SC2MassSymmetryCheck.IsArmyStart))
                    report.Add(SC2ValidationSeverity.Warning, Code, "Objects", marker.Id.ToString(CultureInfo.InvariantCulture),
                        $"Army '{marker.Name}' ({N(marker.Position.x)}, {N(marker.Position.z)}) has no symmetric twin: " +
                        "the map has no symmetry set, and no other army start mirrors or rotates this one about the map centre.",
                        "Place the missing army start(s) so the start positions are symmetric, or ignore this if the map is meant to be asymmetric.");
                return report;
            }

            var description = SC2MassSymmetryCheck.Describe(mode, radialCount);
            foreach (var twin in SC2MassSymmetryCheck.FindMissingTwins(markers, mode, center, radialCount, belongs: SC2MassSymmetryCheck.IsArmyStart))
                report.Add(SC2ValidationSeverity.Warning, Code, "Objects", twin.MarkerId.ToString(CultureInfo.InvariantCulture),
                    $"Army '{twin.MarkerName}' ({N(twin.MarkerPosition.x)}, {N(twin.MarkerPosition.z)}) has no symmetric twin: " +
                    $"the map is {description}, so another army start should be at ({N(twin.Expected.x)}, {N(twin.Expected.z)}).",
                    "Place an army start at that point (with symmetry on, the Objects tab places the copies itself), or move this army onto the symmetry axis.");
            return report;
        }

        private static string N(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
