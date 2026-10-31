# How the systems work together

Status: first draft from reading call sites at commit `17863e4`; not yet reviewed by a second reader. The system documents say what each system does alone; this section says **how they depend on each other**: which data they share, who tells whom about a change, in what order things happen, and what breaks when one side forgets its part.

Read [Architecture](../Architecture.md) first.

## The shape of the coupling

The editor is one host object (`SC2GameViewEditorUI`) that creates one **shell** per tab. The shells are **siblings: a shell does not hold a reference to another shell**. Everything they share is reached in one of five ways, and each page of this section is about some combination of them:

| # | Mechanism | Example | Page |
|---|---|---|---|
| 1 | **Shared live data**: every shell is built with the terrain shell and reads and writes `SC2TerrainData` through it | Costs reads `Heights`, Objects writes `ObjMarkers`, Water writes `WaterPlaneHeight` | [Data ownership](DataOwnership.md) |
| 2 | **The session and the document**: commands, `History`, `PendingDirtyFlags`, `Document.Revision` | an Environment edit is a command that dirties `Info` | [Mechanisms](Mechanisms.md) |
| 3 | **Events and delegates wired by the host** | `HeightBrushApplied` makes Objects, Decals and the background follow a sculpt | [Mechanisms](Mechanisms.md) |
| 4 | **Counters that others poll** | `TerrainGeometryVersion`, `RnmBakeGeneration`, `Document.Revision` | [Mechanisms](Mechanisms.md) |
| 5 | **Static services** | `SC2BusyOverlay`, `SC2Hotkeys`, `SC2TextureBrowser`, `SC2Log`, the game archive reader | [Mechanisms](Mechanisms.md) |

The **terrain shell is the hub**: it owns the live data, hosts the Water tab and the texture picker widgets, runs the per-frame synchronisation of the preview (water plane, sky, shared materials, background, imported terrain environment, height grid), and is the only source of the cross-shell events. The **host** is the only place that knows all the shells and wires them together.

## Who depends on whom

Read a row as "this system uses these others". The right-hand column is the mechanism and the page that explains it.

| System | Reads from | Writes to or notifies | Page |
|---|---|---|---|
| Brushes (Geometry, Paint) | symmetry, pair locks, seam reach | `Heights`, `TrianglePairIndex`; `HeightBrushApplied`, `TerrainGeometryVersion`, live height grid, `Session.MarkDirty` | [Life of an edit](LifeOfAnEdit.md) |
| Blend mask | pair map, background | the blend render textures and layer stacks | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| Materials and pairs | texture picker, shared list | `Materials`, `Pairs`; the background through `SC2SharedMaterials.Sync` | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| Lighting bake | heights (hfield projection), pairs, environment sun, background | baked RNM and point cloud on the data; `RnmBakeGeneration` | [Materials, masks, lighting](MaterialsMasksLighting.md) |
| Background | core rim heights, shared materials | its own `SC2TerrainData`; follows `HeightBrushApplied` | [Life of an edit](LifeOfAnEdit.md) |
| Water | environment header (level, enable), heights, water pairs | `Water*` fields, the water plane, the depth texture | [Height and water](HeightAndWater.md) |
| Environment | none | the header; read by Water, Costs, the bake, Objects, the export and the viewport lights | [Height and water](HeightAndWater.md) |
| Costs | live height grid, water level, Collision2 floor (overlay) | `CostsBlocks`, `TerrainTypes`, `PlayableWater`, then the document | [Height and water](HeightAndWater.md) |
| Collision2 | hfield projection, water, material exclusions | its own settings and mesh bytes | [Height and water](HeightAndWater.md) |
| Objects | live height grid, water, game assets | `Obj*` lists; invalidates the height grid for props in the heightmap | [Objects and scenario](ObjectsAndScenario.md) |
| Scenario, lobby, mission | the armies of Objects, `PlayableAreaName` | lobby positions, the three Lua files | [Objects and scenario](ObjectsAndScenario.md) |
| Decals | live height grid or terrain model, imported terrain; `TerrainGeometryVersion`, `Document.Revision` | the decals document | [Life of an edit](LifeOfAnEdit.md) |
| Reference units | live height grid, Costs, Objects footprints, water | nothing is exported | [Objects and scenario](ObjectsAndScenario.md) |
| Textures | game archives, custom library | every texture slot of Materials, Environment, Decals, Water | [Textures](TexturesAcrossSystems.md) |
| Import | containers, BdfIO | every domain, in a fixed order; the host's imported-terrain list | [Save, import, export](SaveImportExport.md) |
| Export | everything | files; for the smart path, `MarkExported` | [Save, import, export](SaveImportExport.md) |
| Undo | every shell | the dispatcher picks the shell with the newest action | [Undo](UndoAcrossSystems.md) |
| Input and overlays | every shell | event order, modals, the busy plate | [Input and overlays](InputAndOverlays.md) |

## Pages

| Page | Question it answers |
|---|---|
| [Mechanisms](Mechanisms.md) | what are all the ways one system learns about another, with every event, delegate and counter listed |
| [Data ownership](DataOwnership.md) | which system writes and reads each group of `SC2TerrainData` fields, and where the document keeps its copy |
| [Life of an edit](LifeOfAnEdit.md) | what happens between a brush stroke and everything that must follow it |
| [Height and water](HeightAndWater.md) | how the height grid, the water level and the files derived from them stay consistent |
| [Materials, masks, lighting](MaterialsMasksLighting.md) | how materials, pairs, blend masks, the bake and the export fit |
| [Textures](TexturesAcrossSystems.md) | how one texture choice travels from the picker to the exported map |
| [Objects and scenario](ObjectsAndScenario.md) | how objects, armies, symmetry, the lobby, the mission and the Lua files agree |
| [Undo](UndoAcrossSystems.md) | how the three undo mechanisms are joined, and where the join is uneven |
| [Input and overlays](InputAndOverlays.md) | who gets a mouse or key event, and what blocks it |
| [Save, import, export](SaveImportExport.md) | what each long operation reads from and writes to every system |
| [Change impact](ChangeImpact.md) | "I am changing X: what else must I check?" |

## Rules for a new interaction

1. **Prefer the existing mechanisms.** A new cross-shell effect is a host-wired event or a polled counter, never a reference from one shell to another.
2. **A change that others must see needs a signal.** Mutating `SC2TerrainData` is not a signal. A command raises `Document.Changed`; a brush stroke must bump `TerrainGeometryVersion`, invalidate the live height grid and call `Session.MarkDirty` ([Life of an edit](LifeOfAnEdit.md)).
3. **Say who owns the data** in the system document, and add the field to [Data ownership](DataOwnership.md).
4. **A derived file is regenerated from the same model** as the file it derives from ([Height and water](HeightAndWater.md)).
5. **Document the order**, because most of the bugs in this area are ordering bugs (what runs before what in a frame, in an export, in an import).
6. **Add the row to [Change impact](ChangeImpact.md)** in the same commit.
7. **Test the interaction itself**, not only each side: the minimum is a scenario that performs the edit and reads back what the other system produced ([Verification](../Process/Verification.md)).

## What was verified and what was not

The tables of events, subscribers and callers in this section come from searching the source for every subscription, invocation and call site; the field ownership table comes from a script that searches for member accesses. Behaviour at run time (timing, frame order under load) was not measured, and no scenario was run for this section.
