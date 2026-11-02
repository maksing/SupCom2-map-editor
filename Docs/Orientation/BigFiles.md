# The big files: what is where and how to work in them

Status: first draft from reading the section markers and method lists at commit `3077a7d`; line numbers are from that commit and **drift with every edit**, so use the section headings and method names to search, not the numbers. Not yet reviewed by a second reader.

Six files hold most of the editor's logic. Their sizes at that commit:

| File | Lines | Methods (found by search) | What it is |
|---|---:|---:|---|
| `Scripts/Unified/SC2ObjectsShell.cs` | 9026 | about 300 | the whole Objects tab |
| `Scripts/Unified/SC2GameViewEditorUI.cs` | 8538 | about 260 | the host: menus, panels, viewport, imported terrain, hotkeys, camera |
| `Scripts/Unified/SC2TerrainShell.cs` | 5826 | about 210 | the hub: live data, Terrain tab, preview, save, undo, texture pickers |
| `Scripts/TerrainEditor/SC2TerrainExporter.cs` | 3279 | 38 | the export, in which **one method is about 1230 lines** |
| `Scripts/TerrainEditor/SC2TerrainRuntime.cs` | 2118 | | the GPU terrain (plus `SC2TerrainRuntime.BlendLayers.cs`) |
| `Scripts/MapDocument/Storage/SC2MapDocumentSerializer.cs` | 2042 | | the manifest reader and writer |

They are not bad code for being large; they are risky because **one function change can reach unrelated modes**. `SC2ObjectsShell`, for instance, handles the interface, input, placement, selection, symmetry, undo, import, game assets and the document sync of seven object kinds in one class.

## 1. Rules for working in any of them

1. **Find by section heading or method name** (`grep -n "// ── "` for the section headers); the headers are the only structure the files have.
2. **Change the smallest region** and do not reorder, rename or reformat code you did not need to change: the files are edited by several work streams at once, and a moved block makes every other session's diff conflict.
3. **Stage only your own hunks** (`git add -p`, or `git apply --cached` of an extracted hunk). A commit that includes another stream's hunks in these files is the usual way unfinished work gets committed.
4. **Do not add new logic here if it can be pure.** Extract the rule into `Scripts/Core`, test it, and call it ([Core rules](../Systems/CoreRules.md)). Every new `if` inside a 9000-line class is a rule no test can reach.
5. **A new object kind, tab or tool gets its own file** (a new shell class), registered in the host, rather than another section here.
6. **Check the `#if UNITY_EDITOR` blocks.** These files contain many; code inside them does not exist in a player, and code outside them that calls an editor API compiles in a player only through `UnityEditorRuntimeShim` ([Editor vs build](../Systems/EditorVsBuild.md)). Compile a player build after any change.
7. **Search for the callers before changing a signature**; shells call each other's static helpers (`SC2TerrainShell.DrawTexturePicker`, `GameGuiPointToRay`, `TerrainGeometryVersion`) from other files.
8. **A per-frame function must stay cheap**: `OnGUI`, `Update`, `OnGameViewGUI` run every frame, and several regressions were stutter from a rebuild put on a per-event path ([Life of an edit](../Interactions/LifeOfAnEdit.md)).

## 2. `SC2ObjectsShell.cs` (the Objects tab)

Section headings in order, with the topic of each:

