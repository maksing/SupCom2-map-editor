# Change impact: "I am changing X, what else must I check?"

Status: first draft, assembled from the other pages of this section at commit `17863e4`; not yet reviewed by a second reader. Each row names the other systems that depend on the thing you change and the page that explains the dependency. Use it before you start and again before you commit; a change that touches a row and does not touch its checks is probably unfinished.

Add a row in the same commit that adds a dependency ([README](README.md), rule 6).

## 1. Changing data

| You change | Also check | Why | Page |
|---|---|---|---|
| any field of `SC2TerrainData` | its owner group, the document counterpart and bridge, the save carry-across if the document holds it only, the terrain snapshot if it must undo | the live data and the document meet only at save and load | [Data ownership](DataOwnership.md), [Save, import, export](SaveImportExport.md) |
| a document-only domain (lobby positions, mission, decals, imported RNM, imported entries) | `BuildDocumentSnapshot` **and** `AdoptLoadedDocument` | the save builds a fresh document from the live data; what it does not copy across is lost | [Save, import, export](SaveImportExport.md) |
| the environment header or an environment texture | `SyncDataForSave`, the working header and the data header on import, the water preview, Costs generation, the bake's sun, Objects' marker type resolution, the export's path sweep | the header exists twice and is read by many | [Height and water](HeightAndWater.md), [Textures](TexturesAcrossSystems.md) |
| the water level or `WaterEnabled` | every reader in the table of water readers; pass the document's working header into any `Core` code | `Core` cannot reference the session | [Height and water](HeightAndWater.md) |
| `PlayableWater` | the Costs block set, the path graphs, Objects, the export; it is restored by a terrain undo | written by Costs, read widely | [Data ownership](DataOwnership.md) |
| symmetry | every caller of `GetSymmetricPositions`, `SC2LobbySpawnMath`, the mass and army validators; it is part of the terrain auxiliary state | one setting, many users | [Objects and scenario](ObjectsAndScenario.md) |
| army names or order | lobby positions, `ReconcileArmyNamesInDocument`, `_scenario.lua`, `_save.lua`, the mission | the order is the order of the start markers | [Objects and scenario](ObjectsAndScenario.md) |
| an `Obj*` list or its fields | `MarkObjectsDirty()`, the objects document, the export, the validators, the mission's map info, the heightmap-props hash | smart export and the height grid rely on the signal | [Objects and scenario](ObjectsAndScenario.md) |
| a field of `SC2MaterialPair` | `SC2TerrainUndoStack.Capture` (explicit field list), the document pair, the shared background pairs, the export | the clone list is explicit | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| a texture slot or the path tag format | the picker key, source tags and force-copy flags, `SyncDataForSave`, the capture, the export resolution and the final sweep | the tags make the export correct | [Textures](TexturesAcrossSystems.md) |

## 2. Changing behaviour of the ground

| You change | Also check | Page |
|---|---|---|
| a tool or path that changes heights | a checkpoint before the first change; `HeightBrushApplied` per dab; at the end `MarkDirty(TerrainGeometry)`, `InvalidateLiveHeightGrid()`, heights copied to the data, `SetDirty`, `BumpTerrainGeometryVersion()`; the background | [Life of an edit](LifeOfAnEdit.md) |
| what counts as ground (props in the heightmap, imported terrain, water pairs) | the live height grid invalidation and the props signature; the exported projections | [Height and water](HeightAndWater.md) |
| the live height grid or its debounce | every consumer: Costs, placement, Decals, the buildable overlay, the HUD | [Mechanisms](Mechanisms.md) |
| a pair assignment path | `CompleteStroke`: the water check, the outline update, the geometry version | [Life of an edit](LifeOfAnEdit.md) |
| the background's rim or footprint | the rim sync after core edits, export, the bake, the seam reach | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| the hfield projection | hfield, Costs five-block water mask, Collision2 (own call), the live grid, the bake | [Height and water](HeightAndWater.md) |

## 3. Changing masks, lighting and files

| You change | Also check | Page |
|---|---|---|
| how a blend mask is stored or flushed | the bake's size, the exporter's length checks, undo (arrays shared by reference), the `bg_` files | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| the bake output or settings | `RnmBakeGeneration`, the preview's array rebuild, the export's fallback to neutral lighting | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| a mask group rule | group inheritance of new pairs, the export warning, the preview's "first pair's group" | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| the shared material list | `SC2SharedMaterials.Sync`, undo, load, material deletion | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| a generated file | its export step, its dirty-flag expansion and default mask in the smart export, [Formats](../Formats/README.md) | [Save, import, export](SaveImportExport.md) |
| the Lua writers | the passthrough of the original text, the vanilla ordering facts, the lobby and army consistency | [Objects and scenario](ObjectsAndScenario.md) |

## 4. Changing undo, input and the UI

| You change | Also check | Page |
|---|---|---|
| anything that needs undo | which mechanism owns it; add it to `Capture` **and** `Apply`, or give it a candidate in **both** the undo and redo dispatchers | [Undo](UndoAcrossSystems.md) |
| a reference-unit action | it records on the terrain stack but the snapshot has no reference units (see the page) | [Undo](UndoAcrossSystems.md) |
| a modal, overlay or dialog | `AnyModalOpen`, the overlay draw order, the player's runtime replacement | [Input and overlays](InputAndOverlays.md) |
| the order of viewport handlers | Measure's consumption, the passive handlers that must always run, the hotkey handler's position | [Input and overlays](InputAndOverlays.md) |
| a hotkey | the action table, any gesture on the same key, text-field focus | [Input and overlays](InputAndOverlays.md) |
| a long job | `SC2BusyOverlay.RunInBackground`, the main-thread and worker split | [Save, import, export](SaveImportExport.md) |
| a new shell | its creation point (before the first load if it must see `DataChanged`), its events from the host, its timestamp for undo, its input handler, its place in the per-frame synchronisation | [Mechanisms](Mechanisms.md) |
| the per-frame synchronisation of the terrain shell | the order of water, sky, shared materials, background, imported environment, height grid warm-up | [Mechanisms](Mechanisms.md) |

## 5. Changing the player-versus-editor split

| You change | Also check | Page |
|---|---|---|
| anything in the save path | it exists **only in a player** (`SC2RuntimeTerrainPersistence`); the editor saves an asset | [Save, import, export](SaveImportExport.md) |
| a draw call that uses runtime-only materials | the graphics setting that keeps instancing variants; check a player build | [Project and build settings](../Systems/ProjectAndBuildSettings.md) |
| a shader that is loaded by name | it lives under `Resources/` and is loaded with `Resources.Load<Shader>` | [Shaders](../Systems/Shaders.md) |

## 6. How to use this page when reviewing

1. List the files you touched and find their systems ([Data ownership](DataOwnership.md) and the system documents).
2. For each row above that matches, open its page and read **Contract** or **Traps**.
3. For each check, either show where the change handles it or say why it does not apply (in the commit message, per [Editing](../Process/Editing.md)).
4. If a dependency you found is not in any row, add it.
