namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Deliberately minimal: EnvironmentMap and SkyMap are the
    /// two texture paths SC2EnvironmentShell's own "Textures" tab lists
    /// first, and a missing/blank value there would visibly break rendering.
    /// The other three (TexturePath3, ColorCorrectionCube,
    /// FogOfWarCorrectionCube) look optional in the legacy data — no
    /// evidence collected yet that every real map sets them — so they're
    /// left unchecked rather than guessed at as required.
    /// </summary>
    public static class SC2EnvironmentValidator
    {
        public static SC2ValidationReport Validate(SC2EnvironmentDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            if (string.IsNullOrWhiteSpace(document.EnvironmentMap))
                report.Add(SC2ValidationSeverity.Warning, "ENV001", "Environment", null,
                    "EnvironmentMap texture path is empty.",
                    "Set a valid environment map path in the Textures tab.");

            if (string.IsNullOrWhiteSpace(document.SkyMap))
                report.Add(SC2ValidationSeverity.Warning, "ENV002", "Environment", null,
                    "SkyMap texture path is empty.",
                    "Set a valid sky map path in the Textures tab.");

            return report;
        }
    }
}
