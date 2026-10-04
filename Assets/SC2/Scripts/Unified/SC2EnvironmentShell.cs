using System;
using System.IO;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Environment (info.bdf) editor for the PROCEDURAL SC2TerrainData workflow —
/// the Build &amp; Run GameView UI's "Environment" tab
/// (SC2GameViewEditorUI.DrawEnvironmentInspector). Sibling of
/// SC2CostsShell/SC2Collision2Shell.
///
/// info.bdf's EnvironmentHeader is a flat 508-byte buffer (BdfIO.Formats.
/// Info.InfoModel wraps it with computed float/int accessors, no separate
/// per-field storage) — so SC2TerrainData just holds that same byte[508]
/// plus the 5 texture-path strings directly, and this shell wraps/unwraps an
/// InfoModel around it purely in memory. No AssetDatabase/file dialogs here
/// — actual info.bdf file writing only happens at export time
/// (SC2TerrainExporter.BuildInfoModel).
///
/// All field drawing goes through SC2EnvFieldGui (plain GUILayout/GUI calls
/// only) instead of EditorGUILayout/EditorStyles — those resolve to the real
/// Unity Editor inspector skin in-Editor but to the runtime-skin shim
/// fallback in a Build & Run player, so the same call used to render
/// differently in each. SC2EnvFieldGui's controls always use the engine's
/// runtime GUISkin, so the Unity Editor's Game view and an actual build show
/// the same panel, pixel for pixel.
///
/// Undo/redo storage moved from a local List&lt;EnvSnapshot&gt;
/// to SC2MapDocument.Environment + the session's real SC2CommandHistory
/// (SC2SetEnvironmentHeaderCommand/SC2SetEnvironmentTexturePathCommand) — the
/// first of the 8 domains to make this cut. The per-frame edit flow itself is
/// UNCHANGED: widgets still mutate `_m` directly every frame, MaybeSnapshot
/// still runs once per frame and diffs `_m` against the last known state —
/// only WHERE that "last known state" now lives, and HOW a change gets
/// recorded, changed. SC2TerrainData is still the store the save path
/// serializes: SC2TerrainShell.SyncDataForSave copies the document's
/// Environment into it before every save — this cutover makes undo/redo real,
/// it does not yet make SC2TerrainData a derived/secondary store.
/// </summary>
public sealed class SC2EnvironmentShell
{
    private readonly SC2TerrainShell _terrainShell;
    private readonly SC2MapSession _session;
    public Action RepaintHost;

    private long _lastActionTimestamp;

    /// <summary>See SC2ObjectsShell's identical property for why.</summary>
    public bool HasUndo => _session != null && _session.History.CanUndo;
    public bool HasRedo => _session != null && _session.History.CanRedo;
    public long LastActionTimestamp => _lastActionTimestamp;

    private SC2TerrainData Data => _terrainShell?.Data;
    public bool IsReady => Data != null && Data.IsInitialized;

    private InfoModel _m;
    private InfoModel _original;
    /// <summary>The "set all/selected fields to default" reference —
    /// distinct from `_original`. `_original` is whatever THIS map's own
    /// EnvironmentHeader held at load time (for an imported map, that's the
    /// map's own real authored values; "Reset All" undoes edits back to it,
    /// which is correct there). `_defaults` is always the same universal
    /// reference profile (SC2EnvironmentDefaults) regardless of what map is
    /// loaded, so "Set All to Default" can force a sensible baseline onto
    /// ANY map, imported or not — not just undo unsaved edits on a
    /// from-scratch one.</summary>
    private InfoModel _defaults;
    private SC2TerrainData _loadedForData;

    /// <summary>Set by ImportFromInfoBdfFile, shown once under the toolbar
    /// until the next import attempt — this shell has no other status/error
    /// surface (unlike SC2InfoBdfEditorPanel, which has its own message
    /// label).</summary>
    private string _importStatus;

    /// <summary>Field-drawing (Sun/Water/Fog/Glow/... tabs) lives in a shared
    /// class now — SC2InfoModelFieldDrawer — so the standalone Info.bdf
    /// Editor window (SC2InfoBdfEditorWindow) can draw the exact same fields
    /// against its own independently-loaded InfoModel without duplicating
    /// ~80 fields' worth of UI. This shell only owns the document/undo/
    /// SC2TerrainData plumbing around it.</summary>
    private readonly SC2InfoModelFieldDrawer _drawer = new();

