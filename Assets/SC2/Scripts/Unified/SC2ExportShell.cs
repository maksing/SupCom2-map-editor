using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>
/// "Export" top-level tab (SC2GameViewEditorUI.DrawMainPanel, case 6) with
/// the sub-tabs of SC2EditorTabs.ExportTabs ("Export"/"Validation"). Sibling of
/// SC2ScenarioShell/SC2EnvironmentShell — same "lives in Scripts/Unified so it
/// compiles for both the Editor and a real .exe build" reasoning.
///
/// Sub-tab 0 ("Export") is SC2ExportPanel. Sub-tab 1 ("Validation") runs the
/// 8 domain validators. The Smart Export path itself (SC2TerrainExporter's
/// ExportCorrectSmart / PreviewSmartExportPlan) has no entry in the UI.
/// </summary>
public sealed class SC2ExportShell
{
    private readonly SC2TerrainShell _terrainShell;
    private readonly SC2MapSession _session;
    public System.Action RepaintHost;

    /// <summary>The problems the Objects tab lists (set by the editor, which owns that tab); they are validated with the rest.</summary>
    public System.Func<System.Collections.Generic.IReadOnlyList<string>> ObjectsIssues;

    private SC2TerrainData Data => _terrainShell?.Data;
    public bool IsReady => Data != null && Data.IsInitialized;

    private SC2ExportPanel _exportPanel;
    private SC2TerrainData _exportPanelForData;

    private SC2ValidationReport[] _lastValidationReports;
    private string[] _lastValidationDomainNames;
    private long _lastValidationTimestamp;

    public SC2ExportShell(SC2TerrainShell terrainShell, SC2MapSession session)
    {
        _terrainShell = terrainShell;
        _session = session;
    }

