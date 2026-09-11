using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;
using BdfIO.Core;
using BdfIO.Formats.Info;
using Hexa.NET.ImGui;

namespace SC2InfoBdfEditorApp;

/// <summary>
/// Draws the entire Info.bdf Editor UI via Dear ImGui — every tab/field
/// mirrors Scripts/Unified/SC2InfoModelFieldDrawer.cs exactly (same 16
/// tabs, same field groupings, same InfoFieldLimits ranges) so this app and
/// the embedded Unity panel never show a different picture of the same
/// file. Immediate-mode by construction, same paradigm as the Unity-side
/// SC2EnvFieldGui.cs this mirrors — every widget below reads the current
/// InfoModel value, draws it, and writes back only if ImGui reports a
/// change, no retained widget objects anywhere.
/// </summary>
public sealed class InfoBdfEditorUi
{
    private InfoModel? _model;
    private InfoModel? _original;
    private string _loadedFilePath = "";
    private BdfPlatform _loadedPlatform = BdfPlatform.Pc;
    private string _status = "Ready.";
    private int _selectedTab;

    // Auto-save: on by default, debounced so dragging a slider doesn't fire
    // hundreds of writes — every edit just resets the timer; the actual
    // write only happens once AutoSaveDebounceSeconds pass with no further
    // edits (e.g. the user lets go of the drag handle / stops typing).
    private const double AutoSaveDebounceSeconds = 0.5;
    private bool _autoSaveEnabled = true;
    private bool _dirty;
    private DateTime _lastEditUtc;

    // Undo/redo: a snapshot-per-edit-burst stack, quantized by the same
    // dirty/clean boundary auto-save already uses — one undo entry per
    // debounce-separated burst of edits, not one per keystroke/drag-frame.
    // _cleanSnapshot always holds a clone of _model as of the last point it
    // was NOT dirty (file just opened/saved/undone/redone/reverted) — using
    // this instead of _model itself for the push means MarkDirty() doesn't
    // care whether it's called before or after the field's own set() call
    // this frame, since _cleanSnapshot is already the correct pre-edit
    // state regardless of mutation order at any individual call site.
    private readonly Stack<InfoModel> _undoStack = new();
    private readonly Stack<InfoModel> _redoStack = new();
    private InfoModel? _cleanSnapshot;
    // Set by Undo/Redo right after they swap _model — forces the VERY NEXT
    // edit to open a fresh undo entry (and correctly clear the redo stack)
    // even though _dirty is already true at that point (Undo/Redo themselves
    // leave the restored state pending a save) — without this, an edit made
    // immediately after an Undo would silently merge into that Undo's own
    // pending-save window and leave a stale, no-longer-valid redo entry
    // sitting in _redoStack instead of being cleared.
    private bool _boundaryPending;

    private void MarkDirty()
    {
        if ((!_dirty || _boundaryPending) && _cleanSnapshot != null)
        {
            _undoStack.Push(_cleanSnapshot);
            _redoStack.Clear();
        }
        _dirty = true;
        _boundaryPending = false;
        _lastEditUtc = DateTime.UtcNow;
    }