    /// <summary>All Environment fields participate in one
    /// command snapshot/diff — Undo must not be limited to just the
    /// 508-byte Header — a snapshot captures the header AND all 5
    /// texture-path strings together, so a comparison against "the last
    /// known state" never misses a changed string. Now used purely as a
    /// value-type comparison/copy helper (MatchesCurrent/ApplyTo) — the
    /// actual undo storage is SC2MapDocument.Environment +
    /// SC2CommandHistory, not a stack of these anymore.</summary>
    private readonly struct EnvSnapshot
    {
        public readonly byte[] Header;
        public readonly string EnvironmentMap, SkyMap, TexturePath3, ColorCorrectionCube, FogOfWarCorrectionCube;

        public EnvSnapshot(InfoModel m)
        {
            Header = (byte[])m.Header.Clone();
            EnvironmentMap = m.EnvironmentMap;
            SkyMap = m.SkyMap;
            TexturePath3 = m.ReservedTexturePath;
            ColorCorrectionCube = m.ColorCorrectionCube;
            FogOfWarCorrectionCube = m.FogOfWarCorrectionCube;
        }

        public void ApplyTo(InfoModel m)
        {
            Array.Copy(Header, m.Header, Header.Length);
            m.EnvironmentMap = EnvironmentMap;
            m.SkyMap = SkyMap;
            m.ReservedTexturePath = TexturePath3;
            m.ColorCorrectionCube = ColorCorrectionCube;
            m.FogOfWarCorrectionCube = FogOfWarCorrectionCube;
        }

        public bool MatchesCurrent(InfoModel m) =>
            HeaderEquals(Header, m.Header) &&
            EnvironmentMap == m.EnvironmentMap &&
            SkyMap == m.SkyMap &&
            TexturePath3 == m.ReservedTexturePath &&
            ColorCorrectionCube == m.ColorCorrectionCube &&
            FogOfWarCorrectionCube == m.FogOfWarCorrectionCube;
    }

    /// <summary>Mirrors whatever is currently in _session.Document.Environment
    /// — compared against `_m` once per frame (MaybeSnapshot) to detect which
    /// field(s) changed and fire the matching command(s).</summary>
    private EnvSnapshot _lastCommitted;

    /// <summary>Working copy of the 5 texture slots' export provenance/opt-in
    /// (SC2EnvironmentTextureExport). The drawer mutates this instance in
    /// place while the Textures tab is drawn; MaybeSnapshotTextureExport turns
    /// any difference from the document into undoable commands, and
    /// SC2TerrainShell.SyncDataForSave mirrors the document into SC2TerrainData
    /// on every save (which the runtime save path rebuilds the document from).</summary>
    private readonly SC2EnvironmentTextureExport _textureExport = new();

    public SC2EnvironmentShell(SC2TerrainShell terrainShell, SC2MapSession session)
    {
        _terrainShell = terrainShell;
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>subTab indexes SC2EditorTabs.EnvironmentTabs — the host's top
    /// pill bar. Each call only draws the group(s) belonging to the selected
    /// pill, instead of one giant scroll of every group; the fixed-width
    /// left panel stays narrow by design, so cutting the per-frame content
    /// down (rather than trying to fit everything) is what makes it usable.</summary>
    public void DrawEmbeddedGUI(float panelWidth, int subTab = 0)
    {
        // See SC2TerrainShell.SyncEffectiveViewWidth's own doc comment — keeps
        // GetUnifiedContentWidth/DrawUnifiedSegmentGrid sizing against THIS
        // panel's own current width instead of whatever another tab last cached.
        SC2TerrainShell.SyncEffectiveViewWidth(panelWidth);
        var w = Mathf.Max(1f, panelWidth);
        GUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));

        if (!IsReady)
        {
            SC2TerrainShell.DrawUnifiedNote("Create/load a terrain first (Terrain tab) — Environment edits the procedural map's own info.bdf fields.");
            GUILayout.EndVertical();
            return;
        }

        Data.EnsureEnvironmentHeader();
        if (_loadedForData != Data) Load();

        DrawToolbar();

        if (!string.IsNullOrEmpty(_importStatus))
            SC2TerrainShell.DrawUnifiedNote(_importStatus);

        var m = _m;

