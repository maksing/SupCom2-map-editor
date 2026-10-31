# Lighting bake (RNM masks, shadows, point cloud)

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The game lights terrain with baked per-texel images (radiosity normal mapping, "RNM"): three slices, one per basis direction, plus a sun-occlusion alpha. This system computes them from the
terrain geometry and the sun, shows them in the viewport (RNM preview mode), bakes the point-cloud volumes of the environment, and stores the results on the pairs.

Not included: the blend mask that shares the mask files ([blend mask](BlendMask.md)); the environment settings that supply the sun and shadow colour ([environment](Environment.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Orchestration | `TerrainEditor/SC2RnmBakeService.cs` (`Bake`, `BeginBake`, `BakeBackground`, `BeginBakeBackground`, shadow height field, imported masks, world-space imported texture) |
| Rasteriser | `TerrainEditor/SC2RnmMaskBaker.cs` (`BakeMaskGroup`: per-texel basis lighting, shadow march, soft shadow blur, gap fill) |
| Point cloud | `TerrainEditor/SC2PointCloudBakeService.cs` and the baker it calls (`PointCloudBakerTests`) |
| Preview | `SC2MaterialPair.RefreshRnmPreviewTextures`, `SC2TerrainRuntime.RebuildRnmArrays`, `Resources/TerrainEditorShaders/SC2TerrainView.shader` (RNM branch) |
| UI | the Lighting Bake sub-tab of `SC2TerrainShell`; the Point Clouds environment tab |
| Settings | `SC2TerrainData.RnmBake*` (sun and shadow overrides, shadow softness), `RnmBakeGeneration` |

## 3. Data

Per pair (stored on every pair of a mask group): `RnmBakedRgb0/1/2` (RGB bytes, one array per slice), `RnmBakedShadowAlpha`. Preview textures `RnmPreviewMask0/1/2` are made from them
(slice 1's alpha takes the blend mask at that moment). `RnmBakeGeneration` on the core data counts completed bakes. The point cloud is two DXT5 volume files stored on the data. Imported terrain objects have
their own masks in the session document (`ImportedRnm`) and a world-space texture (`ImportedRnmBakedRgba`).

## 4. Flows

1. **Bake Lighting** (manual): the model of the terrain (and background, and imported objects as shadow casters) is built; a shadow height field is made; for each mask group `BakeMaskGroup` rasterises every
   triangle by its UV2, finds the sun shadow per texel (a horizon march), blurs it into a soft penumbra (the width follows the Shadow Sharpness setting and, for imported masks, a width in world units), combines the
   basis lighting, and fills gaps. The results are written to the pairs (`ApplyBake`) and `RnmBakeGeneration` is incremented.
2. **Preview**: in RNM mode the shell refreshes the preview textures when the generation changed and `SetAllPairMaterials` rebuilds the texture arrays (`RebuildRnmArrays`). A pair with no bake falls back to plain lit shading.
3. **Background**: `BakeBackground` bakes the background's pairs with its own model, with the core as shadow caster; it never wipes the imported world texture.
4. **Export**: `ExportRnmMaskByName` writes the baked slices into the mask files; a pair with no bake exports neutral lighting (RGB 128, opaque shadow). The bake never runs on export.
5. **Point cloud**: baked from the terrain (and the baked RNM light when it exists), stored on the data, written as `lighting/pcloud_up|down.win.dds` at export.

## 5. Invariants

- **Baking is manual.** An export never bakes.
- **The bake does not touch the blend mask.** It writes `RnmBaked*` only; the blend alpha of the mask files comes from `RnmMaskBytes`.
- **The sun direction is the direction to the sun** (pointing up into the sky), not the direction light travels.
- **Slices are weighted so a flat normal gives the baked brightness** (the same calibration the baker uses), so the preview shows what the game shows.
- **All pairs of a mask group share one result and one resolution**; the first pair's size is representative.
- **The preview refreshes on `RnmBakeGeneration`**: code that changes baked data without incrementing it will not refresh the viewport.

## 6. Editor and build differences

- The bake runs on a worker thread behind the busy overlay in both (the heavy part has no Unity calls; the main-thread part reads textures). Reading and writing the results is the same code.

## 7. Extension points and how to change it

- **A new lighting term**: in `SC2RnmMaskBaker` (pure) with a test, then the preview shader's RNM branch so the viewport matches.
- **A new bake setting**: a field on `SC2TerrainData`, the document and bridges, the Lighting Bake panel, the call into the baker.
- **A new caster or receiver** (a new kind of object that casts shadow): add it to the shadow height field builder (`BuildShadowHfield`).

## 8. Editing, verification and tests

**How to edit it safely.** Change the baker and the preview shader together. After a change, bake a small map and look at the RNM preview next to the exported mask files.

**Tests today**: `ShadowMarchTests`, `ShadowCastingTests`, `ShadowSoftnessTests`, `SunShadowMathTests`, `RnmGapFillTests`, `ImportedRnmBakeTests`, `ImportedWorldRnmBakeTests`, `PointCloudBakerTests`. The service and the preview have no unit test.

**Tests a change must add.** A lighting rule: a test of `SC2RnmMaskBaker` on a synthetic model (a wall casting a shadow onto a plane). A setting: the round trip. A preview change: a scenario that bakes, applies the preview and reads the pixels.

## 9. Known limitations and unverified behaviour

- The match of the viewport to the game's shading (glow, specular, dynamic shadows) is approximate and was not verified against the game.
- The preview was found not to refresh after a bake unless the shell re-applies the preview mode and the generation changed; this is how it works, not a bug, but a bake called from outside the shell must do the same.
- Bake time and memory on the largest masks (up to 2048 x 2048 per group) were not measured here.

## The point-cloud model and what is known about the vanilla files

`SC2PointCloudBaker` bakes `pcloud_up` and `pcloud_down` (256 x 256 x 8, over the box `PointCloudOrigin` and `PointCloudSize` of the environment header; the layout and file details are in [Texture files](../Formats/TexturesAndMasks.md)). **Terrain only: props do not occlude here**, although in many vanilla maps they do. Per voxel, with its centre at `origin + ((i + 0.5) / 256 * size.x, (k + 0.5) / 8 * size.y, (j + 0.5) / 256 * size.z)`:

- **Inside the ground** (the terrain surface is above the voxel): `down.a = 0`. Otherwise `down.a` is 255, or 0 when the terrain shadows the point from the sun (a hard ray along the direction to the sun, range 400 units by default).
- **`up.rgb`** is the sky colour times the sky visibility (a cos-squared horizon model averaged over 16 azimuths, range 120 units); ground voxels keep the plain sky colour so trilinear filtering never darkens a unit standing on the ground.
- **`down.rgb`** is `GroundReflection` times the top-down ground light blurred with a sigma that grows with the slice index (`BlurTexels` times the index), plus `SkyInGround` times `up`. The ground light is the **map's baked RNM lighting** (the mean of the three basis maps) when it exists, a flat mid value otherwise; bake the RNM first for the real one.

What the vanilla files show, from reverse engineering the `pcloud_up` and `pcloud_down` volumes of the vanilla maps (the game reads them in `unit.hlsl` to light units from the map; a missing file falls back to the game's default 8 x 8 x 2 black DXT1 volume):

- **Verified**: the voxel centre convention is exact; a voxel is solid (`down.a = 0`) where the terrain surface exceeds its height; `down.a` is a hard sun-visibility ray march over the terrain height field (94 to 99% voxel agreement on maps whose terrain was not edited after the bake; the rest is explained by a per-map terrain offset of 1 to 15 units at bake time); `up` is flat sky colour times sky visibility (correlation up to 0.97); the grain in the files is DXT dithering, not Monte Carlo.
- **Not derivable from the map data**: the colour and intensity of `up` (set per map by the artist; 0.5 times (0.75, 0.88, 1.0) on 11 maps and the same hue at other brightness on many), and the scales of `down.rgb`.
- **`down.rgb`** follows the terrain's **baked RNM lighting**, not albedo and not a hemisphere gather or clearance alone: a top-down ground-light map blurred with a radius that grows with clearance. A model of the mean of the three basis maps, blurred per slice, plus the sky visibility and a constant per channel reaches an R-squared of 0.85 to 0.89 on the best maps and 0.14 to 0.38 on the worst, with a median around 0.5 to 0.6 over 28 maps. The per-map scale constants are not derivable.
- The baker that made the vanilla files is in the game developer's own level editor, which is not available here; the numbers above are fits, not the original algorithm. The scratch analysis tools were kept outside the repository and may be gone; they can be rebuilt from the game data.

## 10. Related documents and review

[Blend mask](BlendMask.md), [Water](Water.md), [Background terrain](BackgroundTerrain.md), [Environment](Environment.md), [Export pipeline](ExportPipeline.md).
Interactions with other systems: [Materials, masks, lighting](../Interactions/MaterialsMasksLighting.md), [Height and water](../Interactions/HeightAndWater.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
