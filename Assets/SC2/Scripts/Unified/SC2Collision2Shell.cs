using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using BdfIO.Core;
using BdfIO.Formats.Collision2;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

/// <summary>
/// Collision2 generate/preview panel for the PROCEDURAL SC2TerrainData
/// workflow — the Build &amp; Run GameView UI's "Collision2" tab
/// (SC2GameViewEditorUI.DrawCollisionInspector), which previously only had
/// SetStatus(...) stub buttons (no build-compatible generation logic to
/// call). Sibling of SC2CostsShell — same "lives in Scripts/Unified so it
/// compiles for both the Editor and a real .exe build" reasoning.
///
/// Unlike Costs, there's no brush painting here: collision2 is fully
/// auto-generated from the current terrain's hfield (SC2Collision2MeshBuilder,
/// error-adaptive Delaunay TIN — see that class for why), so this shell is
/// just: a "Generate" button that runs SC2TerrainExporter.BuildCollision2Preview
/// (the same code path Export itself uses to write collision2.bdf, just
/// without touching disk or requiring texture/RNM/BSP work a preview doesn't
/// need — see BuildPreviewGeometryModel's doc comment), stats/validation
/// readout, and an actual in-viewport preview mesh (a real MeshFilter/
/// MeshRenderer GameObject, not a per-frame Graphics.DrawMesh call from
/// OnGUI — that pattern was already found unreliable once for the Costs
/// overlay, vanishing on mouse-up or not rendering at all depending on
/// exactly when OnGUI fires relative to the camera's render pass).
///
/// Height Offset now records real undo through the
/// session's SC2CommandHistory (SC2SetCollision2VerticalOffsetCommand) —
/// this domain persists exactly one field, so unlike Costs it didn't need
/// its own legacy Snapshot stack at all, only a coalesced commit at the end
/// of each slider drag/text-field edit (see DrawHeightOffsetSlider/
/// CommitOffsetChange). Its own Undo/Redo buttons act on the same shared
/// Session.History as Environment's — same "global undo/redo" design the
/// plan calls for, not a per-tab-siloed stack; pressing Undo here can also
/// undo the most recent Environment/Terrain command if that happened more
/// recently, identical to how Environment's own buttons already behave.
/// </summary>
public sealed class SC2Collision2Shell : System.IDisposable
{
    private readonly SC2TerrainShell _terrainShell;
    private readonly SC2MapSession _session;
    public System.Action RepaintHost;

    private SC2TerrainData Data => _terrainShell?.Data;
    public bool IsReady => Data != null && Data.IsInitialized;

    private long _lastActionTimestamp;

    /// <summary>See SC2ObjectsShell's identical property for why.</summary>
    public bool HasUndo => _session != null && _session.History.CanUndo;
    public bool HasRedo => _session != null && _session.History.CanRedo;
    public long LastActionTimestamp => _lastActionTimestamp;

    private Collision2Model _lastModel;
    private HfieldModel _lastHfield;
    private string _lastError;
    private bool _generating;

    private GameObject _previewGO;
    private MeshFilter _previewFilter;
    private MeshRenderer _previewRenderer;
    private Mesh _previewMesh;
    private Material _solidMaterial;
    private Material _wireframeMaterial;
    private bool _showPreview = true;
    private bool _wireframeMode;
    private bool _tabActive;

    private SC2TerrainData _loadedForData;
    private float? _offsetBeforeDrag;
    private float? _budgetBeforeDrag;

    public SC2Collision2Shell(SC2TerrainShell terrainShell, SC2MapSession session)
    {
        _terrainShell = terrainShell;
        _session = session;
    }

    /// <summary>Called every frame from SC2GameViewEditorUI, mirroring how
    /// HandleCostsGameViewInput disables the costs overlay outside its own
    /// tab — the preview mesh shouldn't keep floating over the terrain once
    /// the user has moved to a different tab.
    /// No longer auto-generates on tab activation —
    /// opening the Collision2 tab must never kick off real
    /// generation work on its own; "Generate Collision2" is the only thing
    /// that should ever trigger it). Until the user presses that button,
    /// _lastModel stays null and the preview simply has nothing to show.</summary>
    public void SetTabActive(bool active)
    {
        _tabActive = active;
        UpdatePreviewVisibility();
    }

    private void UpdatePreviewVisibility()
    {
        if (_previewGO != null)
            _previewGO.SetActive(_tabActive && _showPreview && _lastModel != null);
    }

