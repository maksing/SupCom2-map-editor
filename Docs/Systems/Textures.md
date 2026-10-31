# Textures: catalog, browser, game archive and custom library

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) first.

## 1. Purpose and boundaries

Finds, previews, loads and picks the textures of the editor: the game's own textures read live from the player's install, the textures of imported maps, and the user's custom imports. Included: the catalog and its search, the
browser overlay, the sibling suggestion (picking a Normal or Mask next to a Diffuse), the custom texture importer and its library, the loaders that turn a path into a `Texture2D`, the resource path bookkeeping, and the
game install setting.

Not included: how a slot is exported ([export](ExportPipeline.md), including the path safety rule), which slot a material has ([materials](MaterialsAndPairs.md)).

## 2. Where the code is

| Part | Files |
|---|---|
| Catalog | `Scripts/TextureBrowser/SC2TextureCatalog.cs` (`Search`, `Find`, `FindByResourcePath`, `TryFindSiblingMatch`, `FindAllSiblingMatches`), `Resources/SC2TextureCatalog.json` (a manifest of loose files), `SC2TextureCatalogBuilder` (editor) |
| Browser | `Scripts/TextureBrowser/SC2TextureBrowser.cs` (overlay, search, filters, favourites, previews of channels and cubemaps, `Open`/`Consume`) |
| Custom library | `Scripts/TextureBrowser/SC2CustomTextureImporter.cs` (`Import`, `TryLoad`, `TryDelete`, `TryRestoreFromMapshare`) |
| Loading and paths | `Scripts/SC2TerrainMaterialFactory.cs` (`LoadTexturePublic`, `RegisterResourcePath`, `TryGetResourcePath`, `ResolveGamePath`), `SC2TextureManifest.cs` |
| Game access | `MapDocument/Domain/SC2GameInstall.cs` (`SC2GameInstallSettings`, `SC2GameArchiveRegistry`), `SC2VanillaArchiveClassifier.cs` |
| Browser model (pure) | `MapDocument/TextureBrowser/` (`SC2TextureCatalogModel`: sources Vanilla, Map, Custom, Generated and the usage kinds; `SC2TextureCatalogQueryEngine`: search with a cache keyed by a catalog version; `SC2TextureSiblingMatcher`: the core of the sibling suggestion; `SC2TextureVirtualGrid`: which rows of a large grid are visible, so only visible tiles are loaded) |
| Thumbnails | `TextureBrowser/SC2TextureThumbnailCache.cs` (`Get`, `GetFull`, `Pin`, `PinFull`, `Evict`, `Clear`; remembers entries whose thumbnail failed; loads heavy or disk thumbnails only when allowed) |
| Preview shaders | `Resources/SC2Texture3DSlicePreview.shader`, `SC2TextureChannelPreview.shader`, `SC2CubemapFacePreview.shader` ([shaders](Shaders.md)) |
| Importing pictures | `TerrainEditor/SC2PreviewImageImport.cs` (a picture as the minimap or the map shot, fitted by `Core/SC2ImageResample.cs`), `SC2DdsEncoder` ([texture files](../Formats/TexturesAndMasks.md)) |

## 3. Data

A catalog entry has a stable id, a name, a **usage** (diffuse, normal, mask, ramp, cube, ...), a **source** (vanilla, map-local, custom), the archive entry path (`ArchiveEntryPath`) and, for a loose file, a `GamePath`. The game's own
files are never bundled: the catalog is filled live from the install the user points the editor at (`SC2GameInstallSettings`, a PlayerPrefs setting). Custom imports are copied into a **texture library** under the save folder. A loaded texture is registered with its
resource path tag: `archive:<entry>` or `file:<path>`; these tags are internal.

## 4. Flows

- **Pick**: a texture field opens the browser with a key; the user chooses; `Consume(key)` returns the texture and its catalog entry. For a Normal, Mask or Diffuse the browser **suggests the sibling's counterpart first** (same base name, other usage) instead of
  filtering the rest away.
- **Resolving a game path** (`ResolveGamePath(entry)`): the entry's loose path or archive path, with a leading slash and without the `.win` platform infix; the result is what is stored in fields that hold a game path (ramp, environment, water).
- **Custom import**: converts and copies an image into the library as a game-format texture, makes a catalog entry, and keeps the original path so the map can carry the file (`SC2MapshareTextureCapture`).
- **Loading**: by resource path or name (`ResolveTextureByPathOrName`); an archive texture is read from the open game reader; DDS data is decoded into Unity textures with the right colour space.
- **Deleting an imported texture** asks for confirmation (the runtime confirm in a build).

## 5. Invariants

- **A missing mip chain on a vanilla texture is not a bug.** Whether a `.dds` ships with a full chain or one level is a deliberate per-type choice in the game's data (terrain textures have full chains, lighting maps none), and the game does not regenerate mips at load. `SC2CustomTextureImporter.TryBuildDdsFlat` loads the bytes as they are; do not add mip generation for data read from the archives.

- **The game's textures are never committed**: the folders that hold extracted copies are ignored by the repository.
- **A catalog path that begins with a slash is normalised**; ids hash the normalised path, so a path with and without the slash is the same entry.
- **`archive:` and `file:` tags never leave the editor**: they are resolved at export or dropped.
- **A custom library path is a machine-local path**: a game path built from it (`/C:/...`) is replaced at export by a copy in the map, or dropped with a warning.
- **A texture's colour space follows its usage** (normals and data textures are linear); a mask array copy must not be decoded as colour.
- **The vanilla decision keys off the archive that actually owns the file**, not the catalog's source flag (that flag also marks map-local textures from imported maps).

## 6. Editor and build differences

- The browser is a plain overlay, the same in both. Native file dialogs for importing are the runtime dialogs in a player.

## 7. Extension points and how to change it

- **A new usage kind**: the usage enum, the classification of names, the browser filter, the loader's colour space.
- **A new texture source**: a catalog source kind, how entries are listed and loaded, and the export decision for it.

## 8. Editing, verification and tests

**How to edit it safely.** Keep paths normalised, keep the tags internal, and re-test the export of a vanilla, a custom and an archive texture whenever the resolver changes.

**Tests today**: `TextureCatalogTests`, `TextureBrowserFilterTests`, `TextureSiblingMatchTests`, `VanillaArchiveClassifierTests`, `TexturePathSafetyTests`, `MapshareTexturesTests`, `ImageResampleTests`. The browser UI and the loaders have no unit test.

**Tests a change must add.** Catalog and sibling rules: a case with synthetic entries. A path rule: `TexturePathSafetyTests`. A loader or resolver change: an export scenario that reads the written material paths back.

## 9. Known limitations and unverified behaviour

- Without a configured game install the archive catalog is empty and only loose files are listed.
- The speed of the catalog on a full install was not measured here.

## 10. Related documents and review

[Materials and pairs](MaterialsAndPairs.md), [Export pipeline](ExportPipeline.md), [Import pipeline](ImportPipeline.md), [Environment](Environment.md), [Decals](Decals.md).
Interactions with other systems: [Textures across systems](../Interactions/TexturesAcrossSystems.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
