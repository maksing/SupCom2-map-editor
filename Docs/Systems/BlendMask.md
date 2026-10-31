# Blend mask: painting, layers, seam painting and the mask files

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

The blend mask says, per pixel of the map, how much of a pair's second material shows over its first. This system covers painting it (Paint tab, Blend sub-mode), its editing
layers, the outline tool, painting across the core/background seam, undo, saving, and the files the exporter writes from it.

Not included: assigning pairs to triangles ([brushes](Brushes.md)), the baked lighting that shares the mask files ([lighting bake](LightingBake.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Layer model and math | `Scripts/Core/SC2BlendMaskLayers.cs` (`SC2BlendMaskLayer`, `SC2BlendMaskLayerStack`, `SC2BlendMaskLayerMath`) |
| Runtime | `TerrainEditor/SC2TerrainRuntime.BlendLayers.cs` (layer render textures, composite), `SC2TerrainRuntime.cs` (`BlendRT`, `SetActiveRnmMaskGroup`, `FlushActiveBlendGroup`) |
| Painting | `TerrainEditor/SC2PaintMode.cs` (`HandleRnm`, `CollectBlendTargets`, `EnsureBlendStrokeCheckpoint`), `Resources/TerrainEditorShaders/SC2PaintBrush.shader`, `SC2BlendComposite.shader` |
| Outline | `SC2PaintMode.OutlinePairBorder`, `Core/SC2PairOutline*` (math, updater) |
| Undo | `SC2PaintMode.CaptureSurfaceBlendState(bool background)`, `SC2TerrainCombinedUndo.RecordRnmBlendCheckpoint`, `SC2TerrainShell` |
| Document | `MapDocument/Domain/Terrain/SC2RnmDocument.cs`, `SC2BlendDocument.cs`; `SC2TerrainLegacyBridge.AttachBlendLayers` |
| Seam | `Scripts/Core/SC2SeamReach.cs` |
| Export | `SC2TerrainExporter.ExportRnmMaskByName`, `WriteRnmMaskFiles` |

## 3. Data

- **Mask group**: pairs that share a `RnmMaskName` share one mask. The mask is `RnmMaskBytes` on each of those pairs: 2048 x 2048 RGBA, RGB 128, **alpha = blend** (0 first material, 255 second).
  It is the **flattened composite**; the shader and the exporter read only this.
- **Layer stack** (`SC2TerrainData.BlendLayerStacks`, one per group name): bottom to top; a layer stores two bytes per pixel, **value** (R) and **coverage** (G), so an untouched pixel can be told from one
  painted to 0. Layer arrays are treated as immutable once published; an edit builds a new array so undo snapshots share arrays instead of copying 8 MB per layer.
- A group without a stack (a map from before layers) gets a one-layer base stack built from its existing mask on first use.
- The runtime keeps one `BlendRT` per surface and shows **one active group at a time**; each layer of the active group has its own render texture while the group is active.

## 4. Flows

- **Selecting the group**: `SetActivePairForBlend(pairIdx, data)` makes the group of that pair live (`SetActiveRnmMaskGroup`): the old group is flushed first, the new one is loaded into the render textures from its stack.
- **A dab** (`HandleRnm`): `CollectBlendTargets` finds the surfaces the brush reaches (`SC2SeamReach`: the core, the background, or both near the seam); for each, `EnsureBlendStrokeCheckpoint` records the undo state once
  per stroke and makes that surface's group live; `PaintBlend` draws into the **active layer**; after all symmetry slots the layers are flattened into `BlendRT` once per tick.
- **End of a stroke (mouse up)**: every surface the stroke touched is flushed: the active layer is read back into a **new** array, the composite is read into every pair of the group (`FlushActiveBlendGroup`), and the background
  runtime is refreshed.
- **Undo of a stroke**: both surfaces of a seam stroke share one label (`Paint Blend (core + background) #N`) so Undo and Redo walk both back together; each snapshot is of its own surface.
- **Outline**: paints a band of the second material along a pair border into an "Outline" layer of the group, and removes the outline where borders have moved; runs on a worker task and is applied on the next frame.
- **Export**: `ExportRnmMaskByName` finds the first pair of the group with a mask of the right size and writes `<group>_0/_1/_2.win.dds`; `_1` carries the blend alpha. The background's group files are prefixed `bg_`.

## 5. Invariants

- **The composite and `RnmMaskBytes` never diverge**: only `FlushActiveBlendGroup` writes the composite into the pairs.
- **The group painted on a surface is the group of the first pair of that surface** (`_rnmPair` is clamped to the pair count); the preview shows that one blend over **every** pair of the surface, but the export writes
  one file per group name. A pair in another group exports an unpainted blend. Keep every pair of a surface in one group (a new pair does this automatically; the exporter warns about background pairs that do not).
- **Layer arrays are replaced, never mutated**, so undo snapshots can share them.
- **Erase** on the base layer paints value 0 (the first material); on a higher layer it takes the layer's own paint away so the layers below show through. The layer shader uses straight-alpha "over", so a soft brush
  does not erode the existing layer value.
- **A seam stroke touches the surfaces the brush reaches, no more**: a stroke deep inside the core must not change the background and the other way round.
- **A blend mask file of the background never shares a name with the core's**, even if both groups are called the same.

## 6. Editor and build differences

- The flush on mouse up and before save works in both; reading render textures back needs the GPU, so the scenarios that cover it run in Unity (headless in the editor or in a player).

## 7. Extension points and how to change it

- **A new brush option**: the state in `SC2PaintMode`, the shader mode in `SC2PaintBrush.shader`, the call into `PaintBlend`; keep it applying to every symmetry slot and every surface target.
- **A new layer operation** (merge, duplicate, reorder): in `SC2BlendMaskLayerStack` (pure, with a test), then the panel action; every structural edit records an undo checkpoint first.
- **A new saved layer property**: the layer class, the document layer entry (`SC2RnmDocument`), the serializer with a default, the bridge.

## 8. Editing, verification and tests

**How to edit it safely.** Do not write into a layer array in place. After touching painting code, test a seam stroke with undo and redo on both surfaces and a save and load of a layered mask.

**Tests today**: `BlendMaskLayerTests` (composite, merge, base from an old mask), `PairOutlineMathTests`, `PairOutlineUpdaterTests`, `SeamReachTests`, `PairLockSetTests`, `StampGestureTests`, `BrushPresetsTests`,
`TerrainDocumentBridgeTests`. The painting code and the runtime have **no unit test**; they were checked with scenarios (`SeamBlendTest`, `LayerTest`, `LayerEraseTest`, `LayerFuzzTest`) that live outside the repository.

**Tests a change must add.** Layer math: a unit test in `BlendMaskLayerTests`. A save format change: the round trip including a mask saved before layers. Painting or undo: a scenario that paints, undoes and redoes
on both surfaces and compares the alpha of every pair's `RnmMaskBytes`, plus an export whose `_1` file is read back.

## 9. Known limitations and unverified behaviour

- In a real map with several pairs and different group names, only the first pair's group is paintable; this is a limitation of the single `BlendRT` per surface.
- The relation of the exported blend to what the game shows was not checked in the game.
- Memory: each layer is 8 MB on the CPU and 8 MB of video memory while its group is active; many layers were not measured.

## 10. Related documents and review

[Materials and pairs](MaterialsAndPairs.md), [Brushes](Brushes.md), [Lighting bake](LightingBake.md), [Background terrain](BackgroundTerrain.md), [Export pipeline](ExportPipeline.md).
Interactions with other systems: [Materials, masks, lighting](../Interactions/MaterialsMasksLighting.md), [Life of an edit](../Interactions/LifeOfAnEdit.md), [Undo](../Interactions/UndoAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
