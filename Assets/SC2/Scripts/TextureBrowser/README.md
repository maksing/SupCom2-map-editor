# SC2 Texture Browser

The Materials panel uses one catalog browser in the Unity Game view and in a
standalone player. The browser never discovers textures with `Resources.LoadAll`.

Grid cards use catalog thumbnail atlases (or a custom texture's 128px sidecar
thumbnail). Selecting a card keeps the original unloaded. The original is
loaded only when the user requests a channel/full preview or confirms the
selection.

## Catalog generation

- `SC2 > Textures > Rebuild Catalog` writes metadata only.
- `SC2 > Textures > Rebuild Catalog + Thumbnails` also creates 1024x1024 atlas
  pages with 128x128 thumbnails.
- every Player build runs the catalog and thumbnail generation preprocessor,
  so Build & Run cannot accidentally ship a stale or missing catalog.
- the old text manifest remains a compatibility fallback for projects that
  have not regenerated their assets yet.

Generated resources:

```text
Assets/SC2/Resources/SC2TextureCatalog.json
Assets/SC2/Resources/SC2TextureThumbnails/page_XXXX.png
```

## Runtime custom library

PNG, JPEG and DXT1/DXT5 DDS files can be imported from the browser. PNG/JPEG
are converted to game-ready DDS with mipmaps and automatic DXT1/DXT5 alpha
selection. Files are written atomically under:

```text
Application.persistentDataPath/TextureLibrary
```

Each DDS has `.thumb.png` and `.sc2tex.json` sidecars so catalog reconstruction
does not decode every full texture.

Catalog references use full normalized paths. `Texture2D.name` is retained
only for display and migration of old saves. Vanilla resources keep their game
path and are not copied into map exports; custom DDS files are included only
when assigned.

## Performance rules

- search operates on cached normalized metadata;
- the grid draws only visible rows plus one overscan row;
- cards use thumbnail atlases when available;
- fallback texture loads are limited by a 96-entry LRU cache;
- assigned textures are pinned so cache eviction cannot invalidate materials.

The pure catalog query and virtualization code lives under
`Scripts/MapDocument/TextureBrowser` and is covered by the standalone test
suite.