| Section | Contents |
|---|---|
| top | `MarkObjectsDirty`, the heightmap-props signature, selection by stable id per category, search and filters, misc UI state |
| `Undo (JSON snapshot of the whole Objects domain)` and `Undo/Redo` | the shell's own snapshot undo and its coordinator |
| `Shared small helpers` | list rows, viewport click, world to GUI, height sampling, raycasts against imported terrain and the scene, snapping, marker placement candidates |
| `Live symmetry links` | symmetry groups: lookups, propagating a move, a render attribute or a field to the copies |
| `#if UNITY_EDITOR` block and the validation methods | `ImportObjectsFromPath`, `FinishObjectsImport`, `Validate`, `CollectValidationIssues`, `RefreshMassBuildability`, `ValidateStructure` |
| `1. Armies` | the Armies tab, army rename (and its cascade into the document and the lobby) |
| `2. Units` | pre-placed units and groups |
| `3. Props`, `Props brush/scatter`, `Mixed-value multi-edit` | props, the scatter brush, multi-edit of mixed values |
| `4. Markers`, `Marker category grid` | markers and their templates |
| `5. Paths (graphs)`, `Path grid` | path graphs and the generated grid |
| `6. Zones`, `7. Chains` | areas and chains |
| after the tabs | connect-to-chain, placement arming and click handling, the footprint overlay, the props brush tick, placing an army, **`OnGameViewGUI`** (the viewport entry point), box select, drag, the move, rotate and scale gestures, line-batch drawing for chains, zones and graph edges, viewport selection |

**Routes inside it:** a viewport click goes `OnGameViewGUI` → placement or selection or drag → `SetObjectPosition...` → `MarkObjectsDirty`; a panel edit goes through the tab's `Draw...` method → the same setters. Symmetry propagation happens in the setters, not in the callers.

## 3. `SC2GameViewEditorUI.cs` (the host)

Regions by method clusters (approximate order):

| Region | Contents |
|---|---|
| start | `LoadSharedTerrainData`, labels, `SelectMainTab`; the Measure tool, the map frame and reference image overlays state; the imported-terrain gestures and **its own undo** |
| imported terrain snapshots | capture, sync to the document, restore from the document, snapshot apply, undo and redo |
| lifecycle | `OnEnable`, `OnDisable`, `LateUpdate`, `Update`, `TickAutosave`, `OnGUI`, `OnDrawGizmos` |
| styles and drawing helpers | `EnsureStyles`, procedural textures, `DrawSolidRect` |
| header and menus | `DrawHeader`, the File, Edit, View, Visibility, Customize, Autosave, Tools, Symmetry menus |
| panels | `DrawMenuPanel`, `DrawSubtabs`, `DrawMainPanel`, the per-tab inspectors |
| shell creation and viewport handlers | `EnsureTerrainShell`, `EnsureCostsShell`, `EnsureCollision2Shell`, `EnsureEnvironmentShell`, `EnsureScenarioShell`, `EnsureExportShell`, `EnsureObjectsShell`, `EnsureReferenceUnitsShell`, and each `Handle...GameViewInput` |
| import panels and imported terrain input | the Import Map, Info.bdf, lobby and mission panels; imported terrain click, move, rotate, scale gestures |
| navigation and tools | layout rects, the navigation gizmo, the Measure tool, the map frame overlay, the reference image |
| file operations | new, load, delete, duplicate, restore backup, **`DispatchToMostRecent`, `UndoTerrainEdit`, `RedoTerrainEdit`**, `SaveActiveTerrain`, quit handling |
| imported terrain materials and preview | `GetImportedTerrainsForExport`, imported RNM targets, preview modes for imported terrain, imported material cards |
| hotkeys and camera | `HandleGameViewNavigation`, `HandleGlobalUndoRedoSaveHotkey`, `HandleHotkeys`, the preview mode bar, viewport click, orbit, pan, zoom, fly, the top-view mirror, focus |

**Routes inside it:** a frame goes `OnGUI` → panels → overlays → viewport handlers in the fixed order of [Input and overlays](../Interactions/InputAndOverlays.md); a map operation goes menu action → `Request...` → `Apply...`. The imported terrain objects (a large part of the file) are **owned here, not by the terrain shell**.

## 4. `SC2TerrainShell.cs` (the hub)

