# Orientation: where to work, which route is live, what is risky

Status: first draft written from the code at commit `3077a7d` (sizes and counts below were measured there) and from the findings of the audit that produced the other documents; not yet reviewed by a second reader. This section is for the person who has to change something and does not yet know **which files to open, which of two similar routes is the real one, and what the traps are**.

It does not repeat the system documents; it tells you which of them to open and in what order. Nothing here is a fix: it records what is true today so that nobody has to rediscover it.

## The pages

| Page | Question it answers |
|---|---|
| [Live routes and legacy](LiveRoutesAndLegacy.md) | for each job (import, export, save, undo, Lua, UI host) which code is the **live** route, which is **legacy**, which is **present but unused**, and how to check before you edit |
| [The big files](BigFiles.md) | the six files that hold most of the editor: what is where, how to find it, how to work inside them without breaking unrelated modes |
| [Sources of truth](SourcesOfTruth.md) | which copy of the data is authoritative at each moment, and the **route a new field must take** through the code |
| [Known risks](KnownRisks.md) | the structural risks of the project, with evidence, the rule to follow while working, and the open defects found so far |

## The standard route for any change

1. **Name the system** and open its document in [Systems](../README.md) (the index tells you which). Read its invariants and its "Editor and build differences" first.
2. **Check what depends on it**: find the kind of change in [Change impact](../Interactions/ChangeImpact.md) and read the page it points to.
3. **Confirm the route is live** ([Live routes and legacy](LiveRoutesAndLegacy.md)): there are several old paths that look like the real one. Search for callers starting from the host (`SC2GameViewEditorUI`), not from the file you found first.
4. **Locate the code** with [The big files](BigFiles.md) if the logic lives in one of the six large files.
5. **Decide where a rule lives**: a decision that can be pure goes into `Scripts/Core` with a test; the shell calls it ([Testing](../Process/Testing.md)). Do not add new logic to a large file when it can be extracted.
6. **If you add data**, follow the field route in [Sources of truth](SourcesOfTruth.md): the live data, the document, both bridges, the serializer, the save carry-across, the load adoption, undo, the export, the dirty signal.
7. **Write the test first** (a test of another kind where a unit test is impossible), then the change.
8. **Verify in the editor and in a player build** ([Verification](../Process/Verification.md)); the player has code the editor never runs.
9. **State what you did not verify** in the commit, and update the documents whose invariants changed.

## Which route for which job

| Job | Live route (start here) | Do not start from | Documents |
|---|---|---|---|
| Change the interface (a tab, a panel, a menu, a hotkey) | `SC2GameViewEditorUI` and the shell of the tab; `SC2EditorTabs` | `Editor/Unified/SC2MapEditorWindow` (an editor-only adapter) | [Editor UI](../Systems/EditorUi.md), [Input and overlays](../Interactions/InputAndOverlays.md) |
| Change sculpt, paint or brushes | `SC2GeometryMode`, `SC2PaintMode`, `SC2MaterialsPairsMode` through `SC2TerrainShell` | `Editor/TerrainMeshEditorWindow` | [Brushes](../Systems/Brushes.md), [Life of an edit](../Interactions/LifeOfAnEdit.md) |
| Change what is exported | `SC2TerrainExporter.ExportCorrect` (the UI path) | `Editor/ExportWindow`, `ExportCorrectSmart` (not reachable from the UI) | [Export pipeline](../Systems/ExportPipeline.md) |
| Change the Lua files | `SC2ObjectsExport.Regenerate*Lua` | `SC2SaveLuaWriter`, `SC2ScenarioLuaWriter` | [Lua files](../Formats/LuaFiles.md) |
| Change import | `SC2MapImportOrchestrator` and the Import Map panel | `SC2MapImportService` (called only by tests), `Editor/ScdImportWindow` | [Import pipeline](../Systems/ImportPipeline.md) |
| Change what is saved | the legacy bridges, `BuildDocumentSnapshot`, `AdoptLoadedDocument`, the serializer | the editor's asset save (it saves a `ScriptableObject`, not the map folder) | [Document and persistence](../Systems/DocumentAndPersistence.md), [Save, import, export](../Interactions/SaveImportExport.md) |
| Change undo | the shell's own undo coordinator and the global dispatcher | the session's `SC2WorkspaceState` and `SC2SelectionState` (unused seams) | [Undo](../Interactions/UndoAcrossSystems.md) |
| Change a game file's bytes | `Runtime/BdfIO` | the exporter (it only fills the models) | [Formats](../Formats/README.md), [BdfIO](../Systems/BdfIO.md) |
| Add a pure rule | `Scripts/Core` with a test in `Tests~/StandaloneCharacterization` | a shell | [Core rules](../Systems/CoreRules.md) |
| Draw something with a new shader | `Resources/` and a player build | `Shader.Find` alone | [Shaders](../Systems/Shaders.md), [Project and build settings](../Systems/ProjectAndBuildSettings.md) |
| Release | `SC2EditorVersion` and a tag | | [Tests and release](../Systems/TestsAndRelease.md) |
