// SC2ReferenceUnitsShell.cs
//
// Placement/selection/drag/delete + intel-circle rendering for reference-only
// units/buildings (SC2ObjReferenceUnit) — real game units placed purely as an
// in-editor visual scale aid, never written into mapobjs.bdf/_save.lua/
// _scenario.lua. Deliberately its own small standalone shell rather than a
// new branch inside SC2ObjectsShell's already-large PlacementKind/
// ViewportObjectKind switches: keeping the whole read/write/render path here
// means "never exported" is a structural property (SC2ObjectsExport.cs
// simply never references this file or SC2TerrainData.ObjReferenceUnits) —
// verifiable with one grep, not an audit of every existing Props/Units
// export call site.
//
// Mirrors SC2ObjectsShell's own Unit browse -> arm -> click-to-place flow
// (OpenUnitBrowser/PlaceUnitAt) and its viewport pick/drag/delete pattern, at
// a deliberately reduced scope for v1: single-select only, no box-select, no
// symmetry linking. Real mesh rendering reuses SC2GameAssetScenePreview
// exactly as real Units do (see its own SyncTransforms — already extended
// with a small ObjReferenceUnits loop alongside its ObjUnits one, since both
// share the same InstanceKey(true, id) space and AllocateObjectId() counter,
// so ids can never collide).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class SC2ReferenceUnitsShell
{
    /// <summary>Same raycast contract SC2GameViewEditorUI.TryGetMeasureHit
    /// already implements (imported-terrain-first, live-procedural-fallback)
    /// — passed in rather than duplicated here, since building the imported-
    /// terrain collider list is SC2GameViewEditorUI's own responsibility
    /// (BuildImportedTerrainColliders), not something this shell should
    /// reimplement a second time.</summary>
    public delegate bool TryRaycastDelegate(Camera camera, Vector2 guiMousePosition, out Vector3 hit);

    private readonly SC2TerrainShell _terrainShell;
    private readonly SC2UnitPropBrowser _assetBrowser = new();
    private readonly Dictionary<int, SC2ReferenceUnitIntel> _intelCache = new();

    private bool _placementArmed;
    private SC2GameAsset _pendingAsset;
    private int _selectedId = -1;
    private int _draggingId = -1;
    // Set on the MouseDown that starts a drag, consumed on the first
    // MouseDrag that actually moves the unit — so a plain click-to-select
    // (mouse-down then mouse-up with no movement) never pushes a no-op undo
    // checkpoint, matching PlaceAt/DeleteSelected's own "only record when a
    // real change is about to happen" convention.
    private bool _dragUndoPending;

    public Action RepaintHost;
    public Action<string> ReportStatus;

    private SC2TerrainData Data => _terrainShell?.Data;

    public SC2ReferenceUnitsShell(SC2TerrainShell terrainShell)
    {
        _terrainShell = terrainShell;
    }

    /// <summary>Call once per frame regardless of whether the modal is open
    /// — DrawModal itself no-ops when closed, same convention
    /// SC2ObjectsShell.DrawEmbeddedGUI's own call sites already use.</summary>
    public void DrawBrowserModal() => _assetBrowser.DrawModal();

    /// <summary>Clears state tied to the previous map's objects — call from
    /// SC2TerrainShell.DataChanged so switching/reloading a map doesn't keep
    /// stale selection/drag state pointed at ids that no longer exist.</summary>
    public void OnDataChanged()
    {
        _selectedId = -1;
        _draggingId = -1;
        _dragUndoPending = false;
        _placementArmed = false;
        _pendingAsset = null;
        _intelCache.Clear();
        _buildableOverlay.Dispose();
        BuildableOverlayTexture = null;
    }

    public void OpenBrowserForPlacement()
    {
        _assetBrowser.Open(SC2UnitPropBrowser.AssetKind.Unit, selection =>
        {
            _placementArmed = true;
            _pendingAsset = selection.Asset;
            ReportStatus?.Invoke($"Reference: click in the viewport to place {selection.Asset.DisplayName} — Esc to cancel.");
            RepaintHost?.Invoke();
        });
    }

    /// <summary>The whole input+draw loop for this shell, mirroring
    /// SC2GameViewEditorUI.HandleMeasureToolGameViewInput's own shape (this
    /// session's most recent precedent for a small standalone viewport
    /// tool). Runs every frame regardless of which main tab is active —
    /// reference units are a global, always-visible aid, same reasoning
    /// Decals/Objects overlays already use for their own "stay visible from
    /// any tab" content.</summary>
    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition, bool allowInput, bool showOverlay, TryRaycastDelegate tryRaycast)
    {
        _assetBrowser.PollLoadTask();
        SyncMissingPreviews();

        var e = Event.current;
        if (allowInput && e != null && e.type != EventType.Used && viewportRect.Contains(mousePosition))
        {
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                if (_placementArmed) { _placementArmed = false; _pendingAsset = null; ReportStatus?.Invoke("Reference placement cancelled."); }
                else if (_selectedId >= 0) _selectedId = -1;
                e.Use();
            }
            else if ((e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace))
                     && _selectedId >= 0 && _draggingId < 0)
            {
                DeleteSelected();
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && _draggingId >= 0
                     && tryRaycast(camera, mousePosition, out var draggedTo))
            {
                var unit = Data?.ObjReferenceUnits.FirstOrDefault(r => r.Id == _draggingId);
                if (unit != null)
                {
                    if (_dragUndoPending)
                    {
                        _terrainShell?.RecordUndoCheckpoint("Move Reference Unit");
                        _dragUndoPending = false;
                    }
                    unit.Position = SnapXZ(draggedTo);
                    // Data.ObjReferenceUnits now holds the new position, but
                    // the live 3D preview mesh (SC2GameAssetScenePreview) is
                    // a separate GPU-instanced batch that only re-reads
                    // transforms when told to — without this the mesh stayed
                    // frozen at its placement spot while the ring/selection
                    // dot moved, making dragging look like it silently did
                    // nothing. Same "sync every frame while a drag is live"
                    // approach SC2ObjectsShell's own _dragActive gating uses
                    // for real Props/Units.
                    SC2GameAssetScenePreview.SyncTransforms(Data);
                    MarkDirty();
                }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _draggingId >= 0)
            {
                _draggingId = -1;
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (_placementArmed)
                {
                    if (tryRaycast(camera, mousePosition, out var placeAt)) PlaceAt(placeAt);
                    else ReportStatus?.Invoke("Reference: nothing under the cursor to place onto.");
                    e.Use();
                }
                else if (TryHitReferenceUnit(camera, viewportRect, mousePosition, out var hitId))
                {
                    _selectedId = hitId;
                    _draggingId = hitId;
                    _dragUndoPending = true;
                    e.Use();
                }
                else if (_selectedId >= 0)
                {
                    _selectedId = -1;
                    // Deliberately NOT e.Use() here — an empty-space click
                    // with nothing armed should still fall through to
                    // whatever else (camera nav, another tool) wants it.
                }
            }
        }

        // Always drawn (not gated by showOverlay, which only controls the
        // Radar/Sonar/Vision/Weapon rings) — same reasoning real Units/Props
        // use: the click/drag handle is a placement-tool aid, independent
        // of whatever intel-ring visibility the Visibility menu has set,
        // and the unit's own real mesh has no click target of its own at
        // small scale/steep angles otherwise.
        DrawReferenceUnitGlyphs(camera, viewportRect);
        if (showOverlay) DrawIntelOverlays(camera, viewportRect);
        UpdateBuildableOverlay(showOverlay);
        DrawBuildableSkirt(camera, viewportRect);
    }

    private readonly SC2BuildableOverlay _buildableOverlay = new();

    // Session-only: colour the vertices a building could be centred on instead of the ground a building could cover.
    private bool _buildableShowCentres;

    /// <summary>The colouring of the whole map by where the reference building that has "Buildable" on could be built; null when
    /// no building shows it. The editor UI pushes it into the terrain (see SC2GameViewEditorUI.PushBuildableOverlay).</summary>
    public Texture BuildableOverlayTexture { get; private set; }

    // What the skirt outline and its verdict below are drawn from: the building that shows the colouring and the heightmap it was
    // coloured from.
    private SC2ObjReferenceUnit _buildableUnit;
    private SC2ReferenceUnitIntel _buildableIntel;
    private ushort[] _buildableHeights;
    private int _buildableMapX, _buildableMapY;

    private void UpdateBuildableOverlay(bool showOverlay)
    {
        BuildableOverlayTexture = null;
        _buildableUnit = null;
        if (!showOverlay || Data == null || _terrainShell == null) return;

        SC2ObjReferenceUnit unit = null;
        foreach (var candidate in Data.ObjReferenceUnits)
        {
            if (!candidate.ShowBuildable) continue;
            if (unit == null || candidate.Id == _selectedId) unit = candidate;
        }
        if (unit == null || !_intelCache.TryGetValue(unit.Id, out var intel) || !intel.HasBuildSkirt) return;
        if (!_terrainShell.TryGetLiveHeightGridSnapshot(out var heights, out var mapX, out var mapY)) return;

        RefreshFootprintObstacles(unit);
        RefreshBuildRestriction(unit, intel, heights, mapX, mapY);
        BuildableOverlayTexture = _buildableOverlay.Update(heights, mapX, mapY, intel.FootprintSizeX, intel.FootprintSizeZ,
            intel.FlattenSkirt, intel.MaxGroundVariation, _obstacles, _obstaclesSignature, !_buildableShowCentres,
            _restriction.AllowedCells, _restriction.Signature);
        _buildableUnit = unit;
        _buildableIntel = intel;
        _buildableHeights = heights;
        _buildableMapX = mapX;
        _buildableMapY = mapY;
    }

    // The footprints of the other buildings on the map, which the building cannot be put on top of. Gathering them walks every
    // prop and unit, so it is redone a few times a second, and the overlay only recomputes when the result really changed.
    private SC2PlacedFootprint[] _obstacles = Array.Empty<SC2PlacedFootprint>();
    private int _obstaclesSignature;
    private int _obstaclesForUnit = -1;
    private double _obstaclesAt = double.MinValue;
    private const double ObstacleRefreshSeconds = 0.25;

    private void RefreshFootprintObstacles(SC2ObjReferenceUnit self)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (_obstaclesForUnit == self.Id && now - _obstaclesAt < ObstacleRefreshSeconds) return;
        _obstaclesForUnit = self.Id;
        _obstaclesAt = now;

        var found = SC2BuildabilityChecker.GatherObstacles(Data, _assetBrowser, self.Id, out var signature);
        if (signature == _obstaclesSignature && found.Length == _obstacles.Length) return;
        _obstacles = found;
        _obstaclesSignature = signature;
    }

    // What the Costs and the water allow the building. Telling walks every cell of the Costs block, so it is redone a few times a
    // second and the overlay only recomputes when the result really changed.
    private readonly SC2BuildRestriction _restriction = new();
    private int _allowedForUnit = -1;
    private double _allowedAt = double.MinValue;

    /// <summary>The Y of the map's water surface, or null when its water is off. Wired by the editor UI, which owns the map session.</summary>
    public Func<float?> GetWaterSurface;

    private void RefreshBuildRestriction(SC2ObjReferenceUnit unit, SC2ReferenceUnitIntel intel, ushort[] heights, int mapX, int mapY)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        if (_allowedForUnit == unit.Id && now - _allowedAt < ObstacleRefreshSeconds) return;
        _allowedForUnit = unit.Id;
        _allowedAt = now;
        _restriction.Refresh(Data, intel, heights, mapX, mapY, GetWaterSurface);
    }

    private static readonly Color SkirtColor = new Color(1f, 1f, 1f, 0.95f);

    /// <summary>Outlines the skirt the ground check looks at and says in a label what the checks found, so a refused spot can be
    /// explained.</summary>
    private void DrawBuildableSkirt(Camera camera, Rect viewportRect)
    {
        if (_buildableUnit == null || _buildableHeights == null || Event.current == null || Event.current.type != EventType.Repaint) return;
        var unit = _buildableUnit;
        var intel = _buildableIntel;
        var rect = SC2BuildSkirtMath.SkirtAround(unit.Position.x, unit.Position.z, intel.FootprintSizeX, intel.FootprintSizeZ);
        var report = SC2BuildSkirtMath.Evaluate(_buildableHeights, _buildableMapX, _buildableMapY, rect, intel.FlattenSkirt, intel.MaxGroundVariation);
        Func<float, float, float> sample = _terrainShell.SampleLiveHeightGrid;

        DrawSkirtOutline(camera, viewportRect, rect.X0, rect.X1, rect.Z0, rect.Z1, SkirtColor, sample);
        var overlaps = SC2BuildSkirtMath.FindOverlap(unit.Position.x, unit.Position.z, intel.FootprintSizeX, intel.FootprintSizeZ, _obstacles) >= 0;
        var cellsWide = _buildableMapX - 1;
        var costsBlocked = !SC2BuildSkirtMath.FootprintCellsAllowed(_restriction.AllowedCells, cellsWide, _buildableMapY - 1, unit.Position.x, unit.Position.z,
            intel.FootprintSizeX, intel.FootprintSizeZ);

        var anchor = SC2TerrainShell.WorldToGameGui(camera, viewportRect, new Vector3(unit.Position.x, sample(unit.Position.x, unit.Position.z), unit.Position.z));
        if (!viewportRect.Contains(anchor)) return;
        anchor.y += 18f;
        SC2TerrainShell.DrawFloatingLabel(anchor, DescribeSkirtReport(report, overlaps, costsBlocked),
            report.Buildable && !overlaps && !costsBlocked ? new Color(0.35f, 0.9f, 0.45f) : new Color(1f, 0.4f, 0.35f));
    }

    private string DescribeSkirtReport(SC2SkirtReport report, bool overlapsBuilding, bool costsBlocked)
    {
        string N(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (!report.InsideGrid) return "Can't build: the skirt reaches outside the map";
        var verdict = report.Buildable && !overlapsBuilding && !costsBlocked ? "Can build" : "Can't build";
        var text = report.Levelled
            ? $"{verdict}: edge {N(report.MinHeight)}..{N(report.MaxHeight)}, level {N(report.Pivot)}, off by {N(report.Deviation)} (limit {N(report.Tolerance)})"
            : $"{verdict}: skirt {N(report.MinHeight)}..{N(report.MaxHeight)}, spread {N(report.Deviation)} (limit {N(report.Tolerance)})";
        if (overlapsBuilding) text += "; overlaps another building";
        if (costsBlocked) text += "; " + _restriction.DescribeBlock();
        return text;
    }

    /// <summary>A rectangle through whole vertices, laid on the ground vertex by vertex.</summary>
    private static void DrawSkirtOutline(Camera camera, Rect viewportRect, int x0, int x1, int z0, int z1, Color color, Func<float, float, float> sample)
    {
        var points = new List<Vector2>();
        void Add(int x, int z) => points.Add(SC2TerrainShell.WorldToGameGui(camera, viewportRect, new Vector3(x, sample(x, z) + 0.2f, z)));
        for (var x = x0; x <= x1; x++) Add(x, z0);
        for (var z = z0 + 1; z <= z1; z++) Add(x1, z);
        for (var x = x1 - 1; x >= x0; x--) Add(x, z1);
        for (var z = z1 - 1; z >= z0; z--) Add(x0, z);

        var previousColor = GUI.color;
        var previousMatrix = GUI.matrix;
        GUI.color = color;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (!viewportRect.Contains(a) || !viewportRect.Contains(b)) continue;
            var delta = b - a;
            var length = delta.magnitude;
            if (length < 0.5f) continue;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - 1f, length, 2f), Texture2D.whiteTexture);
            GUI.matrix = previousMatrix;
        }
        GUI.color = previousColor;
    }

    /// <summary>The same rotated-square "pick-dot" glyph
    /// SC2ObjectsShell.DrawViewportGlyph draws for every real Unit/Prop (own
    /// small copy here rather than a shared call — that method is private
    /// to SC2ObjectsShell and reference units intentionally don't share its
    /// GPU-instanced-marker machinery, which is built for thousands of
    /// objects; a handful of reference units doesn't need it) — gives the
    /// user an obvious point to click/drag instead of having to land
    /// exactly on the real mesh, and the highlighted color marks whichever
    /// one Delete/the Show* checkboxes in the panel would currently act on.</summary>
    private void DrawReferenceUnitGlyphs(Camera camera, Rect viewportRect)
    {
        if (Data == null || Event.current == null || Event.current.type != EventType.Repaint) return;
        foreach (var unit in Data.ObjReferenceUnits)
        {
            var gui = SC2TerrainShell.WorldToGameGui(camera, viewportRect, unit.Position);
            if (!viewportRect.Contains(gui)) continue;
            var selected = unit.Id == _selectedId;
            DrawGlyph(gui, selected ? new Color(1f, 0.85f, 0.2f, 0.95f) : new Color(0.9f, 0.92f, 0.95f, 0.85f));
        }
    }

    private static void DrawGlyph(Vector2 center, Color color)
    {
        var oldColor = GUI.color;
        var oldMatrix = GUI.matrix;
        const float size = 10f;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(45f, center);
        GUI.DrawTexture(new Rect(center.x - size * .5f, center.y - size * .5f, size, size), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;

        GUI.color = new Color(.04f, .05f, .07f, Mathf.Clamp01(color.a));
        var innerSize = Mathf.Max(2f, size - 5f);
        GUI.DrawTexture(new Rect(center.x - innerSize * .5f, center.y - innerSize * .5f, innerSize, innerSize), Texture2D.whiteTexture);
        GUI.color = oldColor;
        GUI.matrix = oldMatrix;
    }

    /// <summary>Same 1-world-unit grid snap SC2ObjectsShell's own SnapXZ
    /// applies to every real Unit/Marker placement and drag (Props stay
    /// freeform there; reference units are buildings, not props, so they
    /// snap too) — kept as its own copy rather than a shared reference
    /// since SnapXZ is a private one-liner on that class, not a public
    /// utility.</summary>
    private static Vector3 SnapXZ(Vector3 pos) => new(Mathf.Round(pos.x), pos.y, Mathf.Round(pos.z));

    private void PlaceAt(Vector3 position)
    {
        if (Data == null || _pendingAsset == null) return;
        position = SnapXZ(position);
        _terrainShell?.RecordUndoCheckpoint("Place Reference Unit");
        var unit = new SC2ObjReferenceUnit
        {
            Id = Data.AllocateObjectId(),
            BlueprintId = _pendingAsset.Id,
            DisplayName = string.IsNullOrWhiteSpace(_pendingAsset.DisplayName) ? _pendingAsset.Id : _pendingAsset.DisplayName,
            Position = position,
            AnchorToTerrain = true,
        };
        Data.ObjReferenceUnits.Add(unit);
        _selectedId = unit.Id;
        if (!SC2GameAssetScenePreview.TryShowUnit(unit.Id, _pendingAsset, _assetBrowser.GameDataRoot, unit.Position, Quaternion.identity, out var previewError))
            ReportStatus?.Invoke("Reference placed, but preview unavailable: " + previewError);
        else
            ReportStatus?.Invoke($"Placed reference: {unit.DisplayName}.");
        _intelCache[unit.Id] = SC2ReferenceUnitIntelReader.Read(_pendingAsset.Blueprint);
        MarkDirty();
        RepaintHost?.Invoke();
    }

    private void DeleteSelected()
    {
        if (Data == null || _selectedId < 0) return;
        var index = Data.ObjReferenceUnits.FindIndex(r => r.Id == _selectedId);
        if (index < 0) return;
        _terrainShell?.RecordUndoCheckpoint("Delete Reference Unit");
        _intelCache.Remove(_selectedId);
        Data.ObjReferenceUnits.RemoveAt(index);
        // InstanceKey/Remove are private to SC2GameAssetScenePreview — its
        // own SyncTransforms cleanup pass (now aware of ObjReferenceUnits
        // too) removes the now-stale preview batch itself once it no longer
        // finds this id in the list, the same public entry point every
        // other deletion path in this codebase already relies on.
        SC2GameAssetScenePreview.SyncTransforms(Data);
        ReportStatus?.Invoke("Reference unit deleted.");
        _selectedId = -1;
        MarkDirty();
        RepaintHost?.Invoke();
    }

    /// <summary>A freshly-loaded map's reference units have real data but no
    /// live GPU-instanced preview yet (SC2GameAssetScenePreview batches are
    /// runtime-only, never persisted) — mirrors SC2ObjectsShell.
    /// SyncMissingAssetPreviews's own reasoning exactly. Also where each
    /// unit's blueprint intel gets (re)resolved, since SC2GameAsset/
    /// SC2Blueprint themselves are never serialized — only BlueprintId is.</summary>
    private void SyncMissingPreviews()
    {
        if (Data == null || Data.ObjReferenceUnits.Count == 0) return;
        _assetBrowser.EnsureIndexed();
        foreach (var unit in Data.ObjReferenceUnits)
        {
            if (SC2GameAssetScenePreview.HasUnitPreview(unit.Id) && _intelCache.ContainsKey(unit.Id)) continue;
            var asset = _assetBrowser.FindUnitAsset(unit.BlueprintId);
            if (asset == null) continue; // catalog not indexed yet, or blueprint no longer resolvable — try again next frame/on next index
            if (!SC2GameAssetScenePreview.HasUnitPreview(unit.Id))
                SC2GameAssetScenePreview.TryShowUnit(unit.Id, asset, _assetBrowser.GameDataRoot, unit.Position, Quaternion.Euler(unit.OrientationEuler), out _);
            if (!_intelCache.ContainsKey(unit.Id))
                _intelCache[unit.Id] = SC2ReferenceUnitIntelReader.Read(asset.Blueprint);
        }
    }

    private bool TryHitReferenceUnit(Camera camera, Rect viewportRect, Vector2 guiMousePosition, out int id)
    {
        const float hitPixelRadius = 14f;
        id = -1;
        if (Data == null) return false;
        var best = hitPixelRadius;
        foreach (var unit in Data.ObjReferenceUnits)
        {
            var gui = SC2TerrainShell.WorldToGameGui(camera, viewportRect, unit.Position);
            if (!viewportRect.Contains(gui)) continue;
            var dist = Vector2.Distance(guiMousePosition, gui);
            if (dist < best) { best = dist; id = unit.Id; }
        }
        return id >= 0;
    }

    private static readonly Color RadarColor = new(0.25f, 0.45f, 1f, 0.85f);
    private static readonly Color SonarColor = new(0.35f, 0.9f, 0.95f, 0.85f);
    private static readonly Color VisionColor = new(1f, 1f, 1f, 0.85f);
    private static readonly Color WeaponColor = new(1f, 0.55f, 0.15f, 0.85f);

    private void DrawIntelOverlays(Camera camera, Rect viewportRect)
    {
        if (Data == null || Event.current == null || Event.current.type != EventType.Repaint) return;
        // Terrain-hugging (per-vertex height sampling around the ring) is
        // the right behavior for this feature — a flat, single-height ring
        // visibly floats above/below the ground wherever the terrain varies
        // across its footprint, which is confusing for exactly the thing
        // this overlay exists to show (does this range actually reach that
        // hill/valley). An earlier attempt at this warped large rings into
        // chord-cut shapes on jagged procedural terrain; DrawTerrainHuggingRing
        // now smooths its per-vertex height samples (see its own comment)
        // instead of connecting raw, independently-sampled heights straight
        // across a single noisy/spiky sample.
        var sampleHeight = _terrainShell != null ? _terrainShell.SampleLiveHeightGrid : (Func<float, float, float>)null;
        foreach (var unit in Data.ObjReferenceUnits)
        {
            if (!_intelCache.TryGetValue(unit.Id, out var intel)) continue;
            var labelIndex = 0;
            if (unit.ShowRadar && intel.RadarRadius > 0f)
                DrawRing(camera, viewportRect, unit.Position, intel.RadarRadius, RadarColor, $"Radar {intel.RadarRadius:0}", labelIndex++, sampleHeight: sampleHeight);
            if (unit.ShowSonar && intel.SonarRadius > 0f)
                DrawRing(camera, viewportRect, unit.Position, intel.SonarRadius, SonarColor, $"Sonar {intel.SonarRadius:0}", labelIndex++, sampleHeight: sampleHeight);
            if (unit.ShowVision && intel.VisionRadius > 0f)
                DrawRing(camera, viewportRect, unit.Position, intel.VisionRadius, VisionColor, $"Vision {intel.VisionRadius:0}", labelIndex++, sampleHeight: sampleHeight);
            if (unit.ShowWeapons)
                foreach (var weapon in intel.Weapons)
                    DrawRing(camera, viewportRect, unit.Position, weapon.MaxRadius, WeaponColor, FormatWeaponLabel(weapon), labelIndex++, weapon.MinRadius, sampleHeight);
        }
    }

    private static string FormatWeaponLabel(SC2ReferenceUnitWeapon weapon)
    {
        if (weapon.FireTargetLayerCaps.Count == 0) return weapon.Label;
        var caps = string.Join(", ", weapon.FireTargetLayerCaps.Select(c => string.IsNullOrEmpty(c.Value) ? c.Key : $"{c.Key}→{c.Value}"));
        return $"{weapon.Label} ({caps})";
    }

    /// <summary>Passing sampleHeight makes DrawGameViewBrush take its
    /// terrain-hugging branch (per-point-projected, smoothed polyline around
    /// the circle — see DrawTerrainHuggingRing) instead of the flat single-
    /// plane branch. Known limitation inherited from that shared function:
    /// it early-returns if the circle's CENTER (the unit's own position)
    /// projects outside viewportRect, so a large ring can vanish if the unit
    /// itself scrolls just off-screen even though most of the ring would
    /// still be visible — acceptable for v1 (pre-existing behavior of a
    /// function built for mouse-following brush cursors, where this never
    /// mattered).</summary>
    private static void DrawRing(Camera camera, Rect viewportRect, Vector3 center, float radius, Color color, string label, int labelIndex, float innerRadius = 0f, Func<float, float, float> sampleHeight = null)
    {
        if (radius <= 0f) return;
        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, center, radius, color, color, innerRadius, sampleHeight);
        var edgeWorld = center + Vector3.forward * radius;
        if (sampleHeight != null) edgeWorld.y = sampleHeight(edgeWorld.x, edgeWorld.z);
        var edgeGui = SC2TerrainShell.WorldToGameGui(camera, viewportRect, edgeWorld);
        if (!viewportRect.Contains(edgeGui)) return; // don't label a point sitting over a side panel
        edgeGui.y -= 14f * labelIndex; // stagger nested/close rings' labels instead of overlapping
        SC2TerrainShell.DrawFloatingLabel(edgeGui, label, color);
    }

    /// <summary>Drawn inside SC2GameViewEditorUI's new right panel — a
    /// "place one" button plus a compact list of everything already placed,
    /// each with a delete button; the currently-selected entry additionally
    /// gets its four per-instance Show* declutter checkboxes.</summary>
    public void DrawRightPanelSection(float width)
    {
        // Plain GUI.skin styles only (no EditorStyles) — this whole UI also
        // compiles and runs in a Standalone Build & Run player, where
        // UnityEditor.EditorStyles doesn't exist at all.
        //
        // Every control below gets an explicit MaxWidth tied to the caller's
        // own column width — GUILayout otherwise sizes a Toggle/Button/Label
        // from its TEXT content and lets that request exceed the parent
        // BeginVertical's declared width (a long unit DisplayName, or 4
        // "Weapons"-style buttons in a row, easily do), which is what forced
        // the containing ScrollView to grow a horizontal scrollbar.
        GUILayout.Label("REFERENCE UNITS", GUI.skin.label, GUILayout.MaxWidth(width));
        if (GUILayout.Button(_placementArmed ? "Click viewport..." : "Place unit...", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.MaxWidth(width)))
            OpenBrowserForPlacement();

        if (Data == null || Data.ObjReferenceUnits.Count == 0) return;
        var deleteButtonWidth = SC2UiScale.Pxf(18f);
        var nameMaxWidth = Mathf.Max(10f, width - deleteButtonWidth - SC2UiScale.Pxf(4f));
        foreach (var unit in Data.ObjReferenceUnits.ToArray())
        {
            GUILayout.BeginHorizontal(GUILayout.MaxWidth(width));
            var selected = unit.Id == _selectedId;
            var label = string.IsNullOrEmpty(unit.DisplayName) ? unit.BlueprintId : unit.DisplayName;
            if (GUILayout.Toggle(selected, label, GUI.skin.toggle, GUILayout.MaxWidth(nameMaxWidth)))
            {
                if (!selected) _selectedId = unit.Id;
            }
            else if (selected) _selectedId = -1;
            if (GUILayout.Button("x", GUILayout.Width(deleteButtonWidth)))
            {
                _selectedId = unit.Id;
                DeleteSelected();
                GUILayout.EndHorizontal();
                continue;
            }
            GUILayout.EndHorizontal();

            if (!selected) continue;
            // Stacked one-per-row (not a single crowded row of 4) — at this
            // panel's ~118px width, 4 side-by-side buttons is exactly the
            // kind of content that used to overflow into horizontal scroll.
            unit.ShowRadar = GUILayout.Toggle(unit.ShowRadar, "Radar", GUI.skin.button, GUILayout.MaxWidth(width));
            unit.ShowSonar = GUILayout.Toggle(unit.ShowSonar, "Sonar", GUI.skin.button, GUILayout.MaxWidth(width));
            unit.ShowVision = GUILayout.Toggle(unit.ShowVision, "Vision", GUI.skin.button, GUILayout.MaxWidth(width));
            unit.ShowWeapons = GUILayout.Toggle(unit.ShowWeapons, "Weapons", GUI.skin.button, GUILayout.MaxWidth(width));
            DrawBuildableToggle(unit, width);
        }
    }

    private void DrawBuildableToggle(SC2ObjReferenceUnit unit, float width)
    {
        var hasSkirt = _intelCache.TryGetValue(unit.Id, out var intel) && intel.HasBuildSkirt;
        var previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && hasSkirt;
        var show = GUILayout.Toggle(unit.ShowBuildable && hasSkirt, "Buildable", GUI.skin.button, GUILayout.MaxWidth(width));
        GUI.enabled = previousEnabled;
        if (show != unit.ShowBuildable && (hasSkirt || !show))
        {
            unit.ShowBuildable = show;
            // The map is coloured for one building at a time.
            if (show)
                foreach (var other in Data.ObjReferenceUnits)
                    if (other != unit) other.ShowBuildable = false;
            MarkDirty();
        }
        if (!hasSkirt) GUILayout.Label("No Footprint in the blueprint", GUI.skin.label, GUILayout.MaxWidth(width));
        else if (unit.ShowBuildable)
        {
            _buildableShowCentres = GUILayout.Toggle(_buildableShowCentres, "Centres only", GUI.skin.button, GUILayout.MaxWidth(width));
            GUILayout.Label(_buildableShowCentres
                ? "Green: the building's centre may be here."
                : "Green: the building can cover this ground. Red: it cannot.", GUI.skin.label, GUILayout.MaxWidth(width));
        }
    }

    private void MarkDirty()
    {
#if UNITY_EDITOR
        if (Data != null) EditorUtility.SetDirty(Data);
#endif
    }
}
