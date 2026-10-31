# Terrain data model and the live terrain runtime

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The working data of the terrain and the machinery that shows it: the height grid, the triangle-to-pair assignment, the pair map and blend mask on the GPU,
the chunk meshes, the preview modes and the terrain shell that hosts the terrain tabs.

Included: `SC2TerrainData` (the whole live map, not only terrain: it also holds the object lists, costs blocks and environment header), `SC2TerrainRuntime`,
`SC2TerrainShell` and its preview modes, the live height grid for ray hits and sampling, the terrain undo stack.

Not included: brushes ([brushes](Brushes.md)), pair and material editing ([materials and pairs](MaterialsAndPairs.md)), the blend mask ([blend mask](BlendMask.md)),
the background surface ([background](BackgroundTerrain.md)), water ([water](Water.md)), export.

## 2. Where the code is

| Part | Files |
|---|---|
| Data | `Scripts/Core/SC2TerrainData.cs` (about 1300 lines), `SC2TerrainMaterialModel.cs` (`SC2Material`, `SC2MaterialPair`, `SC2CostsBlockData`), `SC2SeamReach.cs` |
| Runtime | `Scripts/TerrainEditor/SC2TerrainRuntime.cs` (chunks, pair map, blend render textures, material arrays), `SC2TerrainRuntime.BlendLayers.cs` (blend layers) |
| Shader | `Resources/TerrainEditorShaders/SC2TerrainView.shader` (the viewport look), `SC2PaintBrush.shader` (GPU brushes) |
| Shell | `Scripts/Unified/SC2TerrainShell.cs` (about 5800 lines): modes, preview, save, undo, the background runtime, the Water and Lighting Bake panels |
| Height queries | `Scripts/Unified/SC2LiveHeightGrid.cs` (live plus imported terrain, debounced rebuild), `SC2TerrainShell.RaycastTerrainHeight` |
| Undo | `TerrainEditor/SC2TerrainUndoStack.cs` (snapshots), `SC2TerrainCombinedUndo.cs` (joins snapshots, commands and blend checkpoints) |
| UV | `TerrainEditor/SC2TerrainUv1Unwrap.cs` (texture coordinates that do not stretch on slopes) |

## 3. Data

`SC2TerrainData` is a `ScriptableObject`. Terrain-relevant fields:
- `GridWidth`, `GridHeight`, `CellSize`, `Heights` (one float per vertex, `(GridWidth+1)*(GridHeight+1)`), `IsInitialized`.
- `TrianglePairIndex` (`short[]`, two triangles per cell, -1 = unassigned), `Pairs` (`SC2MaterialPair`), `Materials` (`SC2Material`).
- `Symmetry`, `RadialCount`, `SymmetryCenter`; `GlobalUvScale`, `Uv1HeightWeight`; `BlendLayerStacks`; the nested `BackgroundTerrainData` and its enable and size.
- Other domains also live here: `Obj*` lists, `CostsBlocks`, `TerrainTypes`, `EnvironmentHeader` and texture paths, water settings, lighting bake settings and baked point-cloud files.

The runtime owns what is derived: the chunk meshes (positions and normals are rebuilt from `Heights`), the **pair map** (a render texture with the pair index of each cell)
and the **blend render texture** (`BlendRT`, 2048 x 2048). It reads `Heights` from the CPU and keeps painting on the GPU.

## 4. Flows

- **Opening a map** (`SC2TerrainShell.SetData`): clears per-map state, builds the runtime lazily (`EnsurePreview`: `Init`, heights, `RestorePairMapFromData`, `RestoreBlendRTFromData`),
  rebuilds the water depth preview for maps with auto-generate, and resets the background. A new map is a flat plane sized by `ComputeFlatPlaneGrid` to about `kTargetTriCount` (524 288) triangles.
- **Every frame** (`SC2TerrainShell.Update`): keeps the water plane and sky preview in step, joins the background's material list with the core's (`SC2SharedMaterials.Sync`), refreshes
  the background runtime and the preview mode of both surfaces.
- **Preview modes** (`SC2TerrainShell.PreviewMode`): Textured, RNM (baked lighting), Mats, Pairs, Slope, Height. The mode becomes a shader mode and a material setup
  (`ApplyPreviewModeToChunks`); RNM mode uses baked textures only for pairs that have baked lighting.
