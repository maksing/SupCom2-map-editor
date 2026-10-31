# Materials, pairs and the shared material list

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P1. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

A **material** is a named set of textures and a colour; a **pair** joins two materials with the settings of one terrain layer of the game. Every triangle of the terrain
is assigned to a pair, and the pair's blend mask decides how much of its second material shows. This system covers the data, the Materials and Pairs tabs, the presets, the
link between pairs and materials, and the single material list that the core and the background share.

Not included: assigning pairs to triangles with a brush ([brushes](Brushes.md)), the blend mask ([blend mask](BlendMask.md)), the lighting of a pair ([lighting bake](LightingBake.md)),
how textures are found and picked ([textures](Textures.md)), export of the slots ([export](ExportPipeline.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Data | `Scripts/Core/SC2TerrainMaterialModel.cs` (`SC2Material`, `SC2MaterialPair`), `SC2MaterialPairLinker.cs`, `SC2SharedMaterials.cs` |
| UI | `Scripts/TerrainEditor/SC2MaterialsPairsMode.cs` (`SC2MaterialsMode`, `SC2PairsMode`), `SC2MaterialPresets.cs` |
| Document | `MapDocument/Domain/Terrain/SC2MaterialDocument.cs`, `SC2SetMaterialNameCommand`, `SC2SetMaterialTextureCommand`, `SC2SetMaterialPairLinkCommand`, `SC2SetMaterialPairNameCommand` |
| Preview | `SC2TerrainRuntime.SetAllPairMaterials`, `SC2TerrainShell` (builds the pair data for the runtime) |
| Resolving textures | `Scripts/SC2TerrainMaterialFactory.cs` |

## 3. Data

**`SC2Material`**: `Id` (stable GUID), `Name`, `Color`, `Diffuse`/`Normal`/`Mask` textures, per-slot override paths (`DiffuseOverridePath`, ...) and "copy into the map on export" flags.

**`SC2MaterialPair`**: `Id`, `Name`; the two materials as `Mat1Index`/`Mat2Index` **and** `Mat1Id`/`Mat2Id` (the id is the truth, the index is a cache that readers use); `TilingScale`,
`SimplifyRatio`, the mask group name `RnmMaskName` (default `mask01`; the background's first pair uses `background`), the blend mask bytes and baked lighting arrays, `ShaderFlags`
(a list of key and on/off, plus `AlphaTestThreshold`), `State`, `TextureScroll`, `ShaderFile`/`ShaderPrefix`, and the ramp and environment texture paths with their source tags.
`GlobalUvScale` of the map data replaces a pair's own tiling when the global option is on (`EffectiveUvTilingScale`). The editor allows at most 72 pairs.

**The shared list.** The core's `Materials` is the list that is edited. The background's `Materials` holds the very same `SC2Material` objects (`SC2SharedMaterials`), so a pair of
either surface can take any material and an edit shows on both.

## 4. Flows

- **Add a pair** (`+ New Pair`): a pair with the default flags (`DefaultFlags`), materials 0 and 1, and **the mask group of the surface's first pair**, so it joins the group the blend brush paints.
- **Pick a material for a pair**: the picker lists the surface's material list; the choice writes `MatNId` and the index.
- **Delete a material**: removed from the core list, then `SC2MaterialPairLinker.RelinkPairsToMaterials` re-resolves every pair's index from its id; a pair that used it becomes unassigned for that slot.
  The background's list and indices are brought back in line in the next frame (`SC2SharedMaterials.Sync`).
- **`SC2SharedMaterials.Sync`** (every frame and before saving): the first time for a background (a map saved before the lists were shared, or a freshly generated background) its own
  materials that the core does not know are appended to the core list; after that the background's list is rebuilt as a copy of the core's, and the background pairs are re-pointed by id.
- **Presets** (`SC2MaterialPresets`): a preset sets pair-level fields only (shader file and prefix, state, flags, texture scroll); it never touches a material's textures.

## 5. Invariants

- **The id is the truth, the index is derived.** Never shift indices by hand when the material list changes; call `RelinkPairsToMaterials`.
- **A material is one object on both surfaces.** The background's list must contain the core's objects, not copies; anything that replaces `Materials` with copies (an undo snapshot) is repaired by `Sync`.
- **A background material that the core does not know is merged once**, and only for a background that has not been joined yet (`MaterialsSharedWithCore`); the core list is authoritative afterwards,
  so an undone material is not resurrected from the background.
- **A new pair joins the first pair's mask group** of its surface; one blend mask is painted per group and shown over every pair of the surface.
- **A pair needs both materials to render as intended**; an unassigned slot falls back to the pair's colour.
- **The rename and texture commands act on the core's document only**; the background has no document entry of its own for materials.

## 6. Editor and build differences

- Texture fields use the runtime picker in a build (the texture browser); a `file:` or `archive:` tag is internal and never exported as is ([export](ExportPipeline.md)).
- Confirmations for deleting a material use `EditorUtility.DisplayDialog`, which in a build always agrees; deletion is undoable.

## 7. Extension points and how to change it

- **A new pair option**: the field on `SC2MaterialPair`, its document and bridges ([Editing](../Process/Editing.md), section 4), the Pairs panel, the exporter (a material flag or a texture slot), the preview if it is visible.
- **A new shader flag**: add it to the default flag list of the Pairs panel and to the exporter's flag handling; a pair that lacks the key is treated as off.
- **A new preset**: a row in `SC2MaterialPresets` (pair-level fields only).

## 8. Editing, verification and tests

**How to edit it safely.** When a change touches the lists, relink by id. When a change touches the background's materials, remember the shared list. A list replacement in undo code needs a `Sync` afterwards.

**Tests today**: EditMode `MaterialPairStableIdTests` (delete a material, relink by id), `SharedMaterialsTests` (merge, cross use, delete, copies restored); standalone `PairMaterialSettingsTests`, `PairLockSetTests`,
`TerrainCommandTests`, `TerrainExpandedCommandTests`, `TerrainDocumentBridgeTests`.

**Tests a change must add.** A change to linking or the shared list: a case in `SharedMaterialsTests` or `MaterialPairStableIdTests`. A new pair field: the round-trip tests. A UI change: a scenario.

## 9. Known limitations and unverified behaviour

- A pair of the background created before the mask-group fix keeps the default group and exports an unpainted blend; the export warns about it, nothing renames it.
- The merge of an old map's background materials into the core list was exercised on synthetic data only.
- Behaviour with the maximum of 72 pairs was not measured.

## 10. Related documents and review

[Blend mask](BlendMask.md), [Brushes](Brushes.md), [Textures](Textures.md), [Export pipeline](ExportPipeline.md), [Background terrain](BackgroundTerrain.md).
Interactions with other systems: [Materials, masks, lighting](../Interactions/MaterialsMasksLighting.md), [Textures](../Interactions/TexturesAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
