using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Info;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Standalone info.bdf reference/preview editor — opens ANY .bdf file from
/// disk, independent of whatever map is currently loaded (unlike the
/// embedded Environment tab, which only ever edits the active map's own
/// SC2TerrainData.EnvironmentHeader). Lets you compare a real vanilla map's
/// lighting against your current scene before deciding to use it, or just
/// tweak a file directly.
///
/// Must work identically in the Editor Game view AND a real standalone
/// Build &amp; Run player (the user's explicit requirement — a real Unity
/// EditorWindow can never run inside a compiled Player, full stop, so this
/// is a plain GUILayout overlay panel hosted inside SC2GameViewEditorUI's
/// own canvas instead, same pattern as SC2MapImportPanel/SC2TextureBrowser).
/// File-picking uses EditorUtility.OpenFilePanel in the Editor and
/// SC2RuntimeFileDialog's native Win32 dialogs in Build, mirroring
/// SC2MapImportPanel.PickFile/PickFolder exactly. The live-preview debounce
/// uses Time.realtimeSinceStartupAsDouble (not EditorApplication.update),
/// matching SC2TextureBrowser's search-debounce pattern, since this needs
/// to work with no Editor-only ticking mechanism available.
///
/// Field drawing itself is delegated to SC2InfoModelFieldDrawer so the ~80
/// Sun/Water/Fog/Glow/... fields aren't duplicated a second time here.
/// </summary>
public sealed class SC2InfoBdfEditorPanel
{
    public Action RepaintHost;
    public Action<string> ReportStatus;
    public bool IsOpen;

    /// <summary>Always asks for the CURRENT shell rather than capturing one
    /// at construction time — the active terrain can be replaced (new/loaded
    /// map) after this panel already exists.</summary>
    private readonly Func<SC2TerrainShell> _getTerrainShell;

    // Debounce: apply the live preview only once edits have been quiet for
    // this long, not on every single GUI event - dragging a color swatch
    // fires many changes per second, and re-applying scene lighting/fog on
    // every one of them would mean 100+ updates while dragging.
    private const double PreviewDebounceSeconds = 0.2;

    private InfoModel _m;
    private InfoModel _original;
    private readonly SC2InfoModelFieldDrawer _drawer = new();

    private string _loadedFilePath = "";
    private BdfPlatform _loadedPlatform = BdfPlatform.Pc;

    private byte[] _lastPreviewedHeader;
    private double _previewReadyAt;
    private bool _previewDirty;

    private GameObject _previewHost;
    private SC2Environment _previewEnv;

    private int _subTab;
    private Vector2 _scroll;
    private string _status = "";

    public SC2InfoBdfEditorPanel(Func<SC2TerrainShell> getTerrainShell)
    {
        _getTerrainShell = getTerrainShell ?? throw new ArgumentNullException(nameof(getTerrainShell));
    }

    public void Open() => IsOpen = true;

    public void DrawOverlay(int screenWidth, int screenHeight)
    {
        if (!IsOpen) return;

        // Debounced live preview - checked every frame this overlay draws
        // (i.e. every OnGUI call, in both the Editor Game view and a real
        // Build & Run player), not via EditorApplication.update (Editor-only,
        // wouldn't fire in Build at all).
        if (_previewDirty && Time.realtimeSinceStartupAsDouble >= _previewReadyAt)
        {
            ApplyLivePreview();
            _previewDirty = false;
        }

        var panelWidth = Mathf.Min(560f, screenWidth - 40f);
        var panelHeight = Mathf.Min(620f, screenHeight - 80f);
        var rect = new Rect((screenWidth - panelWidth) * 0.5f, (screenHeight - panelHeight) * 0.5f, panelWidth, panelHeight);

        var prevDepth = GUI.depth;
        GUI.depth = -2500;

        var prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(new Rect(0, 0, screenWidth, screenHeight), Texture2D.whiteTexture);
        GUI.color = prevColor;

        GUI.Box(rect, GUIContent.none);
        GUILayout.BeginArea(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f));

