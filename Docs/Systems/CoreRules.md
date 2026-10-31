# Core: the pure rules, one by one

Status: first draft from reading the code and searching the tests at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Testing](../Process/Testing.md) first.

## 1. Purpose and boundaries

`Assets/SC2/Scripts/Core` (assembly `SC2.Core`) is where a rule lives when it should be **testable without Unity**: plain static classes with their inputs as parameters. The shells call the rule; a test calls the rule. This page lists every file of `Core` and the few pure helpers that sit next to the shells, says what each decides, and names the tests that reference it,
so a maintainer can see at once which rules are pinned and which are not.

Not included: the data model classes in detail ([terrain data](TerrainDataAndRuntime.md), [objects](Objects.md)), the document layer ([map document](MapDocumentDomain.md)).

## 2. Where the code is

`Assets/SC2/Scripts/Core/*.cs`, plus the extra files marked "outside Core" below. The standalone test project compiles the pure ones by explicit `Compile Include` ([tests and release](TestsAndRelease.md)); the `ScriptableObject`-dependent ones are covered by EditMode tests.

## 3. Data

Each row: the file, the class it holds, what it decides, and the test files that mention the class (a search of the test projects for the class name, not a proof of coverage).

### Geometry, gestures and brushes

| File | What it decides | Tests |
|---|---|---|
| `SC2AxisMove.cs` (`SC2MoveAxis`) | the world axis a move gesture (G, then X, Y or Z, as in Blender) is limited to; how pressing an axis key toggles it; where along the axis the mouse ray points | `AxisMoveTests` |
| `SC2AxisRotation.cs` (`SC2RotationAxis`) | the axis of a rotate gesture (R, then X, Y, Z), global or local, the typed angle text, the sign seen from the camera | `AxisRotationTests` |
| `SC2FlattenMath.cs` (`SC2FlattenPlane`) | the plane the Flatten brush samples under the brush (area weighted), the falloff, the spacing of dabs along a stroke | `FlattenMathTests` |
| `SC2StampGesture.cs` | the press, drag, release gesture of a stamp brush: the anchor, the size and direction from the drag, the dead zone, the brush rotation (the opposite of the stamp angle) | `StampGestureTests` |
| `SC2BrushPresets.cs` | how a built-in brush image is stored as `brush:NAME` in the path a map keeps | `BrushPresetsTests` |
| `SC2SeamReach.cs` | which of the two terrain surfaces a round brush reaches: well inside the core touches only the core, on the seam both, outside only the background | `SeamReachTests` |
| `SC2PairLockSet.cs` | the pairs a brush must not touch; pair -1 (no pair) has its own entry | `PairLockSetTests` |
| `SC2LobbySpawnMath.cs` (`SC2SymmetryMath`) | **symmetry slot math** shared by the terrain painter, the Objects tab and the lobby spawn editor: slot 0 is the point itself; the order of the other slots is the one every domain relies on; also the body of `StartPositions` | `LobbySpawnTests`, `SymmetryMirrorTests`, `SymmetryRotationTests`, `SymmetrySlotYawTests`, `SymmetryTurnTests`, `MassSymmetryTests` |
| `SC2MassSymmetryCheck.cs` | whether the Mass markers follow the map's symmetry; returns the twins that are missing | `MassSymmetryTests` |
| `SC2PropFacing.cs` | which way a prop model faces (where its vertices are on average, away from its origin) so a symmetry copy carries the lopsided side across the mirror | `PropFacingTests` |
| `SC2MarkerOrientation.cs` | a marker quaternion in `_save.lua` is written (x, y, z, w) (identity is `QUATERNION( 0, 0, 0, 1 )`); older saves used (w, x, y, z), and stored Euler angles are converted | `MarkerOrientationTests` |
| `SC2HeightMode.cs` | how a prop, marker or unit gets its Y: from the heightmap, from the imported 3D model's surface, or not at all; stored as the two old flags so old maps load | `HeightModeTests` |
| `SC2MarkerDeduplication.cs` | marker identity at import (by name) and at placement (position and type, since symmetry slots collapse after snapping) | `ObjectsLuaCharacterizationTests` (EditMode) |
| `SC2ObjectGraphRefs.cs` | path-graph references independent of marker list order; the conversion to marker indices belongs at the format boundary | `ObjectsLuaCharacterizationTests` (EditMode) |
| `SC2PathGrid.cs` | the regular grid of path nodes and the joins: each node to its right, lower and both lower diagonal neighbours, 8 per node, each pair once; only passable positions get a node | `PathGridTests` |
| `SC2BuildSkirtMath.cs` | the game's ground rule for building a structure: `SC2BuildCostsRule`, the skirt, `FlattenSkirt`, `MaxGroundVariation`, with defaults when the blueprint is silent | `BuildSkirtMathTests` |
| `SC2ImportedPivot.cs` | the origin of an imported terrain object, read from the map-center floats of its layers; an object exported from Blender away from the origin turns and scales about that point | `ImportedPivotTests` |

