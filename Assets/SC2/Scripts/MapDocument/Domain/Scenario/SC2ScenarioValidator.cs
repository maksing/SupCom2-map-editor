namespace SC2.MapEditor.Document
{
    /// <summary>
    /// map/save/script are Errors, not Warnings (unlike
    /// Environment's texture-path checks): these three paths are how the
    /// game engine locates the map's own .scmap/_save.lua/_script.lua files
    /// (see the real ScenarioInfo field taxonomy extracted from the vanilla
    /// corpus during the KopiLua port) — an empty one isn't a cosmetic
    /// issue, the map fails to load. Name is a Warning: cosmetic (map
    /// title), doesn't stop the map from working.
    /// </summary>
    public static class SC2ScenarioValidator
    {
        public static SC2ValidationReport Validate(SC2ScenarioDocument document)
        {
            var report = new SC2ValidationReport();
            if (document == null) return report;

            if (string.IsNullOrWhiteSpace(document.KnownFields.Name))
                report.Add(SC2ValidationSeverity.Warning, "SCN001", "Scenario", null,
                    "Scenario name is empty.",
                    "Set a name for this map.");

            if (string.IsNullOrWhiteSpace(document.KnownFields.MapPath))
                report.Add(SC2ValidationSeverity.Error, "SCN002", "Scenario", null,
                    "ScenarioInfo.map path is empty.",
                    "Set the .scmap path — the game can't locate the map without it.");

            if (string.IsNullOrWhiteSpace(document.KnownFields.SavePath))
                report.Add(SC2ValidationSeverity.Error, "SCN003", "Scenario", null,
                    "ScenarioInfo.save path is empty.",
                    "Set the _save.lua path — the game can't locate marker/prop data without it.");

            if (string.IsNullOrWhiteSpace(document.KnownFields.ScriptPath))
                report.Add(SC2ValidationSeverity.Error, "SCN004", "Scenario", null,
                    "ScenarioInfo.script path is empty.",
                    "Set the _script.lua path — the game can't locate mission logic without it.");

            return report;
        }
    }
}
