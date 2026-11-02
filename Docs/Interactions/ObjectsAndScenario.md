# Objects, armies, symmetry, the lobby, the mission and the Lua files

Status: first draft from reading the call sites listed below at commit `17863e4`; the mission editor part is from the code of commit `4fa4ae8`; not yet reviewed by a second reader. Read [Objects](../Systems/Objects.md), [Scenario and mission](../Systems/ScenarioAndMission.md) and [Lua files](../Formats/LuaFiles.md) first.

The Objects tab owns the map's points and units. Four other systems depend on them: the Lua files, the lobby, the mission and the reference units. Symmetry and the ground both reach into every placement.

## 1. What is shared

| Data | Owner | Read by |
|---|---|---|
| `ObjMarkers` (including the `ARMY_n` start markers), `ObjArmies`, `ObjUnits`, `ObjProps`, `ObjGraphs`, `ObjZones`, `ObjChains`, `ObjSymmetryGroups` | Objects | the export (`mapobjs.bdf`, `_save.lua`, `_scenario.lua`), the mission's map information, the reference units' footprint obstacles, the game-asset previews, the terrain shell |
| The **army list and its order** | the order of the `ARMY_*` start markers | `_scenario.lua` (`StartPositions` and `armies`), `_save.lua` (`Armies`, plus `ARMY_EXTRA`), the mission (`SC2ObjectsExport.ScenarioArmyNames`), the lobby |
| `LobbyStartPositions` (army name to a point on the lobby minimap) | the document's scenario (edited by the lobby panel) | only `_scenario.lua`'s `StartPositions`; the real marker positions are the fallback and the source of the mirror relationships |
| `PlayableAreaName` | Scenario (Scripts tab) | `_script.lua`, Objects (area roles), the mission |
| `Symmetry`, `SymmetryCenter`, `RadialCount` | the terrain shell and the host menu | brushes, blend mask, Costs, Decals, Objects, the lobby |
| Object ids (`NextObjectId`) | one counter | markers, props, units **and reference units** share the preview instance key space, so ids never collide |

## 2. The interactions