### Blend mask and pairs

| File | What it decides | Tests |
|---|---|---|
| `SC2BlendMaskLayers.cs` (`SC2BlendMaskLayer`) | a layer stores a value and a coverage byte per pixel; the Normal-blend composite `alpha = lerp(previous, value, coverage * opacity)` | `BlendMaskLayerTests` |
| `SC2PairOutlineMath.cs` | the band along the inside of a pair's border that "Outline border" paints into the mask; rasterising cell assignments and a distance falloff | `PairOutlineMathTests` |
| `SC2PairOutlineUpdater.cs` | updating an outline layer after pair assignments change, only around the changed cells (reach, work area, valid zone) | `PairOutlineUpdaterTests` |
| `SC2MaterialPairLinker.cs` | resolves a pair's material indices from its stable ids (the id is the truth, the index a derived cache) | `MaterialPairStableIdTests`, `SharedMaterialsTests` (EditMode) |
| `SC2SharedMaterials.cs` | the core and background use one material list; `Sync` | `SharedMaterialsTests` (EditMode) |
| `SC2ShaderFlag.cs` | a serializable key and boolean value: one shader setup flag of a material | `MapDocumentSerializerTests` |

### Lighting

| File | What it decides | Tests |
|---|---|---|
| `SC2ShadowCasting.cs` | which object (core, background, an imported terrain) casts shadows onto which in the bake; stored as the **switched-off** pairs so an empty list is the default and old maps need no migration | `ShadowCastingTests` |
| `SC2SunShadowMath.cs` | where an object lit by the sun casts its shadow on flat ground: the shadow is `1 / tan(elevation)` times as long as the object is tall, opposite the sun's horizontal part; world X grows right, **Z grows down the map** | `SunShadowMathTests` |
| `SC2PointCloudBaker.cs` | the `pcloud_up` and `pcloud_down` volumes (256 x 256 x 8), terrain only, per voxel as in [Lighting bake](LightingBake.md) | `PointCloudBakerTests` |
| `SC2EnvironmentDefaults.cs` | the header a new map starts with: the real vanilla map Marine_Barrier's header, with water enabled and the water elevation 0 | `EnvironmentDefaultsTests` |

### Costs and collision

| File | What it decides | Tests |
|---|---|---|
| `SC2CostsNavmeshRules.cs` | measured navmesh rules: cross (4-neighbour) growth between blocks, the wall cost ring, the soft-wall gradient in water; 255 = wall, 1 = free, 2 to 254 = cost near a wall | `CostsNavmeshRulesTests` |
| `SC2CostsBlockLabels.cs` | what each block is for by its place in the set (the file names say nothing) | `CostsBlockLabelsTests` |
| `SC2Collision2Floor.cs` | the lowest height a collision vertex may have; raises lower heights (raw 1/128 units) | `Collision2FloorTests` |
| `SC2TerrainMaterialModel.cs` | `SC2CostsBlockData` (1 passable, 5 border auto, 255 impassable) and the material and pair model classes | many bridge and serializer tests |

### Files, Lua and saving

