# Coupling mechanisms: every event, delegate, counter and shared service

Status: first draft from searching the source for every subscription, invocation and assignment at commit `17863e4`; not yet reviewed by a second reader. The list is complete for the Unified and TerrainEditor code as found by search; a new mechanism added later must be added here.

## 1. How shells are built and wired

The host creates each shell lazily (`EnsureTerrainShell`, `EnsureCostsShell`, ...). Shells receive what they need in the constructor and through assigned delegates; the host then connects the shells to each other with events. No shell is given another shell, with three exceptions that are really host callbacks: the lobby panel, the mission panel and the Info.bdf panel receive `Func`s that return the terrain shell, the session and the scenario shell.

| Shell | Built with | Created by |
|---|---|---|
| Terrain (`SC2TerrainShell`, a `ScriptableObject`) | the session; delegates listed in section 3 | `EnsureTerrainShell` |
| Costs | the terrain shell; `Session`, `RepaintHost` | `EnsureCostsShell` |
| Collision2 | the terrain shell and the session | the host |
| Environment | the terrain shell and the session | `EnsureEnvironmentShell` |
| Scenario | the terrain shell and the session; `OpenLobbySpawnEditor`, `OpenMissionEditor` | `EnsureScenarioShell` |
| Export | the terrain shell and the session | `EnsureExportShell` |
| Objects | the terrain shell; `Session`, `RepaintHost`, `ReportStatus`, `RequestSubTab`, `RequestCameraFocus`, `GetCameraFocusPoint`, `GetImportedTerrainColliders` | `EnsureObjectsShell` |
| Reference units | the terrain shell; `RepaintHost`, `ReportStatus`, `GetWaterSurface` | `EnsureReferenceUnitsShell` |
| Decals | the session and the terrain shell | the host (`SC2GameViewEditorUI.Decals.cs`) |
| Import panel | the session and a function that returns the terrain data; `OnImportComplete` | the host |

`EnsureTerrainShell` also calls `EnsureObjectsShell` **before** it loads the first data, so that the Objects subscriptions to `DataChanged` exist in time to see the startup load even if the user never opens the Objects tab. A new shell that must react to a map load has to be created at that point, not lazily by its own tab.

## 2. Events raised by the terrain shell

| Event | Raised when | Raised at | Subscribers (wired in the host) |
|---|---|---|---|
| `DataChanged(SC2TerrainData)` | a map is loaded, created or switched (`SetData`) | `SC2TerrainShell.cs` line 724 | `ApplyReferenceOverlayState` (re-pushes the reference overlay into the new chunk material); `Costs.SyncDocumentFromLive`; `Objects.EnsureObjectsDocumentSynced`; `Objects.EnsureAssetsIndexed` (starts the blueprint index at load, not at the first visit of the tab); `ReferenceUnits.OnDataChanged` (drops selection and the intel cache of the previous map); `Decals.InvalidatePreviewCache` |
| `HeightBrushApplied(center, radius)` | a sculpt dab changed heights | `SC2GeometryMode` (four places in the sculpt handlers) | `Objects.ReprojectAnchoredObjectsInRadius` (anchored props, markers and units follow the ground); `Decals.ReprojectAlignedDecalsInRadius`; `Decals.MarkPreviewDirtyInRadius`; `Terrain.SyncBackgroundRingIfNearCoreBoundary` (subscribed by the terrain shell itself) |
| `ImportedTerrainMoved(bounds)` | an imported terrain object was moved (raised twice: its old and its new region) | `SC2TerrainShell.cs` lines 787 and 788 | `Objects.ReprojectAnchoredObjectsInBounds`; `Decals.ReprojectDecalsInBounds` |

`Document.Changed` is the document's own event: its only subscriber is `SC2MapSession`, which turns each change set into `PendingDirtyFlags` and the Dirty or Stale state of the derived domains. Other code reads `Document.Revision` instead of subscribing (the decals preview does).

## 3. Delegates the host assigns to the terrain shell

