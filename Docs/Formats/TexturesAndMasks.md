# Texture files the editor writes: masks, point clouds, minimap, map shot, water depth, material textures

Code: `Assets/SC2/Scripts/SC2DdsEncoder.cs` (the encoder), `TerrainEditor/SC2TerrainExporter.cs` (`ExportRnmMaskByName`, `WriteRnmMaskFiles`, the water depth block), `TerrainEditor/SC2PointCloudBakeService.cs`, `TerrainEditor/SC2MapPreviewCapture.cs`, `SC2RuntimeTextureExporter.cs`, `TextureBrowser/SC2CustomTextureImporter.cs`.
Systems: [Lighting bake](../Systems/LightingBake.md), [Blend mask](../Systems/BlendMask.md), [Water](../Systems/Water.md), [Textures](../Systems/Textures.md), [Export pipeline](../Systems/ExportPipeline.md). These are DDS files, not `.bdf`.

## The encoder: which function writes which file

| Function | Result | Mip chain | Used for |
|---|---|---|---|
| `BuildDxt5(w, h, rgb, alpha)` | DXT5, one level | none | lighting masks (`_0`, `_1`, `_2`) and the generated water depth texture |
| `BuildDxt5Volume(w, h, depth, rgb[], alpha[])` | DXT5 3D volume | none | `pcloud_up` and `pcloud_down` |
| `BuildMaterialDds(w, h, rgb, alpha)` | DXT1 if every alpha is 255, else DXT5 | full, box-filtered down to 1x1 | material textures re-encoded from live pixels (Diffuse, Normal, Mask) in a player |
| `BuildDxt1SingleMip(w, h, rgb)` | DXT1, one level | none (header flags still say COMPLEX, matched to the original) | the minimap |
| `BuildBgra32(w, h, bgra)` | uncompressed B, G, R, A bytes | none | the map shot |

The colour blocks use a principal-axis range fit (two endpoints plus nearest-colour matching), the approach of common fast DXT encoders. An earlier version averaged each block to one flat colour, which lost all internal detail.

## Lighting mask files (`maps/<Map>/lighting/`)

Three DXT5 files per mask group, all the same size: `<group>_0.win.dds`, `<group>_1.win.dds`, `<group>_2.win.dds`. The background's carry the prefix `bg_`. Each file is one **RNM basis slice** (the three tangent-space directions, azimuth 0, 120 and 240 degrees, elevation 35.26 degrees, exactly the basis constants of the terrain shader).

| File | RGB | Alpha |
|---|---|---|
| `_0` | light of basis 0 | **shadow alpha**: sun occlusion (strongly bimodal on vanilla data: about 44% exactly 0, about 45% exactly 1) |
| `_1` | light of basis 1 | **blend alpha**: the painted blend mask of the pair |
| `_2` | light of basis 2 | opaque (255) |

An unbaked mask exports neutral light (RGB 128) with opaque shadow alpha and the painted blend alpha. A baked mask exports the baked RGB and shadow alpha. The masks have **no mip chain**; this is intentional for lighting maps in the game's own data, and the editor does not generate mips for them. The pair, group and background-prefix rules are in [Blend mask](../Systems/BlendMask.md).
Vanilla masks are read by UV2 (the vertices' second UV set).

## Point clouds (`lighting/pcloud_up.win.dds`, `pcloud_down.win.dds`)

3D DXT5 volumes of **256 x 256 x 8** (`SC2PointCloudBaker.Res`, `Depth`), covering the box `PointCloudOrigin`/`PointCloudSize` of [info.bdf](Info.md). The game reads them to light units from the map. What is known from the vanilla files: `up.rgb` is light from +Y (sky), `down.rgb` is light from -Y (ground bounce), `down.alpha` multiplies the sun light and is the terrain and prop sun shadow
(0 where the voxel is inside the ground or in shadow). Voxel (i, j, k) is centred at `origin + ((i + 0.5) / 256 * size.x, (k + 0.5) / depth * size.y, (j + 0.5) / 256 * size.z)`. `up` carries opaque alpha. The baker, the model it uses and what is known not to be derivable from map data are in [Lighting bake](../Systems/LightingBake.md).
Without the files the game falls back to its own default (an 8 x 8 x 2 black DXT1 volume).

## Minimap, map shot and water depth

| File | Format | Size | Notes |
|---|---|---|---|
| `<Map>.minimap.win.dds` | DXT1, one level | 1024 x 1024 | captured by `SC2MapPreviewCapture` |
| `<Map>_PC_mapshot.dds` | BGRA32 | 448 x 206 | the lobby preview image |
| `<Map>.waterDepth.dds` | DXT5 | square, size from the water settings | generated, or an imported image; the water material's Mask1 slot points at `/maps/<Map>/<Map>.waterDepth.dds` |

The water depth image covers the environment's depth area; when none is set it covers the whole map, as the generated one does. See [Water](../Systems/Water.md).

## Material textures

Textures that a material names are referenced by game path (vanilla) or copied into the map (`Textures/...`, custom or generated). In a player, textures that only exist in memory are re-encoded with `BuildMaterialDds`. **Whether a vanilla texture has a full mip chain or one level is a deliberate per-type choice in the game's data**: terrain textures have full chains, lighting maps have none.
The game does not regenerate mips at load, so the editor loads DDS bytes as they are and never synthesizes missing levels.

## Rules

- A DDS written for the game must not contain an editor marker or machine-local path in anything that references it ([Export pipeline](../Systems/ExportPipeline.md)).
- Do not add mip generation for lighting masks or for textures read straight from the game's archives.
- Sizes of the masks of a group must match; the exporter warns when a background pair's group differs from the first pair's.