        GUILayout.BeginHorizontal();
        GUILayout.Label("Info.bdf Editor");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("X", GUILayout.Width(SC2UiScale.Pxf(26f)), GUILayout.Height(SC2UiScale.Pxf(22f))))
            IsOpen = false;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Open info.bdf...", GUILayout.Width(SC2UiScale.Pxf(140f)))) PickFile();
        if (GUILayout.Button("Import JSON...", GUILayout.Width(SC2UiScale.Pxf(140f)))) PickJsonFile();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Open Info.bdf Editor (external)", GUILayout.Height(SC2UiScale.Pxf(24f))))
            LaunchExternalEditor();
        GUILayout.EndHorizontal();
        GUILayout.Label("The external editor is a genuinely independent window (own taskbar icon) — edit there, then File > Export for Unity (JSON)... and import the result here with \"Import JSON...\" above.");

        if (_m == null)
        {
            GUILayout.Space(6);
            GUILayout.Label("Open an info.bdf file (or import a JSON export from the external editor) to begin. Live preview applies Sun light + fog to the current scene (with a short debounce) as you edit; Water fields are not live-previewed - nothing in this project applies Water live yet, only at real export time.");
        }
        else
        {
            GUILayout.Label($"File: {(_loadedFilePath == "" ? "(unsaved)" : _loadedFilePath)}");
            GUILayout.Label($"Version = {_m.Version}   Platform = {_loadedPlatform}");

            _subTab = GUILayout.Toolbar(_subTab, SC2EditorTabs.EnvironmentTabs);

            var before = (byte[])_m.Header.Clone();
            _scroll = GUILayout.BeginScrollView(_scroll);
            // Indices match SC2EditorTabs.EnvironmentTabs exactly — see that
            // array's own comment for the full rationale. Keep this switch's
            // case order in lockstep with
            // SC2EnvironmentShell.DrawEmbeddedGUI's matching switch.
            switch (_subTab)
            {
                case 0: _drawer.DrawSunTab(_m); break;
                case 1: _drawer.DrawShadowTab(_m); break;
                case 2: _drawer.DrawWaterTab(_m); break;
                case 3: _drawer.DrawFogTab(_m); break;
                case 4: _drawer.DrawGlowTab(_m); break;
                case 5: _drawer.DrawBloomTab(_m); break;
                case 6: _drawer.DrawPointCloudTab(_m); break;
                case 7: _drawer.DrawFogOfWarTab(_m); break;
                case 8: _drawer.DrawIntelTab(_m); break;
                case 9: _drawer.DrawDepthOfFieldTab(_m); break;
                case 10: _drawer.DrawSharpenTab(_m); break;
                case 11: _drawer.DrawRenderPassesTab(_m); break;
                case 12: _drawer.DrawAirElevationTab(_m); break;
                case 13: _drawer.DrawWindTab(_m); break;
                case 14: _drawer.DrawTexturesTab(_m); break;
                default: _drawer.DrawUnverifiedTab(_m); break;
            }
            GUILayout.EndScrollView();

            if (!HeaderEquals(before, _m.Header))
            {
                _previewDirty = true;
                _previewReadyAt = Time.realtimeSinceStartupAsDouble + PreviewDebounceSeconds;
            }

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);

            if (GUILayout.Button("⟲ Reset All", GUILayout.Height(SC2UiScale.Pxf(22f))))
            {
                Array.Copy(_original.Header, _m.Header, 508);
                _m.EnvironmentMap = _original.EnvironmentMap;
                _m.SkyMap = _original.SkyMap;
                _m.ReservedTexturePath = _original.ReservedTexturePath;
                _m.ColorCorrectionCube = _original.ColorCorrectionCube;
                _m.FogOfWarCorrectionCube = _original.FogOfWarCorrectionCube;
                _previewDirty = true;
                _previewReadyAt = Time.realtimeSinceStartupAsDouble + PreviewDebounceSeconds;
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save to Map", GUILayout.Height(SC2UiScale.Pxf(26f)))) SaveToActiveMap();
            if (GUILayout.Button("Save to File", GUILayout.Height(SC2UiScale.Pxf(26f)))) SaveToFile();
            GUILayout.EndHorizontal();
        }

        GUILayout.EndArea();
        GUI.depth = prevDepth;

        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && !rect.Contains(e.mousePosition))
            e.Use(); // swallow clicks outside the panel instead of closing it
    }

    private void PickFile()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Open info.bdf", "", "bdf");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Open info.bdf", "info.bdf (*.bdf)", "bdf");