The terrain shell needs things only the host owns, chiefly the imported terrain objects (the list lives in the host, not in the shell). It gets them through these properties:

| Delegate | What the host supplies | Used for |
|---|---|---|
| `RepaintHost` | repaint the game view | every edit |
| `OnPreviewModeChanged(mode)` | `ApplyPreviewModeToImportedTerrains` | imported objects follow Textured, RNM, Mats, Pairs, Slope or Height |
| `DrawImportedTerrainsContent(width)` | the Imported tab's left panel | drawn inside the Terrain panel |
| `PreSaveSync` | `SyncImportedTerrainsToDocument` | imported objects are copied into the document before every save |
| `GetImportedTerrainsForExport` | the list of `SC2ImportedTerrainExportData` | the export, the live height grid, the per-tick environment refresh of imported materials |
| `GetImportedRnmTargets`, `SetImportedRnmTarget`, `RefreshImportedRnmMasks` | the imported layers' RNM mask assignments | Lighting Bake > Imported Objects |
| `GetImportedTerrainColliders` | colliders of the imported meshes | raycasts of Costs, Objects and Decals against the real imported surface |
| `GetDecalsOverlayVisible`, `SetDecalsOverlayVisible` | the decals overlay toggle | the Visibility menu |

The Objects shell receives its own set: `GetCameraFocusPoint` raycasts the viewport centre (imported terrain first, then the live height field, then a point 40 units in front of the camera); `RequestCameraFocus` and `RequestSubTab` let the shell move the camera and switch a sub-tab it does not own. The reference units shell gets `GetWaterSurface` (the Y of the water surface or null when water is off) and receives a raycast delegate as a call parameter.

## 4. Counters that others poll

| Counter | Owner | Changed by | Read by |
|---|---|---|---|
| `SC2TerrainShell.TerrainGeometryVersion` (static) | terrain shell | `MarkDirtyAndRepaint(geometryChanged: true)`, `BumpTerrainGeometryVersion` (three finalize points of `SC2GeometryMode`), the imported-terrain move | the decals preview, to rebuild clipped decals against the changed ground. A pair assignment that does not touch water uses `MarkDirtyAndRepaint(false)` so it does not force the decals' height grid rebuild (about 300 ms on a large map) |
| live height grid dirty flag | `SC2LiveHeightGrid` | `InvalidateLiveHeightGrid()`, called after the sculpt and pair-paint finalize points, a paint that involves water pairs, **the start of every Costs paint stroke** (the only hook that notices an imported terrain moved with its transform gizmo), props in the heightmap (next row), and in the host wherever the imported terrain objects or the map change (restore from the document, undo of an imported snapshot, the end of a move, rotate or scale gesture, adding a terrain.bdf, deleting an imported material layer, the import-only toggle, the imported panel, loading a map name) | every consumer of ground height (Costs painting and generation, Objects placement, Decals, the buildable overlay, the HUD); the grid rebuilds **debounced by 0.25 s**, and consumers keep reading the previous grid meanwhile |
| props-in-heightmap signature | Objects shell (`RefreshHeightmapPropsSignature`) | `MarkObjectsDirty`, comparing a hash of every prop with `IncludeInHeightmap` (id, position, rotation, scale, blueprint) | when it changes the Objects shell invalidates the live height grid, so Costs generation and placement see the new ground |
| `Document.Revision` | `SC2MapDocument` | every `RaiseChanged` (every command) | the decals preview (`_renderedRevision`) |
| `RnmBakeGeneration` | `SC2TerrainData` | the end of a core bake (`SC2RnmBakeService`) | the terrain shell (`_rnmPreviewGeneration`, to know whether the RNM preview of a data object is stale and `SetAllPairMaterials`) and the runtime's mask cache key |
| `SC2TextureCatalog.Version` | the catalog | add, replace, remove, reload | the browser's query cache |
| `SC2Theme.RenderVersion`, `SC2Theme.Applied`, `SC2UiScale.Changed` | the theme and scale services | a colour or scale change | `SC2UnifiedSkin` (rebuilds its textures), `SC2RuntimeWindow` (title bar colours), every style that bakes a colour |
| `SC2BackgroundRuntimeSync.MarkDirty` / `ConsumePending` (static flag) | the background | undo of terrain state, `SC2SharedMaterials.Sync` finding a change, any code that cannot reach the shell | the terrain shell's `Update`, which then calls `RefreshBackgroundRuntime` |
| `SC2Hotkeys.Changed` | hotkeys | a rebind | declared; no subscriber was found by search (the panel and tooltips read the bindings directly) |

