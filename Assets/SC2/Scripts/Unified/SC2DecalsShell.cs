using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using BdfIO.Formats.Decals;
using SC2.MapEditor.Document;

/// <summary>Runtime-first Decals UI. The same class draws in Game View and
/// standalone builds and edits only SC2MapDocument.Decals.</summary>
public sealed class SC2DecalsShell : IDisposable
{
    private readonly SC2MapSession _session;
    // Decals respect the map's Symmetry mode
    // (SC2TerrainData.Symmetry/SymmetryCenter/RadialCount), which needs a live
    // reference to the terrain shell that owns those, same pattern
    // SC2ObjectsShell already uses for its own symmetry support.
    private readonly SC2TerrainShell _terrainShell;
    private readonly SC2DecalSurfaceProvider _surfaces = new();
    private readonly List<Texture2D> _sources = new();
    private readonly List<string> _sourceNames = new();
    // Parallel to _sources by index — null where that source image has no
    // normal-map counterpart imported yet. ImportSourceNormalImage attaches
    // to the MOST RECENTLY imported diffuse source, matching the natural
    // "import diffuse, then import its normal" workflow order.
    private readonly List<Texture2D> _normalSources = new();
    private Texture2D _diffuseAtlas;
    private Texture2D _normalAtlas;
    private GameObject _previewRoot;
    private Mesh _previewMesh;
    private Material _previewMaterial;
    private long _renderedRevision = -1;
    private int _renderedTerrainVersion = -1;
    // Per-placement preview-mesh cache — see EnsurePreview's own doc
    // comment and MarkPreviewDirtyInRadius for why this exists (avoiding a
    // full Collect+GeometryBuilder re-run for every placement on every
    // single terrain edit, most of which are nowhere near most decals).
    private readonly Dictionary<string, (List<DecalVertex> Vertices, List<int> Indices)> _placementFragmentCache = new();
    private readonly HashSet<string> _dirtyPlacementIds = new();
    private int _selectedEntry = -1;
    private int _selectedPlacement = -1;
    private Vector2 _catalogScroll;
    private Vector2 _atlasPan;
    private float _atlasZoom = 0.8f;
    private string _search = "";
    private string _status = "Decals ready.";
    private bool _atlasWindowOpen;
    // Real imported terrain.bdf water is
    // separate real geometry (not just the decorative, collider-less
    // SC2WaterPlane) sitting above the actual lake/sea floor within the
    // same MeshCollider — without this, a click anywhere near water always
    // anchored the decal at the water surface itself, never the floor
    // beneath it. On by default; a decal
    // meant to sit right at the water's own surface plane can turn this off.
    private bool _placeThroughWater = true;
    // Catalog/Place/Edit are one always-
    // visible panel instead of 3 separate sub-tabs (Atlas Editor stays a
    // modal — a wide, precise canvas is worth more than panel unification
    // there). With Catalog/Place/Edit no longer separate views, a click in
    // the viewport is ambiguous on its own (place a new decal, or select an
    // existing one?) — Mode disambiguates it, set implicitly by what the
    // user last clicked in the panel (a catalog entry arms Place; a
    // placement, in the list or viewport, arms Edit) rather than a
    // dedicated toggle control.
    private InteractionMode _mode = InteractionMode.Edit;
    private int _transformVersion;
    private int _renderedTransformVersion = -1;
    private DecalTransformMode _transformMode;
    private SC2DecalPlacement _transformOriginal;
    private SC2DecalPlacement _transformPreview;
    // Populated by EnsurePreview while a group drag is active, keyed by
    // placement Id — lets DrawPlacementOutlines' 2D markers track the same
    // live symmetric positions the 3D mesh preview already shows.
    private Dictionary<string, SC2DecalPlacement> _transformGroupPreview;
    private Vector2 _transformStartMouse;
    private Vector2 _transformAnchorScreen;
    private float _transformStartDistance;
    private string _placementDraftId;
    private SC2DecalPlacement _placementDraft;
    private string _defaultsDraftId;
    private SC2DecalAtlasEntry _defaultsDraft;
    private string _atlasDraftId;
    private SC2DecalAtlasEntry _atlasDraft;
    private AtlasDragMode _atlasDragMode;
    private int _atlasDragEntryIndex = -1;

    private enum DecalTransformMode { None, Move, Rotate, Scale }
    // Lets the Atlas Editor's rects be nudged/resized visually against the
    // atlas image itself, not just typed as numbers.
    private enum AtlasDragMode { None, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private enum InteractionMode { Place, Edit }

    private SC2DecalsDocument Doc => _session?.Document?.Decals;
    public Action RepaintHost;

    public SC2DecalsShell(SC2MapSession session, SC2TerrainShell terrainShell)
    {
        _session = session;
        _terrainShell = terrainShell;
        // SC2TerrainRuntime's chunk meshes carry no real
        // Collider at all, so Physics.Raycast (and Collect's matching
        // MeshCollider scan) can never find them — wire the same live
        // analytic height source Costs/Objects already trust (SC2LiveHeight
        // Grid, via _terrainShell) as a fallback for both placement-click
        // raycasting and the clip-target geometry Collect gathers for
        // preview/export. See SC2DecalSurfaceProvider's own doc comments.
        _surfaces.LiveTerrainRaycast = ray =>
            _terrainShell != null && _terrainShell.TryRaycastLiveHeightGrid(ray, out var hit) ? hit : (Vector3?)null;
        _surfaces.LiveTerrainSampleHeight = (x, z) => _terrainShell != null ? _terrainShell.SampleLiveHeightGrid(x, z) : 0f;
    }

    public void DrawEmbeddedGUI(float panelWidth)
    {
        var w = Mathf.Max(1f, panelWidth);
        GUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (Doc == null) { GUILayout.Label("No active map document."); GUILayout.EndVertical(); return; }
        GUILayout.Label(_status, GUI.skin.box, GUILayout.Width(w - 6));
        DrawAtlasSection(w);
        GUILayout.Space(8);
        DrawSelectedEntrySection(w);
        GUILayout.Space(8);
        DrawPlacementsSection(w);
        GUILayout.EndVertical();
        EnsurePreview();
        DrawAtlasModal();
    }

    private void DrawAtlasSection(float w)
    {
        GUILayout.Label("Atlas", GUI.skin.label);
        // The actual relative path written
        // into decals.bdf must be visible AND directly editable here, not
        // just a read-only label. Typing a path tries to resolve/preview a
        // texture at that path (SC2TerrainMaterialFactory.LoadTexturePublicAny);
        // if nothing resolves locally (e.g. a real vanilla path not yet
        // extracted into this project), the typed path is still saved and
        // exported as-is — see DrawAtlasPathField.
        DrawAtlasPathField(w, normal: false);
        DrawAtlasPathField(w, normal: true);
        // It wasn't clear that "Normal"
        // here loads a WHOLE separate atlas texture file (shared by every
        // decal via the same atlas UV rects), not a per-decal normal map —
        // this note plus the two atlas-path labels above spell that out;
        // the two buttons themselves are relabeled to say "atlas" too.
        GUILayout.Label("Diffuse/Normal atlases below are each ONE whole texture, shared by every decal in this catalog via the same UV rect.", GUI.skin.box, GUILayout.Width(w - 8));
        // Without a fixed width, button rows here can overflow the panel's
        // real edge (text like "Import decal ima[ge]" cut off mid-word),
        // since GUILayout.Button with no width option
        // requests its own natural text width regardless of what the
        // container can actually show — unlike a nested BeginVertical/
        // FlexibleSpace construct (which correctly inherits the ancestor's
        // real width), sibling Buttons in a plain BeginHorizontal just sum
        // their natural widths, overflowing once that sum exceeds the
        // panel. Giving the row itself a fixed width and each button
        // ExpandWidth(true) makes them share that fixed total instead.
        GUILayout.BeginHorizontal(GUILayout.Width(w));
        if (GUILayout.Button("Choose diffuse atlas...", GUILayout.ExpandWidth(true))) ChooseAtlas(false);
        if (GUILayout.Button("Choose normal atlas...", GUILayout.ExpandWidth(true))) ChooseAtlas(true);
        GUILayout.EndHorizontal();
        // "Import decal image" (diffuse) / its normal-map counterpart below
        // both feed the SEPARATE "Build atlas" generator (_sources/
        // _normalSources), not the two "Choose ... atlas..." buttons above —
        // those load an already-finished atlas file directly instead.
        GUILayout.BeginHorizontal(GUILayout.Width(w));
        if (GUILayout.Button("Import decal image (diffuse)", GUILayout.ExpandWidth(true))) ImportSourceImage();
        if (GUILayout.Button("Import decal image (normal)", GUILayout.ExpandWidth(true))) ImportSourceNormalImage();
        GUILayout.EndHorizontal();
        if (_sources.Count > 0 && GUILayout.Button($"Build atlas ({_sources.Count} images, {_normalSources.Count(t => t != null)} with normals)", GUILayout.Width(w))) BuildAtlas();
        GUILayout.BeginHorizontal(GUILayout.Width(w));
        if (GUILayout.Button("New rect", GUILayout.ExpandWidth(true))) AddEntry();
        GUI.enabled = _diffuseAtlas != null;
        if (GUILayout.Button(_atlasWindowOpen ? "Atlas Editor open" : "Open Atlas Editor", GUILayout.ExpandWidth(true))) { _atlasWindowOpen = true; CancelTransform(false); }
        GUI.enabled = true;
        if (GUILayout.Button("Validate", GUILayout.ExpandWidth(true))) Validate();
        GUILayout.EndHorizontal();
        var keepOriginal = GUILayout.Toggle(Doc.KeepOriginalBakedLayer, "Keep original baked layer on export", GUILayout.Width(w));
        if (keepOriginal != Doc.KeepOriginalBakedLayer)
        {
            var next = Doc.Clone(); next.KeepOriginalBakedLayer = keepOriginal;
            Commit("Change Original Decal Layer Export", next);
        }
        _search = GUILayout.TextField(_search ?? "", GUILayout.Width(w - 8));

        _catalogScroll = GUILayout.BeginScrollView(_catalogScroll, GUILayout.Width(w - 4), GUILayout.MinHeight(140), GUILayout.MaxHeight(220));
        var filtered = Doc.Entries.Select((e, i) => (e, i)).Where(x => string.IsNullOrWhiteSpace(_search) || x.e.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
        foreach (var item in filtered)
        {
            var selected = item.i == _selectedEntry;
            GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Width(w - 8));
            var uv = AtlasUv(item.e);
            var rect = GUILayoutUtility.GetRect(48, 48, GUILayout.Width(48), GUILayout.Height(48));
            if (_diffuseAtlas != null) GUI.DrawTextureWithTexCoords(rect, _diffuseAtlas, uv, true); else GUI.Box(rect, "DEC");
            // Picking a catalog entry arms Place mode — see the Mode field's
            // own doc comment for why this replaces a dedicated tab/toggle.
            if (GUILayout.Button($"{item.e.Name}\n{item.e.Width} x {item.e.Height}" + (selected ? "  [selected]" : ""), GUILayout.Height(48), GUILayout.ExpandWidth(true)))
            {
                _selectedEntry = item.i;
                _mode = InteractionMode.Place;
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        if (_selectedEntry >= 0 && _selectedEntry < Doc.Entries.Count)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(w));
            if (GUILayout.Button("Duplicate", GUILayout.ExpandWidth(true))) DuplicateEntry();
            if (GUILayout.Button("Delete", GUILayout.ExpandWidth(true))) DeleteEntry();
            GUILayout.EndHorizontal();
        }
        GUILayout.Label($"Entries: {Doc.Entries.Count}  Placements: {Doc.Placements.Count}");
    }