#endif
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var raw = File.ReadAllBytes(path);
            var container = BdfContainer.Read(raw, InfoBdf.Padding);
            var parsed = InfoBdf.Parse(container);

            _m = parsed;
            _original = new InfoModel { Platform = parsed.Platform };
            Array.Copy(parsed.Header, _original.Header, 508);
            _original.EnvironmentMap = parsed.EnvironmentMap;
            _original.SkyMap = parsed.SkyMap;
            _original.ReservedTexturePath = parsed.ReservedTexturePath;
            _original.ColorCorrectionCube = parsed.ColorCorrectionCube;
            _original.FogOfWarCorrectionCube = parsed.FogOfWarCorrectionCube;
            _drawer.Original = _original;

            _loadedFilePath = path;
            _loadedPlatform = parsed.Platform;
            _lastPreviewedHeader = null;
            _status = $"Opened {Path.GetFileName(path)}.";
            ReportStatus?.Invoke(_status);

            _previewDirty = true;
            _previewReadyAt = Time.realtimeSinceStartupAsDouble + PreviewDebounceSeconds;
            RepaintHost?.Invoke();
        }
        catch (Exception ex)
        {
            _status = $"Failed to open/parse: {ex.Message}";
            ReportStatus?.Invoke(_status);
        }
    }

    /// <summary>Launches the standalone Info.bdf Editor (Dear ImGui over
    /// Direct3D 11, see Tools~/InfoBdfEditor's own header comments — not
    /// WinForms controls, just a WinForms Form used to obtain a real HWND)
    /// (Tools~/InfoBdfEditor/SC2InfoBdfEditorApp.csproj, published as a
    /// self-contained single-file exe) as a genuinely separate OS process —
    /// a real independent window with its own taskbar icon, unlike this
    /// panel (a GUILayout overlay glued to the same window as the rest of
    /// the game view). Not automated by Unity's own build pipeline: publish
    /// the exe (`dotnet publish -c Release -r win-x64 --self-contained
    /// -p:PublishSingleFile=true`) and copy it next to the Build's own .exe
    /// under an "InfoBdfEditor" subfolder as a separate manual step (see
    /// that project's own header comment). Never crashes if missing - just
    /// reports a clear status message.</summary>
    private void LaunchExternalEditor()
    {
        try
        {
            var exeDir = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            var exePath = Path.Combine(exeDir, "InfoBdfEditor", "SC2InfoBdfEditorApp.exe");
            if (!File.Exists(exePath))
            {
                _status = $"External editor not found at '{exePath}'. Publish Tools~/InfoBdfEditor and copy the exe there (see that project's README/header comment).";
                ReportStatus?.Invoke(_status);
                return;
            }
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            _status = "Launched external Info.bdf Editor.";
            ReportStatus?.Invoke(_status);
        }
        catch (Exception ex)
        {
            _status = $"Failed to launch external editor: {ex.Message}";
            ReportStatus?.Invoke(_status);
        }
    }

    /// <summary>Imports a JSON export produced by the external editor's
    /// File > Export for Unity (JSON)... (InfoBdfJsonDto — same file
    /// compiled into both this Unity project and the standalone app, so the
    /// shape can't drift). Rebuilds _m/_original exactly like a fresh
    /// PickFile() open, just sourced from JSON instead of a real .bdf.</summary>
    private void PickJsonFile()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Import Info.bdf JSON export", "", "json");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Import Info.bdf JSON export", "JSON (*.json)", "json");
