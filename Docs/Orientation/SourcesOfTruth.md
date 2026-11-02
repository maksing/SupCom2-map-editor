# Sources of truth, and the route a new field must take

Status: first draft from the save, load, undo and export code read at commit `3077a7d`; not yet reviewed by a second reader. Read [Data ownership](../Interactions/DataOwnership.md) and [Save, import, export](../Interactions/SaveImportExport.md) first; this page turns them into a procedure.

## 1. The problem in one paragraph

The editor keeps the map in **four places at once**: the **GPU** (render textures and chunk meshes that brushes edit), the **live data** (`SC2TerrainData`, a `ScriptableObject` the shells edit), the **session document** (`SC2MapDocument`, pure data with commands and undo) and the **local state of each shell** (selection, drafts, caches, the host's imported terrain list). Which one is **authoritative** depends on the domain and on the moment. They are brought together by hand at fixed points: the bridges, `SyncDataForSave`, `SyncPairMapToCPU`, `FlushActiveBlendGroup`, `BuildDocumentSnapshot`, `AdoptLoadedDocument`, and the manual dirty signals.
That is the project's main systemic risk ([Known risks](KnownRisks.md)): **a new field is easy to add to the interface and forget in one of the routes**, and the loss shows up later, on a reload, an undo or an export.

## 2. Who is authoritative when

| Data | While editing | At save | At load | At export |
|---|---|---|---|---|
| Heights | the GPU runtime during a stroke; `Data.Heights` after mouse-up; the live height grid for consumers (debounced) | `SyncDataForSave` reads the runtime into the data | data into the runtime | the exporter builds the final model from the data and projects everything derived from it |
| Pair assignment | the GPU pair map during a stroke; `TrianglePairIndex` after the readback | `SyncPairMapToCPU` | data into the runtime | the data |
| Blend mask | the layer render textures and the composite in the runtime | `FlushActiveBlendGroup` writes the composite into `RnmMaskBytes` of every pair of the group | the bytes into the runtime | `RnmMaskBytes` (never a layer) |
| Materials and pairs | the live lists | the live lists, through the bridge | | the live lists |
| Baked lighting | the arrays on the pairs | the arrays | | the arrays, if their length matches the mask |
| Environment | **the document's working header**, current every frame | committed into `Data.EnvironmentHeader` by `SyncDataForSave` | data and document both set | the working header is preferred |
| Costs blocks, terrain types | the live data; a command syncs the document after each stroke | the live data through the bridge | | the live data |
| Objects (`Obj*`) | the live lists | the live lists through the bridge | | the live lists |
| Scenario fields, lobby positions, mission, decals | **the document only** (the live data holds just the name and the playable area) | the session document is **carried across** into the new snapshot | `AdoptLoadedDocument` carries it back | the document |
| Imported terrain objects | **the host's list** | copied into the document by `PreSaveSync`, then carried across | restored by the host from the document | the host's list through a delegate |
| Water, bake settings, symmetry, background settings | the live data | the live data | | the live data |

The consequence for design: **where two copies exist, name the direction of every synchronisation and its trigger.** If you cannot, the data has two sources of truth and will drift.

## 3. The field route

For a new **saved** field `F` of domain `D`, walk the list in order. Each step names the file to open and what breaks silently if it is skipped.

| # | Step | Where | If skipped |
|---|---|---|---|
| 1 | Add `F` to the live data (or to the shell's own class) with a default | `SC2TerrainData` and its group | the field does not exist |
| 2 | Give it **one owner** that writes it while editing; list it in the ownership table | the system's shell; [Data ownership](../Interactions/DataOwnership.md) | two writers, drift |
| 3 | Raise the right **signal** after a write: a command (`DirectDirtyFlags`), or `Session.MarkDirty(flag)`, or `MarkObjectsDirty()`; for the ground also `InvalidateLiveHeightGrid()`, `TerrainGeometryVersion`, `HeightBrushApplied` | the writer | smart export passes a changed file through; consumers use stale ground |
| 4 | **Undo**: add `F` to the terrain snapshot (`Capture` **and** `Apply`, and the clone list for pairs) or make the edit a command, or add it to the owning shell's snapshot | `SC2TerrainUndoStack`, the shell's coordinator | Ctrl+Z does not restore it or restores part of it |
| 5 | Add `F` to the **document class** with a default | `MapDocument/Domain/<D>/` | the package cannot hold it |
| 6 | **Bridge both ways**: `BuildFromLegacy` (live to document) and `ApplyToLegacy` / the `ScriptableObject` partial (document to live) | `SC2<D>LegacyBridge`, `SC2MapDocumentToLegacyBridge` | the field is saved but not loaded, or the reverse |
| 7 | **Serializer**: a read and a write in the domain's section, and a **default for files saved before the field existed**; a migration if the meaning changed | `SC2MapDocumentSerializer` | an old save fails to load or loads garbage |
| 8 | **The save snapshot**: if the field is document-only or not produced by `BuildFromLegacy`, copy it across in `BuildDocumentSnapshot` | `SC2RuntimeTerrainPersistence` | it is silently dropped at the next save (this lost imported terrains and authored decals before) |
| 9 | **The load**: if document-only, adopt it in `AdoptLoadedDocument` | `SC2TerrainShell` | it is saved but not restored |
| 10 | **GPU-resident?** Pull it into the data before a save and an export | `SyncDataForSave` | a save or export sees the state before the last stroke |
| 11 | **Export**: read it in `BuildExportArtifacts` (and write it in the right format); if it is a new file, add its kind, mask and dependency to the smart export | `SC2TerrainExporter`, `Runtime/SmartExport` | the field never reaches the game |
| 12 | **Import**: set it (both header copies for environment fields) in the right orchestrator stage, in the right order | `SC2MapImportOrchestrator` | an imported map has the default, and checks that read the stale copy misbehave |
| 13 | **Validation**: a rule and a code if it can be wrong | `Domain/<D>/SC2<D>Validator` | |
| 14 | **Tests**: a bridge round trip, a serializer round trip **with an old-format file**, undo and redo, and for export the file read back | `Tests~/StandaloneCharacterization` | the route is untested |
| 15 | **Documents**: the ownership row, the [Change impact](../Interactions/ChangeImpact.md) row, the system document | `Docs/` | the next person repeats the search |

For **non-saved** state (a cache, a draft, a selection), steps 5 to 9 do not apply, but step 3 (what must follow a change) and step 4 (does Undo restore it) still do.

## 4. The silent failures, by the step that was missed

| Missed step | What the user sees | Seen before |
|---|---|---|
| 3, no signal | the smart export keeps the old bytes; Costs and placement act on the old ground; decals stay on the old surface | the sculpt stroke once raised no signal to the height grid |
| 4, not in `Apply` | Undo leaves the field changed, or a part of a group; a step is consumed with no visible effect | the RNM fields were once missing from the pair clone list; reference units are missing from the snapshot ([Undo](../Interactions/UndoAcrossSystems.md)) |
| 6, one direction only | the value is lost on reopening | |
| 7, no default | maps saved before the change do not open | |
| 8, not carried across | the value disappears at the next save | imported terrains, authored decals, lobby positions, Collision2 meshes, preview pictures |
| 9, not adopted | the value is in the file and not in the editor | |
| 10, not pulled from the GPU | the save and the export are one stroke behind | the environment header was once committed only by one tab's own save button |
| 12, one header copy | water-gated checks read "no water" on an imported map that has water | |

## 5. Rules

- **Never read the document where the live data is the truth during editing, and the reverse.** The two meet at save and load.
- **A new document-only domain needs a save-and-load pair** (steps 8 and 9) in the same commit; write the round-trip test first.
- **A `Core` function never receives the session**; pass the data it needs (the working header bytes, for example), because `Core` cannot reference the session assembly.
- **If a field has no owner, no signal and no undo story, it is not finished**, even if the interface works.
