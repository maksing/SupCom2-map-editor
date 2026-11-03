# Objects: props, markers, units, armies, areas and chains

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Everything placed on the map besides terrain: props (trees, rocks), markers (start positions, resources, AI points, path nodes, effects, sounds), pre-placed units in army groups, armies
with their alliances, areas (zones) and chains, and the symmetry links between them. Seven sub-tabs: Armies, Units, Props, Markers, Paths, Areas, Chains.

Not included: the path graph tools and the path grid ([path graphs](PathGraphs.md)), reference units ([reference units](ReferenceUnits.md)), the unit and prop browser internals (it is a picker used here).

## 2. Where the code is

| Part | Files |
|---|---|
| Shell | `Scripts/Unified/SC2ObjectsShell.cs` (about 9000 lines: the panels, viewport input, gestures, placement, undo, validation list) |
| Data | `Scripts/Core/SC2ObjectsModel.cs` (`SC2ObjProp`, `SC2ObjMarker`, `SC2ObjUnitNode`, `SC2ObjArmy`, `SC2ObjAlliance`, `SC2ObjGraph`, `SC2ObjZone`, `SC2ObjChain`, `SC2ObjSymmetryGroup`), the lists on `SC2TerrainData` (`ObjProps`, `ObjMarkers`, `ObjUnits`, `ObjArmies`, `ObjGraphs`, `ObjZones`, `ObjChains`) |
| Rules | `Scripts/Core/SC2MarkerDeduplication.cs`, `SC2MarkerOrientation.cs`, `SC2MassSymmetryCheck.cs`, `SC2ObjectGraphRefs.cs`, `SC2HeightMode.cs` (and `SC2HeightModes`) |
| Types | `TerrainEditor/SC2MarkerTemplates.cs` (the engine's marker types), `SC2MarkerPresets.cs` (clusters of markers), `SC2PropHeightmapGeometry.cs` |
| Picker | `Scripts/Unified/SC2UnitPropBrowser.cs`, `SC2EffectMarkerPicker.cs`, `SC2SoundEventPicker.cs` |
| Rendering | `SC2ObjectMarkerRenderer.cs`, `SC2MarkerScenePreview.cs`, `SC2PropModelPreview.cs`, `SC2GameAssetScenePreview.cs`, `SC2SelectionOutline.cs` |
| Document | `MapDocument/Domain/Objects/` (`SC2ObjectsDocument`, `SC2ObjectsLegacyBridge`, validators, `SC2Set*Command` for names, positions, zone rects, symmetry group kind) |
| Export | `Scripts/Core/SC2ObjectsExport.cs` (`BuildMapObjsModel`, `RegenerateSaveLua`, `RegenerateScenarioLua`, `RegenerateScriptLua`) |

## 3. Data

Every object has a stable integer `Id` (`SC2TerrainData.AllocateObjectId()`); **selection and references use ids, not list positions**. Notable shapes:
- `SC2ObjMarker`: name, type, position, orientation (Euler, quaternion flag), colour, prop, extra Lua fields, `AnchorToTerrain`/`AnchorToModel`/`HeightOffset`/`OnWaterSurface` (height mode).
- `SC2ObjUnitNode`: units and groups stored **flat with a parent index**, rebuilt as a tree for display; it carries `ArmyName`, so units follow an army by name.
- `SC2ObjArmy`: name, personality, faction, resources, `Alliances`, `MarkerId` of its start marker. Armies are tied to start-position markers named `ARMY_<n>`.
- `SC2ObjGraph`: edges as pairs of marker ids (`MarkerIdsA/B`); the old index pairs remain only for migration.
- `SC2ObjZone`: rectangle `X1,Z1,X2,Z2` with a `Role` (Playable, Trigger, Unknown); `SC2ObjChain`: a list of marker names; `SC2ObjSymmetryGroup`: members that mirror each other.

## 4. Flows

- **Placing**: a click raycasts to the ground (or an imported model); non-prop objects snap to whole cells, props stay free; a marker is created from its template, optionally linked to its mirror copies when symmetry is on
  (`LinkMarkersWithSymmetry`); a marker preset places a cluster; the height mode decides how Y follows the terrain or a model.
- **Editing**: single-object inspectors; viewport gestures started by hotkeys (`Objects.MoveGesture`, `RotateGesture`, `ScaleGesture`, an axis key limits them, a typed number sets the amount); copy and paste; delete; edits to a
  member of a symmetry group are propagated to its siblings.
- **Chains**: a chain is an ordered list of marker names. **Place waypoints in viewport (blank markers)** (or **Add Chain and place waypoints**) arms `PlacementKind.ChainWaypoint`: every click places a Blank Marker named `<chain>_<n>` and appends it to the chain, so the waypoints follow the order of the clicks (`PlaceChainWaypointAt`; one undo step per click; Esc or Stop placing ends it; with symmetry on, the mirrored markers are created too but only the clicked one joins the chain). While placing, the segment to the cursor is previewed. Every chain has its **own colour**, fixed by its id (`SC2ChainPalette.Rgb`, a low-discrepancy spread over hue, saturation and brightness, tested for several hundred chains); the same colour is shown as a swatch in the list. The selected chain is drawn as a thick line on a dark wider outline. The selected chain's panel has **Go to chain** (the camera centres on its waypoints and backs off until they all fit: `GoToChain`, `RequestCameraFrame`) and a **Colour** picker (**Automatic colour** goes back to the id colour); a chosen colour is saved as `SC2ObjChain.ColorHex` (`rrggbb`, empty = automatic; written to the document manifest as `color` only when set, an old save without it loads as automatic). The colour is editor-only and is not written to `_save.lua`.
- **Armies**: the order of the Armies list is the order of the `ARMY_*` start markers and is what the game gets (start positions, the team's army list, the armies table); the up and down buttons swap markers. **Renaming an army
  cascades** to its marker, its units and groups, the alliances other armies hold with it, its lobby position and chain references; the rename is applied when the field loses focus, as one undo step.
- **Undo**: whole-list snapshots (`PushUndo`, JSON) and document commands for names and positions, joined by `SC2ShellUndoCoordinator`; a command undo copies the document's values back into the live lists.
- **Validation**: `CollectValidationIssues` plus the domain validators (duplicates, army and mass symmetry twins, missing references); the Export tab lists them.
- **Export**: `BuildMapObjsModel` writes props, markers, graphs; the Lua writers regenerate `_save.lua` (armies, areas, chains) keeping original text for imported maps; "Auto Path Node" is resolved to a land or water node.

## 5. Invariants

- **Ids, not indices**, for selection, graph edges, armies and symmetry members.
- **Army names are `ARMY_<something>` while the army has a start marker**; the list order is the game's order; a name that is empty or taken is refused.
- **Units belong to an army by `ArmyName`**; a unit's group is the node it hangs under.
- **Markers do not duplicate**: the same type at the same position is refused (`SC2MarkerDeduplication`), and symmetry slots that collapse onto one point after snapping count once.
- **A marker quaternion is written as (x, y, z, w)**; the identity is (0, 0, 0, 1). Older documents decoded Euler angles in the wrong order and are migrated (`SC2MarkerOrientation.MigrateLegacyEuler`).
- **The graph count follows `PlayableWater`** (2 or 4 graphs); only listed marker types can be nodes of a graph (`ConnectablePathNodeTypes`).
- **A mass marker on a symmetric map has a twin on every symmetry point** (a validator checks it).

## 6. Editor and build differences

- Confirmations that must be cancelable use the runtime confirm; the unit and prop browser indexes blueprint files off the main thread and loads only visible thumbnails, in both.

## 7. Extension points and how to change it

- **A new marker type**: [Editing](../Process/Editing.md), section 6.
- **A new object field that is saved**: the model class, the document and bridges, the serializer default, the undo snapshot (`PushUndo` copies the lists), the export.
- **A new list-based kind**: its own list on `SC2TerrainData`, a stable id, a sub-tab, validation, export.

## 8. Editing, verification and tests

**How to edit it safely.** Never store or compare an object by list index. When a rename or delete touches references (armies, chains, graphs, symmetry groups), update every holder in the same undo step.

**Tests today**: `ObjectsDocumentBridgeTests`, `ObjectsCommandTests`, `ObjectsExpandedCommandTests`, `MassSymmetryTests`, `MarkerOrientationTests`, `PropFacingTests`, `PropHeightmapTests`, `PropDestructableTests`, `PropRenderAttributesTests`,
`LobbySpawnTests`, `LuaBlockParsingTests`; EditMode `ObjectsLuaCharacterizationTests`. The shell has **no unit test**; scenarios for unit placement, chain and army rename and army order exist outside the repository.

**Tests a change must add.** A rule (deduplication, naming, ordering): a pure function with a test. A saved field: the round trip and an old document. A reference cascade (rename, delete): a scenario that performs it and checks every
holder, then undo.

## 9. Known limitations and unverified behaviour

- The selection state of the session document (`SC2SelectionState`) is a seam; the shell keeps its own selection.
- The shell is very large; the list above covers its behaviour at the level of its comments, not every panel.
- Pre-placed unit behaviour in the game (orders, groups) was not checked by the author of this page.

## 10. Related documents and review

[Path graphs](PathGraphs.md), [Reference units](ReferenceUnits.md), [Scenario and mission](ScenarioAndMission.md), [Brushes](Brushes.md) (symmetry), [Export pipeline](ExportPipeline.md), [Document and persistence](DocumentAndPersistence.md).
Interactions with other systems: [Objects and scenario](../Interactions/ObjectsAndScenario.md), [Life of an edit](../Interactions/LifeOfAnEdit.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
