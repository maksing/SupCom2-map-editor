# Shaders

Status: first draft from reading the shader headers and their callers at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Editor vs build](EditorVsBuild.md) first.

## 1. Purpose and boundaries

The catalogue of every shader the editor ships, what each is for, who uses it, and the rules for adding or changing one. All are for the **built-in render pipeline** in linear colour space. The terrain, water, unit and decal shaders are ports of the game's own HLSL (`effects/terrain.hlsl`, `water.hlsl`, `decal.hlsl`, `sky.hlsl`, `unit.hlsl`);
the rest are editor aids.

Not included: how the terrain runtime feeds the terrain shader ([terrain data and runtime](TerrainDataAndRuntime.md)), the baked lighting ([lighting bake](LightingBake.md)), the material factory ([materials and pairs](MaterialsAndPairs.md)).

## 2. Where the code is

Shaders live in `Assets/SC2/Resources/` (and `Resources/TerrainEditorShaders/`), so a player always includes them. They are bound from C# by name.

### Game-look shaders

| Shader (file) | Purpose | Used by |
|---|---|---|
| `SC2/Terrain` (`SC2Terrain.shader`, 660 lines) | the terrain material: RNM blending with the ShaderSetup flags the vanilla maps use (ambient, diffuse, specular, glow, environment mapping, RNM lighting, blending, fog, dynamic shadows, alpha test, infinite distance) and formulas from `effects/terrain.hlsl`; its RNM basis constants are exactly those of `SC2RnmMaskBaker` | `SC2TerrainMaterialFactory` (imported terrain) |
| `Hidden/SC2/TerrainView` (`TerrainEditorShaders/SC2TerrainView.shader`, 842 lines) | the live terrain preview of the game view: uv0 is raw world XZ for tiled textures, uv1 the global terrain UV; preview modes including the baked RNM lighting (mode 4 shows the real baked terrain self-shadow from the `_0/_1/_2` layout the export writes) | `SC2TerrainRuntime` |
| `SC2/Water` | the water preview, following `effects/water.hlsl` in order; engine-owned inputs come from `SC2WaterRenderController`; the game's screen-space refraction (a grab pass) was removed | `SC2WaterMaterialFactory`, `SC2WaterPlane` |
| `SC2/Sky` | the sky as a skybox, `effects/sky.hlsl`: looks the sky cube up by the direction from the camera to the vertex; no tint, fog or lighting; if the cube is not flagged sRGB in a linear project the values are converted first (`_SkyDecode`) | `SC2SkyPreview` |
| `SC2/Decal` (431 lines) | the static decal path of `effects/decal.hlsl`, reading the vertex layout of `decals.bdf` packed into Unity's UV channels | `SC2DecalsBdfImporter`, `SC2DecalsShell` |
| `SC2/Unit` | units and props from SCM models; draws both sides because SCM winding is inconsistent | `SC2UnitMaterialFactory` |
| `SC2/EffectParticle` | the preview of a game particle: particle texture times the ramp texture (read by life and ramp row), blend alpha or additive | `SC2EffectPreview` |

### Overlays and previews

| Shader | Purpose |
|---|---|
| `SC2/CostsOverlay` | the Costs texture drawn over the terrain mesh, UV from world X/Z |
| `SC2/FloorOverlay` | the Costs or terrain-type overlay on the **Collision2 floor plane**, depth tested; the terrain shaders leave the overlay out under the floor (`_SC2OverlayFloorY`) |
| `SC2/PropCostsOverlay` | the Costs overlay on the surfaces of props that are part of the heightmap, from the same world X/Z lookup, since a terrain overlay would sit hidden under a building |
| `SC2/MatPairPreview` | the Mats/Pairs toggle of an imported terrain: flat colours by texture or pair identity |
| `SC2/VertexColorUnlit` | per-vertex colours unlit: the Slope and Height preview of an imported terrain |
| `SC2/StampPreview` | the translucent ground preview of a stamp brush, depth offset against z-fighting |
| `SC2/LineBatchUnlit` | vertex-coloured, alpha-blended, always-on-top lines for the batched path-graph edges (needs a real pass for `GL` drawing) |
| `SC2/SelectionOutline` | the silhouette outline of a selected object: pass 0 draws a mask, pass 1 draws the line just outside it |
| `SC2/MarkerModel` | marker gizmo models as instances; culls front faces by default to undo the marker's Z mirror (`_Cull = Front`) |
| `Hidden/SC2/ObjectMarker` | the GPU-instanced pick dots for props, units and markers (Normal, Selected, Invalid, Hover per kind) |
| `SC2/RuntimeFallback` | a shader guaranteed to exist in a build, for runtime-made placeholder materials |

### Paint, blit and texture-browser helpers (`Hidden/...`)

