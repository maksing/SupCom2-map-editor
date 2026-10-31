# Export pipeline

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1: a wrong change here produces a
map the game refuses to load or loads wrong. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Turns the live map into the files of a `.scd` map folder. Included: building the terrain model (layers, imported terrain, background, water),
the binary files `terrain`, `hfield`, `costs`, `collision2`, `info`, `mapobjs`, `decals`, the Lua files, the lighting mask files, the copied textures,
the minimap and the map shot, and the safety rules that keep editor-only data out of the files.

Not included: saving the map for editing ([persistence](DocumentAndPersistence.md)); how the formats are read and written byte by byte
(`Runtime/BdfIO`); how each domain's data is produced (the domain systems).

## 2. Where the code is

| Part | Files |
|---|---|
| Entry and orchestration | `Scripts/TerrainEditor/SC2TerrainExporter.cs`: `ExportCorrect` (the one the UI uses), `ExportCorrectSmart` and `PreviewSmartExportPlan` (not reachable from the UI), `BuildExportArtifacts`, `DoExport` |
| Terrain model | `SC2TerrainLayerBuilder` (per-pair layers), `SC2TerrainSimplifier`, `SC2TerrainUv1Unwrap`, `MergeImportedTerrain`, `SC2BackgroundTerrain.BuildForExportOrPreview`, `SC2WaterPlane.AppendToExportModel` |
| Derived files | `SC2HfieldProjectionService` (hfield), `BuildCostsModelFromPaintedData` (costs), `BuildCollision2Model` with `SC2Collision2MeshBuilder`, `BuildInfoModel` (info), `SC2ObjectsExport.BuildMapObjsModel` (mapobjs), `SC2DecalsExportService` (decals) |
| Lua | `SC2ObjectsExport.RegenerateSaveLua`, `RegenerateScenarioLua`, `RegenerateScriptLua`; the older `SC2SaveLuaWriter` and `SC2ScenarioLuaWriter` are used only by the editor-only `ExportWindow`; the mission compiler for `_script.lua` ([scenario and mission](ScenarioAndMission.md)) |
| Images | `ExportRnmMaskByName`, `SC2DdsEncoder`, `SC2MapPreviewCapture` (minimap, map shot), `SC2WaterDepthTextureGenerator`, `SC2RuntimeTextureExporter` |
| UI | `SC2ExportPanel` (`RunExport`, folder, options), `SC2ExportShell` (Export tab and Validation sub-tab) |
| Safety | `MapDocument/Domain/SC2TexturePathSafety.cs`, `SC2VanillaArchiveClassifier.cs` |
| Formats | `Runtime/BdfIO/Formats/*` |

## 3. Data

**Input**: the live `SC2TerrainData`, the session's document (for what only it holds: decals, imported terrain, lobby positions, the mission graph, the working
environment header), the list of imported terrain objects (`SC2ImportedTerrainExportData`), an optional minimap image.

**Output**, under `<export folder>/<Map>.scd/` (or `<export folder>/Archives/<Map>.scd/` when the export is packed as archives):

```
maps/<Map>/
  <Map>.terrain.win.bdf  <Map>.hfield.win.bdf  <Map>.costs.win.bdf  <Map>.collision2.win.bdf
  <Map>.info.win.bdf     <Map>.mapobjs.win.bdf  decals (bdf and atlases, when there are decals)
  <Map>_save.lua  <Map>_scenario.lua  <Map>_script.lua   (also copied under uncompiled/maps/<Map>/)
  <Map>.minimap.win.dds  <Map>_PC_mapshot.dds  <Map>.waterDepth.dds (when generated or imported)
  lighting/  <group>_0|_1|_2.win.dds  (bg_<group>_... for the background)  pcloud_up|down.win.dds
Textures/...   copied texture files (see below)
```

## 4. Flows

1. `SC2ExportPanel.RunExport` saves the terrain state first (`SaveTerrainState`), then runs the export on a worker thread behind the busy overlay; anything that needs
   the main thread (textures, the game archive) goes through `SC2BusyOverlay.OnMain`.
2. **Layers**: `SC2TerrainLayerBuilder.Build` buckets triangles by pair and simplifies each pair by its ratio, keeping the map's outer ring and the pair borders.
   Each built layer becomes a `TerrainLayer` with its vertices (UV1 from `SC2TerrainUv1Unwrap`, UV2 for the mask lookup) and a `TerrainMaterial`.
3. **Materials**: every slot goes through `RegTex` / `ResolveRawResourcePath`: an `archive:` marker is resolved to the game path (vanilla textures are referenced, not
   copied, unless the slot asks for a copy) or copied from the archive; a `file:` marker is copied; a plain path is kept if it is safe. The pair's mask group is
   exported by `ExportRnmMaskByName`: three DXT5 files, `_1` carrying the blend alpha.
4. **Merging**: imported terrain objects are merged into the model; the background (when enabled) adds its own layers with their own mask files; the water plane is
   appended when the environment has water and no imported terrain brings water.