        // Indices match SC2EditorTabs.EnvironmentTabs exactly (see that
        // array's own comment for the full rationale). Keep this switch's
        // case order in lockstep with the array whenever either changes.
        switch (subTab)
        {
            case 0: _drawer.DrawSunTab(m); break;
            case 1: _drawer.DrawShadowTab(m); break;
            case 2: _drawer.DrawWaterTab(m); break;
            case 3: _drawer.DrawFogTab(m); break;
            case 4: _drawer.DrawGlowTab(m); break;
            case 5: _drawer.DrawBloomTab(m); break;
            case 6:
                _drawer.DrawPointCloudTab(m);
                // The bake that fills the volumes lives with their info.bdf fields; it works on the terrain data, not on `m`.
                _terrainShell.DrawPointCloudBakeSection();
                break;
            case 7: _drawer.DrawFogOfWarTab(m); break;
            case 8: _drawer.DrawIntelTab(m); break;
            case 9: _drawer.DrawDepthOfFieldTab(m); break;
            case 10: _drawer.DrawSharpenTab(m); break;
            case 11: _drawer.DrawRenderPassesTab(m); break;
            case 12: _drawer.DrawAirElevationTab(m); break;
            case 13: _drawer.DrawWindTab(m); break;
            case 14: _drawer.DrawTexturesTab(m); break;
            default: _drawer.DrawUnverifiedTab(m); break;
        }

        MaybeSnapshot();

        GUILayout.EndVertical();
    }

    private void DrawToolbar()
    {
        // No step-count label anymore — SC2CommandHistory (unlike the old
        // local List<EnvSnapshot>) doesn't expose stack depth, only
        // CanUndo/CanRedo. A minor, visible side effect of this cutover.
        //
        // Each button pair on its own row, not squeezed together with
        // GUILayout.FlexibleSpace() — that push-to-the-right pattern (like
        // the slider/color-row overflow fixed earlier) is unreliable once
        // this panel renders inside a scroll view, and even before that a
        // 5th button on one row pushed later buttons off the right edge
        // entirely (GUILayout doesn't wrap a horizontal group, it just
        // overflows the container). Sequential buttons with no expand/
        // flexible siblings size to their own content width and never
        // fight for space, so simple rows are the robust fix here.
        GUILayout.BeginHorizontal();
        // Restore whatever GUI.enabled was BEFORE this row (not hardcode
        // true) — a caller can legitimately disable the
        // whole panel around this call (SC2GameViewEditorUI does this while
        // a top-panel dropdown is open, to stop clicks reaching panel
        // content underneath it), and unconditionally re-enabling here
        // silently undid that for everything drawn after this row, letting
        // a dropdown click "fall through" to whatever was underneath it.
        var guiEnabledBeforeUndoRow = GUI.enabled;
        GUI.enabled = guiEnabledBeforeUndoRow && _session.History.CanUndo;
        if (SC2EnvFieldGui.Button("↩ Undo")) Undo();
        GUI.enabled = guiEnabledBeforeUndoRow && _session.History.CanRedo;
        if (SC2EnvFieldGui.Button("↪ Redo")) Redo();
        GUI.enabled = guiEnabledBeforeUndoRow;
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (SC2EnvFieldGui.Button("⟲ Reset All")) ResetAllToOriginal();
        if (SC2EnvFieldGui.Button("Set All to Default")) SetAllToDefault();
        GUILayout.EndHorizontal();

        if (SC2EnvFieldGui.Button("Import from info.bdf...")) ImportFromInfoBdfFile();
    }

    /// <summary>Direct one-click equivalent of SC2InfoBdfEditorPanel's "Open
    /// .bdf... -&gt; Save to Map" 2-step flow, integrated right into this tab.
    /// Parses a real info.bdf off disk and applies it to `_m` via the same
    /// EnvSnapshot/MaybeSnapshot mechanism ResetAllToOriginal/SetAllToDefault
    /// already use, so the import goes through undo/redo like any other
    /// edit — it does not touch SC2TerrainData directly (the save path copies
    /// the document into it).</summary>
    private void ImportFromInfoBdfFile()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Import info.bdf", "", "bdf");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Import info.bdf", "info.bdf (*.bdf)", "bdf");
