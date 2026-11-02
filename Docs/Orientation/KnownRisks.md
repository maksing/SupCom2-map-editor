# Known risks and open defects

Status: first draft from the audit of the code at commit `3077a7d` and the findings of the documentation pass; not yet reviewed by a second reader. **This page records; it does not fix.** Every item says how it was found. Sizes and counts were measured at that commit and change; the numbers in an earlier audit summary differ because the files have grown.

## 1. The structural risks

### 1.1 Very large classes

| File | Lines | What it mixes |
|---|---:|---|
| `SC2ObjectsShell.cs` | 9026 | interface, input, placement, selection, symmetry, undo, import, game assets, document sync, for seven object kinds |
| `SC2GameViewEditorUI.cs` | 8538 | menus, panels, viewport input, imported terrain (its own undo and gestures), hotkeys, camera, file operations |
| `SC2TerrainShell.cs` | 5826 | live data, the Terrain tab, preview, save, undo, texture pickers, the per-frame sync |
| `SC2TerrainExporter.cs` | 3279 | the whole export; `BuildExportArtifacts` alone is about 1230 lines |
| `SC2TerrainRuntime.cs` | 2118 | the GPU terrain (and a second file for blend layers) |
| `SC2MapDocumentSerializer.cs` | 2042 | every domain's manifest read and write |

**Consequence:** a change to one function can reach unrelated modes; several work streams edit the same files, so unrelated hunks get committed together. **While working:** [The big files](BigFiles.md), rules 1 to 5.

### 1.2 Two competing sources of truth

The map exists in the GPU, the live data, the session document and shell-local state, with a different authority per domain, joined by bridges, `BuildDocumentSnapshot`, `AdoptLoadedDocument`, `SyncDataForSave`, `SyncPairMapToCPU`, `FlushActiveBlendGroup` and hand-written dirty flags. **Consequence:** a new field is easy to add to the interface and forget in one route (save, load, undo, export, smart export, import); the loss appears later and silently. **While working:** the field route in [Sources of truth](SourcesOfTruth.md).

### 1.3 Fragmented undo

Three mechanisms (the document command history, terrain snapshots, opaque blend channels) plus the shells' own snapshot stacks and the host's imported-terrain stack, joined by timestamps in a dispatcher of eight candidates. **Consequence:** the order of steps depends on every shell stamping its actions correctly; a shell missing from the dispatcher is never undone; a terrain undo restores fields owned by other systems. **While working:** [Undo across systems](../Interactions/UndoAcrossSystems.md), section 5. Specific defects are in section 2.

### 1.4 Tests are concentrated on pure logic

105 test files are in the standalone suite, 3 are EditMode, 6 belong to the Info.bdf tool (114 in all; about 930 test attributes in the standalone suite). That covers rules and file formats well and leaves the riskiest areas thin: the large shells, real input, GPU painting, the save and load of a player, runtime dialogs, import then edit then export, shader stripping, and the interaction of several systems. The scenarios that exist are **outside the repository** and cannot be reproduced by anyone else, and no end-to-end harness is in the repository. The format characterizations read a local copy of the game data and are **skipped, not failed, when it is missing**. **While working:** [Testing](../Process/Testing.md), [Verification](../Process/Verification.md); extract rules into `Core`.

### 1.5 No continuous integration

`.github` holds only the pull request template. Nothing runs automatically: the standalone tests, the EditMode tests, a Windows player build, a check of the documentation links, or an export of a test map read back. **Consequence:** a regression is found by whoever next runs the checks by hand. **While working:** run the checks yourself and say in the commit which you ran ([Testing](../Process/Testing.md), the baseline).

### 1.6 The editor and the player differ a lot

Present only in a player: the real save and the backups (`SC2RuntimeTerrainPersistence`), the runtime dialogs, dropdowns and colour palette, the user directories, the texture capture, the quit confirmation, the window patching. Shaders that exist only at runtime and instanced draws depend on `Resources/` and on `GraphicsSettings` (instancing variants), where **code can work in the editor and silently draw nothing in the player**. The editor's tests do not execute these branches. **While working:** compile and run a player build for any change near them ([Editor vs build](../Systems/EditorVsBuild.md), [Project and build settings](../Systems/ProjectAndBuildSettings.md)).

### 1.7 Legacy code and duplicate routes

The scene-based workflow, the older Lua writers, the editor-only import and export windows, the unified workflow, a smart export that the UI does not call, and two import service designs (one called only by tests) all remain. **Consequence:** it is easy to fix the wrong route. **While working:** [Live routes and legacy](LiveRoutesAndLegacy.md).

### 1.8 Build portability