5. **Safety sweep**: any material slot still holding an editor marker or a machine-local path is emptied with a warning.
6. `TerrainBdf.RebuildAllBsp`, then the terrain file is serialized. The **hfield is projected from this same final model** (never from the unsimplified mesh), then costs
   (from the painted grids; for five blocks the water mask comes from the exported hfield), collision2 (a Delaunay-refined mesh from the hfield), info (with the five
   environment textures checked like the materials), mapobjs, decals, the Lua files.
7. Stale files from an earlier export of the same map folder (texture and lighting files no longer referenced) are cleaned up; the files are written.

## 5. Invariants

- **Nothing the game cannot resolve reaches a `.bdf`**: no `archive:`/`file:` marker, no drive-letter or UNC path, also not behind a leading slash
  (`SC2TexturePathSafety.IsUnexportable`). Terrain materials are swept and the five info textures are checked; a dropped path produces a warning.
- **The hfield is projected from the exact model written to `terrain.bdf`**, after simplification and merging.
- **Mask files are named after their group; the background's carry the `bg_` prefix**, so the two surfaces never share a file even when their groups have the same name.
  Pairs whose group differs from the first pair's get an unpainted file (the exporter warns about background pairs in that case).
- **An unbaked mask exports neutral lighting** (RGB 128, opaque shadow) with the painted blend alpha; baking is manual and never runs on export.
- **Marker types**: an "Auto Path Node" is resolved to a land or water node at export by the terrain height against the water level and is never written as is.
- **Texture copies**: a texture that is not found on a vanilla archive path is copied into the map; a vanilla archive texture is referenced by its game path unless its
  "copy into map" box is on.
- **The export reads the live data and the session**, not the saved file; a domain that edits live data without a command must be flushed first (`SaveTerrainState`).

## 6. Editor and build differences

- Texture bytes come from `AssetDatabase` in the editor and are re-encoded from live pixels in a player (`SC2RuntimeTextureExporter`).
- The export runs on a worker thread in both; main-thread-only calls are marshalled.
- Smart export (`ExportCorrectSmart`) exists in code and is not used by the UI. It consults the session's dirty flags only for Costs, Objects and ScriptLua and regenerates every other file ([smart export](SmartExport.md)).

## 7. Extension points and how to change it

- **A new texture slot**: resolve it through `RegTex`, and make sure the final sweep covers its index.
- **A new exported file**: build it in `BuildExportArtifacts`, add it to `SC2ExportArtifacts`, write it in `DoExport`, and extend the stale-file cleanup.
- **A new environment texture**: add it to `ApplyEnvTextureExport` and `GamePathOrEmpty` in the info block.
- **A new Lua field**: through the writers (`SC2ObjectsExport.Regenerate*`), keeping original text where the map was imported.

## 8. Editing, verification and tests

**How to edit it safely.** Keep the order: layers, merge, sweep, BSP, serialize, then derived files from the final model. Do not touch the user's save folder from an
export test. After a change, export a map with unusual inputs and scan every output file ([Verification](../Process/Verification.md), section 4).

**Tests today**: `BdfFormatRoundTripTests`, `SmartExportTests`, `PropHeightmapTests`, `Uv1UnwrapTests`, `TexturePathSafetyTests`, `VanillaArchiveClassifierTests`, `CostsNavmeshRulesTests`,
`Collision2FloorTests`, `WaterFloorSamplerTests`, `WaterPlaneHeightTests`, `MapShotFrameMaskTests`, `PreviewImagePersistenceTests`, `LuaBlockParsingTests`, plus the format fixtures in `TestCorpus*`.
The exporter class itself (default assembly) has **no unit test**; it is checked by scenarios.

**Tests a change must add.** A changed file format or slot: a round trip through the parser. A new rule about paths: a case in `TexturePathSafetyTests`. An exporter change:
a scenario that exports a map with unusual inputs, reads the files back (`TerrainBdf.Parse`, `InfoBdf.Parse`) and scans all output text for markers and drive letters; the rule it
uses, if it can be pulled out, in `Scripts/Core` with its own unit test.

## 9. Known limitations and unverified behaviour

- Nothing here was run in the game by the author of this page; correctness of a file for the game is judged by parsing it back and by comparison with vanilla maps recorded in code comments.
- A leak scenario (`ExportPathLeakTest`) exists only in a scratch project outside the repository.
- Texture paths of imported terrain materials are trusted as read from the source `.bdf` and swept at the end; they are not otherwise validated.
- The decals' atlas paths are produced at export; a path-only atlas without a blob is written as stored.

## 10. Related documents and review

[Persistence](DocumentAndPersistence.md), [Materials and pairs](MaterialsAndPairs.md), [Blend mask](BlendMask.md), [Costs](Costs.md), [Lighting bake](LightingBake.md), [Water](Water.md), [Background terrain](BackgroundTerrain.md), [Simplification](Simplification.md), [Decals](Decals.md), [Environment](Environment.md), [Scenario and mission](ScenarioAndMission.md).
Interactions with other systems: [Save, import, export](../Interactions/SaveImportExport.md), [Height and water](../Interactions/HeightAndWater.md), [Textures](../Interactions/TexturesAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