#endif
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var raw = File.ReadAllBytes(path);
            var container = BdfContainer.Read(raw, InfoBdf.Padding);
            var parsed = InfoBdf.Parse(container);

            new EnvSnapshot(parsed).ApplyTo(_m);
            MaybeSnapshot();
            _importStatus = $"Imported: {Path.GetFileName(path)}";
            RepaintHost?.Invoke();
        }
        catch (Exception ex)
        {
            _importStatus = $"Import failed: {ex.Message}";
            SC2Log.LogException(ex);
        }
    }

    private void Load()
    {
        _m = new InfoModel();
        Array.Copy(Data.EnvironmentHeader, _m.Header, 508);
        _m.EnvironmentMap = Data.EnvironmentMap;
        _m.SkyMap = Data.SkyMap;
        _m.ReservedTexturePath = Data.TexturePath3;
        _m.ColorCorrectionCube = Data.ColorCorrectionCube;
        _m.FogOfWarCorrectionCube = Data.FogOfWarCorrectionCube;

        _original = new InfoModel();
        Array.Copy(Data.EnvironmentHeader, _original.Header, 508);
        _original.EnvironmentMap = Data.EnvironmentMap;
        _original.SkyMap = Data.SkyMap;
        _original.ReservedTexturePath = Data.TexturePath3;
        _original.ColorCorrectionCube = Data.ColorCorrectionCube;
        _original.FogOfWarCorrectionCube = Data.FogOfWarCorrectionCube;
        _drawer.Original = _original;

        // Always the same reference profile, independent of Data - unlike
        // _original above, this does NOT come from Data.EnvironmentHeader.
        // Of the reference map's 5 texture-path strings only the sky is set; the others are "", matching InfoModel's own field
        // initializers.
        _defaults = new InfoModel { SkyMap = SC2EnvironmentDefaults.SkyMap };
        Array.Copy(SC2EnvironmentDefaults.CloneDefaultHeader(), _defaults.Header, 508);

        // Sync the document to the freshly-loaded terrain's state. Undo
        // history itself is NOT cleared here — SC2TerrainCombinedUndo.Reset()
        // (called from SC2TerrainShell.SetData(), the actual "a new
        // SC2TerrainData just replaced the old one" event) already clears
        // Session.History exactly once, at the moment the load/create
        // happens. This Load() only runs later, whenever this shell's own
        // _loadedForData != Data check next fires (e.g. only once the user
        // switches to the Environment tab) — clearing history AGAIN here
        // used to silently wipe any real command-based edits made on OTHER
        // cut-over domains (Terrain Material/Pair rename, Collision2
        // VerticalOffset) in the meantime, desyncing SC2TerrainCombinedUndo's
        // marker stack from the actual SC2CommandHistory stack underneath.
        var env = _session.Document.Environment;
        env.WorkingHeader = (byte[])_m.Header.Clone();
        env.EnvironmentMap = _m.EnvironmentMap;
        env.SkyMap = _m.SkyMap;
        env.TexturePath3 = _m.ReservedTexturePath;
        env.ColorCorrectionCube = _m.ColorCorrectionCube;
        env.FogOfWarCorrectionCube = _m.FogOfWarCorrectionCube;
        _textureExport.CopyFrom(new SC2EnvironmentTextureExport
        {
            SourceTags = Data.EnvironmentTextureSourceTags,
            ForceExport = Data.EnvironmentTextureForceExport,
        });
        _drawer.TextureExport = _textureExport;
        env.TextureExport = _textureExport.Clone();
        _lastCommitted = new EnvSnapshot(_m);

        _loadedForData = Data;
    }

    /// <summary>Runs once per frame after all tabs draw. Diffs `_m` against
    /// `_lastCommitted` (the document's current state) and fires exactly one
    /// real command per field family that actually changed — a header edit
    /// and a texture-path edit can't both happen in the same frame in
    /// practice (only one tab is visible/edited at a time), but this handles
    /// it correctly either way (e.g. Reset All, which can touch several
    /// fields from different tabs at once) by checking each independently.</summary>
    private void MaybeSnapshot()
    {
        MaybeSnapshotTextureExport();
        if (_lastCommitted.MatchesCurrent(_m)) return;

        var document = _session.Document;
        if (!HeaderEquals(_lastCommitted.Header, _m.Header))
            _session.History.Execute(document, new SC2SetEnvironmentHeaderCommand(_m.Header));

        ExecuteIfChanged(document, SC2EnvironmentTextureSlot.EnvironmentMap, _lastCommitted.EnvironmentMap, _m.EnvironmentMap);
        ExecuteIfChanged(document, SC2EnvironmentTextureSlot.SkyMap, _lastCommitted.SkyMap, _m.SkyMap);
        ExecuteIfChanged(document, SC2EnvironmentTextureSlot.TexturePath3, _lastCommitted.TexturePath3, _m.ReservedTexturePath);
        ExecuteIfChanged(document, SC2EnvironmentTextureSlot.ColorCorrectionCube, _lastCommitted.ColorCorrectionCube, _m.ColorCorrectionCube);
        ExecuteIfChanged(document, SC2EnvironmentTextureSlot.FogOfWarCorrectionCube, _lastCommitted.FogOfWarCorrectionCube, _m.FogOfWarCorrectionCube);

        _lastCommitted = new EnvSnapshot(_m);
        _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Fires one command per texture slot whose provenance tag or
    /// "copy into map on export" opt-in differs from the document's — same
    /// diff-then-command approach as the path strings above.</summary>
    private void MaybeSnapshotTextureExport()
    {
        var document = _session.Document;
        var committed = document.Environment.TextureExport ??= new SC2EnvironmentTextureExport();
        if (committed.ContentEquals(_textureExport)) return;
        for (var i = 0; i < SC2EnvironmentTextureExport.SlotCount; i++)
        {
            var slot = (SC2EnvironmentTextureSlot)i;
            var tag = _textureExport.GetTag(slot);
            var force = _textureExport.GetForce(slot);
            if (string.Equals(tag, committed.GetTag(slot), StringComparison.Ordinal) && force == committed.GetForce(slot)) continue;
            _session.History.Execute(document, new SC2SetEnvironmentTextureExportCommand(slot, tag, force));
        }
        _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private void ExecuteIfChanged(SC2MapDocument document, SC2EnvironmentTextureSlot slot, string previous, string current)
    {
        if (previous == current) return;
        _session.History.Execute(document, new SC2SetEnvironmentTexturePathCommand(slot, current));
    }

    /// <summary>Applies Original to `_m`, then reuses MaybeSnapshot's own
    /// diff logic to fire however many field-level commands are actually
    /// needed to reach it — e.g. if only WaterColor had been touched since
    /// load, Reset All now records one command, not a blanket snapshot for
    /// fields that were never dirty. Slightly more granular than the old
    /// unconditional PushSnapshot(), which always recorded one step
    /// regardless of what (if anything) actually changed.</summary>
    private void ResetAllToOriginal()
    {
        new EnvSnapshot(_original).ApplyTo(_m);
        MaybeSnapshot();
        RepaintHost?.Invoke();
    }

    /// <summary>Forces every field to the universal
    /// reference default (SC2EnvironmentDefaults), same diff-then-command
    /// mechanism as ResetAllToOriginal above, just sourced from `_defaults`
    /// instead of `_original`.</summary>
    private void SetAllToDefault()
    {
        new EnvSnapshot(_defaults).ApplyTo(_m);
        MaybeSnapshot();
        RepaintHost?.Invoke();
    }

    /// <summary>Public so the global Ctrl+Z/Y handler in
    /// SC2GameViewEditorUI dispatches to whichever shell's LastActionTimestamp
    /// is newest, not only ever this shell's own button.</summary>
    public void Undo()
    {
        if (!_session.History.CanUndo) return;
        _session.History.Undo(_session.Document);
        SyncFromDocument();
        RepaintHost?.Invoke();
    }

    public void Redo()
    {
        if (!_session.History.CanRedo) return;
        _session.History.Redo(_session.Document);
        SyncFromDocument();
        RepaintHost?.Invoke();
    }

    /// <summary>Pulls the document's current Environment state back into `_m`
    /// after an Undo/Redo (which mutated the document directly, not `_m` —
    /// unlike a normal edit frame, where a GUI widget already wrote the new
    /// value into `_m` before MaybeSnapshot ever runs).</summary>
    private void SyncFromDocument()
    {
        var env = _session.Document.Environment;
        Array.Copy(env.WorkingHeader, _m.Header, 508);
        _m.EnvironmentMap = env.EnvironmentMap;
        _m.SkyMap = env.SkyMap;
        _m.ReservedTexturePath = env.TexturePath3;
        _m.ColorCorrectionCube = env.ColorCorrectionCube;
        _m.FogOfWarCorrectionCube = env.FogOfWarCorrectionCube;
        _textureExport.CopyFrom(env.TextureExport);
        _lastCommitted = new EnvSnapshot(_m);
    }

    private static bool HeaderEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