| File | What it decides | Tests |
|---|---|---|
| `SC2SaveLayout.cs` | where one saved map lives (`<Map>.sc2mapshare`) and the order in which its files are tried on load | `SaveLayoutTests` |
| `SC2LuaBlocks.cs` | find a keyed block by brace matching and replace its content | `LuaBlockParsingTests` |
| `SC2SaveLuaParser.cs` | parses the `Markers` block of `_save.lua` text | `LuaBlockParsingTests` |
| `SC2SaveLuaBytecodeReader.cs`, `SC2LuaBytecodeValue.cs` | read the same data from compiled Lua 5.1 bytecode | **no dedicated test found** (exercised by the corpus-based Lua tests) |
| `SC2LuaField.cs`, `SC2LuaValue.cs` | one preserved key/value of a marker; a Lua value | `MapDocumentSerializerTests`, `MarkerOrientationTests` |
| `SC2ObjectsExport.cs`, `SC2ObjectsImport.cs`, `SC2ObjectsModel.cs` | the objects export and import and the objects model ([Lua files](../Formats/LuaFiles.md), [Objects](Objects.md)) | `ObjectsLuaCharacterizationTests` (EditMode), `LuaBlockParsingTests`, the objects command and bridge tests |
| `SC2ImageResample.cs` | cover-and-crop resize of an imported picture to an exact size (minimap, map shot) | `ImageResampleTests` |
| `SC2BusyGate.cs` | the life cycle of the busy plate: the job starts only after the plate has been drawn for enough repaints | `BusyGateTests` |
| `InfoBdfJsonDto.cs` | the JSON shape shared by the standalone Info.bdf editor and the in-editor panel; the same file is compiled into both. Public **fields** (Unity's `JsonUtility` sees no properties), so the standalone side sets `IncludeFields = true` | tested in the tool's own tests ([InfoBdf editor tool](InfoBdfEditorTool.md)) |
| `SC2MapRawData.cs` | the scene workflow's asset ([editor-only tools](EditorOnlyAndLegacy.md)) | none |
| `SC2TerrainData.cs` | the live data ([terrain data](TerrainDataAndRuntime.md)) | several |

### Outside Core, but pure or small

| File | What it decides | Tests |
|---|---|---|
| `Scripts/Unified/SC2ColorEditGesture.cs` | tells the first change of a colour edit from the rest, so one undo checkpoint is taken per edit, not per frame of a drag | none |
| `Scripts/Unified/SC2BrushRadiusGesture.cs` | the Blender-style radius resize gesture, action `Brush.RadiusResizeGesture`, F by default (runs before the hotkey handler and consumes the event) | none |
| `Scripts/Unified/SC2TopViewMirror.cs` | the **Top view mirror**: the game's top-down convention has +X to the right and +Z **down** the screen, Unity's default has +Z up; the helpers `ScreenPointToRay` and `WorldToGui` account for it. A camera looking straight down can reach four (right, up) pairings; this is a display convention, not a data bug | none |
| `Scripts/Unified/SC2FloorOverlayPlane.cs` | the Collision2-floor overlay plane and the two globals that tell the terrain shaders to leave the overlay out below the floor | none |
| `Scripts/Unified/SC2ImportedRnmInfo.cs` | one imported layer as shown in Lighting Bake > Imported Objects | none |
| `Scripts/TerrainEditor/SC2PreviewImageImport.cs` | decodes a picture, fits it with `SC2ImageResample` and stores it as the minimap PNG or the map shot BGRA | none |
| `Scripts/TerrainEditor/SC2PreviewMaterialFactory.cs` | flat-colour, grey and debug materials for previews | none |
| `Scripts/TerrainEditor/SC2StampPlacement.cs` | the stamp mode of a custom brush shared by Paint Blend and Geometry: press anchors, drag sets size (1 to 1000) and angle (Shift snaps to 15 degrees), Strength is the alpha, release places it by replaying a normal first dab so symmetry, undo, locks and the background behave as usual; it owns the gesture state, `SC2StampGesture` the pure rules | none directly |
| `Scripts/SC2Symmetry.cs`, `SC2SymmetrySettings.cs` | `SC2SymmetryMode` (None, MirrorX, MirrorZ, MirrorXZ, Rotational, the two diagonal mirrors and both together) used by the costs painter and the objects; `SC2SymmetrySettings` is the **scene-workflow** component (mode, centre, radial count 2 to 8); the unified editor keeps symmetry on `SC2TerrainData` and uses `SC2SymmetryMath` | none directly (the slot math is in `SC2SymmetryMath` above) |

## 4. Flows

A shell reads input, calls the rule with plain numbers and arrays, and applies the result to its data. Because the rule has no hidden state, a test can reproduce any case with literals.

## 5. Invariants

- **A rule that can be pure is pure.** It lives in `Core`, takes its inputs as parameters, and has a test; the shell does not re-implement it.
- **Stable ids, never indices, are identity** (`SC2MaterialPairLinker`, `SC2ObjectGraphRefs`).
- **Defaults are the empty case**, so old maps need no migration (`SC2ShadowCasting`, `SC2HeightMode`).
- **Coordinate conventions are fixed**: X right, Z down the map, heights in raw 1/128 units where a file is concerned, the sun direction means the direction **to** the sun.
- **Marker quaternions are (x, y, z, w) in the Lua**, not (w, x, y, z).

## 6. Editor and build differences

None; this code has no editor API. It compiles in the editor, in a player and in the standalone project.

## 7. Extension points and how to change it

- **A new rule**: a static class in `Scripts/Core` with its inputs as parameters, a `Compile Include` in the standalone project, a test file named for it, and a row here.
- **Moving a rule out of a shell**: extract the decision into `Core`, call it from the shell, and write the test before changing its behaviour.

## 8. Editing, verification and tests

**How to edit it safely.** Change the rule and its test in one commit; run the standalone suite. If a rule has "none" in the table above and you change it, write its test first.

**Tests today**: the files named in the tables. The rows marked "none" or "no dedicated test found" are the gaps.

**Tests a change must add.** For a rule without a test: a test of its boundary cases and of the case you are fixing; for the gestures that depend on `Event` or the camera: extract the decision into a pure function first.

## 9. Known limitations and unverified behaviour

- "Tests" lists files that **mention the class**; whether a test pins a particular behaviour needs reading the test.
- The gestures and overlay helpers outside `Core` are not tested.
- `SC2LobbySpawnMath.cs` holds `SC2SymmetryMath` and the start-position body builder, so searching for "symmetry" by file name misses it.

## 10. Related documents and review

[Brushes](Brushes.md), [Blend mask](BlendMask.md), [Lighting bake](LightingBake.md), [Costs](Costs.md), [Objects](Objects.md), [Scenario and mission](ScenarioAndMission.md), [Testing](../Process/Testing.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