**Symmetry.** One setting, many users: `Data.GetSymmetricPositions` (delegating to `SC2SymmetryMath`) is called by the geometry and paint modes, the Costs and Decals shells, and, most of all, the Objects shell (22 call sites). The Objects shell also keeps **symmetry groups** (`ObjSymmetryGroups`) that link placed copies; the validators (`SC2MassSymmetryValidator`, `SC2ArmySymmetryValidator`, codes `OBJ006` and `OBJ007`) warn about a Mass marker or an army start with no twin. The lobby panel uses the same slot math (`SC2LobbySpawnMath`) to find which armies mirror each other.
A prop mirrored by a symmetry copy turns by its **facing** (`SC2PropFacing`: where the model's vertices lie on average) so a lopsided building keeps its ramp on the correct side.

**Ground.** An object anchored to the terrain gets its Y from the ground: the Objects shell reprojects anchored props, markers and units when `HeightBrushApplied` or `ImportedTerrainMoved` is raised ([Life of an edit](LifeOfAnEdit.md)); a placement samples the live height grid. Conversely **props marked "include in heightmap" are part of the ground**: the Objects shell hashes them (`RefreshHeightmapPropsSignature`) after each `MarkObjectsDirty()` and, when the hash changes, **invalidates the live height grid**, so Costs generation, placement and the buildable overlay see the new ground. The same props are extra solids in every exported projection ([Height and water](HeightAndWater.md)).
Naval and amphibious units get a water-aware height from the **water level**, and an `Auto Path Node` marker becomes a Water or Land Path Node at export by `terrainHeight < waterElevation`.

**Renaming an army.** A rename is committed when the Name field loses focus. It changes the live list, then the document's army list and **the lobby start positions**: the shell keeps a log of renames (`_armyRenames`) so that undo and redo of a rename take the document-side names back with them (`ReconcileArmyNamesInDocument`). The mission reads the army order from the live list at the time it compiles.

**The Lua files.** `RegenerateSaveLua` rewrites the markers, areas, chains and armies blocks from the live lists; `RegenerateScenarioLua` rewrites the name, `StartPositions` and `armies`; `RegenerateScriptLua` writes the fixed script with the playable-area line. **`ARMY_*` markers are written to `_save.lua` and `_scenario.lua` and are kept out of `mapobjs.bdf`**; every other marker goes to `mapobjs.bdf`, and graph entries are written as marker indices into **that** list (the editor keeps graph references by marker id and converts at the format boundary, `SC2ObjectGraphRefs`).
`StartPositions`, `armies`, the `Armies` table and the `ARMY_*` markers must name the same set. The exporter also prefers the document's **working environment header** for the water level when it resolves marker types.

**The lobby.** The lobby panel edits only `Scenario.LobbyStartPositions` through `SC2SetLobbyStartPositionsCommand`; the export computes `SC2LobbyStartPosition.CurrentOverrides(positions, realArmyPositions)` and passes it to `RegenerateScenarioLua`. An army without an entry is written at its real marker position; `_save.lua` is never affected. When a map is loaded, `AdoptLoadedDocument` re-attaches the saved positions to the army positions found in the loaded data (`AdoptBases`).

**The mission.** The mission editor's map information (`SC2MissionMapInfoBuilder.From(data)`) is built from the live objects (armies in scenario order, markers, groups) when the panel opens and at export. At export `RegenerateScriptWithMission` runs at the start of `BuildExportArtifacts`: with the mission **disabled or empty** the plain `_script.lua` is written; with errors in the graph, or when the generated Lua **fails the syntax check**, **the export stops** with a message (a dialog in the editor, the returned text in a build) and writes nothing, so a map without its mission is never handed out silently. The army indices the script writes are looked up by army name when the game starts, so an empty lobby slot does not break it ([MissionEditor](../Systems/MissionEditor.md) 4.5).
Renaming or deleting an object that a mission node names makes the validator report a name the map no longer contains.

**Reference units.** They live in `ObjReferenceUnits` and are drawn by the same instanced previews as real units, share the id counter, and are **never exported**. Their buildable overlay reads Costs blocks, the water surface (a delegate from the host), the live height grid and the **footprints of real props and units** (`SC2BuildabilityChecker.GatherObstacles`, refreshed four times a second).

**Game assets.** Placing a unit or prop opens `SC2UnitPropBrowser` (an index built off the main thread when the map loads, at the `DataChanged` event); the placed objects are drawn by `SC2GameAssetScenePreview` (one instanced draw per blueprint) and markers by `SC2MarkerScenePreview`; Effect markers pick their template from `SC2EffectTemplateCatalog` and Sound markers their event from `SC2SoundEventCatalog`, both read from the game archives ([Game assets](../Systems/GameAssets.md)).

## 3. Order of events that matters

- At load, `DataChanged` runs **`EnsureObjectsDocumentSynced`** (the objects document is brought in line with the data) and starts the asset index; the Objects subscriptions exist before the first load because `EnsureTerrainShell` calls `EnsureObjectsShell` first.
- A save writes the objects from the live lists (`SC2ObjectsLegacyBridge.BuildFromLegacy`), so the live lists are the truth; the document copy is for the passthrough of the original Lua texts.
- An export reads the lists **after** `SaveTerrainState`, so what is exported is what was saved.

## 4. Traps

| Trap | Why |
|---|---|
| An edit path that changes an object list without `MarkObjectsDirty()` | the smart export would treat Objects as untouched and pass the original `mapobjs.bdf` through; the heightmap-props hash would not refresh |
| Changing the army order | changes `StartPositions` order, the `Armies` table and the mission's army indices; lobby positions are keyed by name |
| A marker named `ARMY_n` outside the start markers | it is treated as an army start (the pattern is by name) |
| Adding a symmetric placement path that does not use `SC2SymmetryMath` | its slot order would differ from every other domain's |
| A new object kind that is anchored to the ground | it needs the reprojection hooks, or it keeps a stale Y after a sculpt |
| Deleting an army | its lobby position, its `Armies` entry, its units and the mission's references all have to follow |

## 5. Change checklist

| If you change | Also check |
|---|---|
| an object list or its fields | the objects document and bridge, `MarkObjectsDirty()`, the export (`BuildMapObjsModel`, the Lua blocks), the validators, the mission's map information |
| army naming or order | `ReconcileArmyNamesInDocument`, lobby positions, `ScenarioArmyNames`, the mission, `_scenario.lua` and `_save.lua` |
| symmetry | every caller of `GetSymmetricPositions`, `SC2LobbySpawnMath`, the mass and army validators |
| the Lua writers | the original-text passthrough for imported maps, the vanilla ordering facts in [Lua files](../Formats/LuaFiles.md) |
| what counts as ground | the heightmap-props signature and the live height grid invalidation |

## 6. Tests and verification

`ObjectsCommandTests`, `ObjectsDocumentBridgeTests`, `ObjectsExpandedCommandTests`, `ObjectsLuaCharacterizationTests` (EditMode), `LuaBlockParsingTests`, `LobbySpawnTests`, `MassSymmetryTests`, `Symmetry*Tests`, `PropFacingTests`, `PropHeightmapTests`, `ScenarioCommandTests`, `Mission*Tests`. There is no test that renames an army and checks the lobby, `_scenario.lua`, `_save.lua` and the mission together; export a map with two armies, rename one, undo, and compare the files ([Verification](../Process/Verification.md)).

## 7. Unverified

The sequence of the rename cascade and the export abort are from the code; no map was exported for this page. The mission editor part is from the code of commit `4fa4ae8` and no exported map was run in the game.
