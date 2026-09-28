using UnityEngine;

public sealed partial class SC2GameViewEditorUI
{
    private SC2DecalsShell _decalsShell;

    private void EnsureDecalsShell()
    {
        if (_decalsShell != null || _mapSession == null)
            return;

        // Decal symmetry needs a live reference to the terrain shell that
        // owns Data.Symmetry/SymmetryCenter/RadialCount.
        EnsureTerrainShell();
        _decalsShell = new SC2DecalsShell(_mapSession, _terrainShell)
        {
            RepaintHost = RepaintGameView
        };
        // See SC2TerrainShell.HeightBrushApplied's own doc comment — keeps
        // AlignToSurface decals following live Geometry-mode sculpting
        // instead of only ever aligning once at placement/edit time.
        _terrainShell.HeightBrushApplied += (center, radius) => _decalsShell?.ReprojectAlignedDecalsInRadius(center, radius);
        // See SC2DecalsShell.MarkPreviewDirtyInRadius's own doc comment —
        // lets EnsurePreview's terrain-change rebuild scope itself to just
        // the placements a stroke actually reached, instead of redoing
        // every placement's own Collect+GeometryBuilder work on every edit.
        _terrainShell.HeightBrushApplied += (center, radius) => _decalsShell?.MarkPreviewDirtyInRadius(center, radius);
        // Moving an imported terrain object changes the ground under its old and new region.
        _terrainShell.ImportedTerrainMoved += bounds => _decalsShell?.ReprojectDecalsInBounds(bounds);
        // A genuinely new document just loaded (SetData/LoadFromDisk) — see
        // SC2DecalsShell.InvalidatePreviewCache's own doc comment for why this shell (a
        // long-lived singleton, not recreated per load) needs to be told explicitly, or its
        // per-placement preview cache survives the reload and every decal renders wrong until
        // individually touched.
        _terrainShell.DataChanged += _ => _decalsShell?.InvalidatePreviewCache();
    }

    private void DrawDecalsInspector()
    {
        EnsureDecalsShell();
        _decalsShell?.DrawEmbeddedGUI(PanelContentWidth);
    }

    private void HandleDecalsGameViewInput()
    {
        // Bailing out entirely whenever _mainTab != 4 would mean placed
        // decals never render after loading a saved map until the Decals
        // tab is opened, since the mesh-rebuild step (EnsurePreview, inside
        // OnGameViewGUI) would never run either. Keep
        // the placed-decal mesh synced every frame regardless of active
        // tab (cheap — see EnsurePreview's own doc comment); only the
        // selection outlines/transform-drag input handling stay gated to
        // the Decals tab actually being active.
        EnsureDecalsShell();
        var camera = ResolveCamera();
        if (_decalsShell == null || camera == null)
            return;
        // Visibility menu's "Decals" toggle — applied every frame regardless of tab, same as
        // OverlayTerrain/OverlayWater (SyncTerrainOverlayFlags), so it stays in effect after a
        // tab switch instead of only until the next preview rebuild.
        _decalsShell.SetOverlayVisible(OverlayDecals);
        if (_mainTab != 4)
        {
            _decalsShell.EnsurePreview();
            return;
        }
        if (Event.current == null)
            return;

        var mouse = Event.current.mousePosition;
        var allowInput = _viewportRect.Contains(mouse) && !IsMouseOverUi(mouse);
        _decalsShell.OnGameViewGUI(camera, _viewportRect, mouse, allowInput);
    }
}
