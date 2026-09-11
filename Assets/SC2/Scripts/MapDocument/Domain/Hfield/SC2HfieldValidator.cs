namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Only checks
    /// TerrainTypesUInt8 — the one field this document actually populates
    /// (SC2HfieldDocument's own doc comment: "the one field SC2TerrainData
    /// actually persists"); HeightmapUInt16/Projection are known-empty
    /// placeholders with no producer yet, so validating them would just
    /// flag every single document, which isn't useful.
    /// </summary>
    public static class SC2HfieldValidator
    {
        public static SC2ValidationReport Validate(SC2HfieldDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            if (document.TerrainTypesUInt8.IsEmpty)
                report.Add(SC2ValidationSeverity.Error, "HFD001", "Hfield", null,
                    "TerrainTypesUInt8 has no data.",
                    "Repaint or regenerate terrain types before exporting.");

            return report;
        }
    }
}