## 5. The terrain shell's per-frame synchronisation

`SC2TerrainShell.Update` is where the preview is kept consistent with the data every frame. In order:

1. `SC2WaterPreview.Ensure(data, session, overlayVisible, importedTerrains)`: creates, updates or destroys the water plane from the environment header; skips it when an imported terrain already carries water.
2. `SC2SkyPreview.Ensure(data, session)`.
3. `SC2SharedMaterials.Sync(data)` and, if it changed anything, `SC2BackgroundRuntimeSync.MarkDirty()`.
4. The background runtime: `RefreshBackgroundRuntime()` when pending, else `EnsureBackgroundRuntime()`; then the preview mode of the background chunks follows the core's.
5. For every imported terrain: `RefreshImportedTerrainEnvironment` with a quiet `BuildInfoModel` (sun, shadow, fog, environment map of the Environment tab applied to the imported materials).
6. The pending water-depth preview, once after opening a map with auto-generate and water on.
7. The height-grid and wireframe overlays pushed.
8. The queued preview build (debounced), which also warms the live height grid with the same delay, so the first paint click after an edit does not pay the rebuild; then the dirty preview refresh.

The consequence for anyone adding preview behaviour: **a system whose preview depends on another system's data should be refreshed from here (or from its own tick), not from the editing code of the other system.**

## 6. Static services used across systems

| Service | Used by | What to know |
|---|---|---|
| `SC2BusyOverlay` | autosave backup, export, bake, anything long | one job at a time; swallows input while active; see [Input and overlays](InputAndOverlays.md) |
| `SC2Hotkeys` | every shell's input | looked up by action id; see [Editor UI](../Systems/EditorUi.md) |
| `SC2TextureBrowser` and `SC2TerrainShell.DrawTexturePicker`, `DrawTexturePickerWithPath`, `DrawTextureThumb` | Materials, Decals atlases, the Environment texture fields | the picker widgets live in the terrain shell and are static, which is how Decals and Environment reach them; see [Textures](TexturesAcrossSystems.md) |
| `SC2Log` | everything | one file; flushed per line |
| game archive reader (`SC2GameInstallSettings.TryGetReader`, `SC2GameArchiveRegistry`) | Textures, Game assets, Import, Export | one shared composite reader |
| `SC2MapPreviewCapture` | Export | static state; an export waits while a capture is pending |
| `SC2WaterPreview` | the terrain shell and the exporter | the exporter invalidates its material after writing the generated depth texture |
| `SC2RuntimeConfirm`, `SC2RuntimeDropdown`, `SC2RuntimeFileDialog` | any shell | the player's replacements for editor dialogs; see [Editor vs build](../Systems/EditorVsBuild.md) |

## 7. Rules

- **A system that needs to follow another system's edit subscribes in the host or polls a counter.** It does not import the other shell.
- **An event raised per dab (`HeightBrushApplied`) must stay cheap in every subscriber**; the expensive work (the height grid, the decals' clipped geometry) is deferred by a flag or a debounce.
- **A polled counter has exactly one owner**, and its bump sites are the contract; adding a way to change the ground without bumping `TerrainGeometryVersion` and invalidating the live height grid leaves the decals and the placement stale.
- **A subscription added in the host must also be released or be harmless on a map switch**: `DataChanged` subscribers must reset their per-map state, as the reference units shell does.
