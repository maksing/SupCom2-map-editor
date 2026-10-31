# The life of an edit: a sculpt stroke and everything that must follow it

Status: first draft from reading `SC2GeometryMode`, `SC2PaintMode`, `SC2TerrainShell`, `SC2LiveHeightGrid` and the host's wiring at commit `17863e4`; not yet reviewed by a second reader. Read [Mechanisms](Mechanisms.md) first.

A terrain brush stroke is the edit that touches the most systems, so it is the model for any edit that changes the ground. The same chain, shortened, applies to a pair-paint stroke (section 5). Edits that go through document commands (Environment, Costs paint, Scenario name, Decals) are in section 6.

## 1. Systems involved

Brushes (Geometry mode) and the terrain runtime; symmetry; the background; Objects; Decals; the live height grid (and so Costs, placement, the HUD, the buildable overlay); undo; the session and smart-export dirty tracking; the save; and later the export.

## 2. The sequence

**Mouse down** (left button, no Alt, `SC2GeometryMode`):

1. `_isPainting = true`; the stroke's stamp rotation is rolled (or taken from the stamp placement), the brush image cache is built.
2. **One undo checkpoint per stroke**, not per frame: `EnsureStrokeCheckpoint(background)` checkpoints the surface under the cursor; the other surface is checkpointed the first time the stroke reaches it. `Data.Heights` still holds the state before the stroke, because it is only re-synced on mouse-up.

**Each dab** (mouse down and drag frames that pass a minimum interval; Flatten places dabs by distance instead):

