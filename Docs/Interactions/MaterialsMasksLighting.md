# Materials, pairs, blend masks, lighting and the background

Status: first draft from reading the code and the system documents at commit `17863e4`; not yet reviewed by a second reader. Read [Materials and pairs](../Systems/MaterialsAndPairs.md), [Blend mask](../Systems/BlendMask.md) and [Lighting bake](../Systems/LightingBake.md) first.

These five systems edit different data but all of it ends in **one set of exported files per mask group**, so a change in one is visible in the others. This page follows the chain from a material to an exported lighting file and lists where the seams are.

## 1. The objects that tie them together

| Object | Owned by | Shared with |
|---|---|---|
| `SC2Material` list (`Data.Materials`) | Materials | the **background** (its own list holds the very same objects); every pair of either surface can use any material |
| `SC2MaterialPair` (`Data.Pairs`) | Materials | holds the settings the others use: two material references (by stable id), the mask group name `RnmMaskName`, shader flags, tiling, simplify ratio, ramp and environment textures; **and** the mask bytes, **and** the baked lighting |
| **Mask group** (`RnmMaskName`) | Blend mask | all pairs with the same name share one blend mask and one set of lighting files |
| Blend mask (`RnmMaskBytes` on each pair of a group; layer stacks in `BlendLayerStacks`) | Blend mask | the bake (reads the resolution; does not change the blend), the export (`_1` alpha), the preview |
| Baked lighting (`RnmBakedRgb0/1/2`, `RnmBakedShadowAlpha` on each pair of a group) | Lighting bake | the preview (RNM mode), the export (`_0`, `_1`, `_2` RGB and `_0` alpha) |
| `Data.TrianglePairIndex` | the paint modes | the simplification and layer builder, the bake (which triangles belong to which group), the pair outline |
| The background (`BackgroundTerrainData`, a nested `SC2TerrainData`) | the Background tab | materials (shared list), blend masks (own groups, default `background`), the bake (own `BakeBackground`), the export (own layers, files prefixed `bg_`) |

## 2. How they interact

**Materials to background.** The background's material list is the core's. After an undo, a load or a deleted material the lists drift, so `SC2SharedMaterials.Sync(data)` runs **every frame** in `SC2TerrainShell.Update` and, if it changed anything, calls `SC2BackgroundRuntimeSync.MarkDirty()`, which makes the shell refresh the background runtime. A terrain undo replaces `Data.Materials` and `Data.Pairs` with the snapshot's lists and then marks the background dirty itself.
The background has no entries in the document's material or pair lists; its pairs are saved with it. The background data carries a non-serialized flag `MaterialsSharedWithCore` so that code knows its list is not its own.

**Materials to blend masks.** A new pair inherits `Data.Pairs[0].RnmMaskName`, so a new pair joins the first pair's mask group instead of starting an unpainted one. The preview shows the **first pair's group** blend over **every** pair of the surface, but the export writes **one file set per group name**; a pair in another group exports an unpainted blend. Keep every pair of a surface in one group (the exporter warns about background pairs that are not).

**Paint Materials to blend masks.** Assigning pairs to cells changes where each pair's border is, so after the pair map is read back from the GPU, `OutlineNewlyPaintedCells` updates the pair-outline layers of the blend mask (`SC2PairOutlineUpdater`: draws the outline of the cells that newly became a pair's, removes outline that is no longer along a border, only around the changed cells, on a worker task). Locks (`SC2PairLockSet`) are honoured by both the pair brush and the blend brush.
(See [Life of an edit](LifeOfAnEdit.md), section 5.)

**Blend masks to lighting bake and the files.** The blend mask is the **alpha** of the mask; the bake writes only RGB and the shadow alpha. In the exported files `_1` carries the **blend** alpha, `_0` the **shadow** alpha, `_2` is opaque ([Texture files](../Formats/TexturesAndMasks.md)). The preview texture of slice 1 takes the blend mask **at that moment**, so painting the blend after a bake changes slice 1's alpha without re-baking.
`FlushActiveBlendGroup` is the only writer of the composite into the pairs, so the mask the exporter and the bake read is always the flattened composite, never a layer.

