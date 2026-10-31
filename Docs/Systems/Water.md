# Water

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The water surface of a map: the live plane in the viewport, the exported `Water` material of `terrain.bdf`, the **waterDepth texture** that drives the shoreline foam, and the Water tab of the terrain
editor. Included: the plane, the material slots, the depth texture (generated or imported) and its preview, the shore treatment in simplification.

Not included: the water **settings** in `info.bdf` (colour, waves, elevation, depth falloff), which are the [environment](Environment.md) Water tab; the playable-water costs set ([costs](Costs.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Plane and material | `Scripts/SC2WaterPlane.cs` (`SC2WaterPreview.Ensure` every tick, `AppendToExportModel`, `BuildWaterMaterial`, `PlaneHeight`), `SC2WaterMaterialFactory.cs`, `SC2WaterRenderController.cs`, `Resources/SC2Water.shader` |
| Depth texture | `TerrainEditor/SC2WaterDepthTextureGenerator.cs` (`Generate`, `BuildMaskTexture` for the viewport, `BuildPreviewTexture` for the panel image), `SC2WaterFloorSampler.cs` |
| Shore | `TerrainEditor/SC2ShoreDetail.cs` (used by the simplifier) |
| UI | the Water sub-tab of `SC2TerrainShell` (`DrawWaterPanel`): plane size, depth settings, Regenerate Preview, Import waterDepth texture |
| Data | `SC2TerrainData.WaterTexturePaths` (the material's slots), `WaterDepthAutoGenerate` and the depth settings, `ResetWaterDepthSettings` |

## 3. Data

`WaterTexturePaths` holds the slot paths of the exported `Water` material; slot 6 is the waterDepth (Mask1). With `WaterDepthAutoGenerate` on, slot 6 is replaced at export by the generated
texture; with it off, an imported image is used (the Import button switches auto-generate off). The water level and enable come from the environment header (`WaterEnabled`, `WaterElevation`). The generated mask is
kept in `SC2WaterPreview` while the map is open; it is not part of the saved data.

## 4. Flows

- **Viewport plane**: `SC2WaterPreview.Ensure(data, session, ...)` runs every tick from `SC2TerrainShell.Update` and creates, updates or destroys the plane as the environment changes. On opening a map with auto-generate the
  mask is rebuilt once the terrain is live.
- **Depth texture generation** (`Generate`): samples the water floor (`SC2WaterFloorSampler`: the highest surface not above the water, so the river under a bridge counts as water), classifies land (alpha 0), shore
  (low alpha) and open water (255) with the configured margin, falloff, minimum alpha, land bleed and border handling, and fills RGB with the configured water colour.
- **Panel preview**: `BuildPreviewTexture` approximates the foam against water colour blend; the panel draws it flipped in V (the viewport and the export are unaffected).
- **Export**: the water plane is appended to the model when the environment has water and no imported terrain already carries water; the waterDepth file is written as `<Map>.waterDepth.dds`; an imported depth texture or other
  slot texture is copied into the map and the slot rewritten to its map path. Slots that cannot be resolved are emptied with a warning.
- **Simplification** keeps the ground within a band of the water level unsimplified (`SC2ShoreDetail`), so the shoreline keeps its shape.

## 5. Invariants

- **Alpha is the depth**: 0 is land, a low alpha the shoreline (strong foam), 255 open water; RGB carries no per-pixel signal.
- **The generated texture covers the "water depth area" of the environment**, not the map; an imported image covers the same area.
- **The slot 6 default path is the map's own waterDepth file** even when nothing wrote it; unresolved slots are emptied, not left pointing at a missing file.
- **Imported or library textures never leave a machine-local path in the map**: they are copied and rewritten ([export](ExportPipeline.md)).
- **A map without playable water still can have water**: `WaterEnabled` (decorative) and `PlayableWater` (costs) are separate settings.

## 6. Editor and build differences

- None specific; the Import waterDepth button uses the runtime file dialog in a player.

## 7. Extension points and how to change it

- **A new depth setting**: the field and its reset, the panel, the generator, and the stored default.
- **A new water texture slot**: the slot's default in `WaterTexturePaths`, the export copy loop, and the final path sweep.

## 8. Editing, verification and tests

**How to edit it safely.** Change the classification in the generator and its panel preview together. Check export with auto-generate on and with an imported image. Keep viewport and export independent of the panel's flipped preview.

**Tests today**: `WaterFloorSamplerTests`, `WaterPlaneHeightTests`, `Collision2FloorTests`. The generator and the plane have no unit test; export scenarios (`WaterExportTest`) exist outside the repository.

**Tests a change must add.** Classification rule: extract the per-texel decision and test it (land, shore, open water, bridge). Slot handling: a scenario that exports with each kind of slot and reads the material back.

## 9. Known limitations and unverified behaviour

- The preview image approximates the game's foam; the real look in the game was not compared.
- A map opened from an archive may have a waterDepth that exists only in the game's files; the editor regenerates when auto-generate is on.
- Water reflection was removed from the preview; only the sun's specular remains.

## 10. Related documents and review

[Environment](Environment.md), [Costs](Costs.md), [Simplification](Simplification.md), [Export pipeline](ExportPipeline.md), [Lighting bake](LightingBake.md).
Interactions with other systems: [Height and water](../Interactions/HeightAndWater.md), [Data ownership](../Interactions/DataOwnership.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
