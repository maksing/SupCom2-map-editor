# Collision2 (`collision2.bdf`)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P3. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Collision2 is the low-polygon collision mesh the game uses for camera zoom and air-unit height limits. The editor generates it from the terrain's height field, previews it in the viewport, lets the user round-trip it through Blender as an `.obj`,
and can export a hand-corrected mesh instead of the generated one. The Collision2 main tab holds one sub-tab, "Collision2".

Not included: how the file is written and ordered with the other files ([export pipeline](ExportPipeline.md)); the height field projection it reads from; the format itself (`Runtime/BdfIO/Formats/Collision2`).

This system was not in the original list of 23 in `DocumentationPlan.md`; it is small but has its own data and a trap (see invariants).

## 2. Where the code is

| Part | Files |
|---|---|
| Shell | `Scripts/Unified/SC2Collision2Shell.cs` (generate, preview mesh as a real `MeshRenderer`, stats, height offset, polygon budget, floor, custom area, exclusions, undo, OBJ and `.bdf` import and export) |
| Mesh builder | `Scripts/TerrainEditor/SC2Collision2MeshBuilder.cs` (greedy-insertion Delaunay refinement of a TIN with a triangle budget) |
| Pure rules | `Scripts/Core/SC2Collision2Floor.cs` (`Apply`: raises every height below the floor, raw units of 1/128) |
| OBJ | `Scripts/Unified/SC2Collision2ObjIO.cs` (positions and triangulated faces only; fan-triangulates n-gons) |
| Document | `MapDocument/Domain/Collision2/` (`SC2CollisionDocument`, `SC2CollisionGenerationSettings`, `SC2CollisionLegacyBridge`, `SC2CollisionValidator`, commands `SC2SetCollision2VerticalOffsetCommand`, `SC2SetCollision2PolygonBudgetCommand`, `SC2SetCollision2ExtentsCommand`) |
| Live data | `SC2TerrainData`: `Collision2HeightOffset` (default 0.25), `Collision2PolygonBudget` (default 2500), `Collision2FloorEnabled`/`Collision2FloorHeight`, `Collision2ExcludedMaterialNames`, `UseCustomCollision2`, `CustomCollision2Bytes`, `Collision2PreviewBytes` |
| Export | `SC2TerrainExporter.BuildCollision2Model`, `BuildCollision2Preview` (the same path as the export, without writing) |

## 3. Data

The generation settings are a height offset and a polygon budget. The floor plane, a custom area (a world rectangle that replaces the hfield or costs extent plus margin), and a list of material names left out of generation ("Exclude from Collision2": decorative imported geometry that is not
walkable ground) are further inputs. The result is a vertex and triangle list. Two byte buffers are kept: the **preview** bytes (the last Generate or import, so the preview survives a restart) and the **custom** bytes (an imported or corrected mesh, written verbatim when
`UseCustomCollision2` is on). Both are `BdfContainer` bytes built by `Collision2Bdf.Serialize`.

## 4. Flows

1. **Generate**: the shell calls `BuildCollision2Preview`, which projects a second height map (with the excluded materials left out and the floor applied), runs the mesh builder with the budget, and returns the model; the shell builds the preview mesh and the statistics.
2. **Export**: `BuildCollision2Model` runs the same generation. It prefers the document's settings (offset, polygon budget) over the live data when a session exists. If `UseCustomCollision2` is set and `CustomCollision2Bytes` is not empty, those bytes are written instead and nothing is generated.
3. **Round trip through Blender**: Export Mesh writes an `.obj`; Import Corrected Mesh reads it back (`ImportCorrectedMesh`), turns it into custom bytes and offers "Use this mesh for map export".
4. **Import**: an existing `collision2.bdf` can be imported as the custom mesh (`ImportCollision2Bdf`); the Import main tab has a "Collision Mesh" sub-tab for loose files.
5. **Undo**: offset, budget and extents are document commands, so they undo through the shared history with the other document tabs; the shell does not keep its own snapshot stack.

## 5. Invariants

- **An imported or corrected mesh reaches the export only through `UseCustomCollision2`.** Without it the export regenerates from the terrain whatever the preview showed; the checkbox exists because earlier the import buttons had no effect on the exported map.
- **The preview is stale after a change to the offset or the budget** and is cleared then; it is never exported by itself.
- **The validator's limit is 10 000 polygons** (`COL001`); the slider spans 2500 to 10 000, and 2500 is only the vanilla convention, not a format limit.
- **The mesh must cover the whole map**: generation starts from two triangles over the extent and inserts the worst-error point until the budget is used (Delaunay, to avoid slivers).
- **Heights are in raw units of 1/128**; the floor is applied to the Collision2 height map only, never to the shared hfield that other files use.
- **Two defaults exist for the offset** (`SC2TerrainData.Collision2HeightOffset` 0.25; the document's `SC2CollisionGenerationSettings.VerticalOffset` starts at 0): keep them in step when changing either, or a map loaded through the document can export with a different offset than one in the live data.

## 6. Editor and build differences

- The shell compiles in both (it sits in `Scripts/Unified`). Its `using UnityEditor` resolves in a player to the namespace that `UnityEditorRuntimeShim` provides, so any editor API it calls must exist in the shim; compile a player build after touching the file ([editor vs build](EditorVsBuild.md)). The `.obj` and `.bdf` pickers are the runtime dialogs in a player.

## 7. Extension points and how to change it

- **A new generation input** (like the floor): a field on `SC2TerrainData`, the same field on `SC2CollisionDocument` and in the bridge, a command if it must undo, the shell control, the projection step in `BuildCollision2Model`, the key that marks the preview stale.
- **A different meshing method**: replace the builder behind the same signature; keep the triangle budget and the full coverage of the extent.

## 8. Editing, verification and tests

**How to edit it safely.** Change the preview and the export together (they share `BuildCollision2Preview`/`BuildCollision2Model`); never reach for the shared hfield to apply a Collision2-only rule. After a change, generate on a map with steep terrain and one with excluded materials, export, and parse the file back.

**Tests today**: `Collision2CommandTests`, `Collision2FloorTests`, `CollisionDocumentBridgeTests`, the Collision2 format round trip in `BdfFormatRoundTripTests`. The shell, the mesh builder and the OBJ reader have no unit test.

**Tests a change must add.** A builder change: a synthetic height map with a known feature, check the vertex count against the budget and that the feature is covered. An OBJ change: a round trip of a small mesh including a quad face and negative indices. A new setting: a command test and a bridge round trip.

## 9. Known limitations and unverified behaviour

- The state of the `SC2CollisionDocument` comment, "built only by the bridge as a read-only snapshot", predates the commands and may be stale; which fields the document really carries live was not audited here.
- The in-game effect of the vertical offset (0.25) was taken from code comments, not re-checked in the game.
- The shell's behaviour in a player build was not run for this document.

## 10. Related documents and review

[Export pipeline](ExportPipeline.md), [Import pipeline](ImportPipeline.md), [Terrain data and runtime](TerrainDataAndRuntime.md), [Document and persistence](DocumentAndPersistence.md), [Editor vs build](EditorVsBuild.md).
Interactions with other systems: [Height and water](../Interactions/HeightAndWater.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
