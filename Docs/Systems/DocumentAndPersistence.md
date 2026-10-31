# Document, commands, undo, persistence and backups

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1: a wrong change here loses
saved maps. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

This system holds a map as data, changes it through undoable commands where a domain has them, and writes it to and reads it from disk.
Included: the document model and its sub-documents, the command history, the blob store, the manifest serializer with its migration chain,
the bridges between live data and document, the save folder layout, saving, loading, autosave backups, restore, duplicate and delete of a map.

Not included: what a domain edit means (each domain's system), writing the game's `.bdf` files (the [export pipeline](ExportPipeline.md)), and the
editing of live data by brushes ([terrain](TerrainDataAndRuntime.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Document and domains | `Scripts/MapDocument/Domain/SC2MapDocument.cs`; `Domain/{Terrain,Hfield,Costs,Collision2,Environment,Objects,Scenario,Decals,Mission}/` (a `SC2*Document`, a `*LegacyBridge`, validators and `SC2Set*Command` classes per domain) |
| Session | `MapDocument/Session/SC2MapSession.cs` (document, `History`, `BlobStore`, `PendingDirtyFlags`, `MarkDirty`), `SC2SelectionState`, `SC2WorkspaceState`, `SC2OperationState`, `SC2SceneProjection` (the last two and the selection are seams not yet wired) |
| Commands | `MapDocument/Commands/ISC2DocumentCommand.cs`, `SC2CommandHistory.cs`, `SC2CompositeCommand.cs`, `SC2ShellUndoCoordinator.cs` |
| Change tracking | `MapDocument/Changes/SC2DocumentChangeSet.cs`, `Domain/SC2DerivedState.cs` (Dirty versus Stale) |
| Serialization | `MapDocument/Storage/SC2MapDocumentSerializer.cs` (`Serialize`, `Deserialize` with `ISC2DocumentMigration`), `SC2Json.cs` |
| Blobs | `Storage/ISC2BlobStore.cs`, `SC2DirectoryBlobStore.cs`, `SC2LayeredBlobStore.cs`, `SC2BlobStoreGarbageCollector.cs`, `SC2MapshareTextures.cs` |
| Bridges | `Domain/SC2MapDocumentToLegacyBridge(.ScriptableObject).cs` (document to live), `Domain/*/SC2*LegacyBridge(.ScriptableObject).cs` (live to document) |
| Disk layout | `Scripts/Core/SC2SaveLayout.cs` (pure paths and load order), `Scripts/Unified/SC2SaveStorage.cs` (folders, session blob store) |
| Saving in a player | `Scripts/Unified/SC2RuntimeTerrainPersistence.cs` (compiled only for `!UNITY_EDITOR`), `SC2SaveSettings.cs` (autosave, backups, JSON copy) |

## 3. Data

**On disk.** One map is one folder in `Application.persistentDataPath/SC2Terrains`:

```
<Map>.sc2mapshare/
  <Map>.sc2terrain.json        optional legacy JSON copy of the whole map (SC2SaveSettings.WriteLegacyJson, on by default)
  <Map>.sc2mapdoc/manifest.json the document without its large data
  Blobs/<hash[0:2]>/<hash>.bin the large data (grids, masks, textures), content-addressed
Backups/<Map>/<yyyy-MM-dd_HH-mm-ss>/   self-contained snapshots, laid out like the map folder
```

The map folder is the one source of truth. The same files directly in the save directory (an earlier layout with one shared `Blobs` pool) are
still read for a map that has no folder yet and are never written. `SC2SaveLayout.SafeName` turns a map name into a file name (invalid characters
become `_`, an empty name becomes `Untitled`).

**In memory.** `SC2MapDocument` has `FormatVersion` (`CurrentFormatVersion = 1`), an increasing `Revision`, and the domain sub-documents. A
`SC2BlobReference` in a sub-document names a blob by content hash. A session writes new blobs into a cache in the temporary folder
(`SC2LayeredBlobStore`: reads also look into the folders of maps opened or saved in this session); a save copies what the manifest needs into the
map's own folder.

**Settings.** `SC2SaveSettings` (PlayerPrefs): autosave on/off and interval, the number of backups kept per map (0 turns backups off), and the JSON copy.

## 4. Flows

**Save** (`SC2TerrainShell.SaveState` then `SC2RuntimeTerrainPersistence.Save`):
1. The GPU state is read back into the live data (`FlushActiveBlendGroup` of the core and, when pending, of the background; pair map sync).
2. The legacy JSON is built when enabled; the document is built from live data and the session (`BuildDocumentSnapshot`: every `*LegacyBridge.BuildFromLegacy`,
   plus what only the session's document holds: decals, imported terrain objects and masks, lobby positions, the mission graph).
3. The manifest is serialized; the blobs it references are copied into the map folder; the JSON is written atomically; **the manifest is written last, atomically**,
   so the folder always holds a manifest whose blobs are all present.
4. Afterwards, tidy-up that must not fail the save: blobs no longer referenced are removed from the folder, a backup snapshot is created and pruned
   to `MaxBackupsPerMap`, the saved blobs are dropped from the working cache.

**Autosave**: `CreateAutosaveBackup`/`BeginAutosaveBackup` writes only a backup snapshot from the in-memory data and never touches the map folder, so the
last explicit save stays recoverable. It runs on a worker thread behind the busy overlay after the main thread has read what only it may read.

**Load** (`TryLoad`): the order is the map's own manifest, its JSON copy, the earlier layout's manifest, its JSON (`SC2SaveLayout.LoadOrder`). A source that
fails to read (missing blob, corrupt file, a `FormatVersion` with no migration path) falls through to the next. `Deserialize` applies the migration chain
on the raw JSON tree until it reaches `CurrentFormatVersion`; then `ApplyToLegacy` rebuilds a live `SC2TerrainData`, the background included.

**Restore Backup** (File menu): lists the maps that have backups (also a map whose own save is gone), then that map's backups; confirming copies the chosen
snapshot's blobs, JSON and manifest back into the map folder. The open map is then reloaded; another map is only put back on disk.

**Delete / Duplicate**: `Delete` removes the map's folder and its earlier-layout files; `Duplicate` writes a new folder with the identity fields that key a save to
its name rewritten (`Identity.MapStem`, a new id).

**Commands and undo**: a shell executes an `ISC2DocumentCommand` on `Session.History` (`Do` returns the `SC2DocumentChangeSet` that `RaiseChanged` publishes; `Undo` the
reverse); `SC2CompositeCommand` makes several commands one step; `SC2ShellUndoCoordinator` joins command steps with snapshot steps per shell. See [Architecture](../Architecture.md), section 5.

## 5. Invariants

- **Manifest last.** The manifest is written after its blobs; a reader never sees a manifest whose blobs are missing.
- **An explicit Save is the only thing that changes the map folder.** Autosave only adds backups.
- **Reading falls through, writing does not.** A bad source never makes a map "not load" if another source holds it; a failure while writing throws.
- **Content addressing.** A blob is named by the hash of its bytes; writing the same bytes twice is a no-op; `SC2BlobStoreGarbageCollector` marks from every manifest it is given, so a collection over one map's manifest only ever runs on that map's own folder store (never the shared cache or another map).
- **Selection and graph references are by stable id, not by list index** (`SC2SelectionState`, `SC2ObjectGraphRefs`): an index changes when a list is edited or sorted.
- **A store cached on a folder that no longer exists is dropped** (`SC2SaveStorage.StoreFor`), so a map deleted in this session can be saved or restored again.
- **Dirty versus Stale.** Dirty means the user changed a domain directly; Stale means an upstream source of a derived domain changed and it was not regenerated (`SC2DerivedState`).
- **The live data stays authoritative for brush work.** Terrain sculpting and blend painting do not go through commands; they call `Session.MarkDirty` themselves.

## 6. Editor and build differences

- Saving, loading, backups, listing, delete, duplicate and restore exist only in a player (`SC2RuntimeTerrainPersistence` is `#if !UNITY_EDITOR`). In the editor, saving goes through the asset database and the Restore Backup menu only shows a notice.
- `SC2SaveStorage.SaveDirectory` is `persistentDataPath/SC2Terrains`; with the project's company and product (`BDFTM`, `SupCom2 map editor`) that is the user's real save folder. A player run from a scratch project with another company name writes elsewhere.
- Everything in `Scripts/MapDocument` that has no Unity dependency is covered by the standalone tests; the `.ScriptableObject.cs` halves of the bridges need Unity types and are not.

## 7. Extension points and how to change it

- **A new saved field**: the steps of [Editing](../Process/Editing.md), section 4 (data class, serializer with a default, both bridges, undo snapshot, JSON copy).
- **A format change that older readers would misread**: raise `CurrentFormatVersion` and add an `ISC2DocumentMigration` that reshapes the raw JSON tree; the migration runs before the typed model is built.
- **A new domain**: a `SC2*Document`, a `*LegacyBridge` pair, a validator, commands for what is edited, a dirty/stale entry, serializer read and write, and the shell wiring.
- **A new command**: implement `ISC2DocumentCommand` (`Do` and `Undo` both return a change set) and execute it with `Session.History.Execute`; register the shell's undo channel with the coordinator.

## 8. Editing, verification and tests

**How to edit it safely.** Never change the order manifest-last. Never write into the map folder from autosave. When a bridge gains a field, change both directions and the serializer in the same commit. Keep reading tolerant: a missing field takes a default.

**Tests today** (`dotnet test`, standalone): `MapDocumentSerializerTests`, `MapDocumentToLegacyBridgeTests`, `TerrainDocumentBridgeTests`, `CostsDocumentBridgeTests`, `ObjectsDocumentBridgeTests`, `EnvironmentDocumentBridgeTests`, `ScenarioDocumentBridgeTests`, `DecalsDocumentBridgeTests`, `CollisionDocumentBridgeTests`, `HfieldDocumentBridgeTests`, `CommandHistoryTests`, `CompositeCommandTests`, `ShellUndoCoordinatorTests`, `SessionTests`, `BlobStoreTests`, `BlobStoreGarbageCollectorTests`, `SaveLayoutTests`, `MapshareTexturesTests`, `SafePathTests`, `TerrainCommandTests`, `TerrainExpandedCommandTests`, `CostsCommandTests`, `ObjectsCommandTests`, `ObjectsExpandedCommandTests`, `EnvironmentCommandTests`, `EnvironmentHeaderCommandTests`, `ScenarioCommandTests`, `Collision2CommandTests`, `DecalsCommandTests`, `HfieldCommandTests`. EditMode: none for this system.

**Tests a change must add.**
- A saved field: serialize then deserialize gives the same value; a manifest without the field loads with the default; the bridge pair round-trips it; undo restores it.
- A migration: a manifest of the old version migrates to the new one and loads.
- A command: `Do` then `Undo` gives the original document; both return the right change sets.
- Anything in the save folder logic (`SC2RuntimeTerrainPersistence`, `SC2SaveStorage`): a **player scenario** with a throwaway save folder (save, load, backup, restore, delete then restore), because this code does not compile in the editor. No such scenario is in the repository yet ([Verification](../Process/Verification.md)).

## 9. Known limitations and unverified behaviour

- `SC2RuntimeTerrainPersistence` has no automated test in the repository; its behaviour above is from reading the code and from the one-off player runs that confirmed restore after a deleted map.
- The selection, workspace and scene-projection classes are seams that nothing reads yet.
- Terrain brush work and blend painting bypass the command system and tell the session through `SC2MapSession.MarkDirty` on mouse-up. Smart export (not used by the UI) consults `PendingDirtyFlags` for Costs, Objects and ScriptLua and regenerates everything else ([smart export](SmartExport.md)).
- Which of the live-data fields the legacy JSON copy and the document each carry is not listed here; the JSON copy is a fallback and may lag the document.

## 10. Related documents and review

[Architecture](../Architecture.md), [Export pipeline](ExportPipeline.md), [Tests and release](TestsAndRelease.md), [Editing](../Process/Editing.md), [Testing](../Process/Testing.md).
Interactions with other systems: [Save, import, export](../Interactions/SaveImportExport.md), [Undo](../Interactions/UndoAcrossSystems.md), [Data ownership](../Interactions/DataOwnership.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
