# Height, water and everything derived from them

Status: first draft from reading the call sites listed below at commit `17863e4`; not yet reviewed by a second reader. Read [Life of an edit](LifeOfAnEdit.md) and [Data ownership](DataOwnership.md) first.

The ground height and the water level are read by more systems than any other data. This page lists the **three different height sources**, who uses which, the **water level and who reads it**, and the rule that keeps the exported files consistent with each other.

## 1. The three height sources

| Source | What it is | Fresh when | Used by |
|---|---|---|---|
| **The runtime** (`SC2TerrainRuntime`, GPU) | the heights as the brush is editing them | always, even mid-stroke | the sculpt itself; `SampleLiveRuntimeHeight` (decals follow a stroke in progress); the terrain preview |
| **`SC2TerrainData.Heights`** (and `SC2ObjectsExport.SampleTerrainHeight`, a bilinear lookup of it) | the saved heights | only **after a stroke ends** (mouse-up copies the runtime into it) | Auto Path Node resolution at export; `SC2CostsAutoGen` ("the exact same terrain-height source Auto Path Node already uses, for consistency"); the save |
| **The live height grid** (`SC2LiveHeightGrid`) | one source-agnostic heightmap, **the same projection the export's hfield uses** (`SC2HfieldProjectionService.Project` over the merged live and imported model, plus the props that are included in the heightmap) | after its **debounced rebuild** (0.25 s) following `InvalidateLiveHeightGrid()` | Costs painting and generation, Objects placement and water-aware unit height, Reference units' buildable overlay, the mouse-height HUD, Decals (clipping surface), imported terrain included |

The consequence: **three consumers asking "what is the height here" can get three different answers during and just after an edit.** That is by design for speed; code must pick the source that matches its need (a stroke in progress, the saved state, or what the export will contain) and must not mix them within one decision.

The grid is built from the **merged live and imported terrain**, so an imported terrain object, which is not in `SC2TerrainData`, is part of what Costs and placement see. The host invalidates the grid whenever imported objects change ([Mechanisms](Mechanisms.md), section 4).

## 2. The water level

The water level and enable are two fields of the **environment header** (`WaterElevation` at offset 216, `WaterEnabled` at 48). The header exists in two places, and the code takes the first one available:

1. the **document's working header** (`SC2EnvironmentDocument.WorkingHeader`), kept current every frame by `SC2EnvironmentShell.MaybeSnapshot`, so an edited but unsaved level is seen at once;
2. `SC2TerrainData.EnvironmentHeader`, refreshed only when the user commits or saves.

Because `SC2.Core` cannot reference the session (it would be a circular assembly reference), `SC2ObjectsExport.GetWaterElevation(data, workingEnvironmentHeader)` and `IsWaterEnabled` **take the header bytes as a parameter**; callers outside `Core` pass the document's header. A caller that forgets and reads `data.EnvironmentHeader` alone sees a stale level.

### Who reads the water level

| Reader | Why | Where |
|---|---|---|
| Water plane (preview and export) | the plane's height; the plane is appended to the exported model when the environment has water and no imported terrain brings its own | `SC2WaterPreview.Ensure` every tick; `SC2WaterPlane.AppendToExportModel` |
| Water depth texture | the shore and open-water classification, from the water floor (`SC2WaterFloorSampler`: the highest surface not above the water) | `SC2WaterDepthTextureGenerator`; the export (when auto-generate is on) |
| Costs generation | which cells are under water, the shore, the soft-wall gradient | `SC2CostsAutoGen.ExtractWaterElevation` (prefers the working header) |
| Costs, five-block set | the water mask is taken from the **exported hfield** | the export |
| Collision2 | the mesh is built from a second projection with `bakeWaterElevation` set: every cell with no ground or with ground below the water is flooded up to the water Y **before gap filling** | `BuildCollision2Model` |
| Lighting bake | underwater shading: `WaterEnabled`, `WaterElevation`, `MaxWaterDepth` into the mask baker's settings | `SC2RnmBakeService` |
| Simplification | ground within a band of the water is not simplified (`SC2ShoreDetail`) so the shoreline keeps its shape | the layer builder |
| Objects | an Auto Path Node becomes a Water or Land Path Node by `terrainHeight < waterElevation`; water-aware unit height snapping for naval and amphibious units | `ResolveEffectiveType`, the Objects shell |
| Reference units | a water building is limited to cells under the water surface | `GetWaterSurface` (the host reads the header the way the water plane does) |
| Imported terrain materials | the preview environment (sun, shadow, fog) of imported objects | the terrain shell's `Update` |