    public void DrawEmbeddedGUI(float panelWidth, int subTab)
    {
        // See SC2TerrainShell.SyncEffectiveViewWidth's own doc comment — keeps
        // GetUnifiedContentWidth/DrawUnifiedSegmentGrid sizing against THIS
        // panel's own current width instead of whatever another tab last cached.
        SC2TerrainShell.SyncEffectiveViewWidth(panelWidth);
        var w = Mathf.Max(1f, panelWidth);
        GUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));

        if (!IsReady)
        {
            SC2TerrainShell.DrawUnifiedNote("Create or load a map first (Terrain tab). Export writes the map as the game's .bdf and .lua files.");
            GUILayout.EndVertical();
            return;
        }

        switch (subTab)
        {
            case 1: DrawValidationTab(w); break;
            default: DrawExportTab(); break;
        }

        GUILayout.EndVertical();
    }

    private SC2ExportPanel ExportPanel()
    {
        if (_exportPanel == null || _exportPanelForData != Data)
        {
            _exportPanel = new SC2ExportPanel(_terrainShell, Data);
            _exportPanelForData = Data;
        }
        return _exportPanel;
    }

    private void DrawExportTab() => ExportPanel().DrawPanel();

    /// <summary>The Export hotkey: the same export as the Export tab's Export button (same output folder
    /// and options), from any tab. Returns false when there is nothing to export or an earlier export's
    /// minimap render is still running.</summary>
    public bool RunExport(System.Action<string> onFinished)
    {
        if (!IsReady) return false;
        return ExportPanel().RunExport(onFinished);
    }

    /// <summary>Advisory-only per the approved plan — Errors get a banner
    /// but never disable the Export tab's buttons. Re-syncs live
    /// SC2TerrainData into the document first (same call every shell's own
    /// Export/Save path already uses) so validation always runs against
    /// current data, not whatever the document last happened to hold.</summary>
    private void DrawValidationTab(float w)
    {
        SC2EnvFieldGui.SectionLabel("Validation");
        SC2TerrainShell.DrawUnifiedNote("Advisory only — issues found here do not block the export. Re-run after making changes.");

        if (SC2EnvFieldGui.Button("Run Validation"))
            RunValidation();

        if (_lastValidationReports == null)
        {
            SC2TerrainShell.DrawUnifiedNote("Validation hasn't run yet this session.");
            return;
        }

        var totalErrors = 0;
        var totalWarnings = 0;
        for (var i = 0; i < _lastValidationReports.Length; i++)
        {
            var report = _lastValidationReports[i];
            if (report == null) continue;
            foreach (var m in report.Messages)
            {
                if (m.Severity == SC2ValidationSeverity.Error) totalErrors++;
                else if (m.Severity == SC2ValidationSeverity.Warning) totalWarnings++;
            }
        }

        GUILayout.Space(4);
        if (totalErrors > 0)
            SC2TerrainShell.DrawUnifiedNote($"⚠ {totalErrors} blocking issue(s) found — see below.");
        else
            SC2TerrainShell.DrawUnifiedNote($"No blocking issues. {totalWarnings} warning(s).");

        for (var i = 0; i < _lastValidationReports.Length; i++)
        {
            var report = _lastValidationReports[i];
            if (report == null || report.Messages.Count == 0) continue;
            SC2EnvFieldGui.SectionLabel(_lastValidationDomainNames[i]);
            SC2TerrainShell.BeginUnifiedPanelBox();
            foreach (var m in report.Messages)
            {
                var prefix = m.Severity == SC2ValidationSeverity.Error ? "✖ " : m.Severity == SC2ValidationSeverity.Warning ? "⚠ " : "· ";
                var line = prefix + m.Message;
                if (!string.IsNullOrEmpty(m.SuggestedAction)) line += "\n  → " + m.SuggestedAction;
                SC2TerrainShell.DrawUnifiedNote(line);
            }
            SC2TerrainShell.EndUnifiedPanelBox();
        }
    }

    /// <summary>What the Objects tab lists as problems, as a report: a Mass marker that cannot take the extractor is a warning, a
    /// note about the checks that could not run is information, everything else (a missing start position, a broken reference) an error.</summary>
    private SC2ValidationReport ObjectsTabReport()
    {
        var report = new SC2ValidationReport();
        var issues = ObjectsIssues?.Invoke();
        if (issues == null) return report;
        foreach (var text in issues)
        {
            if (text.StartsWith("Note:", System.StringComparison.Ordinal))
                report.Add(SC2ValidationSeverity.Info, "objects.checks.pending", "Objects", null, text);
            else if (text.StartsWith("Mass marker", System.StringComparison.Ordinal))
                report.Add(SC2ValidationSeverity.Warning, "objects.mass.not_buildable", "Objects", null, text);
            else
                report.Add(SC2ValidationSeverity.Error, "objects.structure", "Objects", null, text);
        }
        return report;
    }

    // Findings about values the export fills in itself, so an empty one in the editor is not a problem: the map's own paths in
    // _scenario.lua (made from the Map Name), the default terrain types, and the environment and sky textures the Environment tab
    // leaves to the game when they are empty.
    private static readonly System.Collections.Generic.HashSet<string> ExportWritesItself =
        new System.Collections.Generic.HashSet<string> { "SCN002", "SCN003", "SCN004", "HFD001", "ENV001", "ENV002" };

    private void RunValidation()
    {
        _terrainShell.SaveTerrainState(writeAssets: false);
        if (_session?.Document == null)
        {
            _lastValidationReports = System.Array.Empty<SC2ValidationReport>();
            _lastValidationDomainNames = System.Array.Empty<string>();
            return;
        }

        var doc = _session.Document;
        _lastValidationDomainNames = new[] { "Terrain", "Hfield", "Costs", "Collision2", "Objects", "Objects (map checks)", "Environment", "Scenario", "Decals" };
        _lastValidationReports = new[]
        {
            SC2TerrainValidator.Validate(doc.Terrain),
            SC2HfieldValidator.Validate(doc.Hfield),
            SC2CostsValidator.Validate(doc.Costs),
            SC2CollisionValidator.Validate(doc.Collision2),
            SC2ObjectsValidator.Validate(doc.Objects),
            ObjectsTabReport(),
            SC2EnvironmentValidator.Validate(doc.Environment),
            SC2ScenarioValidator.Validate(doc.Scenario),
            SC2DecalsValidator.Validate(doc.Decals),
        };
        foreach (var report in _lastValidationReports)
            report?.Messages.RemoveAll(m => ExportWritesItself.Contains(m.Code));
        _lastValidationTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        RepaintHost?.Invoke();
    }
}
