# Smart export (`Runtime/SmartExport`)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Export pipeline](ExportPipeline.md) first.

## 1. Purpose and boundaries

A small, Unity-free layer that decides, per file of a map, whether to write the **original bytes unchanged**, a **patched** or **regenerated** file, or to block the file because it is stale and nothing was produced for it. It exists so an imported map can be re-exported without
rewriting what the user never touched. It also writes a Markdown report of the decision for each file.

Not included: producing the bytes of each file ([export pipeline](ExportPipeline.md) does that), and the document's own dirty tracking (`SC2MapSession.PendingDirtyFlags`, see [document and persistence](DocumentAndPersistence.md)).

**Important: smart export is not what the UI runs.** The Export tab calls `SC2TerrainExporter.ExportCorrect`, which regenerates every file. `ExportCorrectSmart` and `PreviewSmartExportPlan` exist, are built on this layer, and have no entry in the UI (a comment in `SC2ExportShell` says so).

## 2. Where the code is

| Part | Files in `Assets/SC2/Runtime/SmartExport/` |
|---|---|
| Flags | `SC2DirtyFlags.cs` (a `[Flags]` enum of 12 data domains) |
| File description | `SC2ExportFileKind.cs`, `SC2ExportFileMode.cs`, `SC2ExportFileState.cs` |
| Rules | `SC2ExportDependencyGraph.cs` (`ExpandStaleFlags`, `IsDirtyOrStale`, `GetStaleReason`, `CreateDefaultFileMasks`) |
| Planning | `SC2SmartExportPlanner.cs` (`BuildPlan`, `CreateOriginal`), `SC2SmartExportPlan.cs` |
| Output | `SC2SmartExportWriter.cs` (`WriteToFolder`), `SC2SmartExportReport.cs` (`ToMarkdown`, written as `sc2_export_report.md`) |
| Assembly and notes | `SC2.MapEditor.SmartExport.asmdef`, `README_SmartExport.md` |
| Callers | `Scripts/TerrainEditor/SC2TerrainExporter.cs` (`ExportCorrectSmart`, `PreviewSmartExportPlan`, `LastSmartExportPlan`, the shared `BuildSmartExportPlanInternal`), `SC2MapSession.PendingDirtyFlags`, the commands' `DirectDirtyFlags`, `SC2EditorContext.DirtyFlags` |
| Tests | `Tests~/StandaloneCharacterization/SmartExportTests.cs` |

## 3. Data

- **`SC2DirtyFlags`**: `TerrainGeometry`, `TerrainMaterials`, `HfieldHeightmap`, `HfieldTerrainTypes`, `Costs`, `Collision2`, `Objects`, `SaveLua`, `ScenarioLua`, `ScriptLua`, `Info`, `Decals`.
- **`SC2ExportFileKind`**: `TerrainBdf`, `HfieldBdf`, `CostsBdf`, `Collision2Bdf`, `MapObjsBdf`, `InfoBdf`, `DecalsBdf`, `DecalDiffuseAtlas`, `DecalNormalAtlas`, `SaveLua`, `ScenarioLua`, `ScriptLua`, `Other`, `Unknown`.
- **`SC2ExportFileMode`**: `OriginalPassthrough`, `Patched`, `Regenerated`, `Missing`, `SkippedBecauseUnchanged`, `BlockedByValidationError`.
- **`SC2ExportFileState`**: kind, normalised relative path (forward slashes, no leading slash), original bytes, output bytes, the file's dirty mask, mode, `IsStale`, warnings, errors.
- **Default masks** (`CreateDefaultFileMasks`): terrain = geometry + materials; hfield = heightmap + terrain types; costs, collision2, info, objects, save, scenario and script Lua = their own flag; decals and both decal atlases = `Decals`.

## 4. Flows

1. **Expansion** (`ExpandStaleFlags`): terrain geometry also marks hfield heightmap, costs and collision2; hfield heightmap marks costs and collision2; objects mark save Lua and scenario Lua. So a terrain edit makes the files derived from it stale although nothing wrote to their flags.
2. **Planning** (`BuildPlan`): for each file, if its mask does not intersect the expanded flags, it becomes a passthrough of the original. Otherwise a stale reason is recorded when only expansion made it dirty ("Stale because costs depend on hfield" and similar). If the caller supplied output bytes, the file is written from them. If not, and there are no dirty flags at all, it
   passes through; **in any other case the file gets an error** ("dirty or stale but no regenerated/patched bytes were provided") and its mode becomes `BlockedByValidationError`.
3. **Writing** (`WriteToFolder`): writes `GetBytesForWrite()` of every file (patched and regenerated output, or the original for a passthrough, nothing for missing, skipped or blocked), then the report.
4. **A dry run** (`PreviewSmartExportPlan`) builds the same plan through the same internal function as a real run, so the two cannot disagree; the last plan is kept in `LastSmartExportPlan` for the Export > Report tab.

