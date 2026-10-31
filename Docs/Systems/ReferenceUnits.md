# Reference units, buildable overlay and intel circles

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Reference units are real game units and buildings placed only as a scale aid. A placed unit shows its radar, sonar, vision and weapon ranges as circles, and a building can colour the whole map by where it could be built (its footprint skirt, the
ground check of the game, other buildings and the Costs block it needs). **They are never exported**: they do not reach `mapobjs.bdf`, `_save.lua` or `_scenario.lua`.

Not included: real units and props ([objects](Objects.md)); the Costs rules that a building must obey ([costs](Costs.md), read here only through `SC2BuildRestriction`); the unit browser internals (`SC2UnitPropBrowser` is only a picker).

## 2. Where the code is

| Part | Files |
|---|---|
| Shell | `Scripts/Unified/SC2ReferenceUnitsShell.cs` (arm, place, select, drag, delete, the right-panel section, intel circles, the buildable overlay wiring) |
| Blueprint facts | `Scripts/Unified/SC2ReferenceUnitIntel.cs` (`SC2ReferenceUnitIntel`, `SC2ReferenceUnitWeapon`, `SC2ReferenceUnitIntelReader.Read(SC2Blueprint)`) |
| Buildable colouring | `Scripts/Unified/SC2BuildableOverlay.cs` (background task over the height grid), `SC2BuildabilityChecker.cs` (`SC2BuildCheck`, `SC2BuildRestriction`, `SC2PlacedFootprint`, `GatherObstacles`), `Scripts/Core/SC2BuildSkirtMath.cs` (the game's ground rule) |
| Rendering | the meshes come from `SC2GameAssetScenePreview` (the same path as real units); the overlay texture is pushed into the terrain by `SC2GameViewEditorUI.PushBuildableOverlay` |
| Data | `SC2ObjReferenceUnit` in `Core/SC2ObjectsModel.cs`, `SC2TerrainData.ObjReferenceUnits`, `SC2ObjectsDocument.ReferenceUnits` |

## 3. Data

`SC2ObjReferenceUnit`: `Id` (shares the object id counter with real units, so ids never collide), `BlueprintId`, `DisplayName`, `Position`, `OrientationEuler`, `AnchorToTerrain`, `HeightOffset`, the per-unit toggles `ShowRadar`, `ShowSonar`,
`ShowVision`, `ShowWeapons`, and `ShowBuildable` (only one unit shows the colouring at a time). The list is part of the objects document and of the saved map, so a map keeps its reference units between sessions. The intel of a blueprint (ranges,
footprint, `FlattenSkirt`, `MaxGroundVariation`, `BuildOnLayerCaps`) is read from the `.bp` on demand and cached per unit id in the shell; it is not saved.

## 4. Flows

1. **Place**: `OpenBrowserForPlacement` opens the unit browser; picking a unit arms placement; a click in the viewport raycasts (the host passes the same raycast as the Measure tool, imported terrain first) and creates the unit snapped to the grid.
   While armed, **Escape cancels** placement; with a unit selected and nothing armed, Escape deselects; Delete or Backspace removes the selected unit. A panel hint tells the user how to leave placement mode.
2. **Move**: dragging a selected unit pushes one undo checkpoint on the first real movement (a plain click never records a no-op step).
3. **Intel circles**: rendered from the cached intel; each circle kind has its own toggle per unit.
4. **Buildable**: each frame the shell picks the unit with `ShowBuildable`, refreshes the other buildings' footprints and the Costs restriction at most four times a second (`ObstacleRefreshSeconds`), and asks `SC2BuildableOverlay` for a texture. The overlay computes on a
   background task over the heightmap the editor would export and keeps showing the previous result until the new one is ready. Green means some valid placement covers the cell, red means none does (or, in centre mode, whether a building centred on the vertex is valid).
5. **Costs and water**: a building that needs water or seabed is limited to cells under the water surface (`GetWaterSurface`); a land building is limited to cells its Costs block passes.

## 5. Invariants

- **Never exported.** The export code never references this shell or `ObjReferenceUnits`; this is checkable with a search, not an audit. A new export path must not read them.
- **Ids share the real-unit counter** (`AllocateObjectId`), so a reference unit and a real unit never share an id in the preview instance space.
- **A drag records one undo checkpoint, on the first movement.**
- **The overlay never blocks the editor**: the check runs on a worker and the old texture stays until the new one is ready.
- **The ground rule is the game's** (`SC2BuildSkirtMath`, defaults `DefaultFlattenSkirt` and `DefaultMaxGroundVariation` when the blueprint is silent); the overlay and the skirt outline must use the same function.
- **Only one unit shows the buildable colouring**; the selected one wins.

## 6. Editor and build differences

- Plain GUI, identical in both. The unit browser reads blueprints from the configured game install (`SC2GameInstallSettings`), so without one the browser is empty.

## 7. Extension points and how to change it

- **A new intel circle**: a field in `SC2ReferenceUnitIntel`, the reader branch, a toggle on `SC2ObjReferenceUnit` (with a default that keeps old saves unchanged), the drawing, the panel row.
- **A new buildability condition**: a field in `SC2BuildCheck`, a term in `SC2BuildabilityChecker` and in the overlay's `Key` (or the overlay will not recompute when the new input changes).

## 8. Editing, verification and tests

**How to edit it safely.** Keep the shell free of export code. When a new input affects the colouring, add it to the overlay key. Test with a building that has a footprint, one without, and a water building.

**Tests today**: `BuildSkirtMathTests` (the ground rule). The shell, the overlay, the checker and the intel reader have no unit test.

**Tests a change must add.** A ground-rule change: a case in `BuildSkirtMathTests`. An intel reader change: a test with a small blueprint table (the reader walks a parsed `SC2Blueprint`, which needs the game-asset library in the standalone project; until then a harness scenario that reads a real `.bp`).
A change to the saved fields: a round trip through the objects document.

## 9. Known limitations and unverified behaviour

- **Undo of a reference-unit action is probably incomplete**: place, move and delete record a checkpoint on the terrain's snapshot stack, and a search of the snapshot class finds no `ObjReferenceUnits` field, so undoing the step would not bring the unit back (by reading the code only; not exercised, see [Undo](../Interactions/UndoAcrossSystems.md)).
- The overlay's speed on the largest maps was not measured here.
- The game's own rule for the skirt was taken from the blueprint fields and `SC2BuildSkirtMath`; agreement with the game was not re-checked for this document.

## 10. Related documents and review

[Objects](Objects.md), [Costs](Costs.md), [Water](Water.md), [Editor UI](EditorUi.md), [Terrain data and runtime](TerrainDataAndRuntime.md).
Interactions with other systems: [Objects and scenario](../Interactions/ObjectsAndScenario.md), [Undo](../Interactions/UndoAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