    private void DrawSelectedEntrySection(float w)
    {
        if (!TryEntry(out var e)) return;
        GUILayout.Label("Selected decal", GUI.skin.label);
        var preview = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
        if (_diffuseAtlas != null) GUI.DrawTextureWithTexCoords(preview, _diffuseAtlas, AtlasUv(e), true);
        GUILayout.Label(e.Name);
        if (_defaultsDraft == null || _defaultsDraftId != e.Id)
        {
            _defaultsDraftId = e.Id;
            _defaultsDraft = e.Clone();
        }
        var defaults = _defaultsDraft;
        var defaultsKey = "decal_defaults_" + defaults.Id + "_";
        defaults.DefaultWorldWidth = SC2EnvFieldGui.FloatField("Width", defaults.DefaultWorldWidth, defaultsKey + "width");
        defaults.DefaultWorldLength = SC2EnvFieldGui.FloatField("Length", defaults.DefaultWorldLength, defaultsKey + "length");
        defaults.DefaultProjectionDepth = SC2EnvFieldGui.FloatField("Projection depth", defaults.DefaultProjectionDepth, defaultsKey + "depth");
        if ((defaults.DefaultWorldWidth != e.DefaultWorldWidth || defaults.DefaultWorldLength != e.DefaultWorldLength ||
             defaults.DefaultProjectionDepth != e.DefaultProjectionDepth) && GUILayout.Button("Apply placement defaults", GUILayout.Width(w)))
        {
            var next = Doc.Clone();
            next.Entries[_selectedEntry] = defaults.Clone();
            Commit("Edit Decal Placement Defaults", next);
            _defaultsDraft = next.Entries[_selectedEntry].Clone();
        }
        _placeThroughWater = GUILayout.Toggle(_placeThroughWater, "Place through water (click reaches the real floor beneath water instead of stopping at the water surface)", GUILayout.Width(w));
        if (_mode == InteractionMode.Place)
        {
            GUILayout.Label("Click the final terrain surface to place. Imported BDF and runtime terrain use the same raycast/projector path.", GUI.skin.box, GUILayout.Width(w - 8));
            // Without this button, there is no way to leave Place mode
            // without either clicking an existing decal (which switches to
            // Edit as a side effect) or placing another one first.
            if (GUILayout.Button("Exit place mode", GUILayout.Width(w)))
                _mode = InteractionMode.Edit;
        }
        // Re-arms Place mode without needing to reselect the same catalog
        // row — useful after editing a placement (which switched to Edit
        // mode) when the user wants to place more of this same decal.
        else if (GUILayout.Button("Place mode (click terrain to add)", GUILayout.Width(w)))
            _mode = InteractionMode.Place;
    }

    private void DrawPlacementsSection(float w)
    {
        GUILayout.Label($"Authored placements: {Doc.Placements.Count}");
        if (_selectedPlacement < 0 || _selectedPlacement >= Doc.Placements.Count) { GUILayout.Label("Select a decal in the viewport or list.", GUI.skin.box, GUILayout.Width(w - 8)); DrawPlacementList(w); return; }
        var p = Doc.Placements[_selectedPlacement];
        if (_placementDraft == null || _placementDraftId != p.Id)
        {
            _placementDraftId = p.Id;
            _placementDraft = p.Clone();
        }
        var n = _placementDraft;
        var placementKey = "decal_placement_" + n.Id + "_";
        n.Name = SC2EnvFieldGui.TextField("Name", n.Name, placementKey + "name");
        n.Rotation = SC2EnvFieldGui.FloatField("Rotation", n.Rotation, placementKey + "rotation");
        n.Width = SC2EnvFieldGui.FloatField("Width", n.Width, placementKey + "width");
        n.Length = SC2EnvFieldGui.FloatField("Length", n.Length, placementKey + "length");
        n.ProjectionDepth = SC2EnvFieldGui.FloatField("Projection depth", n.ProjectionDepth, placementKey + "depth");
        if (GUILayout.Button("Auto (fix clipping on slopes)", GUILayout.Width(w)))
        {
            // Existing decals placed before this fix (or manually set too
            // shallow) can still be clipped on sloped terrain — this lets
            // the user fix an already-placed decal without deleting and
            // re-placing it. Sets the DRAFT only, same as typing a value
            // into the field above — still goes through "Apply properties"
            // below, so it's undo-tracked and stays in sync with the rest
            // of a symmetry group like any other edit here.
            n.ProjectionDepth = ComputeAdequateProjectionDepth(new Vector3(n.Position[0], n.Position[1], n.Position[2]), n.Rotation, n.Width, n.Length, n.ProjectionDepth);
        }
        n.HeightOffset = SC2EnvFieldGui.FloatField("Height offset", n.HeightOffset, placementKey + "offset");
        n.AlignToSurface = GUILayout.Toggle(n.AlignToSurface, "Align to surface"); n.Enabled = GUILayout.Toggle(n.Enabled, "Enabled");
        if (!PlacementEquals(p, n) && GUILayout.Button("Apply properties", GUILayout.Width(w)))
        {
            var next = Doc.Clone();
            var applied = n.Clone();
            next.Placements[_selectedPlacement] = applied;
            // Editing a symmetric decal's properties
            // (including Rotation) updates its whole group together.
            SyncSymmetryGroup(next, applied);
            Commit("Edit Decal Properties", next);
            _placementDraft = next.Placements[_selectedPlacement].Clone();
        }
        GUILayout.BeginHorizontal(GUILayout.Width(w));
        if (GUILayout.Button("Duplicate", GUILayout.ExpandWidth(true))) DuplicatePlacement();
        if (GUILayout.Button("Delete", GUILayout.ExpandWidth(true))) DeletePlacement();
        GUILayout.EndHorizontal();
        GUILayout.Label("G: move  R: rotate  S: scale  Delete: remove", GUI.skin.box, GUILayout.Width(w - 8));
        DrawPlacementList(w);
    }

    // Was a bare "● Name" / "○ Name" button per row —
    // hard to scan on a map with many decals, no visual confirmation of
    // which atlas shape a row actually refers to. Same thumbnail lookup
    // Catalog already uses (AtlasUv), plus position/size so a row is
    // identifiable without having to select it first.
    private void DrawPlacementList(float w)
    {
        for (var i = 0; i < Doc.Placements.Count; i++)
        {
            var p = Doc.Placements[i];
            GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Width(w - 8));
            var thumb = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
            var entry = Doc.Entries.FirstOrDefault(e => e.Id == p.AtlasEntryId);
            if (entry != null && _diffuseAtlas != null) GUI.DrawTextureWithTexCoords(thumb, _diffuseAtlas, AtlasUv(entry), true);
            else GUI.Box(thumb, "");
            var pos = p.Position != null && p.Position.Length >= 3 ? p.Position : new float[3];
            var label = $"{(i == _selectedPlacement ? "● " : "○ ")}{p.Name}\n({pos[0]:0.#}, {pos[2]:0.#})  {p.Width:0.#}x{p.Length:0.#}" + (p.Enabled ? "" : "  [disabled]");
            // Selecting a placement arms Edit mode — see the Mode field's
            // own doc comment.
            if (GUILayout.Button(label, GUILayout.Height(32), GUILayout.ExpandWidth(true))) { _selectedPlacement = i; _mode = InteractionMode.Edit; }
            GUILayout.EndHorizontal();
        }
    }

    public void OnGameViewGUI(Camera camera, Rect viewport, Vector2 mouse, bool allowInput)
    {
        if (Doc == null || camera == null) return;
        EnsurePreview();
        if (_atlasWindowOpen) return;
        DrawPlacementOutlines(camera);
        if (Event.current == null) return;
        var evt = Event.current;
        if (_transformMode != DecalTransformMode.None)
        {
            TickTransform(camera, mouse, allowInput);
            return;
        }
        if (!allowInput) return;
        // After typing into a left-panel numeric field
        // (Rotation/Width/... in the placements section, or a rect field in
        // the Atlas Editor), the decal would otherwise no longer be
        // selectable or movable, because an IMGUI text field doesn't lose
        // keyboard focus just because a
        // click lands somewhere else — a click in the 3D viewport isn't
        // itself a GUI control, so nothing ever reclaimed focus from it, and
        // a still-focused field silently eats G/R/S as typed characters
        // instead of letting them reach HandleEditHotkey. Reclaim focus the
        // moment a real viewport click or hotkey is about to be processed.
        if ((evt.type == EventType.MouseDown || evt.type == EventType.KeyDown) && EditorGUIUtility.editingTextField)
            GUI.FocusControl(null);
        if (evt.type == EventType.MouseDown && evt.button == 0)
        {
            // If Mode were purely "what did you last
            // click in the panel," a click on the viewport would ALWAYS place
            // a new decal while Mode was Place, even when the cursor was
            // sitting right on an existing marker. Selecting what's under
            // the cursor should always win over placing on top of it —
            // matches how every other click-to-place/pick tool in this
            // editor already behaves. Only when nothing is under the
            // cursor does Mode decide what the click actually does.
            if (TryPickPlacement(camera, mouse, out var pickedIndex))
            {
                _selectedPlacement = pickedIndex;
                _mode = InteractionMode.Edit;
                RepaintHost?.Invoke();
                evt.Use();
            }
            else if (_mode == InteractionMode.Place && TryEntry(out var e) && _surfaces.Raycast(ScreenRay(camera, mouse), _placeThroughWater, out var hit))
            {
                var next = Doc.Clone();
                // One shared GroupId per click when a symmetry mode is
                // active, so this click's siblings can be deleted/edited
                // together later — BuildSymmetrySlots returns just the
                // single seed slot when Symmetry is None, so this is a
                // no-op single placement in that case (empty GroupId,
                // matching how an ungrouped decal always behaves).
                var slots = BuildSymmetrySlots(hit.point, hit.normal, 0f);
                var groupId = slots.Count > 1 ? Guid.NewGuid().ToString("N") : "";
                for (var i = 0; i < slots.Count; i++)
                {
                    var depth = ComputeAdequateProjectionDepth(slots[i].Position, slots[i].Rotation, e.DefaultWorldWidth, e.DefaultWorldLength, e.DefaultProjectionDepth);
                    next.Placements.Add(new SC2DecalPlacement
                    {
                        Name = e.Name, AtlasEntryId = e.Id, Position = A(slots[i].Position), SurfaceNormal = A(slots[i].Normal),
                        Rotation = slots[i].Rotation, Width = e.DefaultWorldWidth, Length = e.DefaultWorldLength, ProjectionDepth = depth,
                        SymmetryGroupId = groupId, SymmetrySlotIndex = i
                    });
                }
                Commit("Place Decal", next); _selectedPlacement = next.Placements.Count - slots.Count; evt.Use();
            }
            else if (_mode == InteractionMode.Edit && _selectedPlacement != -1)
            {
                // Clicked empty space with nothing under the cursor while
                // in Edit mode — deselect, matching the old PickPlacement's
                // own behavior when it found nothing nearby.
                _selectedPlacement = -1;
                RepaintHost?.Invoke();
                evt.Use();
            }
        }
        else if (_mode == InteractionMode.Edit && evt.type == EventType.KeyDown) HandleEditHotkey(camera, mouse, evt);
    }

