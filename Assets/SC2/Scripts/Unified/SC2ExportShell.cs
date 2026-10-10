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
            SC2TerrainShell.DrawUnifiedNote("Create/load a terrain first (Terrain tab) — Export writes the procedural map to real .bdf/.lua files.");
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
        _lastValidationDomainNames = new[] { "Terrain", "Hfield", "Costs", "Collision2", "Objects", "Environment", "Scenario", "Decals" };
        _lastValidationReports = new[]
        {
            SC2TerrainValidator.Validate(doc.Terrain),
            SC2HfieldValidator.Validate(doc.Hfield),
            SC2CostsValidator.Validate(doc.Costs),
            SC2CollisionValidator.Validate(doc.Collision2),
            SC2ObjectsValidator.Validate(doc.Objects),
            SC2EnvironmentValidator.Validate(doc.Environment),
            SC2ScenarioValidator.Validate(doc.Scenario),
            SC2DecalsValidator.Validate(doc.Decals),
        };
        _lastValidationTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        RepaintHost?.Invoke();
    }
}