**Lighting bake to everything else.** The bake reads the geometry (the hfield projection and the layer model), the pair assignment, the **environment's sun and shadow colour** (unless the bake has its own overrides), the water settings and the shadow exclusions (`SC2ShadowCasting`, stored as switched-off caster and receiver pairs). It writes `RnmBaked*` into every pair of each group and increments **`RnmBakeGeneration`**. The terrain shell compares the generation with the one it last rendered and, if different, refreshes the preview textures and calls `SetAllPairMaterials`; the terrain runtime also keys its mask cache on it.
**Changing baked data without incrementing the generation leaves the viewport stale.**

**Export.** `ExportRnmMaskByName` finds the first pair of the group with a mask of the right size and writes the three files. For each of the three RGB arrays and the shadow alpha it **uses the baked array only if its length matches the mask's size** (`n * 3` and `n`); otherwise it silently writes **neutral lighting** (RGB 128) and an opaque shadow. A bake is therefore lost at export, without a warning, if the mask resolution changed after the bake.
The bake never runs on export.

## 3. The seams where things go wrong

| Seam | What happens | Where it is handled |
|---|---|---|
| Undo replaces material and pair lists | the background keeps lists of the old objects | `SC2SharedMaterials.Sync` per frame; `SC2TerrainUndoStack.Apply` marks the background dirty |
| Undo and the mask bytes | the pairs' `RnmMaskBytes`, `RnmMaskTexture` and the baked RGB are **shared by reference** in a terrain snapshot, not copied (the active blend pair's render texture is about 16 MB) | the blend mask has its own opaque undo channel that is captured only for blend steps ([Undo](UndoAcrossSystems.md)) |
| Mask resolution vs bake size | export falls back to neutral lighting | change the resolution, then bake again |
| A pair in another group | exports an unpainted blend | group inheritance for new pairs; the export warning for the background |
| Seam strokes | a blend dab near the core's border paints both surfaces' masks, with **one undo label** (`Paint Blend (core + background) #N`) so Undo walks both back | `SC2SeamReach` and the stroke checkpoints of each surface |
| Background mask name equal to the core's | both exist, their **files must not**: the background's are prefixed `bg_` | the export |
| Bake of the background | it bakes its own pairs with the core as shadow caster and never wipes the imported world texture | `BakeBackground` |
| Imported terrain objects | their masks live in the document (`ImportedRnm`) with a world-space baked texture and are baked separately | [Lighting bake](../Systems/LightingBake.md) |
| Materials change textures | a texture change goes through the picker, resolves a game path and sets source tags | [Textures](TexturesAcrossSystems.md) |

## 4. Order of operations that matters

- **Bake after the blend is final or accept that slice 1's alpha follows the blend later** (it does, because the preview and the export read the blend at that moment).
- **Change a mask resolution before the bake**, not after.
- **Keep the shadow exclusions in step with what exists**: they are stored as switched-off pairs; removing an imported object leaves harmless stale entries.
- **Export reads the baked arrays as they are on the pairs at that moment**, so autosave or a save does not bake.

## 5. Change checklist

| If you change | Also check |
|---|---|
| a field of `SC2MaterialPair` | the terrain snapshot clone in `SC2TerrainUndoStack.Capture` (it lists the fields explicitly and "predated the RNM bake and was never extended" once); the document pair; the background's shared pairs; the export |
| how a mask is stored or flushed | the bake (reads the size), the exporter (`n * 3` length check), undo (shared-by-reference arrays), the background's `bg_` files |
| the bake output | `RnmBakeGeneration`, the preview's `RebuildRnmArrays`, the export fallback |
| the group rules | the export warning, the group inheritance of new pairs, the preview's "first pair's group" |
| the shared material list | `SC2SharedMaterials.Sync`, undo, load, the deletion of a material |

## 6. Tests and verification

`BlendMaskLayerTests`, `PairOutlineMathTests`, `PairOutlineUpdaterTests`, `PairLockSetTests`, `SeamReachTests`, `ShadowCastingTests`, `ShadowMarchTests`, `ShadowSoftnessTests`, `RnmGapFillTests`, `ImportedRnmBakeTests`, `MaterialPairStableIdTests` and `SharedMaterialsTests` (EditMode) cover the pieces. The chain (paint, bake, change mask size, export, read the files back) has no test; the verification is to export a small map and parse the three files of one group ([Verification](../Process/Verification.md)).

## 7. Unverified

Whether a mask resolution change really discards the bake in the editor UI (rather than being prevented) was not exercised; the export-side length check is from the code.