    private void DrawAtlasModal()
    {
        if (!_atlasWindowOpen || Doc == null || _diffuseAtlas == null) return;
        var width = Mathf.Min(1120f, Mathf.Max(720f, Screen.width - 80f));
        var height = Mathf.Min(780f, Mathf.Max(540f, Screen.height - 100f));
        var rect = new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height);
        GUI.ModalWindow(0x5C2DEC4, rect, DrawAtlasWindow, "Decal Atlas Editor");
    }

    private void DrawAtlasWindow(int windowId)
    {
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Diffuse atlas  {Doc.AtlasWidth} x {Doc.AtlasHeight}");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", GUILayout.Width(80))) { _atlasWindowOpen = false; _atlasDragMode = AtlasDragMode.None; _atlasDragEntryIndex = -1; }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(280), GUILayout.ExpandHeight(true));
        if (!TryEntry(out var entry))
        {
            GUILayout.Label("No atlas rect selected.", GUI.skin.box);
            if (GUILayout.Button("Create first rect")) AddEntry();
        }
        else
        {
            if (_atlasDraft == null || _atlasDraftId != entry.Id)
            {
                _atlasDraftId = entry.Id;
                _atlasDraft = entry.Clone();
            }
            var e = _atlasDraft;
            var key = "decal_atlas_" + e.Id + "_";
            e.Name = SC2EnvFieldGui.TextField("Name", e.Name, key + "name");
            e.X = SC2EnvFieldGui.IntField("X", e.X, key + "x");
            e.Y = SC2EnvFieldGui.IntField("Y", e.Y, key + "y");
            e.Width = SC2EnvFieldGui.IntField("Width", e.Width, key + "width");
            e.Height = SC2EnvFieldGui.IntField("Height", e.Height, key + "height");
            e.DefaultWorldWidth = SC2EnvFieldGui.FloatField("World width", e.DefaultWorldWidth, key + "world_width");
            e.DefaultWorldLength = SC2EnvFieldGui.FloatField("World length", e.DefaultWorldLength, key + "world_length");
            e.DefaultProjectionDepth = SC2EnvFieldGui.FloatField("Projection depth", e.DefaultProjectionDepth, key + "depth");
            if (GUILayout.Button("Apply rect"))
            {
                ClampRect(e);
                var next = Doc.Clone();
                next.Entries[_selectedEntry] = e.Clone();
                Commit("Edit Decal Atlas Rect", next);
                _atlasDraft = next.Entries[_selectedEntry].Clone();
                _status = $"Applied {e.Name}: {e.Width} x {e.Height} at {e.X}, {e.Y}.";
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Previous")) _selectedEntry = Mathf.Max(0, _selectedEntry - 1);
            if (GUILayout.Button("Next")) _selectedEntry = Mathf.Min(Doc.Entries.Count - 1, _selectedEntry + 1);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("New rect")) AddEntry();
            if (GUILayout.Button("Duplicate")) DuplicateEntry();
            if (GUILayout.Button("Delete")) DeleteEntry();
        }
        GUILayout.Space(8);
        GUILayout.Label("Wheel: zoom\nMiddle mouse: pan\nDrag a rect to move it, drag a corner handle to resize.\nRect values use atlas pixels.", GUI.skin.box);
        GUILayout.EndVertical();
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        var canvasViewport = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        DrawAtlasCanvas(canvasViewport, Event.current != null ? Event.current.mousePosition : Vector2.zero, true);
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private const float AtlasHandleSize = 8f;

    private void DrawAtlasCanvas(Rect viewport, Vector2 mouse, bool allowInput)
    {
        if (_diffuseAtlas == null) return;
        var size = Mathf.Min(viewport.width, viewport.height) * _atlasZoom;
        var canvas = new Rect(viewport.center.x - size * .5f + _atlasPan.x, viewport.center.y - size * .5f + _atlasPan.y, size, size);
        GUI.DrawTexture(canvas, _diffuseAtlas, ScaleMode.StretchToFill, true);

        Rect selectedCanvasRect = default;
        var haveSelectedRect = false;
        for (var i = 0; i < Doc.Entries.Count; i++)
        {
            // Live entries render from Doc, except the one currently being
            // dragged — that one shows the in-progress draft instead, so the
            // box on screen tracks the mouse instead of only updating once
            // the drag commits on mouse-up.
            var e = (i == _selectedEntry && _atlasDraft != null) ? _atlasDraft : Doc.Entries[i];
            var r = EntryCanvasRect(canvas, e);
            var old = GUI.color; GUI.color = i == _selectedEntry ? Color.yellow : new Color(0, 1, 1, .8f); GUI.Box(r, i == _selectedEntry ? e.Name : ""); GUI.color = old;
            if (i == _selectedEntry) { selectedCanvasRect = r; haveSelectedRect = true; }
        }
        if (haveSelectedRect)
            foreach (var handleRect in CornerHandleRects(selectedCanvasRect))
                GUI.Box(handleRect, GUIContent.none);

        if (!allowInput || Event.current == null) return;
        var evt = Event.current;
        if (evt.type == EventType.ScrollWheel && canvas.Contains(mouse)) { _atlasZoom = Mathf.Clamp(_atlasZoom * (1f - evt.delta.y * .08f), .1f, 4f); evt.Use(); }
        if (evt.type == EventType.MouseDrag && evt.button == 2) { _atlasPan += evt.delta; evt.Use(); }

        if (evt.type == EventType.MouseDown && evt.button == 0)
            BeginAtlasDrag(canvas, mouse, haveSelectedRect, selectedCanvasRect, evt);
        else if (evt.type == EventType.MouseDrag && evt.button == 0 && _atlasDragMode != AtlasDragMode.None)
            TickAtlasDrag(canvas, mouse, evt);
        else if (evt.type == EventType.MouseUp && evt.button == 0 && _atlasDragMode != AtlasDragMode.None)
            EndAtlasDrag(evt);
    }

    private Rect EntryCanvasRect(Rect canvas, SC2DecalAtlasEntry e) => new(
        canvas.x + e.X / (float)Doc.AtlasWidth * canvas.width,
        canvas.y + e.Y / (float)Doc.AtlasHeight * canvas.height,
        e.Width / (float)Doc.AtlasWidth * canvas.width,
        e.Height / (float)Doc.AtlasHeight * canvas.height);

    private static IEnumerable<Rect> CornerHandleRects(Rect r)
    {
        var h = AtlasHandleSize;
        yield return new Rect(r.x - h * .5f, r.y - h * .5f, h, h);
        yield return new Rect(r.xMax - h * .5f, r.y - h * .5f, h, h);
        yield return new Rect(r.x - h * .5f, r.yMax - h * .5f, h, h);
        yield return new Rect(r.xMax - h * .5f, r.yMax - h * .5f, h, h);
    }

    private void BeginAtlasDrag(Rect canvas, Vector2 mouse, bool haveSelectedRect, Rect selectedCanvasRect, Event evt)
    {
        if (haveSelectedRect)
        {
            var handles = new[] { AtlasDragMode.ResizeTL, AtlasDragMode.ResizeTR, AtlasDragMode.ResizeBL, AtlasDragMode.ResizeBR };
            var i = 0;
            foreach (var handleRect in CornerHandleRects(selectedCanvasRect))
            {
                if (handleRect.Contains(mouse))
                {
                    _atlasDragMode = handles[i];
                    _atlasDragEntryIndex = _selectedEntry;
                    evt.Use();
                    return;
                }
                i++;
            }
        }

        for (var i = Doc.Entries.Count - 1; i >= 0; i--)
        {
            if (!EntryCanvasRect(canvas, Doc.Entries[i]).Contains(mouse)) continue;
            _selectedEntry = i;
            ResetEntryDrafts();
            _atlasDraftId = Doc.Entries[i].Id;
            _atlasDraft = Doc.Entries[i].Clone();
            _atlasDragMode = AtlasDragMode.Move;
            _atlasDragEntryIndex = i;
            evt.Use();
            return;
        }
    }

    private void TickAtlasDrag(Rect canvas, Vector2 mouse, Event evt)
    {
        if (_atlasDraft == null || _atlasDragEntryIndex != _selectedEntry) { _atlasDragMode = AtlasDragMode.None; return; }
        var deltaAtlasX = evt.delta.x / canvas.width * Doc.AtlasWidth;
        var deltaAtlasY = evt.delta.y / canvas.height * Doc.AtlasHeight;
        var e = _atlasDraft;
        switch (_atlasDragMode)
        {
            case AtlasDragMode.Move:
                e.X += Mathf.RoundToInt(deltaAtlasX);
                e.Y += Mathf.RoundToInt(deltaAtlasY);
                break;
            case AtlasDragMode.ResizeTL:
                e.X += Mathf.RoundToInt(deltaAtlasX); e.Width -= Mathf.RoundToInt(deltaAtlasX);
                e.Y += Mathf.RoundToInt(deltaAtlasY); e.Height -= Mathf.RoundToInt(deltaAtlasY);
                break;
            case AtlasDragMode.ResizeTR:
                e.Width += Mathf.RoundToInt(deltaAtlasX);
                e.Y += Mathf.RoundToInt(deltaAtlasY); e.Height -= Mathf.RoundToInt(deltaAtlasY);
                break;
            case AtlasDragMode.ResizeBL:
                e.X += Mathf.RoundToInt(deltaAtlasX); e.Width -= Mathf.RoundToInt(deltaAtlasX);
                e.Height += Mathf.RoundToInt(deltaAtlasY);
                break;
            case AtlasDragMode.ResizeBR:
                e.Width += Mathf.RoundToInt(deltaAtlasX);
                e.Height += Mathf.RoundToInt(deltaAtlasY);
                break;
        }
        e.Width = Mathf.Max(1, e.Width);
        e.Height = Mathf.Max(1, e.Height);
        evt.Use();
        RepaintHost?.Invoke();
    }

    // Coalesced into one commit on mouse-up, same reasoning as this
    // project's other drag-to-adjust controls (e.g. Collision2's height-
    // offset slider) — not one undo entry per drag frame.
    private void EndAtlasDrag(Event evt)
    {
        if (_atlasDraft != null && _atlasDragEntryIndex == _selectedEntry && _selectedEntry >= 0 && _selectedEntry < Doc.Entries.Count)
        {
            ClampRect(_atlasDraft);
            var next = Doc.Clone();
            next.Entries[_selectedEntry] = _atlasDraft.Clone();
            Commit(_atlasDragMode == AtlasDragMode.Move ? "Move Decal Atlas Rect" : "Resize Decal Atlas Rect", next);
            _atlasDraft = next.Entries[_selectedEntry].Clone();
            _status = $"{_atlasDraft.Name}: {_atlasDraft.Width} x {_atlasDraft.Height} at {_atlasDraft.X}, {_atlasDraft.Y}.";
        }
        _atlasDragMode = AtlasDragMode.None;
        _atlasDragEntryIndex = -1;
        evt.Use();
    }

    private readonly struct SymmetrySlot
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly float Rotation;
        public SymmetrySlot(Vector3 position, Vector3 normal, float rotation) { Position = position; Normal = normal; Rotation = rotation; }
    }

    /// <summary>On sloped/rugged (real mountain-scale) terrain, a decal can
    /// get cut off partway across its own footprint —
    /// SC2DecalGeometryBuilder's depth-clip, Local.Y ∈
    /// [-0.5, 0.5], only tolerates ±ProjectionDepth/2 of real-world distance
    /// either side of the placement plane. A sparse relief sample can still
    /// miss a sharp local peak, and an overly strict face-angle threshold
    /// can reject facets that differ too much from a single fixed placement
    /// normal on a real mountain's widely-varying local facets. The angle
    /// threshold is therefore maximally permissive (rejects true backfaces
    /// only, see SC2DecalGeometryBuilder.MinFaceAngleDot), and this depth
    /// estimate does not rely on the sampled relief alone — the footprint's own
    /// diagonal is a real floor too (a bigger decal is inherently covering a
    /// bigger, typically more varied area, so it earns proportionally more
    /// depth regardless of what a sparse sample happened to catch), plus a
    /// larger safety multiplier on whatever relief the 9x9 grid did observe.
    /// Compares against the placement's OWN Position.y (the real plane
    /// SC2DecalGeometryBuilder actually clips around), not a re-sampled
    /// center height. Flat terrain is unaffected (falls straight back to
    /// `minimum`, the atlas entry's own configured default).</summary>
    private float ComputeAdequateProjectionDepth(Vector3 position, float rotationDegrees, float width, float length, float minimum)
    {
        if (_terrainShell == null) return minimum;
        var angle = rotationDegrees * Mathf.Deg2Rad;
        var right = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        var forward = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle));
        var halfW = width * 0.5f; var halfL = length * 0.5f;
        const int samplesPerAxis = 9;
        var minH = position.y; var maxH = position.y;
        for (var sx = 0; sx < samplesPerAxis; sx++)
        for (var sz = 0; sz < samplesPerAxis; sz++)
        {
            var u = samplesPerAxis > 1 ? sx / (float)(samplesPerAxis - 1) * 2f - 1f : 0f;
            var v = samplesPerAxis > 1 ? sz / (float)(samplesPerAxis - 1) * 2f - 1f : 0f;
            var offset = right * (u * halfW) + forward * (v * halfL);
            var h = _terrainShell.SampleLiveHeightGrid(position.x + offset.x, position.z + offset.y);
            minH = Mathf.Min(minH, h); maxH = Mathf.Max(maxH, h);
        }
        var maxDeviation = Mathf.Max(maxH - position.y, position.y - minH);
        // A 9x9 sample grid can still miss
        // a sharp local peak between its own points on genuinely jagged
        // rock, so this no longer relies on the sampled estimate alone -
        // the footprint's own diagonal is now a real floor too (a bigger
        // decal is being asked to cover a bigger, typically more varied
        // area of terrain, so it should get proportionally more depth
        // headroom regardless of what the sparse sample happened to catch),
        // on top of a larger safety multiplier on whatever relief WAS
        // observed. Combined with SC2DecalGeometryBuilder's now-much-more-
        // permissive angle rejection (backfaces only), depth no longer
        // needs to be tightly correct - only safely generous.
        var footprintFloor = Mathf.Sqrt(width * width + length * length);
        return Mathf.Max(minimum, Mathf.Max(footprintFloor, maxDeviation * 5f));
    }

    /// <summary>Slot 0 is always the seed itself; slot count/order depends
    /// only on Data.Symmetry — deliberately kept in exact lockstep with
    /// SC2TerrainData.GetSymmetricPositions' own switch (same axes, same
    /// order) so a decal's mirrored copies land on the same slots every
    /// other symmetry-aware system in this editor already agrees on.
    /// SurfaceNormal mirrors the same way a direction vector should (no
    /// center offset, only the linear part of the transform). Rotation
    /// negates for a handedness-reversing single mirror, and simply adds
    /// the same angular offset for a handedness-preserving 180°/radial
    /// step — see InvertSymmetrySlot's own comment for the inverse.</summary>
    private List<SymmetrySlot> BuildSymmetrySlots(Vector3 seedPos, Vector3 seedNormal, float seedRotation)
    {
        var list = new List<SymmetrySlot> { new(seedPos, seedNormal, seedRotation) };
        var data = _terrainShell?.Data;
        if (data == null) return list;
        var c = data.SymmetryCenter; var rel = seedPos - c;
        switch (data.Symmetry)
        {
            case SymmetryMode.X:
                list.Add(new SymmetrySlot(c + new Vector3(-rel.x, rel.y, rel.z), new Vector3(-seedNormal.x, seedNormal.y, seedNormal.z), -seedRotation));
                break;
            case SymmetryMode.Y:
                list.Add(new SymmetrySlot(c + new Vector3(rel.x, rel.y, -rel.z), new Vector3(seedNormal.x, seedNormal.y, -seedNormal.z), -seedRotation));
                break;
            case SymmetryMode.XY:
                list.Add(new SymmetrySlot(c + new Vector3(-rel.x, rel.y, rel.z), new Vector3(-seedNormal.x, seedNormal.y, seedNormal.z), -seedRotation));
                list.Add(new SymmetrySlot(c + new Vector3(rel.x, rel.y, -rel.z), new Vector3(seedNormal.x, seedNormal.y, -seedNormal.z), -seedRotation));
                list.Add(new SymmetrySlot(c + new Vector3(-rel.x, rel.y, -rel.z), new Vector3(-seedNormal.x, seedNormal.y, -seedNormal.z), seedRotation + 180f));
                break;
            case SymmetryMode.Radial:
                var ang = Mathf.Atan2(rel.z, rel.x); var r = new Vector2(rel.x, rel.z).magnitude;
                var nAng = Mathf.Atan2(seedNormal.z, seedNormal.x); var nR = new Vector2(seedNormal.x, seedNormal.z).magnitude;
                foreach (var deg in SC2RadialSymmetryMath.RotationAngleSteps(data.RadialCount))
                {
                    var a = ang + deg * Mathf.Deg2Rad; var na = nAng + deg * Mathf.Deg2Rad;
                    list.Add(new SymmetrySlot(
                        c + new Vector3(r * Mathf.Cos(a), rel.y, r * Mathf.Sin(a)),
                        new Vector3(nR * Mathf.Cos(na), seedNormal.y, nR * Mathf.Sin(na)),
                        seedRotation + deg));
                }
                break;
        }
        return list;
    }

    /// <summary>Recovers what the seed (slot 0) transform must have been
    /// from a member at a KNOWN slot index, so dragging/editing ANY group
    /// member (not just the original click) can still correctly regenerate
    /// every sibling. Every mirror/180° slot is self-inverse (applying the
    /// same rule again undoes it); a Radial step needs the negated angle
    /// instead, since rotating twice by the same amount does not undo a
    /// rotation.</summary>
    private (Vector3 pos, Vector3 normal, float rotation) InvertSymmetrySlot(int slotIndex, Vector3 pos, Vector3 normal, float rotation)
    {
        var data = _terrainShell?.Data;
        if (data == null || slotIndex <= 0) return (pos, normal, rotation);
        var c = data.SymmetryCenter; var rel = pos - c;
        switch (data.Symmetry)
        {
            case SymmetryMode.X:
                return (c + new Vector3(-rel.x, rel.y, rel.z), new Vector3(-normal.x, normal.y, normal.z), -rotation);
            case SymmetryMode.Y:
                return (c + new Vector3(rel.x, rel.y, -rel.z), new Vector3(normal.x, normal.y, -normal.z), -rotation);
            case SymmetryMode.XY:
                if (slotIndex == 1) return (c + new Vector3(-rel.x, rel.y, rel.z), new Vector3(-normal.x, normal.y, normal.z), -rotation);
                if (slotIndex == 2) return (c + new Vector3(rel.x, rel.y, -rel.z), new Vector3(normal.x, normal.y, -normal.z), -rotation);
                return (c + new Vector3(-rel.x, rel.y, -rel.z), new Vector3(-normal.x, normal.y, -normal.z), rotation + 180f);
            case SymmetryMode.Radial:
                var steps = SC2RadialSymmetryMath.RotationAngleSteps(data.RadialCount).ToList();
                var deg = slotIndex - 1 < steps.Count ? steps[slotIndex - 1] : 0f;
                var ang = Mathf.Atan2(rel.z, rel.x) - deg * Mathf.Deg2Rad; var r = new Vector2(rel.x, rel.z).magnitude;
                var nAng = Mathf.Atan2(normal.z, normal.x) - deg * Mathf.Deg2Rad; var nR = new Vector2(normal.x, normal.z).magnitude;
                return (c + new Vector3(r * Mathf.Cos(ang), rel.y, r * Mathf.Sin(ang)), new Vector3(nR * Mathf.Cos(nAng), normal.y, nR * Mathf.Sin(nAng)), rotation - deg);
            default:
                return (pos, normal, rotation);
        }
    }

    /// <summary>Regenerates every OTHER member of `edited`'s symmetry group
    /// (by GroupId) from its current Position/SurfaceNormal/Rotation, and
    /// copies its scalar properties (Width/Length/ProjectionDepth/
    /// HeightOffset/AlignToSurface/Enabled/Name) uniformly — those never
    /// need mirroring, only Position/Normal/Rotation do. Mutates `next` in
    /// place; caller commits once. No-op if `edited` isn't grouped.</summary>
    private void SyncSymmetryGroup(SC2DecalsDocument next, SC2DecalPlacement edited)
    {
        if (string.IsNullOrEmpty(edited.SymmetryGroupId)) return;
        var editedPos = new Vector3(edited.Position[0], edited.Position[1], edited.Position[2]);
        var editedNormal = new Vector3(edited.SurfaceNormal[0], edited.SurfaceNormal[1], edited.SurfaceNormal[2]);
        var seed = InvertSymmetrySlot(edited.SymmetrySlotIndex, editedPos, editedNormal, edited.Rotation);
        var slots = BuildSymmetrySlots(seed.pos, seed.normal, seed.rotation);
        for (var i = 0; i < next.Placements.Count; i++)
        {
            var sibling = next.Placements[i];
            if (sibling.Id == edited.Id || sibling.SymmetryGroupId != edited.SymmetryGroupId) continue;
            if (sibling.SymmetrySlotIndex < 0 || sibling.SymmetrySlotIndex >= slots.Count) continue;
            var slot = slots[sibling.SymmetrySlotIndex];
            sibling.Position = A(slot.Position);
            sibling.SurfaceNormal = A(slot.Normal);
            sibling.Rotation = slot.Rotation;
            sibling.Name = edited.Name; sibling.Width = edited.Width; sibling.Length = edited.Length;
            sibling.ProjectionDepth = edited.ProjectionDepth; sibling.HeightOffset = edited.HeightOffset;
            sibling.AlignToSurface = edited.AlignToSurface; sibling.Enabled = edited.Enabled;
        }
    }

    private void DrawPlacementOutlines(Camera camera)
    {
        for (var i = 0; i < Doc.Placements.Count; i++)
        {
            var stored = Doc.Placements[i];
            var p = i == _selectedPlacement && _transformPreview != null ? _transformPreview
                : (_transformGroupPreview != null && _transformGroupPreview.TryGetValue(stored.Id, out var live) ? live : stored);
            if (!p.Enabled) continue;
            var c = new Vector3(p.Position[0], p.Position[1], p.Position[2]);
            var s = SC2TopViewMirror.WorldToGui(camera, c); if (s.z <= 0) continue;
            var selected = i == _selectedPlacement;
            // A plain 10x10 box, same size as every unselected one and only
            // differing by color, is hard to notice as selected. A real
            // highlight instead: bigger, plus a translucent outer ring, so
            // it reads as "selected" at a glance instead of blending into
            // the rest of the markers.
            var size = selected ? 16f : 10f; var half = size * 0.5f;
            if (selected)
            {
                var ringOld = GUI.color; GUI.color = new Color(1f, 1f, 0.4f, 0.5f);
                GUI.Box(new Rect(s.x - half - 4f, s.y - half - 4f, size + 8f, size + 8f), "");
                GUI.color = ringOld;
            }
            var gui = new Rect(s.x - half, s.y - half, size, size);
            var old = GUI.color; GUI.color = selected ? Color.yellow : Color.cyan; GUI.Box(gui, ""); GUI.color = old;
        }
    }

    private bool TryPickPlacement(Camera camera, Vector2 mouse, out int index)
    {
        var best = 18f; index = -1;
        for (var i = 0; i < Doc.Placements.Count; i++)
        {
            var p = Doc.Placements[i]; var s = SC2TopViewMirror.WorldToGui(camera, new Vector3(p.Position[0], p.Position[1], p.Position[2]));
            if (s.z <= 0) continue; var d = Vector2.Distance(mouse, new Vector2(s.x, s.y)); if (d < best) { best = d; index = i; }
        }
        return index >= 0;
    }

    private void HandleEditHotkey(Camera camera, Vector2 mouse, Event evt)
    {
        if (_selectedPlacement < 0 || _selectedPlacement >= Doc.Placements.Count) return;
        if (SC2Hotkeys.Matches("Decals.DeleteSelection", evt)) { DeletePlacement(); evt.Use(); return; }
        if (SC2Hotkeys.Matches("Decals.MoveGesture", evt)) _transformMode = DecalTransformMode.Move;
        else if (SC2Hotkeys.Matches("Decals.RotateGesture", evt)) _transformMode = DecalTransformMode.Rotate;
        else if (SC2Hotkeys.Matches("Decals.ScaleGesture", evt)) _transformMode = DecalTransformMode.Scale;
        else return;
        _transformOriginal = Doc.Placements[_selectedPlacement].Clone();
        _transformPreview = _transformOriginal.Clone();
        _transformStartMouse = mouse;
        var world = new Vector3(_transformOriginal.Position[0], _transformOriginal.Position[1], _transformOriginal.Position[2]);
        var screen = SC2TopViewMirror.WorldToGui(camera, world);
        _transformAnchorScreen = new Vector2(screen.x, screen.y);
        _transformStartDistance = Mathf.Max(1f, Vector2.Distance(_transformAnchorScreen, mouse));
        _status = $"{_transformMode}: move mouse, click/Enter to confirm, Esc/right-click to cancel.";
        _transformVersion++;
        evt.Use();
    }

    private void TickTransform(Camera camera, Vector2 mouse, bool allowInput)
    {
        var evt = Event.current;
        if (evt == null || _transformPreview == null) return;
        if ((evt.type == EventType.KeyDown && (evt.keyCode == KeyCode.Escape)) || (evt.type == EventType.MouseDown && evt.button == 1))
        {
            CancelTransform(true); evt.Use(); return;
        }
        if ((evt.type == EventType.KeyDown && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)) ||
            (evt.type == EventType.MouseDown && evt.button == 0 && allowInput))
        {
            var next = Doc.Clone();
            var applied = _transformPreview.Clone();
            next.Placements[_selectedPlacement] = applied;
            // G/R/S on any one symmetric decal moves/
            // rotates/scales its whole group together.
            SyncSymmetryGroup(next, applied);
            var name = _transformMode == DecalTransformMode.Move ? "Move Decal" : _transformMode == DecalTransformMode.Rotate ? "Rotate Decal" : "Scale Decal";
            _transformMode = DecalTransformMode.None; _transformOriginal = null; _transformPreview = null; _transformVersion++;
            Commit(name, next); ResetPlacementDraft(); _status = name + " applied."; evt.Use(); return;
        }

        if (_transformMode == DecalTransformMode.Move)
        {
            if (allowInput && _surfaces.Raycast(ScreenRay(camera, mouse), _placeThroughWater, out var hit))
            {
                var oldPosition = new Vector3(_transformPreview.Position[0], _transformPreview.Position[1], _transformPreview.Position[2]);
                var oldNormal = new Vector3(_transformPreview.SurfaceNormal[0], _transformPreview.SurfaceNormal[1], _transformPreview.SurfaceNormal[2]);
                if ((oldPosition - hit.point).sqrMagnitude > 1e-8f || (oldNormal - hit.normal).sqrMagnitude > 1e-8f)
                {
                    _transformPreview.Position = A(hit.point);
                    _transformPreview.SurfaceNormal = A(hit.normal);
                    TransformChanged();
                }
            }
        }
        else if (_transformMode == DecalTransformMode.Rotate)
        {
            var start = _transformStartMouse - _transformAnchorScreen;
            var current = mouse - _transformAnchorScreen;
            var delta = start.sqrMagnitude > 4f && current.sqrMagnitude > 4f
                ? -(Mathf.Atan2(current.y, current.x) - Mathf.Atan2(start.y, start.x)) * Mathf.Rad2Deg
                : (mouse.x - _transformStartMouse.x) * .5f;
            var rotation = _transformOriginal.Rotation + delta;
            if (!Mathf.Approximately(_transformPreview.Rotation, rotation))
            {
                _transformPreview.Rotation = rotation;
                _status = $"Rotate: {delta:0.##} degrees. Click/Enter to confirm, Esc to cancel.";
                TransformChanged();
            }
        }
        else if (_transformMode == DecalTransformMode.Scale)
        {
            var factor = Vector2.Distance(_transformAnchorScreen, _transformStartMouse) > 2f
                ? Mathf.Max(.01f, Vector2.Distance(_transformAnchorScreen, mouse) / _transformStartDistance)
                : Mathf.Max(.01f, 1f + (mouse.x - _transformStartMouse.x) * .01f);
            var width = Mathf.Max(.01f, _transformOriginal.Width * factor);
            var length = Mathf.Max(.01f, _transformOriginal.Length * factor);
            if (!Mathf.Approximately(_transformPreview.Width, width) || !Mathf.Approximately(_transformPreview.Length, length))
            {
                _transformPreview.Width = width;
                _transformPreview.Length = length;
                _status = $"Scale: {factor:0.###}x. Click/Enter to confirm, Esc to cancel.";
                TransformChanged();
            }
        }
    }

    private void TransformChanged()
    {
        _transformVersion++;
        // Deliberately NOT touching _renderedRevision here (unlike Commit/
        // CancelTransform, which really do need a full rebuild) — bumping
        // _transformVersion alone is already enough to make EnsurePreview
        // see a change. Forcing _renderedRevision too used to make every
        // single mouse-move during a Move/Rotate/Scale drag look exactly
        // like a full document edit, so EnsurePreview re-ran Collect+
        // GeometryBuilder for EVERY placement on every drag frame instead
        // of just the one (plus symmetry siblings) actually being dragged —
        // the real cause of decal transform-editing feeling sluggish.
        RepaintHost?.Invoke();
    }

    private void CancelTransform(bool repaint)
    {
        if (_transformMode == DecalTransformMode.None) return;
        _transformMode = DecalTransformMode.None; _transformOriginal = null; _transformPreview = null;
        _transformVersion++; _renderedRevision = -1; _status = "Transform cancelled.";
        if (repaint) RepaintHost?.Invoke();
    }

    /// <summary>Called from SC2CostsShell (wired in SC2GameViewEditorUI, same
    /// callback pattern as SC2TerrainShell.GetImportedTerrainsForExport)
    /// every frame the Costs tab keeps its overlay on/updated — without this,
    /// a placed decal's own mesh completely occludes the Costs
    /// paint overlay wherever it sits, since SC2Decal.shader has no idea the
    /// overlay existed at all (see that shader's own Properties comment).
    /// Mutates the SAME long-lived _previewMaterial instance EnsurePreview
    /// already reuses across rebuilds (never destroyed/recreated, just
    /// reassigned to the mesh renderer each time), so this stays correctly
    /// set even if EnsurePreview runs again afterward while Costs is still
    /// active.</summary>
    public void SetCostsOverlay(Texture overlay, bool enabled, float gridSize)
    {
        if (Doc == null) return;
        EnsureMaterial();
        if (_previewMaterial == null || !_previewMaterial.HasProperty("_CostsOverlayTex")) return;
        if (overlay != null) _previewMaterial.SetTexture("_CostsOverlayTex", overlay);
        _previewMaterial.SetFloat("_CostsOverlayEnabled", enabled ? 1f : 0f);
        if (_previewMaterial.HasProperty("_CostsOverlayGridSize"))
            _previewMaterial.SetFloat("_CostsOverlayGridSize", gridSize);
    }

    /// <summary>HandleDecalsGameViewInput bails out immediately unless the
    /// Decals tab is active, so without this being public and called every
    /// frame regardless of the active tab, placed decals would not render at
    /// all after loading a saved map until the Decals tab was opened, since
    /// this method (the actual mesh-rebuild-from-document step) would never
    /// run otherwise. Mirrors how SC2MarkerScenePreview.Sync already does the
    /// equivalent for markers — cheap/idempotent, since the body below
    /// early-returns unless the document/terrain actually changed since the
    /// last call.</summary>
    public void EnsurePreview()
    {
        // Confirmed real: live terrain brush strokes (Raise/Lower/Smooth/Set
        // Height/import) never bump Session.Document.Revision — they mutate
        // SC2TerrainData directly and only the command/history system ever
        // raises that. Without also polling SC2TerrainShell's own
        // TerrainGeometryVersion (bumped directly at those same mutation
        // sites), a decal's projected mesh kept rendering against whatever
        // terrain shape existed the last time this ran, even after the
        // ground under it had been reshaped.
        if (Doc == null) return;
        var revisionChanged = _renderedRevision != _session.Document.Revision;
        var transformChanged = _renderedTransformVersion != _transformVersion;
        var terrainChanged = _renderedTerrainVersion != SC2TerrainShell.TerrainGeometryVersion;
        if (!revisionChanged && !transformChanged && !terrainChanged) return;

        var previewDocument = Doc;
        if (_transformPreview != null && _selectedPlacement >= 0 && _selectedPlacement < Doc.Placements.Count)
        {
            previewDocument = Doc.Clone();
            var dragged = _transformPreview.Clone();
            previewDocument.Placements[_selectedPlacement] = dragged;
            // Live-preview the whole symmetry group moving/rotating/scaling
            // together while actively dragging, not just at commit — cached
            // by Id so DrawPlacementOutlines' 2D markers can track the same
            // live positions the 3D mesh preview already does.
            SyncSymmetryGroup(previewDocument, dragged);
            _transformGroupPreview = previewDocument.Placements.ToDictionary(x => x.Id);
        }
        else
        {
            _transformGroupPreview = null;
        }

        // Matches SC2DecalGeometryBuilder.Build's own top-level guard (an
        // empty model when the atlas is missing/invalid) — without this,
        // a still-populated _placementFragmentCache from before the atlas
        // became invalid would keep getting merged into the preview mesh
        // below, since the per-placement rebuild loop skips entirely in
        // this case and never touches the cache.
        if (previewDocument.AtlasWidth <= 0 || previewDocument.AtlasHeight <= 0)
        {
            _placementFragmentCache.Clear();
            if (_previewMesh != null) { UnityEngine.Object.Destroy(_previewMesh); _previewMesh = null; }
            if (_previewRoot != null) _previewRoot.GetComponent<MeshFilter>().sharedMesh = null;
            Doc.VertexCount = 0; Doc.IndexCount = 0;
            if (_transformPreview == null) Doc.SpatialState = SC2DecalsSpatialState.Current;
            _renderedRevision = _session.Document.Revision; _renderedTransformVersion = _transformVersion; _renderedTerrainVersion = SC2TerrainShell.TerrainGeometryVersion;
            _dirtyPlacementIds.Clear();
            return;
        }

        // See SC2LiveHeightGrid.EnsureFreshImmediate's own doc comment: this
        // method runs in the SAME frame a stroke's MouseUp bumps
        // TerrainGeometryVersion, well before the live height grid's normal
        // debounce window could ever elapse — without this, Collect below
        // (via SampleLiveHeightGrid) always read the PREVIOUS stroke's
        // terrain shape, one stroke behind.
        if (terrainChanged) _terrainShell?.RefreshLiveHeightGridImmediate();

        // A terrain-only change (no authored edit) can be scoped to just the
        // placements MarkPreviewDirtyInRadius actually flagged (a recent
        // brush stroke's own touched radius). An active transform drag
        // (Move/Rotate/Scale, TransformChanged firing on every mouse-move)
        // can similarly be scoped to just the ONE placement being dragged
        // plus any symmetry-group siblings SyncSymmetryGroup mirrors it onto
        // below — the only other placements a drag frame can possibly touch.
        // Confirmed real cost: before this, every single drag frame redid
        // Collect+GeometryBuilder for EVERY placement in the document, not
        // just the dragged one, making decal Move/Rotate/Scale visibly
        // sluggish on any map with more than a couple of decals. A real
        // authored edit (Commit) always falls back to recomputing everyone,
        // since it can change literally anything (added/removed/reordered
        // placements, atlas entries, etc.) with no cheap way to know just
        // what changed.
        var fullRebuild = revisionChanged || _placementFragmentCache.Count == 0;
        HashSet<string> toRebuild = null;
        if (!fullRebuild)
        {
            toRebuild = new HashSet<string>();
            if (transformChanged)
            {
                if (_transformPreview != null && _selectedPlacement >= 0 && _selectedPlacement < previewDocument.Placements.Count
                    && previewDocument.Placements[_selectedPlacement]?.Id != null)
                {
                    var dragged = previewDocument.Placements[_selectedPlacement];
                    toRebuild.Add(dragged.Id);
                    if (!string.IsNullOrEmpty(dragged.SymmetryGroupId))
                        foreach (var p in previewDocument.Placements)
                            if (p?.Id != null && p.SymmetryGroupId == dragged.SymmetryGroupId) toRebuild.Add(p.Id);
                }
                else
                {
                    // transformVersion changed with no active drag to scope
                    // to — shouldn't normally happen (Commit/CancelTransform
                    // both also bump the real document Revision, which
                    // already forces fullRebuild above) — fall back to the
                    // always-correct full rebuild rather than guess.
                    fullRebuild = true;
                }
            }
            if (!fullRebuild && terrainChanged)
                foreach (var p in previewDocument.Placements)
                    if (p?.Id != null && _dirtyPlacementIds.Contains(p.Id)) toRebuild.Add(p.Id);

            if (!fullRebuild && toRebuild.Count == 0)
            {
                // Nothing actually affected any decal (terrain changed
                // nowhere near one) — nothing to rebuild; just record the
                // new versions so this becomes a no-op again next call.
                _renderedTerrainVersion = SC2TerrainShell.TerrainGeometryVersion;
                _renderedTransformVersion = _transformVersion;
                _dirtyPlacementIds.Clear();
                return;
            }
        }

        var swCollect = System.Diagnostics.Stopwatch.StartNew();
        List<SC2DecalSurfaceTriangle> surfaceTriangles;
        if (fullRebuild)
        {
            surfaceTriangles = _surfaces.Collect(previewDocument, _surfaces.LiveTerrainSampleHeight);
        }
        else
        {
            // A lightweight subset document — Collect()/AddLiveHeightGrid
            // only ever READ placements, so reusing the same Entries list
            // and placement objects (no deep clone) is safe, and keeps
            // Collect's own per-placement tessellation work scoped to just
            // the handful actually dirty instead of every placement in the
            // document.
            var scoped = new SC2DecalsDocument
            {
                AtlasWidth = previewDocument.AtlasWidth,
                AtlasHeight = previewDocument.AtlasHeight,
                Entries = previewDocument.Entries,
                Placements = previewDocument.Placements.Where(p => p?.Id != null && toRebuild.Contains(p.Id)).ToList()
            };
            surfaceTriangles = _surfaces.Collect(scoped, _surfaces.LiveTerrainSampleHeight);
        }
        var swCollectMs = swCollect.ElapsedMilliseconds;

        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        var rebuiltCount = 0;
        // AtlasWidth/AtlasHeight are already confirmed > 0 by the guard
        // above (this method returns before reaching here otherwise).
        var entries = previewDocument.Entries.Where(e => e != null).ToDictionary(e => e.Id, e => e);
        foreach (var placement in previewDocument.Placements)
        {
            if (placement?.Id == null) continue;
            if (!fullRebuild && !toRebuild.Contains(placement.Id)) continue; // keep cached fragment as-is
            if (!placement.Enabled || !entries.TryGetValue(placement.AtlasEntryId ?? "", out var entry))
            {
                _placementFragmentCache.Remove(placement.Id);
                continue;
            }
            _placementFragmentCache[placement.Id] = SC2DecalGeometryBuilder.BuildPlacementFragment(
                placement, entry, surfaceTriangles, previewDocument.AtlasWidth, previewDocument.AtlasHeight);
            rebuiltCount++;
        }
        if (fullRebuild)
        {
            // Drop cache entries for placements no longer in this preview's
            // Id set (deleted since the last full rebuild) — otherwise a
            // removed decal's fragment lingers in the cache forever.
            var liveIds = new HashSet<string>(previewDocument.Placements.Where(p => p?.Id != null).Select(p => p.Id));
            foreach (var staleId in _placementFragmentCache.Keys.Where(id => !liveIds.Contains(id)).ToList())
                _placementFragmentCache.Remove(staleId);
        }
        var swBuildMs = swBuild.ElapsedMilliseconds;
        SC2Log.Log($"[SC2DecalsShell] EnsurePreview rebuild: placements={previewDocument.Placements.Count}, rebuilt={rebuiltCount}, surfaceTriangles={surfaceTriangles.Count}, Collect={swCollectMs}ms, GeometryBuilder.Build={swBuildMs}ms");

        // Merge every placement's cached (freshly rebuilt or reused)
        // fragment into one combined model — SC2DecalsBdfImporter.BuildMesh
        // still wants one flat Mesh for the whole document.
        var model = new DecalsModel { DiffuseAtlasPath = previewDocument.DiffuseAtlasPath ?? "", NormalAtlasPath = previewDocument.NormalAtlasPath ?? "" };
        foreach (var placement in previewDocument.Placements)
        {
            if (placement?.Id == null || !_placementFragmentCache.TryGetValue(placement.Id, out var fragment)) continue;
            var baseIndex = model.Vertices.Count;
            model.Vertices.AddRange(fragment.Vertices);
            foreach (var idx in fragment.Indices) model.Indices.Add(baseIndex + idx);
        }

        if (_previewRoot == null)
        {
            _previewRoot = new GameObject("SC2_Decals_AuthoredPreview") { hideFlags = HideFlags.DontSave };
            _previewRoot.AddComponent<SC2DecalAuthoredPreviewMarker>();
            _previewRoot.AddComponent<MeshFilter>(); _previewRoot.AddComponent<MeshRenderer>();
        }
        if (_previewMesh != null) UnityEngine.Object.Destroy(_previewMesh);
        _previewMesh = SC2DecalsBdfImporter.BuildMesh(model); _previewRoot.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
        EnsureMaterial(); _previewRoot.GetComponent<MeshRenderer>().sharedMaterial = _previewMaterial;
        Doc.VertexCount = model.Vertices.Count; Doc.IndexCount = model.Indices.Count;
        if (_transformPreview == null) Doc.SpatialState = SC2DecalsSpatialState.Current;
        _renderedRevision = _session.Document.Revision; _renderedTransformVersion = _transformVersion; _renderedTerrainVersion = SC2TerrainShell.TerrainGeometryVersion;
        _dirtyPlacementIds.Clear();
    }

    // Keyed on the blob's ContentHash (falling back to the plain path when
    // there's no blob) rather than just "is _diffuseAtlas non-null" —
    // confirmed real via the MapDocument audit: after LoadFromDisk replaces
    // Session.Document.Decals wholesale (see SC2TerrainShell.LoadFromDisk),
    // this shell's own cached _diffuseAtlas/_normalAtlas fields were never
    // invalidated, so the OLD map's atlas kept rendering even though the
    // document's own DiffuseAtlasContent/Path now pointed at a different
    // one entirely.
    private string _diffuseAtlasSourceKey;
    private string _normalAtlasSourceKey;

    private void EnsureMaterial()
    {
        if (_previewMaterial == null) { var shader = Shader.Find("SC2/Decal") ?? Resources.Load<Shader>("SC2Decal"); if (shader != null) _previewMaterial = new Material(shader); }
        if (_previewMaterial == null) return;
        var diffuseKey = !Doc.DiffuseAtlasContent.IsEmpty ? "blob:" + Doc.DiffuseAtlasContent.ContentHash : "path:" + Doc.DiffuseAtlasPath;
        var normalKey = !Doc.NormalAtlasContent.IsEmpty ? "blob:" + Doc.NormalAtlasContent.ContentHash : "path:" + Doc.NormalAtlasPath;
        if (_diffuseAtlas == null || _diffuseAtlasSourceKey != diffuseKey)
        {
            _diffuseAtlas = LoadBlobTexture(Doc.DiffuseAtlasContent, false);
            if (_diffuseAtlas == null && !string.IsNullOrEmpty(Doc.DiffuseAtlasPath)) _diffuseAtlas = SC2TerrainMaterialFactory.LoadTexturePublic(Doc.DiffuseAtlasPath);
            _diffuseAtlasSourceKey = diffuseKey;
        }
        if (_normalAtlas == null || _normalAtlasSourceKey != normalKey)
        {
            _normalAtlas = LoadBlobTexture(Doc.NormalAtlasContent, true);
            if (_normalAtlas == null && !string.IsNullOrEmpty(Doc.NormalAtlasPath)) _normalAtlas = SC2TerrainMaterialFactory.LoadTexturePublic(Doc.NormalAtlasPath);
            _normalAtlasSourceKey = normalKey;
        }
        if (_diffuseAtlas != null) _previewMaterial.SetTexture("_DiffuseMap", _diffuseAtlas);
        if (_normalAtlas != null) _previewMaterial.SetTexture("_NormalMap", _normalAtlas);
        _previewMaterial.SetFloat("_HasNormalMap", _normalAtlas != null ? 1 : 0);
    }

    /// <summary>Editable text field for Doc.DiffuseAtlasPath/NormalAtlasPath.
    /// SC2DecalsExportService.Build overwrites this path with a generated one
    /// whenever a real texture blob exists (i.e. the atlas was chosen/built
    /// in-editor) — this field only actually reaches decals.bdf verbatim when
    /// no blob is present, exactly the "not-yet-extracted vanilla texture"
    /// case this field exists to support. Committing on every edit
    /// mirrors SC2MaterialsPairsMode.DrawTextureField's own path-field
    /// pattern (no debounce there either).</summary>
    private void DrawAtlasPathField(float w, bool normal)
    {
        var current = normal ? Doc.NormalAtlasPath : Doc.DiffuseAtlasPath;
        GUILayout.BeginHorizontal(GUILayout.Width(w));
        var locked = SC2TerrainShell.DrawPathLockToggle("DecalsAtlas:" + (normal ? "Normal" : "Diffuse") + ":lock");
        var prevEnabled = GUI.enabled;
        GUI.enabled = prevEnabled && !locked;
        var edited = GUILayout.TextField(current ?? "", GUILayout.Width(w - 8 - 30));
        GUI.enabled = prevEnabled;
        GUILayout.EndHorizontal();
        if (locked || edited == current) return;
        var next = Doc.Clone();
        if (normal) next.NormalAtlasPath = edited; else next.DiffuseAtlasPath = edited;
        var resolved = SC2TerrainMaterialFactory.LoadTexturePublicAny(edited) as Texture2D;
        if (resolved != null)
        {
            var blob = PutTexture(resolved);
            if (normal) { next.NormalAtlasContent = blob; _normalAtlas = resolved; }
            else { next.DiffuseAtlasContent = blob; _diffuseAtlas = resolved; next.AtlasWidth = resolved.width; next.AtlasHeight = resolved.height; }
        }
        Commit(normal ? "Edit Decal Normal Atlas Path" : "Edit Decal Diffuse Atlas Path", next);
    }

    private void ChooseAtlas(bool normal)
    {
        var path = PickTextureFile(normal ? "Choose decal normal atlas" : "Choose decal diffuse atlas");
        if (string.IsNullOrEmpty(path)) return;
        if (!SC2CustomTextureImporter.TryLoad(path, out var texture, out var error, linear: normal)) { _status = error; return; }
        var next = Doc.Clone();
        var blob = PutTexture(texture);
        if (normal) { _normalAtlas = texture; next.NormalAtlasPath = "decals_normal.dds"; next.NormalAtlasContent = blob; }
        else { _diffuseAtlas = texture; next.DiffuseAtlasPath = "decals_diffuse.dds"; next.DiffuseAtlasContent = blob; next.AtlasWidth = texture.width; next.AtlasHeight = texture.height; next.AtlasSource = SC2DecalAtlasSource.Existing; }
        Commit(normal ? "Choose Decal Normal Atlas" : "Choose Decal Diffuse Atlas", next); _status = $"Loaded {texture.width}x{texture.height} atlas.";
    }

    private void ImportSourceImage()
    {
        var path = PickTextureFile("Import decal image"); if (string.IsNullOrEmpty(path)) return;
        if (!SC2CustomTextureImporter.TryLoad(path, out var texture, out var error)) { _status = error; return; }
        _sources.Add(texture); _sourceNames.Add(Path.GetFileNameWithoutExtension(path)); _normalSources.Add(null); _status = $"Added {_sourceNames.Last()}.";
    }

    /// <summary>A source image's normal-map
    /// counterpart, for later "Build atlas" packing — attaches to the MOST
    /// RECENTLY imported diffuse source (natural "diffuse, then its normal"
    /// import order), not a whole separate atlas file (that's what "Choose
    /// normal atlas..." above is for).</summary>
    private void ImportSourceNormalImage()
    {
        if (_sources.Count == 0) { _status = "Import a diffuse decal image first — its normal map attaches to the most recently imported one."; return; }
        var path = PickTextureFile("Import decal normal image"); if (string.IsNullOrEmpty(path)) return;
        if (!SC2CustomTextureImporter.TryLoad(path, out var texture, out var error, linear: true)) { _status = error; return; }
        var index = _sources.Count - 1;
        while (_normalSources.Count <= index) _normalSources.Add(null);
        _normalSources[index] = texture;
        _status = $"Added normal map for {_sourceNames[index]}.";
    }

    private void BuildAtlas()
    {
        if (Doc.Placements.Count > 0)
        {
            _status = "Remove or reassign placed decals before rebuilding the atlas.";
            return;
        }
        var atlas = new Texture2D(2048, 2048, TextureFormat.RGBA32, true, false) { name = "SC2_DecalAtlas_Generated" };
        var rects = atlas.PackTextures(_sources.ToArray(), 0, 2048, false);

        // Packs a matching normal atlas at
        // the SAME size, using the SAME rects PackTextures just computed
        // for diffuse — Texture2D.PackTextures has no "pack this second set
        // into the same layout" mode, so each source with a real normal-map
        // counterpart (_normalSources[i] != null) gets manually resized and
        // blitted into the identical pixel rect on a second canvas instead.
        // Entries with no imported normal map are left at a flat/neutral
        // tangent-space normal (0.5, 0.5, 1) — "no bump" — rather than
        // black/garbage, so a partially-normal-mapped catalog still reads
        // correctly for the entries that don't have one.
        var hasAnyNormal = _normalSources.Any(t => t != null);
        Texture2D normalAtlas = null;
        if (hasAnyNormal)
        {
            normalAtlas = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, true, true) { name = "SC2_DecalNormalAtlas_Generated" };
            var flat = new Color32(128, 128, 255, 255);
            var fillPixels = new Color32[normalAtlas.width * normalAtlas.height];
            for (var p = 0; p < fillPixels.Length; p++) fillPixels[p] = flat;
            normalAtlas.SetPixels32(fillPixels);
        }

        var next = Doc.Clone(); next.Entries.Clear(); next.AtlasWidth = atlas.width; next.AtlasHeight = atlas.height;
        next.DiffuseAtlasPath = "decals_diffuse.dds"; next.AtlasSource = SC2DecalAtlasSource.Generated;
        next.DiffuseAtlasContent = PutTexture(atlas);
        for (var i = 0; i < rects.Length; i++)
        {
            var px = Mathf.RoundToInt(rects[i].x * atlas.width); var py = Mathf.RoundToInt(rects[i].y * atlas.height);
            var pw = Mathf.RoundToInt(rects[i].width * atlas.width); var ph = Mathf.RoundToInt(rects[i].height * atlas.height);
            next.Entries.Add(new SC2DecalAtlasEntry { Name = _sourceNames[i], X = px, Y = py, Width = pw, Height = ph, Source = SC2DecalEntrySource.Generated });
            if (normalAtlas != null && i < _normalSources.Count && _normalSources[i] != null)
                BlitIntoAtlas(normalAtlas, _normalSources[i], px, py, pw, ph);
        }

        if (normalAtlas != null)
        {
            normalAtlas.Apply(true, false);
            _normalAtlas = normalAtlas;
            next.NormalAtlasPath = "decals_normal.dds";
            next.NormalAtlasContent = PutTexture(normalAtlas);
        }

        _diffuseAtlas = atlas; Commit("Build Decal Atlas", next); _selectedEntry = next.Entries.Count > 0 ? 0 : -1;
        _status = normalAtlas != null ? "Generated diffuse + normal atlas and catalog." : "Generated atlas and catalog.";
    }

    /// <summary>Point-sampled resize of source into the [x,y,width,height]
    /// pixel rect of atlas — matches PackTextures' own "just place this
    /// image at this size" contract closely enough for a normal-map
    /// counterpart; the atlas gets proper bilinear filtering at sample
    /// time regardless of how it was written.</summary>
    private static void BlitIntoAtlas(Texture2D atlas, Texture2D source, int x, int y, int width, int height)
    {
        if (source == null || width <= 0 || height <= 0) return;
        for (var row = 0; row < height; row++)
        {
            var v = height > 1 ? row / (float)(height - 1) : 0f;
            for (var col = 0; col < width; col++)
            {
                var u = width > 1 ? col / (float)(width - 1) : 0f;
                atlas.SetPixel(x + col, y + row, source.GetPixelBilinear(u, v));
            }
        }
    }

    private void AddEntry()
    {
        var next = Doc.Clone(); next.Entries.Add(new SC2DecalAtlasEntry { Name = $"Decal {next.Entries.Count + 1}", Width = Mathf.Min(64, Math.Max(1, next.AtlasWidth)), Height = Mathf.Min(64, Math.Max(1, next.AtlasHeight)) });
        Commit("Add Decal Atlas Entry", next); _selectedEntry = next.Entries.Count - 1; ResetEntryDrafts();
    }
    private void DuplicateEntry() { var next = Doc.Clone(); var copy = next.Entries[_selectedEntry].Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " Copy"; next.Entries.Add(copy); Commit("Duplicate Decal Entry", next); _selectedEntry = next.Entries.Count - 1; ResetEntryDrafts(); }
    private void DeleteEntry() { var next = Doc.Clone(); var id = next.Entries[_selectedEntry].Id; next.Entries.RemoveAt(_selectedEntry); next.Placements.RemoveAll(p => p.AtlasEntryId == id); Commit("Delete Decal Entry", next); _selectedEntry = Mathf.Min(_selectedEntry, next.Entries.Count - 1); ResetEntryDrafts(); }
    private void DuplicatePlacement() { var next = Doc.Clone(); var p = next.Placements[_selectedPlacement].Clone(); p.Id = Guid.NewGuid().ToString("N"); p.Position[0] += 1; p.SymmetryGroupId = ""; p.SymmetrySlotIndex = 0; next.Placements.Add(p); Commit("Duplicate Decal", next); _selectedPlacement = next.Placements.Count - 1; ResetPlacementDraft(); }
    private void DeletePlacement()
    {
        var next = Doc.Clone();
        var target = next.Placements[_selectedPlacement];
        // Deleting a symmetric decal removes its whole
        // group, not just the one member.
        if (string.IsNullOrEmpty(target.SymmetryGroupId))
            next.Placements.RemoveAt(_selectedPlacement);
        else
            next.Placements.RemoveAll(p => p.SymmetryGroupId == target.SymmetryGroupId);
        Commit("Delete Decal", next); _selectedPlacement = Mathf.Min(_selectedPlacement, next.Placements.Count - 1); ResetPlacementDraft();
    }

    private void Validate()
    {
        var report = SC2DecalsValidator.Validate(Doc); _status = report.Messages.Count == 0 ? "Decals validation passed." : $"Validation: {report.Messages.Count} issue(s).";
    }
    // Confirmed real, via a dedicated MapDocument save/restore + Undo/Redo
    // audit: decal commands were already real ISC2DocumentCommand entries
    // on the shared _session.History (same stack every other domain uses),
    // but this shell never exposed HasUndo/HasRedo/LastActionTimestamp/
    // Undo/Redo — so the global Ctrl+Z dispatcher in SC2GameViewEditorUI
    // (which picks whichever of its candidate shells has the newest
    // LastActionTimestamp) could never actually pick Decals, even right
    // after placing/moving/deleting one. Same bug class, same fix already
    // applied to Collision2's height-offset/polygon-budget commands
    // (see SC2Collision2Shell.CommitOffsetChange's own comment).
    private long _lastActionTimestamp;
    public bool HasUndo => _session != null && _session.History.CanUndo;
    public bool HasRedo => _session != null && _session.History.CanRedo;
    public long LastActionTimestamp => _lastActionTimestamp;

    /// <summary>Public so the global Ctrl+Z/Y handler
    /// in SC2GameViewEditorUI dispatches to whichever shell's
    /// LastActionTimestamp is newest, not only ever this shell's own
    /// button. No manual field re-sync needed afterward (unlike Environment/
    /// Collision2, which mirror document fields into legacy SC2TerrainData
    /// fields) — Decals has no legacy dual-ownership at all; EnsurePreview
    /// already self-heals from Session.Document.Revision on its own next
    /// call, regardless of which shell's Undo()/Redo() actually popped the
    /// command. Only the local selection indices need clamping, since Undo/
    /// Redo can change Entries/Placements counts out from under them.</summary>
    public void Undo()
    {
        if (_session == null || !_session.History.CanUndo) return;
        _session.History.Undo(_session.Document);
        ClampSelectionAfterUndoRedo();
        RepaintHost?.Invoke();
    }

    public void Redo()
    {
        if (_session == null || !_session.History.CanRedo) return;
        _session.History.Redo(_session.Document);
        ClampSelectionAfterUndoRedo();
        RepaintHost?.Invoke();
    }

    private void ClampSelectionAfterUndoRedo()
    {
        if (Doc == null) return;
        _selectedEntry = Doc.Entries.Count == 0 ? -1 : Mathf.Clamp(_selectedEntry, 0, Doc.Entries.Count - 1);
        _selectedPlacement = Doc.Placements.Count == 0 ? -1 : Mathf.Clamp(_selectedPlacement, 0, Doc.Placements.Count - 1);
        ResetEntryDrafts();
        ResetPlacementDraft();
        CancelTransform(false);
    }

    private void Commit(string name, SC2DecalsDocument next)
    {
        _session.History.Execute(_session.Document, new SC2ReplaceDecalsStateCommand(name, next));
        _lastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _renderedRevision = -1; RepaintHost?.Invoke();
    }

    /// <summary>Wired to SC2TerrainShell.HeightBrushApplied (see its own doc
    /// comment) — re-samples every AlignToSurface decal's stored Position/
    /// SurfaceNormal against the terrain's CURRENT height/normal, for every
    /// placement whose X/Z falls within `radius` of `center`, so live
    /// Geometry-mode sculpting keeps decals following the surface instead
    /// of only aligning once at placement/edit time.
    ///
    /// Mutates SC2DecalPlacement fields directly rather than going through
    /// Commit/SC2ReplaceDecalsStateCommand — same reasoning as
    /// SC2ObjectsShell.ReprojectAnchoredObjectsInRadius's own doc comment:
    /// this runs on every single brush stamp during a drag, far too often
    /// to push a real undo command each time, and Position[1]/SurfaceNormal
    /// here are derived values that self-correct on the next reprojection
    /// regardless.</summary>
    public void ReprojectAlignedDecalsInRadius(Vector3 center, float radius)
    {
        if (Doc?.Placements == null || _terrainShell == null) return;
        var r2 = radius * radius;
        var touched = false;
        const float eps = 0.5f;
        foreach (var p in Doc.Placements)
        {
            if (p == null || !p.AlignToSurface || p.Position == null || p.Position.Length < 3) continue;
            var dx = p.Position[0] - center.x; var dz = p.Position[2] - center.z;
            if (dx * dx + dz * dz > r2) continue;

            // SampleLiveRuntimeHeight, not SampleLiveHeightGrid — the latter
            // is a debounced cache that only rebuilds from Data.Heights,
            // itself only re-synced from the runtime at stroke MouseUp (see
            // SC2TerrainShell.SampleLiveRuntimeHeight's own doc comment), so
            // it read one whole stroke stale during a live drag.
            var x = p.Position[0]; var z = p.Position[2];
            p.Position[1] = _terrainShell.SampleLiveRuntimeHeight(x, z) + p.HeightOffset;

            var hL = _terrainShell.SampleLiveRuntimeHeight(x - eps, z);
            var hR = _terrainShell.SampleLiveRuntimeHeight(x + eps, z);
            var hD = _terrainShell.SampleLiveRuntimeHeight(x, z - eps);
            var hU = _terrainShell.SampleLiveRuntimeHeight(x, z + eps);
            var normal = new Vector3(hL - hR, 2f * eps, hD - hU).normalized;
            if (p.SurfaceNormal == null || p.SurfaceNormal.Length < 3) p.SurfaceNormal = new float[3];
            p.SurfaceNormal[0] = normal.x; p.SurfaceNormal[1] = normal.y; p.SurfaceNormal[2] = normal.z;
            touched = true;
        }
        if (touched) { _renderedRevision = -1; RepaintHost?.Invoke(); }
    }

    /// <summary>Wired to SC2TerrainShell.HeightBrushApplied alongside
    /// ReprojectAlignedDecalsInRadius above — marks just the placements
    /// whose footprint this stroke could plausibly reach as needing their
    /// own preview-mesh fragment rebuilt on the next EnsurePreview call,
    /// instead of EnsurePreview blindly re-running Collect+GeometryBuilder
    /// for EVERY placement on every single terrain edit (confirmed the
    /// dominant remaining cost in EnsurePreview's own measured rebuild
    /// timing). Cheap — only updates a HashSet here (a handful of bounds
    /// checks); the actual expensive work still only happens inside
    /// EnsurePreview, still gated by SC2TerrainShell.TerrainGeometryVersion
    /// the same as before (once per completed stroke, not once per brush
    /// stamp — this can fire many times per stroke without cost).
    ///
    /// Only meaningful when EnsurePreview ends up taking its "terrain
    /// changed only" branch (see its own doc comment) — an authored edit or
    /// an active transform drag always falls back to rebuilding everyone
    /// regardless of this set, since either can touch placements this
    /// radius check has no way to know about (e.g. a whole symmetry group
    /// moving together).</summary>
    public void MarkPreviewDirtyInRadius(Vector3 center, float radius)
    {
        if (Doc?.Placements == null) return;
        foreach (var p in Doc.Placements)
        {
            if (p?.Id == null || p.Position == null || p.Position.Length < 3) continue;
            // Same rotation-safe diagonal bound SC2DecalSurfaceProvider.
            // PlacementBoundsList/SC2DecalGeometryBuilder.
            // FilterTrianglesNearPlacement already use — never too small,
            // so this can only over-mark (a placement gets rebuilt for no
            // visible reason), never under-mark (a genuinely affected
            // placement silently going stale).
            var halfDiag = 0.5f * Mathf.Sqrt(p.Width * p.Width + p.Length * p.Length);
            var dx = p.Position[0] - center.x; var dz = p.Position[2] - center.z;
            if (Mathf.Sqrt(dx * dx + dz * dz) <= radius + halfDiag)
                _dirtyPlacementIds.Add(p.Id);
        }
    }

    private bool TryEntry(out SC2DecalAtlasEntry e) { e = null; if (_selectedEntry < 0 || _selectedEntry >= Doc.Entries.Count) return false; e = Doc.Entries[_selectedEntry]; return true; }
    private Rect AtlasUv(SC2DecalAtlasEntry e) => Doc.AtlasWidth > 0 && Doc.AtlasHeight > 0 ? new Rect(e.X / (float)Doc.AtlasWidth, 1f - (e.Y + e.Height) / (float)Doc.AtlasHeight, e.Width / (float)Doc.AtlasWidth, e.Height / (float)Doc.AtlasHeight) : new Rect(0, 0, 1, 1);
    private void ClampRect(SC2DecalAtlasEntry e) { e.X = Mathf.Clamp(e.X, 0, Mathf.Max(0, Doc.AtlasWidth - 1)); e.Y = Mathf.Clamp(e.Y, 0, Mathf.Max(0, Doc.AtlasHeight - 1)); e.Width = Mathf.Clamp(e.Width, 1, Mathf.Max(1, Doc.AtlasWidth - e.X)); e.Height = Mathf.Clamp(e.Height, 1, Mathf.Max(1, Doc.AtlasHeight - e.Y)); }
    private void ResetEntryDrafts() { _atlasDraftId = null; _atlasDraft = null; _defaultsDraftId = null; _defaultsDraft = null; }
    private void ResetPlacementDraft() { _placementDraftId = null; _placementDraft = null; }
    private static Ray ScreenRay(Camera c, Vector2 gui) => SC2TopViewMirror.ScreenPointToRay(c, gui);
    private static float[] A(Vector3 v) => new[] { v.x, v.y, v.z };
    private static bool PlacementEquals(SC2DecalPlacement a, SC2DecalPlacement b) => a.Name == b.Name && a.Rotation == b.Rotation && a.Width == b.Width && a.Length == b.Length && a.ProjectionDepth == b.ProjectionDepth && a.HeightOffset == b.HeightOffset && a.AlignToSurface == b.AlignToSurface && a.Enabled == b.Enabled;
    private static string PickTextureFile(string title)
    {
#if UNITY_EDITOR
        return UnityEditor.EditorUtility.OpenFilePanelWithFilters(title, "", new[] { "Textures", "png,jpg,jpeg,dds", "All files", "*" });
#else
        return SC2RuntimeFileDialog.OpenFile(title, "Texture", "png");
#endif
    }
    private SC2BlobReference PutTexture(Texture2D texture)
    {
        if (_session.BlobStore == null || texture == null) return SC2BlobReference.Empty;
        // GetPixels32 returns Unity's native bottom-up row order;
        // SC2DdsEncoder.BuildMaterialDds expects DDS's top-down order and
        // never flips internally — same fix as SC2CustomTextureImporter.
        // EncodeMaterialDds/SC2RuntimeTextureExporter.TryExportAsDds.
        var rawPixels = texture.GetPixels32();
        var w = texture.width; var h = texture.height;
        var pixels = new Color32[rawPixels.Length];
        for (var y = 0; y < h; y++)
            Array.Copy(rawPixels, y * w, pixels, (h - 1 - y) * w, w);
        var rgb = new byte[pixels.Length * 3]; var alpha = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++) { rgb[i * 3] = pixels[i].r; rgb[i * 3 + 1] = pixels[i].g; rgb[i * 3 + 2] = pixels[i].b; alpha[i] = pixels[i].a; }
        using var stream = new MemoryStream(SC2DdsEncoder.BuildMaterialDds(texture.width, texture.height, rgb, alpha), false);
        return _session.BlobStore.Put(stream);
    }
    private Texture2D LoadBlobTexture(SC2BlobReference reference, bool linear)
    {
        if (_session.BlobStore == null || reference.IsEmpty) return null;
        using var stream = _session.BlobStore.OpenRead(reference.ContentHash);
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        if (!SC2CustomTextureImporter.TryLoadBytesAny(memory.ToArray(), ".dds", out var texture, out var preview,
                out _, out _, out _, linear)) return null;
        return texture as Texture2D ?? preview;
    }
    public void Dispose()
    {
        if (_previewRoot != null) UnityEngine.Object.Destroy(_previewRoot); if (_previewMesh != null) UnityEngine.Object.Destroy(_previewMesh); if (_previewMaterial != null) UnityEngine.Object.Destroy(_previewMaterial);
    }
}

/// <summary>Marks generated decal geometry as a document view rather than a
/// selectable scene object. Generic picking must never use it as a camera pivot.</summary>
public sealed class SC2DecalAuthoredPreviewMarker : MonoBehaviour { }
