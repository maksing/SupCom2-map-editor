namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Only
    /// flags a missing DiffuseAtlasPath when the map actually HAS decal
    /// geometry (HasDecals) — a map with no decals legitimately has no
    /// atlas path at all, so checking unconditionally would flag most maps
    /// for no reason.
    /// </summary>
    public static class SC2DecalsValidator
    {
        public static SC2ValidationReport Validate(SC2DecalsDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            if ((document.HasDecals || document.Placements.Count > 0) && string.IsNullOrWhiteSpace(document.DiffuseAtlasPath))
                report.Add(SC2ValidationSeverity.Warning, "DEC001", "Decals", null,
                    "Map has decal geometry but DiffuseAtlasPath is empty.",
                    "Set a diffuse atlas texture path — decals will render unshaded without one.");

            if (document.AtlasWidth < 0 || document.AtlasHeight < 0)
                report.Add(SC2ValidationSeverity.Error, "DEC002", "Decals", null,
                    "Decal atlas dimensions are invalid.", "Choose a valid atlas texture.");

            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var entry in document.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
                    report.Add(SC2ValidationSeverity.Error, "DEC003", "Decals", entry?.Id,
                        "A decal catalog entry has a missing or duplicate stable ID.", "Duplicate the entry again or assign a new ID.");
                if (entry == null) continue;
                if (entry.Width <= 0 || entry.Height <= 0 || entry.X < 0 || entry.Y < 0 ||
                    (document.AtlasWidth > 0 && entry.X + entry.Width > document.AtlasWidth) ||
                    (document.AtlasHeight > 0 && entry.Y + entry.Height > document.AtlasHeight))
                    report.Add(SC2ValidationSeverity.Error, "DEC004", "Decals", entry.Id,
                        $"Atlas rect '{entry.Name}' is outside the atlas or has zero size.", "Open Atlas and correct its pixel rectangle.");
            }

            foreach (var placement in document.Placements)
            {
                if (placement == null || !ids.Contains(placement.AtlasEntryId))
                    report.Add(SC2ValidationSeverity.Error, "DEC005", "Decals", placement?.Id,
                        "A decal placement references a missing catalog entry.", "Replace its entry or delete the placement.");
                else if (placement.Width <= 0 || placement.Length <= 0 || placement.ProjectionDepth <= 0)
                    report.Add(SC2ValidationSeverity.Error, "DEC006", "Decals", placement.Id,
                        "A decal placement has non-positive dimensions.", "Set Width, Length and Projection Depth above zero.");
            }

            if (document.HasAuthoredChanges && document.SpatialState != SC2DecalsSpatialState.Current)
                report.Add(SC2ValidationSeverity.Error, "DEC007", "Decals", null,
                    "Edited decals require regenerated spatial data.", "Regenerate decal geometry before export.");

            return report;
        }
    }
}
