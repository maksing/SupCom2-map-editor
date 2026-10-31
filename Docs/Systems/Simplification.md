# Simplification and the terrain layer builder

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Turns the heightfield grid into the exported terrain geometry: triangles are bucketed per pair into layers, each layer is simplified by its pair's ratio, texture coordinates are computed, and the
Simplified preview shows the result. Included: `SC2TerrainLayerBuilder`, `SC2TerrainSimplifier`, UV1 unwrap, the shore band, the Simplified sub-tab and its triangle counts.

Not included: the export after this step ([export](ExportPipeline.md)), the pair assignment ([brushes](Brushes.md)), the background's own detail settings ([background](BackgroundTerrain.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Layer builder | `TerrainEditor/SC2TerrainLayerBuilder.cs` (`Build(data, sortByTriangleCount, ...)`, `SC2TerrainBuildResult`, `SC2TerrainBuiltLayer`) |
| Simplifier | `TerrainEditor/SC2TerrainSimplifier.cs` (vertex clustering; `FindBoundaryVertices`, `FindOuterPerimeterVertices`, `ComputeGlobalNormals`) |
| UV1 | `TerrainEditor/SC2TerrainUv1Unwrap.cs` (`BuildProfiles`, `Sample`), `SC2TerrainShell`/runtime `RecalculateUv1` |
| Shore | `TerrainEditor/SC2ShoreDetail.cs` |
| Preview | `TerrainEditor/SC2SimplifiedPreviewMode.cs`, `SC2TerrainShell.RequestSimplifiedCounts`, `StoreSimplifiedCounts` |

## 3. Data

- Per pair: `SimplifyRatio` (0 = raw). The map-wide `Uv1HeightWeight` controls how much slope stretches UV1. `GlobalUvScale` replaces per-pair tiling when on.
- The output of `Build` is a list of built layers: a mesh (vertices, normals, triangles) and the pair index of each, plus counts before and after (`SourceTriangleCount`, `BuiltTriangleCount`).

## 4. Flows

1. `SC2TerrainLayerBuilder.Build` groups triangles by pair, then simplifies each group by vertex clustering, linear in the size of the layer.
2. Vertices that must not move are locked: the pair borders (`FindBoundaryVertices`), the map's outer ring (`FindOuterPerimeterVertices`, always, so the map edge cannot collapse), the water shore band, and, for the background, its
   rim along the carved hole.
3. UV1 comes from the real 3D surface distance along rows and columns (profiles over the whole grid), so a tiled texture does not smear on steep ground.
4. The Simplified preview shows the same build behind the busy overlay, with a wireframe option; the Export tab can count triangles without showing it.

## 5. Invariants

- **The outer ring of the map survives.** It is locked even when it is not a pair border.
- **Pair borders survive**, so neighbouring layers meet without cracks.
- **Ground within the shore band of the water is not simplified at all** and never merges, so the mesh stays closed along the shore.
- **Triangle indices inside a layer are local to that layer**, as in the real `terrain.bdf`; code that adds a layer offset breaks the BSP builder and the hfield projection.
- **Simplification is deterministic** for the same data and ratios, so the preview and the export agree.

## 6. Editor and build differences

- None specific; heavy work runs behind the busy overlay in both.

## 7. Extension points and how to change it

- **A new locked set of vertices**: add it to the lock set built before `Simplify` (and test that nothing in it moves).
- **A different reduction method**: replace the clustering inside `Simplify`, keeping the locks, the local indices and determinism.

## 8. Editing, verification and tests

**How to edit it safely.** Compare the built triangle counts and a rendering of the Simplified preview before and after. Never change index semantics without the hfield and BSP in mind.

**Tests today**: `Uv1UnwrapTests`, `WaterFloorSamplerTests`; the BSP and hfield projection are covered by `BdfFormatRoundTripTests` and `PropHeightmapTests`. The simplifier and the layer builder have **no unit test**.

**Tests a change must add.** The simplifier is pure geometry on arrays: extract a function over arrays and test it (a flat grid reduces, a locked vertex stays, a border stays, determinism). The result of a change on a real map is checked by comparing counts and the exported file parsed back.

## 9. Known limitations and unverified behaviour

- Quality of the simplified surface against the raw one is judged by eye; no error metric is recorded here.
- Time on very large maps was not measured here.

## 10. Related documents and review

[Export pipeline](ExportPipeline.md), [Background terrain](BackgroundTerrain.md), [Water](Water.md), [Brushes](Brushes.md).
Interactions with other systems: [Height and water](../Interactions/HeightAndWater.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
