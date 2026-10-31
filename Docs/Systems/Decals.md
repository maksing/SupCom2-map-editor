# Decals

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Decals are textures projected onto the terrain and imported models (`decals.bdf`): placed from an atlas of images, clipped against the surface, exported as geometry. This system covers the atlas, the placements, the preview, the
projection onto surfaces, the import of an existing `decals.bdf`, and the export.

Not included: the terrain model decals are clipped against ([export pipeline](ExportPipeline.md) builds it), the texture picker ([textures](Textures.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Shell | `Scripts/Unified/SC2DecalsShell.cs` (the panel, placement, preview, an atlas editor modal), `SC2GameViewEditorUI.Decals.cs` |
| Document | `MapDocument/Domain/Decals/` (`SC2DecalsDocument`, `SC2DecalAtlasEntry`, `SC2DecalPlacement`, `SC2DecalsLegacyBridge`, `SC2DecalsValidator`, `SC2ReplaceDecalsStateCommand`, `SC2SetDecalsAtlasPathCommand`) |
| Geometry | `Domain/Decals/SC2DecalGeometryBuilder.cs` (clip pipeline), `SC2DecalGrouping.cs`, `SC2DecalPreviewDiff.cs`, `SC2DecalTerrainModelSurface.cs`, `Scripts/Unified/SC2DecalSurfaceProvider.cs`, `Scripts/SC2DecalProjectionSurface.cs` |
| Export and import | `Scripts/Unified/SC2DecalsExportService.cs`, `Scripts/SC2DecalsBdfImporter.cs` |
| Shader | `Resources/SC2Decal.shader` |

## 3. Data

All decal state lives in `SC2MapDocument.Decals` (the shell edits only the document): atlas entries (name, rectangle in the atlas, default world size and depth), placements (id, atlas entry, position, surface normal, rotation,
size, projection depth, height offset, align-to-surface, enabled, symmetry group and slot), the diffuse and normal atlases as **content-hashed blobs** with their source tags and "copy into the map" flags, `KeepOriginalBakedLayer`, and for an imported map the
original `decals.bdf` as a blob. The atlas path fields hold a game path or are filled in at export.

## 4. Flows

- **Placing**: a click projects onto the live height grid or an imported model; a placement is created from the selected atlas entry; with symmetry on, mirrored placements are created in a group. Every change is a document command.
- **Preview**: `EnsurePreview` builds the clipped geometry of the placements against the surface provider; `SC2DecalPreviewDiff` limits a rebuild to changed placements; the cache is invalidated when the terrain geometry changed
  (`SC2TerrainShell.TerrainGeometryVersion`).
- **Atlas pick**: the picked texture is stored as bytes in a blob; its source tag remembers an archive origin so a vanilla atlas can be referenced by game path instead of copied.
- **Export** (`SC2DecalsExportService.Build`): with no placements the original is kept (or an empty stub is written, as vanilla maps ship one); otherwise the geometry is clipped against **the final terrain model of the export**, the atlas path
  is the game path (vanilla source, not forced) or `maps/<Map>/decals/decals_*.dds` (a copy), an optional original baked layer is merged (only if it uses the same atlas), the spatial tree is rebuilt, and the bdf and atlases are written.
- **Import** (`SC2DecalsBdfImporter`): a loose `decals.win.bdf` can be imported as a merged shaded mesh.

## 5. Invariants

- **Decals are clipped against the exported surface**, so they lie on what the game draws.
- **An atlas from a vanilla archive is referenced, not copied**, unless its box says copy; custom or generated atlases are copied.
- **A kept original baked layer needs the same atlas as the authored decals**; a different atlas is refused with an error.
- **Placement and atlas edits are document commands** and undo through the shared history; terrain edits invalidate the preview but do not move placements unless re-projected (`ReprojectAlignedDecalsInRadius`).
- **A map with no decals still exports a `decals` file stub** when the original had one (vanilla convention).

## 6. Editor and build differences

- The shell is plain GUI and runs identically; the atlas editor is a modal overlay, not a window, so it works in a player.

## 7. Extension points and how to change it

- **A new placement property**: the placement class, the document serializer default, the geometry builder, the panel.
- **A new surface source for clipping**: a surface provider implementing the same collection contract.

## 8. Editing, verification and tests

**How to edit it safely.** Keep the clip against the final terrain model. After changing geometry code, compare triangle counts of a known map. After changing the atlas handling, export once with a vanilla atlas and once with a custom one.

**Tests today**: `DecalsAuthoringTests`, `DecalsCommandTests`, `DecalsDocumentBridgeTests`, `DecalGroupingTests`, `DecalPreviewDiffTests`, `DecalTerrainModelSurfaceTests`. The shell and the export service have no unit test.

**Tests a change must add.** A geometry rule: a test with synthetic triangles. A document field: the round trip. An export rule: a scenario that exports and reads `decals.bdf` back.

## 9. Known limitations and unverified behaviour

- Vanilla maps ship 15 to 40 thousand decal triangles in total (a comment in the export service); the editor's limit and behaviour beyond that were not measured here.
- The look of clipped decals in the game was not compared.

## 10. Related documents and review

[Export pipeline](ExportPipeline.md), [Textures](Textures.md), [Import pipeline](ImportPipeline.md), [Document and persistence](DocumentAndPersistence.md).
Interactions with other systems: [Life of an edit](../Interactions/LifeOfAnEdit.md), [Mechanisms](../Interactions/Mechanisms.md), [Textures](../Interactions/TexturesAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