- **Pair data to the GPU**: `SetAllPairMaterials` packs the pairs' diffuse, normal, mask and ramp textures into texture arrays and look-up tables (the limit of the look-up is 72 pairs).
- **CPU sync**: the pair map on the GPU is read back to `TrianglePairIndex` on a timer (`kPairSyncInterval`, 0.5 s), at the end of a stroke, and before saving.
- **Undo**: `RecordUndoCheckpoint` snapshots the terrain state before a change; `SC2TerrainCombinedUndo` records which mechanism owns each step so one Undo walks back through snapshots,
  document commands and blend checkpoints in order.

## 5. Invariants

- **`TrianglePairIndex` has `GridWidth*GridHeight*2` entries**, in row-major cell order, two triangles per cell; code that writes it must keep that.
- **The GPU is ahead of the CPU between syncs.** Anything that reads pair data or the blend mask must sync first (`SyncPairMapToCPU`, `FlushActiveBlendGroup`).
- **A restored undo snapshot replaces the lists and objects of the data** (`data.Materials = snapshot.Materials`, and so on). References held by identity do not survive it.
- **The core and the background are two `SC2TerrainData` instances with two runtimes**; a shell method that touches "the active surface" must say which (`ActiveTerrainData`, `ActiveRuntime`).
- **The live height grid is rebuilt lazily and debounced** after geometry changes; consumers may read slightly old data mid-stroke by design.
- **Shaders are loaded from `Resources` first and by name second**, because a shader no material references is stripped from a build (`SC2TerrainRuntime`, `SC2TerrainRuntime.BlendLayers`).

## 6. Editor and build differences

- `UnityEditor.Undo` does not exist in a player; the whole terrain undo is the snapshot stack, which is why it exists.
- The terrain shell and runtime are plain classes, so they run in both. The standalone tests cannot reach them (default assembly).
- A build needs the preview shaders in `Resources` so they are not stripped; the shader variant stripping settings matter for GPU-instanced drawing of props and units.

## 7. Extension points and how to change it

- **A new preview mode**: an enum value and a label in `SC2TerrainShell`, the shader mode mapping, a branch in `SC2TerrainView.shader`.
- **A new per-pair option shown in the preview**: the pair field, `SetAllPairMaterials` (look-up table or array), the shader.
- **A new field of `SC2TerrainData`**: [Editing](../Process/Editing.md), section 4 (document, serializer, bridges, undo snapshot, JSON copy).

## 8. Editing, verification and tests

**How to edit it safely.** Rebuild or refresh a runtime after changing its inputs; sync GPU state before reading it. When a method has a variant for the background, change both. Compile a player build: the shell has `#if` blocks.

**Tests today**: `TerrainCommandTests`, `TerrainExpandedCommandTests`, `TerrainDocumentBridgeTests`, `Uv1UnwrapTests`, `FlattenMathTests`, `SeamReachTests`, `SymmetryMirrorTests`, `SymmetryRotationTests`,
`SymmetrySlotYawTests`, `SymmetryTurnTests`, `HeightModeTests`; EditMode: `MaterialPairStableIdTests`, `SharedMaterialsTests`. The runtime and the shells have **no unit test**.

**Tests a change must add.** A data rule: a unit test in `Scripts/Core` (extract it first). A runtime or shader change: a scenario that builds a small map, drives the shell and reads back the state or a player screenshot
(see `SeamBlendTest`-style scenarios in [Verification](../Process/Verification.md)). A new saved field: the round-trip tests of [Testing](../Process/Testing.md).

## 9. Known limitations and unverified behaviour

- The relation between preview modes and the game's own shading is approximate, especially glow, specular and dynamic shadows (see [Lighting bake](LightingBake.md)).
- The reason for each GPU/CPU sync interval is recorded in code comments only; changing `kPairSyncInterval` was not measured here.
- Large maps and the memory of the blend render textures per surface and per layer were not measured for this document.

## 10. Related documents and review

[Architecture](../Architecture.md), [Brushes](Brushes.md), [Materials and pairs](MaterialsAndPairs.md), [Blend mask](BlendMask.md), [Background terrain](BackgroundTerrain.md), [Editor UI](EditorUi.md).
Interactions with other systems: [Data ownership](../Interactions/DataOwnership.md), [Mechanisms](../Interactions/Mechanisms.md), [Life of an edit](../Interactions/LifeOfAnEdit.md), [Height and water](../Interactions/HeightAndWater.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