| Section | Contents |
|---|---|
| `State` | fields, the modes, the background-terrain field group |
| view width tracking, `Public API`, `Init / Cleanup` | |
| `Undo / Redo` | the combined undo, the checkpoint dispatch by data object, seam strokes |
| editor-only blocks | the editor window rendering of the panels |
| `Lighting Bake > Shadow Casting`, `Environment > Point Clouds`, `Lighting Bake > Imported Objects` | the bake-related panels hosted by this shell |
| `Helpers exposed to sub-editors` | static helpers other files call |
| `Toolbar drawing`, `Texture slot` | the pickers (`DrawTexturePicker`, `DrawTexturePickerWithPath`, `DrawTextureThumb`) used by Decals and Environment as well |
| `Triangle counts after simplification` | |
| `Panels` | the Terrain tab's panels |
| `Terrain lifecycle`, `Live height grid` | `SetData` and `DataChanged`, the grid |
| `Top-panel Visibility menu overlays`, save and autosave, `AdoptLoadedDocument`, `LoadFromDisk` | |
| `Preview`, `Sub-editors`, `Preview rendering` | the per-frame `Update` that synchronises water, sky, shared materials, the background and imported environments |

**Routes inside it:** edit → the mode class (`SC2GeometryMode`, `SC2PaintMode`, `SC2MaterialsPairsMode`) → `MarkDirtyAndRepaint` and the event raises; save → `SaveState` → `SyncDataForSave`; per frame → `Update`.

## 5. `SC2TerrainExporter.cs` (the export)

| Method | Lines | Role |
|---|---:|---|
| `ExportCorrect` | about 190 | the entry used by the UI; folder and archive handling |
| **`BuildExportArtifacts`** | **about 1230** | the whole export: layers and materials, texture resolution, merging imported terrain and the background, the water plane, the safety sweep, hfield, costs, collision2, info, mapobjs, decals, Lua |
| `DoExport` | about 130 | writes the artifacts to disk and cleans stale files |
| `BuildSmartExportPlanInternal`, `PreviewSmartExportPlan`, `ExportCorrectSmart` | about 350 | the smart path (not used by the UI) |
| `ComputeImportedVertexTbn`, `MergeImportedTerrain`, `CollectForceExportBasePaths`, `CopyImportedTextures` | | imported terrain merging |
| `ExportRnmMaskByName`, `WriteRnmMaskFiles`, `ApplyImportedRnmMasks` | | lighting masks (section `RNM DDS export`) |
| `BuildCostsModelFromPaintedData`, `BuildWaterMask`, `BuildCollision2Model`, `BuildInfoModel`, `SnapPropsToExportedSurface`, `BuildPreviewGeometryModel` | | the derived files and the preview model |

**`BuildExportArtifacts` is the one place to read before any change to what is written.** Its order is the contract ([Export pipeline](../Systems/ExportPipeline.md), [Height and water](../Interactions/HeightAndWater.md)): a change that moves a step can make the hfield, costs and collision2 disagree. The smart path calls the same method, so a change there affects both paths.

## 6. `SC2TerrainRuntime.cs` and `SC2MapDocumentSerializer.cs`

**Runtime** sections: CPU heights, GPU paint render textures, the chunk grid, brush materials, the blend mask, the pair and tiling lookup, init, chunk rebuild (dirty chunks only), the UV1 compensation, LOD, the heights API, the GPU sculpt brush, the GPU pair paint, preview modes, CPU readback (used by export and save), diffuse arrays, material and shader setup. Blend layers are in the second file (`SC2TerrainRuntime.BlendLayers.cs`).

**Serializer** sections, one per domain, in the same order as the document: Identity, Source, EntryPoint, Archive, Environment, Collision2, Costs, Hfield, Objects (about 370 lines), Terrain (about 500 lines), Scenario (about 350 lines), Decals, then shared value-type helpers. **A new saved field needs a read and a write in its domain's section and a default for older files** ([Sources of truth](SourcesOfTruth.md)).

## 7. When to break a file up

Do not do it as a side effect of a feature. If a split is wanted, it is its own change: extract by cohesion (the viewport input of one object kind, the imported-terrain code in the host, the per-domain parts of the serializer), keep the public surface the same, and run the player build and the standalone suite before and after. Moving code between files while other streams have unstaged edits in them is the main cost; coordinate first.