The standalone test project holds an absolute `SC2Root` (`C:\Users\user\SupCom2 map editor\Assets\SC2`) and an absolute path to Unity's `UnityEngine.CoreModule.dll`. The Info.bdf tool's two projects point `SC2Root` at the **old copy of the sources** under `Documents\Codex\...\outputs\...`, so a build of the tool compiles old code. The vanilla corpus has a default path on the developer's desktop. **Consequence:** builds and tests do not reproduce on another machine, and the tool can ship stale code. **While working:** do not rely on a green run from another machine; fix the paths in their own change ([Tests and release](../Systems/TestsAndRelease.md), [Info.bdf tool](../Systems/InfoBdfEditorTool.md)).

### 1.9 Product limits

Windows only; a local installation of the game is required for textures, units and props; importing an existing map is limited; some visual results were never compared with the game (shading, water, decals); the set-up effort raises the entry cost for users and developers. These are stated in the system documents under "Known limitations".

## 2. Open defects and surprises found by reading the code

None of these was reproduced in a run. They are listed so that nobody has to find them again; each names the document that explains it.

| # | Finding | Evidence | Risk | Document |
|---|---|---|---|---|
| 1 | **Undo of reference-unit actions is probably incomplete**: place, move and delete push a terrain checkpoint, and the terrain snapshot has no `ObjReferenceUnits` | search of `SC2TerrainUndoStack` finds no `Obj*` field | an undo step is consumed without restoring the unit | [Undo](../Interactions/UndoAcrossSystems.md) |
| 2 | Redo candidates of Collision2, Scenario, Environment and Decals use `LastActionTimestamp`, those of terrain, Objects and Costs use `LastRedoTimestamp` | `RedoTerrainEdit` | redo order across shells may not be the exact reverse | [Undo](../Interactions/UndoAcrossSystems.md) |
| 3 | Whether Ctrl+Z reaches **mission** edits (commands on the shared history) was not checked; the mission panel is not a dispatcher candidate | `UndoTerrainEdit` | mission edits may not undo | [Undo](../Interactions/UndoAcrossSystems.md) |
| 4 | `ExportCorrect` (the UI export) never calls `MarkExported()`; only the smart path does | callers of `MarkExported` | the session's pending flags and derived states only accumulate | [Smart export](../Systems/SmartExport.md) |
| 5 | The class comment of `SC2MapSession.PendingDirtyFlags` says smart export does not consult the flags; the exporter does for Costs, Objects and ScriptLua | the exporter | a reader trusts the stale comment | [Smart export](../Systems/SmartExport.md) |
| 6 | The Collision2 vertical offset defaults differ: `SC2TerrainData` 0.25, the document 0 | the two classes | a map loaded through the document can export a different offset | [Collision2](../Systems/Collision2.md) |
| 7 | The writer differs from the vanilla files in three ways: the content tail of collision2 (always 28 zero bytes, vanilla 0 to 28), the offset of an **empty** mapobjs list (real offsets, vanilla `0xFFFFFFFF`), and the padding after the zlib block (the format's mode, vanilla plain `Align32`) | a script over the 56 vanilla maps | unknown whether the game minds; exports have been accepted | [Container](../Formats/Container.md), [Collision2](../Formats/Collision2.md), [MapObjs](../Formats/MapObjs.md) |
| 8 | A mission graph with errors, or Lua that fails the syntax check, makes the export write the plain `_script.lua` and only log the reason | `RegenerateScriptWithMission` | a mission silently missing from an exported map | [Objects and scenario](../Interactions/ObjectsAndScenario.md) |
| 9 | The Info.bdf tool's projects compile the sources of an old copy | the two `.csproj` files | stale tool | [Info.bdf tool](../Systems/InfoBdfEditorTool.md) |
| 10 | `SC2ExportFileState.NormalizeRelativePath` does not reject `..` and `SC2SmartExportWriter` joins the path to the output folder; `SC2SafePath` exists and is not used there | the two classes | a path traversal if a path ever came from a map's data | [Smart export](../Systems/SmartExport.md) |
| 11 | `SC2MapImportService` is not called by the editor | search of callers | dead code that looks like the import route | [Live routes and legacy](LiveRoutesAndLegacy.md) |
| 12 | The map-folder save and the texture capture run only in a player; the editor's save writes an asset | `#if !UNITY_EDITOR` | cannot be exercised in the editor at all | [Save, import, export](../Interactions/SaveImportExport.md) |
| 13 | `SC2Hotkeys.Changed` has no subscriber | search | none known | [Mechanisms](../Interactions/Mechanisms.md) |
| 14 | A mask resolution change after a bake makes the export silently fall back to neutral lighting | the length check in `ExportRnmMaskByName` | a bake lost without a warning | [Materials, masks, lighting](../Interactions/MaterialsMasksLighting.md) |
| 15 | Several shader headers contain non-English text and dates | the shader files | style only | [Shaders](../Systems/Shaders.md) |

## 3. How to use this page

- **Before a change in a risky area**, read the relevant 1.x item and follow its "while working" rule.
- **When you fix an item in section 2**, remove the row in the same commit and update the document it points to.
- **When you find a new risk**, add it here with its evidence, the same day, even if you do not fix it.