## 5. Invariants

- **A stale file is never silently passed through.** Staleness without bytes is an error, not a skip.
- **The dependency table is the only place that says what derives from what.** A new derived file needs an entry in `ExpandStaleFlags`, in `GetStaleReason` and in the default masks, or its staleness is not seen.
- **`SetPassthrough` does not destroy computed output**: a file with no original but with output that was already computed (a cheap mirror copy such as the uncompiled script copy) keeps it as `Regenerated` instead of becoming `Missing`.
- **Paths are relative and normalised** in the plan (backslashes become slashes, a leading slash is removed). The writer joins the path to the output folder and creates the folders; it does **not** reject `..` segments, so every caller must hand it paths it built itself, never a path from a map's own data.
- **The layer has no Unity dependency** (it sits in its own assembly) so it is tested in the standalone project.
- **`PendingDirtyFlags` is the ground truth only for the domains `ExportCorrectSmart` lets it decide.** It unions `PendingDirtyFlags` with an `alwaysRegenerate` set (terrain geometry and materials, both hfield flags, collision2, save Lua, scenario Lua, info and decals). So **only Costs, Objects and ScriptLua can pass through**: Costs because every paint stroke fires a real command and the original `costs.bdf` is kept as a blob; Objects because every mutation site calls `MarkObjectsDirty()` (about 90 call sites in `SC2ObjectsShell`) and the original `mapobjs.bdf` is kept; ScriptLua because nothing edits it and the original is kept. Decals is not gated here because `SC2DecalsExportService` decides original, regenerated or merged itself.
- **Edits that bypass the command layer must call `SC2MapSession.MarkDirty(flags)`.** The terrain brushes have their own undo and never go through `History`, so `SC2GeometryMode` and `SC2PaintMode` call `MarkDirty` on mouse-up. `MarkDirty` applies the same stale expansion and marks the derived domains stale as a command would. A new edit path that mutates data without a command and without `MarkDirty` makes smart export skip a changed file.
- **`MarkExported` is called only by `ExportCorrectSmart`**, after a successful write. The export the UI runs (`ExportCorrect`) never calls it, so in normal use `PendingDirtyFlags` and the derived states only accumulate.

## 6. Editor and build differences

- None; plain C# with no editor API. The callers in `SC2TerrainExporter` are shared by the editor and the player.

## 7. Extension points and how to change it

- **A new exported file**: a value in `SC2ExportFileKind`, a mask in `CreateDefaultFileMasks`, a dirty flag if it has its own source, an entry in the expansion and stale-reason rules if it depends on another file, the caller that supplies its original and regenerated bytes, and a test.
- **Letting a domain pass through in smart export**: it needs a reliable dirty signal at every mutation site (a command or `MarkDirty`) and an original-bytes blob in the document; only then remove its flag from `alwaysRegenerate`. Info stays forced because the document keeps only the 508-byte header, not the whole original container; hfield and collision2 stay forced because they are always derived from the merged mesh and hfield is one file with an always-derived half.
- **Making the UI use smart export**: call `ExportCorrectSmart` from the Export shell and decide where `MarkExported` is called for the normal export too.

## 8. Editing, verification and tests

**How to edit it safely.** Change the rules and the tests together: the planner is small and fully tested; behaviour that looks harmless (passing a file through) is the dangerous one.

**Tests today**: `SmartExportTests` (the planner, the dependency expansion, the passthrough and stale-error cases). The exporter's smart path has no test.

**Tests a change must add.** A new file kind or dependency: a plan test with that flag set that shows the new file's mode and reason. A change to `SetPassthrough` or the stale error: a case for each of "has original", "has output only", "has neither".

## 9. Known limitations and unverified behaviour

- The smart path is not reachable from the UI and was not run for this document, so its end-to-end behaviour on a real imported map is unverified.
- `SC2ExportFileState.NormalizeRelativePath` does not reject `..`; `SC2SafePath` ([map document](MapDocumentDomain.md)) exists for that and the writer does not use it.
- The class comment of `SC2MapSession.PendingDirtyFlags` still says the flags are "deliberately NOT yet consulted" by smart export; the exporter code does consult them (for Costs, Objects and ScriptLua). Trust the exporter.
- Whether every mutation site of the Objects shell calls `MarkObjectsDirty()` was taken from the exporter's comment about a sweep, not re-audited.

## 10. Related documents and review

[Export pipeline](ExportPipeline.md), [Document and persistence](DocumentAndPersistence.md), [Save, import, export (interactions)](../Interactions/SaveImportExport.md), [BdfIO and formats](BdfIO.md), [Import pipeline](ImportPipeline.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
