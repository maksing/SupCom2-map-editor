# Live routes and legacy

Status: first draft from searching the callers of each class at commit `3077a7d`; not yet reviewed by a second reader. Nothing in this page was run.

The project went through a transition from a **scene-based workflow** (a map imported as a Unity scene of marker, prop and graph components, edited in Unity windows, exported from the scene) to a **unified workflow** (one game view, live `SC2TerrainData`, the map document). Both routes still exist in the code, and several of them look equally real. **Editing the wrong one is the most common way to waste a day or ship a change that does nothing.**

## 1. The four labels

| Label | Meaning |
|---|---|
| **LIVE** | reached from the host (`SC2GameViewEditorUI`) in normal use; the route to edit |
| **SHARED** | the name suggests legacy, but live code still uses it |
| **LEGACY** | reachable only from an editor menu or an editor window of the older workflow; works on older data structures |
| **UNUSED** | present, compiles, and no editor code calls it (tests may) |

## 2. The inventory

| Job | LIVE | LEGACY or UNUSED | Notes |
|---|---|---|---|
| **UI host** | `SC2GameViewEditorUI` (with `.Startup.cs` and `.Decals.cs`), the shells, `SC2EditorTabs`; `Editor/Unified/SC2GameViewEditorBootstrap` (menu entries that create the host rig) | `Editor/Unified/SC2MapEditorWindow` (an `EditorWindow` that hosts the same terrain shell; its header says it is **not the main UI path** and new features belong in the shell), `SC2SceneViewBridge`, `SC2GameViewTerrainBridge`, `SC2CameraNavigator`, `SC2EditorStyles`; `Editor/Unified/README_UnifiedShell.md` describes a very early stage and is out of date | the host runs identically in the editor's Game view and in the player |
| **Scene components** | none | `SC2Marker`, `SC2Prop`, `SC2Graph`, `SC2Environment` (MonoBehaviours), `SC2SceneToMapObjs`, `SC2SymmetrySettings` (a scene component; the unified editor keeps symmetry on `SC2TerrainData`) | `SC2MarkerScenePreview` (the live marker preview) also names `SC2Marker`; whether that is a call or a comment was not checked |
| **Original imported bytes** | **SHARED:** `SC2MapRawData` (`SC2TerrainData.RawData`): holds the original scenario, save and script Lua and the decals bytes of an imported map; `BuildDocumentSnapshot` and the exporter read it | the older windows also build it | do not delete it because the name looks old |
| **Import** | `SC2MapImportOrchestrator` + the Import Map panel (`SC2MapImportPanel`), then the per-domain importers (`SC2TerrainBdfImporter`, `SC2ObjectsImport`, `SC2CostsBdfImporter`, `SC2DecalsBdfImporter`) | **UNUSED:** `SC2MapImportService`, `ISC2MapImportService`, `SC2ImportOptions` (empty), `SC2ContainerProbeResult` (a two-phase probe and import contract used only by tests). **LEGACY:** `Editor/ScdImportWindow` (`SC2/Import .scd...`), `Editor/SC2VanillaImportDiagnostics` (`SC2/Diagnostics/Batch Import Vanilla Maps`) | searching for "import" finds both; start from the panel |
| **Export** | `SC2ExportShell` / `SC2ExportPanel.RunExport` → `SC2TerrainExporter.ExportCorrect` → `BuildExportArtifacts` → `DoExport` | **UNUSED by the UI:** `ExportCorrectSmart`, `PreviewSmartExportPlan`, `LastSmartExportPlan` (the shell's own header says there is no entry in the UI). **LEGACY:** `Editor/ExportWindow` (`SC2/Export .scd...`: patches only the terrain materials, exports markers from the scene) | the smart path shares `BuildExportArtifacts` with the live one, so a change there affects both |
| **Lua files** | `SC2ObjectsExport.RegenerateSaveLua`, `RegenerateScenarioLua`, `RegenerateScriptLua`; the mission compiler for `_script.lua` | **LEGACY:** `SC2SaveLuaWriter`, `SC2ScenarioLuaWriter` (called only by `ExportWindow`) | same file names in the output, different code |
| **Save** | the legacy bridges, `SC2TerrainShell.SaveState`, **in a player** `SC2RuntimeTerrainPersistence.Save` with `BuildDocumentSnapshot` | in the **Unity Editor** a save is `AssetDatabase.SaveAssets()` of the `ScriptableObject` and writes no map folder | the real map-folder save exists only in a player (`#if !UNITY_EDITOR`); the editor cannot exercise it |
| **Load** | `LoadFromDisk` → `SetData` → `AdoptLoadedDocument`, the host's `RestoreImportedTerrainsFromDocument` | | |
| **Undo** | per shell: `SC2TerrainCombinedUndo` (snapshot, blend, commands), the Objects and Costs coordinators, the document `History`; the global dispatcher in the host | **UNUSED seams:** `SC2WorkspaceState`, `SC2SelectionState`, `SC2SceneProjection` (referenced only by `SC2MapSession`; the host and shells keep their own state) | a new undoable thing needs a dispatcher candidate |
| **Editing windows** | the tabs of the game view | `Editor/TerrainMeshEditorWindow` (`SC2/Edit Terrain Geometry`), `Editor/SC2EnvironmentEditor`, `Editor/SC2CostsTerrainTypesEditor`, `Editor/TerrainLayerVisibilityWindow` (`SC2/Scene Visibility`), `Editor/InfoPreset` | the exporter's and the island map's comments name some of these classes; a name in a comment is not a call |
| **Info.bdf editing** | the Environment tab (`SC2EnvironmentShell`), the Info.bdf overlay panel (`SC2InfoBdfEditorPanel`), the standalone tool (`Tools~/InfoBdfEditor`, three ways, one data model) | `SC2EnvironmentEditor` (the older window) | the standalone tool's projects point at an old copy of the sources ([Known risks](KnownRisks.md)) |
| **Material and mesh helpers** | **SHARED:** `SC2TerrainMaterialExporter`, `TerrainMeshBuilder`, `SC2IslandMap`, `SC2FallbackShader` (started in the scene workflow; the exporter, the importers, the material factory and the host use them) | | not legacy despite their origin |
| **Texture tooling (editor only)** | `SC2TextureCatalogBuilder` (`SC2/Textures/Rebuild Catalog`), `SC2TextureManifestMenu`, `SC2WaterTextureImportFixer`, `SC2TerrainTexturePostprocessor` | | live, but only in the editor |
| **Document import contract** | | `SC2ImportOptions` has no options | |

## 3. How to check that a route is live before you edit it

Do this for any class you are about to change if you did not reach it from the host:

1. **Search for callers, not mentions.** `grep -rn "ClassName\." --include=*.cs` and discard hits in `///` and `//` comments; a legacy class is often named in the comments of the live code that replaced it.
2. **Walk up to an entry point.** A live route starts at `OnGUI`, `Update` or `LateUpdate` of the host, at a shell's `OnGameViewGUI` or `DrawEmbeddedGUI`, or at a menu action in the host. A route that starts only at a `[MenuItem]` in `Assets/SC2/Editor` is legacy.
3. **Check the assembly.** Anything under `Assets/SC2/Editor` is editor-only and absent from a player; the user's real build cannot use it.
4. **Ask whether the data it writes is the data the live route reads.** The scene components and `SC2MapRawData`'s scene asset do not feed `SC2TerrainData`.
5. **If it is UNUSED**, a change to it changes no behaviour and no test of the editor will notice; say so in the commit rather than "fixing" something that cannot be reached.

## 4. Rules

- **Do not extend a LEGACY or UNUSED route** to add a feature. Add it to the live route; if the legacy one must keep working, say so in the commit.
- **Do not delete a LEGACY route in the same commit as a feature.** Retiring one is its own change: first move every remaining live caller out ([Editor-only tools](../Systems/EditorOnlyAndLegacy.md), section 7).
- **A fix that touches both a live and a legacy route is a smell**: it usually means shared code that should be extracted to `Core`.
- **When you find a new legacy or unused route, add it to this page** in the commit that finds it.