3. The brush position is expanded by the **symmetry** (`Data.GetSymmetricPositions`, which delegates to `SC2SymmetryMath`; the symmetry always follows the **core's** setting). For each symmetric position, `CollectTargets` finds which surface runtimes it reaches (core, background, or both near the seam, `SC2SeamReach`), the stroke records that it touched them, and the GPU sculpt is applied to each target's runtime in that surface's local space.
4. **`HeightBrushApplied(position, radius)`** is raised for each symmetric position, with the radius widened by `max(2, cell size * 2)` so that objects at the very edge of the brush's falloff are not missed. Its subscribers (all wired in the host):
   - Objects: `ReprojectAnchoredObjectsInRadius`: props, markers and units anchored to the terrain are moved to the new ground height.
   - Decals: `ReprojectAlignedDecalsInRadius` (decals aligned to the surface follow it) and `MarkPreviewDirtyInRadius`.
   - Terrain shell: `SyncBackgroundRingIfNearCoreBoundary` (if the brush is near the core's border, the background's rim is re-synced to the core's perimeter heights).

   **While the stroke runs, the heights live on the GPU runtime, not in `Data.Heights`.** Code that must see the stroke in progress samples the runtime (`SampleLiveRuntimeHeight`, which the decals use for exactly this reason); code that reads `Data.Heights` or the live height grid sees the state before the stroke.

**Mouse up** (`_isPainting` gate: a camera drag's mouse-up must not pay this cost):

5. `Session.MarkDirty(TerrainGeometry)`: the smart export's dirty tracking learns about the edit (the brush has its own undo and never goes through `Session.History`). The session expands it: hfield, costs and collision2 become dirty or stale as well, and those domains are marked **stale**.
6. `InvalidateLiveHeightGrid()`: the grid every ground-height consumer shares is marked dirty. It rebuilds **debounced by 0.25 s** (the terrain shell also warms it with the preview-build delay), and consumers read the previous grid in the meantime. Without this call, sculpting a hill above the water still showed the old height in the HUD, Costs painting and Objects placement.
7. The runtime heights are copied into the data: `Data.Heights = Runtime.GetHeightsCopy()` for the core if it was touched, and the same for the background's own data.
8. `EditorUtility.SetDirty` on the touched data (so a save picks it up).
9. **`SC2TerrainShell.BumpTerrainGeometryVersion()`**: the static counter that the decals preview polls, so clipped decals are rebuilt against the changed ground.
10. If the background was touched, `RefreshBackgroundRuntime()` re-syncs its rim and preview.

**After the stroke, in later frames:**

11. `SC2TerrainShell.Update` (per frame): the queued preview build runs after its debounce and warms the live height grid; `SC2WaterPreview.Ensure` re-fits the water plane if the water depends on the ground; the background and the imported terrain environment are refreshed ([Mechanisms](Mechanisms.md), section 5).
12. The decals shell notices `TerrainGeometryVersion` changed and rebuilds the projected geometry (and forces a synchronous rebuild of the height grid, about 300 ms on a large map, which is why a pair assignment that does not touch water avoids the bump).
13. Costs painting, generation, placement and the HUD read the rebuilt grid; the buildable overlay recomputes on its worker when its inputs changed.

**Save and export** (later, by the user or by autosave):

14. The save writes the data through the bridges; autosave only adds a backup ([Save, import, export](SaveImportExport.md)).
15. The export projects the hfield and everything derived from it **from the final exported model**, never from the live grid ([Height and water](HeightAndWater.md)).

## 3. The contract each side relies on

| Who | Assumes | Broken if |
|---|---|---|
| Objects, Decals | `HeightBrushApplied` is raised for **every** change of heights, with a radius that covers the falloff | a new height-changing tool (a stamp, an import, a flatten) does not raise it: anchored objects keep their old Y |
| Decals preview | `TerrainGeometryVersion` is bumped for every change of the shape | a path changes heights and only raises the event: the clipped decals stay on the old ground until something else bumps it |
| Costs, placement, HUD, buildable overlay | the live height grid is invalidated after any change of the ground (terrain, props in the heightmap, imported terrain, water pairs) | the invalidation is missed: they act on stale ground |
| Smart export | `MarkDirty(TerrainGeometry)` follows every brush stroke | a stroke without it: the export regenerates anyway today because terrain geometry is in the `alwaysRegenerate` set, but the derived domains' stale state is wrong |
| Undo | one checkpoint is taken **before** the first mutation of the stroke | a checkpoint after the first dab restores a state that already contains the stroke |
| The background | the rim equals the core's perimeter | the ring is not re-synced after a stroke near the border: a visible step between the two surfaces |

## 4. Traps

- **Reading `Data.Heights` during a stroke gives the pre-stroke ground.** Use the runtime.
- **The mouse-up block must stay gated on an active stroke.** Ungated, every camera drag's mouse-up paid for a full heightmap copy, `SetDirty` and a version bump (a real freeze on the Terrain tab).
- **A cheap dab, an expensive stroke end.** Per-dab work is the GPU sculpt plus cheap event handlers; copying heights, invalidating the grid and bumping the version happen once at mouse-up.
- **Symmetry follows the core**, even for a stroke on the background; the background's own setting is ignored.
- **The widened notify radius is deliberate**; do not "tighten" it to the brush radius.

## 5. A pair-paint stroke (shorter)

`SC2PaintMode` pair assignment: one checkpoint at mouse-down; the pair map is written on the GPU; at mouse-up `Session.MarkDirty(TerrainMaterials)` is called and the GPU pair map is **read back asynchronously** into `TrianglePairIndex`. When the readback has been applied, `CompleteStroke` runs: `MarkDirtyAndRepaint(geometryChanged: waterInvolved)`, then **`InvalidateLiveHeightGrid()` only if a water pair changed** (`WaterPairChanged`: the height projection leaves water pairs out, so a pair assignment moves the ground only through them), and
`OutlineNewlyPaintedCells`, which updates the pair-outline layers of the blend mask (`SC2PairOutlineUpdater`, only around the changed cells). So a pair assignment also edits blend-mask layers: **Paint Materials and Blend Mask are coupled through the outline**. The geometry version is **not** bumped for a pair change away from water. A blend-mask stroke writes the blend render texture and its layer stack and has its own opaque undo channel ([Undo](UndoAcrossSystems.md), [Materials, masks, lighting](MaterialsMasksLighting.md)).

## 6. Command-based edits (shorter)

An Environment, Costs, Scenario or Decals edit is an `ISC2DocumentCommand`: `History.Execute` mutates the document and the live data through the bridge, raises `Document.Changed` with `DirectDirtyFlags`, and the session accumulates the flags and sets Dirty or Stale on the derived domains. The shell registers it for the dispatcher by its `LastActionTimestamp`. Examples with an extra step:
Costs paint syncs the live blocks to the document at each stroke's mouse-up (`SyncDocumentFromLive`); Environment takes a snapshot every frame and writes a command when the header changed (`MaybeSnapshot`); Decals commands change `Document.Revision`, which the decals preview polls.

## 7. Change checklist

Adding a tool or path that changes the ground: take a checkpoint before the first change; raise `HeightBrushApplied` per dab; at the end call `MarkDirty(TerrainGeometry)`, `InvalidateLiveHeightGrid()`, copy heights into the data, `SetDirty`, `BumpTerrainGeometryVersion()`; handle the background if it can be reached; add the path to [Change impact](ChangeImpact.md).

## 8. Tests and verification

No test covers the sequence as a whole; `ShellUndoCoordinatorTests`, `FlattenMathTests`, `StampGestureTests` and `SeamReachTests` cover pieces. A scenario that sculpts with an object, a decal and a Costs block in place, then reads back their positions and the dirty flags, is the missing check ([Verification](../Process/Verification.md)).

## 9. Unverified

The timing figures (0.25 s debounce, about 300 ms rebuild) are from the code and its comments, not measured here. The order in which the event subscribers run is the order of subscription in the host and was not relied on.
