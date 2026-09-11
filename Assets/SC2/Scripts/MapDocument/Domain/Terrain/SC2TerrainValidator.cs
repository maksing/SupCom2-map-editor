using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Directly guards the exact bug class already found and fixed once
    /// in the legacy code (delete-material relinking by index instead of a
    /// stable Id — before
    /// that fix, deleting a material could silently repoint an unrelated
    /// pair at the wrong material via index shift). Stable Ids fixed the
    /// legacy delete path, but nothing validates that a document's pairs
    /// still point at materials that actually exist — this does.
    /// </summary>
    public static class SC2TerrainValidator
    {
        public static SC2ValidationReport Validate(SC2TerrainDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            var materialIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in document.Materials)
            {
                if (!materialIds.Add(material.Id))
                    report.Add(SC2ValidationSeverity.Error, "TER001", "Terrain", material.Id,
                        $"Duplicate Material Id '{material.Id}'.",
                        "Material Ids must be unique.");
            }

            foreach (var pair in document.MaterialPairs)
            {
                if (!materialIds.Contains(pair.Material1Id))
                    report.Add(SC2ValidationSeverity.Error, "TER002", "Terrain", pair.Id,
                        $"Material pair '{pair.Id}' references Material1Id '{pair.Material1Id}', which doesn't exist.",
                        "Fix the pair's material link or remove the pair.");

                if (!materialIds.Contains(pair.Material2Id))
                    report.Add(SC2ValidationSeverity.Error, "TER003", "Terrain", pair.Id,
                        $"Material pair '{pair.Id}' references Material2Id '{pair.Material2Id}', which doesn't exist.",
                        "Fix the pair's material link or remove the pair.");
            }

            return report;
        }
    }
}
