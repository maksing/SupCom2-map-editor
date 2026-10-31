# Background terrain and the core/background seam

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The optional decorative terrain around the playable core: a second `SC2TerrainData` generated on the same grid, with the core's rectangle carved out, edited with the same brushes, exported into the same
`terrain.bdf`. This document covers its generation, its runtime, how brushes reach it, how it is saved and exported, and the seam with the core.

Not included: the brushes themselves ([brushes](Brushes.md)), the shared material list ([materials](MaterialsAndPairs.md)), the blend mask details ([blend mask](BlendMask.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Generator | `Scripts/TerrainEditor/SC2BackgroundTerrain.cs` (`EnsureGenerated`, `Regenerate`, `ComputeBounds`, `SyncRingFromCore`, `BuildForExportOrPreview`, per-pair detail) |
| Runtime and sync | `SC2TerrainShell` (`_bgRuntime`, `EnsureBackgroundRuntime`, `RefreshBackgroundRuntime`, `ProceduralBackgroundData`, the Background panel), `SC2BackgroundRuntimeSync.cs` |
| Brush reach | `Scripts/Core/SC2SeamReach.cs`, `SC2GeometryMode`/`SC2PaintMode` (`ActiveTerrainData`, `ActiveRuntime`, `ActiveWorldOffset`) |
| Undo | `SC2TerrainShell._bgUndoStack`, `RecordUndoCheckpointFor(data, label)`, `SC2TerrainCombinedUndo` |
| Document | `SC2TerrainDocument.Background` (a full terrain document built by the same bridge, recursively) |
| Export | `SC2TerrainExporter` (background block), `SC2RnmBakeService.BakeBackground` |

## 3. Data

`SC2TerrainData.BackgroundTerrainEnabled`, `BackgroundTerrainSize` (footprint in world units, default 4096, clamped between `MinFootprint` and `MaxFootprint`), and `BackgroundTerrainData`, a nested `SC2TerrainData` that is
never saved as an asset of its own: it travels inside the parent's document (`document.Terrain.Background`). Its grid has the **core's cell size**, its grid lines coincide with the core's, and the cells inside the core
rectangle are assigned to no pair (carved out). Its pairs, mask groups (default group `background`), blend layer stacks, baked lighting and heights are saved like the core's. Its material list is the core's (shared).

## 4. Flows

- **Create or open**: `EnsureGenerated` returns the existing background unchanged if its grid still matches what the current settings would produce; otherwise `Regenerate` rebuilds the heights (a deterministic falloff
  from the core's edge height plus noise, blended with the core's own perimeter heights near the rim) and the pair assignment, keeping the same instance and its pairs.
- **The rim**: `SyncRingFromCore` copies the core's perimeter heights onto the background's rim vertices every time the core's edge changes, so the surfaces meet at one height; export calls it too.
- **Editing**: the active surface is the one under the cursor (`DetermineActiveBrushTarget`); a brush near the seam reaches both surfaces (`SC2SeamReach.ReachesCore/ReachesBackground`), and a blend dab paints both masks,
  as one undo step per surface with a shared label.
- **Preview**: the background has its own runtime; `RefreshBackgroundRuntime` re-applies the preview mode and the ring sync; changes that cannot reach the shell (the undo stack) set `SC2BackgroundRuntimeSync.MarkDirty`.
- **Export**: `BuildForExportOrPreview` builds the background's layers with the rim locked and per-pair simplification, with vertices moved by the world offset; its mask files get the `bg_` prefix.
- **Bake**: `BakeBackground` bakes the background's own masks (see [lighting bake](LightingBake.md)).

## 5. Invariants

- **Same cell size, whole-cell rectangle**: a background cell is either fully inside the carved core rectangle or fully outside, so there is no partial cell and no stitching geometry.
- **The rim heights equal the core's perimeter heights** at export, whatever the preview last showed.
- **The footprint is clamped** to what fits (`ClampFootprint`); a smaller footprint than the core is not allowed.
- **A background whose grid no longer matches is rebuilt**, and hand edits of heights and pair assignment are lost (a notice is shown); its pairs and materials stay.
- **The background is not in the session document's material or pair lists**; its pairs have no document entry, so document commands for pairs act on the core only.
- **Mask names of the two surfaces may be equal; their files may not**: the background's files carry the `bg_` prefix.

## 6. Editor and build differences

- None specific. The background runtime is built lazily in both.

## 7. Extension points and how to change it

- **A new background setting**: a field on the parent `SC2TerrainData`, the document (parent's terrain document), bridges, the Background panel; regeneration must respect it.
- **A new brush**: make it resolve its target with `ActiveTerrainData`/`ActiveRuntime`/`ActiveWorldOffset`, and a seam-aware target list if it paints a mask.

## 8. Editing, verification and tests

**How to edit it safely.** Never assume "the data" is the core: ask which surface. A change to generation must keep the rectangle whole-cell and the rim equal to the core's. Test a stroke on the seam, deep in the core and far in the background.

**Tests today**: `SeamReachTests`, `TerrainDocumentBridgeTests` (including the nested background). The generator and the runtime have no unit test; `SeamBlendTest` and the export scenarios exist outside the repository.

**Tests a change must add.** A geometry rule of the generator: extract it into `Scripts/Core` with a unit test (bounds, hole range, ring set). A brush change: a scenario of three strokes (seam, core, background) checking which surfaces changed and undo of each.

## 9. Known limitations and unverified behaviour

- A very large footprint makes a very large grid; the generation and preview time at the maximum was not measured.
- The look of the seam in the game was not checked in the game.
- Pair-level document commands do not reach the background's pairs.

## 10. Related documents and review

[Brushes](Brushes.md), [Blend mask](BlendMask.md), [Materials and pairs](MaterialsAndPairs.md), [Simplification](Simplification.md), [Lighting bake](LightingBake.md), [Export pipeline](ExportPipeline.md).
Interactions with other systems: [Materials, masks, lighting](../Interactions/MaterialsMasksLighting.md), [Life of an edit](../Interactions/LifeOfAnEdit.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
