# Save, load, autosave, import and export: what each reads and writes in every system

Status: first draft from reading `SC2TerrainShell`, `SC2RuntimeTerrainPersistence`, `SC2MapImportOrchestrator`, `SC2ExportPanel` and the host at commit `17863e4`; not yet reviewed by a second reader. Read [Document and persistence](../Systems/DocumentAndPersistence.md), [Import pipeline](../Systems/ImportPipeline.md) and [Export pipeline](../Systems/ExportPipeline.md) first.

These five operations walk across all the systems, so they are where a missed hand-off loses data. The main fact: **the live data and the session document are two copies that are brought together only at these moments**, and some data exists in only one of them.

## 1. Where data lives (the reason these operations are delicate)

| Lives in | Examples |
|---|---|
| **Live data only** (`SC2TerrainData`; saved by `BuildFromLegacy`) | heights, pair map, materials, pairs, costs blocks, Obj* lists, environment header, water settings |
| **The session document only** (the saved package must **carry it across** by hand) | the lobby start positions, the mission graph, the Decals document, the imported-layer RNM masks, the imported terrain entries, the working environment header between saves |
| **GPU state** (pulled into the data before a save) | heights during a stroke, the pair map, the active blend group's composite, the background's pending composite |
| **The host** | the list of imported terrain objects (reached through the delegates of [Mechanisms](Mechanisms.md)) |

## 2. Save (`SaveState(writeAssets)`, from Ctrl+S, the File menu, and the first half of an export)

