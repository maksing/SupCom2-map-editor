# Costs (movement navmesh) and terrain types

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1: a wrong change here makes units path wrongly in the game.
Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The Costs tab edits the movement grids of `costs.bdf`: one grid per unit class, painted over the terrain, with walls, a cost ring near walls, soft shore walls in water and, derived from them,
the islands. It also paints the **terrain types** grid that goes into `hfield.bdf`. This system covers the data, the painting, the derivation between blocks, the automatic generation from the
terrain, the switch between the 3-block and 5-block sets, the overlay and the guide.

Not included: the path graphs of the AI ([path graphs](PathGraphs.md)); building a `costs.bdf` file byte by byte (`Runtime/BdfIO`); the hfield projection ([export](ExportPipeline.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Pure rules | `Scripts/Core/SC2CostsNavmeshRules.cs` (growth, cost ring, shore zone, block derivation), `SC2CostsBlockLabels.cs`, `SC2BuildSkirtMath.cs`, `Scripts/SC2IslandMap.cs` |
| Generation | `Scripts/SC2CostsAutoGen.cs` (`GenerateFromHeight`, thresholds, water mask), `SC2CostsBdfImporter.cs` (import of a real `costs.bdf`) |
| Shell | `Scripts/Unified/SC2CostsShell.cs` (about 1900 lines), `Scripts/Unified/SC2CostsGuide.cs`, `Scripts/TerrainEditor/SC2CostsDebugImageWriter.cs` |
| Overlay and checks | `Scripts/Unified/SC2BuildabilityChecker.cs`, `SC2BuildableOverlay.cs`, `Resources/SC2CostsOverlay.shader` |
| Data | `SC2CostsBlockData` in `Scripts/Core/SC2TerrainMaterialModel.cs`; `SC2TerrainData.CostsBlocks`, `TerrainTypes`, `CostsGridSize`, `PlayableWater`, `CostsShoreHintOverrideMask` |
| Document | `MapDocument/Domain/Costs/` (`SC2CostsDocument`, `SC2CostsLegacyBridge`, `SC2CostsValidator`, `SC2SetCostsLayerGridCommand`) |

## 3. Data

- `CostsBlocks`: 3 blocks (`Land_Small`, `Land_Medium`, `Land_Large`) for a land map, 5 (`Land_Small_water_Surface_ShoreLine`, `Land_medium`, `Land_Large_water_Abyss_ShoreLine`,
  `Land_Large_water_Seafloor`, `Land_Medium_Water_Surface`) when `PlayableWater` is on. The editor labels them by role (`SC2CostsBlockLabels`: "Tanks", "Buildings", "ACU, experimentals" / "Megaliths", ...).
- A block's `Navmesh` has one byte per cell of the `CostsGridSize` square: **255 wall, 1 free, 2..254 cost of a passable cell near a wall**. Also per block: `OutlineOpenMask` (cells of the automatic outline the user opened),
  `BlockedMask` (the ACU block's hand-blocked cells).
- `TerrainTypes` (`ushort` per cell): the id painted into `hfield.bdf` (2 = Default, 3 = Chasm_Bottom).
- `CostsShoreHintOverrideMask`: pins on the shore block.

## 4. Flows

- **Block 0 is painted; the others are derived.** Block 0 is edited by brush, copy, grow or auto-generate; every other block is recomputed from it by `RecomputeDerivedCostsBlocks`
  (`SC2CostsNavmeshRules.DeriveLandOnlyWalls` / `DeriveWaterProfileWalls` and `BuildBlock`). The shore block of a 5-block set (block 4) accepts shore pins. A block switched to Manual keeps what the user painted.
- **Resync** happens at the end of a stroke, after Auto-generate, Grow, Copy and the "Resync derived blocks now" button; it does **not** happen on load or when the terrain changes.
- **Auto-generate** (`GenerateFromHeight`): marks cells impassable by slope (limits per size class, small 60, medium 45, large 30 degrees) and, where the block does not tolerate water, by being underwater; the
  result becomes block 0 and the rest is derived. The thresholds are sliders, not asserted facts.
- **Switching 3 and 5 blocks** (`ApplyPlayableWaterToggle`): always asks for confirmation (the runtime confirm in a build). On confirmation the block list is replaced by an empty set of the other kind, the manual
  flags and shore pins are cleared, the decorative water is switched on when playable water is switched on, and **nothing is generated from the terrain**; the user presses Auto-generate when wanted.
- **Export** (`BuildCostsModelFromPaintedData`): reads the painted grids (from the document's blob when present), rebuilds every block with `BuildBlock` (for five blocks with the water mask taken from the exported hfield),
  and computes the island maps.
- **Overlay**: the grids are shown over the terrain by `SC2CostsOverlay.shader`; the buildable overlay and the build checker also read them.

## 5. Invariants

- **Only block 0 and the shore block are painted** (`IsAutoManagedCostsBlock`); every other block is derived and must be resynced after block 0 changes.
- **Walls in water are soft walls and other walls hard walls**; the cost ring is built from hard walls only, except in the large seafloor block where every wall counts (measured on the vanilla corpus, see the comment of
  `SC2CostsNavmeshRules`).
- **Every cell value is 255, 1 or 2..254**; a block must have `CostsGridSize * CostsGridSize` bytes (a mis-sized block is recreated all-free by `EnsureCostsAndTerrainTypes`, which only ever **adds** missing blocks).
- **A set switch never regenerates and never keeps old blocks** (the block list is cleared, then ensured).
- **The path graph count follows `PlayableWater`** (2 graphs for land, 4 with water).
- **Block indices mean roles only for a set of 3 or 5**; any other count is treated as an unknown set (labels fall back to file names, derivation is skipped).

## 6. Editor and build differences

- The Costs tab is a plain shell, identical in both. The confirmation of a set switch uses the runtime confirm in a build.
- `SC2CostsTerrainTypesEditor` (in `Editor/`) is the editor-only tool for imported maps and does not compile into a player.

## 7. Extension points and how to change it

- **A new rule of the derivation**: in `SC2CostsNavmeshRules` (pure, with a test), then call it from `RecomputeDerivedCostsBlocks`; the export recomputes with the same rule, so both must change together.
- **A new terrain type**: the preset list of the shell and the export of the id; the id is fixed by the game.
- **A new generation threshold**: `SC2CostsAutoGen.Thresholds` and its slider.

## 8. Editing, verification and tests

**How to edit it safely.** A change to a rule applies to the live derivation and to the export; test both. After changing the block set code, check a switch back and forth with painted data and with undo.

**Tests today**: `CostsNavmeshRulesTests`, `CostsBlockLabelsTests`, `CostsCommandTests`, `CostsDocumentBridgeTests` (**one known failure**: `BuildFromLegacy_DetectsPlayableWaterProfile`, see [Testing](../Process/Testing.md)),
`BuildSkirtMathTests`. The shell and the generator have **no unit test**; a scenario for the set switch (cancel, accept, the state afterwards) was run once outside the repository.

**Tests a change must add.** A rule: a case in `CostsNavmeshRulesTests` with a small grid. A generation change: a test of `GenerateFromHeight` on a synthetic height function (extract the pure part first). A shell change: a scenario
that drives the shell and reads the grids back.

## 9. Known limitations and unverified behaviour

- The generation thresholds are not confirmed against the game; they are starting points.
- The known failing test above has not been explained.
- The result in the game (units pathing on the exported grids) was not checked by the author of this page.
- The derived blocks are not resynced on load; an older save may need the manual resync.

## 10. Related documents and review

[Path graphs](PathGraphs.md), [Export pipeline](ExportPipeline.md), [Water](Water.md), [Document and persistence](DocumentAndPersistence.md).
The user-facing explanation of the tab is `SC2_Costs_Guide_Draft.md` and `SC2CostsGuide`.
Interactions with other systems: [Height and water](../Interactions/HeightAndWater.md), [Life of an edit](../Interactions/LifeOfAnEdit.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