| Shader | Purpose |
|---|---|
| `Hidden/SC2/PaintBrush` (`SC2PaintBrush.shader`) | paint for pair assignment and the blend mask, including the soft paint and erase of a blend layer; **no GPU blend**: it reads the old value and returns the new or old one explicitly, so strokes cannot accumulate |
| `Hidden/SC2/HeightBrush` | the GPU sculpt brush on the `RFloat` height map: raise, lower, smooth, flatten, noise in one pass, no CPU work |
| `Hidden/SC2/BlendComposite` | flattens one blend-mask editing layer over the running composite (value in R, coverage in G; alpha = lerp(previous, layer value, coverage * opacity)); no GPU blend |
| `Hidden/SC2/PairColorView` | the pair map (R8, 0 to 254) turned to colours from a lookup of up to 72 pair colours |
| `Hidden/SC2/BlitExplicitLod` | a blit at an explicit mip level, for correct antialiased thumbnails without changing the source texture's mip bias |
| `Hidden/SC2/TextureChannelPreview`, `CubemapFacePreview`, `Texture3DSlicePreview` | channel, cube-face and volume-slice previews for the texture browser; the cube-face and volume-slice ones are used by the editor-only catalog builder (`SC2TextureCatalogBuilder`) to make thumbnails, loaded with `Resources.Load<Shader>` and `Shader.Find` as a fallback |

## 3. Data

Shaders take their inputs as material properties and global shader variables set from C#. The ones that must stay in step with C#: the RNM basis constants (shader and baker), the pair colour array (72 entries), the mask layout (value in R, coverage in G for blend layers; RGB 0.5 and the blend factor in alpha for the composite), and the overlay floor height `_SC2OverlayFloorY`.

## 4. Flows

C# creates the material from the shader found by name, sets properties per frame or on change, and draws: renderers for terrain and water, `Graphics.Blit` for paint and composite passes, `Graphics.DrawMeshInstanced` for units, props, markers and pick dots, `GL` for path lines, a command buffer for the selection outline.

## 5. Invariants

- **A runtime-only shader must be reachable in a player.** Everything bound only by name sits under `Resources/` and is loaded with `Resources.Load<Shader>`; `Shader.Find` alone works in the editor and fails in a build because the stripper removes shaders nothing references (the reason several shaders carry a "lives under Resources" comment, and for `SC2RuntimeFallback`).
- **Instanced shaders need their instancing variant kept** (`GraphicsSettings` instancing stripping is `Keep All`); see [Project and build settings](ProjectAndBuildSettings.md).
- **Paint and composite shaders never rely on GPU blending**; they return the exact result. Adding a blend state brings back stroke accumulation artifacts.
- **Data textures are sampled linear.** Water wave normals, the wave ramp and the water depth are raw [0, 1] RGB in `SC2/Water`; the sky cube may need a decode ([editor-only tools](EditorOnlyAndLegacy.md) for the import side).
- **A mirrored instance matrix (negative determinant) does not flip winding in an instanced draw**; a shader used that way must set its cull accordingly (`SC2MarkerModel`).
- **The terrain shader and `SC2RnmMaskBaker` share the RNM basis**: azimuth 0, 120 and 240 degrees at elevation 35.26 degrees. Changing one without the other makes the preview of the bake wrong.

## 6. Editor and build differences

The shader sources are the same; differences are in how they are found and kept: `Resources/` inclusion, variant stripping and `Shader.Find`. A shader that "works" only because the editor found it by name is a latent player bug.

## 7. Extension points and how to change it

- **A new shader**: a file under `Resources/` named `SC2<Name>.shader` with a `Shader "SC2/<Name>"` (or `Hidden/SC2/...` for helpers), a header comment saying what it ports or serves, a loader in C# using `Resources.Load<Shader>` with a fallback, and a line in the table above.
- **A new terrain flag or preview mode**: the shader, the C# that sets the keyword or the mode index, and the preview-mode list in the terrain runtime.

## 8. Editing, verification and tests

**How to edit it safely.** Render the result: after any change to the water shader render it with the scratch water scenario and look at the images; for the others a screenshot taken by the editor or the player itself. A shader change must be seen in a **player build** when it uses instancing, `Resources.Load` or a keyword.

**Tests today**: none run shaders. `SunShadowMathTests` and similar test the C# math the shaders mirror.

**Tests a change must add.** A harness scenario that renders a known input and compares the picture (or checks pixel values) in the editor and, for instanced or `Resources`-loaded shaders, in a player.

## 9. Known limitations and unverified behaviour

- No shader has an automated test; agreement with the game's own shaders was checked by comparing renders, not by a suite.
- A few shader headers contain non-English text and a dated comment; they predate the comment-style rules and were not rewritten here.
- Per-shader keyword and property lists are in the shader files and not repeated here.

## 10. Related documents and review

[Terrain data and runtime](TerrainDataAndRuntime.md), [Water](Water.md), [Lighting bake](LightingBake.md), [Decals](Decals.md), [Game assets](GameAssets.md), [Project and build settings](ProjectAndBuildSettings.md), [Editor vs build](EditorVsBuild.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