`WaterEnabled` (the decorative surface) and `PlayableWater` (the Costs block set and the path graphs) are **separate settings**; `PlayableWater` is written by the Costs shell.

## 3. The rule: derived files come from the same final model

At export, the terrain model is built (layers, merged imported terrain, the background, the water plane) and then **everything derived from the ground is projected from that exact model**, in this order:

1. `terrain.bdf` is serialized from the final model.
2. **hfield**: `SC2HfieldProjectionService.Project(model, existingTerrainTypes, ...)`: the heights, plus the terrain types painted in Costs (`data.TerrainTypes`). **Water-material layers are left out of the projection** (`IsWaterMaterial`), so the water plane does not raise the hfield.
3. **Costs**: built from the painted blocks; for the five-block set the water mask comes from step 2.
4. **Collision2**: a separate projection of the same model with the excluded materials left out, the water baked in, the floor applied and the polygon budget; or the custom mesh bytes verbatim when `UseCustomCollision2` is set.
5. The water depth texture is generated from the same model's water floor.

Props that are marked "include in heightmap" are extra solids in every one of these projections and in the live grid, so the editor's preview of the ground and the exported hfield agree.

**Never project a derived file from the live grid or from the unsimplified mesh**: simplification and merging change the ground, and the game's pathing and collision must match the terrain it actually draws.

## 4. What breaks when a link is missed

| Missed | Visible symptom |
|---|---|
| `InvalidateLiveHeightGrid()` after a change of the ground | Costs paint, placement, the HUD and the buildable overlay use the old ground; a hill sculpted above the water still showed the old water-relative height |
| Passing the working header to `GetWaterElevation` | Auto Path Node and Costs use the last saved level, not the edited one |
| Props-in-heightmap signature (`RefreshHeightmapPropsSignature`) | Costs generation and placement ignore a building that should be part of the ground |
| Flooding before gap filling in Collision2 | a large lake takes the shore's height across its whole area, because gap filling propagates one boundary value across a hole in a single pass |
| `bakeWaterElevation` passed to the shared hfield | hfield, Costs and the bake would see flooded heights; it is for Collision2 only |
| Terrain types array length not `(mapX - 1) * (mapY - 1)` | the projection ignores it and writes the default type everywhere |

## 5. Change checklist

- **A new reader of ground height**: choose the source from section 1; if it is the live grid, you inherit its 0.25 s staleness; if it needs the stroke in progress, use the runtime.
- **A new thing that changes the ground** (a prop type, an import, a sculpt tool): invalidate the grid, bump the geometry version, raise `HeightBrushApplied` if it is a brush ([Life of an edit](LifeOfAnEdit.md)).
- **A new reader of the water level**: take the header from the document first (pass the bytes into `Core` code).
- **A new file derived from the ground**: project it from the final export model, after merging and simplification, and add it to the dependency expansion of [smart export](../Systems/SmartExport.md).

## 6. Tests and verification

`WaterFloorSamplerTests`, `WaterPlaneHeightTests`, `Collision2FloorTests`, `CostsNavmeshRulesTests`, `CostsDocumentBridgeTests` cover pieces. The agreement of hfield, Costs and Collision2 on one exported map is checked by exporting a map with a lake and an imported terrain and reading the three files back ([Verification](../Process/Verification.md)); no such scenario is in the repository.

## 7. Unverified

Which of the live grid's exact inputs include the background was not checked. The 0.25 s and 300 ms figures are from comments in the code.
