# Data ownership: who writes and reads the live data

Status: first draft at commit `17863e4`; not yet reviewed by a second reader. The writer and reader columns come from a script (`C:\temp\docgen\owner.py`, outside the repository) that finds every member access of each of the 107 public fields of `SC2TerrainData` and classifies the file's system; **a write is an assignment, `+=`, `++` or a call of `Add`, `Clear`, `Remove`, `RemoveAll`, `Insert`, `AddRange`, `Sort`**, everything else is a read. It is a search, not a proof: indirect writes through a helper, reflection-free copies and property setters are not seen.
"Load" below means the persistence and bridge code (`SC2MapDocumentToLegacyBridge`, `SC2RuntimeTerrainPersistence`, the legacy bridges), which writes every field when a map is opened.

## 1. The rule

`SC2TerrainData` is the **live, shared, mutable** data of the open map. Any shell may read any field; **each group of fields has one shell that is allowed to write it during editing**, and the other writers are the load path and, for imports, the import code. Anything else that writes a field is a coupling to be aware of (marked below). The document keeps its own copy of most groups, and a shell tells the document through a command or `MarkDirty`
([Mechanisms](Mechanisms.md)); the document is **not** updated by merely changing the live data.

## 2. The groups

| Group (fields) | Written during editing by | Read by | Document counterpart | Signal after a write |
|---|---|---|---|---|
| **Grid and heights** (`GridWidth`, `CellSize`, `Heights`) | Brushes (Geometry mode); the background when it regenerates; terrain shell on `SetData` | Costs, Objects, Decals, Reference units, Lighting bake, Export, preview, Simplification | `SC2TerrainGrid` (heights as a blob) | `HeightBrushApplied`, `TerrainGeometryVersion`, `InvalidateLiveHeightGrid`, `Session.MarkDirty(TerrainGeometry)` |
| **Pair assignment** (`TrianglePairIndex`) | the paint modes (`SC2PaintMode`, `SC2MaterialsPairsMode`); the background | Export, Lighting bake, preview, Simplification | `SC2TrianglePairMap` | `MarkDirtyAndRepaint`, `Session.MarkDirty(TerrainMaterials)` on mouse-up; the live height grid only when a water pair is involved |
| **Materials and pairs** (`Materials`, `Pairs`, `ImportOnlyMode`, `SingleBspNodePerLayer`, `VanillaCandidateBspSplit`) | Materials and pairs (`SC2MaterialsPairsMode`); the background (`SC2SharedMaterials.Sync` rewrites its own list); *the script is less reliable for `Materials` and `Pairs`, whose names other types also use, so this row is from reading [Materials and pairs](../Systems/MaterialsAndPairs.md)* | Export, preview, Lighting bake, Background | `SC2MaterialDocument`, pair documents | a document command (`SC2SetMaterial...`); `SC2SharedMaterials.Sync` keeps the background's list identical |
| **UV settings** (`GlobalUvScaleEnabled`, `GlobalUvScale`, `Uv1HeightWeight`) | Materials | Export, preview, Lighting bake | via the legacy bridge | `MarkDirtyAndRepaint` |
| **Symmetry** (`Symmetry`, `SymmetryCenter`, `RadialCount`) | the terrain shell and the host (the Symmetry menu) | Brushes, Blend mask, Costs, Decals, Objects, Scenario (lobby), Export | with the data | none: readers read it when they act; it is part of the terrain snapshot, so Undo restores it |
| **Brush settings** (`UseCustomBrush`, `CustomBrushTexturePath`, `RandomizeBrushRotation`..., and the same four for the geometry brush) | Blend mask (paint brush), Brushes (geometry brush) | nobody else | with the data | none |
| **Costs** (`CostsBlocks`, `CostsShoreHintOverrideMask`) | Costs | Export, Reference units (the build restriction), Objects | `SC2CostsDocument` | a command (`SC2SetCostsLayerGridCommand`) on each stroke's mouse-up through `SyncDocumentFromLive` |
| **Terrain types** (`TerrainTypes`) | Costs (the terrain types painter) | Export (hfield), BdfIO | `SC2HfieldDocument` | `SC2SetHfieldTerrainTypesCommand` |
| **Playable water** (`PlayableWater`) | Costs (a toggle that regenerates the block set, with a confirm) | Costs, Export, Objects, the host | with the data | the toggle itself regenerates; see [Height and water](HeightAndWater.md) |
| **Costs grid size** (`CostsGridSize`) | import, load, the terrain shell | Costs, Water (plane and depth), Lighting bake, Export, Collision2, Objects | with the data | none |
| **Collision2** (`Collision2HeightOffset`, `Collision2PolygonBudget`, `Collision2FloorEnabled`, `Collision2FloorHeight`, `Collision2CustomArea`, `Collision2AreaSizeZ`, `Collision2ExcludedMaterialNames`, `UseCustomCollision2`, `CustomCollision2Bytes`, `Collision2PreviewBytes`, `Collision2HidePreview`, `Collision2Wireframe`) | Collision2 | Export, Lighting bake (exclusions), **Costs and the terrain runtime** (the offset and the floor, for the overlay on the floor plane) | `SC2CollisionDocument` | commands for offset, budget and extents; the others with the data |
| **Water** (`WaterPlane*`, `WaterUvScale`, `WaterTexturePaths`, `WaterDepth*`) | **the terrain shell** (the Water tab is part of it) | Water preview, Environment panel, Export | with the data | `SC2WaterPreview.Ensure` every tick; `RegenerateWaterDepthPreview` |
| **Background** (`BackgroundTerrainEnabled`, `BackgroundTerrainSize`, `BackgroundTerrainUvScale`, `BackgroundTerrainData`) | the terrain shell (Background tab); `SC2BackgroundTerrain` for the nested data | Brushes, Blend mask, Materials, Lighting bake, Export | `document.Terrain.Background` | `SC2BackgroundRuntimeSync.MarkDirty` |
| **Lighting bake settings** (`RnmBake*`, `RnmShadowExclusions`, `PointCloud*` settings) | the terrain shell (Lighting Bake tab) | Lighting bake, preview | with the data | none until the user bakes |
| **Baked results** (`ImportedRnm*`, `PointCloudUpDds`, `PointCloudDownDds`, `PointCloudBoxOrigin`, `PointCloudBoxSize`; the pairs' baked RNM arrays) | the bake services | Export, the terrain runtime | with the data | `RnmBakeGeneration++` after a core bake |
| **Environment** (`EnvironmentHeader`, `EnvironmentMap`, `SkyMap`, `TexturePath3`, `ColorCorrectionCube`, `FogOfWarCorrectionCube`, `EnvironmentTextureSourceTags`, `EnvironmentTextureForceExport`) | Environment (and import); the terrain shell on load | **Costs** (the water elevation), **Water**, Lighting bake (sun), Objects (marker type resolution), Export, Materials (`EnvironmentMap`), Textures | `SC2EnvironmentDocument` (the working header wins until saved) | commands (`SC2SetEnvironment...`); the per-frame water, sky and imported-material refresh |
| **Scenario fields** (`PlayableAreaName`, `DisplayName`, `MapName`) | Scenario (Scripts tab, Info tab); the host for the name | Export, Objects, Mission | `SC2ScenarioDocument` | commands (`SC2SetScenarioNameCommand`); `PlayableAreaName` through the legacy bridge |
| **Preview images** (`CustomMinimapPng`, `MapShotBgraPixels`) | Scenario (Preview tab) | Export | with the data | none |
| **Objects** (`ObjProps`, `ObjMarkers`, `ObjGraphs`, `ObjArmies`, `ObjUnits`, `ObjZones`, `ObjChains`, `ObjSymmetryGroups`, `NextObjectId`) | Objects | Export, Scenario (army list), Mission (map info), Reference units (footprints), Game assets, the terrain shell | `SC2ObjectsDocument` | `MarkObjectsDirty()` at every mutation site; some edits are commands |
| **Original Lua texts** (`ObjSaveLuaTextOriginal`, `ObjScenarioLuaTextOriginal`) | import only | Export, Objects | `SC2ObjectsDocument` | none: read-only after import |
| **Reference units** (`ObjReferenceUnits`) | Reference units | Objects, Game assets | `SC2ObjectsDocument.ReferenceUnits` | the terrain shell's undo checkpoint |
| **Bookkeeping** (`IsInitialized`, `DataVersion`) | load | everyone | n/a | n/a |

## 3. Couplings that surprise

- **Costs reads the Environment header** for the water elevation (`SC2CostsAutoGen.ExtractWaterElevation`, preferring the document's working header), so changing the water level in the Environment tab changes what Costs generation produces.
- **Costs and the terrain runtime read Collision2's floor and offset** to draw the overlay on the floor plane; the floor is Collision2's data but a Costs overlay concern.
- **The Water settings are written by the terrain shell**, not by a Water shell: the Water tab is a Terrain sub-tab. The Environment panel reads `WaterPlane*` and `WaterDepth*`.
- **Lighting-bake settings belong to the terrain shell** too (the Lighting Bake tab); the bake services write the results back into the data and the pairs.
- **`PlayableWater` is written by the Costs shell**, although Objects and the export also depend on it (the four path graphs, the five costs blocks).
- **Undo of the terrain restores fields of other systems**: the terrain snapshot includes the water plane and depth settings, `PlayableWater`, the Collision2 exclusion list, the custom Collision2 bytes, `CostsGridSize`, the background settings and the UV settings (see [Undo](UndoAcrossSystems.md)).
- **Imported terrain objects are not in `SC2TerrainData`.** They live in the host; the terrain shell reaches them only through the delegates of [Mechanisms](Mechanisms.md), and the document keeps them as `ImportedEntries`.

## 4. Rules

- A new field goes in one of these groups; its writer is named in the system document, and the signal after a write is decided when the field is added.
- **A writer outside the owner is a design smell**; if it is needed, record it in section 3.
- **Never read the document where the live data is the truth during editing** (and the reverse): the live data is edited, the document is updated by commands and `MarkDirty`, and the two meet at save and load through the bridges ([Document and persistence](../Systems/DocumentAndPersistence.md)).
