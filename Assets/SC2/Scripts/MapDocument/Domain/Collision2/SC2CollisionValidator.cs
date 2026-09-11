namespace SC2.MapEditor.Document
{
    /// <summary>
    /// A hard "&lt;= 2500 polygons" constraint here
    /// turned out to be the vanilla-observed convention, not an actual
    /// binary format ceiling — Collision2Bdf.Serialize writes vertex/
    /// triangle counts as plain UInt32 fields with no fixed-size limit.
    /// SC2Collision2Shell now exposes a real, user-adjustable budget slider
    /// (2500-10000; see SC2TerrainData.Collision2PolygonBudget), so this
    /// check's ceiling was raised to match rather than flagging a
    /// deliberately-configured value as an Error.
    /// </summary>
    public static class SC2CollisionValidator
    {
        public const int MaxAllowedPolygons = 10000;

        public static SC2ValidationReport Validate(SC2CollisionDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            if (document.Generation.MaxPolygons > MaxAllowedPolygons)
                report.Add(SC2ValidationSeverity.Error, "COL001", "Collision2", null,
                    $"MaxPolygons ({document.Generation.MaxPolygons}) exceeds the {MaxAllowedPolygons}-polygon limit.",
                    $"Reduce MaxPolygons to {MaxAllowedPolygons} or fewer.");

            return report;
        }
    }
}