1. `SC2SharedMaterials.Sync(data)`: what is saved must not depend on a frame having run since the last undo or deleted material.
2. `SyncDataForSave`, in this order: `FlushOutlineJob` (a pending pair-outline worker must finish first); **`PreSaveSync`** (the host copies the imported terrain objects into the session document, in the editor and the player alike); then **GPU to CPU**: the runtime's heights (`GetHeightsCopy`) and the pair map are read into the data, `FlushActiveBlendGroup` is called for the core and, if its composite is pending (it is flushed lazily after an outline update), for the background; **the document's working environment header, the five texture paths and the per-slot source tags and force-copy flags are copied into the data** (this used to happen only on the Environment tab's own save button, so a save from the Terrain tab wrote a stale header); then `EnsureSerializableState`.
3. `EditorUtility.SetDirty(data)`.
4. **In the Unity Editor**: `AssetDatabase.SaveAssets()` and `Refresh()`; that is all. **In a player**: `SC2RuntimeTerrainPersistence.Save(data, session)`. The terrain shell's `Dispose` also calls `SaveTerrainState(false)`, so closing the shell runs the same synchronisation.

**The player's save** (`BuildDocumentSnapshot`, only in a player because the class is `#if !UNITY_EDITOR`) builds a **fresh** `SC2MapDocument` from the live data with each domain's `BuildFromLegacy`, and then **copies across what the legacy data does not hold**: the imported terrain entries, the imported-layer RNM masks, the lobby start positions, the mission graph, the point cloud and imported-RNM textures as blobs, the preview pictures, the Collision2 preview and custom meshes and settings, and the Decals document (the session's, when it `HasAuthoredChanges`, else a reconstruction from the original bytes). It also calls `SC2MapshareTextureCapture.Capture` so textures that exist only on this machine travel with the map.
It then writes the manifest last and atomically, with the blobs and, optionally, the legacy JSON copy, and rotates backups ([Document and persistence](../Systems/DocumentAndPersistence.md)).

**Consequence: any new document-only data must be added to the carry-across in `BuildDocumentSnapshot`, or the next save silently drops it.** This has happened before (imported terrains vanished on reload; Decals authored in a session were discarded; the lobby positions, the Collision2 meshes and the preview pictures each needed their own line).

## 3. Load (`LoadFromDisk`, then `AdoptLoadedDocument`)

1. The package (or the legacy JSON fallback) is read; `SetData` adopts the legacy data and **rebuilds the session from it**, then raises **`DataChanged`** (whose subscribers re-sync Costs, Objects, the reference units, the decals preview and the reference overlay).
2. `AdoptLoadedDocument` carries what only the document holds back into the **live session**: the lobby positions (re-attached to the army positions found in the loaded markers, `AdoptBases`), the imported-layer RNM masks, the Decals document (`CopyFrom`, followed by a `Load Terrain` change set with `DirectDirtyFlags = Decals`) and the mission graph (`CopyFrom`, followed by `Load Mission` with `ScriptLua`).
3. The host restores the imported terrain objects from the document (`RestoreImportedTerrainsFromDocument(loadedDocument.Terrain.ImportedEntries)`; a new map or a load without a document calls it with `null` to clear them), which also invalidates the live height grid.
4. **Save and load must be symmetric**: whatever step 2 of the save carried across, step 2 of the load must carry back.

## 4. Autosave

`TickAutosave` (host, per the preferences) calls `BeginAutosaveBackup`. **In the Unity Editor it does nothing** (`AutosaveBackupsSupported` is false there; backups exist only for the player's map folders). In a player: the busy plate is shown first, the main-thread part runs `SyncDataForSave` and prepares the backup, the worker writes it, and the main thread reports the outcome. **Autosave never touches the canonical save** (the `.sc2mapshare` folder); before this split, autosave called the manual save and the "last saved state" was whatever autosave last caught.

## 5. Import (`SC2MapImportOrchestrator.ImportSelected`)

Stages, in order, each wrapped independently so one broken file does not hide the others (the summary says "n of m stages succeeded"):

| # | Stage | Writes | Cross-system effect |
|---|---|---|---|
| 1 | `terrain.bdf` | an imported terrain object (a mesh, not in `SC2TerrainData`) | the host registers it in its own list; the live height grid is invalidated |
| 2 | Objects (`mapobjs`, `_save.lua`, `_scenario.lua`) | the Obj* lists and the original Lua texts | later export patches these texts |
| 3 | `info.bdf` | `data.EnvironmentHeader` and the five texture paths **and `session.Document.Environment.WorkingHeader`** | writing only the first left the working header as the all-zero header of the freshly created map, so every water-gated check (prop placement over water, Auto Path Node) read `WaterEnabled = 0` on an imported map that has water |
| 4 | `hfield.bdf`, terrain types only | `CostsGridSize` and `TerrainTypes` (heights are re-derived from geometry, never imported) | runs **before** Costs, which sizes its grid off the same cell count |
| 5 | `costs.bdf` | `CostsBlocks` and the original bytes as a blob (the source of the smart export's Costs passthrough) | |
| 6 | `collision2.bdf` | preview and reference only; the export always regenerates | |
| 7 | `decals.bdf` | the decals state (original kept as a blob) | |
| 8 | Scenario and Lua | scenario fields; `_save.lua` and `_script.lua` kept as passthrough blobs | |

`OnMapImportComplete` (host) then registers the imported terrain entry, selects the Terrain > Imported tab and applies the current preview mode to it. Textures are not extracted eagerly: the terrain importer pulls the files a material references from the still-open reader.

## 6. Export (`SC2ExportPanel.RunExport`)

1. Refuses to start while a minimap capture is pending or the busy plate is active.
2. Under the busy plate, **main thread**: **`SaveTerrainState(writeAssets: false)`** (the whole of step 2 of the save, so the export sees the same state as a save: GPU read back, working environment header committed), then the export folder, the data, the session and `GetImportedTerrainsForExport`.
3. **Worker**: the minimap is rendered fresh (`OnMainAsync` capture, then DXT1-encoded), then `SC2TerrainExporter.ExportCorrect(data, folder, importedTerrains, ..., session, asArchives, minimapDds)`; the heavy parts that need the main thread (textures, the game archive) go through `SC2BusyOverlay.OnMain`.
4. **Main thread again**: the result text is shown. On an exception the failure is described and shown.

**What the export reads from each system** (details in [Export pipeline](../Systems/ExportPipeline.md), [Height and water](HeightAndWater.md), [Objects and scenario](ObjectsAndScenario.md)): the live data and the **document's working header** (`BuildInfoModel` prefers it), the objects for three files, the **document** for decals, imported terrain entries, lobby positions and the mission graph, the environment's textures through the safety sweep, the baked lighting arrays from the pairs, and the `Costs` blocks.
**`ExportCorrect` does not call `session.MarkExported()`**; only the smart path (`ExportCorrectSmart`, not used by the UI) does, so in normal use the session's pending dirty flags accumulate for the whole session ([Smart export](../Systems/SmartExport.md)).

## 7. The busy plate is shared

Autosave backup, export and bake all use the single `SC2BusyOverlay`: one job at a time (`RunInBackground` returns false when the plate is owned), input swallowed while it runs. A job is split into a main-thread start (reads GPU data, textures, PlayerPrefs), a worker part and a main-thread finish; a worker that trips on a main-thread-only API is re-run on the main thread.

## 8. Change checklist

| If you add | Also do |
|---|---|
| document-only data | add it to the carry-across in `BuildDocumentSnapshot` **and** to `AdoptLoadedDocument`; round-trip test |
| a field of the environment header or textures | commit it in `SyncDataForSave`, set both header copies on import |
| an import stage | decide its position (does it depend on a previous stage's data?), wrap it in `RunStage`, say what it does when its file is missing |
| a thing the export needs from the GPU | make sure `SyncDataForSave` pulls it before the export reads it |
| a new long job | use `SC2BusyOverlay.RunInBackground`; keep Unity calls in the main-thread parts |
| a new generated file | its export step, its dirty-flag expansion ([Smart export](../Systems/SmartExport.md)), its entry in [Texture files](../Formats/TexturesAndMasks.md) or [Formats](../Formats/README.md) |

## 9. Tests and verification

`MapDocumentSerializerTests`, `MapDocumentToLegacyBridgeTests`, `MapImportServiceTests`, `SaveLayoutTests`, `BlobStoreTests`, `PreviewImagePersistenceTests`, `SmartExportTests`, `BusyGateTests`. The two-sided carry-across (save, load, compare every document-only domain) and the import-to-export chain have no test; the verification is a player run: import a map, edit each domain, save, reopen, export, and compare ([Verification](../Process/Verification.md)).

## 10. Unverified

Nothing was run. The player-only save path cannot be exercised in the Unity Editor at all.