    private void RefreshCleanSnapshot()
    {
        _cleanSnapshot = _model != null ? CloneModel(_model) : null;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0 || _model == null) return;
        _redoStack.Push(CloneModel(_model));
        _model = _undoStack.Pop();
        RefreshCleanSnapshot();
        _dirty = true;
        _boundaryPending = true;
        _lastEditUtc = DateTime.UtcNow;
        _status = $"Undo ({_undoStack.Count} more available).";
    }

    private void Redo()
    {
        if (_redoStack.Count == 0 || _model == null) return;
        _undoStack.Push(CloneModel(_model));
        _model = _redoStack.Pop();
        RefreshCleanSnapshot();
        _dirty = true;
        _boundaryPending = true;
        _lastEditUtc = DateTime.UtcNow;
        _status = $"Redo ({_redoStack.Count} more available).";
    }

    /// <summary>Unconditionally opens a fresh undo entry capturing the
    /// EXACT current _model state, regardless of whether an edit is
    /// already pending (_dirty). Used by discrete, deliberate whole-model
    /// replacements (Revert All, Import Values) — unlike a slider drag,
    /// these must never be silently coalesced into whatever ordinary field
    /// edit happened to already be pending, or the in-progress edit's real
    /// value would be unrecoverable via Undo (only the last saved/clean
    /// boundary would be, silently dropping it).</summary>
    private void PushUndoBoundary()
    {
        if (_model != null)
        {
            _undoStack.Push(CloneModel(_model));
            _redoStack.Clear();
        }
        _dirty = true;
        _boundaryPending = true;
        _lastEditUtc = DateTime.UtcNow;
    }

    private void RevertAll()
    {
        if (_model == null || _original == null) return;
        PushUndoBoundary();
        _model = CloneModel(_original);
        _status = "Reverted all fields to the opened state.";
    }

    private void TickAutoSave()
    {
        if (!_autoSaveEnabled || !_dirty || _model == null || string.IsNullOrEmpty(_loadedFilePath)) return;
        if ((DateTime.UtcNow - _lastEditUtc).TotalSeconds < AutoSaveDebounceSeconds) return;
        WriteToPath(_loadedFilePath, isAutoSave: true);
    }

    /// <summary>Set by Program.cs right after construction — used only to
    /// own real Win32 file dialogs (System.Windows.Forms.OpenFileDialog/
    /// SaveFileDialog/ColorDialog remain the simplest way to get genuine
    /// native dialogs; ImGui doesn't need to reinvent a file browser) and
    /// to close the window from the Exit menu item.</summary>
    public Form? Host;

    private static readonly string[] TabTitles =
    {
        "Sun", "Shadow", "Water", "Fog", "Glow", "Bloom",
        "Point Clouds", "Fog of War", "Intel", "Depth of Field",
        "Sharpen", "Render Passes", "Air Elevation", "Wind", "Textures", "Advanced"
    };

    /// <summary>A subtle per-category tint blended into each tab button's
    /// background (unselected tabs only — selected keeps the existing
    /// bright AccentDim highlight) so the 16-tab grid reads as distinct
    /// groups at a glance instead of one flat wall of gray buttons. Order
    /// matches TabTitles exactly.</summary>
    private static readonly Vector3[] TabAccentColors =
    {
        new(1.00f, 0.85f, 0.40f), // Sun — warm gold
        new(0.55f, 0.58f, 0.66f), // Shadow — cool slate
        new(0.30f, 0.62f, 0.92f), // Water — blue
        new(0.62f, 0.67f, 0.74f), // Fog — pale gray-blue
        new(0.85f, 0.50f, 0.90f), // Glow — pink-purple
        new(0.90f, 0.45f, 0.75f), // Bloom — magenta
        new(0.30f, 0.80f, 0.75f), // Point Clouds — teal
        new(0.55f, 0.60f, 0.68f), // Fog of War — slate
        new(0.35f, 0.75f, 0.85f), // Intel — cyan
        new(0.95f, 0.60f, 0.30f), // Depth of Field — orange
        new(0.95f, 0.75f, 0.30f), // Sharpen — amber
        new(0.50f, 0.80f, 0.55f), // Render Passes — green
        new(0.55f, 0.75f, 0.95f), // Air Elevation — sky blue
        new(0.55f, 0.85f, 0.70f), // Wind — mint
        new(0.70f, 0.55f, 0.90f), // Textures — violet
        new(0.90f, 0.55f, 0.40f), // Advanced — warning red-orange
    };

    // Runs once, on the first real render frame (window already created and
    // showing at that point) — deferred out of the constructor because
    // Host isn't assigned until right after construction (Program.cs sets
    // it), and reopening the last file needs a real MessageBox owner ready
    // for the (rare) failure case.
    private bool _startupInitialized;

    private void InitializeFromSettings()
    {
        if (Host != null) Host.FormClosing += (_, _) => SaveSettings();
        var settings = AppSettings.Load();
        if (!string.IsNullOrEmpty(settings.LastOpenedFilePath) && System.IO.File.Exists(settings.LastOpenedFilePath))
            TryOpenPath(settings.LastOpenedFilePath);
    }

    private void SaveSettings()
    {
        if (Host == null) return;
        AppSettings.Save(new AppSettings.Data
        {
            WindowX = Host.Location.X,
            WindowY = Host.Location.Y,
            WindowWidth = Host.Width,
            WindowHeight = Host.Height,
            LastOpenedFilePath = _loadedFilePath
        });
    }

    public void Draw()
    {
        if (!_startupInitialized)
        {
            _startupInitialized = true;
            InitializeFromSettings();
        }

        DrawMenuBar();
        HandleKeyboardShortcuts();
        DrawImportModal();

        if (_model == null)
        {
            ImGui.Dummy(new Vector2(0, 8));
            ImGui.TextColored(Theme.TextSecondary, "File -> Open .bdf... to begin editing a real info.bdf file.");
            ImGui.Dummy(new Vector2(0, 8));
            if (ImGui.Button("Help / How to Use This Editor...")) (Host as MainForm)?.RequestHelpDialog();
        }
        else
        {
            var (tabHasChanges, changedCount) = ComputeChangeSummary();

            ImGui.Text($"{(string.IsNullOrEmpty(_loadedFilePath) ? "(unsaved)" : System.IO.Path.GetFileName(_loadedFilePath))}  (platform={_loadedPlatform})");
            ImGui.SameLine();
            ImGui.Dummy(new Vector2(16, 0));
            ImGui.SameLine();
            ImGui.Checkbox("Auto-save", ref _autoSaveEnabled);
            if (_dirty)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Accent, _autoSaveEnabled ? "(pending...)" : "(unsaved changes)");
            }
            if (changedCount > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextSecondary, $"— {changedCount} field{(changedCount == 1 ? "" : "s")} differ from opened file");
            }
            TickAutoSave();
            ImGui.Dummy(new Vector2(0, 4));
            DrawTabGrid(tabHasChanges);
            ImGui.Separator();
            ImGui.BeginChild("##tabcontent", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), ImGuiChildFlags.None);
            DrawSelectedTab();
            ImGui.EndChild();
        }

        ImGui.Separator();
        ImGui.TextColored(Theme.TextSecondary, _status);

        CheckAutoCloseHelp();
    }

    // ================================================================
    // Menu bar / file actions
    // ================================================================

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMenuBar()) return;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open .bdf...")) OpenFile();
            if (ImGui.MenuItem("Save")) SaveFile();
            if (ImGui.MenuItem("Save As...")) SaveFileAs();
            ImGui.Separator();
            using (Gated(!string.IsNullOrEmpty(_loadedFilePath)))
                if (ImGui.MenuItem("Restore Backup (.bak from when opened)...")) RestoreFromBackup();
            ImGui.Separator();
            if (ImGui.MenuItem("Import Values From...")) OpenImportSourceFile();
            ImGui.Separator();
            if (ImGui.MenuItem("Export for Unity (JSON)...")) ExportJson();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) Host?.Close();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Edit"))
        {
            using (Gated(_undoStack.Count > 0))
                if (ImGui.MenuItem($"Undo ({_undoStack.Count})", "Ctrl+Z")) Undo();
            using (Gated(_redoStack.Count > 0))
                if (ImGui.MenuItem($"Redo ({_redoStack.Count})", "Ctrl+Y")) Redo();
            ImGui.Separator();
            using (Gated(_model != null && _original != null))
                if (ImGui.MenuItem("Revert All to Opened State")) RevertAll();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("How to Use This Editor...")) (Host as MainForm)?.RequestHelpDialog();
            ImGui.EndMenu();
        }
        ImGui.EndMenuBar();
    }

    /// <summary>Global Ctrl+Z/Ctrl+Y (and Ctrl+Shift+Z) shortcuts — skipped
    /// while a text field has keyboard focus so they don't fight ImGui's
    /// own per-widget text-edit undo (InputText/InputFloat already handle
    /// Ctrl+Z locally while focused).</summary>
    private void HandleKeyboardShortcuts()
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput) return;
        if (!io.KeyCtrl) return;

        if (ImGui.IsKeyPressed(ImGuiKey.Z) && !io.KeyShift) Undo();
        else if (ImGui.IsKeyPressed(ImGuiKey.Y) || (ImGui.IsKeyPressed(ImGuiKey.Z) && io.KeyShift)) Redo();
    }

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog { Filter = "info.bdf (*.bdf)|*.bdf|All files (*.*)|*.*", Title = "Open info.bdf" };
        if (dlg.ShowDialog(Host) != DialogResult.OK) return;
        TryOpenPath(dlg.FileName);
    }

    private void TryOpenPath(string path)
    {
        try
        {
            CloseHelp();
            var raw = System.IO.File.ReadAllBytes(path);
            var container = BdfContainer.Read(raw, InfoBdf.Padding);
            var parsed = InfoBdf.Parse(container);

            _model = parsed;
            _original = CloneModel(parsed);
            _loadedFilePath = path;
            _loadedPlatform = parsed.Platform;
            _dirty = false;
            _undoStack.Clear();
            _redoStack.Clear();
            _boundaryPending = false;
            RefreshCleanSnapshot();
            WriteOpenBackup(path, raw);
            _status = $"Opened {System.IO.Path.GetFileName(path)} (platform={_loadedPlatform}). Backup saved as {System.IO.Path.GetFileName(path)}.bak.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Host, $"Failed to open/parse:\n{ex.Message}", "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Best-effort snapshot of the file's exact on-disk bytes at
    /// the moment it was opened — separate from _original (in-memory only,
    /// lost on app restart). This is the "save point at open time" backing
    /// store RestoreFromBackup reads from, so the opened state survives
    /// even across an app crash/restart despite auto-save continuously
    /// overwriting the real file.</summary>
    private static void WriteOpenBackup(string path, byte[] rawBytes)
    {
        try
        {
            System.IO.File.WriteAllBytes(path + ".bak", rawBytes);
        }
        catch
        {
            // Best-effort only — a failed backup write must never block
            // opening the file itself.
        }
    }

    private void RestoreFromBackup()
    {
        if (string.IsNullOrEmpty(_loadedFilePath)) return;
        var backupPath = _loadedFilePath + ".bak";
        if (!System.IO.File.Exists(backupPath))
        {
            MessageBox.Show(Host, "No backup found — one is only written the moment a file is opened via File -> Open.", "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var raw = System.IO.File.ReadAllBytes(backupPath);
            var parsed = InfoBdf.Parse(BdfContainer.Read(raw, InfoBdf.Padding));
            PushUndoBoundary();
            _model = parsed;
            _status = "Restored from the backup taken when this file was opened.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Host, $"Failed to restore backup:\n{ex.Message}", "Restore failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static InfoModel CloneModel(InfoModel src)
    {
        var copy = new InfoModel { Platform = src.Platform };
        Array.Copy(src.Header, copy.Header, 508);
        copy.EnvironmentMap = src.EnvironmentMap;
        copy.SkyMap = src.SkyMap;
        copy.ReservedTexturePath = src.ReservedTexturePath;
        copy.ColorCorrectionCube = src.ColorCorrectionCube;
        copy.FogOfWarCorrectionCube = src.FogOfWarCorrectionCube;
        return copy;
    }

    private static bool ModelsEqual(InfoModel a, InfoModel b)
    {
        for (var i = 0; i < 508; i++) if (a.Header[i] != b.Header[i]) return false;
        return a.EnvironmentMap == b.EnvironmentMap && a.SkyMap == b.SkyMap && a.ReservedTexturePath == b.ReservedTexturePath
               && a.ColorCorrectionCube == b.ColorCorrectionCube && a.FogOfWarCorrectionCube == b.FogOfWarCorrectionCube;
    }

    /// <summary>Whether the ONE field a given ImportFields entry's Copy
    /// action touches differs between the currently open model and the
    /// opened-state original — reused for the tab-grid's per-category
    /// changed-dot (item 6) and the "N pending" count (item 10). Works
    /// generically for any Copy action without needing a separate getter
    /// per field: cloning `model` and then overwriting just this one
    /// field with `original`'s value isolates exactly what that field
    /// alone would change, since Copy touches nothing else.</summary>
    private static bool FieldChanged(InfoModel model, InfoModel original, Action<InfoModel, InfoModel> copy)
    {
        var probe = CloneModel(model);
        copy(original, probe);
        return !ModelsEqual(probe, model);
    }

    private void SaveFile()
    {
        if (_model == null) return;
        if (string.IsNullOrEmpty(_loadedFilePath)) { SaveFileAs(); return; }
        WriteToPath(_loadedFilePath);
    }

    private void SaveFileAs()
    {
        if (_model == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "info.bdf (*.bdf)|*.bdf|All files (*.*)|*.*",
            Title = "Save info.bdf",
            FileName = string.IsNullOrEmpty(_loadedFilePath) ? "info.win.bdf" : System.IO.Path.GetFileName(_loadedFilePath)
        };
        if (dlg.ShowDialog(Host) != DialogResult.OK) return;
        WriteToPath(dlg.FileName);
    }

    private void WriteToPath(string path, bool isAutoSave = false)
    {
        try
        {
            var (data, offsetTable) = InfoBdf.Serialize(_model!, _loadedPlatform);
            var container = new BdfContainer { Platform = _loadedPlatform, Data = data, OffsetTable = offsetTable };
            var bytes = container.Write(InfoBdf.Padding);
            System.IO.File.WriteAllBytes(path, bytes);
            _loadedFilePath = path;
            _dirty = false;
            RefreshCleanSnapshot();
            _status = isAutoSave
                ? $"Auto-saved to {System.IO.Path.GetFileName(path)} at {DateTime.Now:HH:mm:ss}."
                : $"Saved to {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            // Auto-save failures shouldn't interrupt editing with a modal —
            // just surface it in the status line; a manual Save still shows
            // the real dialog so the user notices a genuine problem (e.g.
            // the file got locked/deleted externally) when they explicitly
            // asked to save.
            if (isAutoSave)
                _status = $"Auto-save failed: {ex.Message}";
            else
                MessageBox.Show(Host, $"Failed to save:\n{ex.Message}", "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportJson()
    {
        if (_model == null) return;
        using var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json|All files (*.*)|*.*", Title = "Export for Unity (JSON)", FileName = "info.json" };
        if (dlg.ShowDialog(Host) != DialogResult.OK) return;

        try
        {
            var dto = new InfoBdfJsonDto
            {
                Platform = _loadedPlatform.ToString(),
                HeaderBase64 = Convert.ToBase64String(_model.Header),
                EnvironmentMap = _model.EnvironmentMap,
                SkyMap = _model.SkyMap,
                ReservedTexturePath = _model.ReservedTexturePath,
                ColorCorrectionCube = _model.ColorCorrectionCube,
                FogOfWarCorrectionCube = _model.FogOfWarCorrectionCube
            };
            var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            System.IO.File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(dto, options));
            _status = $"Exported {System.IO.Path.GetFileName(dlg.FileName)} — import it via the Unity panel's \"Import JSON...\" button.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Host, $"Failed to export JSON:\n{ex.Message}", "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ================================================================
    // Import Values From... — copy selected fields out of a DIFFERENT
    // info.bdf into the file currently being edited, at either whole-
    // category granularity ("all of Water") or individual-field
    // granularity ("just WaterElevation"). One entry per UI row here,
    // grouped by category in the same order as TabTitles/Draw*Tab — if a
    // tab's field list ever changes, this table must be updated to match,
    // or an import would silently miss (or wrongly include) a field
    // relative to what the UI actually shows for that category.
    // ================================================================

    private static readonly (string Category, string Label, Action<InfoModel, InfoModel> Copy)[] ImportFields =
    {
        ("Sun", "SunDirection", (src, dst) => { dst.SunDirectionX = src.SunDirectionX; dst.SunDirectionY = src.SunDirectionY; dst.SunDirectionZ = src.SunDirectionZ; }),
        ("Sun", "SunColor", (src, dst) => { dst.SunColorR = src.SunColorR; dst.SunColorG = src.SunColorG; dst.SunColorB = src.SunColorB; }),
        ("Sun", "SunIntensity", (src, dst) => dst.SunIntensity = src.SunIntensity),

        ("Shadow", "ShadowColor", (src, dst) => { dst.ShadowColorR = src.ShadowColorR; dst.ShadowColorG = src.ShadowColorG; dst.ShadowColorB = src.ShadowColorB; dst.ShadowColorA = src.ShadowColorA; }),
        ("Shadow", "ShadowCastDistance", (src, dst) => dst.ShadowCastDistance = src.ShadowCastDistance),

        ("Water", "WaterEnabled", (src, dst) => dst.WaterEnabled = src.WaterEnabled),
        ("Water", "MaxWaterDepth", (src, dst) => dst.MaxWaterDepth = src.MaxWaterDepth),
        ("Water", "WaterElevation", (src, dst) => dst.WaterElevation = src.WaterElevation),
        ("Water", "WaterRenderElevationOverride", (src, dst) => dst.WaterRenderElevationOverride = src.WaterRenderElevationOverride),
        ("Water", "WaterColor", (src, dst) => { dst.WaterColorR = src.WaterColorR; dst.WaterColorG = src.WaterColorG; dst.WaterColorB = src.WaterColorB; dst.WaterColorA = src.WaterColorA; }),
        ("Water", "WaterDiffuse1Movement", (src, dst) => { dst.WaterDiffuse1MovementX = src.WaterDiffuse1MovementX; dst.WaterDiffuse1MovementY = src.WaterDiffuse1MovementY; }),
        ("Water", "WaterDiffuse2Movement", (src, dst) => { dst.WaterDiffuse2MovementX = src.WaterDiffuse2MovementX; dst.WaterDiffuse2MovementY = src.WaterDiffuse2MovementY; }),
        ("Water", "WaterNormal1Movement", (src, dst) => { dst.WaterNormal1MovementX = src.WaterNormal1MovementX; dst.WaterNormal1MovementY = src.WaterNormal1MovementY; }),
        ("Water", "WaterNormal2Movement", (src, dst) => { dst.WaterNormal2MovementX = src.WaterNormal2MovementX; dst.WaterNormal2MovementY = src.WaterNormal2MovementY; }),
        ("Water", "WaterNormal3Movement", (src, dst) => { dst.WaterNormal3MovementX = src.WaterNormal3MovementX; dst.WaterNormal3MovementY = src.WaterNormal3MovementY; }),
        ("Water", "WaterNormal4Movement", (src, dst) => { dst.WaterNormal4MovementX = src.WaterNormal4MovementX; dst.WaterNormal4MovementY = src.WaterNormal4MovementY; }),
        ("Water", "WaterFoamRepeatRate", (src, dst) => { dst.WaterFoamRepeatRateX = src.WaterFoamRepeatRateX; dst.WaterFoamRepeatRateY = src.WaterFoamRepeatRateY; }),
        ("Water", "WaterNormalRepeatRate", (src, dst) => { dst.WaterNormalRepeatRateX = src.WaterNormalRepeatRateX; dst.WaterNormalRepeatRateY = src.WaterNormalRepeatRateY; dst.WaterNormalRepeatRateZ = src.WaterNormalRepeatRateZ; dst.WaterNormalRepeatRateW = src.WaterNormalRepeatRateW; }),
        ("Water", "WaterRefractionScale", (src, dst) => dst.WaterRefractionScale = src.WaterRefractionScale),
        ("Water", "WaterReflectionScale", (src, dst) => dst.WaterReflectionScale = src.WaterReflectionScale),
        ("Water", "WaterChromaticScale", (src, dst) => dst.WaterChromaticScale = src.WaterChromaticScale),
        ("Water", "WaterFresnelBias", (src, dst) => dst.WaterFresnelBias = src.WaterFresnelBias),
        ("Water", "WaterFresnelPower", (src, dst) => dst.WaterFresnelPower = src.WaterFresnelPower),
        ("Water", "WaterDepthScale", (src, dst) => dst.WaterDepthScale = src.WaterDepthScale),
        ("Water", "WaterWhiteCapBrightness", (src, dst) => dst.WaterWhiteCapBrightness = src.WaterWhiteCapBrightness),
        ("Water", "WaterSpecPower", (src, dst) => dst.WaterSpecPower = src.WaterSpecPower),
        ("Water", "WaterBloomFactor", (src, dst) => dst.WaterBloomFactor = src.WaterBloomFactor),
        ("Water", "WaterWhiteCapCutoff", (src, dst) => dst.WaterWhiteCapCutoff = src.WaterWhiteCapCutoff),
        ("Water", "WaterWaveTiming", (src, dst) => dst.WaterWaveTiming = src.WaterWaveTiming),
        ("Water", "WaterWaveModulus", (src, dst) => dst.WaterWaveModulus = src.WaterWaveModulus),
        ("Water", "WaterFoamHeightOffset", (src, dst) => dst.WaterFoamHeightOffset = src.WaterFoamHeightOffset),
        ("Water", "WaterWaveStartDepth", (src, dst) => dst.WaterWaveStartDepth = src.WaterWaveStartDepth),
        ("Water", "WaterDepthOrigin", (src, dst) => { dst.WaterDepthOriginX = src.WaterDepthOriginX; dst.WaterDepthOriginY = src.WaterDepthOriginY; }),
        ("Water", "WaterDepthBoundsSize", (src, dst) => { dst.WaterDepthBoundsSizeX = src.WaterDepthBoundsSizeX; dst.WaterDepthBoundsSizeY = src.WaterDepthBoundsSizeY; }),

        ("Fog", "CoreFogColor", (src, dst) => { dst.CoreFogColorR = src.CoreFogColorR; dst.CoreFogColorG = src.CoreFogColorG; dst.CoreFogColorB = src.CoreFogColorB; dst.CoreFogColorA = src.CoreFogColorA; }),
        ("Fog", "CoreFogAmount1", (src, dst) => dst.CoreFogAmount1 = src.CoreFogAmount1),
        ("Fog", "CoreFogAmount2", (src, dst) => dst.CoreFogAmount2 = src.CoreFogAmount2),
        ("Fog", "CoreFogAmount3", (src, dst) => dst.CoreFogAmount3 = src.CoreFogAmount3),
        ("Fog", "CoreFogDist1", (src, dst) => dst.CoreFogDist1 = src.CoreFogDist1),
        ("Fog", "CoreFogDist2", (src, dst) => dst.CoreFogDist2 = src.CoreFogDist2),
        ("Fog", "HorizonFogColor", (src, dst) => { dst.HorizonFogColorR = src.HorizonFogColorR; dst.HorizonFogColorG = src.HorizonFogColorG; dst.HorizonFogColorB = src.HorizonFogColorB; dst.HorizonFogColorA = src.HorizonFogColorA; }),
        ("Fog", "HorizonFogAmount1", (src, dst) => dst.HorizonFogAmount1 = src.HorizonFogAmount1),
        ("Fog", "HorizonFogAmount2", (src, dst) => dst.HorizonFogAmount2 = src.HorizonFogAmount2),
        ("Fog", "HorizonFogAmount3", (src, dst) => dst.HorizonFogAmount3 = src.HorizonFogAmount3),
        ("Fog", "HorizonFogDist1", (src, dst) => dst.HorizonFogDist1 = src.HorizonFogDist1),
        ("Fog", "HorizonFogDist2", (src, dst) => dst.HorizonFogDist2 = src.HorizonFogDist2),

        ("Glow", "GlowEnabled", (src, dst) => dst.GlowEnabled = src.GlowEnabled),
        ("Glow", "GlowBlurKernelScale0", (src, dst) => dst.GlowBlurKernelScale0 = src.GlowBlurKernelScale0),
        ("Glow", "GlowBlurKernelScale1", (src, dst) => dst.GlowBlurKernelScale1 = src.GlowBlurKernelScale1),
        ("Glow", "GlowBlurPassCount", (src, dst) => dst.GlowBlurPassCount = src.GlowBlurPassCount),
        ("Glow", "GlowBlurFactor0", (src, dst) => dst.GlowBlurFactor0 = src.GlowBlurFactor0),
        ("Glow", "GlowBlurFactor1", (src, dst) => dst.GlowBlurFactor1 = src.GlowBlurFactor1),

        ("Bloom", "BloomEnabled", (src, dst) => dst.BloomEnabled = src.BloomEnabled),
        ("Bloom", "PostProcessSourceWeight", (src, dst) => dst.PostProcessSourceWeight = src.PostProcessSourceWeight),
        ("Bloom", "BloomThreshold", (src, dst) => dst.BloomThreshold = src.BloomThreshold),
        ("Bloom", "BloomDirectionCount", (src, dst) => dst.BloomDirectionCount = src.BloomDirectionCount),
        ("Bloom", "BloomDirectionPhase", (src, dst) => dst.BloomDirectionPhase = src.BloomDirectionPhase),
        ("Bloom", "BloomSpreadDivisor", (src, dst) => dst.BloomSpreadDivisor = src.BloomSpreadDivisor),
        ("Bloom", "BloomSampleScaleX", (src, dst) => dst.BloomSampleScaleX = src.BloomSampleScaleX),
        ("Bloom", "BloomSampleScaleY", (src, dst) => dst.BloomSampleScaleY = src.BloomSampleScaleY),
        ("Bloom", "BloomResultWeight", (src, dst) => dst.BloomResultWeight = src.BloomResultWeight),
        ("Bloom", "BloomPassCount", (src, dst) => dst.BloomPassCount = src.BloomPassCount),

        ("Point Clouds", "PointCloudOrigin", (src, dst) => { dst.PointCloudOriginX = src.PointCloudOriginX; dst.PointCloudOriginY = src.PointCloudOriginY; dst.PointCloudOriginZ = src.PointCloudOriginZ; }),
        ("Point Clouds", "PointCloudSize", (src, dst) => { dst.PointCloudSizeX = src.PointCloudSizeX; dst.PointCloudSizeY = src.PointCloudSizeY; dst.PointCloudSizeZ = src.PointCloudSizeZ; }),
        ("Point Clouds", "PointCloudLightAmount", (src, dst) => dst.PointCloudLightAmount = src.PointCloudLightAmount),

        ("Fog of War", "FogOfWarLowerHeight", (src, dst) => dst.FogOfWarLowerHeight = src.FogOfWarLowerHeight),
        ("Fog of War", "FogOfWarHighestHeight", (src, dst) => dst.FogOfWarHighestHeight = src.FogOfWarHighestHeight),
        ("Fog of War", "FogOfWarHeightStrength", (src, dst) => dst.FogOfWarHeightStrength = src.FogOfWarHeightStrength),

        ("Intel", "IntelLowerHeight", (src, dst) => dst.IntelLowerHeight = src.IntelLowerHeight),
        ("Intel", "IntelHighestHeight", (src, dst) => dst.IntelHighestHeight = src.IntelHighestHeight),
        ("Intel", "IntelHeightStrength", (src, dst) => dst.IntelHeightStrength = src.IntelHeightStrength),

        ("Depth of Field", "DepthOfFieldEnabled", (src, dst) => dst.DepthOfFieldEnabled = src.DepthOfFieldEnabled),
        ("Depth of Field", "DepthOfFieldStart", (src, dst) => dst.DepthOfFieldStart = src.DepthOfFieldStart),
        ("Depth of Field", "DepthOfFieldEnd", (src, dst) => dst.DepthOfFieldEnd = src.DepthOfFieldEnd),

        ("Sharpen", "SharpenStart", (src, dst) => dst.SharpenStart = src.SharpenStart),
        ("Sharpen", "SharpenEnd", (src, dst) => dst.SharpenEnd = src.SharpenEnd),
        ("Sharpen", "SharpenBaseAmount", (src, dst) => dst.SharpenBaseAmount = src.SharpenBaseAmount),
        ("Sharpen", "SharpenNearOffset", (src, dst) => dst.SharpenNearOffset = src.SharpenNearOffset),
        ("Sharpen", "SharpenFarOffset", (src, dst) => dst.SharpenFarOffset = src.SharpenFarOffset),

        ("Render Passes", "RenderPassACEnabled", (src, dst) => dst.RenderPassACEnabled = src.RenderPassACEnabled),
        ("Render Passes", "RenderPassB0Enabled", (src, dst) => dst.RenderPassB0Enabled = src.RenderPassB0Enabled),
        ("Render Passes", "RenderPassB4Enabled", (src, dst) => dst.RenderPassB4Enabled = src.RenderPassB4Enabled),

        ("Air Elevation", "AirElevation", (src, dst) => dst.AirElevation = src.AirElevation),

        ("Wind", "WindDirectionX", (src, dst) => dst.WindDirectionX = src.WindDirectionX),
        ("Wind", "WindDirectionZ", (src, dst) => dst.WindDirectionZ = src.WindDirectionZ),
        ("Wind", "WindSpeed", (src, dst) => dst.WindSpeed = src.WindSpeed),

        ("Textures", "EnvironmentMap", (src, dst) => dst.EnvironmentMap = src.EnvironmentMap),
        ("Textures", "SkyMap", (src, dst) => dst.SkyMap = src.SkyMap),
        ("Textures", "ReservedTexturePath", (src, dst) => dst.ReservedTexturePath = src.ReservedTexturePath),
        ("Textures", "ColorCorrectionCube", (src, dst) => dst.ColorCorrectionCube = src.ColorCorrectionCube),
        ("Textures", "FogOfWarCorrectionCube", (src, dst) => dst.FogOfWarCorrectionCube = src.FogOfWarCorrectionCube),

        ("Advanced", "WaterAuxParameter0", (src, dst) => dst.WaterAuxParameter0 = src.WaterAuxParameter0),
        ("Advanced", "WaterAuxParameter1", (src, dst) => dst.WaterAuxParameter1 = src.WaterAuxParameter1),
        ("Advanced", "ReservedZero236", (src, dst) => dst.ReservedZero236 = src.ReservedZero236),
    };

    private InfoModel? _importSourceModel;
    private string _importSourcePath = "";
    private bool[] _importSelected = Array.Empty<bool>();
    private bool _importModalOpenPending;

    private void OpenImportSourceFile()
    {
        if (_model == null) return;
        using var dlg = new OpenFileDialog { Filter = "info.bdf (*.bdf)|*.bdf|All files (*.*)|*.*", Title = "Import Values From — pick the source info.bdf" };
        if (dlg.ShowDialog(Host) != DialogResult.OK) return;

        try
        {
            var raw = System.IO.File.ReadAllBytes(dlg.FileName);
            _importSourceModel = InfoBdf.Parse(BdfContainer.Read(raw, InfoBdf.Padding));
            _importSourcePath = dlg.FileName;
            _importSelected = new bool[ImportFields.Length];
            _importModalOpenPending = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Host, $"Failed to open/parse source file:\n{ex.Message}", "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DrawImportModal()
    {
        if (_importModalOpenPending)
        {
            ImGui.OpenPopup("Import Values");
            _importModalOpenPending = false;
        }

        ImGui.SetNextWindowSize(new Vector2(460, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Import Values", ImGuiWindowFlags.None)) return;

        ImGui.TextColored(Theme.TextSecondary, $"Source: {System.IO.Path.GetFileName(_importSourcePath)}");
        ImGui.TextColored(Theme.TextSecondary, "Pick individual fields below, or use a category's All/None.");
        ImGui.Dummy(new Vector2(0, 4));

        if (ImGui.Button("Select All")) Array.Fill(_importSelected, true);
        ImGui.SameLine();
        if (ImGui.Button("Select None")) Array.Fill(_importSelected, false);
        ImGui.Separator();

        ImGui.BeginChild("##importfields", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2), ImGuiChildFlags.None);
        var i = 0;
        while (i < ImportFields.Length)
        {
            var category = ImportFields[i].Category;
            var start = i;
            while (i < ImportFields.Length && ImportFields[i].Category == category) i++;
            var end = i;
            var selectedInCategory = 0;
            for (var k = start; k < end; k++) if (_importSelected[k]) selectedInCategory++;

            if (ImGui.CollapsingHeader($"{category} ({selectedInCategory}/{end - start})"))
            {
                ImGui.PushID(category);
                ImGui.Indent();
                if (ImGui.SmallButton("All")) for (var k = start; k < end; k++) _importSelected[k] = true;
                ImGui.SameLine();
                if (ImGui.SmallButton("None")) for (var k = start; k < end; k++) _importSelected[k] = false;
                for (var k = start; k < end; k++)
                {
                    var selected = _importSelected[k];
                    if (ImGui.Checkbox(ImportFields[k].Label, ref selected)) _importSelected[k] = selected;
                }
                ImGui.Unindent();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        ImGui.Separator();
        var anySelected = Array.Exists(_importSelected, x => x);
        using (Gated(anySelected))
            if (ImGui.Button("Import"))
            {
                ApplyImport();
                ImGui.CloseCurrentPopup();
            }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _importSourceModel = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void ApplyImport()
    {
        if (_importSourceModel == null || _model == null) return;
        PushUndoBoundary();
        var count = 0;
        for (var i = 0; i < ImportFields.Length; i++)
        {
            if (!_importSelected[i]) continue;
            ImportFields[i].Copy(_importSourceModel, _model);
            count++;
        }
        _status = $"Imported {count} field{(count == 1 ? "" : "s")} from {System.IO.Path.GetFileName(_importSourcePath)}.";
        _importSourceModel = null;
    }

    // ================================================================
    // Tab strip — explicit 4x4 grid (not ImGui's own tab bar, which
    // scrolls a single row instead of wrapping) and tab dispatch
    // ================================================================

    /// <summary>Which fields (grouped by tab) currently differ from the
    /// opened-state original, and the total count — computed once per
    /// frame and shared between the tab-grid's per-tab dot indicators and
    /// the "N fields differ" status text, rather than walking all ~90
    /// ImportFields entries twice.</summary>
    private (bool[] TabHasChanges, int TotalChanged) ComputeChangeSummary()
    {
        var flags = new bool[TabTitles.Length];
        var total = 0;
        if (_model == null || _original == null) return (flags, 0);

        foreach (var field in ImportFields)
        {
            if (!FieldChanged(_model, _original, field.Copy)) continue;
            total++;
            var tabIndex = Array.IndexOf(TabTitles, field.Category);
            if (tabIndex >= 0) flags[tabIndex] = true;
        }
        return (flags, total);
    }

    private void DrawTabGrid(bool[] tabHasChanges)
    {
        const int columns = 4;
        var avail = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonWidth = (avail - spacing * (columns - 1)) / columns;

        for (var i = 0; i < TabTitles.Length; i++)
        {
            var selected = i == _selectedTab;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.AccentDim);
            }
            else
            {
                var tint = TabAccentColors[i];
                var blended = new Vector4(
                    Theme.Surface.X * 0.82f + tint.X * 0.18f,
                    Theme.Surface.Y * 0.82f + tint.Y * 0.18f,
                    Theme.Surface.Z * 0.82f + tint.Z * 0.18f,
                    1f);
                ImGui.PushStyleColor(ImGuiCol.Button, blended);
            }

            var clicked = ImGui.Button(TabTitles[i], new Vector2(buttonWidth, 0));
            ImGui.PopStyleColor();
            if (clicked) { _selectedTab = i; CloseHelp(); }

            if (tabHasChanges[i])
            {
                var rectMax = ImGui.GetItemRectMax();
                var rectMin = ImGui.GetItemRectMin();
                var dotCenter = new Vector2(rectMax.X - 9f, rectMin.Y + 9f);
                ImGui.GetWindowDrawList().AddCircleFilled(dotCenter, 3.5f, ImGui.GetColorU32(Theme.Accent));
            }

            if ((i + 1) % columns != 0 && i != TabTitles.Length - 1)
                ImGui.SameLine();
        }
    }

    private void DrawSelectedTab()
    {
        switch (_selectedTab)
        {
            case 0: DrawSunTab(); break;
            case 1: DrawShadowTab(); break;
            case 2: DrawWaterTab(); break;
            case 3: DrawFogTab(); break;
            case 4: DrawGlowTab(); break;
            case 5: DrawBloomTab(); break;
            case 6: DrawPointCloudTab(); break;
            case 7: DrawFogOfWarTab(); break;
            case 8: DrawIntelTab(); break;
            case 9: DrawDepthOfFieldTab(); break;
            case 10: DrawSharpenTab(); break;
            case 11: DrawRenderPassesTab(); break;
            case 12: DrawAirElevationTab(); break;
            case 13: DrawWindTab(); break;
            case 14: DrawTexturesTab(); break;
            default: DrawAdvancedTab(); break;
        }
    }

    // ---- Sun ----
    private void DrawSunTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Sun (@4..28)");

        var dirChanged = !ApproxEqual(m.SunDirectionX, o.SunDirectionX) || !ApproxEqual(m.SunDirectionY, o.SunDirectionY) || !ApproxEqual(m.SunDirectionZ, o.SunDirectionZ);
        var dir = new Vector3(m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ);
        LabelRow("SunDirection", dirChanged);
        ImGui.SetNextItemWidth(-40f);
        if (ImGui.InputFloat3("##sundir", ref dir))
        {
            m.SunDirectionX = dir.X; m.SunDirectionY = dir.Y; m.SunDirectionZ = dir.Z;
            MarkDirty();
        }
        if (ResetIconButton("SunDirection", dirChanged))
        {
            m.SunDirectionX = o.SunDirectionX; m.SunDirectionY = o.SunDirectionY; m.SunDirectionZ = o.SunDirectionZ;
            MarkDirty();
        }
        if (ImGui.Button("Normalize"))
        {
            var len = MathF.Sqrt(m.SunDirectionX * m.SunDirectionX + m.SunDirectionY * m.SunDirectionY + m.SunDirectionZ * m.SunDirectionZ);
            if (len > 1e-6f) { m.SunDirectionX /= len; m.SunDirectionY /= len; m.SunDirectionZ /= len; MarkDirty(); }
        }

        ImGui.Dummy(new Vector2(0, 4));
        ColorField3("SunColor (HDR)",
            () => (m.SunColorR, m.SunColorG, m.SunColorB),
            v => { m.SunColorR = v.Item1; m.SunColorG = v.Item2; m.SunColorB = v.Item3; },
            () => (o.SunColorR, o.SunColorG, o.SunColorB));

        ImGui.Dummy(new Vector2(0, 4));
        FloatSlider("SunIntensity", () => m.SunIntensity, v => m.SunIntensity = v,
            InfoFieldLimits.SunIntensity.Min, InfoFieldLimits.SunIntensity.Max, () => o.SunIntensity);
    }

    // ---- Shadow ----
    private void DrawShadowTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("ShadowColor (@32..44)");
        ColorField4("ShadowColor",
            () => (m.ShadowColorR, m.ShadowColorG, m.ShadowColorB, m.ShadowColorA),
            v => { m.ShadowColorR = v.Item1; m.ShadowColorG = v.Item2; m.ShadowColorB = v.Item3; m.ShadowColorA = v.Item4; },
            () => (o.ShadowColorR, o.ShadowColorG, o.ShadowColorB, o.ShadowColorA));

        ImGui.Dummy(new Vector2(0, 4));
        FloatSlider("ShadowCastDistance (@492)", () => m.ShadowCastDistance, v => m.ShadowCastDistance = v,
            InfoFieldLimits.ShadowCastDistance.Min, InfoFieldLimits.ShadowCastDistance.Max, () => o.ShadowCastDistance);
    }

    // ---- Water (merged with the former Water FX tab; 6 real collapsing
    // sections, matching SC2InfoModelFieldDrawer.DrawWaterTab exactly) ----
    private void DrawWaterTab()
    {
        var m = _model!; var o = _original!;
        // Read once, up front — every section below (not just the first
        // one) gates its fields on this, since WaterEnabled is the CPU
        // flag for the whole water render path, not just its elevation.
        var enabled = m.WaterEnabled != 0;

        if (ImGui.CollapsingHeader("Water (@48, @212..220)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            if (ImGui.Checkbox("WaterEnabled", ref enabled)) { m.WaterEnabled = enabled ? 1 : 0; MarkDirty(); }
            ImGui.SameLine();
            HelpButton("WaterEnabled");
            {
                var changed = m.WaterEnabled != o.WaterEnabled;
                if (ResetIconButton("WaterEnabled", changed)) { m.WaterEnabled = o.WaterEnabled; enabled = o.WaterEnabled != 0; MarkDirty(); }
            }
            using (Gated(enabled))
            {
                FloatSlider("MaxWaterDepth", () => m.MaxWaterDepth, v => m.MaxWaterDepth = v, InfoFieldLimits.MaxWaterDepth.Min, InfoFieldLimits.MaxWaterDepth.Max, () => o.MaxWaterDepth);
                FloatSlider("WaterElevation", () => m.WaterElevation, v => m.WaterElevation = v, InfoFieldLimits.WaterElevation.Min, InfoFieldLimits.WaterElevation.Max, () => o.WaterElevation);
                FloatSlider("WaterRenderElevationOverride", () => m.WaterRenderElevationOverride, v => m.WaterRenderElevationOverride = v, InfoFieldLimits.WaterRenderElevationOverride.Min, InfoFieldLimits.WaterRenderElevationOverride.Max, () => o.WaterRenderElevationOverride);
            }
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("WaterColor (@100..112)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            using (Gated(enabled))
            {
                ColorField4("WaterColor",
                    () => (m.WaterColorR, m.WaterColorG, m.WaterColorB, m.WaterColorA),
                    v => { m.WaterColorR = v.Item1; m.WaterColorG = v.Item2; m.WaterColorB = v.Item3; m.WaterColorA = v.Item4; },
                    () => (o.WaterColorR, o.WaterColorG, o.WaterColorB, o.WaterColorA));
            }
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("WaterDiffuse/NormalMovement (@52..96)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            using (Gated(enabled))
            {
                Vec2Field("WaterDiffuse1Movement", () => (m.WaterDiffuse1MovementX, m.WaterDiffuse1MovementY), v => { m.WaterDiffuse1MovementX = v.Item1; m.WaterDiffuse1MovementY = v.Item2; }, () => (o.WaterDiffuse1MovementX, o.WaterDiffuse1MovementY));
                Vec2Field("WaterDiffuse2Movement", () => (m.WaterDiffuse2MovementX, m.WaterDiffuse2MovementY), v => { m.WaterDiffuse2MovementX = v.Item1; m.WaterDiffuse2MovementY = v.Item2; }, () => (o.WaterDiffuse2MovementX, o.WaterDiffuse2MovementY));
                Vec2Field("WaterNormal1Movement", () => (m.WaterNormal1MovementX, m.WaterNormal1MovementY), v => { m.WaterNormal1MovementX = v.Item1; m.WaterNormal1MovementY = v.Item2; }, () => (o.WaterNormal1MovementX, o.WaterNormal1MovementY));
                Vec2Field("WaterNormal2Movement", () => (m.WaterNormal2MovementX, m.WaterNormal2MovementY), v => { m.WaterNormal2MovementX = v.Item1; m.WaterNormal2MovementY = v.Item2; }, () => (o.WaterNormal2MovementX, o.WaterNormal2MovementY));
                Vec2Field("WaterNormal3Movement", () => (m.WaterNormal3MovementX, m.WaterNormal3MovementY), v => { m.WaterNormal3MovementX = v.Item1; m.WaterNormal3MovementY = v.Item2; }, () => (o.WaterNormal3MovementX, o.WaterNormal3MovementY));
                Vec2Field("WaterNormal4Movement", () => (m.WaterNormal4MovementX, m.WaterNormal4MovementY), v => { m.WaterNormal4MovementX = v.Item1; m.WaterNormal4MovementY = v.Item2; }, () => (o.WaterNormal4MovementX, o.WaterNormal4MovementY));
            }
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("WaterFoam/NormalRepeatRate (@116..136)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            using (Gated(enabled))
            {
                Vec2Field("WaterFoamRepeatRate", () => (m.WaterFoamRepeatRateX, m.WaterFoamRepeatRateY), v => { m.WaterFoamRepeatRateX = v.Item1; m.WaterFoamRepeatRateY = v.Item2; }, () => (o.WaterFoamRepeatRateX, o.WaterFoamRepeatRateY));
                var nrChanged = !ApproxEqual(m.WaterNormalRepeatRateX, o.WaterNormalRepeatRateX) || !ApproxEqual(m.WaterNormalRepeatRateY, o.WaterNormalRepeatRateY)
                    || !ApproxEqual(m.WaterNormalRepeatRateZ, o.WaterNormalRepeatRateZ) || !ApproxEqual(m.WaterNormalRepeatRateW, o.WaterNormalRepeatRateW);
                LabelRow("WaterNormalRepeatRate", nrChanged);
                ImGui.SetNextItemWidth(-40f);
                var nr = new Vector4(m.WaterNormalRepeatRateX, m.WaterNormalRepeatRateY, m.WaterNormalRepeatRateZ, m.WaterNormalRepeatRateW);
                if (ImGui.InputFloat4("##waternormalrepeat", ref nr))
                {
                    m.WaterNormalRepeatRateX = nr.X; m.WaterNormalRepeatRateY = nr.Y; m.WaterNormalRepeatRateZ = nr.Z; m.WaterNormalRepeatRateW = nr.W;
                    MarkDirty();
                }
                if (ResetIconButton("WaterNormalRepeatRate", nrChanged))
                {
                    m.WaterNormalRepeatRateX = o.WaterNormalRepeatRateX; m.WaterNormalRepeatRateY = o.WaterNormalRepeatRateY;
                    m.WaterNormalRepeatRateZ = o.WaterNormalRepeatRateZ; m.WaterNormalRepeatRateW = o.WaterNormalRepeatRateW;
                    MarkDirty();
                }
            }
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Water Optics (@140..212)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            using (Gated(enabled))
            {
                FloatSlider("WaterRefractionScale", () => m.WaterRefractionScale, v => m.WaterRefractionScale = v, InfoFieldLimits.WaterRefractionScale.Min, InfoFieldLimits.WaterRefractionScale.Max, () => o.WaterRefractionScale);
                FloatSlider("WaterReflectionScale", () => m.WaterReflectionScale, v => m.WaterReflectionScale = v, InfoFieldLimits.WaterReflectionScale.Min, InfoFieldLimits.WaterReflectionScale.Max, () => o.WaterReflectionScale);
                FloatSlider("WaterChromaticScale", () => m.WaterChromaticScale, v => m.WaterChromaticScale = v, InfoFieldLimits.WaterChromaticScale.Min, InfoFieldLimits.WaterChromaticScale.Max, () => o.WaterChromaticScale);
                FloatSlider("WaterFresnelBias", () => m.WaterFresnelBias, v => m.WaterFresnelBias = v, InfoFieldLimits.WaterFresnelBias.Min, InfoFieldLimits.WaterFresnelBias.Max, () => o.WaterFresnelBias);
                FloatSlider("WaterFresnelPower", () => m.WaterFresnelPower, v => m.WaterFresnelPower = v, InfoFieldLimits.WaterFresnelPower.Min, InfoFieldLimits.WaterFresnelPower.Max, () => o.WaterFresnelPower);
                FloatSlider("WaterDepthScale", () => m.WaterDepthScale, v => m.WaterDepthScale = v, InfoFieldLimits.WaterDepthScale.Min, InfoFieldLimits.WaterDepthScale.Max, () => o.WaterDepthScale);
                FloatSlider("WaterWhiteCapBrightness", () => m.WaterWhiteCapBrightness, v => m.WaterWhiteCapBrightness = v, InfoFieldLimits.WaterWhiteCapBrightness.Min, InfoFieldLimits.WaterWhiteCapBrightness.Max, () => o.WaterWhiteCapBrightness);
                FloatSlider("WaterSpecPower", () => m.WaterSpecPower, v => m.WaterSpecPower = v, InfoFieldLimits.WaterSpecPower.Min, InfoFieldLimits.WaterSpecPower.Max, () => o.WaterSpecPower);
                FloatSlider("WaterBloomFactor", () => m.WaterBloomFactor, v => m.WaterBloomFactor = v, InfoFieldLimits.WaterBloomFactor.Min, InfoFieldLimits.WaterBloomFactor.Max, () => o.WaterBloomFactor);
                FloatSlider("WaterWhiteCapCutoff", () => m.WaterWhiteCapCutoff, v => m.WaterWhiteCapCutoff = v, InfoFieldLimits.WaterWhiteCapCutoff.Min, InfoFieldLimits.WaterWhiteCapCutoff.Max, () => o.WaterWhiteCapCutoff);
                FloatSlider("WaterWaveTiming", () => m.WaterWaveTiming, v => m.WaterWaveTiming = v, InfoFieldLimits.WaterWaveTiming.Min, InfoFieldLimits.WaterWaveTiming.Max, () => o.WaterWaveTiming);
                FloatSlider("WaterWaveModulus", () => m.WaterWaveModulus, v => m.WaterWaveModulus = v, InfoFieldLimits.WaterWaveModulus.Min, InfoFieldLimits.WaterWaveModulus.Max, () => o.WaterWaveModulus);
                FloatSlider("WaterFoamHeightOffset", () => m.WaterFoamHeightOffset, v => m.WaterFoamHeightOffset = v, InfoFieldLimits.WaterFoamHeightOffset.Min, InfoFieldLimits.WaterFoamHeightOffset.Max, () => o.WaterFoamHeightOffset);
                FloatSlider("WaterWaveStartDepth", () => m.WaterWaveStartDepth, v => m.WaterWaveStartDepth = v, InfoFieldLimits.WaterWaveStartDepth.Min, InfoFieldLimits.WaterWaveStartDepth.Max, () => o.WaterWaveStartDepth);
            }
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("WaterDepthOrigin / WaterDepthBoundsSize (@196..208)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            using (Gated(enabled))
            {
                Vec2Field("WaterDepthOrigin", () => (m.WaterDepthOriginX, m.WaterDepthOriginY), v => { m.WaterDepthOriginX = v.Item1; m.WaterDepthOriginY = v.Item2; }, () => (o.WaterDepthOriginX, o.WaterDepthOriginY));
                Vec2Field("WaterDepthBoundsSize", () => (m.WaterDepthBoundsSizeX, m.WaterDepthBoundsSizeY), v => { m.WaterDepthBoundsSizeX = v.Item1; m.WaterDepthBoundsSizeY = v.Item2; }, () => (o.WaterDepthBoundsSizeX, o.WaterDepthBoundsSizeY));
            }
            ImGui.Unindent();
        }
    }

    // ---- Fog: 2 real collapsing sections (Core/Horizon) ----
    private void DrawFogTab()
    {
        var m = _model!; var o = _original!;

        if (ImGui.CollapsingHeader("Core (@260..296)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            ColorField4("CoreFogColor", () => (m.CoreFogColorR, m.CoreFogColorG, m.CoreFogColorB, m.CoreFogColorA),
                v => { m.CoreFogColorR = v.Item1; m.CoreFogColorG = v.Item2; m.CoreFogColorB = v.Item3; m.CoreFogColorA = v.Item4; },
                () => (o.CoreFogColorR, o.CoreFogColorG, o.CoreFogColorB, o.CoreFogColorA));
            FloatSlider("CoreFogAmount1", () => m.CoreFogAmount1, v => m.CoreFogAmount1 = v, InfoFieldLimits.CoreFogAmount1.Min, InfoFieldLimits.CoreFogAmount1.Max, () => o.CoreFogAmount1);
            FloatSlider("CoreFogAmount2", () => m.CoreFogAmount2, v => m.CoreFogAmount2 = v, InfoFieldLimits.CoreFogAmount2.Min, InfoFieldLimits.CoreFogAmount2.Max, () => o.CoreFogAmount2);
            FloatSlider("CoreFogAmount3", () => m.CoreFogAmount3, v => m.CoreFogAmount3 = v, InfoFieldLimits.CoreFogAmount3.Min, InfoFieldLimits.CoreFogAmount3.Max, () => o.CoreFogAmount3);
            FloatSlider("CoreFogDist1", () => m.CoreFogDist1, v => m.CoreFogDist1 = v, InfoFieldLimits.CoreFogDist1.Min, InfoFieldLimits.CoreFogDist1.Max, () => o.CoreFogDist1);
            FloatSlider("CoreFogDist2", () => m.CoreFogDist2, v => m.CoreFogDist2 = v, InfoFieldLimits.CoreFogDist2.Min, InfoFieldLimits.CoreFogDist2.Max, () => o.CoreFogDist2);
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Horizon (@296..328)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            ColorField4("HorizonFogColor", () => (m.HorizonFogColorR, m.HorizonFogColorG, m.HorizonFogColorB, m.HorizonFogColorA),
                v => { m.HorizonFogColorR = v.Item1; m.HorizonFogColorG = v.Item2; m.HorizonFogColorB = v.Item3; m.HorizonFogColorA = v.Item4; },
                () => (o.HorizonFogColorR, o.HorizonFogColorG, o.HorizonFogColorB, o.HorizonFogColorA));
            FloatSlider("HorizonFogAmount1", () => m.HorizonFogAmount1, v => m.HorizonFogAmount1 = v, InfoFieldLimits.HorizonFogAmount1.Min, InfoFieldLimits.HorizonFogAmount1.Max, () => o.HorizonFogAmount1);
            FloatSlider("HorizonFogAmount2", () => m.HorizonFogAmount2, v => m.HorizonFogAmount2 = v, InfoFieldLimits.HorizonFogAmount2.Min, InfoFieldLimits.HorizonFogAmount2.Max, () => o.HorizonFogAmount2);
            FloatSlider("HorizonFogAmount3", () => m.HorizonFogAmount3, v => m.HorizonFogAmount3 = v, InfoFieldLimits.HorizonFogAmount3.Min, InfoFieldLimits.HorizonFogAmount3.Max, () => o.HorizonFogAmount3);
            FloatSlider("HorizonFogDist1", () => m.HorizonFogDist1, v => m.HorizonFogDist1 = v, InfoFieldLimits.HorizonFogDist1.Min, InfoFieldLimits.HorizonFogDist1.Max, () => o.HorizonFogDist1);
            FloatSlider("HorizonFogDist2", () => m.HorizonFogDist2, v => m.HorizonFogDist2 = v, InfoFieldLimits.HorizonFogDist2.Min, InfoFieldLimits.HorizonFogDist2.Max, () => o.HorizonFogDist2);
            ImGui.Unindent();
        }
    }

    // ---- Glow ----
    private void DrawGlowTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Glow (@356..380)");
        var enabled = m.GlowEnabled != 0;
        if (ImGui.Checkbox("GlowEnabled", ref enabled)) { m.GlowEnabled = enabled ? 1 : 0; MarkDirty(); }
        ImGui.SameLine();
        HelpButton("GlowEnabled");
        {
            var changed = m.GlowEnabled != o.GlowEnabled;
            if (ResetIconButton("GlowEnabled", changed)) { m.GlowEnabled = o.GlowEnabled; enabled = o.GlowEnabled != 0; MarkDirty(); }
        }
        using (Gated(enabled))
        {
            FloatSlider("GlowBlurKernelScale0", () => m.GlowBlurKernelScale0, v => m.GlowBlurKernelScale0 = v, InfoFieldLimits.GlowBlurKernelScale0.Min, InfoFieldLimits.GlowBlurKernelScale0.Max, () => o.GlowBlurKernelScale0);
            FloatSlider("GlowBlurKernelScale1", () => m.GlowBlurKernelScale1, v => m.GlowBlurKernelScale1 = v, InfoFieldLimits.GlowBlurKernelScale1.Min, InfoFieldLimits.GlowBlurKernelScale1.Max, () => o.GlowBlurKernelScale1);
            IntSlider("GlowBlurPassCount", () => m.GlowBlurPassCount, v => m.GlowBlurPassCount = v, InfoFieldLimits.GlowBlurPassCount.Min, InfoFieldLimits.GlowBlurPassCount.Max, () => o.GlowBlurPassCount);
            FloatSlider("GlowBlurFactor0", () => m.GlowBlurFactor0, v => m.GlowBlurFactor0 = v, InfoFieldLimits.GlowBlurFactor0.Min, InfoFieldLimits.GlowBlurFactor0.Max, () => o.GlowBlurFactor0);
            FloatSlider("GlowBlurFactor1", () => m.GlowBlurFactor1, v => m.GlowBlurFactor1 = v, InfoFieldLimits.GlowBlurFactor1.Min, InfoFieldLimits.GlowBlurFactor1.Max, () => o.GlowBlurFactor1);
        }
    }

    // ---- Bloom ----
    private void DrawBloomTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Bloom (@368..416)");
        var enabled = m.BloomEnabled != 0;
        if (ImGui.Checkbox("BloomEnabled", ref enabled)) { m.BloomEnabled = enabled ? 1 : 0; MarkDirty(); }
        ImGui.SameLine();
        HelpButton("BloomEnabled");
        {
            var changed = m.BloomEnabled != o.BloomEnabled;
            if (ResetIconButton("BloomEnabled", changed)) { m.BloomEnabled = o.BloomEnabled; enabled = o.BloomEnabled != 0; MarkDirty(); }
        }
        using (Gated(enabled))
        {
            FloatSlider("PostProcessSourceWeight", () => m.PostProcessSourceWeight, v => m.PostProcessSourceWeight = v, InfoFieldLimits.PostProcessSourceWeight.Min, InfoFieldLimits.PostProcessSourceWeight.Max, () => o.PostProcessSourceWeight);
            FloatSlider("BloomThreshold", () => m.BloomThreshold, v => m.BloomThreshold = v, InfoFieldLimits.BloomThreshold.Min, InfoFieldLimits.BloomThreshold.Max, () => o.BloomThreshold);
            IntSlider("BloomDirectionCount", () => m.BloomDirectionCount, v => m.BloomDirectionCount = v, InfoFieldLimits.BloomDirectionCount.Min, InfoFieldLimits.BloomDirectionCount.Max, () => o.BloomDirectionCount);
            FloatSlider("BloomDirectionPhase", () => m.BloomDirectionPhase, v => m.BloomDirectionPhase = v, InfoFieldLimits.BloomDirectionPhase.Min, InfoFieldLimits.BloomDirectionPhase.Max, () => o.BloomDirectionPhase);
            FloatSlider("BloomSpreadDivisor", () => m.BloomSpreadDivisor, v => m.BloomSpreadDivisor = v, InfoFieldLimits.BloomSpreadDivisor.Min, InfoFieldLimits.BloomSpreadDivisor.Max, () => o.BloomSpreadDivisor);
            FloatSlider("BloomSampleScaleX", () => m.BloomSampleScaleX, v => m.BloomSampleScaleX = v, InfoFieldLimits.BloomSampleScaleX.Min, InfoFieldLimits.BloomSampleScaleX.Max, () => o.BloomSampleScaleX);
            FloatSlider("BloomSampleScaleY", () => m.BloomSampleScaleY, v => m.BloomSampleScaleY = v, InfoFieldLimits.BloomSampleScaleY.Min, InfoFieldLimits.BloomSampleScaleY.Max, () => o.BloomSampleScaleY);
            FloatSlider("BloomResultWeight", () => m.BloomResultWeight, v => m.BloomResultWeight = v, InfoFieldLimits.BloomResultWeight.Min, InfoFieldLimits.BloomResultWeight.Max, () => o.BloomResultWeight);
            IntSlider("BloomPassCount", () => m.BloomPassCount, v => m.BloomPassCount = v, InfoFieldLimits.BloomPassCount.Min, InfoFieldLimits.BloomPassCount.Max, () => o.BloomPassCount);
        }
    }

    // ---- Point Clouds ----
    private void DrawPointCloudTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("PointCloud (@332..352)");
        Vec3Field("PointCloudOrigin", () => (m.PointCloudOriginX, m.PointCloudOriginY, m.PointCloudOriginZ),
            v => { m.PointCloudOriginX = v.Item1; m.PointCloudOriginY = v.Item2; m.PointCloudOriginZ = v.Item3; },
            () => (o.PointCloudOriginX, o.PointCloudOriginY, o.PointCloudOriginZ));
        Vec3Field("PointCloudSize", () => (m.PointCloudSizeX, m.PointCloudSizeY, m.PointCloudSizeZ),
            v => { m.PointCloudSizeX = v.Item1; m.PointCloudSizeY = v.Item2; m.PointCloudSizeZ = v.Item3; },
            () => (o.PointCloudSizeX, o.PointCloudSizeY, o.PointCloudSizeZ));
        FloatSlider("PointCloudLightAmount", () => m.PointCloudLightAmount, v => m.PointCloudLightAmount = v, InfoFieldLimits.PointCloudLightAmount.Min, InfoFieldLimits.PointCloudLightAmount.Max, () => o.PointCloudLightAmount);
    }

    // ---- Fog of War ----
    private void DrawFogOfWarTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Fog of War (@424..432)");
        FloatSlider("FogOfWarLowerHeight", () => m.FogOfWarLowerHeight, v => m.FogOfWarLowerHeight = v, InfoFieldLimits.FogOfWarLowerHeight.Min, InfoFieldLimits.FogOfWarLowerHeight.Max, () => o.FogOfWarLowerHeight);
        FloatSlider("FogOfWarHighestHeight", () => m.FogOfWarHighestHeight, v => m.FogOfWarHighestHeight = v, InfoFieldLimits.FogOfWarHighestHeight.Min, InfoFieldLimits.FogOfWarHighestHeight.Max, () => o.FogOfWarHighestHeight);
        FloatSlider("FogOfWarHeightStrength", () => m.FogOfWarHeightStrength, v => m.FogOfWarHeightStrength = v, InfoFieldLimits.FogOfWarHeightStrength.Min, InfoFieldLimits.FogOfWarHeightStrength.Max, () => o.FogOfWarHeightStrength);
    }

    // ---- Intel ----
    private void DrawIntelTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Intel (@436..444)");
        FloatSlider("IntelLowerHeight", () => m.IntelLowerHeight, v => m.IntelLowerHeight = v, InfoFieldLimits.IntelLowerHeight.Min, InfoFieldLimits.IntelLowerHeight.Max, () => o.IntelLowerHeight);
        FloatSlider("IntelHighestHeight", () => m.IntelHighestHeight, v => m.IntelHighestHeight = v, InfoFieldLimits.IntelHighestHeight.Min, InfoFieldLimits.IntelHighestHeight.Max, () => o.IntelHighestHeight);
        FloatSlider("IntelHeightStrength", () => m.IntelHeightStrength, v => m.IntelHeightStrength = v, InfoFieldLimits.IntelHeightStrength.Min, InfoFieldLimits.IntelHeightStrength.Max, () => o.IntelHeightStrength);
    }

    // ---- Depth of Field ----
    private void DrawDepthOfFieldTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("DepthOfField (@448..456)");
        var enabled = m.DepthOfFieldEnabled != 0;
        if (ImGui.Checkbox("DepthOfFieldEnabled", ref enabled)) { m.DepthOfFieldEnabled = enabled ? 1 : 0; MarkDirty(); }
        ImGui.SameLine();
        HelpButton("DepthOfFieldEnabled");
        {
            var changed = m.DepthOfFieldEnabled != o.DepthOfFieldEnabled;
            if (ResetIconButton("DepthOfFieldEnabled", changed)) { m.DepthOfFieldEnabled = o.DepthOfFieldEnabled; enabled = o.DepthOfFieldEnabled != 0; MarkDirty(); }
        }
        using (Gated(enabled))
        {
            FloatSlider("DepthOfFieldStart", () => m.DepthOfFieldStart, v => m.DepthOfFieldStart = v, InfoFieldLimits.DepthOfFieldStart.Min, InfoFieldLimits.DepthOfFieldStart.Max, () => o.DepthOfFieldStart);
            FloatSlider("DepthOfFieldEnd", () => m.DepthOfFieldEnd, v => m.DepthOfFieldEnd = v, InfoFieldLimits.DepthOfFieldEnd.Min, InfoFieldLimits.DepthOfFieldEnd.Max, () => o.DepthOfFieldEnd);
        }
    }

    // ---- Sharpen ----
    private void DrawSharpenTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Sharpen (@460..476)");
        FloatSlider("SharpenStart", () => m.SharpenStart, v => m.SharpenStart = v, InfoFieldLimits.SharpenStart.Min, InfoFieldLimits.SharpenStart.Max, () => o.SharpenStart);
        FloatSlider("SharpenEnd", () => m.SharpenEnd, v => m.SharpenEnd = v, InfoFieldLimits.SharpenEnd.Min, InfoFieldLimits.SharpenEnd.Max, () => o.SharpenEnd);
        FloatSlider("SharpenBaseAmount", () => m.SharpenBaseAmount, v => m.SharpenBaseAmount = v, InfoFieldLimits.SharpenBaseAmount.Min, InfoFieldLimits.SharpenBaseAmount.Max, () => o.SharpenBaseAmount);
        FloatSlider("SharpenNearOffset", () => m.SharpenNearOffset, v => m.SharpenNearOffset = v, InfoFieldLimits.SharpenNearOffset.Min, InfoFieldLimits.SharpenNearOffset.Max, () => o.SharpenNearOffset);
        FloatSlider("SharpenFarOffset", () => m.SharpenFarOffset, v => m.SharpenFarOffset = v, InfoFieldLimits.SharpenFarOffset.Min, InfoFieldLimits.SharpenFarOffset.Max, () => o.SharpenFarOffset);
    }

    // ---- Render Passes ----
    private void DrawRenderPassesTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Render Passes (@480..488)");
        Toggle("RenderPassACEnabled", () => m.RenderPassACEnabled, v => m.RenderPassACEnabled = v, () => o.RenderPassACEnabled);
        Toggle("RenderPassB0Enabled", () => m.RenderPassB0Enabled, v => m.RenderPassB0Enabled = v, () => o.RenderPassB0Enabled);
        Toggle("RenderPassB4Enabled", () => m.RenderPassB4Enabled, v => m.RenderPassB4Enabled = v, () => o.RenderPassB4Enabled);
    }

    // ---- Air Elevation ----
    private void DrawAirElevationTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("AirElevation (@232)");
        FloatSlider("AirElevation", () => m.AirElevation, v => m.AirElevation = v, InfoFieldLimits.AirElevation.Min, InfoFieldLimits.AirElevation.Max, () => o.AirElevation);
    }

    // ---- Wind ----
    private void DrawWindTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Wind (@496..504)");
        FloatSlider("WindDirectionX", () => m.WindDirectionX, v => m.WindDirectionX = v, InfoFieldLimits.WindDirectionX.Min, InfoFieldLimits.WindDirectionX.Max, () => o.WindDirectionX);
        FloatSlider("WindDirectionZ", () => m.WindDirectionZ, v => m.WindDirectionZ = v, InfoFieldLimits.WindDirectionZ.Min, InfoFieldLimits.WindDirectionZ.Max, () => o.WindDirectionZ);
        FloatSlider("WindSpeed", () => m.WindSpeed, v => m.WindSpeed = v, InfoFieldLimits.WindSpeed.Min, InfoFieldLimits.WindSpeed.Max, () => o.WindSpeed);
    }

    // ---- Textures ----
    private void DrawTexturesTab()
    {
        var m = _model!; var o = _original!;
        SectionLabel("Texture Paths (@240..256)");
        ImGui.TextColored(Theme.TextSecondary, "No live preview here (this app has no Unity scene) — import the JSON export into Unity to resolve/preview them there.");
        TextureField("EnvironmentMap", () => m.EnvironmentMap, v => m.EnvironmentMap = v, () => o.EnvironmentMap);
        TextureField("SkyMap", () => m.SkyMap, v => m.SkyMap = v, () => o.SkyMap);
        TextureField("ReservedTexturePath", () => m.ReservedTexturePath, v => m.ReservedTexturePath = v, () => o.ReservedTexturePath);
        TextureField("ColorCorrectionCube", () => m.ColorCorrectionCube, v => m.ColorCorrectionCube = v, () => o.ColorCorrectionCube);
        TextureField("FogOfWarCorrectionCube", () => m.FogOfWarCorrectionCube, v => m.FogOfWarCorrectionCube = v, () => o.FogOfWarCorrectionCube);
    }

    // ---- Advanced (was "Unverified") ----
    private void DrawAdvancedTab()
    {
        var m = _model!; var o = _original!;
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Advanced — effect not confirmed");
        FloatSlider("WaterAuxParameter0 (@224)", () => m.WaterAuxParameter0, v => m.WaterAuxParameter0 = v, InfoFieldLimits.WaterAuxParameter0.Min, InfoFieldLimits.WaterAuxParameter0.Max, () => o.WaterAuxParameter0);
        FloatSlider("WaterAuxParameter1 (@228)", () => m.WaterAuxParameter1, v => m.WaterAuxParameter1 = v, InfoFieldLimits.WaterAuxParameter1.Min, InfoFieldLimits.WaterAuxParameter1.Max, () => o.WaterAuxParameter1);
        FloatSlider("ReservedZero236 (@236, zero on all 56 PC maps)", () => m.ReservedZero236, v => m.ReservedZero236 = v, InfoFieldLimits.ReservedZero236.Min, InfoFieldLimits.ReservedZero236.Max, () => o.ReservedZero236);
    }

    // ================================================================
    // Field-drawing helpers — reusable across every tab above. ImGui's
    // SliderFloat/SliderInt never clamp a manually-typed value by default
    // (only the drag handle itself is bounded to [min,max], via
    // ImGuiSliderFlags_AlwaysClamp NOT being set) — matching the same
    // "soft bounds" contract Unity's own SC2EnvFieldGui.Slider deliberately
    // keeps, with zero extra code needed on this side.
    // ================================================================

    private static bool ApproxEqual(float a, float b) => MathF.Abs(a - b) < 1e-5f;

    private static void SectionLabel(string text)
    {
        ImGui.TextColored(Theme.TextPrimary, text);
        ImGui.Dummy(new Vector2(0, 2));
    }

    // ================================================================
    // Field hover tooltips, sourced from the
    // reverse-engineering audit's mapping.csv (copied alongside the exe as
    // FieldDescriptions.json at build time; see the .csproj). A handful of
    // this app's UI labels combine several mapping.csv rows into one
    // widget (e.g. CoreFogAmount1/2/3 are three sliders here but one
    // "CoreFogAmounts" row there) or use a slightly different name
    // (WaterDepthOrigin vs. WaterDepthOriginXZ) — DescriptionAliases maps
    // those cases; anything not listed falls back to an exact name match,
    // and anything with neither just shows no tooltip (not every field
    // needs one).
    // ================================================================

    private static readonly Dictionary<string, string> DescriptionAliases = new()
    {
        ["WaterDepthOrigin"] = "WaterDepthOriginXZ",
        ["WaterDepthBoundsSize"] = "WaterDepthBoundsSizeXZ",
        ["CoreFogAmount1"] = "CoreFogAmounts",
        ["CoreFogAmount2"] = "CoreFogAmounts",
        ["CoreFogAmount3"] = "CoreFogAmounts",
        ["HorizonFogAmount1"] = "HorizonFogAmounts",
        ["HorizonFogAmount2"] = "HorizonFogAmounts",
        ["HorizonFogAmount3"] = "HorizonFogAmounts",
        ["CoreFogDist1"] = "CoreFogDistances",
        ["CoreFogDist2"] = "CoreFogDistances",
        ["HorizonFogDist1"] = "HorizonFogDistances",
        ["HorizonFogDist2"] = "HorizonFogDistances",
    };

    private static readonly Dictionary<string, string> Descriptions = LoadDescriptions();

    private static Dictionary<string, string> LoadDescriptions()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "FieldDescriptions.json");
            if (!System.IO.File.Exists(path)) return new Dictionary<string, string>();
            var json = System.IO.File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, options)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            // Tooltips are a nicety — a malformed/missing data file must
            // never stop the editor itself from opening.
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Which field's help popup is currently open (matches the
    /// exact <c>label</c> string passed to the owning HelpButton call), or
    /// null if none is. Only one can be open at a time — there is only one
    /// underlying native tooltip window (MainForm._nativeTooltip).</summary>
    private string? _openHelpKey;

    /// <summary>Screen-space bounds of the currently-open field's own "?"
    /// button, refreshed every frame that field is drawn — the tooltip
    /// should stay open while the cursor is over this button too, not
    /// just over the tooltip itself (otherwise it would auto-close the
    /// instant it's shown, since the cursor is on the button, not yet on
    /// the tooltip, at the moment of the click). See CheckAutoCloseHelp.</summary>
    private System.Drawing.Rectangle _openHelpButtonScreenRect;

    private void CloseHelp()
    {
        if (_openHelpKey == null) return;
        _openHelpKey = null;
        (Host as MainForm)?.HideNativeTooltip();
    }

    /// <summary>Auto-closes the open help popup once the cursor has moved
    /// away from both it and the "?" button that opened it — called once
    /// per frame from Draw(). A native ToolTip has no MouseLeave-style
    /// event of its own to hook (it isn't an ImGui widget this app can
    /// call IsItemHovered() on), so this polls Cursor.Position instead.</summary>
    private void CheckAutoCloseHelp()
    {
        if (_openHelpKey == null) return;
        if (Host is MainForm mf && mf.ShouldAutoCloseTooltip(_openHelpButtonScreenRect)) CloseHelp();
    }

    /// <summary>Builds the help text for a field — its description,
    /// then (for numeric fields) the slider's valid range —
    /// or null if there's nothing to show. <paramref name="label"/> may
    /// carry a trailing "(@offset...)" comment (several DrawXxxTab call
    /// sites already do, e.g. "ShadowCastDistance (@492)") — stripped
    /// before lookup since mapping.csv's own names never include it.</summary>
    private static string? BuildHelpText(string label, float? min, float? max)
    {
        var parenIndex = label.IndexOf('(');
        var cleanLabel = (parenIndex >= 0 ? label[..parenIndex] : label).Trim();
        var key = DescriptionAliases.TryGetValue(cleanLabel, out var alias) ? alias : cleanLabel;
        var hasDescription = Descriptions.TryGetValue(key, out var desc);
        if (!hasDescription && min == null) return null;

        var sb = new System.Text.StringBuilder();
        if (hasDescription && !string.IsNullOrEmpty(desc))
            sb.AppendLine(desc);
        if (min != null && max != null)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"Range: {min:0.###} - {max:0.###}");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    /// <summary>Draws a small "?" button that shows field help — its
    /// description, then the numeric range — via a real
    /// native Win32 tooltip (routed through MainForm.ShowNativeTooltip)
    /// instead of ImGui.SetTooltip, since this app's Dear ImGui build has
    /// no working custom-font path (three independent attempts at loading
    /// a bigger font all rendered a blank window; see
    /// MainForm.cs's own comment).
    ///
    /// Deliberately click-triggered, not hover-triggered — an earlier
    /// hover-based version called ShowNativeTooltip() on every single
    /// frame while the mouse sat over the field, which made the tooltip
    /// visibly flicker (each call re-triggers the native window's
    /// show/paint cycle) and, since it followed the mouse, could extend
    /// past the app window's own edge for a long description. Clicking
    /// calls Show() exactly once, anchored to the button itself (clamped
    /// to stay inside the window by MainForm.ShowNativeTooltip). It hides
    /// again on any of: clicking the same "?" a second time, clicking a
    /// different one, switching tabs, or — per CheckAutoCloseHelp, called
    /// once per frame from Draw() — moving the cursor away from both the
    /// tooltip and this button.</summary>
    private void HelpButton(string label, float? min = null, float? max = null)
    {
        var text = BuildHelpText(label, min, max);
        if (text == null) return;

        ImGui.PushID(label);
        ImGui.PushID("##help");
        var isOpen = _openHelpKey == label;
        if (isOpen) ImGui.PushStyleColor(ImGuiCol.Button, Theme.AccentDim);
        var frameHeight = ImGui.GetFrameHeight();
        var clicked = ImGui.Button("?", new Vector2(frameHeight, frameHeight));
        if (isOpen) ImGui.PopStyleColor();

        var btnMin = ImGui.GetItemRectMin();
        var btnMax = ImGui.GetItemRectMax();
        // Keep the tracked "stay open" rect fresh every frame this field's
        // button is actually drawn (handles scrolling/tab redraws moving
        // it), not just at the moment it was clicked.
        if (isOpen) UpdateOpenHelpButtonRect(btnMin, btnMax);

        if (clicked)
        {
            if (isOpen)
            {
                CloseHelp();
            }
            else
            {
                _openHelpKey = label;
                UpdateOpenHelpButtonRect(btnMin, btnMax);
                (Host as MainForm)?.ShowNativeTooltip(text, (int)btnMin.X, (int)btnMax.Y + 4);
            }
        }
        ImGui.PopID();
        ImGui.PopID();
    }

    private void UpdateOpenHelpButtonRect(Vector2 min, Vector2 max)
    {
        if (Host is not MainForm mf) return;
        var screenMin = mf.PointToScreen(new System.Drawing.Point((int)min.X, (int)min.Y));
        _openHelpButtonScreenRect = new System.Drawing.Rectangle(screenMin.X, screenMin.Y, (int)(max.X - min.X), (int)(max.Y - min.Y));
    }

    /// <summary>Draws a field's label followed immediately by its "?" help
    /// button on the same line — the shared shape every field-drawing
    /// helper below uses for its label row.</summary>
    private void LabelRow(string label, bool changed, float? min = null, float? max = null)
    {
        ImGui.TextColored(LabelColor(changed), label);
        ImGui.SameLine();
        HelpButton(label, min, max);
    }

    /// <summary>Draws a compact circular-arrow reset icon in place of a
    /// text "Reset" button — same click/enabled-state semantics (disabled
    /// and grayed out when the field already matches its original value),
    /// just far less visually noisy repeated ~90 times across every tab.
    /// Drawn as vector shapes over a real ImGui.Button (for its background/
    /// hover/disabled styling and click handling) rather than as a text
    /// glyph, since this build's font doesn't carry a dedicated icon
    /// glyph range.</summary>
    private static bool ResetIconButton(string id, bool enabled)
    {
        ImGui.SameLine();
        ImGui.PushID(id);
        ImGui.PushID("##reset");
        bool clicked;
        using (Gated(enabled))
        {
            var frameHeight = ImGui.GetFrameHeight();
            clicked = ImGui.Button("##reseticon", new Vector2(frameHeight, frameHeight));

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var center = (min + max) * 0.5f;
            var radius = frameHeight * 0.26f;
            var color = ImGui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled);
            var drawList = ImGui.GetWindowDrawList();

            const float startAngle = -0.3f;
            const float endAngle = 4.9f; // ~300 degrees — leaves a gap for the arrowhead, reading as a "reset/refresh" loop
            drawList.PathArcTo(center, radius, startAngle, endAngle, 12);
            drawList.PathStroke(color, 1.6f);

            var tip = center + new Vector2(MathF.Cos(endAngle), MathF.Sin(endAngle)) * radius;
            var tangent = new Vector2(-MathF.Sin(endAngle), MathF.Cos(endAngle));
            var normal = new Vector2(MathF.Cos(endAngle), MathF.Sin(endAngle));
            drawList.AddTriangleFilled(tip + tangent * 3.2f, tip - tangent * 3.2f, tip + normal * 4.2f, color);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset to opened value");
        ImGui.PopID();
        ImGui.PopID();
        return clicked;
    }

    /// <summary>ImGui.BeginDisabled/EndDisabled scope — mirrors the source
    /// drawer's `GUI.enabled = m.XEnabled != 0` pattern (dependent fields
    /// stay visible but disabled when off, never hidden).</summary>
    private static IDisposable Gated(bool enabled) => new DisabledScope(!enabled);

    private readonly struct DisabledScope : IDisposable
    {
        private readonly bool _disabled;
        public DisabledScope(bool disabled) { _disabled = disabled; if (disabled) ImGui.BeginDisabled(); }
        public void Dispose() { if (_disabled) ImGui.EndDisabled(); }
    }

    private static Vector4 LabelColor(bool changed) => changed ? Theme.Accent : Theme.TextSecondary;

    private void FloatSlider(string label, Func<float> get, Action<float> set, float min, float max, Func<float>? original = null)
    {
        ImGui.PushID(label);
        var changed = original != null && !ApproxEqual(get(), original());
        LabelRow(label, changed, min, max);
        var value = get();
        ImGui.SetNextItemWidth(original != null ? -40f : -1f);
        if (ImGui.SliderFloat("##v", ref value, min, max)) { set(value); MarkDirty(); }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    private void IntSlider(string label, Func<int> get, Action<int> set, int min, int max, Func<int>? original = null)
    {
        ImGui.PushID(label);
        var changed = original != null && get() != original();
        LabelRow(label, changed, min, max);
        var value = get();
        ImGui.SetNextItemWidth(original != null ? -40f : -1f);
        if (ImGui.SliderInt("##v", ref value, min, Math.Max(min + 1, max))) { set(value); MarkDirty(); }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    private void Toggle(string label, Func<int> get, Action<int> set, Func<int>? original = null)
    {
        var value = get() != 0;
        if (ImGui.Checkbox(label, ref value)) { set(value ? 1 : 0); MarkDirty(); }
        ImGui.SameLine();
        HelpButton(label);
        if (original != null)
        {
            var changed = get() != original();
            if (ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        }
    }

    private void Vec2Field(string label, Func<(float, float)> get, Action<(float, float)> set, Func<(float, float)>? original = null)
    {
        ImGui.PushID(label);
        bool changed;
        if (original != null) { var (x0, y0) = get(); var (ox0, oy0) = original(); changed = !ApproxEqual(x0, ox0) || !ApproxEqual(y0, oy0); }
        else changed = false;
        LabelRow(label, changed);
        var (x, y) = get();
        var v = new Vector2(x, y);
        ImGui.SetNextItemWidth(original != null ? -40f : -1f);
        if (ImGui.InputFloat2("##v", ref v)) { set((v.X, v.Y)); MarkDirty(); }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    private void Vec3Field(string label, Func<(float, float, float)> get, Action<(float, float, float)> set, Func<(float, float, float)>? original = null)
    {
        ImGui.PushID(label);
        bool changed;
        if (original != null) { var (x0, y0, z0) = get(); var (ox0, oy0, oz0) = original(); changed = !ApproxEqual(x0, ox0) || !ApproxEqual(y0, oy0) || !ApproxEqual(z0, oz0); }
        else changed = false;
        LabelRow(label, changed);
        var (x, y, z) = get();
        var v = new Vector3(x, y, z);
        ImGui.SetNextItemWidth(original != null ? -40f : -1f);
        if (ImGui.InputFloat3("##v", ref v)) { set((v.X, v.Y, v.Z)); MarkDirty(); }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    private void ColorField3(string label, Func<(float, float, float)> get, Action<(float, float, float)> set, Func<(float, float, float)>? original = null)
    {
        ImGui.PushID(label);
        bool changed;
        if (original != null) { var (r0, g0, b0) = get(); var (or0, og0, ob0) = original(); changed = !ApproxEqual(r0, or0) || !ApproxEqual(g0, og0) || !ApproxEqual(b0, ob0); }
        else changed = false;
        LabelRow(label, changed);
        var (r, g, b) = get();
        var v = new Vector3(r, g, b);
        if (ImGui.ColorEdit3("##v", ref v, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr)) { set((v.X, v.Y, v.Z)); MarkDirty(); }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    private void ColorField4(string label, Func<(float, float, float, float)> get, Action<(float, float, float, float)> set, Func<(float, float, float, float)>? original = null)
    {
        ImGui.PushID(label);
        bool changed;
        if (original != null) { var (r0, g0, b0, a0) = get(); var (or0, og0, ob0, oa0) = original(); changed = !ApproxEqual(r0, or0) || !ApproxEqual(g0, og0) || !ApproxEqual(b0, ob0) || !ApproxEqual(a0, oa0); }
        else changed = false;
        LabelRow(label, changed);
        var (r, g, b, a) = get();
        var v = new Vector4(r, g, b, a);
        if (ImGui.ColorEdit4("##v", ref v, ImGuiColorEditFlags.Float)) { set((v.X, v.Y, v.Z, v.W)); MarkDirty(); }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    /// <summary>Text field for a texture path, plus a "Browse..." button that opens a
    /// real Windows file picker and auto-fills the field with the game's
    /// own relative-path convention, derived from wherever the picked file
    /// sits under a real "*.scd" folder on disk (the established
    /// already-extracted-archive convention this codebase uses elsewhere —
    /// see ComputeBdfTexturePath's own comment for the exact rule).</summary>
    private void TextureField(string label, Func<string> get, Action<string> set, Func<string>? original = null)
    {
        ImGui.PushID(label);
        var changed = original != null && get() != original();
        LabelRow(label, changed);
        var text = get();
        // Reserve exactly what "Browse..." (and, if present, the reset
        // icon) actually measure at the CURRENT font/scale rather than a
        // guessed constant — a hardcoded reservation here previously left
        // the reset icon rendering past the window's right edge once the
        // real button widths exceeded the guess.
        var style = ImGui.GetStyle();
        var browseWidth = ImGui.CalcTextSize("Browse...").X + style.FramePadding.X * 2f;
        var resetWidth = original != null ? ImGui.GetFrameHeight() + style.ItemSpacing.X : 0f;
        var reserved = browseWidth + style.ItemSpacing.X + resetWidth;
        ImGui.SetNextItemWidth(-reserved);
        if (ImGui.InputText("##v", ref text, 260)) { set(text); MarkDirty(); }
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "DDS textures (*.dds)|*.dds|All files (*.*)|*.*",
                Title = $"Select {label}"
            };
            if (dlg.ShowDialog(Host) == DialogResult.OK)
            {
                var bdfPath = ComputeBdfTexturePath(dlg.FileName);
                set(bdfPath);
                MarkDirty();
            }
        }
        if (original != null && ResetIconButton(label, changed)) { set(original!()); MarkDirty(); }
        ImGui.PopID();
    }

    /// <summary>Converts an absolute path picked from disk into the game's
    /// own texture-path convention: relative to (and starting right after)
    /// the nearest ancestor folder whose name ends in ".scd" — matching the
    /// already-established "point this tool at your manually-extracted
    /// gamedata.scd folder" convention used elsewhere in this codebase
    /// (SC2GameAssetDiscovery) — with forward slashes, a single leading
    /// slash, and the real ".win" mid-filename marker always stripped
    /// (real .bdf texture paths never carry it, whether or not the source
    /// file on disk happens to have it).
    /// e.g. ".../gamedata.scd/textures/environment/C01_001_env.win.dds"
    ///   -> "/textures/environment/C01_001_env.dds"</summary>
    private static string ComputeBdfTexturePath(string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/');
        var parts = normalized.Split('/');

        var scdIndex = -1;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) { scdIndex = i; break; }
        }

        var relativeParts = scdIndex >= 0 ? parts[(scdIndex + 1)..] : new[] { parts[^1] };
        var fileName = relativeParts[^1];

        var ext = System.IO.Path.GetExtension(fileName);
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith(".win", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        relativeParts[^1] = stem + ext;

        return "/" + string.Join('/', relativeParts);
    }
}
