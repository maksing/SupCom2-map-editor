# Undo across systems

Status: first draft from reading the undo code and the dispatcher at commit `17863e4`; not yet reviewed by a second reader. Read [Document and persistence](../Systems/DocumentAndPersistence.md) (section on undo) first.

There are **three undo mechanisms** and **eight shells' worth of history**, joined by one global dispatcher. This page says how they are joined, what each one restores, and where the join is uneven.

## 1. The mechanisms

| Mechanism | What it is | Used by |
|---|---|---|
| **Snapshot stack** (`SC2TerrainUndoStack`) | a full copy of the terrain state before each action; `RecordBeforeChange` pushes, `Undo` pushes the current state on the redo side and applies the snapshot; maximum depth `SC2ShellUndoCoordinator.MaxSteps` (100); cleared when the tracked data object changes | terrain (geometry, paint, materials, pairs); the background has its **own separate stack** (`_bgUndoStack`); Costs and Objects have their own legacy snapshot stacks |
| **Command history** (`SC2CommandHistory` on `Session.History`) | `ISC2DocumentCommand`s with do, undo, redo | Environment, Collision2, Scenario, Decals, document-based edits of Costs, Objects and Terrain |
| **Opaque channels** | data captured only for specific step kinds, such as the active blend pair's state (a 2048 x 2048 RGBA texture, about 16 MB) for blend-mask strokes; `CaptureExtra` and `ApplyExtra` for state outside the data object | blend mask (`SC2TerrainCombinedUndo`'s RNM channel); the terrain's auxiliary state |

`SC2ShellUndoCoordinator` joins the mechanisms **inside one shell**: it records which channel owns each step and in what order, and never touches the channels' data. Terrain, Objects and Costs each have one, with up to three channels (snapshot, opaque, commands). So one Undo button inside a shell walks back through steps of different mechanisms in true chronological order.

## 2. The global dispatcher

`SC2GameViewEditorUI.UndoTerrainEdit` and `RedoTerrainEdit` build a list of **eight candidates** and run the one with the **newest timestamp** (`DispatchToMostRecent`):

| # | Candidate | Available when | Undo timestamp | Redo timestamp |
|---|---|---|---|---|
| 1 | Terrain shell | `CanUndo` / `CanRedo` | `LastActionTimestamp` | `LastRedoTimestamp` |
| 2 | Objects shell | `HasUndo` / `HasRedo` | `LastActionTimestamp` | `LastRedoTimestamp` |
| 3 | Costs shell | `HasUndo` / `HasRedo` | `LastActionTimestamp` | `LastRedoTimestamp` |
| 4 | Collision2 shell | `HasUndo` / `HasRedo` | `LastActionTimestamp` | **`LastActionTimestamp`** |
| 5 | Scenario shell | likewise | `LastActionTimestamp` | **`LastActionTimestamp`** |
| 6 | Environment shell | likewise | `LastActionTimestamp` | **`LastActionTimestamp`** |
| 7 | Decals shell | likewise | `LastActionTimestamp` | **`LastActionTimestamp`** |
| 8 | Imported terrains (host-owned local stack) | `ImportedTerrainsHasUndo` | `ImportedTerrainsLastActionTimestamp` | `ImportedTerrainsLastRedoTimestamp` |

Three shells (terrain, objects, costs) use the **redo** timestamp for redo, because redo must replay in exact reverse of what was just undone and the coordinator keeps a separate `LastRedoTimestamp`. The other four use the action timestamp for redo. If a shell's undo moves its action timestamp, this is equivalent; if not, redo order across those shells can differ from the exact reverse. Which is the case was not run.
All timestamps come from the same monotonic clock (`Stopwatch.GetTimestamp()`), so they are comparable across shells.

**What is not a candidate**:
- The **Mission** edits are `SC2ReplaceMissionCommand`s on the shared history, but the Mission panel is not in the list; whether Ctrl+Z reaches them depends on which shell's timestamp covers a command on the shared history (not checked).
- The **reference units shell** has no history of its own; see section 3.
- The **background** is reached through the terrain shell: `Undo()` and `Redo()` consult `_lastEditedTargetIsBackground` to decide which stack to pop, and a **seam stroke** (one that reached both surfaces) is one entry on each stack with the label `Sculpt (core + background)`, recognised on both so they walk back together; blend seam strokes use `Paint Blend (core + background) #N`.

