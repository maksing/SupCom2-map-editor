using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Both checks here are structural, not judgment calls: a duplicate
    /// layer Id silently breaks SC2CostsDocument.Find (returns only the
    /// first match), and an empty FinalGrid means the one field this
    /// document actually populates (SC2CostsLegacyBridge's own doc comment:
    /// "the one real, populated field") is missing — either would produce a
    /// broken costs.bdf on export, not just a cosmetic issue.
    /// </summary>
    public static class SC2CostsValidator
    {
        public static SC2ValidationReport Validate(SC2CostsDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in document.Layers)
            {
                if (!seenIds.Add(layer.Id))
                    report.Add(SC2ValidationSeverity.Error, "COST001", "Costs", layer.Id,
                        $"Duplicate Costs layer Id '{layer.Id}'.",
                        "Layer Ids must be unique — rename one of the duplicates.");

                if (layer.FinalGrid.IsEmpty)
                    report.Add(SC2ValidationSeverity.Error, "COST002", "Costs", layer.Id,
                        $"Costs layer '{layer.Id}' has no grid data.",
                        "Repaint or regenerate this layer before exporting.");
            }

            return report;
        }
    }
}