    public void DrawEmbeddedGUI(float panelWidth)
    {
        // See SC2TerrainShell.SyncEffectiveViewWidth's own doc comment — keeps
        // GetUnifiedContentWidth/DrawUnifiedSegmentGrid sizing against THIS
        // panel's own current width instead of whatever another tab last cached.
        SC2TerrainShell.SyncEffectiveViewWidth(panelWidth);
        var w = Mathf.Max(1f, panelWidth);
        EditorGUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));

        if (!IsReady)
        {
            SC2TerrainShell.DrawUnifiedNote("Create/load a terrain first (Terrain tab) — Collision2 is generated from the painted procedural terrain's hfield.");
            EditorGUILayout.EndVertical();
            return;
        }

        if (_loadedForData != Data) LoadFromNewData();

        DrawHeightOffsetSlider();
        SC2TerrainShell.DrawUnifiedNote("Uniform vertical shift applied to the whole collision mesh (default 0.25) — keeps camera/air-unit collision from clipping into the visual terrain at seams instead of floating above or sinking below it.");

        EditorGUILayout.Space(4);
        DrawPolygonBudgetSlider();
        SC2TerrainShell.DrawUnifiedNote("Ground TIN triangle budget (default 2500, the vanilla-observed convention — the file format itself has no fixed ceiling here). Raising it lets the error-adaptive refinement spend more triangles on steep/detailed terrain before it runs out of budget.");

        EditorGUILayout.Space(4);
        DrawUndoRedoRow(w);

        EditorGUILayout.Space(4);
        EditorGUI.BeginDisabledGroup(_generating);
        if (GUILayout.Button(_generating ? "Generating..." : "Generate Collision2", GUILayout.Height(SC2UiScale.Pxf(28f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
            Generate();
        EditorGUI.EndDisabledGroup();

        if (!string.IsNullOrEmpty(_lastError))
        {
            EditorGUILayout.HelpBox(_lastError, MessageType.Error);
        }
        else if (_lastModel != null)
        {
            var triCount = _lastModel.Triangles.Count;
            var budget = Data.Collision2PolygonBudget;
            EditorGUILayout.LabelField("Vertices", _lastModel.Vertices.Count.ToString());
            EditorGUILayout.LabelField("Triangles", $"{triCount} / {budget} {(triCount <= budget ? "(OK)" : "(OVER BUDGET)")}");
            EditorGUILayout.LabelField("EDB Nodes", _lastModel.Nodes.Count.ToString());

            EditorGUILayout.Space(4);
            var showPreview = EditorGUILayout.ToggleLeft("Show preview in viewport", _showPreview, GUILayout.Width(w), GUILayout.MaxWidth(w));
            if (showPreview != _showPreview)
            {
                _showPreview = showPreview;
                UpdatePreviewVisibility();
            }

            var wireframe = EditorGUILayout.ToggleLeft("Wireframe", _wireframeMode, GUILayout.Width(w), GUILayout.MaxWidth(w));
            if (wireframe != _wireframeMode)
            {
                _wireframeMode = wireframe;
                // Re-slice the same _lastModel data into the other topology
                // (Triangles <-> Lines) immediately, not just next Generate.
                RebuildPreviewMesh();
            }
            SC2TerrainShell.DrawUnifiedNote("Shows the real TIN triangulation (edges only) instead of a filled solid — useful for spotting sliver triangles or where the error-driven refinement spent its 2500-triangle budget.");

            // Real fix for "Import Corrected Mesh"/"Import collision2.bdf..."
            // having no effect on the actual exported map — see
            // SC2TerrainData.UseCustomCollision2's own doc comment for the
            // full explanation. Previously this checkbox didn't exist at
            // all, so there was no way for anything imported/corrected here
            // to survive past this preview.
            EditorGUILayout.Space(4);
            var useCustom = EditorGUILayout.ToggleLeft("Use this mesh for map export", Data.UseCustomCollision2, GUILayout.Width(w), GUILayout.MaxWidth(w));
            if (useCustom != Data.UseCustomCollision2)
            {
                Data.UseCustomCollision2 = useCustom;
                if (useCustom) SyncCustomCollision2Bytes();
            }
            SC2TerrainShell.DrawUnifiedNote(Data.UseCustomCollision2
                ? "Real map export will write THIS mesh as collision2.bdf, unchanged — it will not be regenerated from the terrain. Re-import or re-Generate to replace it; click Generate Collision2 to go back to auto-generation."
                : "Real map export always auto-generates collision2.bdf fresh from the current terrain, ignoring this preview — check the box above to make export use this exact mesh instead.");
        }
        else
        {
            SC2TerrainShell.DrawUnifiedNote("Not generated yet this session — click Generate Collision2 above. Export also regenerates it automatically when writing the map, so this button is only needed to preview it beforehand.");
        }

        EditorGUILayout.EndVertical();
    }

    /// <summary>Mirrors SC2EnvironmentShell.Load()'s own "sync the document
    /// to whatever the freshly-loaded terrain already has" step — does NOT
    /// clear Session.History itself (SC2TerrainCombinedUndo.Reset(), called
    /// from SC2TerrainShell.SetData(), already did that once at the actual
    /// load/create moment; see that class's doc comment for why duplicating
    /// the Clear() here would be wrong once more than one domain shares the
    /// same session history).</summary>
    private void LoadFromNewData()
    {
        if (_session?.Document != null)
        {
            _session.Document.Collision2.Generation.VerticalOffset = Data.Collision2HeightOffset;
            _session.Document.Collision2.Generation.MaxPolygons = Data.Collision2PolygonBudget;
        }
        _offsetBeforeDrag = null;
        _budgetBeforeDrag = null;
        _loadedForData = Data;
    }

    /// <summary>Height Offset is the only mutable Collision2 field, so unlike
    /// Costs this doesn't need a full legacy Snapshot stack — just a single
    /// real ISC2DocumentCommand, coalesced to fire once per completed drag/
    /// edit gesture rather than once per frame (see DrawCompactSlider's own
    /// DraggingSliderKeys doc comment for why a naive per-frame diff would
    /// flood the undo stack during a slider drag).</summary>
    private void DrawHeightOffsetSlider()
    {
        var before = Data.Collision2HeightOffset;
        var newOffset = SC2TerrainShell.DrawCompactSlider("Height Offset", before, -5f, 5f,
            onValueChangeStart: () => _offsetBeforeDrag ??= before);

        if (!Mathf.Approximately(newOffset, before))
        {
            Data.Collision2HeightOffset = newOffset;
            _lastModel = null; // stale until regenerated with the new offset
            UpdatePreviewVisibility();

            // Only a KeyDown/Return text-field commit
            // (rawType, since DrawCompactSlider already called e.Use() on it
            // before returning) settles to its final value in one frame with
            // no drag involved — commit that immediately. Everything else
            // (a slider drag) must wait for the real release below, however
            // many intermediate events fire while the mouse button stays
            // held. The previous check here
            // ("!= EventType.MouseDrag") also matched the initial MouseDown
            // that jumps the handle to the click position, AND apparently
            // other non-MouseDrag event types this custom Game View's event
            // stream produces mid-drag — so every intermediate movement got
            // pushed onto the undo stack as its own step instead of the
            // whole gesture collapsing into one.
            var ev = Event.current;
            if (ev == null || ev.rawType == EventType.KeyDown)
            {
                CommitOffsetChange(newOffset);
                _offsetBeforeDrag = null;
            }
        }
        else if (_offsetBeforeDrag.HasValue)
        {
            // Was "did an IMGUI MouseUp event reach this
            // code" (ev.type/rawType == EventType.MouseUp): releasing the
            // slider handle itself never committed;
            // only a LATER, unrelated click somewhere else did. Root cause:
            // whatever genuine MouseUp fires when the mouse button is
            // physically released over the slider handle never reaches this
            // code as a real Event with that type in this custom Game
            // View's event stream (GUILayout.HorizontalSlider's own release
            // handling likely fully absorbs it) — a subsequent click
            // elsewhere just happens to produce an unrelated, "unclaimed"
            // MouseUp that this over-broad "was there ANY MouseUp this
            // frame" check would also match. Polling the actual OS mouse-
            // button state instead sidesteps IMGUI event dispatch/consumption
            // entirely — it's true the very next GUI pass after the button
            // is physically released, regardless of which control (if any)
            // claimed the underlying event.
            if (!Input.GetMouseButton(0))
            {
                CommitOffsetChange(Data.Collision2HeightOffset);
                _offsetBeforeDrag = null;
            }
        }
    }

    private void CommitOffsetChange(float finalValue)
    {
        var document = _session?.Document;
        if (document == null) return;
        if (Mathf.Approximately(document.Collision2.Generation.VerticalOffset, finalValue)) return;
        _session.History.Execute(document, new SC2SetCollision2VerticalOffsetCommand(finalValue));
        // This was never set anywhere in this class,
        // so LastActionTimestamp stayed at its default 0 forever — the
        // global Ctrl+Z routing in SC2GameViewEditorUI (which calls whichever
        // shell's HasUndo is true AND has the most recent LastActionTimestamp)
        // could then never actually pick THIS shell's Undo(), even right
        // after editing a Collision2 slider. Since every shell shares the
        // same underlying _session.History, the wrong shell's Undo() still
        // correctly reverted the DOCUMENT value — but never called this
        // shell's own SyncOffsetFromDocument(), so Data.Collision2HeightOffset/
        // Collision2PolygonBudget (what the sliders actually display) stayed
        // stale, making Ctrl+Z look like it silently did nothing on every
        // Collision2 slider.
        _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Same drag-coalescing shape as DrawHeightOffsetSlider — the
    /// underlying value is an int triangle count, but DrawCompactSlider is
    /// float-only, so the widget works in float and this rounds to the
    /// nearest whole triangle before comparing/committing.</summary>
    private void DrawPolygonBudgetSlider()
    {
        var before = Data.Collision2PolygonBudget;
        var newBudgetRaw = SC2TerrainShell.DrawCompactSlider("Polygon Budget", before, 2500f, 10000f,
            onValueChangeStart: () => _budgetBeforeDrag ??= before);
        var newBudget = Mathf.RoundToInt(newBudgetRaw);

        if (newBudget != before)
        {
            Data.Collision2PolygonBudget = newBudget;
            _lastModel = null; // stale until regenerated with the new budget
            UpdatePreviewVisibility();

            // See DrawHeightOffsetSlider's identical fix for why this
            // checks rawType == KeyDown rather than "!= MouseDrag".
            var ev = Event.current;
            if (ev == null || ev.rawType == EventType.KeyDown)
            {
                CommitBudgetChange(newBudget);
                _budgetBeforeDrag = null;
            }
        }
        else if (_budgetBeforeDrag.HasValue)
        {
            // See DrawHeightOffsetSlider's identical fix — polling the real
            // mouse-button state instead of waiting for an IMGUI MouseUp
            // event that doesn't reliably reach this code.
            if (!Input.GetMouseButton(0))
            {
                CommitBudgetChange(Data.Collision2PolygonBudget);
                _budgetBeforeDrag = null;
            }
        }
    }

    private void CommitBudgetChange(int finalValue)
    {
        var document = _session?.Document;
        if (document == null) return;
        if (document.Collision2.Generation.MaxPolygons == finalValue) return;
        _session.History.Execute(document, new SC2SetCollision2PolygonBudgetCommand(finalValue));
        // See CommitOffsetChange's own comment — same bug, same fix.
        _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private void DrawUndoRedoRow(float w)
    {
        if (_session == null) return;
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        EditorGUI.BeginDisabledGroup(!_session.History.CanUndo);
        if (GUILayout.Button("↩ Undo")) Undo();
        EditorGUI.EndDisabledGroup();
        EditorGUI.BeginDisabledGroup(!_session.History.CanRedo);
        if (GUILayout.Button("↪ Redo")) Redo();
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>Public so the shared undo dispatch can call it — see SC2EnvironmentShell.Undo's
    /// identical doc comment for why.</summary>
    public void Undo()
    {
        if (_session == null || !_session.History.CanUndo) return;
        _session.History.Undo(_session.Document);
        SyncOffsetFromDocument();
    }

    public void Redo()
    {
        if (_session == null || !_session.History.CanRedo) return;
        _session.History.Redo(_session.Document);
        SyncOffsetFromDocument();
    }

    /// <summary>Pulls VerticalOffset back from the document after an Undo/
    /// Redo, which mutated the document directly rather than through the
    /// slider — same reasoning as SC2EnvironmentShell.SyncFromDocument.</summary>
    private void SyncOffsetFromDocument()
    {
        if (_session?.Document == null) return;
        Data.Collision2HeightOffset = _session.Document.Collision2.Generation.VerticalOffset;
        Data.Collision2PolygonBudget = _session.Document.Collision2.Generation.MaxPolygons;
        _lastModel = null; // stale until regenerated with the restored offset/budget
        _offsetBeforeDrag = null;
        _budgetBeforeDrag = null;
        UpdatePreviewVisibility();
        RepaintHost?.Invoke();
    }

    /// <summary>Serializes _lastModel via the exact same Collision2Bdf.
    /// Serialize/BdfContainer path the real export uses, into
    /// Data.CustomCollision2Bytes — the bytes real export writes verbatim
    /// as collision2.win.bdf when Data.UseCustomCollision2 is set. Called
    /// once when the checkbox is first turned on, and again whenever a NEW
    /// import replaces _lastModel while the checkbox is already on (so
    /// re-importing a further-corrected mesh doesn't require unchecking and
    /// rechecking the box).</summary>
    private void SyncCustomCollision2Bytes()
    {
        if (_lastModel == null) { Data.CustomCollision2Bytes = null; return; }
        try
        {
            var (data, offsetTable) = Collision2Bdf.Serialize(_lastModel, BdfPlatform.Pc);
            Data.CustomCollision2Bytes = new BdfContainer { Platform = BdfPlatform.Pc, Data = data, OffsetTable = offsetTable, NumFiles = 1 }.Write(Collision2Bdf.Padding);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            Data.CustomCollision2Bytes = null;
            Data.UseCustomCollision2 = false;
            _lastError = "Failed to prepare custom mesh for export: " + ex.Message;
        }
    }

    private void Generate()
    {
        if (_generating || Data == null) return;
        // An explicit "Generate" click is a request to go back to real
        // auto-generation — without this, a previously-checked "Use this
        // mesh for map export" would keep exporting the OLD custom bytes
        // even after the user regenerated a fresh preview, silently
        // ignoring it exactly like the original bug this whole mechanism
        // fixes.
        Data.UseCustomCollision2 = false;
        _generating = true;
        _lastError = null;
        try
        {
            // Without this, a fully-Blender-imported map's
            // live preview would be empty/wrong (BuildPreviewGeometryModel
            // only ever saw the live procedural terrain, never the imported
            // geometry) even though the real exported collision2.bdf was
            // always correct - see BuildCollision2Preview's own comment.
            var importedTerrains = _terrainShell.GetImportedTerrainsForExport?.Invoke();
            // Must pass _session through so BuildCollision2Preview
            // reads WaterElevation/WaterEnabled via BuildInfoModel's document-
            // aware lookup, not SC2TerrainData's own legacy field (which only
            // updates on "Save Terrain") - see BuildPreviewGeometryModel's own
            // comment for the full explanation.
            var (model, hfield) = SC2TerrainExporter.BuildCollision2Preview(Data, importedTerrains, _session);
            _lastModel = model;
            _lastHfield = hfield;
            RebuildPreviewMesh();
        }
        catch (System.Exception ex)
        {
            _lastModel = null;
            _lastError = "Generation failed: " + ex.Message;
            SC2Log.LogException(ex);
        }
        finally
        {
            _generating = false;
            RepaintHost?.Invoke();
        }
    }

    // ── Blender round-trip ("Export Mesh To Blender" / "Import Corrected
    // Mesh" — previously stub buttons that only called SetStatus with a
    // placeholder "...queued." message, never did anything) ─────────────

    /// <summary>Writes the currently generated Collision2 preview mesh to a
    /// real .obj file. Returns a status string for the caller's SetStatus.</summary>
    public string ExportMeshToObj(string path)
    {
        if (_lastModel == null)
            return "No Collision2 mesh generated yet — click Generate first.";
        try
        {
            SC2Collision2ObjIO.Write(path, _lastModel.Vertices, _lastModel.Triangles);
            return $"Exported {_lastModel.Vertices.Count} vert(s) / {_lastModel.Triangles.Count} tri(s) to {path}";
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            return "Export failed: " + ex.Message;
        }
    }

    /// <summary>Reads a corrected .obj (edited/fixed in Blender), rebuilds
    /// the EDB/BVH spatial index the real collision2.bdf format needs
    /// (EdbTreeBuilder.Build — the exact same call BuildCollision2Model
    /// itself uses, so a hand-corrected mesh gets a real, valid tree, not
    /// just raw triangle soup), and replaces the live preview with it.
    /// Positions only — collision2.bdf carries no UV/normal data, so any
    /// material/texture info Blender attached is discarded on import.</summary>
    public string ImportCorrectedMeshObj(string path)
    {
        try
        {
            var (verts, tris) = SC2Collision2ObjIO.Read(path);
            if (verts.Count == 0 || tris.Count == 0)
                return "Imported .obj has no usable vertices/faces.";

            var flatVerts = new TerrainVertex[verts.Count];
            for (var i = 0; i < verts.Count; i++)
            {
                var (x, y, z) = verts[i];
                flatVerts[i] = new TerrainVertex { PosX = x, PosY = y, PosZ = z };
            }
            var flatTris = tris.ToArray();

            // Blender's own .obj exporter commonly emits duplicate vertex
            // POSITIONS at UV/hard-edge seams (it splits a vertex whenever
            // adjacent faces disagree on UV/normal, even though this format
            // carries neither) — collision2 import needs
            // vertex deduplication. Merges exact-or-near-identical positions
            // into one before building the BVH, so those duplicates don't
            // inflate the real vertex/triangle count (and therefore the
            // leaf/triangle-budget accounting) a hand-edited mesh brings
            // back into the editor. Also drops exact-duplicate POLYGONS (same
            // 3 world positions, any winding — the same straddle-duplication
            // class TerrainBdf.DeduplicateLayerTriangles fixes for terrain.bdf's
            // own BSP), which plain vertex merging alone does not catch: two
            // distinct, non-degenerate triangle records that reference the
            // same 3 (now-merged) vertices survive vertex dedup untouched.
            var vertsBefore = flatVerts.Length;
            var trisBefore = flatTris.Length;
            (flatVerts, flatTris) = DeduplicateVertices(flatVerts, flatTris);
            var mergedCount = vertsBefore - flatVerts.Length;
            var removedTriCount = trisBefore - flatTris.Length;

            // Same leaf threshold BuildCollision2Model uses (real
            // collision2.bdf leaves have a median of ~7 triangles).
            // useVanillaCandidateSplit: true — same reasoning as
            // BuildCollision2Model (SC2TerrainExporter.cs): the real vanilla
            // split selector is 9 fixed candidates + count-imbalance score,
            // not the SAH sweep this used to silently default to.
            const int collision2MinPolysPerLeaf = 8;
            var built = EdbTreeBuilder.Build(flatVerts, flatTris, 0, 0, flatVerts.Length, flatTris.Length, collision2MinPolysPerLeaf, useVanillaCandidateSplit: true);

            var model = new Collision2Model();
            foreach (var v in built.Vertices) model.Vertices.Add((v.PosX, v.PosY, v.PosZ));
            model.Triangles.AddRange(built.Triangles);
            model.Nodes.AddRange(built.Nodes);

            var bmin = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var bmax = new float[] { float.MinValue, float.MinValue, float.MinValue };
            foreach (var (x, y, z) in model.Vertices)
            {
                if (x < bmin[0]) bmin[0] = x; if (x > bmax[0]) bmax[0] = x;
                if (y < bmin[1]) bmin[1] = y; if (y > bmax[1]) bmax[1] = y;
                if (z < bmin[2]) bmin[2] = z; if (z > bmax[2]) bmax[2] = z;
            }
            model.MapCenter = new float[] { (bmin[0] + bmax[0]) / 2f, (bmin[1] + bmax[1]) / 2f, (bmin[2] + bmax[2]) / 2f };

            _lastModel = model;
            _lastHfield = null; // no longer derived from a live hfield projection
            if (Data.UseCustomCollision2) SyncCustomCollision2Bytes(); // keep export in sync with a re-import while the checkbox is already on
            RebuildPreviewMesh();
            UpdatePreviewVisibility();
            RepaintHost?.Invoke();
            return $"Imported corrected mesh: {model.Vertices.Count} vert(s) / {model.Triangles.Count} tri(s)" +
                   (mergedCount > 0 ? $" ({mergedCount} duplicate vert(s) merged)" : "") +
                   (removedTriCount > 0 ? $" ({removedTriCount} duplicate/degenerate polygon(s) removed)" : "") + ", " +
                   $"BVH rebuilt ({model.Nodes.Count} node(s)). Preview updated — use \"Save Corrected Mesh As collision2.bdf\" to write it to disk, " +
                   "or check \"Use this mesh for map export\" below to make real export use it directly.";
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            return "Import failed: " + ex.Message;
        }
    }

    /// <summary>Reads a real collision2.bdf file directly (not via the .obj
    /// round-trip) and replaces the live preview with it — e.g. loading an
    /// already-generated file from a previous session, or a real vanilla
    /// map's own collision2.bdf for reference/comparison. Uses the exact
    /// same Parse/BdfContainer path the rest of this codebase already
    /// trusts for round-tripping this format.</summary>
    public string ImportCollision2Bdf(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            var container = BdfContainer.Read(bytes, Collision2Bdf.Padding);
            var model = Collision2Bdf.Parse(container);
            return ImportCollision2Model(model, path);
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            return "Import failed: " + ex.Message;
        }
    }

    /// <summary>Same as ImportCollision2Bdf(path) above, but for a model
    /// already parsed elsewhere (e.g. the unified "Import Map" orchestrator,
    /// which extracts collision2.bdf to a temp file it deletes before this
    /// shell would get a chance to re-read it — passing the already-parsed
    /// model avoids needing to keep that temp file alive just for this).</summary>
    public string ImportCollision2Model(Collision2Model model, string sourceDescription)
    {
        var (mergedCount, removedTriCount) = DeduplicateCollision2Model(model);

        _lastModel = model;
        _lastHfield = null; // no longer derived from a live hfield projection
        if (Data.UseCustomCollision2) SyncCustomCollision2Bytes(); // keep export in sync with a re-import while the checkbox is already on
        RebuildPreviewMesh();
        UpdatePreviewVisibility();
        RepaintHost?.Invoke();
        return $"Imported {sourceDescription}: {model.Vertices.Count} vert(s) / {model.Triangles.Count} tri(s)" +
               (mergedCount > 0 ? $" ({mergedCount} duplicate vert(s) merged)" : "") +
               (removedTriCount > 0 ? $" ({removedTriCount} duplicate/degenerate polygon(s) removed)" : "") + ", " +
               $"{model.Nodes.Count} EDB node(s). Preview updated — check \"Use this mesh for map export\" below to make real export use it directly.";
    }

    /// <summary>Deduplicates a parsed Collision2Model's vertices AND
    /// polygons in place — dedup applies on collision2.bdf import too, not
    /// just the .obj
    /// round-trip path — and separately, polygon dedup is needed too since
    /// vertex merging alone doesn't catch two distinct,
    /// non-degenerate triangle records that reference the same 3
    /// (now-merged) vertices) and rebuilds a fresh BVH to match, since the
    /// model's existing EDB Nodes reference the OLD vertex/triangle indices
    /// and would silently point at the wrong geometry once those shift.
    /// No-ops (skips the rebuild entirely) when nothing was actually
    /// duplicated — the common case for a collision2.bdf this tool
    /// generated itself, which already comes from EdbTreeBuilder's own
    /// unique-vertex, duplicate-free output. Returns (vertices merged,
    /// polygons removed).</summary>
    private static (int MergedVerts, int RemovedTris) DeduplicateCollision2Model(Collision2Model model)
    {
        var flatVerts = new TerrainVertex[model.Vertices.Count];
        for (var i = 0; i < flatVerts.Length; i++)
        {
            var (x, y, z) = model.Vertices[i];
            flatVerts[i] = new TerrainVertex { PosX = x, PosY = y, PosZ = z };
        }
        var flatTris = model.Triangles.ToArray();

        var vertsBefore = flatVerts.Length;
        var trisBefore = flatTris.Length;
        var (dedupedVerts, dedupedTris) = DeduplicateVertices(flatVerts, flatTris);
        var mergedCount = vertsBefore - dedupedVerts.Length;
        var removedTriCount = trisBefore - dedupedTris.Length;
        if (mergedCount == 0 && removedTriCount == 0) return (0, 0);

        // useVanillaCandidateSplit: true — same reasoning as
        // BuildCollision2Model (SC2TerrainExporter.cs): the real vanilla
        // split selector is 9 fixed candidates + count-imbalance score,
        // not the SAH sweep this used to silently default to.
        const int collision2MinPolysPerLeaf = 8;
        var built = EdbTreeBuilder.Build(dedupedVerts, dedupedTris, 0, 0, dedupedVerts.Length, dedupedTris.Length, collision2MinPolysPerLeaf, useVanillaCandidateSplit: true);

        model.Vertices.Clear();
        foreach (var v in built.Vertices) model.Vertices.Add((v.PosX, v.PosY, v.PosZ));
        model.Triangles.Clear();
        model.Triangles.AddRange(built.Triangles);
        model.Nodes.Clear();
        model.Nodes.AddRange(built.Nodes);
        return (mergedCount, removedTriCount);
    }

    /// <summary>Merges vertices at (quantized) identical positions into
    /// one, remapping triangle indices and dropping any triangle that
    /// degenerates to fewer than 3 distinct vertices as a result. Positions
    /// are rounded to 5 decimal places before comparison — exact-equal
    /// duplicates (the common Blender-seam case) always merge; this also
    /// tolerates the tiny floating-point drift a plain-text .obj round-trip
    /// can introduce without accidentally welding two genuinely distinct,
    /// merely-close vertices. Also drops exact-duplicate POLYGONS via
    /// <see cref="DeduplicatePolygons"/> once vertices are merged — two
    /// distinct, non-degenerate triangle records that reference the same 3
    /// (now-merged) vertices are a separate defect class plain vertex
    /// merging does not catch on its own.</summary>
    private static (TerrainVertex[] Vertices, TerrainTriangle[] Triangles) DeduplicateVertices(TerrainVertex[] verts, TerrainTriangle[] tris)
    {
        var remap = new int[verts.Length];
        var keyToIndex = new Dictionary<(float, float, float), int>();
        var unique = new List<TerrainVertex>(verts.Length);
        for (var i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            var key = (Mathf.Round(v.PosX * 100000f) / 100000f, Mathf.Round(v.PosY * 100000f) / 100000f, Mathf.Round(v.PosZ * 100000f) / 100000f);
            if (!keyToIndex.TryGetValue(key, out var idx))
            {
                idx = unique.Count;
                keyToIndex[key] = idx;
                unique.Add(v);
            }
            remap[i] = idx;
        }

        var mergedTris = new List<TerrainTriangle>(tris.Length);
        foreach (var t in tris)
        {
            var a = (uint)remap[t.V0]; var b = (uint)remap[t.V1]; var c = (uint)remap[t.V2];
            if (a == b || b == c || a == c) continue; // degenerate after merge
            mergedTris.Add(new TerrainTriangle { V0 = a, V1 = b, V2 = c });
        }
        return (unique.ToArray(), DeduplicatePolygons(mergedTris, unique));
    }

    /// <summary>Drops triangles that are exact geometric duplicates of an
    /// earlier-appearing triangle — same 3 world vertex positions (rounded
    /// to 1e-3 units), regardless of winding order or which vertex indices
    /// they reference. Plain vertex merging (above) does not catch this on
    /// its own: two distinct, non-degenerate triangle records that end up
    /// referencing the same 3 (now-merged) vertices survive it untouched.
    /// Same position-canonical approach as
    /// TerrainBdf.DeduplicateLayerTriangles (kept as its own small copy here
    /// rather than a shared reference, since that method is private to
    /// TerrainBdf.cs) — confirmed root cause there on SC2_D3_Eclipse: a
    /// from-Blender exporter can emit the same physical triangle twice via
    /// straddle-duplication, and a hand-corrected/re-imported collision2
    /// mesh can carry the same class of duplicate.</summary>
    private static TerrainTriangle[] DeduplicatePolygons(List<TerrainTriangle> tris, List<TerrainVertex> verts)
    {
        const float scale = 1000f; // round to 1e-3 world units, matches TerrainBdf's own convention
        var seen = new HashSet<(long, long, long, long, long, long, long, long, long)>();
        var result = new List<TerrainTriangle>(tris.Count);
        foreach (var t in tris)
        {
            var pts = new (long x, long y, long z)[3]
            {
                RoundPos(verts[(int)t.V0], scale),
                RoundPos(verts[(int)t.V1], scale),
                RoundPos(verts[(int)t.V2], scale),
            };
            System.Array.Sort(pts, (a, b) =>
            {
                var c = a.x.CompareTo(b.x);
                if (c != 0) return c;
                c = a.y.CompareTo(b.y);
                return c != 0 ? c : a.z.CompareTo(b.z);
            });
            var key = (pts[0].x, pts[0].y, pts[0].z, pts[1].x, pts[1].y, pts[1].z, pts[2].x, pts[2].y, pts[2].z);
            if (seen.Add(key))
                result.Add(t);
        }
        return result.ToArray();
    }

    private static (long x, long y, long z) RoundPos(TerrainVertex v, float scale) =>
        ((long)Mathf.Round(v.PosX * scale), (long)Mathf.Round(v.PosY * scale), (long)Mathf.Round(v.PosZ * scale));

    /// <summary>Writes the current preview model (whether from Generate or
    /// ImportCorrectedMeshObj) as a real, standalone collision2.bdf — the
    /// same Serialize/BdfContainer path the main map export uses.</summary>
    public string SaveCorrectedMeshAsCollision2Bdf(string path)
    {
        if (_lastModel == null)
            return "No mesh to save — Generate or Import a corrected mesh first.";
        try
        {
            var (data, offsetTable) = Collision2Bdf.Serialize(_lastModel, BdfPlatform.Pc);
            var bytes = new BdfContainer { Platform = BdfPlatform.Pc, Data = data, OffsetTable = offsetTable, NumFiles = 1 }.Write(Collision2Bdf.Padding);
            System.IO.File.WriteAllBytes(path, bytes);
            return $"Wrote {path} ({bytes.Length} bytes).";
        }
        catch (System.Exception ex)
        {
            SC2Log.LogException(ex);
            return "Save failed: " + ex.Message;
        }
    }

    private void RebuildPreviewMesh()
    {
        if (_lastModel == null) return;
        EnsurePreviewObject();

        var verts = new Vector3[_lastModel.Vertices.Count];
        for (var i = 0; i < verts.Length; i++)
        {
            var (x, y, z) = _lastModel.Vertices[i];
            verts[i] = new Vector3(x, y, z);
        }

        _previewMesh.Clear();
        _previewMesh.indexFormat = verts.Length > 65000
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        _previewMesh.vertices = verts;

        if (_wireframeMode)
        {
            // Each triangle contributes its 3 edges as one line segment
            // pair each (MeshTopology.Lines draws every consecutive index
            // pair as a segment) — a real GPU line-list mesh, not an
            // Editor-only Handles/Gizmos wireframe (those are no-ops in a
            // standalone build, see UnityEditorRuntimeShim), so this shows
            // correctly in both the Editor Game view and a real build.
            var lineIndices = new int[_lastModel.Triangles.Count * 6];
            for (var i = 0; i < _lastModel.Triangles.Count; i++)
            {
                var t = _lastModel.Triangles[i];
                var a = (int)t.V0; var b = (int)t.V1; var c = (int)t.V2;
                var baseIdx = i * 6;
                lineIndices[baseIdx + 0] = a; lineIndices[baseIdx + 1] = b;
                lineIndices[baseIdx + 2] = b; lineIndices[baseIdx + 3] = c;
                lineIndices[baseIdx + 4] = c; lineIndices[baseIdx + 5] = a;
            }
            _previewMesh.SetIndices(lineIndices, MeshTopology.Lines, 0);
            _previewRenderer.sharedMaterial = _wireframeMaterial;
        }
        else
        {
            var tris = new int[_lastModel.Triangles.Count * 3];
            for (var i = 0; i < _lastModel.Triangles.Count; i++)
            {
                var t = _lastModel.Triangles[i];
                tris[i * 3] = (int)t.V0; tris[i * 3 + 1] = (int)t.V1; tris[i * 3 + 2] = (int)t.V2;
            }
            _previewMesh.SetIndices(tris, MeshTopology.Triangles, 0);
            _previewMesh.RecalculateNormals();
            _previewRenderer.sharedMaterial = _solidMaterial;
        }
        _previewMesh.RecalculateBounds();

        UpdatePreviewVisibility();
    }

    private void EnsurePreviewObject()
    {
        if (_previewGO != null) return;

        _previewGO = new GameObject("SC2Collision2Preview") { hideFlags = HideFlags.DontSave };
        _previewFilter = _previewGO.AddComponent<MeshFilter>();
        _previewRenderer = _previewGO.AddComponent<MeshRenderer>();
        _previewMesh = new Mesh { name = "SC2Collision2PreviewMesh" };
        _previewFilter.sharedMesh = _previewMesh;

        _solidMaterial = new Material(SC2FallbackShader.Get());
        // Semi-transparent and a distinct color from the visual terrain, so
        // the collision volume's shape/coverage reads clearly without fully
        // hiding the ground it sits on top of.
        SetupTransparentMaterial(_solidMaterial, new Color(0.2f, 0.8f, 1f, 0.35f));

        // Opaque, bright, unlit-ish flat color — wireframe edges need to
        // read clearly against the terrain regardless of lighting, and a
        // Lines-topology mesh has no coherent normals for a lit shader to
        // shade against anyway.
        _wireframeMaterial = SC2PreviewMaterialFactory.MakeColor("SC2Collision2Wireframe", new Color(1f, 0.9f, 0.1f, 1f));

        _previewRenderer.sharedMaterial = _solidMaterial;
        _previewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewRenderer.receiveShadows = false;
    }

    /// <summary>Best-effort transparency setup across whichever shader
    /// SC2FallbackShader.Get() resolves to (URP/Lit, Standard, or this
    /// project's own SC2RuntimeFallback) — every property set is guarded by
    /// HasProperty so an unsupported shader just no-ops instead of
    /// throwing.</summary>
    private static void SetupTransparentMaterial(Material mat, Color color)
    {
        mat.color = color;
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // URP: 1 = Transparent
        if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 3f); // Standard: 3 = Transparent
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
    }

    public void Dispose()
    {
        if (_previewGO != null)
        {
            Object.Destroy(_previewGO);
            _previewGO = null;
        }
        if (_solidMaterial != null) { Object.Destroy(_solidMaterial); _solidMaterial = null; }
        if (_wireframeMaterial != null) { Object.Destroy(_wireframeMaterial); _wireframeMaterial = null; }
    }
}
