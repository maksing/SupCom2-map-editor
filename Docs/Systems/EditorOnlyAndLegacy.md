# Editor-only tools and the older scene-based workflow

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Architecture](../Architecture.md) and [Editor vs build](EditorVsBuild.md) first.

## 1. Purpose and boundaries

Everything that runs only inside the Unity Editor (`Assets/SC2/Editor`), and the older workflow it serves: a map imported as a **Unity scene** (GameObjects with `SC2Marker`, `SC2Prop`, `SC2Graph`, `SC2Environment` components and an `SC2MapRawData` asset), edited with Unity windows and exported from the scene.
The current workflow (one game view, `SC2TerrainData`, the map document) does not use the scene objects, but some of this code is still reached: the texture catalog builder, the water texture fixer, the log menu, the game view bootstrap.

Not included: the current editor ([editor UI](EditorUi.md)), the importers that the unified Import Map panel uses ([import pipeline](ImportPipeline.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Game view bootstrap | `Editor/Unified/SC2GameViewEditorBootstrap.cs` (menu `SC2 Map Editor > Open` and `Show Game View UI`: creates the rig that hosts `SC2GameViewEditorUI` as an always-executing component, so the overlay shows in the Game tab without Play mode), `README_UnifiedShell.md` (describes the first stage of this shell; parts are out of date) |
| Editor-window host | `Editor/Unified/SC2MapEditorWindow.cs` (an `EditorWindow` that hosts the same `SC2TerrainShell`; layout exists only in the editor; **new features belong in the shell, not here**), `SC2SceneViewBridge.cs`, `SC2GameViewTerrainBridge.cs`, `SC2CameraNavigator.cs`, `SC2EditorStyles.cs` |
| Older import and export windows | `Editor/ScdImportWindow.cs` (menu `SC2/Import .scd...`: unzip, parse every `.bdf`, build the scene), `Editor/ExportWindow.cs` (`SC2/Export .scd...`: scene to the bdf files, Lua files and a `.scd`), `Editor/SC2VanillaImportDiagnostics.cs` (`SC2/Diagnostics/Batch Import Vanilla Maps`: parse-level diagnostics over a folder of `.scd` files) |
| Older editors | `Editor/TerrainMeshEditorWindow.cs` (`SC2/Edit Terrain Geometry`: transform a layer, replace textures, add a layer, recompute the hfield), `Editor/SC2EnvironmentEditor.cs` (the `info.bdf` editor of the older unified window), `Editor/SC2CostsTerrainTypesEditor.cs` (costs and terrain-type painting of the older window), `Editor/TerrainLayerVisibilityWindow.cs` (`SC2/Scene Visibility`), `Editor/InfoPreset.cs` (an `info.bdf` preset saved as JSON, the whole header as base64) |
| Texture tooling | `Editor/SC2TextureCatalogBuilder.cs` (`SC2/Textures/Rebuild Catalog`, `Rebuild Catalog + Thumbnails`: builds `Resources/SC2TextureCatalog.json` and thumbnail atlases of 1024 px made of 128 px tiles), `Editor/SC2TextureManifestMenu.cs` (`SC2/Regenerate Texture Manifest`), `Editor/SC2TerrainTexturePostprocessor.cs`, `Editor/SC2WaterTextureImportFixer.cs` |
| Scene data carriers | `Scripts/SC2Marker.cs`, `SC2Prop.cs`, `SC2Graph.cs`, `SC2Environment.cs`, `Scripts/Core/SC2MapRawData.cs`, `Scripts/SC2SceneToMapObjs.cs` (scene to `MapObjsModel`, with symmetry-link markers and deleted markers compacted out of graph indices) |
| Started in the scene workflow, **still used by the current editor** | `Scripts/SC2TerrainMaterialExporter.cs` (reads `SC2/Terrain` material textures back into `TerrainMaterial.TexturePaths`; called by `SC2TerrainExporter`, `SC2TerrainMaterialFactory` and the game view as well as `ExportWindow`), `Scripts/TerrainMeshBuilder.cs` (a `TerrainModel` to a Unity mesh split by material; used by the terrain importer, the material factory, imported terrain entries and the game view), `Scripts/SC2IslandMap.cs` (island map generation for costs; used by the exporter and the costs debug images), `Scripts/SC2FallbackShader.cs` |
| Older Lua writers | `Scripts/SC2SaveLuaWriter.cs`, `SC2ScenarioLuaWriter.cs` (see [Lua files](../Formats/LuaFiles.md)) |
| Shader fallback | `SC2FallbackShader` (in the row above) resolves a shader that exists in a build, for placeholder materials: `Shader.Find` can locate any shader in the editor but a build strips shaders nothing references |
| Menu entries in runtime code | `SC2/Open Log File`, `SC2/Reveal Log Folder` in `Scripts/TerrainEditor/SC2Log.cs`; the shim's `MenuItem` attribute is an empty stub so such code compiles in a player |

## 3. Data

The scene workflow keeps the map in the scene and in an `SC2MapRawData` asset (`ScriptableObject`): the platform, the hfield size, and `.bytes` references to the raw terrain, hfield, costs, collision2 and decals data as they were at import. Markers, props and graphs are GameObjects. Export reads them back.
The current workflow keeps all of this in `SC2TerrainData` and the document instead.

## 4. Flows

- **Old import**: pick a `.scd`, unzip, run each `.bdf` through BdfIO, build `<MapName>/Terrain`, `Props/` (placeholder cubes), `Markers/`, `Graphs/`, `Environment` and the raw data asset.
- **Old export** (`ExportWindow`): `terrain.bdf` from the raw data with only the **materials patched** (`TerrainBdf.PatchMaterials`, geometry and BSP unchanged), `mapobjs.bdf` from the scene (`SC2SceneToMapObjs`), `info.bdf` from `SC2Environment`, hfield and costs from the raw data (updated by the painters), collision2 and decals passed through, Lua files by the older writers, then a `.scd` zip.
- **Texture catalog**: `SC2TextureCatalogBuilder` scans `Assets/SC2/Resources/SC2Textures`, writes the JSON catalog and the thumbnail pages; the runtime catalog loads that JSON ([textures](Textures.md)).
- **Texture import fixes**: described below.

## 5. Invariants

- **Editor code never leaks into a player.** Everything in `Assets/SC2/Editor` compiles only in the editor; the runtime code that a player needs must not call into it. Runtime files with a `using UnityEditor` get the shim's types in a player.
- **The texture import postprocessor sets `npotScale = None`** for the editor-copied game textures: real vanilla textures are often not power of two (for example 1896 x 1896 with 11 mips), and Unity's resize round trip produced a full-frame RGB noise on most of them. A few NPOT files with deep mip chains still show corrupted deeper mips at a distance; no `npotScale` setting fixes them, and a decode bypass that was tried was reverted
  after it introduced a blend regression (see the git history around commits `312be98` and `6109844`).
- **Water data textures must be sampled as linear.** Wave normals, the wave animation ramp and per-map water depth are read as raw [0, 1] RGB by `SC2Water.shader`. They import through Unity's IHV (DDS) importer, which has no public sRGB property and which `OnPreprocessTexture` never reaches, so `SC2WaterTextureImportFixer` rewrites the `sRGBTexture: 1` line of the `.meta` file directly and forces a reimport
  (menu `SC2/Textures/Fix Water Texture Import Settings (sRGB)`). In a player the same texture resolved from Resources has no such correction, so the runtime path forces it ([water](Water.md)).
- **Do not add features to `SC2MapEditorWindow`**; add them to the shell so both hosts get them.
- **A catalog change needs a rebuild**: the runtime catalog is a baked JSON, so new files under `Resources/SC2Textures` appear only after `Rebuild Catalog`.

## 6. Editor and build differences

This whole area is the "editor" side: files under `Assets/SC2/Editor` do not exist in a player; the runtime classes named above (data carriers, island map, mesh builder, material exporter, fallback shader, log menu stubs) compile in both. The consequences of that difference for the rest of the code are in [Editor vs build](EditorVsBuild.md).

## 7. Extension points and how to change it

- **A new editor menu entry**: a static method with `[MenuItem("SC2/...")]` in `Assets/SC2/Editor`; if runtime code needs the same function, put the function in `Scripts` and call it from the menu.
- **A texture import rule**: the postprocessor for `TextureImporter` assets; for IHV (DDS) assets the `.meta` rewrite is the only reliable route.
- **Retiring the scene workflow**: first move every remaining caller (the catalog builder, the water fixer, the log menu, the bootstrap) out of the legacy windows; `SC2SaveLuaWriter` and `SC2ScenarioLuaWriter` are then unused.

## 8. Editing, verification and tests

**How to edit it safely.** These files are verified by running the Unity Editor; a change must still compile in a player build (the runtime files they share a name space with). Do not reintroduce `EditorUtility`, `AssetDatabase` or `EditorGUILayout` calls into runtime code.

**Tests today**: none of the editor windows has a test. `Uv1UnwrapTests`, `MarkerOrientationTests`, `SymmetryMirrorTests` and similar cover shared pure code. The scene-to-mapobjs conversion has no test.

**Tests a change must add.** For `SC2SceneToMapObjs` or `SC2TerrainMaterialExporter` changes: extract the rule into `Core` and test it; or a harness scenario that imports a small map, exports and parses the files. For texture import changes: import the problem texture in a scratch project and compare a screenshot.

## 9. Known limitations and unverified behaviour

- The old windows were not run for this document; whether they still work against the current data model is unknown.
- `README_UnifiedShell.md` describes an early stage (navigation keys, bridge buttons) and does not match the current game view.
- The corruption of deep mips on a few NPOT vanilla textures in the editor is unresolved.

## 10. Related documents and review

[Editor vs build](EditorVsBuild.md), [Import pipeline](ImportPipeline.md), [Textures](Textures.md), [Water](Water.md), [Lua files](../Formats/LuaFiles.md), [Costs](Costs.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