#endif
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonUtility.FromJson<InfoBdfJsonDto>(json);
            if (dto == null || string.IsNullOrEmpty(dto.HeaderBase64))
                throw new InvalidDataException("Not a valid Info.bdf JSON export (missing HeaderBase64).");

            var platform = Enum.TryParse<BdfPlatform>(dto.Platform, out var p) ? p : BdfPlatform.Pc;
            var header = Convert.FromBase64String(dto.HeaderBase64);
            if (header.Length != 508)
                throw new InvalidDataException($"HeaderBase64 decoded to {header.Length} bytes, expected 508.");

            var parsed = new InfoModel { Platform = platform };
            header.CopyTo(parsed.Header, 0);
            parsed.EnvironmentMap = dto.EnvironmentMap ?? "";
            parsed.SkyMap = dto.SkyMap ?? "";
            parsed.ReservedTexturePath = dto.ReservedTexturePath ?? "";
            parsed.ColorCorrectionCube = dto.ColorCorrectionCube ?? "";
            parsed.FogOfWarCorrectionCube = dto.FogOfWarCorrectionCube ?? "";

            _m = parsed;
            _original = new InfoModel { Platform = parsed.Platform };
            Array.Copy(parsed.Header, _original.Header, 508);
            _original.EnvironmentMap = parsed.EnvironmentMap;
            _original.SkyMap = parsed.SkyMap;
            _original.ReservedTexturePath = parsed.ReservedTexturePath;
            _original.ColorCorrectionCube = parsed.ColorCorrectionCube;
            _original.FogOfWarCorrectionCube = parsed.FogOfWarCorrectionCube;
            _drawer.Original = _original;

            _loadedFilePath = ""; // JSON-sourced, not tied to a real .bdf path — "Save to File" still works, just prompts for a path
            _loadedPlatform = parsed.Platform;
            _lastPreviewedHeader = null;
            _status = $"Imported {Path.GetFileName(path)}.";
            ReportStatus?.Invoke(_status);

            _previewDirty = true;
            _previewReadyAt = Time.realtimeSinceStartupAsDouble + PreviewDebounceSeconds;
            RepaintHost?.Invoke();
        }
        catch (Exception ex)
        {
            _status = $"Failed to import JSON: {ex.Message}";
            ReportStatus?.Invoke(_status);
        }
    }

    /// <summary>Sun light + fog only - the only live-apply mechanism that
    /// already exists in this project (SC2Environment.ApplyToScene). Nothing
    /// in this codebase applies Water live to the scene yet (only at real
    /// export time via SC2WaterMaterialFactory), so Water fields are edited
    /// here but not previewed live.</summary>
    private void ApplyLivePreview()
    {
        if (_m == null) return;
        if (_lastPreviewedHeader != null && HeaderEquals(_lastPreviewedHeader, _m.Header)) return;

        if (_previewHost == null)
        {
            _previewHost = new GameObject("Info.bdf Editor Preview") { hideFlags = HideFlags.DontSave };
            _previewEnv = _previewHost.AddComponent<SC2Environment>();
        }
        _previewEnv.Apply(_m);
        _lastPreviewedHeader = (byte[])_m.Header.Clone();
    }

    /// <summary>Mirrors SC2EnvironmentShell.Commit() exactly (same fields,
    /// same SaveTerrainState(writeAssets:true) call, which already branches
    /// Editor-vs-Build internally) so "Save to Map" behaves identically to
    /// the embedded Environment tab's own Save button - just sourced from
    /// this panel's independently-loaded InfoModel. If the Environment tab
    /// happens to already be open when you do this, switch tabs away and
    /// back to see the saved values reflected there (this bypasses that
    /// tab's own Load() cache, same as any other external change to the
    /// same asset would).</summary>
    private void SaveToActiveMap()
    {
        var shell = _getTerrainShell();
        var data = shell != null ? shell.Data : null;
        if (data == null)
        {
            _status = "No active map found - create/load a terrain first.";
            ReportStatus?.Invoke(_status);
            return;
        }

        data.EnvironmentHeader = (byte[])_m.Header.Clone();
        data.EnvironmentMap = _m.EnvironmentMap;
        data.SkyMap = _m.SkyMap;
        data.TexturePath3 = _m.ReservedTexturePath;
        data.ColorCorrectionCube = _m.ColorCorrectionCube;
        data.FogOfWarCorrectionCube = _m.FogOfWarCorrectionCube;
        shell.SaveTerrainState(writeAssets: true);

        _status = $"Saved into active map '{data.MapName}'.";
        ReportStatus?.Invoke(_status);
    }

    private void SaveToFile()
    {
        var path = _loadedFilePath;
        if (string.IsNullOrEmpty(path))
        {
#if UNITY_EDITOR
            path = EditorUtility.SaveFilePanel("Save info.bdf", "", "info.win", "bdf");
#else
            path = SC2RuntimeFileDialog.SaveFile("Save info.bdf", "info.bdf (*.bdf)", "bdf");
#endif
        }
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var (data, offsetTable) = InfoBdf.Serialize(_m, _loadedPlatform);
            var container = new BdfContainer { Platform = _loadedPlatform, Data = data, OffsetTable = offsetTable };
            var bytes = container.Write(InfoBdf.Padding);
            File.WriteAllBytes(path, bytes);
            _loadedFilePath = path;
            _status = $"Saved to {Path.GetFileName(path)}.";
            ReportStatus?.Invoke(_status);
        }
        catch (Exception ex)
        {
            _status = $"Failed to save file: {ex.Message}";
            ReportStatus?.Invoke(_status);
        }
    }

    private static bool HeaderEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