## 3. What a terrain undo restores, and what it does not

A terrain snapshot (`SC2TerrainUndoStack.Capture`, applied by `Apply`) holds: the initialised flag and grid size, the **heights**, the **pair assignment**, the **materials and pairs** (cloned field by field), `ImportOnlyMode`, **the water plane and water depth settings and the water texture paths**, `PlayableAreaName`, the BSP flags, **the Collision2 exclusion list and the custom Collision2 bytes**, **`PlayableWater`**, `CostsGridSize`, **the background's settings**, the global UV settings and the UV1 height weight.
The auxiliary state adds: symmetry mode, centre and radial count, the preview mode, the active sculpt tool and its parameters (`Geometry`), the map name and raw data reference, the Simplified-wireframe state and the export panel state.

So **one terrain Undo can change fields owned by Water, Collision2, Costs (the block set switch through `PlayableWater`) and the Background**; this is why those fields are listed in [Data ownership](DataOwnership.md), section 3. After applying a snapshot the code marks the background runtime dirty and invalidates the pairs' triangle counts and the geometry cache.

**Not in the snapshot:**
- the pairs' mask bytes, mask textures and baked lighting: they are **shared by reference**, not copied (see [Materials, masks, lighting](MaterialsMasksLighting.md));
- `Obj*` lists: Objects have their own history;
- **`ObjReferenceUnits`**: a search of the snapshot class and the auxiliary state finds no reference-unit field.
  The reference units shell records its place, move and delete actions with `_terrainShell.RecordUndoCheckpoint("Place Reference Unit" | "Move Reference Unit" | "Delete Reference Unit")`, which pushes a snapshot onto the **terrain** stack. **By reading the code, that snapshot does not contain the units, so undoing such a step restores the terrain state it does hold but not the unit that was placed, moved or deleted, and the step is consumed.**
  This was found by search and not exercised in a run; it is the first thing to test ([Verification](../Process/Verification.md)).

## 4. Undo and the other systems' caches

- **The live height grid** is invalidated by the apply paths of imported terrain undo (`ApplyImportedTerrainsSnapshot`) and the host; a terrain snapshot restoring heights relies on the terrain shell's own dirty handling.
- **The shared material list**: a snapshot replaces `Data.Materials` and `Data.Pairs` with new lists; the next frame's `SC2SharedMaterials.Sync` repairs the background's list.
- **Costs and the document**: a Costs undo changes the live blocks and the document through the coordinator; `SyncDocumentFromLive` is the path the other way.
- **Decals** rebuild their preview when `Document.Revision` or `TerrainGeometryVersion` changes. A decals command changes the revision; whether every path that restores heights in a terrain undo bumps the geometry version was not checked.
- **The busy overlay** swallows input while a job runs, so Ctrl+Z is not seen during a bake or an export ([Input and overlays](InputAndOverlays.md)).

## 5. Rules for a new undoable thing

1. Decide whether it is a document command (preferred for new document data) or part of an existing shell's snapshot.
2. If it lives in a shell's snapshot, add the fields to `Capture` **and** `Apply` (and the clone list for pairs, which was once missed for the RNM fields).
3. If it needs its own history, give it a timestamp and add a **candidate** to both `UndoTerrainEdit` and `RedoTerrainEdit`; otherwise Ctrl+Z will never reach it.
4. Use the redo timestamp for redo if the shell uses `SC2ShellUndoCoordinator`.
5. Record the checkpoint **before** the first mutation.
6. Add a test: do, undo, redo, and compare ([Testing](../Process/Testing.md)).

## 6. Tests

`ShellUndoCoordinatorTests`, `CommandHistoryTests`, `CompositeCommandTests`, `SessionTests`, `ObjectsCommandTests` and the other `*CommandTests` cover the mechanisms; there is no test of the dispatcher, of the terrain snapshot's field list, or of a reference-unit undo.

## 7. Unverified

Everything about run-time behaviour in this page, including the reference-unit finding and the redo-timestamp equivalence, comes from reading the code.
